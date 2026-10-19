using Aiq.Gateway.Data;
using Aiq.Gateway.Suppliers;
using Aiq.Gateway.Vouchers;
using Dapper;

namespace Aiq.Gateway.Employees;

// ============================================================================
// Luồng 03 — Hóa đơn thanh toán (expense claim outcome engine). Classifies a
// `voucher_type=expense_claim` into one of 5 canonical outcomes (APPROVED /
// PARTIALLY_APPROVED / RETURN_FOR_DOCUMENTS / WAITING_EXCEPTION_APPROVAL /
// REJECTED), reconciles the employee advance (either direction), detects
// duplicate line items against real Voucher history, and evaluates
// *configured* policy limits (department_expense_policies — empty by
// default, never a hardcoded number). This is a proposal-only classifier:
// ProposeAsync never touches sim_employees or sim_ap_ledger — only
// ConfirmAsync/GrantExceptionAsync (ADMIN-only, called from
// VoucherEndpoints.ApplyExpenseAsync) settle the real advance/ytd_expense
// balances via IEmployeeRepository.ApplyExpenseClaimAsync.
// ============================================================================

public sealed record ExpenseClaimItemInput(
    int Seq,
    string Category,
    string? Description,
    decimal Amount,
    bool? ReceiptAttached,
    string? VendorMst,
    string? InvoiceNo,
    DateOnly? DocumentDate,
    // R15 — VAT portion of Amount, if the source document/extraction
    // identified one. Null/0 when not present — never fabricated as a
    // percentage guess of Amount.
    decimal? VatAmount = null);

public sealed record ExpenseClaimProposalInput(
    string? EmployeeCode,
    decimal? TotalClaim,
    decimal AdvanceTaken,
    string Currency,
    IReadOnlyList<ExpenseClaimItemInput> Items);

public sealed record ExpenseClaimResult(
    Guid Id,
    Guid VoucherId,
    Guid? EmployeeId,
    decimal RequestedAmount,
    decimal ApprovedAmount,
    decimal BlockedAmount,
    decimal CappedAmount,
    decimal NonDeductibleAmount,
    // R15 — VAT identified across items (0 if the source never carried a
    // VAT breakdown) and the portion of it that actually qualifies for
    // input-VAT credit (items with a real invoice number only, prorated
    // down for a policy-capped line). Separate from NonDeductibleAmount
    // (CIT-deductibility) — a line can be VAT-deductible and CIT-non-
    // deductible, or vice versa; collapsing them into one flag was the
    // original defect this migration fixes.
    decimal VatAmount,
    decimal VatDeductibleAmount,
    decimal AdvanceAmount,
    decimal PayableAmount,
    string Currency,
    string DuplicateFlagsJson,
    string PolicyFlagsJson,
    string ResolvedVendorsJson,
    string? MissingDocuments,
    string Outcome,
    // P1 audit fix — true when at least one item on this claim carries a
    // real invoice_no (so there's an actual GTGT invoice a tax-portal check
    // applies to). Computed once at propose time; the payment-approval gate
    // in ConfirmAsync reads this instead of re-parsing extracted_fields.
    bool RequiresTaxVerification,
    // R14 — 3-stage sequence: proposed -> dept_reviewed -> accounting_reviewed
    // -> confirmed (or rejected from any of the first three). ReviewedBy/At
    // is the FINAL (payment-approval) actor+timestamp; DeptReviewedBy/At and
    // AccountingReviewedBy/At are the two earlier stages. The same
    // department ADMIN may perform every stage, including on a claim they
    // created (ADMIN Department Authority Invariant).
    string ReviewStatus,
    Guid? DeptReviewedBy,
    DateTime? DeptReviewedAt,
    Guid? AccountingReviewedBy,
    DateTime? AccountingReviewedAt,
    Guid? ReviewedBy,
    DateTime? ReviewedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    // Migration 058 — payment is separate from approval: unpaid / paid /
    // not_due, and the canonical cash posting that paid it.
    string PaymentStatus,
    Guid? PaidCashTransactionId,
    Guid? PaidBy,
    DateTime? PaidAt,
    // Migration 059 — authoritative expense recognition (accounting approval).
    string RecognitionStatus,
    Guid? RecognizedBy,
    DateTime? RecognizedAt);

public interface IExpenseClaimRepository
{
    // Deterministic outcome classification + persistence. Idempotent by
    // voucher_id — re-proposing (a manual "Trích xuất lại", or the standalone
    // propose endpoint) upserts the same row rather than creating a
    // duplicate, and never clobbers an already-confirmed (settled) row.
    Task<ExpenseClaimResult> ProposeAsync(
        Guid tenantId, Guid departmentId, Guid voucherId,
        ExpenseClaimProposalInput input, CancellationToken ct);

    Task<ExpenseClaimResult?> GetByVoucherIdAsync(Guid tenantId, Guid departmentId, Guid voucherId, CancellationToken ct);

