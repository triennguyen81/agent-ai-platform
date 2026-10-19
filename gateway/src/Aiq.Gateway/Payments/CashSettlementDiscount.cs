using Dapper;
using Npgsql;

namespace Aiq.Gateway.Payments;

/// <summary>A read-only discount proposal shown next to the settlement context. Never applied automatically.</summary>
public sealed record PaymentDiscountProposal(
    PaymentDiscountEvaluation Evaluation, string? ContractLabel, bool AccountingPolicyAvailable, string? BlockedBy);

/// <summary>
/// ADMIN "apply discount and settle". The expected values are the proposal
/// the ADMIN saw — compared against a fresh server-side evaluation, never used
/// as the amount.
/// </summary>
public sealed record ConfirmPaymentDiscountRequest(
    Guid InvoiceId, Guid? TermId, int? TermRevision, decimal? DiscountAmount, decimal? OpenAmount, bool ConfirmNotDuplicate = false);

public static class PaymentDiscountConflicts
{
    public const string TermsChanged = "discount_terms_changed";
    public const string EligibilityChanged = "discount_eligibility_changed";
    public const string InvoiceBalanceChanged = "invoice_balance_changed";
    public const string InvoiceNotOpen = "discount_invoice_not_open";
    public const string RequiresReceipt = "discount_requires_money_in";
}

/// <summary>
/// Payment discount on the canonical settlement: the SAME evaluation and the
/// SAME confirmation core for a Phiếu thu and for a bank-statement money_in
/// line. Only the source (lock + receipt date/amount) differs.
/// </summary>
public sealed partial class CashSettlementRepository
{
    private sealed record DiscountInvoiceRow(
        Guid Id, string DocumentNumber, Guid CustomerId, string? CustomerTaxCode, Guid? ContractId,
        DateOnly DocumentDate, decimal Original, decimal Paid);

    private sealed record DiscountContractRow(
        Guid Id, Guid TenantId, Guid DepartmentId, string WorkflowState, string? Direction, string? ContractNumber, string? Title);

    private sealed record DiscountTermRow(
        Guid Id, Guid ContractId, int Revision, bool Enabled, decimal RatePercent, string Basis, int DaysCount, string Currency);

