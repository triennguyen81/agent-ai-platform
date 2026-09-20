using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Books;
using Aiq.Gateway.Middleware;
using Aiq.Gateway.Security;
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
            .RequireAuthorization("department_member")
            .RequireRateLimiting("default")
            .RequireBusinessCapability("voucher.processing");

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
        group.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization("department_admin");

        group.MapPost("/{id:guid}/journal-suggestions", SaveJournalAsync);
        group.MapPost("/{id:guid}/journal-suggestions/{entryId:guid}/decision", DecideJournalAsync).RequireAuthorization("department_admin");

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
        group.MapPost("/{id:guid}/apply-receipt", ApplyReceiptAsync).RequireAuthorization("department_admin");

        // UC3 counterpart — kế toán viên bấm "Chấp nhận chi" trên voucher
        // AP side detail page. Routes based on voucher_type:
        //   invoice_vat_in → book credit row (Có 331 tăng)
        //   payment/bank_transfer → apply debit rows per payment_matching.applications
        //   expense_claim → update sim_employees.outstanding_advance + ytd_expense
        group.MapPost("/{id:guid}/apply-expense", ApplyExpenseAsync).RequireAuthorization("department_admin");

        // Sprint 4 gap UC1: bấm "Chấp nhận HĐ" trên invoice_vat_out detail page
        // → insert debit row vào sim_ar_ledger (Nợ 131 tăng) + update current_ar.
        group.MapPost("/{id:guid}/apply-invoice", ApplyInvoiceAsync).RequireAuthorization("department_admin");

        // Sprint 4 gap UC2: bấm "Huỷ áp thu" khi cần rollback receipt đã áp.
        // DELETE rows tagged voucher:{id} + recompute current_ar.
        group.MapPost("/{id:guid}/unapply-receipt", UnapplyReceiptAsync).RequireAuthorization("department_admin");

        // Symmetric reversal for the Purchase side — previously missing
        // entirely (only Customer/receipt had an unapply path).
        group.MapPost("/{id:guid}/unapply-payment", UnapplyPaymentAsync).RequireAuthorization("department_admin");

        // Luồng 02 — Customer/AR/Contract-installment match proposal for a
        // receipt voucher. Read is Department-scoped like GetAsync; reject
        // (ADMIN decides the proposed match is wrong, e.g. UNKNOWN_PAYER/
        // OVERPAYMENT needing a different treatment) is department_admin-only,
        // same posture as apply-receipt which confirms it.
        group.MapGet("/{id:guid}/receipt-match", GetReceiptMatchAsync);
        // "Process a record I created" (creator-or-admin, not read-only) —
        // classifies the outcome from the voucher's own buyerMst/totalAmount/
        // currency directly, independent of the LLM extraction pipeline
        // having run. apply-receipt also (re-)proposes internally so a real
        // upload flow never needs this called explicitly; exposed so a USER
        // can process their own receipt without waiting on/needing an ADMIN.
        group.MapPost("/{id:guid}/receipt-match/propose", ProposeReceiptMatchAsync);
        group.MapPost("/{id:guid}/reject-receipt-match", RejectReceiptMatchAsync).RequireAuthorization("department_admin");

        // Luồng 03 — expense_claim outcome classification (duplicate/policy/
        // advance reconciliation). Same posture as receipt-match: read is
        // Department-scoped, propose is creator-or-admin (a USER may process
        // their own claim), reject/grant-exception/export are ADMIN-only.
        group.MapGet("/{id:guid}/expense-claim-result", GetExpenseClaimResultAsync);
        group.MapPost("/{id:guid}/expense-claim-result/propose", ProposeExpenseClaimAsync);
        group.MapPost("/{id:guid}/reject-expense-claim", RejectExpenseClaimAsync).RequireAuthorization("department_admin");
        group.MapPost("/{id:guid}/grant-expense-exception", GrantExpenseExceptionAsync).RequireAuthorization("department_admin");
        // R14 — Luồng 03's 3-stage approval (Department review -> Accounting
        // review -> Payment approval == apply-expense above). Each stage is
        // ADMIN-only, Department-scoped, and enforces a different actor than
        // every earlier stage (IExpenseClaimRepository) plus never the
        // claim's own creator (R16, checked here before calling in).
        group.MapPost("/{id:guid}/expense-claim/department-review", DepartmentReviewExpenseClaimAsync).RequireAuthorization("department_admin");
        group.MapPost("/{id:guid}/expense-claim/accounting-review", AccountingReviewExpenseClaimAsync).RequireAuthorization("department_admin");
        // Read-only payment/export handoff payload — never marks anything
        // paid; only valid once ADMIN has confirmed (review_status=confirmed).
        group.MapGet("/{id:guid}/expense-claim-export", GetExpenseClaimExportAsync).RequireAuthorization("department_admin");

        // R05 fix — persists a REAL positive result from the Agent's
        // verify_einvoice_tct tool (genuine hoadondientu.gdt.gov.vn lookup,
        // agent/src/aiq_agent/agentic/voucher_tools.py). Deterministic
        // one-way flip (false→true only); never called for an "unavailable"
        // or negative result, so this endpoint never fabricates a
        // verification that didn't happen.
        group.MapPost("/{id:guid}/tax-verification/confirm", ConfirmTaxVerifiedAsync);

        // P1 audit fix — the other two honest outcomes of a tax-portal
        // check: found-and-invalid/cancelled, or the portal was
        // unreachable. Recording these (instead of leaving the voucher
        // silently "unverified" with no trace of why) is what lets
        // IExpenseClaimRepository.ConfirmAsync's approval gate distinguish
        // "never checked" from "checked and blocked".
        group.MapPost("/{id:guid}/tax-verification/reject", RejectTaxVerificationAsync);

        // R06 — explicit, human-recorded Contract -> Invoice/Receipt link.
        // Department-scoped; the repository itself re-verifies the contract
        // (and installment, if given) resolve in the SAME tenant+department
        // as the voucher, so this can never cross a tenant/department
        // boundary even if the caller supplies a foreign id.
        group.MapPatch("/{id:guid}/link-contract", LinkContractAsync);

        return app;
    }

    private static async Task<IResult> ConfirmTaxVerifiedAsync(
        Guid id,
        IVoucherRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var tenantId, out var departmentId)) return Results.Forbid();
        var updated = await repo.MarkTaxVerifiedAsync(tenantId, departmentId, id, ct);
        return updated ? Results.Ok(new { voucherId = id, tctVerified = true }) : Results.NotFound();
    }

    private sealed record RejectTaxVerificationRequest(string Reason, bool ProviderUnavailable);

    private static async Task<IResult> RejectTaxVerificationAsync(
        Guid id,
        RejectTaxVerificationRequest request,
        IVoucherRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Results.BadRequest(new { error = "reason required" });
        }
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var tenantId, out var departmentId)) return Results.Forbid();
        var updated = await repo.RejectTaxVerificationAsync(tenantId, departmentId, id, request.Reason, request.ProviderUnavailable, ct);
        return updated
            ? Results.Ok(new { voucherId = id, taxVerificationStatus = request.ProviderUnavailable ? "unavailable" : "invalid" })
            : Results.NotFound();
    }

    private sealed record LinkContractRequest(Guid ContractId, Guid? InstallmentId);

    private static async Task<IResult> LinkContractAsync(
        Guid id,
        LinkContractRequest request,
        IVoucherRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var tenantId, out var departmentId)) return Results.Forbid();
        var updated = await repo.LinkContractAsync(tenantId, departmentId, id, request.ContractId, request.InstallmentId, ct);
        return updated
            ? Results.Ok(new { voucherId = id, contractId = request.ContractId, installmentId = request.InstallmentId })
            : Results.NotFound(new { error = "voucher hoặc hợp đồng/đợt thanh toán không thuộc cùng phòng ban" });
    }

    private sealed record ReextractRequest(string? RawText, string? VoucherType);

    private static async Task<IResult> ApplyReceiptAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Customers.ICustomerRepository customers,
        Aiq.Gateway.Receipts.IReceiptMatchRepository receiptMatches,
        HttpContext ctx,
        CancellationToken ct)
    {
        // Route is department_admin-only — ADMIN applies on any Department
        // voucher regardless of who created it, so scope by tenant+department,
        // never by the acting admin's own userId (that was the bug: an ADMIN
        // could previously only apply-receipt on vouchers they themselves had
        // uploaded).
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();

        var voucher = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        if (voucher is null) return Results.NotFound();
        if (voucher.Voucher.VoucherType != "receipt")
        {
            return Results.BadRequest(new { error = "apply-receipt chỉ áp dụng cho phiếu thu" });
        }
        if (BusinessModuleAuthorization.IsSelfApproval(voucher.Voucher.UserId, principal.UserId))
        {
            return Results.Forbid();
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

        // Luồng 02 — (re-)propose then confirm. Proposing here too (not only
        // from the extraction pipeline) makes apply-receipt self-contained:
        // it works even if the LLM extraction step never ran/failed to
        // persist a proposal, using the same ar_context/receipt_matching
        // data already required above. ProposeMatchAsync only touches rows
        // still in 'proposed' state, so this never clobbers an existing
        // confirmed/rejected result. ConfirmMatchAsync itself no-ops if
        // already confirmed, matching ApplyReceiptAsync's own ledger-side
        // idempotency above.
        var originalReceiptAmount = matchEl.TryGetProperty("receipt_amount", out var raEl) && raEl.ValueKind == System.Text.Json.JsonValueKind.Number
            ? raEl.GetDecimal()
            : apps.Sum(a => a.AppliedAmount) + discount;
        await receiptMatches.ProposeMatchAsync(
            tenantId, departmentId, id, voucher.Voucher.BuyerMst,
            originalReceiptAmount, voucher.Voucher.Currency, ct);
        var receiptMatch = await receiptMatches.ConfirmMatchAsync(tenantId, departmentId, id, principal.UserId, ct);

        // Flip voucher to verified so the Accept button hides.
        await repo.UpdateAsync(
            tenantId,
            voucher.Voucher.UserId,
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
            receipt_match = receiptMatch,
        });
    }

    // Luồng 02 — any Department member with voucher.processing may read the
    // proposed/confirmed match for a receipt voucher, same read-scope as
    // GetAsync (department-wide, not creator-scoped).
    private static async Task<IResult> GetReceiptMatchAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Receipts.IReceiptMatchRepository receiptMatches,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var tenantId, out var departmentId)) return Results.Forbid();
        if (await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct) is null) return Results.NotFound();
        var match = await receiptMatches.GetByVoucherIdAsync(tenantId, departmentId, id, ct);
        return match is null ? Results.NotFound() : Results.Ok(match);
    }

    // "USER may process records they created" — creator-or-admin, same
    // posture as ReextractAsync/UpdateAsync. Classifies directly from the
    // voucher's own buyerMst/totalAmount/currency, so this works even when
    // extracted_fields has no ar_context/receipt_matching yet (e.g. the LLM
    // extraction step never ran) — the whole point of exposing it standalone
    // rather than only ever firing inside the extraction pipeline.
    private static async Task<IResult> ProposeReceiptMatchAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Receipts.IReceiptMatchRepository receiptMatches,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        var voucher = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        if (voucher is null) return Results.NotFound();
        if (voucher.Voucher.VoucherType != "receipt")
        {
            return Results.BadRequest(new { error = "receipt-match/propose chỉ áp dụng cho phiếu thu" });
        }
        if (!principal.IsInRole("admin") && voucher.Voucher.UserId != principal.UserId)
        {
            return Results.Forbid();
        }

        var match = await receiptMatches.ProposeMatchAsync(
            tenantId, departmentId, id, voucher.Voucher.BuyerMst,
            voucher.Voucher.TotalAmount ?? 0m, voucher.Voucher.Currency, ct);

        // J1 fix — deterministic journal-entry proposal from the outcome
        // ProposeMatchAsync just classified (never from an LLM). fxRateConfigured
        // is hard-false: no FX rate source exists anywhere in this system
        // (BR09, still BLOCKED) — passing anything else would fabricate a rate.
        var routing = Aiq.Gateway.Accounting.JournalRoutingRules.ForReceiptOutcome(
            match.Outcome, voucher.Voucher.PaymentMethod,
            match.ReceiptAmount, match.MatchedAmount, match.UnallocatedAmount,
            fxRateConfigured: false);
        if (routing.Outcome == Aiq.Gateway.Accounting.JournalRoutingOutcome.Proposed)
        {
            try
            {
                await repo.SaveJournalSuggestionsAsync(tenantId, departmentId, id,
                    new SaveJournalSuggestionsRequest(
                        routing.Entries.Select(e => new SaveJournalSuggestionDto(
                            e.EntryNo, e.DebitAccount, e.CreditAccount, e.Amount, e.Description, Confidence: 1.0m)).ToList(),
                        ReplaceExisting: true),
                    ct);
            }
            catch (Exception) { /* proposal is best-effort — never breaks the already-committed classification */ }
        }

        return Results.Ok(match);
    }

    private static async Task<IResult> RejectReceiptMatchAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Receipts.IReceiptMatchRepository receiptMatches,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        var voucher = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        if (voucher is null) return Results.NotFound();
        if (BusinessModuleAuthorization.IsSelfApproval(voucher.Voucher.UserId, principal.UserId)) return Results.Forbid();
        var updated = await receiptMatches.RejectMatchAsync(tenantId, departmentId, id, principal.UserId, ct);
        return updated is null ? Results.Conflict(new { error = "receipt_match_not_pending" }) : Results.Ok(updated);
    }

    // Luồng 03 — any Department member with voucher.processing may read the
    // proposed/confirmed expense claim result, same read-scope as GetAsync.
    private static async Task<IResult> GetExpenseClaimResultAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Employees.IExpenseClaimRepository expenseClaims,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var tenantId, out var departmentId)) return Results.Forbid();
        if (await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct) is null) return Results.NotFound();
        var result = await expenseClaims.GetByVoucherIdAsync(tenantId, departmentId, id, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    // "USER may process records they created" — creator-or-admin, same
    // posture as receipt-match/propose. Re-parses employee_code/total_claim/
    // advance_taken/items directly from the voucher's own extracted_fields
    // (the raw pre-enrichment keys survive alongside expense_claim_matching),
    // so this works even when the LLM extraction pipeline never ran.
    private static async Task<IResult> ProposeExpenseClaimAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Employees.IExpenseClaimRepository expenseClaims,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        var voucher = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        if (voucher is null) return Results.NotFound();
        if (voucher.Voucher.VoucherType != "expense_claim")
        {
            return Results.BadRequest(new { error = "expense-claim-result/propose chỉ áp dụng cho đề nghị hoàn ứng" });
        }
        if (!principal.IsInRole("admin") && voucher.Voucher.UserId != principal.UserId)
        {
            return Results.Forbid();
        }

        var extractedJson = voucher.Voucher.ExtractedFieldsJson ?? "{}";
        using var doc = System.Text.Json.JsonDocument.Parse(extractedJson);
        var root = doc.RootElement;

        string? employeeCode = root.TryGetProperty("employee_code", out var ecEl) && ecEl.ValueKind == System.Text.Json.JsonValueKind.String
            ? ecEl.GetString() : null;
        decimal? totalClaim = root.TryGetProperty("total_claim", out var tcEl) && tcEl.ValueKind == System.Text.Json.JsonValueKind.Number
            ? tcEl.GetDecimal() : null;
        decimal advanceTaken = root.TryGetProperty("advance_taken", out var atEl) && atEl.ValueKind == System.Text.Json.JsonValueKind.Number
            ? atEl.GetDecimal() : 0m;
        string currency = root.TryGetProperty("currency", out var curEl) && curEl.ValueKind == System.Text.Json.JsonValueKind.String
            ? curEl.GetString() ?? "VND" : "VND";

        var items = new List<Aiq.Gateway.Employees.ExpenseClaimItemInput>();
        if (root.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var seq = 0;
            foreach (var it in itemsEl.EnumerateArray())
            {
                seq++;
                var category = it.TryGetProperty("category", out var catEl) && catEl.ValueKind == System.Text.Json.JsonValueKind.String ? catEl.GetString()! : "khác";
                string? description = it.TryGetProperty("description", out var descEl) && descEl.ValueKind == System.Text.Json.JsonValueKind.String ? descEl.GetString() : null;
                var amount = it.TryGetProperty("amount", out var amtEl) && amtEl.ValueKind == System.Text.Json.JsonValueKind.Number ? amtEl.GetDecimal() : 0m;
                bool? receiptAttached = it.TryGetProperty("receipt_attached", out var raEl)
                    ? raEl.ValueKind switch { System.Text.Json.JsonValueKind.True => true, System.Text.Json.JsonValueKind.False => false, _ => null }
                    : null;
                string? vendorMst = it.TryGetProperty("vendor_mst", out var vmEl) && vmEl.ValueKind == System.Text.Json.JsonValueKind.String ? vmEl.GetString() : null;
                string? invoiceNo = it.TryGetProperty("invoice_no", out var invEl) && invEl.ValueKind == System.Text.Json.JsonValueKind.String ? invEl.GetString() : null;
                DateOnly? docDate = it.TryGetProperty("document_date", out var ddEl) && ddEl.ValueKind == System.Text.Json.JsonValueKind.String
                    && DateOnly.TryParse(ddEl.GetString(), out var parsedDate) ? parsedDate : null;
                decimal? vatAmount = it.TryGetProperty("vat_amount", out var vatEl) && vatEl.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? vatEl.GetDecimal() : null;
                items.Add(new Aiq.Gateway.Employees.ExpenseClaimItemInput(seq, category, description, amount, receiptAttached, vendorMst, invoiceNo, docDate, vatAmount));
            }
        }

        var result = await expenseClaims.ProposeAsync(
            tenantId, departmentId, id,
            new Aiq.Gateway.Employees.ExpenseClaimProposalInput(employeeCode, totalClaim, advanceTaken, currency, items),
            ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> RejectExpenseClaimAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Employees.IExpenseClaimRepository expenseClaims,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        var voucher = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        if (voucher is null) return Results.NotFound();
        if (BusinessModuleAuthorization.IsSelfApproval(voucher.Voucher.UserId, principal.UserId)) return Results.Forbid();
        var updated = await expenseClaims.RejectAsync(tenantId, departmentId, id, principal.UserId, ct);
        return updated is null ? Results.Conflict(new { error = "expense_claim_not_pending" }) : Results.Ok(updated);
    }

    // R14 stage 1 — "Bộ phận" (department review): claim reasonableness.
    private static async Task<IResult> DepartmentReviewExpenseClaimAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Employees.IExpenseClaimRepository expenseClaims,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        var voucher = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        if (voucher is null) return Results.NotFound();
        if (BusinessModuleAuthorization.IsSelfApproval(voucher.Voucher.UserId, principal.UserId)) return Results.Forbid();
        var updated = await expenseClaims.DepartmentReviewAsync(tenantId, departmentId, id, principal.UserId, ct);
        return updated is null
            ? Results.Conflict(new { error = "expense_claim_not_ready_for_department_review" })
            : Results.Ok(updated);
    }

    // R14 stage 2 — "Kế toán" (accounting review): tax/account validity.
    // Rejected with a 409 (not Forbid) when the actor is the same as stage
    // 1's reviewer, since that's a business-state conflict (this specific
    // claim can't proceed with this reviewer), not an authorization denial.
    private static async Task<IResult> AccountingReviewExpenseClaimAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Employees.IExpenseClaimRepository expenseClaims,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        var voucher = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        if (voucher is null) return Results.NotFound();
        if (BusinessModuleAuthorization.IsSelfApproval(voucher.Voucher.UserId, principal.UserId)) return Results.Forbid();
        var updated = await expenseClaims.AccountingReviewAsync(tenantId, departmentId, id, principal.UserId, ct);
        return updated is null
            ? Results.Conflict(new { error = "expense_claim_not_ready_for_accounting_review_or_same_reviewer_as_department_stage" })
            : Results.Ok(updated);
    }

    // ADMIN grants a policy exception on a WAITING_EXCEPTION_APPROVAL claim
    // — un-caps to the full requested amount (minus only genuinely blocked
    // duplicate lines) and applies it immediately (same one-step
    // confirm-and-apply posture as ApplyExpenseAsync's plain confirm).
    private static async Task<IResult> GrantExpenseExceptionAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Employees.IExpenseClaimRepository expenseClaims,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        var voucher = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        if (voucher is null) return Results.NotFound();
        if (BusinessModuleAuthorization.IsSelfApproval(voucher.Voucher.UserId, principal.UserId)) return Results.Forbid();
        var updated = await expenseClaims.GrantExceptionAsync(tenantId, departmentId, id, principal.UserId, ct);
        return updated is null ? Results.Conflict(new { error = "expense_claim_not_waiting_exception" }) : Results.Ok(updated);
    }

    // Payment/export prep (EXP-14) — read-only, structured handoff payload.
    // Never marks anything paid; only reachable once ADMIN has confirmed.
    // The Agent boundary: AiQ prepares this data, a human/external system
    // executes the actual bank payment.
    private static async Task<IResult> GetExpenseClaimExportAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Employees.IExpenseClaimRepository expenseClaims,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var tenantId, out var departmentId)) return Results.Forbid();
        var voucher = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        if (voucher is null) return Results.NotFound();
        var result = await expenseClaims.GetByVoucherIdAsync(tenantId, departmentId, id, ct);
        if (result is null) return Results.NotFound();
        if (result.ReviewStatus != "confirmed")
        {
            return Results.BadRequest(new { error = "expense_claim chưa được duyệt — không thể chuẩn bị dữ liệu thanh toán" });
        }
        return Results.Ok(new
        {
            voucherId = id,
            documentNumber = voucher.Voucher.DocumentNumber,
            employeeId = result.EmployeeId,
            outcome = result.Outcome,
            approvedAmount = result.ApprovedAmount,
            advanceAmount = result.AdvanceAmount,
            payableAmount = result.PayableAmount,
            payableDirection = result.PayableAmount >= 0m ? "company_owes_employee" : "employee_owes_company",
            currency = result.Currency,
            // R15 — vatAmount/vatDeductibleAmount are real per-item VAT
            // sums (0 if the source never carried a VAT breakdown — never
            // fabricated), independent of citNonDeductibleAmount (a
            // separate flag: a line can be VAT-deductible and CIT-non-
            // deductible or vice versa).
            vatAmount = result.VatAmount,
            vatDeductibleAmount = result.VatDeductibleAmount,
            citNonDeductibleAmount = result.NonDeductibleAmount,
            readyForPayment = true,
            paymentExecuted = false,
            note = "Dữ liệu chuẩn bị xuất/thanh toán — AiQ không tự động thực hiện thanh toán ngân hàng.",
        });
    }

    private static async Task<IResult> ApplyInvoiceAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Customers.ICustomerRepository customers,
        Aiq.Gateway.Governance.ITenantRepository tenants,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();

        var voucher = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        if (voucher is null) return Results.NotFound();
        var vt = voucher.Voucher.VoucherType?.ToLowerInvariant() ?? "";
        if (vt is not ("invoice_vat_out" or "invoice_vat"))
        {
            return Results.BadRequest(new
            {
                error = "apply-invoice chỉ áp dụng cho invoice_vat_out (hoá đơn bán)",
            });
        }
        if (BusinessModuleAuthorization.IsSelfApproval(voucher.Voucher.UserId, principal.UserId))
        {
            return Results.Forbid();
        }
        if (voucher.Voucher.Status == "verified")
        {
            return Results.Ok(new { voucherId = id, status = "already_applied", message = "Đã áp trước đó." });
        }

        // Deterministic direction gate — never trust voucher_type alone (it
        // is set by the LLM extraction pipeline from document text). Resolve
        // from the voucher's own seller/buyer MST against the tenant's
        // server-side-configured company_tax_id; block the apply rather than
        // silently booking AR under a possibly-wrong classification.
        var companyTaxId = await tenants.GetCompanyTaxIdAsync(tenantId, ct);
        var resolution = Aiq.Gateway.Accounting.InvoiceDirectionResolver.Resolve(
            voucher.Voucher.SellerMst, voucher.Voucher.BuyerMst, companyTaxId);
        if (resolution.Direction != Aiq.Gateway.Accounting.InvoiceDirection.Sales)
        {
            return Results.Conflict(new
            {
                error = "invoice_direction_mismatch",
                resolvedDirection = resolution.Direction.ToString().ToLowerInvariant(),
                reason = resolution.Reason,
                message = resolution.Direction == Aiq.Gateway.Accounting.InvoiceDirection.NeedsReview
                    ? "Không xác định được chiều hoá đơn (bán/mua) một cách chắc chắn — cần cấu hình MST công ty hoặc kiểm tra lại MST người bán/mua."
                    : "MST người mua trùng với MST công ty hiện tại — đây có vẻ là hoá đơn MUA, không áp dụng apply-invoice (hoá đơn bán).",
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
            tenantId, customerId, id, docNumber, docDate, dueDate, invoiceTotal,
            voucher.Voucher.ContractId, voucher.Voucher.ContractInstallmentId, ct);

        await repo.UpdateAsync(
            tenantId, voucher.Voucher.UserId, id,
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
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var tenantId, out var departmentId)) return Results.Forbid();

        var voucher = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
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
            tenantId, voucher.Voucher.UserId, id,
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

    // Symmetric to UnapplyReceiptAsync, Purchase side — previously missing
    // entirely: only payment/bank_transfer vouchers (not invoice_vat_in
    // itself, which is a one-shot book, not a reversible "match") are
    // unappliable, mirroring what actually gets reversed.
    private static async Task<IResult> UnapplyPaymentAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Suppliers.ISupplierRepository suppliers,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var tenantId, out var departmentId)) return Results.Forbid();

        var voucher = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        if (voucher is null) return Results.NotFound();
        var vt = voucher.Voucher.VoucherType?.ToLowerInvariant() ?? "";
        if (vt is not ("payment" or "bank_transfer"))
        {
            return Results.BadRequest(new { error = "unapply-payment chỉ áp dụng cho payment / bank_transfer" });
        }
        if (voucher.Voucher.Status != "verified")
        {
            return Results.BadRequest(new
            {
                error = $"chỉ unapply được payment đã verified (hiện: {voucher.Voucher.Status})",
            });
        }

        var extractedJson = voucher.Voucher.ExtractedFieldsJson ?? "{}";
        using var doc = System.Text.Json.JsonDocument.Parse(extractedJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("ap_context", out var apEl)
            || !apEl.TryGetProperty("supplier", out var supEl)
            || !supEl.TryGetProperty("Id", out var supIdEl))
        {
            return Results.BadRequest(new { error = "voucher chưa có ap_context.supplier" });
        }
        var supplierId = supIdEl.GetGuid();

        var result = await suppliers.UnapplyExpenseAsync(tenantId, supplierId, id, ct);

        await repo.UpdateAsync(
            tenantId, voucher.Voucher.UserId, id,
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
        Aiq.Gateway.Employees.IExpenseClaimRepository expenseClaims,
        Aiq.Gateway.Governance.ITenantRepository tenants,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();

        var voucher = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        if (voucher is null) return Results.NotFound();
        var vt = voucher.Voucher.VoucherType?.ToLowerInvariant() ?? "";
        if (vt is not ("invoice_vat_in" or "payment" or "bank_transfer" or "expense_claim"))
        {
            return Results.BadRequest(new
            {
                error = "apply-expense chỉ áp dụng cho invoice_vat_in / payment / bank_transfer / expense_claim",
            });
        }
        if (BusinessModuleAuthorization.IsSelfApproval(voucher.Voucher.UserId, principal.UserId))
        {
            return Results.Forbid();
        }
        if (voucher.Voucher.Status == "verified")
        {
            return Results.Ok(new { voucherId = id, status = "already_applied", message = "Đã áp trước đó." });
        }

        // Same deterministic direction gate as ApplyInvoiceAsync — only for
        // invoice_vat_in (payment/bank_transfer settle an AP that already
        // passed this gate when its invoice was booked; expense_claim is a
        // different domain, no seller/buyer MST pair at all).
        if (vt == "invoice_vat_in")
        {
            var companyTaxId = await tenants.GetCompanyTaxIdAsync(tenantId, ct);
            var resolution = Aiq.Gateway.Accounting.InvoiceDirectionResolver.Resolve(
                voucher.Voucher.SellerMst, voucher.Voucher.BuyerMst, companyTaxId);
            if (resolution.Direction != Aiq.Gateway.Accounting.InvoiceDirection.Purchase)
            {
                return Results.Conflict(new
                {
                    error = "invoice_direction_mismatch",
                    resolvedDirection = resolution.Direction.ToString().ToLowerInvariant(),
                    reason = resolution.Reason,
                    message = resolution.Direction == Aiq.Gateway.Accounting.InvoiceDirection.NeedsReview
                        ? "Không xác định được chiều hoá đơn (bán/mua) một cách chắc chắn — cần cấu hình MST công ty hoặc kiểm tra lại MST người bán/mua."
                        : "MST người bán trùng với MST công ty hiện tại — đây có vẻ là hoá đơn BÁN, không áp dụng apply-expense (hoá đơn mua).",
                });
            }
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
                    tenantId, supplierId, id, docNumber, docDate, dueDate, invoiceTotal,
                    voucher.Voucher.ContractId, voucher.Voucher.ContractInstallmentId, ct);
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
                // Luồng 03 — the outcome classification (duplicate/policy/
                // advance reconciliation) must already have been proposed,
                // either by the extraction pipeline or by explicitly calling
                // POST /expense-claim-result/propose first (same posture as
                // apply-receipt requiring receipt_matching beforehand).
                var existing = await expenseClaims.GetByVoucherIdAsync(tenantId, departmentId, id, ct);
                if (existing is null)
                {
                    return Results.BadRequest(new { error = "voucher chưa có expense_claim_result — chạy trích xuất lại hoặc gọi expense-claim-result/propose trước" });
                }
                if (existing.Outcome is "RETURN_FOR_DOCUMENTS" or "REJECTED")
                {
                    return Results.BadRequest(new { error = $"expense_claim outcome={existing.Outcome} — không thể apply, cần bổ sung chứng từ hoặc đã bị từ chối" });
                }
                // P1 audit fix — internal-control gate #1: a claim carrying a
                // real invoice cannot reach final payment approval unless
                // that invoice was actually checked against the tax portal
                // and came back positive. "Never checked" (null) and
                // "checked and invalid/cancelled/unavailable" are both
                // blocked here — this is the one and only path that settles
                // an expense_claim voucher, so there is no way around it.
                if (existing.RequiresTaxVerification && voucher.Voucher.TaxVerificationStatus != "verified")
                {
                    return Results.Conflict(new
                    {
                        error = "tax_verification_required",
                        taxVerificationStatus = voucher.Voucher.TaxVerificationStatus,
                        reason = voucher.Voucher.TaxVerificationReason,
                    });
                }

                var confirmed = await expenseClaims.ConfirmAsync(tenantId, departmentId, id, principal.UserId, ct);
                if (confirmed is null)
                {
                    return Results.Conflict(new { error = "expense_claim_result không ở trạng thái có thể apply" });
                }
                payload = new
                {
                    voucherId = id,
                    status = "applied",
                    voucher_type = vt,
                    expense_claim_result = confirmed,
                };
                break;
            }

            default:
                return Results.BadRequest(new { error = $"voucher_type không được hỗ trợ: {vt}" });
        }

        // Flip voucher to verified so the Accept button hides + auto-refresh.
        await repo.UpdateAsync(
            tenantId,
            voucher.Voucher.UserId,
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
        // Re-running extraction mutates the voucher (raw_ocr_text, then
        // re-triggers the LLM step) — same creator-scoped-USER /
        // unrestricted-ADMIN rule as UpdateAsync, not just a read.
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        var userId = principal.UserId;

        var voucher = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        if (voucher is null)
        {
            return Results.NotFound();
        }
        if (!principal.IsInRole("admin") && voucher.Voucher.UserId != userId)
        {
            return Results.Forbid();
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
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var tenantId, out var departmentId)) return Results.Forbid();

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
            departmentId,
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
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var tenantId, out var departmentId)) return Results.Forbid();

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
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var tenantId, out var departmentId)) return Results.Forbid();

        var created = await repo.CreateAsync(tenantId, departmentId, userId, request, ct);
        return Results.Created($"/vouchers/{created.Voucher.Id}", created);
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Governance.ITenantRepository tenants,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var tenantId, out var departmentId)) return Results.Forbid();

        var voucher = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        if (voucher is null) return Results.NotFound();

        // Explain, never decide — this is display-only so a human reviewer
        // (or the agent, when asked to explain) can see the deterministic
        // classification BEFORE apply-invoice/apply-expense enforces it.
        var vt = voucher.Voucher.VoucherType?.ToLowerInvariant() ?? "";
        object? resolvedDirection = null;
        if (vt is "invoice_vat_out" or "invoice_vat_in" or "invoice_vat")
        {
            var companyTaxId = await tenants.GetCompanyTaxIdAsync(tenantId, ct);
            var resolution = Aiq.Gateway.Accounting.InvoiceDirectionResolver.Resolve(
                voucher.Voucher.SellerMst, voucher.Voucher.BuyerMst, companyTaxId);
            resolvedDirection = new
            {
                direction = resolution.Direction.ToString().ToLowerInvariant(),
                counterpartyTaxId = resolution.CounterpartyTaxId,
                reason = resolution.Reason,
            };
        }

        return Results.Ok(new { voucher.Voucher, voucher.LineItems, voucher.JournalSuggestions, resolvedDirection });
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
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        var existing = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        if (existing is null) return Results.NotFound();
        if (!principal.IsInRole("admin") && existing.Voucher.UserId != userId) return Results.Forbid();
        if (existing.Voucher.Status is "verified" or "journalized" or "rejected") return Results.Conflict(new { error = "voucher_finalized" });
        var updated = await repo.UpdateAsync(tenantId, existing.Voucher.UserId, id, request, ct);
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
        // Department-scoped read, matching GetAsync — any Department member
        // with voucher.processing may view the metadata AND the attached
        // file of any Department voucher, not just their own uploads.
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var tenantId, out var departmentId)) return Results.Forbid();

        var voucher = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
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
        // from before the migration. Checked against the voucher's actual
        // owner, not the viewing caller — a department-mate previewing a
        // colleague's voucher is legitimate now.
        var isTenantObject = TenantObjectKey.BelongsTo(tenantId, path);
        var isOwnedLegacyObject = path.StartsWith($"vouchers/{voucher.Voucher.UserId:N}/", StringComparison.Ordinal);
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
        // Route is department_admin-only. The repo's DeleteAsync still
        // filters by (tenant_id, user_id) — so look the voucher up by
        // Department first and pass its actual creator's id through, never
        // the deleting admin's own id (that was the bug: an ADMIN could
        // previously only delete vouchers they themselves had created).
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var tenantId, out var departmentId)) return Results.Forbid();
        var existing = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        if (existing is null) return Results.NotFound();

        var deleted = await repo.DeleteAsync(tenantId, existing.Voucher.UserId, id, ct);
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

        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        var existing = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        if (existing is null) return Results.NotFound();
        // Proposing journal entries is an edit on the voucher itself — same
        // creator-scoped rule as PATCH /vouchers/{id}: ADMIN unrestricted,
        // USER only on their own, still-editable voucher.
        if (!principal.IsInRole("admin") && existing.Voucher.UserId != userId) return Results.Forbid();
        if (existing.Voucher.Status is "verified" or "journalized" or "rejected") return Results.Conflict(new { error = "voucher_finalized" });

        // J1 fix — the business process document's situational journal
        // table is a closed allow-list for receipt/expense_claim vouchers
        // ("ngoài danh mục này, agent không tự sinh bút toán nào khác").
        // This blocks any (debit,credit) pair outside that list from being
        // saved for those two voucher types, regardless of whether it came
        // from the deterministic router above or the Agent's free-form
        // suggest_journal_entry tool.
        if (existing.Voucher.VoucherType is "receipt" or "expense_claim")
        {
            var disallowed = request.Entries
                .Where(e => !Aiq.Gateway.Accounting.JournalRoutingRules.IsAllowed(e.DebitAccount, e.CreditAccount))
                .Select(e => $"{e.DebitAccount}/{e.CreditAccount}")
                .ToArray();
            if (disallowed.Length > 0)
            {
                return Results.BadRequest(new { error = "account_pair_not_allowed", pairs = disallowed });
            }
        }

        var saved = await repo.SaveJournalSuggestionsAsync(tenantId, departmentId, id, request, ct);
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
        // Route already requires "department_admin" — ADMIN decides on any
        // Department voucher regardless of who created it, so scoping is
        // tenant+department here, never creator.
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var tenantId, out var departmentId)) return Results.Forbid();

        var updated = await repo.AcceptJournalAsync(tenantId, departmentId, id, entryId, userId, request, ct);
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