    // R14 — 3 sequential stages, each requiring department ADMIN authority
    // (route policy department_admin). ADMIN Department Authority Invariant:
    // NO different-actor or not-the-creator rule — one ADMIN may run every
    // stage on a claim they created. Skipping a stage is impossible — each
    // method's own WHERE clause only matches the exact prior review_status.
    //
    // Stage 1 — "Bộ phận" (department review): claim reasonableness. Only
    // valid from review_status='proposed' and an approvable outcome
    // (APPROVED/PARTIALLY_APPROVED/WAITING_EXCEPTION_APPROVAL) — a claim
    // still needing documents or already rejected is not reviewable.
    Task<ExpenseClaimResult?> DepartmentReviewAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid reviewerId, CancellationToken ct);

    // Stage 2 — "Kế toán" (accounting review): tax/account validity. Only
    // valid from review_status='dept_reviewed'.
    Task<ExpenseClaimResult?> AccountingReviewAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid reviewerId, CancellationToken ct);

    // Stage 3 — "Duyệt chi" (payment approval): the only stage that ever
    // touches the ledger (sim_employees). Only valid from
    // review_status='accounting_reviewed'. Idempotent: already-confirmed returns the
    // existing row unchanged rather than re-applying.
    Task<ExpenseClaimResult?> ConfirmAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid reviewerId, CancellationToken ct);

    // ADMIN-only. Only valid when
    // outcome=WAITING_EXCEPTION_APPROVAL and review_status='proposed' —
    // grants the exception (recomputes approved_amount up to
    // requested_amount minus only genuinely blocked/duplicate lines,
    // ignoring the policy cap) but does NOT apply it — per the business
    // document ("...sau đó quay lại nhánh duyệt", the exception decision
    // returns the claim to the normal approval branch), it resets
    // review_status back to 'proposed' so the full 3-stage chain still
    // has to happen before anything touches the ledger.
    Task<ExpenseClaimResult?> GrantExceptionAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid reviewerId, CancellationToken ct);

    // ADMIN-only. Valid from any pre-settlement stage (proposed/
    // dept_reviewed/accounting_reviewed) — nothing to reject once
    // confirmed. No ledger effect; outcome forced to REJECTED, audit
    // preserved (row stays, not deleted).
    Task<ExpenseClaimResult?> RejectAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid reviewerId, CancellationToken ct);
}

public sealed class ExpenseClaimRepository : IExpenseClaimRepository
{
    private readonly NpgsqlConnectionFactory _factory;
    private readonly IEmployeeRepository _employees;
    private readonly ISupplierRepository _suppliers;
    private readonly IVoucherRepository _vouchers;
    private readonly Aiq.Gateway.Contracts.INotificationRepository _notifications;

    public ExpenseClaimRepository(
        NpgsqlConnectionFactory factory,
        IEmployeeRepository employees,
        ISupplierRepository suppliers,
        IVoucherRepository vouchers,
        Aiq.Gateway.Contracts.INotificationRepository notifications)
    {
        _factory = factory;
        _employees = employees;
        _suppliers = suppliers;
        _vouchers = vouchers;
        _notifications = notifications;
    }

    // R18 — best-effort notification, never lets a failure break the
    // caller's own (already-committed) business operation.
    private async Task NotifyCreatorAsync(Guid tenantId, Guid departmentId, Guid voucherId, string kind, string title, string? body, CancellationToken ct)
    {
        try
        {
            var voucher = await _vouchers.FindInDepartmentAsync(tenantId, departmentId, voucherId, ct);
            if (voucher is null) return;
            await _notifications.InsertManyAsync(
                new[] { new Aiq.Gateway.Contracts.InsertNotificationDto(tenantId, voucher.Voucher.UserId, kind, "voucher", voucherId, title, body) }, ct);
        }
        catch (Exception) { /* notification is best-effort */ }
    }

    private const string Cols = """
            id                     AS Id,
            voucher_id             AS VoucherId,
            employee_id            AS EmployeeId,
            requested_amount       AS RequestedAmount,
            approved_amount        AS ApprovedAmount,
            blocked_amount         AS BlockedAmount,
            capped_amount          AS CappedAmount,
            non_deductible_amount  AS NonDeductibleAmount,
            vat_amount              AS VatAmount,
            vat_deductible_amount   AS VatDeductibleAmount,
            advance_amount         AS AdvanceAmount,
            payable_amount         AS PayableAmount,
            currency               AS Currency,
            duplicate_flags::text  AS DuplicateFlagsJson,
            policy_flags::text     AS PolicyFlagsJson,
            resolved_vendors::text AS ResolvedVendorsJson,
            missing_documents      AS MissingDocuments,
            outcome                AS Outcome,
            requires_tax_verification AS RequiresTaxVerification,
            review_status          AS ReviewStatus,
            dept_reviewed_by        AS DeptReviewedBy,
            dept_reviewed_at        AS DeptReviewedAt,
            accounting_reviewed_by  AS AccountingReviewedBy,
            accounting_reviewed_at  AS AccountingReviewedAt,
            reviewed_by            AS ReviewedBy,
            reviewed_at            AS ReviewedAt,
            created_at             AS CreatedAt,
            updated_at             AS UpdatedAt,
            payment_status         AS PaymentStatus,
            paid_cash_transaction_id AS PaidCashTransactionId,
            paid_by                AS PaidBy,
            paid_at                AS PaidAt,
            recognition_status     AS RecognitionStatus,
            recognized_by          AS RecognizedBy,
            recognized_at          AS RecognizedAt
        """;

