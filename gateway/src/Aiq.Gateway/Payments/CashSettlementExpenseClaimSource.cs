using Aiq.Gateway.Vouchers;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Payments;

// ============================================================================
// Expense claim (đề nghị hoàn ứng) -> final approval + ACTUAL payment through
// the SAME canonical cash infrastructure: one cash_transactions row
// (source_type = 'expense_claim', counterparty = the employee, Cr cash/bank,
// Dr 334) and its 'posting' journal line, written by the shared journal
// writer. Not AP: an expense claim has no supplier invoice, so there are no
// payment_applications. Nothing here is a second accounting engine — it only
// gathers the claim's facts, enforces the claim's own gates, and posts.
//
// Supported deterministic case ONLY: payable_amount > 0 (the company owes the
// employee the remainder after the advance). payable <= 0 has no cash due to
// the employee (and an employee refund is not modelled) — those claims keep
// the existing no-cash final approval (IExpenseClaimRepository.ConfirmAsync).
// ============================================================================

public static class ExpenseClaimPaymentStatuses
{
    public const string Unpaid = "unpaid";
    public const string Paid = "paid";
    public const string NotDue = "not_due";
}

public sealed record ExpenseClaimPayRequest(string? PaymentMethod, Guid? CompanyBankAccountId);

public sealed record PaymentBlocker(string Code, string Message);

public sealed record ExpenseClaimEmployee(Guid Id, string Code, string Name);

public sealed record ExpenseClaimPaymentPosting(
    Guid CashTransactionId, decimal Amount, string PaymentMethod, string CashAccount, string CounterAccount,
    Guid? BankAccountId, string? BankName, string? BankAccountNumber, string DocumentNumber, DateTime PostedAt,
    Guid? PaidBy, string? PaidByName, string Status, IReadOnlyList<JournalLine> Journal);

/// <summary>GHI NHẬN KẾ TOÁN: the posted recognition, or (not yet recognized) the deterministic preview + its blockers.</summary>
public sealed record ExpenseClaimRecognitionView(
    string Status, Guid? RecognizedBy, string? RecognizedByName, DateTime? RecognizedAt,
    decimal ExpenseAmount, decimal VatDeductible, decimal AdvanceCleared, decimal EmployeePayable,
    bool IsPreview, IReadOnlyList<JournalLine> Lines, IReadOnlyList<Aiq.Gateway.Employees.RecognitionBlocker> Blockers);

public sealed record ExpenseClaimPaymentContext(
    Guid VoucherId, Guid ClaimId, string ReviewStatus, string Outcome, string PaymentStatus,
    decimal ApprovedAmount, decimal AdvanceAmount, decimal PayableAmount, string Currency,
    ExpenseClaimEmployee? Employee, bool TaxVerificationRequired, string? TaxGate,
    IReadOnlyList<PaymentBlocker> Blockers, string CashAccount, IReadOnlyList<CompanyBankAccountInfo> CompanyBankAccounts,
    ExpenseClaimPaymentPosting? Payment,
    ExpenseClaimRecognitionView Recognition);

public sealed record ExpenseClaimPaymentResult(string Outcome, ExpenseClaimPaymentContext Context);

public interface IExpenseClaimPayment
{
    /// <summary>Read model for the THANH TOÁN section; blockers are evaluated for <paramref name="actorId"/>.</summary>
    Task<ExpenseClaimPaymentContext?> GetExpenseClaimPaymentAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid actorId, CancellationToken ct);

    /// <summary>Duyệt & chi tiền: final approval + cash posting + journal + PAID, atomically. Idempotent.</summary>
    Task<ExpenseClaimPaymentResult?> PayExpenseClaimAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid actorId, ExpenseClaimPayRequest request, CancellationToken ct);
}

public sealed partial class CashSettlementRepository : IExpenseClaimPayment
{
    public static class ExpenseClaimSource
    {
        public const string SourceType = "expense_claim";
        public const string CashAccount = "1111";      // CashSettlementRules.DefaultCashAccount("cash")
        public const string EmployeePayableAccount = "334";
    }

    private sealed record ClaimRow(
        Guid Id, Guid VoucherId, Guid? EmployeeId, string ReviewStatus, string Outcome, decimal ApprovedAmount,
        decimal AdvanceAmount, decimal PayableAmount, string Currency, bool RequiresTaxVerification,
        Guid? DeptReviewedBy, Guid? AccountingReviewedBy, string PaymentStatus, Guid? PaidCashTransactionId, string RecognitionStatus);