    /// <summary>
    /// Evaluates the customer's open, contract-linked invoices (or exactly
    /// one invoice) against the receipt. lockContracts = FOR SHARE on the
    /// contract rows, so a concurrent term edit (FOR UPDATE) cannot interleave
    /// with a confirmation.
    /// </summary>
    private static async Task<List<(PaymentDiscountEvaluation Evaluation, string? ContractLabel)>> EvaluateDiscountsAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, Guid customerId, Guid? invoiceId,
        DiscountReceipt receipt, bool lockContracts, CancellationToken ct)
    {
        var invoices = (await conn.QueryAsync<DiscountInvoiceRow>(new CommandDefinition("""
            SELECT a.id AS Id, a.document_number AS DocumentNumber, a.customer_id AS CustomerId, c.mst AS CustomerTaxCode,
                   a.contract_id AS ContractId, a.document_date AS DocumentDate, a.debit AS Original, a.paid_amount AS Paid
            FROM sim_ar_ledger a JOIN sim_customers c ON c.id = a.customer_id AND c.tenant_id = a.tenant_id
            WHERE a.tenant_id=@tenantId AND a.customer_id=@customerId AND a.document_type='invoice'
              AND a.status IN ('open','partial')
              AND (@invoiceId::uuid IS NULL OR a.id=@invoiceId)
              AND (@invoiceId::uuid IS NOT NULL OR a.contract_id IS NOT NULL)
            ORDER BY a.document_date, a.id
            """, new { tenantId, customerId, invoiceId }, tx, cancellationToken: ct))).AsList();
        if (invoices.Count == 0) return new();

        var contractIds = invoices.Where(i => i.ContractId is not null).Select(i => i.ContractId!.Value).Distinct().ToArray();
        var contracts = contractIds.Length == 0 ? new Dictionary<Guid, DiscountContractRow>()
            : (await conn.QueryAsync<DiscountContractRow>(new CommandDefinition($"""
                SELECT id AS Id, tenant_id AS TenantId, department_id AS DepartmentId, workflow_state AS WorkflowState,
                       direction AS Direction, contract_number AS ContractNumber, title AS Title
                FROM contracts WHERE id = ANY(@contractIds) AND tenant_id=@tenantId
                {(lockContracts ? "FOR SHARE" : "")}
                """, new { contractIds, tenantId }, tx, cancellationToken: ct))).ToDictionary(c => c.Id);
        var buyers = contractIds.Length == 0 ? new Dictionary<Guid, List<string>>()
            : (await conn.QueryAsync<(Guid ContractId, string TaxCode)>(new CommandDefinition("""
                SELECT contract_id, tax_code FROM contract_parties
                WHERE contract_id = ANY(@contractIds) AND tenant_id=@tenantId AND party_role='buyer' AND tax_code IS NOT NULL
                """, new { contractIds, tenantId }, tx, cancellationToken: ct)))
              .GroupBy(b => b.ContractId).ToDictionary(g => g.Key, g => g.Select(b => b.TaxCode).ToList());
        var terms = contractIds.Length == 0 ? new Dictionary<Guid, DiscountTermRow>()
            : (await conn.QueryAsync<DiscountTermRow>(new CommandDefinition("""
                SELECT id AS Id, contract_id AS ContractId, revision AS Revision, enabled AS Enabled, rate_percent AS RatePercent,
                       basis AS Basis, days_count AS DaysCount, currency AS Currency
                FROM contract_payment_discount_terms
                WHERE contract_id = ANY(@contractIds) AND tenant_id=@tenantId AND superseded_at IS NULL
                """, new { contractIds, tenantId }, tx, cancellationToken: ct))).ToDictionary(t => t.ContractId);

        var result = new List<(PaymentDiscountEvaluation, string?)>();
        foreach (var i in invoices)
        {
            DiscountContractRow? c = i.ContractId is { } cid && contracts.TryGetValue(cid, out var cr) ? cr : null;
            DiscountTermRow? t = c is not null && terms.TryGetValue(c.Id, out var tr) ? tr : null;
            var evaluation = PaymentDiscountCalculator.Evaluate(
                new DiscountInvoiceSnapshot(i.Id, i.DocumentNumber, i.CustomerId, i.CustomerTaxCode, i.ContractId, i.DocumentDate, i.Original, i.Paid),
                c is null ? null : new DiscountContractSnapshot(c.Id, c.TenantId, c.DepartmentId, c.WorkflowState, c.Direction,
                    buyers.TryGetValue(c.Id, out var b) ? b : []),
                t is null ? null : new DiscountTermSnapshot(t.Id, t.Revision, t.Enabled, t.RatePercent, t.Basis, t.DaysCount, t.Currency),
                receipt);
            result.Add((evaluation, c is null ? null : c.ContractNumber ?? c.Title));
        }
        return result;
    }

    /// <summary>Proposals for an UNPOSTED money_in receipt. Not-applicable invoices (no link / no term) are omitted, so nothing changes for them.</summary>
    private static async Task<IReadOnlyList<PaymentDiscountProposal>> LoadDiscountProposalsAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, Guid customerId, DiscountReceipt receipt, CancellationToken ct)
    {
        if (receipt.CashAvailable <= 0m) return [];
        var policy = PaymentDiscountAccountingPolicy.ResolveDiscountDebitAccount() is not null;
        return (await EvaluateDiscountsAsync(conn, tx, tenantId, customerId, null, receipt, lockContracts: false, ct))
            .Where(e => e.Evaluation.Status != PaymentDiscountStatus.NotApplicable)
            .Select(e => new PaymentDiscountProposal(e.Evaluation, e.ContractLabel, policy,
                e.Evaluation.Eligible && !policy ? PaymentDiscountAccountingPolicy.MissingCode : null))
            .ToList();
    }

    // ------------------------------------------------------------------
    // Confirmation (ADMIN) — voucher source
    // ------------------------------------------------------------------

    public async Task<SettlementMutationResult?> ConfirmPaymentDiscountAsync(
        Guid tenantId, Guid departmentId, Guid voucherId, Guid actorId, ConfirmPaymentDiscountRequest request, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // Same lock order as PostAsync: voucher -> customer -> invoice rows (-> contract FOR SHARE).
        var ctx = await LoadContextAsync(conn, tx, tenantId, departmentId, voucherId, lockVoucher: true, null, ct);
        if (ctx is null) return null;
        if (ctx.Posting is not null)
            throw new CashSettlementException("already_posted", "Chứng từ đã được ghi nhận — chiết khấu chỉ áp dụng cùng lúc ghi nhận khoản thu.");
        if (ctx.Blockers.Contains("voucher_type_unconfirmed"))
            throw new CashSettlementException("voucher_type_unconfirmed", "Loại chứng từ chưa được xác nhận — ADMIN cần xác nhận loại đúng trước khi ghi nhận.");
        ThrowIfDocumentGated(ctx.Blockers);
        if (ctx.Direction.Direction != CashDirections.MoneyIn)
            throw new CashSettlementException(PaymentDiscountConflicts.RequiresReceipt, "Chiết khấu thanh toán chỉ áp dụng cho khoản thu của khách hàng.");
        if (ctx.Counterparty is not { } cp)
            throw new CashSettlementException("counterparty_required", "Chưa xác định được khách hàng — chọn khách hàng.");
        if (ctx.Voucher.Amount is not { } amount || amount <= 0m)
            throw new CashSettlementException("amount_missing", "Chứng từ chưa có số tiền.");
        if (!string.Equals(ctx.Voucher.Currency, "VND", StringComparison.OrdinalIgnoreCase))
            throw new CashSettlementException("foreign_currency_not_supported", "Chưa hỗ trợ ngoại tệ (không có nguồn tỷ giá).");
        if (ctx.Voucher.PaymentMethod is null)
            throw new CashSettlementException("payment_method_required", "Chọn hình thức: Tiền mặt hoặc Chuyển khoản.");

        await LockCustomerAndInvoicesAsync(conn, tx, tenantId, cp.Id, ct);
        return await ConfirmDiscountCoreAsync(conn, tx, tenantId, departmentId, SourceTypes.Voucher, voucherId, null, cp.Id,
            new DiscountReceipt(tenantId, departmentId, ctx.Voucher.DocumentDate, amount, ctx.Voucher.Currency), request, actorId, ct);
    }

    // ------------------------------------------------------------------
    // Confirmation (ADMIN) — bank-statement money_in line source
    // ------------------------------------------------------------------

    public async Task<SettlementMutationResult?> ConfirmStatementLinePaymentDiscountAsync(
        Guid tenantId, Guid departmentId, Guid voucherId, Guid lineId, Guid actorId, ConfirmPaymentDiscountRequest request, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var locked = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition("""
            SELECT t.id FROM vouchers v JOIN sim_bank_transactions t ON t.voucher_id = v.id
            WHERE v.id=@voucherId AND v.tenant_id=@tenantId AND v.department_id=@departmentId
              AND t.id=@lineId AND t.tenant_id=@tenantId AND t.department_id=@departmentId
            FOR UPDATE OF v, t
            """, new { voucherId, lineId, tenantId, departmentId }, tx, cancellationToken: ct));
        if (locked is null) return null;
        var ctx = (await LoadStatementContextAsync(conn, tx, tenantId, departmentId, voucherId, lineId, ct))!;
        if (ctx.Posting is not null)
            throw new CashSettlementException("already_posted", "Giao dịch đã được ghi nhận — chiết khấu chỉ áp dụng cùng lúc ghi nhận khoản thu.");
        if (ctx.ReconciledWith is not null)
            throw new CashSettlementException("already_reconciled", "Giao dịch đã được đối chiếu với một bút toán đã ghi nhận — không ghi nhận lại.");
        ThrowIfDocumentGated(ctx.Blockers);
        var blocking = ctx.Blockers.Where(b => !(b == "possible_duplicate" && request.ConfirmNotDuplicate)).ToList();
        if (blocking.Count > 0) throw BlockerException(blocking[0], ctx);

        var customer = ctx.Customer!;
        await LockCustomerAndInvoicesAsync(conn, tx, tenantId, customer.Id, ct);
        return await ConfirmDiscountCoreAsync(conn, tx, tenantId, departmentId, SourceTypes.BankStatementLine, voucherId, lineId, customer.Id,
            new DiscountReceipt(tenantId, departmentId, ctx.Line.TxDate, ctx.Line.Amount, ctx.Line.Currency), request, actorId, ct);
    }

    private static async Task LockCustomerAndInvoicesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid customerId, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition(
            "SELECT 1 FROM sim_customers WHERE id=@customerId AND tenant_id=@tenantId FOR UPDATE",
            new { customerId, tenantId }, tx, cancellationToken: ct));
        await LoadOpenItemsAsync(conn, tx, tenantId, CashDirections.MoneyIn, customerId, lockRows: true, ct);
    }

    /// <summary>
    /// Shared confirmation core. Re-evaluates from authoritative data under
    /// the locks, rejects a stale proposal, enforces the accounting-policy
    /// boundary, and records the attempt. In this version it NEVER writes a
    /// discount: with no approved accounting policy every valid confirmation
    /// stops at payment_discount_accounting_policy_missing (the ADMIN can
    /// still settle the cash normally, without discount).
    /// </summary>
    private static async Task<SettlementMutationResult?> ConfirmDiscountCoreAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid departmentId, string sourceType, Guid voucherId,
        Guid? bankTransactionId, Guid customerId, DiscountReceipt receipt, ConfirmPaymentDiscountRequest request, Guid actorId, CancellationToken ct)
    {
        var found = await EvaluateDiscountsAsync(conn, tx, tenantId, customerId, request.InvoiceId, receipt, lockContracts: true, ct);
        if (found.Count == 0)
            throw new CashSettlementException(PaymentDiscountConflicts.InvoiceNotOpen, "Hoá đơn không còn công nợ mở của khách hàng này.");
        var e = found[0].Evaluation;

        string outcome, code, message;
        if (request.OpenAmount != e.OpenAmount)
            (outcome, code, message) = ("rejected_stale", PaymentDiscountConflicts.InvoiceBalanceChanged, "Công nợ của hoá đơn đã thay đổi — tải lại đề xuất.");
        else if (e.TermId is not null && (request.TermId != e.TermId || request.TermRevision != e.TermRevision))
            (outcome, code, message) = ("rejected_stale", PaymentDiscountConflicts.TermsChanged, "Điều khoản chiết khấu của hợp đồng đã thay đổi — tải lại đề xuất.");
        else if (!e.Eligible)
            (outcome, code, message) = ("rejected_not_eligible", PaymentDiscountConflicts.EligibilityChanged, "Khoản thu không (còn) đủ điều kiện chiết khấu.");
        else if (request.DiscountAmount != e.CalculatedDiscount)
            (outcome, code, message) = ("rejected_stale", PaymentDiscountConflicts.EligibilityChanged, "Số chiết khấu không khớp tính toán của hệ thống — tải lại đề xuất.");
        else if (PaymentDiscountAccountingPolicy.ResolveDiscountDebitAccount() is null)
            (outcome, code, message) = ("blocked_accounting_policy_missing", PaymentDiscountAccountingPolicy.MissingCode,
                "Chưa có chính sách hạch toán chiết khấu thanh toán được kế toán phê duyệt — chưa thể áp dụng chiết khấu. Có thể ghi nhận khoản thu không kèm chiết khấu.");
        else
            throw new InvalidOperationException("payment discount write path is not implemented without an approved accounting policy");

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO payment_discount_decisions(tenant_id, department_id, source_type, voucher_id, bank_transaction_id, ar_invoice_id,
                contract_id, term_id, term_revision, rate_percent, basis, basis_date, qualifying_deadline, receipt_date,
                cash_amount, open_amount, calculated_discount, eligible, reason_code, outcome, conflict_code, actor_id)
            VALUES (@tenantId, @departmentId, @sourceType, @voucherId, @bankTransactionId, @InvoiceId,
                @ContractId, @TermId, @TermRevision, @RatePercent, @Basis, @BasisDate, @QualifyingDeadline, @ReceiptDate,
                @CashAvailable, @OpenAmount, @CalculatedDiscount, @eligible, @ReasonCode, @outcome, @code, @actorId)
            """, new
        {
            tenantId, departmentId, sourceType, voucherId, bankTransactionId, e.InvoiceId, e.ContractId, e.TermId, e.TermRevision,
            e.RatePercent, e.Basis, e.BasisDate, e.QualifyingDeadline, e.ReceiptDate, e.CashAvailable, e.OpenAmount,
            e.CalculatedDiscount, eligible = e.Eligible, e.ReasonCode, outcome, code, actorId,
        }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);   // only the audit row; no settlement effect
        throw new CashSettlementException(code, message, new PaymentDiscountConflictDetails(
            e.ReasonCode, e, code == PaymentDiscountAccountingPolicy.MissingCode ? PaymentDiscountAccountingPolicy.MissingDecisions : null));
    }
}

public sealed record PaymentDiscountConflictDetails(
    string ReasonCode, PaymentDiscountEvaluation Evaluation, IReadOnlyList<string>? MissingPolicy);