    public async Task<ExpenseClaimResult?> GetByVoucherIdAsync(Guid tenantId, Guid departmentId, Guid voucherId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<ExpenseClaimResult>(new CommandDefinition(
            $"SELECT {Cols} FROM expense_claim_results WHERE tenant_id=@tenantId AND department_id=@departmentId AND voucher_id=@voucherId",
            new { tenantId, departmentId, voucherId }, cancellationToken: ct));
    }

    // Most-specific-first: a category-matching row beats the department's
    // category=NULL wildcard. Only the first active match applies.
    // P2 audit fix — position (cấp bậc) is an optional extra dimension on
    // top of category: NULL matches every position, same wildcard
    // convention as category=NULL. Precedence prefers whichever row matches
    // MORE of (category, position) specifically, so a policy row that pins
    // both beats one that only pins category, which beats the department's
    // fully-generic default.
    private async Task<(decimal LimitAmount, string LimitUnit, bool AllowException, bool RequiresReceipt, string? DebitAccount)?> FindPolicyAsync(
        Npgsql.NpgsqlConnection conn, Guid tenantId, Guid departmentId, string category, string? position, CancellationToken ct)
    {
        return await conn.QueryFirstOrDefaultAsync<(decimal, string, bool, bool, string?)?>(new CommandDefinition("""
            SELECT limit_amount, limit_unit, allow_exception, requires_receipt, debit_account
            FROM department_expense_policies
            WHERE tenant_id=@tenantId AND department_id=@departmentId AND active
              AND (category = @category OR category IS NULL)
              AND (position = @position OR position IS NULL)
            ORDER BY (category IS NOT NULL)::int + (position IS NOT NULL)::int DESC
            LIMIT 1
            """, new { tenantId, departmentId, category, position }, cancellationToken: ct));
    }

