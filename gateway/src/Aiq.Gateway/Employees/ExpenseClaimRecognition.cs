using System.Text.Json;
using Aiq.Gateway.Accounting;
using Aiq.Gateway.Payments;
using Aiq.Gateway.Vouchers;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Employees;

/// <summary>One category of the deterministic proposal: gross approved (VAT-inclusive), its deductible VAT, and the un-capped values.</summary>
public sealed record RecognitionBasisLine(string Category, decimal Gross, decimal VatDeductible, decimal GrossUncapped, decimal VatUncapped);

public sealed record RecognitionBlocker(string Code, string Message);

/// <summary>What recognition posts (or would post): every line from trusted rules/config only.</summary>
public sealed record RecognitionPlan(
    IReadOnlyList<RecognitionBlocker> Blockers, IReadOnlyList<JournalLine> Lines,
    decimal ExpenseAmount, decimal VatDeductible, decimal AdvanceCleared, decimal EmployeePayable,
    IReadOnlyDictionary<string, string> CategoryAccounts);

public sealed class ExpenseRecognitionException(string code, string message, object? details = null) : Exception(message)
{
    public string Code { get; } = code;
    public object? Details { get; } = details;
}

/// <summary>
/// Authoritative expense recognition of an expense claim (đề nghị hoàn ứng),
/// performed at accounting approval (stage 2), before any payment:
///
///   Dr 641/642/627  per category, NET of deductible VAT — account ONLY from
///                   department_expense_policies.debit_account (trusted config,
///                   validated by JournalRoutingRules.AllowedPairs)
///   Dr 1331         deductible input VAT (invoice-backed lines, per proposal)
///       Cr 141      employee advance cleared
///       Cr 334      payable to the employee (settled later by Dr 334 / Cr 111|112)
///
/// Lines come from the existing deterministic router; nothing from the LLM,
/// the browser, extracted JSON or a journal suggestion. Supported: payable
/// >= 0. payable < 0 (employee owes the company) is NOT posted.
/// </summary>
public static class ExpenseRecognition
{
    public const string Unrecognized = "unrecognized";
    public const string Recognized = "recognized";

    // ------------------------------------------------------------- basis

    /// <summary>Rounded per-category basis whose sums equal the claim totals exactly (residual on the largest line).</summary>
    public static List<RecognitionBasisLine> BuildBasis(
        IReadOnlyDictionary<string, decimal> approvedByCategory, IReadOnlyDictionary<string, decimal> vatDeductibleByCategory,
        IReadOnlyDictionary<string, decimal> uncappedByCategory, IReadOnlyDictionary<string, decimal> vatUncappedByCategory,
        decimal approvedTotal, decimal vatDeductibleTotal)
    {
        var lines = approvedByCategory.Keys.Select(c => new RecognitionBasisLine(c,
            Math.Round(approvedByCategory[c], 2), Math.Round(vatDeductibleByCategory.GetValueOrDefault(c), 2),
            Math.Round(uncappedByCategory.GetValueOrDefault(c), 2), Math.Round(vatUncappedByCategory.GetValueOrDefault(c), 2))).ToList();
        if (lines.Count == 0) return lines;
        var largest = lines.IndexOf(lines.MaxBy(l => l.Gross)!);
        var grossResidual = Math.Round(approvedTotal, 2) - lines.Sum(l => l.Gross);
        var vatResidual = Math.Round(vatDeductibleTotal, 2) - lines.Sum(l => l.VatDeductible);
        lines[largest] = lines[largest] with { Gross = lines[largest].Gross + grossResidual, VatDeductible = lines[largest].VatDeductible + vatResidual };
        return lines;
    }

