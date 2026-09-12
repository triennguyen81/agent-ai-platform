using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Books;
using Aiq.Gateway.Middleware;
using Aiq.Gateway.Storage;
using Aiq.Gateway.Vouchers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Aiq.Gateway.Endpoints;

public static class VoucherEndpoints
{
    private static readonly string[] ImageContentTypes =
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/webp",
        "image/bmp",
        "image/tiff",
        "image/heic",
    };

    public static IEndpointRouteBuilder MapVoucherEndpoints(this IEndpointRouteBuilder app)
    {
        // Any authenticated tenant member (role >= user) can upload +
        // work with vouchers — that's the daily job of kế toán viên.
        // Repository queries are tenant-scoped so no cross-tenant leak.
        // Policy "user" (super_admin/tenant_owner/admin/user) matches the
        // pilot's direct-upload UX; agent tool_execution scope is not
        // required because the browser-facing flow is the primary path.
        var group = app
            .MapGroup("/vouchers")
            .RequireAuthorization("user")
            .RequireRateLimiting("default");

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapPost("/upload", UploadAsync).DisableAntiforgery();
        // Sprint 6' A3: gói hồ sơ ZIP theo NCC + khoảng thời gian cho kế toán
        // gửi bank / kiểm toán. Reuse filter A2 qua query string.
        group.MapGet("/export", ExportAsync);
        // Sprint 6' A6: báo cáo tổng hợp trạng thái (đã đối soát / treo /
        // quá hạn) cho dashboard KT trưởng.
        group.MapGet("/status-report", StatusReportAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPatch("/{id:guid}", UpdateAsync);

        // Destructive delete stays admin-only.
        group.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization("admin");

        group.MapPost("/{id:guid}/journal-suggestions", SaveJournalAsync);
        group.MapPost("/{id:guid}/journal-suggestions/{entryId:guid}/decision", DecideJournalAsync);

        // Preview: streams the uploaded voucher file through the gateway
        // so the browser never needs a direct route to MinIO. Signed URLs
        // would offload the bytes but require MinIO to be publicly
        // reachable + SSL — more infra work for no MVP-critical gain.
        group.MapGet("/{id:guid}/preview", GetPreviewAsync);

        // Idempotent "fill in extracted fields on an existing stub row" —
        // used by the agent's save_voucher tool after OCR + LLM extract so
        // the upload flow doesn't create a duplicate row.
        group.MapPatch("/{id:guid}/apply-extraction", ApplyExtractionAsync);

        // Manual re-extract: user clicks "Trích xuất" button on detail page
        // when the automated pipeline failed. Enqueues the same Hangfire
        // extract job (returns fast) instead of running LLM inline. Optional
        // rawText body overrides raw_ocr_text for the paste-fallback flow.
        group.MapPost("/{id:guid}/reextract", ReextractAsync);

        // Kế toán viên bấm "Chấp nhận thu" trên phiếu thu detail page:
        // reads receipt_matching from extracted_fields, inserts credit
        // rows in sim_ar_ledger (one per application + optional discount),
        // updates sim_customers.current_ar, marks voucher status='verified'.
        group.MapPost("/{id:guid}/apply-receipt", ApplyReceiptAsync);

        // UC3 counterpart — kế toán viên bấm "Chấp nhận chi" trên voucher
        // AP side detail page. Routes based on voucher_type:
        //   invoice_vat_in → book credit row (Có 331 tăng)
        //   payment/bank_transfer → apply debit rows per payment_matching.applications
        //   expense_claim → update sim_employees.outstanding_advance + ytd_expense
        group.MapPost("/{id:guid}/apply-expense", ApplyExpenseAsync);

        // Sprint 4 gap UC1: bấm "Chấp nhận HĐ" trên invoice_vat_out detail page
        // → insert debit row vào sim_ar_ledger (Nợ 131 tăng) + update current_ar.
        group.MapPost("/{id:guid}/apply-invoice", ApplyInvoiceAsync);

        // Sprint 4 gap UC2: bấm "Huỷ áp thu" khi cần rollback receipt đã áp.
        // DELETE rows tagged voucher:{id} + recompute current_ar.
        group.MapPost("/{id:guid}/unapply-receipt", UnapplyReceiptAsync);

        return app;
    }

    private sealed record ReextractRequest(string? RawText, string? VoucherType);

    private static async Task<IResult> ApplyReceiptAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Customers.ICustomerRepository customers,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        if (!TenantContext.TryGet(ctx, out var tenantId))
        {
            return Results.BadRequest(new { error = "tenant scope required" });
        }

        var voucher = await repo.FindAsync(tenantId, userId, id, ct);
        if (voucher is null) return Results.NotFound();
        if (voucher.Voucher.VoucherType != "receipt")
        {
            return Results.BadRequest(new { error = "apply-receipt chỉ áp dụng cho phiếu thu" });
        }
        if (voucher.Voucher.Status == "verified")
        {
            return Results.Ok(new { voucherId = id, status = "already_applied", message = "Đã áp trước đó." });
        }

        var extractedJson = voucher.Voucher.ExtractedFieldsJson ?? "{}";
        using var doc = System.Text.Json.JsonDocument.Parse(extractedJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("receipt_matching", out var matchEl))
        {
            return Results.BadRequest(new { error = "voucher chưa có receipt_matching — chạy trích xuất lại trước" });
        }
        if (!root.TryGetProperty("ar_context", out var arEl)
            || !arEl.TryGetProperty("customer", out var custEl)
            || !custEl.TryGetProperty("Id", out var custIdEl))
        {
            return Results.BadRequest(new { error = "voucher chưa có ar_context.customer" });
        }
        var customerId = custIdEl.GetGuid();

        var apps = new List<Aiq.Gateway.Customers.ReceiptApplicationInput>();
        if (matchEl.TryGetProperty("applications", out var appsEl) && appsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var a in appsEl.EnumerateArray())
            {
                var docNum = a.GetProperty("document_number").GetString() ?? "?";
                var applied = a.GetProperty("applied").GetDecimal();
                if (applied > 0m) apps.Add(new Aiq.Gateway.Customers.ReceiptApplicationInput(docNum, applied));
            }
        }
        decimal discount = 0m;
        string? discountReason = null;
        if (matchEl.TryGetProperty("discount", out var discEl) && discEl.ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            discount = discEl.GetDecimal();
        }
        if (matchEl.TryGetProperty("discount_reason", out var drEl) && drEl.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            discountReason = drEl.GetString();
        }

        if (apps.Count == 0 && discount == 0m)
        {
            return Results.BadRequest(new { error = "không có khoản nào để áp thu" });
        }

        var receiptDocumentNumber = voucher.Voucher.DocumentNumber ?? $"PT-{id:N}";
        var receiptDate = voucher.Voucher.DocumentDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

        var result = await customers.ApplyReceiptAsync(
            tenantId,
            customerId,
            id,
            receiptDocumentNumber,
            receiptDate,
            apps,
            discount,
            discountReason,
            ct);

        // Flip voucher to verified so the Accept button hides.
        await repo.UpdateAsync(
            tenantId,
            userId,
            id,
            new UpdateVoucherRequest(
                Status: "verified",
                DocumentNumber: null, DocumentSeries: null, DocumentDate: null,
                SellerName: null, SellerMst: null, SellerAddress: null,
                BuyerName: null, BuyerMst: null, BuyerAddress: null,
                PaymentMethod: null, Subtotal: null, VatRate: null, VatAmount: null,
                DiscountAmount: null, TotalAmount: null, Currency: null,
                AmountInWords: null,
                ErrorMessage: null,
                Tags: null),
            ct);

        return Results.Ok(new
        {
            voucherId = id,
            status = "applied",
            previous_balance = result.PreviousBalance,
            new_balance = result.NewBalance,
            total_applied = result.TotalApplied,
            discount_amount = result.DiscountAmount,
            ledger_rows_inserted = result.LedgerRowsInserted,
        });
    }

    private static async Task<IResult> ApplyInvoiceAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Customers.ICustomerRepository customers,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId)) return Results.Unauthorized();
        if (!TenantContext.TryGet(ctx, out var tenantId))
        {
            return Results.BadRequest(new { error = "tenant scope required" });
        }

        var voucher = await repo.FindAsync(tenantId, userId, id, ct);
        if (voucher is null) return Results.NotFound();
        var vt = voucher.Voucher.VoucherType?.ToLowerInvariant() ?? "";
        if (vt is not ("invoice_vat_out" or "invoice_vat"))
        {
            return Results.BadRequest(new
            {
                error = "apply-invoice chỉ áp dụng cho invoice_vat_out (hoá đơn bán)",
            });
        }
        if (voucher.Voucher.Status == "verified")
        {
            return Results.Ok(new { voucherId = id, status = "already_applied", message = "Đã áp trước đó." });
        }

        var extractedJson = voucher.Voucher.ExtractedFieldsJson ?? "{}";
        using var doc = System.Text.Json.JsonDocument.Parse(extractedJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("ar_context", out var arEl)
            || !arEl.TryGetProperty("customer", out var custEl)
            || !custEl.TryGetProperty("Id", out var custIdEl))
        {
            return Results.BadRequest(new { error = "voucher chưa có ar_context.customer" });
        }
        var customerId = custIdEl.GetGuid();
        var invoiceTotal = voucher.Voucher.TotalAmount ?? 0m;
        if (invoiceTotal <= 0m)
        {
            return Results.BadRequest(new { error = "invoice_vat_out không có total_amount" });
        }
        var docNumber = voucher.Voucher.DocumentNumber ?? $"HD-{id:N}";
        var docDate = voucher.Voucher.DocumentDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

        // Due date = doc_date + customer.credit_terms_days.
        DateOnly? dueDate = null;
        if (custEl.TryGetProperty("CreditTermsDays", out var termsEl)
            && termsEl.ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            var days = termsEl.GetInt32();
            if (days > 0) dueDate = docDate.AddDays(days);
        }

        var result = await customers.BookInvoiceAsync(
            tenantId, customerId, id, docNumber, docDate, dueDate, invoiceTotal, ct);

        await repo.UpdateAsync(
            tenantId, userId, id,
            new UpdateVoucherRequest(
                Status: "verified",
                DocumentNumber: null, DocumentSeries: null, DocumentDate: null,
                SellerName: null, SellerMst: null, SellerAddress: null,
                BuyerName: null, BuyerMst: null, BuyerAddress: null,
                PaymentMethod: null, Subtotal: null, VatRate: null, VatAmount: null,
                DiscountAmount: null, TotalAmount: null, Currency: null,
                AmountInWords: null,
                ErrorMessage: null,
                Tags: null),
            ct);

        return Results.Ok(new
        {
            voucherId = id,
            status = result.LedgerRowsInserted == 0 ? "already_applied" : "applied",
            voucher_type = vt,
            previous_balance = result.PreviousBalance,
            new_balance = result.NewBalance,
            invoice_total = result.InvoiceTotal,
            ledger_rows_inserted = result.LedgerRowsInserted,
        });
    }

    private static async Task<IResult> UnapplyReceiptAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Customers.ICustomerRepository customers,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId)) return Results.Unauthorized();
        if (!TenantContext.TryGet(ctx, out var tenantId))
        {
            return Results.BadRequest(new { error = "tenant scope required" });
        }

        var voucher = await repo.FindAsync(tenantId, userId, id, ct);
        if (voucher is null) return Results.NotFound();
        if (voucher.Voucher.VoucherType != "receipt")
        {
            return Results.BadRequest(new { error = "unapply-receipt chỉ áp dụng cho phiếu thu" });
        }
        if (voucher.Voucher.Status != "verified")
        {
            return Results.BadRequest(new
            {
                error = $"chỉ unapply được phiếu thu đã verified (hiện: {voucher.Voucher.Status})",
            });
        }

        var extractedJson = voucher.Voucher.ExtractedFieldsJson ?? "{}";
        using var doc = System.Text.Json.JsonDocument.Parse(extractedJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("ar_context", out var arEl)
            || !arEl.TryGetProperty("customer", out var custEl)
            || !custEl.TryGetProperty("Id", out var custIdEl))
        {
            return Results.BadRequest(new { error = "voucher chưa có ar_context.customer" });
        }
        var customerId = custIdEl.GetGuid();

        var result = await customers.UnapplyReceiptAsync(tenantId, customerId, id, ct);

        // Rollback voucher status về 'extracted' để user có thể apply lại.
        await repo.UpdateAsync(
            tenantId, userId, id,
            new UpdateVoucherRequest(
                Status: "extracted",
                DocumentNumber: null, DocumentSeries: null, DocumentDate: null,
                SellerName: null, SellerMst: null, SellerAddress: null,
                BuyerName: null, BuyerMst: null, BuyerAddress: null,
                PaymentMethod: null, Subtotal: null, VatRate: null, VatAmount: null,
                DiscountAmount: null, TotalAmount: null, Currency: null,
                AmountInWords: null,
                ErrorMessage: null,
                Tags: null),
            ct);

        return Results.Ok(new
        {
            voucherId = id,
            status = result.LedgerRowsDeleted == 0 ? "nothing_to_unapply" : "unapplied",
            previous_balance = result.PreviousBalance,
            new_balance = result.NewBalance,
            ledger_rows_deleted = result.LedgerRowsDeleted,
        });
    }

    private static async Task<IResult> ApplyExpenseAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Suppliers.ISupplierRepository suppliers,
        Aiq.Gateway.Employees.IEmployeeRepository employees,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        if (!TenantContext.TryGet(ctx, out var tenantId))
        {
            return Results.BadRequest(new { error = "tenant scope required" });
        }

        var voucher = await repo.FindAsync(tenantId, userId, id, ct);
        if (voucher is null) return Results.NotFound();
        var vt = voucher.Voucher.VoucherType?.ToLowerInvariant() ?? "";
        if (vt is not ("invoice_vat_in" or "payment" or "bank_transfer" or "expense_claim"))
        {
            return Results.BadRequest(new
            {
                error = "apply-expense chỉ áp dụng cho invoice_vat_in / payment / bank_transfer / expense_claim",
            });
        }
        if (voucher.Voucher.Status == "verified")
        {
            return Results.Ok(new { voucherId = id, status = "already_applied", message = "Đã áp trước đó." });
        }

        var extractedJson = voucher.Voucher.ExtractedFieldsJson ?? "{}";
        using var doc = System.Text.Json.JsonDocument.Parse(extractedJson);
        var root = doc.RootElement;

        object payload;
        switch (vt)
        {
            case "invoice_vat_in":
            {
                if (!root.TryGetProperty("ap_context", out var apEl)
                    || !apEl.TryGetProperty("supplier", out var supEl)
                    || !supEl.TryGetProperty("Id", out var supIdEl))
                {
                    return Results.BadRequest(new { error = "voucher chưa có ap_context.supplier" });
                }
                var supplierId = supIdEl.GetGuid();
                var invoiceTotal = voucher.Voucher.TotalAmount ?? 0m;
                if (invoiceTotal <= 0m)
                {
                    return Results.BadRequest(new { error = "invoice_vat_in không có total_amount" });
                }
                var docNumber = voucher.Voucher.DocumentNumber ?? $"HDNCC-{id:N}";
                var docDate = voucher.Voucher.DocumentDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

                // Due date = doc_date + supplier.payment_terms_days (nếu có).
                DateOnly? dueDate = null;
                if (supEl.TryGetProperty("PaymentTermsDays", out var termsEl)
                    && termsEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    var days = termsEl.GetInt32();
                    if (days > 0) dueDate = docDate.AddDays(days);
                }

                var bookResult = await suppliers.BookInvoiceAsync(
                    tenantId, supplierId, id, docNumber, docDate, dueDate, invoiceTotal, ct);
                payload = new
                {
                    voucherId = id,
                    status = bookResult.LedgerRowsInserted == 0 ? "already_applied" : "applied",
                    voucher_type = vt,
                    previous_balance = bookResult.PreviousBalance,
                    new_balance = bookResult.NewBalance,
                    invoice_total = bookResult.InvoiceTotal,
                    ledger_rows_inserted = bookResult.LedgerRowsInserted,
                };
                break;
            }

            case "payment":
            case "bank_transfer":
            {
                if (!root.TryGetProperty("ap_context", out var apEl)
                    || !apEl.TryGetProperty("supplier", out var supEl)
                    || !supEl.TryGetProperty("Id", out var supIdEl))
                {
                    return Results.BadRequest(new { error = "voucher chưa có ap_context.supplier" });
                }
                if (!root.TryGetProperty("payment_matching", out var matchEl))
                {
                    return Results.BadRequest(new { error = "voucher chưa có payment_matching — chạy trích xuất lại trước" });
                }
                var supplierId = supIdEl.GetGuid();

                var apps = new List<Aiq.Gateway.Suppliers.ExpenseApplicationInput>();
                if (matchEl.TryGetProperty("applications", out var appsEl)
                    && appsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var a in appsEl.EnumerateArray())
                    {
                        var docNum = a.GetProperty("document_number").GetString() ?? "?";
                        var applied = a.GetProperty("applied").GetDecimal();
                        if (applied > 0m) apps.Add(new Aiq.Gateway.Suppliers.ExpenseApplicationInput(docNum, applied));
                    }
                }

                decimal discount = 0m;
                string? discountReason = null;
                if (matchEl.TryGetProperty("discount", out var discEl)
                    && discEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    discount = discEl.GetDecimal();
                }
                if (matchEl.TryGetProperty("discount_reason", out var drEl)
                    && drEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    discountReason = drEl.GetString();
                }

                if (apps.Count == 0 && discount == 0m)
                {
                    return Results.BadRequest(new { error = "không có khoản nào để áp chi" });
                }

                var payDocNumber = voucher.Voucher.DocumentNumber ?? $"PC-{id:N}";
                var payDate = voucher.Voucher.DocumentDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

                var result = await suppliers.ApplyExpenseAsync(
                    tenantId, supplierId, id, payDocNumber, payDate, apps, discount, discountReason, ct);
                payload = new
                {
                    voucherId = id,
                    status = "applied",
                    voucher_type = vt,
                    previous_balance = result.PreviousBalance,
                    new_balance = result.NewBalance,
                    total_applied = result.TotalApplied,
                    discount_amount = result.DiscountAmount,
                    ledger_rows_inserted = result.LedgerRowsInserted,
                };
                break;
            }

            case "expense_claim":
            {
                if (!root.TryGetProperty("expense_claim_matching", out var ecEl)
                    || !ecEl.TryGetProperty("employee", out var empEl)
                    || !empEl.TryGetProperty("Id", out var empIdEl))
                {
                    return Results.BadRequest(new { error = "voucher chưa có expense_claim_matching.employee" });
                }
                var employeeId = empIdEl.GetGuid();
                decimal totalClaim = 0m, advanceTaken = 0m;
                if (ecEl.TryGetProperty("total_claim", out var tcEl)
                    && tcEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    totalClaim = tcEl.GetDecimal();
                }
                if (ecEl.TryGetProperty("advance_taken", out var atEl)
                    && atEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    advanceTaken = atEl.GetDecimal();
                }
                if (totalClaim <= 0m)
                {
                    return Results.BadRequest(new { error = "expense_claim không có total_claim" });
                }

                var result = await employees.ApplyExpenseClaimAsync(
                    tenantId, employeeId, voucher.Voucher.DocumentNumber, totalClaim, advanceTaken, ct);
                payload = new
                {
                    voucherId = id,
                    status = "applied",
                    voucher_type = vt,
                    previous_advance = result.PreviousAdvance,
                    new_advance = result.NewAdvance,
                    previous_ytd_expense = result.PreviousYtdExpense,
                    new_ytd_expense = result.NewYtdExpense,
                };
                break;
            }

            default:
                return Results.BadRequest(new { error = $"voucher_type không được hỗ trợ: {vt}" });
        }

        // Flip voucher to verified so the Accept button hides + auto-refresh.
        await repo.UpdateAsync(
            tenantId,
            userId,
            id,
            new UpdateVoucherRequest(
                Status: "verified",
                DocumentNumber: null, DocumentSeries: null, DocumentDate: null,
                SellerName: null, SellerMst: null, SellerAddress: null,
                BuyerName: null, BuyerMst: null, BuyerAddress: null,
                PaymentMethod: null, Subtotal: null, VatRate: null, VatAmount: null,
                DiscountAmount: null, TotalAmount: null, Currency: null,
                AmountInWords: null,
                ErrorMessage: null,
                Tags: null),
            ct);

        return Results.Ok(payload);
    }

    private static async Task<IResult> ReextractAsync(
        Guid id,
        ReextractRequest? request,
        IVoucherRepository repo,
        Aiq.Gateway.Jobs.IVoucherExtractJobScheduler extractScheduler,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        TenantContext.TryGet(ctx, out var tenantId);

        var voucher = await repo.FindAsync(tenantId, userId, id, ct);
        if (voucher is null)
        {
            return Results.NotFound();
        }

        // If caller provided raw text (paste-fallback flow), stash it into
        // the voucher first so the extract job reads it from DB just like
        // OCR-populated rows.
        var newRawText = request?.RawText?.Trim() ?? "";
        if (newRawText.Length >= 20)
        {
            await repo.UpdateOcrResultAsync(
                id,
                status: "ocr_complete",
                rawText: newRawText,
                ocrConfidence: 1.0m,   // user-verified text is 100% by definition
                errorMessage: null,
                ct);
        }

        var voucherType = string.IsNullOrWhiteSpace(request?.VoucherType)
            ? voucher.Voucher.VoucherType
            : request!.VoucherType!.Trim();
        var userToken = ExtractBearerToken(ctx);
        var jobId = extractScheduler.EnqueueImmediate(id, userId, tenantId, voucherType, userToken);

        return Results.Ok(new
        {
            voucherId = id,
            status = "processing",
            extractJobId = jobId,
        });
    }

    // ------------------------------------------------------------------
    // Sync upload: image → OCR → return voucherId + raw_text. Extraction
    // + save is done by the web tier via /agent/run with save_voucher tool
    // (mirrors the contract upload flow).
    // ------------------------------------------------------------------
    private const long MaxUploadBytes = 25L * 1024 * 1024;  // 25 MB per voucher

    // UC-ACC-001 P1: async upload. File goes to S3, Hangfire job runs OCR
    // in the background. Response returns in <5s so Cloudflare doesn't cut
    // us off. Web tier polls voucher detail until status='ocr_complete',
    // then drives the LLM extraction step.
    private static async Task<IResult> UploadAsync(
        IFormFile file,
        IVoucherRepository repo,
        IObjectStorage storage,
        Aiq.Gateway.Jobs.IVoucherOcrJobScheduler ocrScheduler,
        Aiq.Gateway.Jobs.IVoucherExtractJobScheduler extractScheduler,
        IOptions<S3Options> s3Options,
        HttpContext ctx,
        CancellationToken ct,
        string voucherType = "invoice_vat")
    {
        if (file is null || file.Length == 0)
        {
            return Results.BadRequest(new { error = "file trống" });
        }
        if (file.Length > MaxUploadBytes)
        {
            return Results.BadRequest(new { error = $"file quá lớn (>25MB): {file.Length} bytes" });
        }
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        var tenantId = TenantContext.Require(ctx);

        var contentType = (file.ContentType ?? "").ToLowerInvariant();
        var isImage = ImageContentTypes.Contains(contentType);
        var isPdf = contentType == "application/pdf" || file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        if (!isImage && !isPdf)
        {
            return Results.BadRequest(new { error = $"content-type '{contentType}' không hỗ trợ. Dùng image/* hoặc application/pdf." });
        }

        var extension = Path.GetExtension(file.FileName);
        if (string.IsNullOrEmpty(extension))
        {
            extension = isImage ? ".jpg" : ".pdf";
        }

        // E-Step 4 — upload to MinIO/S3 với tenant-scoped key layout.
        // Key: {tenantPrefix}/vouchers/{userId}/{uuid}{ext} — tenant prefix
        // giúp isolation + GDPR-style "delete all files for user/tenant".
        var s3Key = TenantObjectKey.Build(tenantId, "vouchers", userId.ToString("N"), $"{Guid.NewGuid():N}{extension}");
        var bucket = s3Options.Value.VoucherBucket;

        try
        {
            // Stream directly to S3 — no need to buffer the full file in
            // memory anymore since OCR happens in a separate process later.
            await using var readStream = file.OpenReadStream();
            await storage.PutAsync(bucket, s3Key, readStream, contentType, ct);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                type: "https://aiq.local/problems/storage",
                title: "Object storage upload failed",
                statusCode: StatusCodes.Status502BadGateway,
                detail: ex.Message);
        }

        var voucherId = await repo.CreateStubAsync(
            tenantId,
            userId,
            voucherType,
            s3Key,
            file.FileName,
            contentType,
            ct);

        // Enqueue background OCR. Job will:
        //   1. status='processing' (currently 'uploading')
        //   2. Download file from S3, run PaddleOCR
        //   3. status='ocr_complete' + rawText + confidence
        // Any OCR failure → status='ocr_unavailable' + errorMessage.
        var ocrJobId = ocrScheduler.Enqueue(voucherId, s3Key, file.FileName, contentType, isImage);

        // Chain the extraction job so LLM + AR + 3-way match run
        // automatically after OCR succeeds. Uses ContinueJobWith so the
        // extract runner sees rawOcrText in DB. We forward the caller's
        // JWT so /agent/run accepts the request.
        var userToken = ExtractBearerToken(ctx);
        var extractJobId = extractScheduler.Enqueue(
            voucherId, userId, tenantId, voucherType, userToken, ocrJobId);

        return Results.Ok(new
        {
            voucherId,
            status = "uploading",
            ocrJobId,
            extractJobId,
        });
    }

    private static string ExtractBearerToken(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return auth.Substring("Bearer ".Length).Trim();
        }
        return string.Empty;
    }

    private static async Task<IResult> ListAsync(
        IVoucherRepository repo,
        HttpContext ctx,
        CancellationToken ct,
        string? voucherType = null,
        string? status = null,
        string? q = null,
        string? sellerMst = null,
        string? documentDateFrom = null,
        string? documentDateTo = null,
        int limit = 50,
        int offset = 0)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        var tenantId = TenantContext.Require(ctx);

        DateOnly? dateFrom = null;
        DateOnly? dateTo = null;
        if (!string.IsNullOrWhiteSpace(documentDateFrom))
        {
            if (!DateOnly.TryParse(documentDateFrom, out var parsedFrom))
            {
                return Results.BadRequest(new { error = $"documentDateFrom không hợp lệ (yyyy-MM-dd): {documentDateFrom}" });
            }
            dateFrom = parsedFrom;
        }
        if (!string.IsNullOrWhiteSpace(documentDateTo))
        {
            if (!DateOnly.TryParse(documentDateTo, out var parsedTo))
            {
                return Results.BadRequest(new { error = $"documentDateTo không hợp lệ (yyyy-MM-dd): {documentDateTo}" });
            }
            dateTo = parsedTo;
        }

        var items = await repo.ListAsync(
            tenantId, userId, voucherType, status, q,
            sellerMst, dateFrom, dateTo,
            limit, offset, ct);
        return Results.Ok(new VouchersListResponse(items, items.Count));
    }

    private static async Task<IResult> StatusReportAsync(
        IVoucherRepository repo,
        HttpContext ctx,
        CancellationToken ct,
        int overdueDays = 30)
    {
        if (!TryGetUserId(ctx, out _))
        {
            return Results.Unauthorized();
        }
        var tenantId = TenantContext.Require(ctx);

        // overdueDays hợp lệ trong [1, 365]. Default 30 = 1 tháng theo policy
        // kế toán VN (kết sổ cuối tháng, chứng từ > 30 ngày chưa Accept = quá hạn).
        var days = Math.Clamp(overdueDays, 1, 365);
        var threshold = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-days));

        var report = await repo.GetStatusReportAsync(tenantId, threshold, ct);
        return Results.Ok(new
        {
            report,
            overdueDays = days,
            overdueThresholdDate = threshold.ToString("yyyy-MM-dd"),
        });
    }

    private static async Task<IResult> ExportAsync(
        VoucherExportService exportService,
        HttpContext ctx,
        CancellationToken ct,
        string? voucherType = null,
        string? status = null,
        string? sellerMst = null,
        string? documentDateFrom = null,
        string? documentDateTo = null)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        var tenantId = TenantContext.Require(ctx);

        DateOnly? dateFrom = null;
        DateOnly? dateTo = null;
        if (!string.IsNullOrWhiteSpace(documentDateFrom))
        {
            if (!DateOnly.TryParse(documentDateFrom, out var parsedFrom))
            {
                return Results.BadRequest(new { error = $"documentDateFrom không hợp lệ (yyyy-MM-dd): {documentDateFrom}" });
            }
            dateFrom = parsedFrom;
        }
        if (!string.IsNullOrWhiteSpace(documentDateTo))
        {
            if (!DateOnly.TryParse(documentDateTo, out var parsedTo))
            {
                return Results.BadRequest(new { error = $"documentDateTo không hợp lệ (yyyy-MM-dd): {documentDateTo}" });
            }
            dateTo = parsedTo;
        }

        var filter = new VoucherExportFilter(voucherType, status, sellerMst, dateFrom, dateTo);
        var filename = VoucherExportService.BuildFilename(filter);

        // Stream ZIP thẳng vào response body — không buffer full ZIP vào memory.
        // Set headers trước khi write để browser download đúng filename.
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{filename}\"";
        ctx.Response.ContentType = "application/zip";
        await exportService.WriteZipAsync(tenantId, userId, filter, ctx.Response.Body, ct);
        return Results.Empty;
    }

    private static async Task<IResult> CreateAsync(
        CreateVoucherRequest request,
        IVoucherRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryValidate(request, out var errors))
        {
            return Results.ValidationProblem(errors);
        }
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        var tenantId = TenantContext.Require(ctx);

        var created = await repo.CreateAsync(tenantId, userId, request, ct);
        return Results.Created($"/vouchers/{created.Voucher.Id}", created);
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        IVoucherRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        var tenantId = TenantContext.Require(ctx);

        var voucher = await repo.FindAsync(tenantId, userId, id, ct);
        return voucher is null ? Results.NotFound() : Results.Ok(voucher);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateVoucherRequest request,
        IVoucherRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        var tenantId = TenantContext.Require(ctx);

        var updated = await repo.UpdateAsync(tenantId, userId, id, request, ct);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    // ApplyExtractionAsync — used by the agent's save_voucher tool AFTER
    // the web upload flow has staged an empty voucher stub. Replaces all
    // extracted fields + line items on the existing row (via
    // ReplaceExtractedAsync) and marks it status='extracted'. Body is
    // the same shape as CreateVoucherRequest, minus source_* fields
    // which were set at stub time.
    // GET /vouchers/{id}/preview
    // Streams the uploaded file from MinIO back to the browser. Wraps
    // the S3 GetObject stream through our own auth layer so voucher
    // access still goes through the standard ACL check.
    //
    // If the row's source_file_path is a legacy local path (not an S3
    // key), we fall back to reading from the local upload volume so
    // pre-migration rows still render.
    private static async Task<IResult> GetPreviewAsync(
        Guid id,
        IVoucherRepository repo,
        IObjectStorage storage,
        IOptions<S3Options> s3Options,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        var tenantId = TenantContext.Require(ctx);

        var voucher = await repo.FindAsync(tenantId, userId, id, ct);
        if (voucher is null)
        {
            return Results.NotFound();
        }
        var path = voucher.Voucher.SourceFilePath;
        if (string.IsNullOrEmpty(path))
        {
            return Results.NotFound(new { error = "voucher has no source file" });
        }
        var contentType = voucher.Voucher.SourceMimeType ?? "application/octet-stream";
        var downloadName = voucher.Voucher.SourceFileName ?? "voucher";

        // S3-hosted rows use the vouchers/... prefix from the E-4 upload
        // path. Everything else is a legacy /var/aiq/uploads/... path
        // from before the migration.
        var isTenantObject = TenantObjectKey.BelongsTo(tenantId, path);
        var isOwnedLegacyObject = path.StartsWith($"vouchers/{userId:N}/", StringComparison.Ordinal);
        if (isTenantObject || isOwnedLegacyObject)
        {
            var signedUrl = await storage.GetSignedUrlAsync(
                s3Options.Value.VoucherBucket,
                path,
                TimeSpan.FromMinutes(2),
                ct);

            // Redirect the browser to the signed URL directly. Keeps
            // gateway CPU/memory idle even for large PDFs — MinIO
            // serves the bytes. Requires Object__PublicUrlPrefix to be
            // set so the URL is reachable from outside Docker net;
            // otherwise fall through to streaming.
            if (!string.IsNullOrEmpty(s3Options.Value.PublicUrlPrefix))
            {
                return Results.Redirect(signedUrl, permanent: false);
            }

            // No public prefix — stream through gateway.
            using var http = new HttpClient();
            var res = await http.GetAsync(signedUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode)
            {
                return Results.Problem(
                    title: "Preview fetch failed",
                    statusCode: (int)res.StatusCode);
            }
            var stream = await res.Content.ReadAsStreamAsync(ct);
            return Results.File(stream, contentType, downloadName);
        }

        // Legacy local-file path — read from Docker volume mount.
        if (!File.Exists(path))
        {
            return Results.NotFound(new { error = "local file missing (migrate to S3 or re-upload)" });
        }
        return Results.File(File.OpenRead(path), contentType, downloadName);
    }

    private static async Task<IResult> ApplyExtractionAsync(
        Guid id,
        CreateVoucherRequest request,
        IVoucherRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryValidate(request, out var errors))
        {
            return Results.ValidationProblem(errors);
        }
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        var tenantId = TenantContext.Require(ctx);

        // Ownership check — 404 if the caller doesn't own the stub.
        var existing = await repo.FindAsync(tenantId, userId, id, ct);
        if (existing is null)
        {
            return Results.NotFound();
        }

        // UC-ACC-001 E2: chống kê khai trùng — cùng seller_mst + document_number.
        // Search hoá đơn khác cùng cặp (loại trừ chính stub này). If found,
        // don't overwrite; return 409 with the duplicate's ID so web can flag.
        if (!string.IsNullOrWhiteSpace(request.SellerMst)
            && !string.IsNullOrWhiteSpace(request.DocumentNumber))
        {
            var maybeDup = await repo.FindDuplicateAsync(
                tenantId, request.SellerMst, request.DocumentNumber, excludeId: id, ct);
            if (maybeDup is not null)
            {
                return Results.Conflict(new
                {
                    error = "duplicate_voucher",
                    message = $"Hoá đơn {request.DocumentNumber} của MST {request.SellerMst} đã tồn tại.",
                    existingVoucherId = maybeDup.Id,
                    existingDocumentDate = maybeDup.DocumentDate,
                });
            }
        }

        await repo.ReplaceExtractedAsync(tenantId, userId, id, request, ct);
        var updated = await repo.FindAsync(tenantId, userId, id, ct);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        IVoucherRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        var tenantId = TenantContext.Require(ctx);

        var deleted = await repo.DeleteAsync(tenantId, userId, id, ct);
        return deleted ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> SaveJournalAsync(
        Guid id,
        SaveJournalSuggestionsRequest request,
        IVoucherRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        if (request?.Entries is null || request.Entries.Count == 0)
        {
            return Results.BadRequest(new { error = "entries required" });
        }

        var tenantId = TenantContext.Require(ctx);
        if (await repo.FindAsync(tenantId, userId, id, ct) is null) return Results.NotFound();
        var saved = await repo.SaveJournalSuggestionsAsync(tenantId, userId, id, request, ct);
        return Results.Ok(saved);
    }

    private static async Task<IResult> DecideJournalAsync(
        Guid id,
        Guid entryId,
        AcceptJournalRequest request,
        IVoucherRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }

        var updated = await repo.AcceptJournalAsync(TenantContext.Require(ctx), id, entryId, userId, request, ct);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    private static bool TryGetUserId(HttpContext ctx, out Guid userId)
    {
        var raw = ctx.User.FindFirst("sub")?.Value
            ?? ctx.User.FindFirst("nameidentifier")?.Value
            ?? ctx.User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
        return Guid.TryParse(raw, out userId);
    }

    private static bool TryValidate(object instance, out Dictionary<string, string[]> errors)
    {
        var validationCtx = new ValidationContext(instance);
        var results = new List<ValidationResult>();
        var ok = Validator.TryValidateObject(instance, validationCtx, results, validateAllProperties: true);
        errors = results
            .SelectMany(r => r.MemberNames.Select(m => (Member: m, r.ErrorMessage)))
            .GroupBy(t => t.Member)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage ?? "Invalid").ToArray());
        return ok;
    }
}