    public async Task<ExpenseClaimResult> ProposeAsync(
        Guid tenantId, Guid departmentId, Guid voucherId,
        ExpenseClaimProposalInput input, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var requested = input.TotalClaim ?? input.Items.Sum(i => i.Amount);
        var currency = string.IsNullOrWhiteSpace(input.Currency) ? "VND" : input.Currency;

        // Identity must resolve before anything else — without it we cannot
        // reconcile the advance, so there is nothing safe to approve.
        var emp = string.IsNullOrWhiteSpace(input.EmployeeCode)
            ? null
            : await _employees.FindByCodeAsync(tenantId, input.EmployeeCode, ct);
        // P1 audit fix — a real invoice number is what makes tax-portal
        // verification applicable at all (per the business process
        // document's step 3); a claim of entirely no-invoice items never
        // needs it.
        var requiresTaxVerification = input.Items.Any(i => !string.IsNullOrWhiteSpace(i.InvoiceNo));

        if (emp is null)
        {
            return await UpsertAsync(conn, tenantId, departmentId, voucherId, employeeId: null,
                requested, approved: 0m, blocked: 0m, capped: 0m, nonDeductible: 0m, vat: 0m, vatDeductible: 0m,
                advance: 0m, payable: 0m, currency,
                duplicateFlags: new List<object>(), policyFlags: new List<object>(), resolvedVendors: new List<object>(),
                missingDocuments: string.IsNullOrWhiteSpace(input.EmployeeCode)
                    ? "Thiếu mã nhân viên trên đề nghị hoàn ứng."
                    : $"Không tìm thấy nhân viên mã {input.EmployeeCode} — kiểm tra lại mã nhân viên.",
                outcome: "RETURN_FOR_DOCUMENTS", requiresTaxVerification, ct);
        }

        decimal approvedTotal = 0m, blockedTotal = 0m, cappedTotal = 0m, nonDeductibleTotal = 0m;
        decimal vatTotal = 0m, vatDeductibleTotal = 0m;
        var duplicateFlags = new List<object>();
        var policyFlags = new List<object>();
        var resolvedVendors = new List<object>();
        var missingItems = new List<string>();
        var seenInClaim = new HashSet<(string Mst, string InvoiceNo)>();
        var anyExceptionAllowed = false;
        // J1 fix — tracked alongside the existing per-item loop so the
        // deterministic journal router (JournalRoutingRules.ForExpenseSettlement)
        // can propose the expense-recognition debit leg without a second
        // pass or re-querying policies.
        var approvedByCategory = new Dictionary<string, decimal>();
        var categoryDebitAccounts = new Dictionary<string, string>();
        // Per-category deductible VAT and un-capped values — persisted as the
        // recognition basis (the split exists nowhere else).
        var vatDeductibleByCategory = new Dictionary<string, decimal>();
        var uncappedByCategory = new Dictionary<string, decimal>();
        var vatUncappedByCategory = new Dictionary<string, decimal>();

        foreach (var item in input.Items)
        {
            var isDuplicate = false;

            // Supplier resolution (EXP-05) — canonical sim_suppliers lookup,
            // same as invoice_vat_in/payment enrichment. Best-effort: an
            // unresolved vendor never blocks the line, it just stays a free
            // -text vendor name for display.
            if (!string.IsNullOrWhiteSpace(item.VendorMst))
            {
                var supplier = await _suppliers.FindByMstAsync(tenantId, departmentId, item.VendorMst!, ct);
                resolvedVendors.Add(new
                {
                    seq = item.Seq,
                    vendorMst = item.VendorMst,
                    resolved = supplier is not null,
                    supplierId = supplier?.Id,
                    supplierName = supplier?.Name,
                });
            }

            if (!string.IsNullOrWhiteSpace(item.VendorMst) && !string.IsNullOrWhiteSpace(item.InvoiceNo))
            {
                var key = (item.VendorMst!, item.InvoiceNo!);
                if (!seenInClaim.Add(key))
                {
                    isDuplicate = true;
                    duplicateFlags.Add(new { seq = item.Seq, reason = "duplicate_within_claim", vendorMst = item.VendorMst, invoiceNo = item.InvoiceNo });
                }
                else
                {
                    var existingVoucher = await _vouchers.FindDuplicateAsync(tenantId, item.VendorMst!, item.InvoiceNo!, voucherId, ct);
                    if (existingVoucher is not null)
                    {
                        isDuplicate = true;
                        duplicateFlags.Add(new { seq = item.Seq, reason = "duplicate_of_existing_voucher", vendorMst = item.VendorMst, invoiceNo = item.InvoiceNo, existingVoucherId = existingVoucher.Id });
                    }
                }
            }

            if (isDuplicate)
            {
                blockedTotal += item.Amount;
                continue;
            }

            var itemApproved = item.Amount;
            var policy = await FindPolicyAsync(conn, tenantId, departmentId, item.Category, emp.Position, ct);
            if (policy is { } p)
            {
                if (p.LimitUnit == "per_item" && item.Amount > p.LimitAmount)
                {
                    var over = item.Amount - p.LimitAmount;
                    itemApproved = p.LimitAmount;
                    cappedTotal += over;
                    policyFlags.Add(new
                    {
                        seq = item.Seq, category = item.Category, rule = "POLICY_LIMIT_EXCEEDED",
                        limitAmount = p.LimitAmount, limitUnit = p.LimitUnit, claimedAmount = item.Amount,
                        allowException = p.AllowException,
                    });
                    if (p.AllowException) anyExceptionAllowed = true;
                }
                if (p.RequiresReceipt && item.ReceiptAttached == false)
                {
                    missingItems.Add($"{item.Category}: {item.Description ?? "(không có mô tả)"}");
                }
                if (!string.IsNullOrWhiteSpace(p.DebitAccount))
                {
                    categoryDebitAccounts[item.Category] = p.DebitAccount!;
                }
            }
            approvedByCategory[item.Category] = approvedByCategory.GetValueOrDefault(item.Category) + itemApproved;
            uncappedByCategory[item.Category] = uncappedByCategory.GetValueOrDefault(item.Category) + item.Amount;

            // R15 — VAT/CIT are separate flags, not one combined value.
            // vatTotal is the RAW VAT identified on the source document
            // (what the invoice actually says) — never prorated, since
            // policy capping is a company reimbursement decision, not a
            // fact about the document. Only the DEDUCTIBLE portion is
            // prorated: never claim input-VAT credit on the part of the
            // spend that was never recognized as a cost at all.
            var itemVat = item.VatAmount ?? 0m;
            vatTotal += itemVat;
            var itemVatAfterCap = item.Amount > 0m ? itemVat * (itemApproved / item.Amount) : 0m;

            if (string.IsNullOrWhiteSpace(item.InvoiceNo))
            {
                // EX.NO_INVOICE — spec §07: still reimbursable, just not
                // VAT/CIT-deductible. Never blocks approval by itself.
                nonDeductibleTotal += itemApproved;
            }
            else
            {
                // Has a real invoice — the (capped-prorated) VAT on this
                // line qualifies for input-VAT credit.
                vatDeductibleTotal += itemVatAfterCap;
                vatDeductibleByCategory[item.Category] = vatDeductibleByCategory.GetValueOrDefault(item.Category) + itemVatAfterCap;
                vatUncappedByCategory[item.Category] = vatUncappedByCategory.GetValueOrDefault(item.Category) + itemVat;
            }

            approvedTotal += itemApproved;
        }

        // Claim-level (per_claim) policy — evaluated against the total that
        // survived per-item duplicate/per_item-cap processing.
        var perClaimPolicy = await conn.QueryFirstOrDefaultAsync<(decimal LimitAmount, bool AllowException)?>(new CommandDefinition("""
            SELECT limit_amount, allow_exception FROM department_expense_policies
            WHERE tenant_id=@tenantId AND department_id=@departmentId AND active
              AND category IS NULL AND limit_unit='per_claim'
            LIMIT 1
            """, new { tenantId, departmentId }, cancellationToken: ct));
        if (perClaimPolicy is { } cp && approvedTotal > cp.LimitAmount)
        {
            var over = approvedTotal - cp.LimitAmount;
            // Prorate the deductible VAT down by the same ratio the claim
            // total was cut by — the excess was never recognized as a cost,
            // so its VAT can't be credited either.
            var claimCapRatio = approvedTotal > 0m ? cp.LimitAmount / approvedTotal : 0m;
            vatDeductibleTotal *= claimCapRatio;
            cappedTotal += over;
            approvedTotal = cp.LimitAmount;
            foreach (var cat in approvedByCategory.Keys.ToArray())
            {
                approvedByCategory[cat] *= claimCapRatio;
            }
            foreach (var cat in vatDeductibleByCategory.Keys.ToArray())
            {
                vatDeductibleByCategory[cat] *= claimCapRatio;
            }
            policyFlags.Add(new
            {
                seq = (int?)null, category = "*", rule = "POLICY_LIMIT_EXCEEDED",
                limitAmount = cp.LimitAmount, limitUnit = "per_claim", claimedAmount = requested,
                allowException = cp.AllowException,
            });
            if (cp.AllowException) anyExceptionAllowed = true;
        }

        // Advance reconciliation — signed payable: positive = company owes
        // employee, negative = employee owes the difference back. Capped at
        // the employee's real outstanding_advance — never applies more than
        // what is genuinely outstanding, regardless of what the document
        // claims was taken.
        var advanceApplied = Math.Min(Math.Max(0m, input.AdvanceTaken), emp.OutstandingAdvance);
        var payable = approvedTotal - advanceApplied;

        var allBlocked = input.Items.Count > 0 && blockedTotal >= requested && approvedTotal == 0m;
        var anyPolicyExceeded = policyFlags.Count > 0;

        string outcome;
        if (allBlocked)
        {
            outcome = "REJECTED";
        }
        else if (missingItems.Count > 0)
        {
            outcome = "RETURN_FOR_DOCUMENTS";
        }
        else if (anyPolicyExceeded && anyExceptionAllowed)
        {
            outcome = "WAITING_EXCEPTION_APPROVAL";
        }
        else if (anyPolicyExceeded || blockedTotal > 0m)
        {
            outcome = "PARTIALLY_APPROVED";
        }
        else
        {
            outcome = "APPROVED";
        }

        var missingDocs = missingItems.Count > 0
            ? "Thiếu chứng từ cho: " + string.Join("; ", missingItems)
            : null;

        var basis = ExpenseRecognition.BuildBasis(approvedByCategory, vatDeductibleByCategory, uncappedByCategory, vatUncappedByCategory,
            approvedTotal, vatDeductibleTotal);
        var result = await UpsertAsync(conn, tenantId, departmentId, voucherId, emp.Id,
            requested, approvedTotal, blockedTotal, cappedTotal, nonDeductibleTotal, vatTotal, vatDeductibleTotal,
            advanceApplied, payable, currency, duplicateFlags, policyFlags, resolvedVendors, missingDocs, outcome, requiresTaxVerification, ct,
            System.Text.Json.JsonSerializer.Serialize(basis));

        // J1 fix — deterministic journal-entry proposal from the outcome
        // just computed (never from an LLM). Only for outcomes that will
        // actually settle the ledger; NeedsReview/Blocked routing results
        // simply don't produce a proposal, leaving journal-suggestions
        // exactly as they were (no invented entry, no cleared entry).
        if (IsApprovableOutcome(outcome))
        {
            // Category amounts are VAT-inclusive (R15: VAT is a portion of the
            // item amount) — the router expects them NET of deductible VAT,
            // otherwise the VAT is debited twice and 334 over-credited.
            var routing = Aiq.Gateway.Accounting.JournalRoutingRules.ForExpenseSettlement(
                ExpenseRecognition.NetByCategory(basis), categoryDebitAccounts, basis.Sum(b => b.VatDeductible), advanceApplied, payable);
            if (routing.Outcome == Aiq.Gateway.Accounting.JournalRoutingOutcome.Proposed)
            {
                try
                {
                    await _vouchers.SaveJournalSuggestionsAsync(tenantId, departmentId, voucherId,
                        new Aiq.Gateway.Vouchers.SaveJournalSuggestionsRequest(
                            routing.Entries.Select(e => new Aiq.Gateway.Vouchers.SaveJournalSuggestionDto(
                                e.EntryNo, e.DebitAccount, e.CreditAccount, e.Amount, e.Description, Confidence: 1.0m)).ToList(),
                            ReplaceExisting: true),
                        ct);
                }
                catch (Exception) { /* proposal is best-effort — never breaks the already-committed classification */ }
            }
        }

        return result;
    }