    public static List<RecognitionBasisLine>? ParseBasis(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<RecognitionBasisLine>>(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Exception grant: the approved basis becomes the un-capped one.</summary>
    public static List<RecognitionBasisLine>? Uncap(List<RecognitionBasisLine>? basis) =>
        basis?.Select(b => b with { Gross = b.GrossUncapped, VatDeductible = b.VatUncapped }).ToList();

    /// <summary>The router's input: category amounts net of their deductible VAT.</summary>
    public static Dictionary<string, decimal> NetByCategory(IEnumerable<RecognitionBasisLine> basis) =>
        basis.ToDictionary(b => b.Category, b => b.Gross - b.VatDeductible);

    // ------------------------------------------------------------- plan

    internal sealed record ClaimFacts(
        Guid Id, Guid VoucherId, Guid? EmployeeId, string ReviewStatus, string Outcome, decimal ApprovedAmount,
        decimal AdvanceAmount, decimal PayableAmount, decimal VatDeductibleAmount, string Currency, bool RequiresTaxVerification,
        Guid? DeptReviewedBy, Guid? AccountingReviewedBy, string RecognitionStatus, string? RecognitionBasis);

    internal const string ClaimFactColumns = """
        id AS Id, voucher_id AS VoucherId, employee_id AS EmployeeId, review_status AS ReviewStatus, outcome AS Outcome,
        approved_amount AS ApprovedAmount, advance_amount AS AdvanceAmount, payable_amount AS PayableAmount,
        vat_deductible_amount AS VatDeductibleAmount, currency AS Currency, requires_tax_verification AS RequiresTaxVerification,
        dept_reviewed_by AS DeptReviewedBy, accounting_reviewed_by AS AccountingReviewedBy,
        recognition_status AS RecognitionStatus, recognition_basis::text AS RecognitionBasis
        """;

    private static readonly IReadOnlySet<string> ApprovableOutcomes = new HashSet<string> { "APPROVED", "PARTIALLY_APPROVED" };

    /// <summary>
    /// Everything recognition needs, evaluated from authoritative state; the
    /// SAME function feeds the UI preview and the posting. No actor-based
    /// rule: any department ADMIN may recognize (ADMIN Department Authority Invariant).
    /// </summary>
    internal static async Task<RecognitionPlan> PlanAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, Guid departmentId, ClaimFacts c, VoucherRecord v,
        Guid? actorId, CancellationToken ct)
    {
        var blockers = new List<RecognitionBlocker>();
        var empty = new Dictionary<string, string>();
        if (c.RecognitionStatus == Recognized)
            return new([new("already_recognized", "Đề nghị đã được ghi nhận kế toán.")], [], 0, 0, 0, 0, empty);

        if (c.ReviewStatus == "rejected" || c.Outcome is "REJECTED" or "RETURN_FOR_DOCUMENTS")
            blockers.Add(new("claim_not_recognizable", "Đề nghị đã bị từ chối hoặc đang chờ bổ sung chứng từ."));
        else if (c.Outcome == "WAITING_EXCEPTION_APPROVAL")
            blockers.Add(new("exception_approval_pending", "Đề nghị đang chờ duyệt vượt định mức."));
        else if (c.ReviewStatus != "dept_reviewed" || !ApprovableOutcomes.Contains(c.Outcome))
            blockers.Add(new("department_review_required", "Đề nghị chưa được duyệt bộ phận."));

        // ADMIN Department Authority Invariant: no actor-based blocker — the
        // same department ADMIN may be claimant, department reviewer and
        // accounting reviewer (actorId is kept for the audit trail only).
        // Multi-file claims: every bill item resolved (ready/excluded), at least one ready.
        if (await ExpenseClaimItemGate.ErrorAsync(conn, tx, c.VoucherId, ct) is { } itemsGate)
            blockers.Add(new(itemsGate, itemsGate == ExpenseClaimItemGate.Unresolved
                ? "Còn khoản chi đang xử lý / cần kiểm tra / lỗi." : "Đề nghị chưa có khoản chi hợp lệ nào."));
        if (TaxVerificationRules.ExpenseGateError(v, c.RequiresTaxVerification) is { } tax)
            blockers.Add(new(tax, "Chứng từ chưa được xác minh thuế."));
        if (!string.Equals(c.Currency, "VND", StringComparison.OrdinalIgnoreCase))
            blockers.Add(new("foreign_currency_not_supported", "Chưa hỗ trợ ngoại tệ."));
        if (c.PayableAmount < 0m)
            blockers.Add(new("employee_refund_not_supported",
                "Nhân viên phải hoàn lại tạm ứng thừa — chưa hỗ trợ ghi nhận (cần quyết định của kế toán)."));

        var employee = c.EmployeeId is { } eid
            ? await conn.QuerySingleOrDefaultAsync<(string? Position, decimal Outstanding)?>(new CommandDefinition(
                "SELECT position, outstanding_advance FROM sim_employees WHERE id=@eid AND tenant_id=@tenantId",
                new { eid, tenantId }, tx, cancellationToken: ct))
            : null;
        if (employee is null) blockers.Add(new("employee_not_resolved", "Chưa xác định nhân viên."));
        else if (employee.Value.Outstanding < c.AdvanceAmount)
            blockers.Add(new("advance_changed", "Tạm ứng còn lại của nhân viên nhỏ hơn số đã đối trừ trong đề nghị — cần đề xuất lại."));

        var basis = ParseBasis(c.RecognitionBasis);
        if (basis is null || basis.Count == 0)
            blockers.Add(new("recognition_basis_missing", "Đề nghị chưa có cơ sở ghi nhận theo khoản mục — cần trích xuất/đề xuất lại."));
        else if (basis.Sum(b => b.Gross) != c.ApprovedAmount || basis.Sum(b => b.VatDeductible) != c.VatDeductibleAmount
                 || c.ApprovedAmount - c.AdvanceAmount != c.PayableAmount || basis.Any(b => b.VatDeductible < 0m || b.VatDeductible > b.Gross))
            blockers.Add(new("recognition_basis_inconsistent", "Cơ sở ghi nhận không khớp số liệu đề nghị — cần đề xuất lại."));

        // Trusted accounts: category -> configured debit account, never anything else.
        var accounts = new Dictionary<string, string>();
        if (basis is { Count: > 0 })
        {
            foreach (var b in basis)
            {
                var account = await conn.ExecuteScalarAsync<string?>(new CommandDefinition("""
                    SELECT debit_account FROM department_expense_policies
                    WHERE tenant_id=@tenantId AND department_id=@departmentId AND active
                      AND (category = @category OR category IS NULL)
                      AND (position = @position OR position IS NULL)
                    ORDER BY (category IS NOT NULL)::int + (position IS NOT NULL)::int DESC
                    LIMIT 1
                    """, new { tenantId, departmentId, category = b.Category, position = employee?.Position }, tx, cancellationToken: ct));
                if (string.IsNullOrWhiteSpace(account))
                    blockers.Add(new("expense_account_mapping_missing", $"Chưa cấu hình tài khoản chi phí cho khoản mục \"{b.Category}\"."));
                else accounts[b.Category] = account.Trim();
            }
        }

        var lines = new List<JournalLine>();
        if (blockers.Count == 0)
        {
            var routing = JournalRoutingRules.ForExpenseSettlement(NetByCategory(basis!), accounts, basis!.Sum(b => b.VatDeductible),
                c.AdvanceAmount, c.PayableAmount);
            if (routing.Outcome != JournalRoutingOutcome.Proposed)
                blockers.Add(new("expense_account_not_allowed", $"Tài khoản chi phí cấu hình không hợp lệ ({routing.Reason})."));
            else
            {
                lines.AddRange(routing.Entries.Select(e => new JournalLine(e.DebitAccount, e.CreditAccount, e.Amount, e.Description)));
                // Invariants of the posting — any mismatch is a bug, never posted.
                var credit141 = lines.Where(l => l.CreditAccount == "141").Sum(l => l.Amount);
                var credit334 = lines.Where(l => l.CreditAccount == "334").Sum(l => l.Amount);
                if (lines.Sum(l => l.Amount) != c.ApprovedAmount || credit141 != c.AdvanceAmount || credit334 != c.PayableAmount
                    || lines.Any(l => l.Amount <= 0m) || lines.Any(l => l.DebitAccount == "334"))
                {
                    lines.Clear();
                    blockers.Add(new("recognition_unbalanced", "Bút toán ghi nhận không cân với số liệu đề nghị."));
                }
            }
        }
        return new(blockers, lines, c.ApprovedAmount, c.VatDeductibleAmount, c.AdvanceAmount, Math.Max(c.PayableAmount, 0m), accounts);
    }

    // ------------------------------------------------------------- post

    /// <summary>
    /// Stage 2 "Duyệt kế toán" = accounting approval + authoritative recognition,
    /// ONE transaction: lock, plan, post lines, clear advance / add YTD once,
    /// set statuses. Any failure rolls everything back.
    /// </summary>
    internal static async Task<(string Outcome, Guid ClaimId)?> RecognizeAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid departmentId, Guid voucherId, Guid actorId, CancellationToken ct)
    {
        // Lock order: voucher -> claim -> employee.
        var v = await conn.QuerySingleOrDefaultAsync<VoucherRecord>(new CommandDefinition($"""
            SELECT {TaxVerificationRepository.VoucherColumns} FROM vouchers
            WHERE id=@voucherId AND tenant_id=@tenantId AND department_id=@departmentId AND voucher_type='expense_claim'
            FOR UPDATE
            """, new { voucherId, tenantId, departmentId }, tx, cancellationToken: ct));
        if (v is null) return null;
        var c = await conn.QuerySingleOrDefaultAsync<ClaimFacts>(new CommandDefinition($"""
            SELECT {ClaimFactColumns} FROM expense_claim_results
            WHERE voucher_id=@voucherId AND tenant_id=@tenantId AND department_id=@departmentId FOR UPDATE
            """, new { voucherId, tenantId, departmentId }, tx, cancellationToken: ct));
        if (c is null) return null;
        if (c.RecognitionStatus == Recognized) return ("already_recognized", c.Id);

        var plan = await PlanAsync(conn, tx, tenantId, departmentId, c, v, actorId, ct);
        if (plan.Blockers.Count > 0)
            throw new ExpenseRecognitionException(plan.Blockers[0].Code, plan.Blockers[0].Message, new { blockers = plan.Blockers });

        // Employee: advance cleared + YTD expense — exactly once, here (no longer at payment).
        await EmployeeRepository.ApplyExpenseClaimInTransactionAsync(conn, tx, tenantId, c.EmployeeId!.Value, c.ApprovedAmount, c.AdvanceAmount, ct);

        // AI suggestions stay as history, visibly superseded — never deleted.
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE voucher_journal_suggestions SET rejected_at = NOW(), reject_reason = 'superseded_by_recognition'
            WHERE voucher_id=@voucherId AND source='suggestion' AND accepted_at IS NULL AND rejected_at IS NULL
            """, new { voucherId }, tx, cancellationToken: ct));
        foreach (var line in plan.Lines)
            await CashSettlementRepository.InsertPostingLineAsync(conn, tx, voucherId, null, c.Id, line, "posting", actorId, ct);

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE expense_claim_results SET
                review_status='accounting_reviewed', accounting_reviewed_by=@actorId, accounting_reviewed_at=NOW(),
                recognition_status='recognized', recognized_by=@actorId, recognized_at=NOW(),
                payment_status = CASE WHEN payable_amount = 0 THEN 'not_due' ELSE 'unpaid' END,
                updated_at=NOW()
            WHERE id=@id
            """, new { actorId, id = c.Id }, tx, cancellationToken: ct));
        return ("recognized", c.Id);
    }

    /// <summary>The authoritative recognition lines of a claim (posting rows linked to it).</summary>
    internal static async Task<IReadOnlyList<JournalLine>> PostedLinesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid claimId, CancellationToken ct) =>
        (await conn.QueryAsync<JournalLine>(new CommandDefinition("""
            SELECT debit_account AS DebitAccount, credit_account AS CreditAccount, amount AS Amount, COALESCE(description,'') AS Description
            FROM voucher_journal_suggestions WHERE expense_claim_id=@claimId AND source='posting' ORDER BY entry_no
            """, new { claimId }, tx, cancellationToken: ct))).ToList();
}