    private const string ClaimColumns = """
        id AS Id, voucher_id AS VoucherId, employee_id AS EmployeeId, review_status AS ReviewStatus, outcome AS Outcome,
        approved_amount AS ApprovedAmount, advance_amount AS AdvanceAmount, payable_amount AS PayableAmount,
        currency AS Currency, requires_tax_verification AS RequiresTaxVerification,
        dept_reviewed_by AS DeptReviewedBy, accounting_reviewed_by AS AccountingReviewedBy,
        payment_status AS PaymentStatus, paid_cash_transaction_id AS PaidCashTransactionId,
        recognition_status AS RecognitionStatus
        """;

    private static readonly IReadOnlySet<string> ApprovableOutcomes = new HashSet<string> { "APPROVED", "PARTIALLY_APPROVED" };

    /// <summary>
    /// Every gate of the final payment, from authoritative DB state only (never
    /// extracted JSON, browser input or LLM output). Empty = may pay.
    /// </summary>
    private static List<PaymentBlocker> ClaimBlockers(ClaimRow c, VoucherRecord v, Guid actorId)
    {
        var b = new List<PaymentBlocker>();
        if (c.PaymentStatus == ExpenseClaimPaymentStatuses.Paid)
        {
            b.Add(new("already_paid", "Đề nghị đã được thanh toán."));
            return b;
        }
        if (c.ReviewStatus == "rejected" || c.Outcome is "REJECTED" or "RETURN_FOR_DOCUMENTS")
            b.Add(new("claim_not_payable", "Đề nghị đã bị từ chối hoặc đang chờ bổ sung chứng từ."));
        else if (c.Outcome == "WAITING_EXCEPTION_APPROVAL")
            b.Add(new("exception_approval_pending", "Đề nghị đang chờ duyệt vượt định mức."));
        else if (c.ReviewStatus == "confirmed")
            b.Add(new("approved_without_payment", "Đề nghị đã được duyệt cuối mà không chi tiền — cần xử lý thủ công."));
        else if (c.ReviewStatus != "accounting_reviewed" || !ApprovableOutcomes.Contains(c.Outcome))
            b.Add(new("approval_incomplete", "Đề nghị chưa hoàn tất phê duyệt (bộ phận → kế toán)."));
        // The 334 liability must exist (authoritative recognition) before Dr 334.
        if (c.RecognitionStatus != Aiq.Gateway.Employees.ExpenseRecognition.Recognized)
            b.Add(new("expense_not_recognized", "Đề nghị chưa được ghi nhận kế toán."));

        if (c.PayableAmount <= 0m)
            b.Add(new("no_payment_due", "Không phát sinh khoản chi cho nhân viên (tạm ứng đã bù trừ, hoặc nhân viên phải hoàn lại — chưa hỗ trợ)."));
        if (c.EmployeeId is null)
            b.Add(new("employee_not_resolved", "Chưa xác định nhân viên nhận tiền."));
        if (!string.Equals(c.Currency, "VND", StringComparison.OrdinalIgnoreCase))
            b.Add(new("foreign_currency_not_supported", "Chưa hỗ trợ ngoại tệ."));
        if (TaxVerificationRules.ExpenseGateError(v, c.RequiresTaxVerification) is { } tax)
            b.Add(new(tax, "Chứng từ chưa được xác minh thuế."));

        // ADMIN Department Authority Invariant: the paying ADMIN may also be the
        // claimant and/or an earlier reviewer — no actor-based blocker.
        return b;
    }

