using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Audit;
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
            .RequireBusinessCapability("voucher.processing")
            // A recognized expense claim's document/amounts are frozen by DB
            // triggers (migration 059): every edit path answers 409, never 500.
            .AddEndpointFilter(async (invocation, next) =>
            {
                try { return await next(invocation); }
                catch (Npgsql.PostgresException ex) when (ex.SqlState == "23001" && ex.MessageText.StartsWith("journal_posting_immutable", StringComparison.Ordinal))
                {
                    return Results.Conflict(new { error = "journal_posting_immutable", message = "Bút toán đã ghi sổ không thể sửa hoặc xoá — chỉ có thể đảo bằng bút toán mới." });
                }
                catch (Npgsql.PostgresException ex) when (ex.SqlState == "23001" && ex.MessageText.StartsWith("expense_claim_recognized", StringComparison.Ordinal))
                {
                    return Results.Conflict(new
                    {
                        error = "expense_claim_recognized",
                        message = "Đề nghị đã được ghi nhận kế toán — không thể sửa/trích xuất lại số liệu (cần đảo ghi nhận trước).",
                    });
                }
            });

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
        // Chứng từ flow v2 PR2: deterministic validator result (advisory only).
        group.MapGet("/{id:guid}/validation", ValidationAsync);
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

        // Compatibility entry point for "Chấp nhận thu" — delegates to the
        // cash-settlement service (MapCashSettlement below): cash journal +
        // payment applications on a CERTAIN match only (else 409
        // admin_selection_required), current_ar recomputed, one transaction.
        group.MapPost("/{id:guid}/apply-receipt", ApplyReceiptAsync).RequireAuthorization("department_admin");

        // UC3 counterpart. Routes based on voucher_type:
        //   invoice_vat_in → book credit row (Có 331 tăng)
        //   payment/bank_transfer → cash-settlement service (Nợ 331 / Có 111x|112x)
        //   expense_claim → update sim_employees.outstanding_advance + ytd_expense
        group.MapPost("/{id:guid}/apply-expense", ApplyExpenseAsync).RequireAuthorization("department_admin");

        // Sprint 4 gap UC1: bấm "Chấp nhận HĐ" trên invoice_vat_out detail page
        // → insert debit row vào sim_ar_ledger (Nợ 131 tăng) + update current_ar.
        group.MapPost("/{id:guid}/apply-invoice", ApplyInvoiceAsync).RequireAuthorization("department_admin");

        // "Huỷ áp thu": voids the posting via the cash-settlement service —
        // reversing rows + reversing journal, never a DELETE.
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
        // ADMIN-only and Department-scoped. ADMIN Department Authority
        // Invariant: the same ADMIN (including the claim's creator) may
        // perform every stage — no different-actor requirement.
        group.MapPost("/{id:guid}/expense-claim/department-review", DepartmentReviewExpenseClaimAsync).RequireAuthorization("department_admin");
        group.MapPost("/{id:guid}/expense-claim/accounting-review", AccountingReviewExpenseClaimAsync).RequireAuthorization("department_admin");
        // Read-only payment/export handoff payload — never marks anything
        // paid; only valid once ADMIN has confirmed (review_status=confirmed).
        group.MapGet("/{id:guid}/expense-claim-export", GetExpenseClaimExportAsync).RequireAuthorization("department_admin");

        // THANH TOÁN — final approval + actual payment ("Duyệt & chi tiền"):
        // canonical cash posting (source_type='expense_claim', Dr 334 /
        // Cr 111x|112x) + journal + PAID in one transaction. Department ADMIN
        // only (the same ADMIN may also be claimant/earlier reviewer — ADMIN
        // Department Authority Invariant); the authoritative tax gate is re-checked inside.
        group.MapGet("/{id:guid}/expense-claim/payment", GetExpenseClaimPaymentAsync);
        group.MapPost("/{id:guid}/expense-claim/pay", PayExpenseClaimAsync).RequireAuthorization("department_admin");

        // Tax verification gate (Vouchers/TaxVerification.cs).
        //   GET     — state, current invoice identities, history.
        //   propose — any member / the Agent: a recommendation (verified /
        //             invalid / unavailable); authoritative state stays
        //             'pending_review' and never overrides an ADMIN decision.
        //   confirm — department ADMIN with evidence matched to the voucher's
        //             current invoices; never the expense claim's submitter.
        //   reject  — department ADMIN with evidence (source + reason):
        //             authoritative invalid / unavailable.
        group.MapGet("/{id:guid}/tax-verification", GetTaxVerificationAsync);
        group.MapPost("/{id:guid}/tax-verification/propose", ProposeTaxVerificationAsync);
        group.MapPost("/{id:guid}/tax-verification/confirm", ConfirmTaxVerifiedAsync).RequireAuthorization("department_admin");
        group.MapPost("/{id:guid}/tax-verification/reject", RejectTaxVerificationAsync).RequireAuthorization("department_admin");

        // R06 — explicit, human-recorded Contract -> Invoice/Receipt link.
        // Department-scoped; the repository itself re-verifies the contract
        // (and installment, if given) resolve in the SAME tenant+department
        // as the voucher, so this can never cross a tenant/department
        // boundary even if the caller supplies a foreign id.
        group.MapPatch("/{id:guid}/link-contract", LinkContractAsync);

        // Chứng từ THU / CHI -> ADMIN duyệt -> tài khoản tiền -> đối trừ
        // AR/AP. apply-receipt / apply-expense(payment|bank_transfer) /
        // unapply-receipt / unapply-payment above are kept as compatibility
        // entry points and delegate to the same service.
        group.MapCashSettlement();

        // Bank statement lines -> Customer identification (no posting).
        group.MapBankStatementLines();

        // Voucher type: ADMIN confirms the detected type or corrects it
        // (persisted as voucher_type_source = admin_corrected). Refused once
        // the voucher has been posted.
        group.MapPost("/{id:guid}/voucher-type", SetVoucherTypeAsync).RequireAuthorization("department_admin");

        // Document workflow review (require_review). Reading is department
        // scoped; deciding is the department's own ADMIN only — USER,
        // TENANT_OWNER and SUPER_ADMIN fail the department_admin policy.
        group.MapGet("/{id:guid}/document-review", ListDocumentReviewsAsync);
        // Safe processing state for the detail page: extraction failure
        // (code/stage/time only — never provider text) + document blockers.
        group.MapGet("/{id:guid}/processing-state", GetProcessingStateAsync);
        group.MapPost("/{id:guid}/document-review/{taskId:guid}/decision", DecideDocumentReviewAsync).RequireAuthorization("department_admin");

        return app;
    }

    /// <summary>'agent' when the Agent's tool says so — a label for the audit trail only, never an authority.</summary>
    private static string ActorType(HttpContext ctx) =>
        string.Equals(ctx.Request.Headers["X-AIQ-Actor"].ToString(), "agent", StringComparison.OrdinalIgnoreCase) ? "agent" : "user";

    /// <summary>Role from the authenticated principal (TryScope admits only admin/user here).</summary>
    private static string ActorRole(PrincipalContext p) => p.IsInRole("admin") ? "admin" : "user";

    private static IResult TaxProblem(TaxVerificationException ex) => ex.Code switch
    {
        "verified_requires_admin" or "admin_required" => Results.Json(new { error = ex.Code, message = ex.Message, details = ex.Details }, statusCode: StatusCodes.Status403Forbidden),
        "not_verifiable" or "evidence_mismatch" => Results.Conflict(new { error = ex.Code, message = ex.Message, details = ex.Details }),
        _ => Results.BadRequest(new { error = ex.Code, message = ex.Message, details = ex.Details }),
    };

    private static async Task<IResult> GetTaxVerificationAsync(Guid id, ITaxVerificationRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        var view = await repo.GetAsync(tenantId, departmentId, id, ct);
        return view is null ? Results.NotFound() : Results.Ok(new
        {
            verification = view,
            // ADMIN Department Authority Invariant: any same-department ADMIN —
            // including the claim's own creator — may confirm.
            canConfirm = principal.IsInRole("admin"),
        });
    }

    private sealed record ProposeTaxVerificationRequest(string? Reason, string? Recommendation, System.Text.Json.JsonElement? Evidence);

    private static async Task<IResult> ProposeTaxVerificationAsync(Guid id, ProposeTaxVerificationRequest? request, ITaxVerificationRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        try
        {
            var view = await repo.ProposeAsync(tenantId, departmentId, id, principal.UserId, ActorType(ctx), ActorRole(principal),
                request?.Recommendation, request?.Evidence is { } e ? e.GetRawText() : null, request?.Reason, ct);
            return view is null ? Results.NotFound() : Results.Ok(view);
        }
        catch (TaxVerificationException ex) { return TaxProblem(ex); }
    }

    private static async Task<IResult> ConfirmTaxVerifiedAsync(Guid id, TaxConfirmRequest? request, ITaxVerificationRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        // The legacy Agent tool posted with no body at all: a clean 400, never a 500.
        if (request is null)
            return Results.BadRequest(new { error = "evidence_required", message = "Cần bằng chứng xác minh (nguồn, ghi chú, hoá đơn đã đối chiếu)." });
        try
        {
            var view = await repo.ConfirmAsync(tenantId, departmentId, id, principal.UserId, request, ct);
            return view is null ? Results.NotFound() : Results.Ok(view);
        }
        catch (TaxVerificationException ex) { return TaxProblem(ex); }
    }

    private static async Task<IResult> RejectTaxVerificationAsync(Guid id, TaxRejectRequest? request, ITaxVerificationRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        if (request is null)
            return Results.BadRequest(new { error = "evidence_reason_required", message = "Cần lý do/bằng chứng." });
        try
        {
            var view = await repo.RejectAsync(tenantId, departmentId, id, principal.UserId, ActorType(ctx), ActorRole(principal), request, ct);
            return view is null ? Results.NotFound() : Results.Ok(view);
        }
        catch (TaxVerificationException ex) { return TaxProblem(ex); }
    }

    private static async Task<IResult> GetExpenseClaimPaymentAsync(Guid id, Aiq.Gateway.Payments.IExpenseClaimPayment payments, HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        var context = await payments.GetExpenseClaimPaymentAsync(tenantId, departmentId, id, principal.UserId, ct);
        return context is null ? Results.NotFound() : Results.Ok(new { context, canPay = principal.IsInRole("admin") });
    }

    private static async Task<IResult> PayExpenseClaimAsync(Guid id, Aiq.Gateway.Payments.ExpenseClaimPayRequest? request,
        Aiq.Gateway.Payments.IExpenseClaimPayment payments, HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        try
        {
            var result = await payments.PayExpenseClaimAsync(tenantId, departmentId, id, principal.UserId,
                request ?? new Aiq.Gateway.Payments.ExpenseClaimPayRequest(null, null), ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (Aiq.Gateway.Payments.CashSettlementException ex)
        {
            return CashSettlementEndpoints.ToProblem(ex);
        }
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

    private static async Task<IResult> GetProcessingStateAsync(
        Guid id, IVoucherRepository repo, Aiq.Gateway.Vouchers.DocumentProcessing.IDocumentReviewRepository reviews,
        Aiq.Gateway.Vouchers.DocumentProcessing.IDocumentProcessingRepository documents, HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var tenantId, out var departmentId)) return Results.Forbid();
        if (await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct) is null) return Results.NotFound();
        var failure = await documents.GetExtractionFailureAsync(tenantId, id, ct);
        return Results.Ok(new
        {
            extractionError = failure is null ? null : new { code = failure.Code, stage = failure.Stage, failedAt = failure.FailedAt },
            reviewStatus = await reviews.GetGateStatusAsync(tenantId, id, ct),
            blockingReasons = await reviews.GetBlockingReasonsAsync(tenantId, id, ct),
        });
    }

    private static async Task<IResult> ListDocumentReviewsAsync(
        Guid id, Aiq.Gateway.Vouchers.DocumentProcessing.IDocumentReviewRepository reviews, HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var tenantId, out var departmentId)) return Results.Forbid();
        var items = await reviews.ListForVoucherAsync(tenantId, departmentId, id, ct);
        if (items is null) return Results.NotFound();
        return Results.Ok(new { reviewStatus = await reviews.GetGateStatusAsync(tenantId, id, ct), items, total = items.Count });
    }

    internal static async Task<IResult> DecideDocumentReviewAsync(
        Guid id, Guid taskId, Aiq.Gateway.Vouchers.DocumentProcessing.DocumentReviewDecisionRequest request,
        Aiq.Gateway.Vouchers.DocumentProcessing.IDocumentReviewRepository reviews, IAuditLogger audit, HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        if (!principal.IsInRole("admin")) return Results.Forbid();
        var decision = request.Decision?.Trim().ToLowerInvariant();
        if (decision is not (Aiq.Gateway.Vouchers.DocumentProcessing.DocumentReviewStatuses.Approved or Aiq.Gateway.Vouchers.DocumentProcessing.DocumentReviewStatuses.Rejected))
            return Results.BadRequest(new { error = "decision must be 'approved' or 'rejected'" });
        var decided = await reviews.DecideAsync(tenantId, departmentId, id, taskId, principal.UserId, decision, request.Note?.Trim(), ct);
        if (decided is null) return Results.NotFound(new { error = "review_task_not_found_or_not_open" });
        await audit.WriteAsync(new AuditEntry(principal.UserId, "POST document review decision", "voucher", id.ToString(),
            new { taskId, decision, tenantId, departmentId }, ctx.Connection.RemoteIpAddress, ctx.Request.Headers.UserAgent.ToString()), ct);
        return Results.Ok(new { task = decided, reviewStatus = await reviews.GetGateStatusAsync(tenantId, id, ct) });
    }

    internal sealed record ReextractRequest(string? RawText, string? VoucherType);

    private sealed record SetVoucherTypeRequest(string? VoucherType);

    private static async Task<IResult> SetVoucherTypeAsync(
        Guid id,
        SetVoucherTypeRequest request,
        IVoucherRepository repo,
        Aiq.Gateway.Jobs.IVoucherExtractJobScheduler extractScheduler,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        var chosen = Aiq.Gateway.Vouchers.VoucherTypes.Normalize(request.VoucherType);
        if (chosen is null) return Results.BadRequest(new { error = $"voucherType không hợp lệ: {request.VoucherType}" });

        var voucher = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        if (voucher is null) return Results.NotFound();

        var current = Aiq.Gateway.Vouchers.VoucherTypeClassification.FromExtractedFields(voucher.Voucher.ExtractedFieldsJson)
            ?? new Aiq.Gateway.Vouchers.VoucherTypeClassification { VoucherType = voucher.Voucher.VoucherType };
        var changed = !string.Equals(chosen, voucher.Voucher.VoucherType, StringComparison.OrdinalIgnoreCase);
        var updated = current with
        {
            VoucherType = chosen,
            Source = changed ? Aiq.Gateway.Vouchers.VoucherTypeSources.AdminCorrected : current.Source,
            Status = Aiq.Gateway.Vouchers.VoucherTypeStatuses.Ok,
            RequiresConfirmation = false,
            ConfirmedBy = principal.UserId,
            ConfirmedAt = DateTime.UtcNow,
            Evidence = current.Evidence.Append(changed
                ? $"ADMIN đổi loại: {Aiq.Gateway.Vouchers.VoucherTypes.Label(voucher.Voucher.VoucherType)} → {Aiq.Gateway.Vouchers.VoucherTypes.Label(chosen)}"
                : $"ADMIN xác nhận loại: {Aiq.Gateway.Vouchers.VoucherTypes.Label(chosen)}").ToList(),
        };
        var json = System.Text.Json.JsonSerializer.Serialize(updated, Aiq.Gateway.Vouchers.VoucherTypeClassification.Json);
        if (!await repo.SetVoucherTypeClassificationAsync(tenantId, departmentId, id, chosen, json, ct, reprocessing: changed))
        {
            return Results.Conflict(new { error = "voucher_already_posted", message = "Chứng từ đã ghi nhận — không thể đổi loại. Hãy huỷ ghi nhận trước." });
        }

        // A different type means different enrichment (AR vs AP context,
        // matching, journal proposal): re-run extraction with the type FORCED.
        string? extractJobId = null;
        if (changed)
        {
            // The job loads the row creator-scoped: pass the CREATOR's id
            // (the ADMIN's own token still authenticates the agent call).
            extractJobId = extractScheduler.EnqueueImmediate(id, voucher.Voucher.UserId, tenantId,
                Aiq.Gateway.Vouchers.VoucherTypeRequest.ForAdmin(chosen), ExtractBearerToken(ctx), requestedBy: principal.UserId);
        }
        return Results.Ok(new { voucherId = id, voucherType = chosen, changed, classification = updated, extractJobId });
    }

    private static async Task<IResult> ApplyReceiptAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Payments.ICashSettlementRepository settlement,
        Aiq.Gateway.Receipts.IReceiptMatchRepository receiptMatches,
        HttpContext ctx,
        CancellationToken ct)
    {
        // Compatibility entry point for "Chấp nhận thu": delegates to the
        // cash-settlement service (one transaction: cash journal + payment
        // applications + open items + current_ar). Only a CERTAIN match is
        // applied without an explicit ADMIN pick; otherwise 409
        // admin_selection_required and the UI shows the candidates.
        // ADMIN Department Authority Invariant (docs/ACL_V2_RESOURCE_VISIBILITY_RULES.md §15): the department ADMIN may approve a receipt they created
        // themselves — no self-approval block here.
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();

        var voucher = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        if (voucher is null) return Results.NotFound();
        if (voucher.Voucher.VoucherType != "receipt")
        {
            return Results.BadRequest(new { error = "apply-receipt chỉ áp dụng cho phiếu thu" });
        }

        Aiq.Gateway.Payments.SettlementMutationResult? result;
        try
        {
            result = await settlement.PostAsync(tenantId, departmentId, id, principal.UserId,
                new Aiq.Gateway.Payments.PostCashRequest(null, null, false, null), ct);
        }
        catch (Aiq.Gateway.Payments.CashSettlementException ex)
        {
            return CashSettlementEndpoints.ToProblem(ex);
        }
        if (result is null) return Results.NotFound();

        // Luồng 02 classification is metadata only — confirm it best-effort
        // AFTER the money has committed; it never gates or changes posting.
        object? receiptMatch = null;
        if (result.Outcome == "posted")
        {
            try
            {
                await receiptMatches.ProposeMatchAsync(tenantId, departmentId, id, voucher.Voucher.BuyerMst,
                    result.Posting?.Amount ?? voucher.Voucher.TotalAmount ?? 0m, voucher.Voucher.Currency, ct);
                receiptMatch = await receiptMatches.ConfirmMatchAsync(tenantId, departmentId, id, principal.UserId, ct);
            }
            catch (Exception) { /* classification is advisory */ }
        }

        return Results.Ok(new
        {
            voucherId = id,
            status = result.Outcome == "posted" ? "applied" : "already_applied",
            previous_balance = result.PreviousBalance,
            new_balance = result.NewBalance,
            total_applied = result.Posting?.AppliedAmount ?? 0m,
            unapplied_amount = result.Posting?.UnappliedAmount ?? 0m,
            discount_amount = 0m,
            ledger_rows_inserted = result.Outcome == "posted" ? result.Applications.Count : 0,
            posting = result.Posting,
            applications = result.Applications,
            journal = result.Journal,
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
        // Final ACL rule: the department ADMIN reviews its own receipts too.
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
    /// <summary>
    /// Multi-file claims (expense_claim_intakes): items are the only source of
    /// the claim's lines. requireResolved=false → "is this a multi-file
    /// claim?" (document-level re-proposal / re-extraction refused);
    /// requireResolved=true → the item gate before an approval stage.
    /// </summary>
    private static async Task<IResult?> ExpenseClaimItemGateAsync(HttpContext ctx, Guid voucherId, bool requireResolved, CancellationToken ct)
    {
        await using var conn = await ctx.RequestServices.GetRequiredService<Aiq.Gateway.Data.NpgsqlConnectionFactory>().OpenAsync(ct);
        if (!requireResolved)
            return await Aiq.Gateway.Employees.ExpenseClaimItemGate.IsMultiFileAsync(conn, voucherId, ct)
                ? Results.Conflict(new { error = "expense_claim_items_managed", message = "Đề nghị nhiều chứng từ: số liệu lấy từ từng khoản chi — dùng màn hình khoản chi để sửa/trích xuất lại." })
                : null;
        return await Aiq.Gateway.Employees.ExpenseClaimItemGate.ErrorAsync(conn, null, voucherId, ct) is { } error
            ? Results.Conflict(new { error, message = error == Aiq.Gateway.Employees.ExpenseClaimItemGate.Unresolved
                ? "Còn khoản chi đang xử lý / cần kiểm tra / lỗi — xử lý hoặc loại trước khi duyệt."
                : "Đề nghị chưa có khoản chi hợp lệ nào." })
            : null;
    }

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
        if (await ExpenseClaimItemGateAsync(ctx, id, requireResolved: false, ct) is { } managed) return managed;
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
        if (await ExpenseClaimItemGateAsync(ctx, id, requireResolved: true, ct) is { } itemsGate) return itemsGate;
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
        // "Duyệt kế toán" = accounting approval + authoritative expense recognition.
        try
        {
            var updated = await expenseClaims.AccountingReviewAsync(tenantId, departmentId, id, principal.UserId, ct);
            return updated is null
                ? Results.Conflict(new { error = "expense_claim_not_ready_for_accounting_review" })
                : Results.Ok(updated);
        }
        catch (Aiq.Gateway.Employees.ExpenseRecognitionException ex)
        {
            return Results.Conflict(new { error = ex.Code, message = ex.Message, details = ex.Details });
        }
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

    internal static ILogger DocumentGateLogger(HttpContext ctx) =>
        ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Aiq.Gateway.DocumentAccountingGate");

    private static async Task<IResult> ApplyInvoiceAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Customers.ICustomerRepository customers,
        Aiq.Gateway.Governance.ITenantCompanyProfileRepository companyProfiles,
        Aiq.Gateway.Vouchers.DocumentProcessing.IDocumentReviewRepository reviews,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();

        var voucher = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        if (voucher is null) return Results.NotFound();
        // Final voucher_type must be confirmed before any posting.
        if (Aiq.Gateway.Vouchers.VoucherTypeClassification.FromExtractedFields(voucher.Voucher.ExtractedFieldsJson) is { BlocksPosting: true })
        {
            return Results.Conflict(new { error = "voucher_type_unconfirmed", message = "Loại chứng từ chưa được xác nhận — ADMIN cần xác nhận loại đúng trước khi ghi nhận." });
        }
        // Canonical document-level gate (review + extraction-owned blockers).
        if (await reviews.GetBlockingReasonsAsync(tenantId, id, ct) is { Count: > 0 } blockingReasons)
        {
            Aiq.Gateway.Vouchers.DocumentProcessing.DocumentAccountingGate.LogBlocked(DocumentGateLogger(ctx), tenantId, departmentId,
                principal.UserId, id.ToString(), "apply_invoice", blockingReasons);
            return Results.Conflict(new { error = Aiq.Gateway.Vouchers.DocumentProcessing.DocumentReviewRequired.Code, reasons = blockingReasons,
                message = Aiq.Gateway.Vouchers.DocumentProcessing.DocumentReviewRequired.Message });
        }
        var vt = voucher.Voucher.VoucherType?.ToLowerInvariant() ?? "";
        if (vt is not ("invoice_vat_out" or "invoice_vat"))
        {
            return Results.BadRequest(new
            {
                error = "apply-invoice chỉ áp dụng cho invoice_vat_out (hoá đơn bán)",
            });
        }
        // ADMIN Department Authority Invariant: ADMIN has full operational
        // authority inside its own department, including approving/posting an
        // accounting record it uploaded/created/extracted/reviewed itself — no
        // second ADMIN, TENANT_OWNER, or SUPER_ADMIN required, and no
        // creator/previous-reviewer check anywhere. Same tenant/department requirement
        // (FindInDepartmentAsync above) and ADMIN-only posting
        // (RequireAuthorization("department_admin") on the route) are
        // both independent of this check and remain fully enforced.
        if (voucher.Voucher.Status == "verified")
        {
            return Results.Ok(new { voucherId = id, status = "already_applied", message = "Đã áp trước đó." });
        }

        // Deterministic direction gate — never trust voucher_type alone (it
        // is set by the LLM extraction pipeline from document text), and
        // never trust a client-supplied company identity either. Resolve
        // from the voucher's own seller/buyer MST against the tenant's
        // server-side, TENANT_OWNER-configured Company Profile
        // (tenant_company_profiles — the trusted "công ty chủ thể" identity,
        // not the legacy single-field tenants.company_tax_id); block the
        // apply rather than silently booking AR under a possibly-wrong
        // classification.
        var companyProfile = await companyProfiles.GetAsync(tenantId, ct);
        var companyTaxId = companyProfile?.TaxCode;
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
            posting_status = "posted",
            // Booking an invoice only ever opens the receivable — it never
            // means cash arrived. Explicit in the response so no caller has
            // to infer this from ledger_rows_inserted; a receipt/payment
            // applied later is the only thing that can ever change this.
            payment_status = "unpaid",
            voucher_type = vt,
            previous_balance = result.PreviousBalance,
            new_balance = result.NewBalance,
            invoice_total = result.InvoiceTotal,
            ledger_rows_inserted = result.LedgerRowsInserted,
        });
    }

    private static Task<IResult> UnapplyReceiptAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Payments.ICashSettlementRepository settlement,
        HttpContext ctx,
        CancellationToken ct) =>
        ReversePostingAsync(id, repo, settlement, ctx, new[] { "receipt" }, "unapply-receipt chỉ áp dụng cho phiếu thu", ct);

    // Symmetric to UnapplyReceiptAsync, Purchase side.
    private static Task<IResult> UnapplyPaymentAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Payments.ICashSettlementRepository settlement,
        HttpContext ctx,
        CancellationToken ct) =>
        ReversePostingAsync(id, repo, settlement, ctx, new[] { "payment", "bank_transfer" }, "unapply-payment chỉ áp dụng cho payment / bank_transfer", ct);

    // Compatibility "Huỷ áp thu/chi": voids the whole posting through the
    // cash-settlement service — every application is reversed with a
    // reversing sub-ledger row + a reversing journal entry (nothing is
    // deleted), AR/AP and invoice settlement status are restored, and the
    // voucher returns to 'extracted' so it can be posted again.
    private static async Task<IResult> ReversePostingAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Payments.ICashSettlementRepository settlement,
        HttpContext ctx,
        string[] allowedTypes,
        string wrongTypeError,
        CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();

        var voucher = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        if (voucher is null) return Results.NotFound();
        if (!allowedTypes.Contains(voucher.Voucher.VoucherType?.ToLowerInvariant() ?? ""))
        {
            return Results.BadRequest(new { error = wrongTypeError });
        }

        var result = await settlement.ReverseAsync(tenantId, departmentId, id, principal.UserId, "unapply", ct);
        if (result is null) return Results.NotFound();
        if (result.Outcome == "nothing_to_reverse")
        {
            return Results.BadRequest(new { error = $"chứng từ chưa được ghi nhận (hiện: {voucher.Voucher.Status})" });
        }

        return Results.Ok(new
        {
            voucherId = id,
            status = "unapplied",
            previous_balance = result.PreviousBalance,
            new_balance = result.NewBalance,
            ledger_rows_deleted = 0,
            applications_reversed = result.Applications.Count(a => a.Status == "reversed"),
            posting = result.Posting,
            journal = result.Journal,
        });
    }

    private static async Task<IResult> ApplyExpenseAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Suppliers.ISupplierRepository suppliers,
        Aiq.Gateway.Payments.ICashSettlementRepository settlement,
        Aiq.Gateway.Employees.IExpenseClaimRepository expenseClaims,
        Aiq.Gateway.Governance.ITenantCompanyProfileRepository companyProfiles,
        Aiq.Gateway.Vouchers.DocumentProcessing.IDocumentReviewRepository reviews,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();

        var voucher = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        if (voucher is null) return Results.NotFound();
        // Final voucher_type must be confirmed before any posting.
        if (Aiq.Gateway.Vouchers.VoucherTypeClassification.FromExtractedFields(voucher.Voucher.ExtractedFieldsJson) is { BlocksPosting: true })
        {
            return Results.Conflict(new { error = "voucher_type_unconfirmed", message = "Loại chứng từ chưa được xác nhận — ADMIN cần xác nhận loại đúng trước khi ghi nhận." });
        }
        // Canonical document-level gate (review + extraction-owned blockers).
        if (await reviews.GetBlockingReasonsAsync(tenantId, id, ct) is { Count: > 0 } blockingReasons)
        {
            Aiq.Gateway.Vouchers.DocumentProcessing.DocumentAccountingGate.LogBlocked(DocumentGateLogger(ctx), tenantId, departmentId,
                principal.UserId, id.ToString(), "apply_expense", blockingReasons);
            return Results.Conflict(new { error = Aiq.Gateway.Vouchers.DocumentProcessing.DocumentReviewRequired.Code, reasons = blockingReasons,
                message = Aiq.Gateway.Vouchers.DocumentProcessing.DocumentReviewRequired.Message });
        }
        var vt = voucher.Voucher.VoucherType?.ToLowerInvariant() ?? "";
        if (vt is not ("invoice_vat_in" or "payment" or "bank_transfer" or "expense_claim"))
        {
            return Results.BadRequest(new
            {
                error = "apply-expense chỉ áp dụng cho invoice_vat_in / payment / bank_transfer / expense_claim",
            });
        }
        // ADMIN Department Authority Invariant (docs/ACL_V2_RESOURCE_VISIBILITY_RULES.md §15): the same-department ADMIN completes
        // the normal workflow — including an expense claim, purchase invoice
        // or payment they created themselves. No second ADMIN, TENANT_OWNER or
        // SUPER_ADMIN is required (USER is refused by the route policy).
        if (voucher.Voucher.Status == "verified")
        {
            return Results.Ok(new { voucherId = id, status = "already_applied", message = "Đã áp trước đó." });
        }

        // Same deterministic direction gate as ApplyInvoiceAsync — only for
        // invoice_vat_in (payment/bank_transfer settle an AP that already
        // passed this gate when its invoice was booked; expense_claim is a
        // different domain, no seller/buyer MST pair at all). Same Company
        // Profile source as ApplyInvoiceAsync — never a client-supplied
        // company identity.
        if (vt == "invoice_vat_in")
        {
            var companyProfile = await companyProfiles.GetAsync(tenantId, ct);
            var companyTaxId = companyProfile?.TaxCode;
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
                    posting_status = "posted",
                    // Booking a supplier invoice only ever opens the
                    // payable — it never means cash was paid. A payment
                    // applied later (a separate flow) is the only thing
                    // that can ever change this.
                    payment_status = "unpaid",
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
                // Delegates to the cash-settlement service: Dr 331 / Cr 111x|112x,
                // payment applications against the SELECTED open AP items only
                // (a certain match, else 409 admin_selection_required), AP
                // recomputed — all in one transaction, idempotent per voucher.
                Aiq.Gateway.Payments.SettlementMutationResult? result;
                try
                {
                    result = await settlement.PostAsync(tenantId, departmentId, id, principal.UserId,
                        new Aiq.Gateway.Payments.PostCashRequest(null, null, false, null), ct);
                }
                catch (Aiq.Gateway.Payments.CashSettlementException ex)
                {
                    return CashSettlementEndpoints.ToProblem(ex);
                }
                if (result is null) return Results.NotFound();
                // Service already moved the voucher to 'verified' in the same transaction.
                return Results.Ok(new
                {
                    voucherId = id,
                    status = result.Outcome == "posted" ? "applied" : "already_applied",
                    voucher_type = vt,
                    previous_balance = result.PreviousBalance,
                    new_balance = result.NewBalance,
                    total_applied = result.Posting?.AppliedAmount ?? 0m,
                    unapplied_amount = result.Posting?.UnappliedAmount ?? 0m,
                    discount_amount = 0m,
                    ledger_rows_inserted = result.Outcome == "posted" ? result.Applications.Count : 0,
                    posting = result.Posting,
                    applications = result.Applications,
                    journal = result.Journal,
                });
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
                // Hardened gate: an ADMIN attestation (with evidence) of exactly
                // the claim's CURRENT invoices — a proposal, an unattested flag
                // or a verification of since-edited data does not pass.
                if (TaxVerificationRules.ExpenseGateError(voucher.Voucher, existing.RequiresTaxVerification) is { } taxGate)
                {
                    return Results.Conflict(new
                    {
                        error = taxGate,
                        message = "Chứng từ chưa được xác minh thuế.",
                        taxVerificationStatus = voucher.Voucher.TaxVerificationStatus,
                        reason = voucher.Voucher.TaxVerificationReason,
                    });
                }

                // Cash due to the employee: approval and payment are one action.
                if (existing.PayableAmount > 0m)
                {
                    return Results.Conflict(new
                    {
                        error = "payment_source_required",
                        message = "Đề nghị có khoản chi cho nhân viên — dùng \"Duyệt & chi tiền\" và chọn nguồn chi.",
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

    internal static async Task<IResult> ReextractAsync(
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
        // A posted voucher is never re-extracted — not even its raw text (P2).
        if (VoucherPostingGuard.IsPostedStatus(voucher.Voucher.Status)) return VoucherPostingGuard.Conflict();
        if (await ExpenseClaimItemGateAsync(ctx, id, requireResolved: false, ct) is { } managed) return managed;
        // The background job would only fail on the frozen row: refuse up front.
        if (voucher.Voucher.VoucherType == "expense_claim"
            && await ctx.RequestServices.GetRequiredService<Aiq.Gateway.Employees.IExpenseClaimRepository>()
                   .GetByVoucherIdAsync(tenantId, departmentId, id, ct) is { RecognitionStatus: "recognized" })
        {
            return Results.Conflict(new
            {
                error = "expense_claim_recognized",
                message = "Đề nghị đã được ghi nhận kế toán — không thể trích xuất lại (cần đảo ghi nhận trước).",
            });
        }

        // If caller provided raw text (paste-fallback flow), stash it into
        // the voucher first so the extract job reads it from DB just like
        // OCR-populated rows.
        var newRawText = request?.RawText?.Trim() ?? "";
        if (newRawText.Length >= 20
            && !await repo.UpdateOcrResultAsync(
                id,
                status: "ocr_complete",
                rawText: newRawText,
                ocrConfidence: 1.0m,   // user-verified text is 100% by definition
                errorMessage: null,
                ct))
        {
            return VoucherPostingGuard.Conflict();
        }

        // Explicit type in the request = a user hint. Otherwise keep what the
        // previous run was asked: an ADMIN correction stays forced, an upload
        // hint stays a hint, auto stays auto.
        var previous = Aiq.Gateway.Vouchers.VoucherTypeClassification.FromExtractedFields(voucher.Voucher.ExtractedFieldsJson);
        var voucherType = !string.IsNullOrWhiteSpace(request?.VoucherType)
            ? request!.VoucherType!.Trim()
            : previous?.Source == Aiq.Gateway.Vouchers.VoucherTypeSources.AdminCorrected
                ? Aiq.Gateway.Vouchers.VoucherTypeRequest.ForAdmin(previous.VoucherType)
                : previous is not null
                    ? previous.UploadedAs ?? Aiq.Gateway.Vouchers.VoucherTypes.Auto
                    : voucher.Voucher.VoucherType;
        if (!string.IsNullOrWhiteSpace(request?.VoucherType)
            && Aiq.Gateway.Vouchers.VoucherTypeRequest.Parse(voucherType).Forced is not null)
        {
            return Results.BadRequest(new { error = "voucherType không hợp lệ" });
        }
        var userToken = ExtractBearerToken(ctx);
        // Creator id: the job loads the row creator-scoped (an ADMIN re-running
        // someone else's voucher must not silently no-op).
        // The creator scopes the row; THIS principal is the run actor.
        var jobId = extractScheduler.EnqueueImmediate(id, voucher.Voucher.UserId, tenantId, voucherType, userToken, requestedBy: userId);

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
        Aiq.Gateway.Vouchers.DocumentProcessing.IDocumentProcessingRepository documents,
        HttpContext ctx,
        CancellationToken ct,
        string voucherType = Aiq.Gateway.Vouchers.VoucherTypes.Auto,
        Guid? preferredAgentId = null)
    {
        // "auto" (default) or a canonical type used only as a HINT — the
        // extract job decides the final type from OCR evidence + extractor
        // (Aiq.Gateway.Vouchers.VoucherTypeClassifier). "admin:" is reserved
        // for ADMIN corrections and is never accepted from upload.
        var typeRequest = Aiq.Gateway.Vouchers.VoucherTypeRequest.Parse(voucherType);
        var isAuto = string.Equals(voucherType?.Trim(), Aiq.Gateway.Vouchers.VoucherTypes.Auto, StringComparison.OrdinalIgnoreCase);
        if (typeRequest.Forced is not null || (!isAuto && typeRequest.Hint is null))
        {
            return Results.BadRequest(new { error = $"voucherType không hợp lệ: {voucherType}" });
        }
        voucherType = typeRequest.Hint ?? Aiq.Gateway.Vouchers.VoucherTypes.Auto;
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
        // Bank statement exports (CSV / Excel) are parsed structurally, not OCR'd.
        var isSheet = file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            || file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);
        if (!isImage && !isPdf && !isSheet)
        {
            return Results.BadRequest(new { error = $"content-type '{contentType}' không hỗ trợ. Dùng image/*, application/pdf, .csv hoặc .xlsx." });
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
            // Until detection finishes the row is 'unclassified' (auto) or the hint.
            typeRequest.Hint ?? Aiq.Gateway.Vouchers.VoucherTypes.Unclassified,
            s3Key,
            file.FileName,
            contentType,
            ct);

        // "Xử lý bằng trợ lý": recorded as a HINT only — the extract job
        // re-authorizes it through the uploader's own capability chain
        // (ICapabilityRepository.ResolveAgentAsync) and ignores it if the
        // uploader isn't eligible for that agent.
        if (preferredAgentId is { } requestedAgent)
            await documents.SetRequestedAgentAsync(tenantId, voucherId, requestedAgent, ct);

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

    internal static async Task<IResult> ValidationAsync(
        Guid id,
        Aiq.Gateway.Vouchers.Validation.VoucherValidationService validation,
        HttpContext ctx,
        CancellationToken ct)
    {
        // A read for every member of the voucher's department (USER and ADMIN).
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var tenantId, out var departmentId)) return Results.Forbid();
        var result = await validation.ValidateAsync(tenantId, departmentId, id, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    internal static async Task<IResult> CreateAsync(
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
        if (created is not null) return Results.Created($"/vouchers/{created.Voucher.Id}", created);

        // Same (MST, số, ngày) already exists: never overwrite it (P3). Its id
        // is disclosed only inside the caller's own department.
        var existing = await repo.FindDuplicateAsync(tenantId, request.SellerMst!, request.DocumentNumber!, excludeId: Guid.Empty, ct);
        const string message = "Chứng từ trùng (MST, số, ngày) đã tồn tại — không ghi đè.";
        return existing?.DepartmentId == departmentId
            ? Results.Conflict(new { error = "duplicate_voucher", message, existingVoucherId = existing.Id })
            : Results.Conflict(new { error = "duplicate_voucher", message });
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        IVoucherRepository repo,
        Aiq.Gateway.Governance.ITenantCompanyProfileRepository companyProfiles,
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
        // Must read the exact same Company Profile source apply-invoice/
        // apply-expense use below — otherwise the preview shown here could
        // disagree with what approval actually decides.
        var vt = voucher.Voucher.VoucherType?.ToLowerInvariant() ?? "";
        object? resolvedDirection = null;
        if (vt is "invoice_vat_out" or "invoice_vat_in" or "invoice_vat")
        {
            var companyProfile = await companyProfiles.GetAsync(tenantId, ct);
            var companyTaxId = companyProfile?.TaxCode;
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

    internal static async Task<IResult> ApplyExtractionAsync(
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
        // Same creator-scoped-USER / same-department-ADMIN rule as reextract.
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();

        var existing = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        if (existing is null)
        {
            return Results.NotFound();
        }
        if (!principal.IsInRole("admin") && existing.Voucher.UserId != principal.UserId)
        {
            return Results.Forbid();
        }
        // P1: an extraction never overwrites a posted voucher.
        if (VoucherPostingGuard.IsPostedStatus(existing.Voucher.Status)) return VoucherPostingGuard.Conflict();

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

        // The repository re-checks the posting guard in the UPDATE itself, so a
        // posting that lands between the check above and this write still wins.
        if (!await repo.ReplaceExtractedAsync(tenantId, existing.Voucher.UserId, id, request, ct))
        {
            return VoucherPostingGuard.Conflict();
        }
        var updated = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    internal static async Task<IResult> DeleteAsync(
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
        // P5: a posted voucher (invoice AR/AP included) is reversed, never deleted.
        if (VoucherPostingGuard.IsPostedStatus(existing.Voucher.Status)) return VoucherPostingGuard.Conflict();

        // A receipt/payment that ever carried an accounting effect (posted or
        // reversed) is audit history — cash_transactions references it with
        // ON DELETE RESTRICT. Refuse explicitly instead of surfacing a 500.
        try
        {
            var deleted = await repo.DeleteAsync(tenantId, existing.Voucher.UserId, id, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.ForeignKeyViolation)
        {
            return Results.Conflict(new { error = "voucher_has_accounting_history", message = "Chứng từ đã phát sinh bút toán thu/chi — không thể xoá. Hãy huỷ ghi nhận (đảo bút toán) thay vì xoá." });
        }
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