    private async Task<ExpenseClaimResult> UpsertAsync(
        Npgsql.NpgsqlConnection conn, Guid tenantId, Guid departmentId, Guid voucherId, Guid? employeeId,
        decimal requested, decimal approved, decimal blocked, decimal capped, decimal nonDeductible,
        decimal vat, decimal vatDeductible,
        decimal advance, decimal payable, string currency,
        List<object> duplicateFlags, List<object> policyFlags, List<object> resolvedVendors, string? missingDocuments,
        string outcome, bool requiresTaxVerification, CancellationToken ct, string? recognitionBasisJson = null)
    {
        var duplicateFlagsJson = System.Text.Json.JsonSerializer.Serialize(duplicateFlags);
        var policyFlagsJson = System.Text.Json.JsonSerializer.Serialize(policyFlags);
        var resolvedVendorsJson = System.Text.Json.JsonSerializer.Serialize(resolvedVendors);

        // Guard excludes only 'confirmed' (settled — never silently
        // overwritten by a re-extraction). A re-propose resets review_status
        // back to 'proposed' so it goes through review again, mirroring the
        // Receipt/AR fix — never throws when blocked (returns the existing
        // confirmed row instead).
        var row = await conn.QueryFirstOrDefaultAsync<ExpenseClaimResult>(new CommandDefinition($"""
            INSERT INTO expense_claim_results (
                tenant_id, department_id, voucher_id, employee_id,
                requested_amount, approved_amount, blocked_amount, capped_amount, non_deductible_amount,
                vat_amount, vat_deductible_amount,
                advance_amount, payable_amount, currency,
                duplicate_flags, policy_flags, resolved_vendors, missing_documents, outcome, requires_tax_verification,
                recognition_basis
            ) VALUES (
                @tenantId, @departmentId, @voucherId, @employeeId,
                @requested, @approved, @blocked, @capped, @nonDeductible,
                @vat, @vatDeductible,
                @advance, @payable, @currency,
                @duplicateFlagsJson::jsonb, @policyFlagsJson::jsonb, @resolvedVendorsJson::jsonb, @missingDocuments, @outcome, @requiresTaxVerification,
                @recognitionBasisJson::jsonb
            )
            ON CONFLICT (voucher_id) DO UPDATE SET
                employee_id = EXCLUDED.employee_id,
                requested_amount = EXCLUDED.requested_amount,
                approved_amount = EXCLUDED.approved_amount,
                blocked_amount = EXCLUDED.blocked_amount,
                capped_amount = EXCLUDED.capped_amount,
                non_deductible_amount = EXCLUDED.non_deductible_amount,
                vat_amount = EXCLUDED.vat_amount,
                vat_deductible_amount = EXCLUDED.vat_deductible_amount,
                advance_amount = EXCLUDED.advance_amount,
                payable_amount = EXCLUDED.payable_amount,
                currency = EXCLUDED.currency,
                duplicate_flags = EXCLUDED.duplicate_flags,
                policy_flags = EXCLUDED.policy_flags,
                resolved_vendors = EXCLUDED.resolved_vendors,
                missing_documents = EXCLUDED.missing_documents,
                outcome = EXCLUDED.outcome,
                requires_tax_verification = EXCLUDED.requires_tax_verification,
                recognition_basis = EXCLUDED.recognition_basis,
                review_status = 'proposed',
                dept_reviewed_by = NULL,
                dept_reviewed_at = NULL,
                accounting_reviewed_by = NULL,
                accounting_reviewed_at = NULL,
                reviewed_by = NULL,
                reviewed_at = NULL,
                updated_at = NOW()
            WHERE expense_claim_results.review_status <> 'confirmed'
              AND expense_claim_results.recognition_status <> 'recognized'
            RETURNING {Cols};
            """,
            new
            {
                tenantId, departmentId, voucherId, employeeId,
                requested, approved, blocked, capped, nonDeductible,
                vat, vatDeductible,
                advance, payable, currency,
                duplicateFlagsJson, policyFlagsJson, resolvedVendorsJson, missingDocuments, outcome,
                requiresTaxVerification, recognitionBasisJson,
            }, cancellationToken: ct));

        if (row is not null)
        {
            // R18 — the two outcomes the business document flags as needing
            // someone to act right away: send the submitter back to fix
            // documents, or hold for an authorized exception decision.
            if (outcome == "RETURN_FOR_DOCUMENTS")
            {
                await NotifyCreatorAsync(tenantId, departmentId, voucherId, "expense_claim_returned",
                    "Đề nghị hoàn ứng cần bổ sung chứng từ", missingDocuments, ct);
            }
            else if (outcome == "WAITING_EXCEPTION_APPROVAL")
            {
                try
                {
                    await _notifications.NotifyDepartmentAdminsAsync(
                        tenantId, departmentId, "expense_claim_exception_pending", "voucher", voucherId,
                        $"Đề nghị hoàn ứng chờ duyệt ngoại lệ — {requested:N0} {currency}",
                        "Vượt định mức nhưng có lý do chính đáng — cần cấp có thẩm quyền quyết định.", ct);
                }
                catch (Exception) { /* notification is best-effort */ }
            }
            return row;
        }

        return await conn.QuerySingleAsync<ExpenseClaimResult>(new CommandDefinition(
            $"SELECT {Cols} FROM expense_claim_results WHERE tenant_id=@tenantId AND department_id=@departmentId AND voucher_id=@voucherId",
            new { tenantId, departmentId, voucherId }, cancellationToken: ct));
    }