    private static async Task<(VoucherRecord V, ClaimRow C)?> LoadClaimAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, Guid departmentId, Guid voucherId, bool lockRows, CancellationToken ct)
    {
        // Lock order: voucher -> claim -> (employee) -> (bank account).
        var v = await conn.QuerySingleOrDefaultAsync<VoucherRecord>(new CommandDefinition($"""
            SELECT {TaxVerificationRepository.VoucherColumns} FROM vouchers
            WHERE id=@voucherId AND tenant_id=@tenantId AND department_id=@departmentId AND voucher_type='expense_claim'
            {(lockRows ? "FOR UPDATE" : "")}
            """, new { voucherId, tenantId, departmentId }, tx, cancellationToken: ct));
        if (v is null) return null;
        var c = await conn.QuerySingleOrDefaultAsync<ClaimRow>(new CommandDefinition($"""
            SELECT {ClaimColumns} FROM expense_claim_results
            WHERE voucher_id=@voucherId AND tenant_id=@tenantId AND department_id=@departmentId
            {(lockRows ? "FOR UPDATE" : "")}
            """, new { voucherId, tenantId, departmentId }, tx, cancellationToken: ct));
        return c is null ? null : (v, c);
    }

    public async Task<ExpenseClaimPaymentContext?> GetExpenseClaimPaymentAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid actorId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var loaded = await LoadClaimAsync(conn, null, tenantId, departmentId, voucherId, false, ct);
        return loaded is { } l ? await BuildClaimContextAsync(conn, tenantId, l.V, l.C, actorId, ct) : null;
    }

    private async Task<ExpenseClaimPaymentContext> BuildClaimContextAsync(
        NpgsqlConnection conn, Guid tenantId, VoucherRecord v, ClaimRow c, Guid actorId, CancellationToken ct)
    {
        var employee = c.EmployeeId is { } eid
            ? await conn.QuerySingleOrDefaultAsync<ExpenseClaimEmployee>(new CommandDefinition(
                "SELECT id AS Id, employee_code AS Code, full_name AS Name FROM sim_employees WHERE id=@eid AND tenant_id=@tenantId",
                new { eid, tenantId }, cancellationToken: ct))
            : null;
        var banks = (await ListCompanyBankAccountsAsync(tenantId, ct)).Where(a => a.GlAccountCode.StartsWith("112", StringComparison.Ordinal)).ToList();

        ExpenseClaimPaymentPosting? payment = null;
        var paidTx = c.PaidCashTransactionId ?? await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition("""
            SELECT id FROM cash_transactions WHERE source_expense_claim_id=@claimId ORDER BY posted_at DESC LIMIT 1
            """, new { claimId = c.Id }, cancellationToken: ct));
        if (paidTx is { } txId)
        {
            var row = await conn.QuerySingleAsync<(decimal Amount, string Method, string CashAccount, string CounterAccount, Guid? BankId,
                string? BankName, string? BankNumber, string Doc, DateTime PostedAt, Guid? PostedBy, string? PostedByName, string Status)>(
                new CommandDefinition("""
                    SELECT c.amount, c.payment_method, c.cash_account, c.counter_account, c.bank_account_id, b.bank_name, b.account_number,
                           c.document_number, c.posted_at, c.posted_by, COALESCE(u.display_name, u.email), c.status
                    FROM cash_transactions c
                    LEFT JOIN sim_bank_accounts b ON b.id = c.bank_account_id
                    LEFT JOIN users u ON u.id = c.posted_by
                    WHERE c.id=@txId
                    """, new { txId }, cancellationToken: ct));
            var journal = (await conn.QueryAsync<JournalLine>(new CommandDefinition("""
                SELECT debit_account AS DebitAccount, credit_account AS CreditAccount, amount AS Amount, COALESCE(description,'') AS Description
                FROM voucher_journal_suggestions WHERE cash_transaction_id=@txId ORDER BY entry_no
                """, new { txId }, cancellationToken: ct))).ToList();
            payment = new ExpenseClaimPaymentPosting(txId, row.Amount, row.Method, row.CashAccount, row.CounterAccount, row.BankId,
                row.BankName, row.BankNumber, row.Doc, row.PostedAt, row.PostedBy, row.PostedByName, row.Status, journal);
        }

        var recognition = await BuildRecognitionViewAsync(conn, tenantId, v, c, actorId, ct);
        return new ExpenseClaimPaymentContext(v.Id, c.Id, c.ReviewStatus, c.Outcome, c.PaymentStatus, c.ApprovedAmount, c.AdvanceAmount,
            c.PayableAmount, c.Currency, employee, c.RequiresTaxVerification, TaxVerificationRules.ExpenseGateError(v, c.RequiresTaxVerification),
            ClaimBlockers(c, v, actorId), ExpenseClaimSource.CashAccount, banks, payment, recognition);
    }

    private static async Task<ExpenseClaimRecognitionView> BuildRecognitionViewAsync(
        NpgsqlConnection conn, Guid tenantId, VoucherRecord v, ClaimRow c, Guid actorId, CancellationToken ct)
    {
        var facts = await conn.QuerySingleAsync<Aiq.Gateway.Employees.ExpenseRecognition.ClaimFacts>(new CommandDefinition(
            $"SELECT {Aiq.Gateway.Employees.ExpenseRecognition.ClaimFactColumns} FROM expense_claim_results WHERE id=@id",
            new { id = c.Id }, cancellationToken: ct));
        if (c.RecognitionStatus == Aiq.Gateway.Employees.ExpenseRecognition.Recognized)
        {
            var who = await conn.QuerySingleAsync<(Guid? By, string? Name, DateTime? At)>(new CommandDefinition("""
                SELECT r.recognized_by, COALESCE(u.display_name, u.email), r.recognized_at
                FROM expense_claim_results r LEFT JOIN users u ON u.id = r.recognized_by WHERE r.id=@id
                """, new { id = c.Id }, cancellationToken: ct));
            var posted = await Aiq.Gateway.Employees.ExpenseRecognition.PostedLinesAsync(conn, null, c.Id, ct);
            return new(c.RecognitionStatus, who.By, who.Name, who.At, facts.ApprovedAmount, facts.VatDeductibleAmount,
                posted.Where(l => l.CreditAccount == "141").Sum(l => l.Amount), posted.Where(l => l.CreditAccount == "334").Sum(l => l.Amount),
                false, posted, Array.Empty<Aiq.Gateway.Employees.RecognitionBlocker>());
        }
        var departmentId = v.DepartmentId ?? Guid.Empty;
        var plan = await Aiq.Gateway.Employees.ExpenseRecognition.PlanAsync(conn, null, tenantId, departmentId, facts, v, actorId, ct);
        return new(c.RecognitionStatus, null, null, null, plan.ExpenseAmount, plan.VatDeductible, plan.AdvanceCleared, plan.EmployeePayable,
            true, plan.Lines, plan.Blockers);
    }

    public async Task<ExpenseClaimPaymentResult?> PayExpenseClaimAsync(
        Guid tenantId, Guid departmentId, Guid voucherId, Guid actorId, ExpenseClaimPayRequest request, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var loaded = await LoadClaimAsync(conn, tx, tenantId, departmentId, voucherId, lockRows: true, ct);
        if (loaded is not { } l) { await tx.RollbackAsync(ct); return null; }
        var (v, c) = l;

        // Idempotent replay / the loser of a concurrent double-submit (it waited
        // on the row lock above): nothing to do.
        if (c.PaymentStatus == ExpenseClaimPaymentStatuses.Paid)
        {
            await tx.RollbackAsync(ct);
            return new("already_paid", await BuildClaimContextAsync(conn, tenantId, v, c, actorId, ct));
        }
        var blockers = ClaimBlockers(c, v, actorId);
        if (blockers.Count > 0)
            throw new CashSettlementException(blockers[0].Code, blockers[0].Message, new { blockers });

        // Payment source: explicitly chosen by ADMIN, resolved server-side.
        var method = (request.PaymentMethod ?? "").Trim().ToLowerInvariant();
        string cashAccount;
        Guid? bankAccountId = null;
        if (method == "cash")
        {
            cashAccount = ExpenseClaimSource.CashAccount;
        }
        else if (method == "bank")
        {
            if (request.CompanyBankAccountId is not { } bankId)
                throw new CashSettlementException("company_bank_account_required", "Chưa chọn tài khoản ngân hàng của công ty.");
            var bank = await conn.QuerySingleOrDefaultAsync<(string Gl, string? Currency)?>(new CommandDefinition("""
                SELECT gl_account_code, currency FROM sim_bank_accounts WHERE id=@bankId AND tenant_id=@tenantId FOR UPDATE
                """, new { bankId, tenantId }, tx, cancellationToken: ct));
            if (bank is not { } b)
                throw new CashSettlementException("company_bank_account_invalid", "Tài khoản ngân hàng không thuộc công ty.");
            if (!b.Gl.StartsWith("112", StringComparison.Ordinal) || !string.Equals(b.Currency ?? "VND", "VND", StringComparison.OrdinalIgnoreCase))
                throw new CashSettlementException("company_bank_account_invalid", "Tài khoản ngân hàng chưa gắn TK 112x (VND).");
            cashAccount = b.Gl;
            bankAccountId = bankId;
        }
        else
        {
            throw new CashSettlementException("payment_source_required", "Chọn nguồn chi: tiền mặt hoặc tài khoản ngân hàng của công ty.");
        }

        var employee = await conn.QuerySingleAsync<ExpenseClaimEmployee>(new CommandDefinition(
            "SELECT id AS Id, employee_code AS Code, full_name AS Name FROM sim_employees WHERE id=@eid AND tenant_id=@tenantId",
            new { eid = c.EmployeeId, tenantId }, tx, cancellationToken: ct));

        // The employee's advance / YTD were updated once, at recognition — the
        // payment only settles the liability. Prove that liability: the posted
        // recognition credited 334 by exactly the payable being paid.
        var recognizedPayable = (await Aiq.Gateway.Employees.ExpenseRecognition.PostedLinesAsync(conn, tx, c.Id, ct))
            .Where(l => l.CreditAccount == ExpenseClaimSource.EmployeePayableAccount).Sum(l => l.Amount);
        if (recognizedPayable != c.PayableAmount || recognizedPayable <= 0m)
            throw new CashSettlementException("recognition_payable_mismatch",
                "Khoản phải trả nhân viên (334) đã ghi nhận không khớp số tiền chi.", new { recognizedPayable, c.PayableAmount });

        var amount = recognizedPayable;
        var docNumber = string.IsNullOrWhiteSpace(v.DocumentNumber) ? $"PC-DNTT-{v.Id.ToString("N")[..8]}" : v.DocumentNumber!;
        var cashId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition("""
            INSERT INTO cash_transactions (tenant_id, department_id, voucher_id, direction, counterparty_type, employee_id,
                amount, currency, transaction_date, payment_method, cash_account, counter_account, bank_account_id,
                document_number, description, status, posted_by, source_type, source_expense_claim_id)
            VALUES (@tenantId, @departmentId, @voucherId, 'money_out', 'employee', @employeeId,
                @amount, 'VND', @txDate, @method, @cashAccount, '334', @bankAccountId,
                @docNumber, @description, 'posted', @actorId, @sourceType, @claimId)
            ON CONFLICT DO NOTHING
            RETURNING id
            """, new
        {
            tenantId, departmentId, voucherId, employeeId = employee.Id, amount, txDate = Today, method, cashAccount, bankAccountId,
            docNumber, description = $"Chi hoàn ứng — {employee.Name}", actorId, sourceType = ExpenseClaimSource.SourceType, claimId = c.Id,
        }, tx, cancellationToken: ct));
        if (cashId is not { } paidId)
        {
            // Defence in depth — the claim lock makes this unreachable; the
            // unique live-posting index makes a second payment impossible.
            await tx.RollbackAsync(ct);
            return new("already_paid", await BuildClaimContextAsync(conn, tenantId, v, c with { PaymentStatus = ExpenseClaimPaymentStatuses.Paid }, actorId, ct));
        }
        if (bankAccountId is { } paidFrom)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE sim_bank_accounts SET current_balance = COALESCE(current_balance,0) - @amount WHERE id=@paidFrom AND tenant_id=@tenantId",
                new { amount, paidFrom, tenantId }, tx, cancellationToken: ct));
        }

        // Deterministic posting journal (the shared writer): Dr 334 / Cr 111x|112x.
        await InsertJournalAsync(conn, tx, voucherId, paidId,
            new JournalLine(ExpenseClaimSource.EmployeePayableAccount, cashAccount, amount, $"Chi trả hoàn ứng cho {employee.Name}"),
            "posting", actorId, ct);

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE expense_claim_results SET
                review_status='confirmed', reviewed_by=@actorId, reviewed_at=NOW(), updated_at=NOW(),
                payment_status='paid', paid_cash_transaction_id=@paidId, paid_by=@actorId, paid_at=NOW()
            WHERE id=@claimId
            """, new { actorId, paidId, claimId = c.Id }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE vouchers SET status='verified' WHERE id=@voucherId",
            new { voucherId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);

        var after = (await LoadClaimAsync(conn, null, tenantId, departmentId, voucherId, false, ct))!.Value;
        return new("paid", await BuildClaimContextAsync(conn, tenantId, after.V, after.C, actorId, ct));
    }
}