    private static bool IsApprovableOutcome(string outcome) =>
        outcome is "APPROVED" or "PARTIALLY_APPROVED" or "WAITING_EXCEPTION_APPROVAL";

    public async Task<ExpenseClaimResult?> DepartmentReviewAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid reviewerId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var existing = await conn.QueryFirstOrDefaultAsync<ExpenseClaimResult>(new CommandDefinition(
            $"SELECT {Cols} FROM expense_claim_results WHERE tenant_id=@tenantId AND department_id=@departmentId AND voucher_id=@voucherId",
            new { tenantId, departmentId, voucherId }, cancellationToken: ct));
        if (existing is null) return null;
        if (existing.ReviewStatus == "dept_reviewed" && existing.DeptReviewedBy == reviewerId) return existing; // idempotent retry
        if (existing.ReviewStatus != "proposed" || !IsApprovableOutcome(existing.Outcome)) return null;

        return await conn.QuerySingleAsync<ExpenseClaimResult>(new CommandDefinition($"""
            UPDATE expense_claim_results SET
                review_status='dept_reviewed', dept_reviewed_by=@reviewerId, dept_reviewed_at=NOW(), updated_at=NOW()
            WHERE id=@id
            RETURNING {Cols};
            """, new { id = existing.Id, reviewerId }, cancellationToken: ct));
    }

    /// <summary>
    /// Stage 2 "Duyệt kế toán" = accounting approval + AUTHORITATIVE expense
    /// recognition (ExpenseRecognition), atomically. Any department ADMIN —
    /// including the stage-1 reviewer and the claimant (ADMIN Department
    /// Authority Invariant).
    /// Recognition blockers (tax not verified, missing account mapping,
    /// unsupported refund, ...) throw ExpenseRecognitionException — nothing
    /// is written then.
    /// </summary>
    public async Task<ExpenseClaimResult?> AccountingReviewAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid reviewerId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var existing = await conn.QueryFirstOrDefaultAsync<ExpenseClaimResult>(new CommandDefinition(
            $"SELECT {Cols} FROM expense_claim_results WHERE tenant_id=@tenantId AND department_id=@departmentId AND voucher_id=@voucherId",
            new { tenantId, departmentId, voucherId }, cancellationToken: ct));
        if (existing is null) return null;
        if (existing.ReviewStatus is "accounting_reviewed" or "confirmed" && existing.AccountingReviewedBy == reviewerId) return existing; // idempotent retry
        if (existing.ReviewStatus != "dept_reviewed") return null;

        await using var tx = await conn.BeginTransactionAsync(ct);
        var done = await ExpenseRecognition.RecognizeAsync(conn, tx, tenantId, departmentId, voucherId, reviewerId, ct);
        if (done is null) { await tx.RollbackAsync(ct); return null; }
        await tx.CommitAsync(ct);
        return await conn.QuerySingleAsync<ExpenseClaimResult>(new CommandDefinition(
            $"SELECT {Cols} FROM expense_claim_results WHERE id=@id", new { id = done.Value.ClaimId }, cancellationToken: ct));
    }

    public async Task<ExpenseClaimResult?> ConfirmAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid reviewerId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var existing = await conn.QueryFirstOrDefaultAsync<ExpenseClaimResult>(new CommandDefinition(
            $"SELECT {Cols} FROM expense_claim_results WHERE tenant_id=@tenantId AND department_id=@departmentId AND voucher_id=@voucherId FOR UPDATE",
            new { tenantId, departmentId, voucherId }, tx, cancellationToken: ct));
        if (existing is null) { await tx.RollbackAsync(ct); return null; }
        if (existing.ReviewStatus == "confirmed") { await tx.RollbackAsync(ct); return existing; }
        // Stage 3 only reachable after stage 1 + 2 — never skips the chain.
        if (existing.ReviewStatus != "accounting_reviewed") { await tx.RollbackAsync(ct); return null; }
        // Cash is due to the employee: approval and payment are ONE action
        // (CashSettlementRepository.PayExpenseClaimAsync) — never approve
        // without paying.
        if (existing.PayableAmount > 0m)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        // The expense (and the employee advance / YTD) was recognized at stage 2;
        // the final no-cash approval only closes the chain — never re-applies it.
        if (existing.RecognitionStatus != ExpenseRecognition.Recognized)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        var updated = await conn.QuerySingleAsync<ExpenseClaimResult>(new CommandDefinition($"""
            UPDATE expense_claim_results SET
                review_status='confirmed', reviewed_by=@reviewerId, reviewed_at=NOW(), updated_at=NOW(),
                payment_status='not_due'
            WHERE id=@id
            RETURNING {Cols};
            """, new { id = existing.Id, reviewerId }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return updated;
    }

    public async Task<ExpenseClaimResult?> GrantExceptionAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid reviewerId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var existing = await conn.QueryFirstOrDefaultAsync<ExpenseClaimResult>(new CommandDefinition(
            $"SELECT {Cols} FROM expense_claim_results WHERE tenant_id=@tenantId AND department_id=@departmentId AND voucher_id=@voucherId",
            new { tenantId, departmentId, voucherId }, cancellationToken: ct));
        if (existing is null) return null;
        if (existing.ReviewStatus != "proposed" || existing.Outcome != "WAITING_EXCEPTION_APPROVAL") return null;

        // Grant: un-cap — approve the full requested amount minus only the
        // genuinely blocked (duplicate) lines, ignoring the policy cap.
        // Per the business document ("...sau đó quay lại nhánh duyệt"), this
        // does NOT apply to the ledger and does NOT confirm — it resets to
        // 'proposed' so the granted claim still goes through the full
        // dept-review -> accounting-review -> payment-approval chain,
        // exactly like any other approved claim. The exception-granter can
        // therefore never also be the one who gives final payment approval
        // on their own grant.
        var newApproved = existing.RequestedAmount - existing.BlockedAmount;
        var newPayable = newApproved - existing.AdvanceAmount;
        var newOutcome = existing.BlockedAmount > 0m ? "PARTIALLY_APPROVED" : "APPROVED";
        // The recognition basis follows the grant: un-capped per category
        // (and the deductible VAT on it), so recognition stays consistent.
        var basisJson = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT recognition_basis::text FROM expense_claim_results WHERE id=@id", new { id = existing.Id }, cancellationToken: ct));
        var uncapped = ExpenseRecognition.Uncap(ExpenseRecognition.ParseBasis(basisJson));
        var newVatDeductible = uncapped?.Sum(b => b.VatDeductible);

        return await conn.QuerySingleAsync<ExpenseClaimResult>(new CommandDefinition($"""
            UPDATE expense_claim_results SET
                approved_amount=@newApproved, capped_amount=0, payable_amount=@newPayable,
                vat_deductible_amount=COALESCE(@newVatDeductible, vat_deductible_amount),
                recognition_basis=@newBasis::jsonb,
                outcome=@newOutcome, review_status='proposed', updated_at=NOW()
            WHERE id=@id
            RETURNING {Cols};
            """, new { id = existing.Id, newApproved, newPayable, newOutcome, newVatDeductible,
                       newBasis = uncapped is null ? null : System.Text.Json.JsonSerializer.Serialize(uncapped) }, cancellationToken: ct));
    }

    public async Task<ExpenseClaimResult?> RejectAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid reviewerId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var updated = await conn.QueryFirstOrDefaultAsync<ExpenseClaimResult>(new CommandDefinition($"""
            UPDATE expense_claim_results SET
                outcome='REJECTED', review_status='rejected', reviewed_by=@reviewerId, reviewed_at=NOW(), updated_at=NOW()
            WHERE tenant_id=@tenantId AND department_id=@departmentId AND voucher_id=@voucherId
              AND review_status IN ('proposed','dept_reviewed','accounting_reviewed')
            RETURNING {Cols};
            """, new { tenantId, departmentId, voucherId, reviewerId }, cancellationToken: ct));

        if (updated is not null)
        {
            await NotifyCreatorAsync(tenantId, departmentId, voucherId, "expense_claim_rejected",
                "Đề nghị hoàn ứng đã bị từ chối", null, ct);
        }
        return updated;
    }
}
