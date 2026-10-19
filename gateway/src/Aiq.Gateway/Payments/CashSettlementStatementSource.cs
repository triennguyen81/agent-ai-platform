using System.Text.Json;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Payments;

// ============================================================================
// Bank statement money_in line -> the SAME canonical cash settlement used by
// Phiếu thu (PostCoreAsync / ApplyOneAsync / UnapplyInternalAsync /
// ReverseCoreAsync). Only the source differs: cash_transactions.source_type
// = 'bank_statement_line' + source_bank_transaction_id, at most one live
// posting per line (unique index). Nothing here is a second accounting
// engine — it only gathers the line's facts, enforces eligibility, and calls
// the shared core.
// ============================================================================

public static class StatementAccountingStatuses
{
    public const string NotPosted = "NOT_POSTED";
    public const string Posted = "POSTED";
    public const string Reconciled = "RECONCILED";   // accounted by another source (e.g. the Phiếu thu)
    public const string Reversed = "REVERSED";
}

public static class InvoiceMatchStatuses
{
    public const string AutoMatched = "AUTO_MATCHED"; // still only a suggestion — ADMIN approves
    public const string NeedsReview = "NEEDS_REVIEW";
    public const string NoMatch = "NO_MATCH";
}

public sealed record StatementLineInfo(
    Guid Id, Guid VoucherId, int? LineNo, DateOnly TxDate, string? BankReference, string Description,
    string Direction, decimal Amount, string Currency, string? CounterpartyAccount, string? CounterpartyName,
    string? MatchStatus, string? MatchMethod, Guid? CustomerId, string? CompanyAccountNumber, Guid? CompanyBankAccountId,
    Guid? ReconciledCashTransactionId, string SettlementEvidenceJson);

public sealed record CompanyBankAccountInfo(Guid Id, string AccountNumber, string BankName, string GlAccountCode, bool IsPrimary);

public sealed record ReferenceProblem(string Reference, string Problem, string? OwnerName, string? Status);

public sealed record InvoiceMatchView(
    string Status, MatchSuggestion? Suggestion, IReadOnlyList<string> ExplicitReferences,
    IReadOnlyList<ReferenceProblem> ReferenceProblems, string? Message);

public sealed record DuplicateCandidate(
    Guid CashTransactionId, string Kind, string SourceType, Guid VoucherId, string DocumentNumber,
    DateOnly TransactionDate, decimal Amount, string? DocumentReference, string? BankReference, string Reason);

public sealed record AppliedInvoiceState(Guid InvoiceId, string DocumentNumber, decimal OriginalAmount, decimal PaidAmount, decimal Outstanding, string SettlementStatus);

public sealed record StatementSettlementContext(
    StatementLineInfo Line,
    string AccountingStatus,
    string? AllocationStatus,
    SettlementCounterparty? Customer,
    CompanyBankAccountInfo? CompanyBankAccount,
    IReadOnlyList<SettlementOpenItem> OpenItems,
    InvoiceMatchView InvoiceMatch,
    CashTransactionRecord? Posting,
    IReadOnlyList<PaymentApplicationRecord> Applications,
    IReadOnlyList<CashTransactionRecord> ReversedPostings,
    CashTransactionRecord? ReconciledWith,
    IReadOnlyList<DuplicateCandidate> Duplicates,
    IReadOnlyList<JournalLine> ProposedJournal,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<AppliedInvoiceState> AppliedInvoices,
    IReadOnlyList<Aiq.Gateway.Contracts.ContractScheduleImpact> ContractSchedules,
    IReadOnlyList<PaymentDiscountProposal> DiscountProposals);

public sealed record PostStatementLineRequest(IReadOnlyList<AllocationInput>? Allocations, bool LeaveUnapplied, bool ConfirmNotDuplicate);

public interface IStatementSettlement
{
    Task<StatementSettlementContext?> GetStatementLineContextAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid lineId, CancellationToken ct);
    Task<SettlementMutationResult?> PostStatementLineAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid lineId, Guid actorId, PostStatementLineRequest request, CancellationToken ct);
    Task<SettlementMutationResult?> AllocateStatementLineAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid lineId, Guid actorId, IReadOnlyList<AllocationInput> allocations, CancellationToken ct);
    Task<SettlementMutationResult?> UnapplyStatementLineAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid lineId, Guid actorId, Guid? applicationId, string? reason, CancellationToken ct);
    Task<SettlementMutationResult?> ReverseStatementLineAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid lineId, Guid actorId, string? reason, CancellationToken ct);
    /// <summary>Link the line to an EXISTING posting (no new accounting) after ADMIN review.</summary>
    /// <summary>ADMIN confirmation of a payment-discount proposal for a money_in line (same core as a Phiếu thu).</summary>
    Task<SettlementMutationResult?> ConfirmStatementLinePaymentDiscountAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid lineId, Guid actorId, ConfirmPaymentDiscountRequest request, CancellationToken ct);
    Task<StatementSettlementContext?> ReconcileStatementLineAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid lineId, Guid actorId, Guid cashTransactionId, CancellationToken ct);
    Task<IReadOnlyList<CompanyBankAccountInfo>> ListCompanyBankAccountsAsync(Guid tenantId, CancellationToken ct);
    /// <summary>Link a statement's own account to an existing company bank account (sim_bank_accounts). Returns lines updated, null if out of scope.</summary>
    Task<int?> LinkCompanyBankAccountAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid bankAccountId, Guid actorId, CancellationToken ct);
}

public sealed partial class CashSettlementRepository : IStatementSettlement
{
    private const int DuplicateWindowDays = 3;

    // ------------------------------------------------------------------
    // Context
    // ------------------------------------------------------------------

    public async Task<StatementSettlementContext?> GetStatementLineContextAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid lineId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await LoadStatementContextAsync(conn, null, tenantId, departmentId, voucherId, lineId, ct);
    }

    private async Task<StatementSettlementContext?> LoadStatementContextAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, Guid departmentId, Guid voucherId, Guid lineId, CancellationToken ct)
    {
        var line = await conn.QuerySingleOrDefaultAsync<StatementLineInfo>(new CommandDefinition("""
            SELECT t.id AS Id, t.voucher_id AS VoucherId, t.line_no AS LineNo, t.tx_date AS TxDate, t.tx_ref AS BankReference,
                   t.description AS Description, t.direction AS Direction, t.amount AS Amount, t.currency AS Currency,
                   t.counterparty_account AS CounterpartyAccount, t.counterparty_name AS CounterpartyName,
                   t.match_status AS MatchStatus, t.match_method AS MatchMethod, t.customer_id AS CustomerId,
                   t.company_account_number AS CompanyAccountNumber, t.account_id AS CompanyBankAccountId,
                   t.reconciled_cash_transaction_id AS ReconciledCashTransactionId, t.settlement_evidence::text AS SettlementEvidenceJson
            FROM sim_bank_transactions t JOIN vouchers v ON v.id = t.voucher_id
            WHERE t.id=@lineId AND t.voucher_id=@voucherId AND t.tenant_id=@tenantId AND t.department_id=@departmentId
              AND v.tenant_id=@tenantId AND v.department_id=@departmentId
            """, new { lineId, voucherId, tenantId, departmentId }, tx, cancellationToken: ct));
        if (line is null) return null;

        var blockers = new List<string>();
        var moneyIn = line.Direction == CashDirections.MoneyIn;
        if (!moneyIn) blockers.Add("not_money_in");
        // The statement document's own review/extraction blockers: lines can
        // still be extracted, matched and viewed, but not settled.
        blockers.AddRange(DocumentGateBlockers(
            await Aiq.Gateway.Vouchers.DocumentProcessing.DocumentAccountingGate.LoadReasonsAsync(conn, tx, tenantId, voucherId, ct) ?? []));

        // Own postings (this line as the source) + reconciliation link.
        var postings = (await conn.QueryAsync<CashTransactionRecord>(new CommandDefinition($"""
            SELECT {PostingColumns} FROM cash_transactions c
            WHERE c.source_bank_transaction_id=@lineId AND c.tenant_id=@tenantId
            ORDER BY c.posted_at DESC
            """, new { lineId, tenantId }, tx, cancellationToken: ct))).ToList();
        var live = postings.FirstOrDefault(p => p.Status == "posted");
        var applications = await LoadApplicationsAsync(conn, tx, postings.Select(p => p.Id).ToArray(), ct);
        var reconciledWith = line.ReconciledCashTransactionId is { } rid ? await LoadPostingByIdAsync(conn, tx, rid, ct) : null;

        var accounting = live is not null ? StatementAccountingStatuses.Posted
            : reconciledWith is not null ? StatementAccountingStatuses.Reconciled
            : postings.Count > 0 ? StatementAccountingStatuses.Reversed
            : StatementAccountingStatuses.NotPosted;
        string? allocation = live is null ? null
            : live.AppliedAmount <= 0m ? "UNAPPLIED"
            : live.UnappliedAmount > 0m ? "PARTIALLY_APPLIED" : "FULLY_APPLIED";

        // Customer: only a MATCHED line (auto or ADMIN-confirmed) can settle AR.
        SettlementCounterparty? customer = null;
        var customerId = live?.CustomerId ?? reconciledWith?.CustomerId ?? line.CustomerId;
        if (moneyIn && customerId is { } cid)
            customer = await FindCounterpartyByIdAsync(conn, tx, tenantId, departmentId, CashDirections.MoneyIn, cid, ct);
        if (moneyIn && live is null && reconciledWith is null)
        {
            if (line.MatchStatus != "matched" || customer is null) blockers.Add("customer_not_matched");
        }

        // MY_COMPANY's bank account: must resolve to sim_bank_accounts.
        CompanyBankAccountInfo? company = line.CompanyBankAccountId is { } bid
            ? await conn.QuerySingleOrDefaultAsync<CompanyBankAccountInfo>(new CommandDefinition("""
                SELECT id AS Id, account_number AS AccountNumber, bank_name AS BankName, gl_account_code AS GlAccountCode, COALESCE(is_primary,false) AS IsPrimary
                FROM sim_bank_accounts WHERE id=@bid AND tenant_id=@tenantId
                """, new { bid, tenantId }, tx, cancellationToken: ct))
            : null;
        if (moneyIn && live is null && reconciledWith is null)
        {
            if (company is null) blockers.Add("company_bank_account_not_linked");
            else if (!company.GlAccountCode.StartsWith("112", StringComparison.Ordinal)) blockers.Add("company_bank_account_not_bank_gl");
            if (!string.Equals(line.Currency, "VND", StringComparison.OrdinalIgnoreCase)) blockers.Add("foreign_currency_not_supported");
        }

        // Open AR (source of truth = sim_ar_ledger invoice rows) + invoice match.
        var openItems = customer is null ? new List<SettlementOpenItem>()
            : await LoadOpenItemsAsync(conn, tx, tenantId, CashDirections.MoneyIn, customer.Id, lockRows: false, ct);
        var invoiceMatch = customer is null
            ? new InvoiceMatchView(InvoiceMatchStatuses.NoMatch, null, Array.Empty<string>(), Array.Empty<ReferenceProblem>(), null)
            : await BuildInvoiceMatchAsync(conn, tx, tenantId, departmentId, line, customer, openItems,
                live?.UnappliedAmount ?? line.Amount, ct);

        // Double-posting protection against other sources (Phiếu thu, other lines).
        var duplicates = moneyIn && live is null && reconciledWith is null && customer is not null
            ? await FindDuplicatesAsync(conn, tx, tenantId, departmentId, line, customer.Id, ct)
            : new List<DuplicateCandidate>();
        if (duplicates.Any(d => d.Kind == "exact")) blockers.Add("already_recorded_elsewhere");
        else if (duplicates.Count > 0) blockers.Add("possible_duplicate");

        var journal = new List<JournalLine>();
        if (moneyIn && live is null && company is not null)
            journal.Add(BuildJournalLine(CashDirections.MoneyIn, company.GlAccountCode, line.Amount, customer?.Name));

        // Current state of every invoice this line's money touched (AR ledger is the truth).
        var touched = applications.Select(a => a.InvoiceId).Distinct().ToArray();
        var appliedInvoices = touched.Length == 0 ? new List<AppliedInvoiceState>()
            : (await conn.QueryAsync<AppliedInvoiceRow>(new CommandDefinition("""
                SELECT id AS Id, document_number AS Doc, debit AS Original, paid_amount AS Paid, due_date AS Due
                FROM sim_ar_ledger WHERE id = ANY(@touched) AND tenant_id=@tenantId
                """, new { touched, tenantId }, tx, cancellationToken: ct)))
              .Select(r => new AppliedInvoiceState(r.Id, r.Doc, r.Original, r.Paid, r.Original - r.Paid,
                  CashSettlementRules.SettlementStatus(r.Original, r.Paid, r.Due, Today))).ToList();

        return new StatementSettlementContext(line, accounting, allocation, customer, company, openItems, invoiceMatch,
            live, applications, postings.Where(p => p.Status == "reversed").ToList(), reconciledWith, duplicates, journal, blockers,
            appliedInvoices,
            moneyIn ? await LoadContractSchedulesAsync(conn, tx, tenantId, departmentId, live, applications, ct)
                    : Array.Empty<Aiq.Gateway.Contracts.ContractScheduleImpact>(),
            // Same calculator as a Phiếu thu; receipt date = the bank transaction date.
            moneyIn && live is null && reconciledWith is null && customer is not null
                ? await LoadDiscountProposalsAsync(conn, tx, tenantId, customer.Id,
                    new DiscountReceipt(tenantId, departmentId, line.TxDate, line.Amount, line.Currency), ct)
                : []);
    }

    private sealed record AppliedInvoiceRow(Guid Id, string Doc, decimal Original, decimal Paid, DateOnly? Due);

    private sealed record KnownInvoice(Guid Id, string DocumentNumber, Guid CustomerId, string CustomerName, decimal Original, decimal Paid, DateOnly? DueDate);

    private static async Task<InvoiceMatchView> BuildInvoiceMatchAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, Guid departmentId,
        StatementLineInfo line, SettlementCounterparty customer, List<SettlementOpenItem> openItems, decimal amountToMatch, CancellationToken ct)
    {
        // Every AR invoice number in the department (any customer, any status):
        // what the description NAMES matters even when it is closed/foreign.
        var known = (await conn.QueryAsync<KnownInvoice>(new CommandDefinition("""
            SELECT l.id AS Id, l.document_number AS DocumentNumber, l.customer_id AS CustomerId, c.name AS CustomerName,
                   l.debit AS Original, l.paid_amount AS Paid, l.due_date AS DueDate
            FROM sim_ar_ledger l JOIN sim_customers c ON c.id = l.customer_id
            WHERE l.tenant_id=@tenantId AND c.department_id=@departmentId AND l.document_type='invoice'
            """, new { tenantId, departmentId }, tx, cancellationToken: ct))).ToList();
        var explicitRefs = CashSettlementRules.ExplicitInvoiceReferences(line.Description, known.Select(k => k.DocumentNumber));
        var openNumbers = openItems.Select(o => CashSettlementRules.NormalizeDocNumber(o.DocumentNumber)).ToHashSet();
        var refsHittingOpen = explicitRefs.Where(r => openNumbers.Contains(CashSettlementRules.NormalizeDocNumber(r))).ToList();

        var problems = new List<ReferenceProblem>();
        foreach (var r in explicitRefs.Except(refsHittingOpen))
        {
            var k = known.FirstOrDefault(i => CashSettlementRules.NormalizeDocNumber(i.DocumentNumber) == CashSettlementRules.NormalizeDocNumber(r));
            problems.Add(k is null ? new ReferenceProblem(r, "not_booked", null, null)
                : k.CustomerId != customer.Id ? new ReferenceProblem(r, "other_customer", k.CustomerName, null)
                : new ReferenceProblem(r, "not_open", k.CustomerName,
                    CashSettlementRules.SettlementStatus(k.Original, k.Paid, k.DueDate, Today)));
        }

        // Canonical matching rules (same as Phiếu thu); an explicit reference
        // that is not an open invoice of THIS customer blocks auto-application.
        var suggestion = CashSettlementRules.SuggestAllocations(
            amountToMatch,
            openItems.Select(o => new OpenItem(o.Id, o.DocumentNumber, o.DocumentDate, o.DueDate, o.OriginalAmount, o.PaidAmount)).ToList(),
            new[] { line.Description },
            refsHittingOpen.Count == 0 && explicitRefs.Count > 0 ? explicitRefs : null);
        var status = suggestion.Certainty switch
        {
            MatchCertainty.Certain => InvoiceMatchStatuses.AutoMatched,
            MatchCertainty.NoOpenItem => InvoiceMatchStatuses.NoMatch,
            _ => InvoiceMatchStatuses.NeedsReview,
        };
        string? message = problems.Count > 0 && refsHittingOpen.Count == 0
            ? $"Chứng từ tham chiếu {string.Join(", ", problems.Select(p => p.Reference))} không còn công nợ phù hợp."
            : null;
        return new InvoiceMatchView(status, suggestion, explicitRefs, problems, message);
    }

    private static async Task<List<DuplicateCandidate>> FindDuplicatesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, Guid departmentId,
        StatementLineInfo line, Guid customerId, CancellationToken ct)
    {
        // Exact: the same bank transaction reference already carried by a
        // posting of any other source. Probable: same customer + amount within
        // a few days (no deterministic identity) — never auto-resolved.
        var rows = (await conn.QueryAsync<DuplicateCandidate>(new CommandDefinition("""
            SELECT c.id AS CashTransactionId,
                   CASE WHEN @bankRef::text IS NOT NULL AND c.bank_reference = @bankRef THEN 'exact' ELSE 'probable' END AS Kind,
                   c.source_type AS SourceType, c.voucher_id AS VoucherId, c.document_number AS DocumentNumber,
                   c.transaction_date AS TransactionDate, c.amount AS Amount, c.document_reference AS DocumentReference,
                   c.bank_reference AS BankReference,
                   CASE WHEN @bankRef::text IS NOT NULL AND c.bank_reference = @bankRef THEN 'Cùng mã giao dịch ngân hàng'
                        ELSE 'Cùng khách hàng, cùng số tiền, ngày gần nhau' END AS Reason
            FROM cash_transactions c
            WHERE c.tenant_id=@tenantId AND c.department_id=@departmentId AND c.status='posted' AND c.direction='money_in'
              AND (c.source_bank_transaction_id IS NULL OR c.source_bank_transaction_id <> @lineId)
              AND NOT EXISTS (SELECT 1 FROM sim_bank_transactions o WHERE o.reconciled_cash_transaction_id = c.id AND o.id <> @lineId)
              AND (
                (@bankRef::text IS NOT NULL AND c.bank_reference = @bankRef)
                OR (c.customer_id=@customerId AND c.amount=@amount AND abs(c.transaction_date - @txDate) <= @window)
              )
            ORDER BY 2, abs(c.transaction_date - @txDate)
            """, new { tenantId, departmentId, lineId = line.Id, bankRef = string.IsNullOrWhiteSpace(line.BankReference) ? null : line.BankReference,
                       customerId, amount = line.Amount, txDate = line.TxDate, window = DuplicateWindowDays }, tx, cancellationToken: ct))).ToList();
        return rows;
    }

    // ------------------------------------------------------------------
    // Post (canonical core)
    // ------------------------------------------------------------------

    public async Task<SettlementMutationResult?> PostStatementLineAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid lineId,
        Guid actorId, PostStatementLineRequest request, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Lock order: statement voucher -> line -> customer -> invoices.
        var locked = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition("""
            SELECT t.id FROM vouchers v JOIN sim_bank_transactions t ON t.voucher_id = v.id
            WHERE v.id=@voucherId AND v.tenant_id=@tenantId AND v.department_id=@departmentId
              AND t.id=@lineId AND t.tenant_id=@tenantId AND t.department_id=@departmentId
            FOR UPDATE OF v, t
            """, new { voucherId, lineId, tenantId, departmentId }, tx, cancellationToken: ct));
        if (locked is null) return null;

        var ctx = (await LoadStatementContextAsync(conn, tx, tenantId, departmentId, voucherId, lineId, ct))!;
        if (ctx.Posting is { } existing)
        {
            await tx.RollbackAsync(ct);
            return new SettlementMutationResult("already_posted", existing,
                ctx.Applications.Where(a => a.CashTransactionId == existing.Id).ToList(),
                ctx.Customer?.CurrentBalance ?? 0m, ctx.Customer?.CurrentBalance ?? 0m, Array.Empty<JournalLine>());
        }
        if (ctx.ReconciledWith is not null)
            throw new CashSettlementException("already_reconciled", "Giao dịch đã được đối chiếu với một bút toán đã ghi nhận — không ghi nhận lại.");

        ThrowIfDocumentGated(ctx.Blockers);
        var blocking = ctx.Blockers.Where(b => !(b == "possible_duplicate" && request.ConfirmNotDuplicate)).ToList();
        if (blocking.Count > 0) throw BlockerException(blocking[0], ctx);

        var customer = ctx.Customer!;
        var company = ctx.CompanyBankAccount!;
        var line = ctx.Line;
        var previous = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
            "SELECT COALESCE(current_ar,0) FROM sim_customers WHERE id=@id AND tenant_id=@tenantId FOR UPDATE",
            new { id = customer.Id, tenantId }, tx, cancellationToken: ct));
        var lockedItems = await LoadOpenItemsAsync(conn, tx, tenantId, CashDirections.MoneyIn, customer.Id, lockRows: true, ct);
        var plan = PlanAllocations(ctx.InvoiceMatch.Suggestion, ctx.OpenItems, lockedItems, line.Amount,
            new PostCashRequest(null, request.Allocations, request.LeaveUnapplied, "bank"));

        var docNumber = !string.IsNullOrWhiteSpace(line.BankReference) ? line.BankReference!
            : $"SK-{voucherId.ToString("N")[..8]}-{line.LineNo ?? 0}";
        var posted = await PostCoreAsync(conn, tx, new PostingSource(
            tenantId, departmentId, voucherId, SourceTypes.BankStatementLine, lineId, CashDirections.MoneyIn, customer,
            line.Amount, line.TxDate, "bank", company.GlAccountCode, company.Id, docNumber, line.Description,
            ctx.InvoiceMatch.ExplicitReferences.Count == 0 ? null : string.Join(", ", ctx.InvoiceMatch.ExplicitReferences),
            string.IsNullOrWhiteSpace(line.BankReference) ? null : line.BankReference), plan, actorId, ct);
        if (posted is not { } done)
        {
            await tx.RollbackAsync(ct);
            var again = await GetStatementLineContextAsync(tenantId, departmentId, voucherId, lineId, ct);
            return new SettlementMutationResult("already_posted", again?.Posting, again?.Applications ?? Array.Empty<PaymentApplicationRecord>(),
                previous, previous, Array.Empty<JournalLine>());
        }

        // Audit on the line: the automatic suggestion as it was, what ADMIN
        // chose, duplicate override — appended, never overwritten.
        var audit = JsonSerializer.Serialize(new
        {
            action = "posted", cash_transaction_id = done.CashTxId, by = actorId, at = DateTime.UtcNow,
            customer_id = customer.Id, customer_match_method = line.MatchMethod,
            invoice_match_status = ctx.InvoiceMatch.Status, auto_suggestion = ctx.InvoiceMatch.Suggestion,
            explicit_references = ctx.InvoiceMatch.ExplicitReferences, reference_problems = ctx.InvoiceMatch.ReferenceProblems,
            admin_allocations = request.Allocations, leave_unapplied = request.LeaveUnapplied,
            applied = plan.Select(p => new { invoice_id = p.Item.Id, invoice = p.Item.DocumentNumber, amount = p.Amount, method = p.Method }),
            not_duplicate_confirmed = request.ConfirmNotDuplicate && ctx.Duplicates.Count > 0 ? ctx.Duplicates : null,
        });
        await AppendLineEvidenceAsync(conn, tx, lineId, audit, reconciled: true, ct);

        var next = await RecomputeAsync(conn, tx, tenantId, CashDirections.MoneyIn, customer.Id, done.CashTxId, actorId, "statement_line_posted", ct);
        await tx.CommitAsync(ct);
        return await ResultAsync("posted", tenantId, departmentId, voucherId, done.CashTxId, previous, next, new[] { done.Line }, ct);
    }

    private static CashSettlementException BlockerException(string blocker, StatementSettlementContext ctx) => blocker switch
    {
        "not_money_in" => new("not_money_in", "Giao dịch tiền ra — chưa hỗ trợ đối trừ công nợ phải trả trong giai đoạn này."),
        "customer_not_matched" => new("customer_not_matched", "Chưa xác định khách hàng (cần MATCHED hoặc ADMIN xác nhận)."),
        "company_bank_account_not_linked" => new("company_bank_account_not_linked", "Chưa liên kết tài khoản ngân hàng của công ty."),
        "company_bank_account_not_bank_gl" => new("company_bank_account_not_bank_gl", "Tài khoản ngân hàng của công ty chưa gắn TK kế toán 112x."),
        "foreign_currency_not_supported" => new("foreign_currency_not_supported", "Chưa hỗ trợ ngoại tệ (không có nguồn tỷ giá)."),
        "already_recorded_elsewhere" => new("already_recorded_elsewhere", "Đã ghi nhận kế toán (cùng mã giao dịch ngân hàng).", new { ctx.Duplicates }),
        "possible_duplicate" => new("possible_duplicate", "Có giao dịch đã ghi nhận có thể trùng.", new { ctx.Duplicates }),
        _ => new(blocker, blocker),
    };

    private static Task AppendLineEvidenceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid lineId, string entryJson, bool? reconciled, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition("""
            UPDATE sim_bank_transactions SET
                settlement_evidence = jsonb_set(settlement_evidence, '{history}',
                    COALESCE(settlement_evidence->'history', '[]'::jsonb) || jsonb_build_array(@entry::jsonb), true),
                reconciled = COALESCE(@reconciled, reconciled)
            WHERE id=@lineId
            """, new { lineId, entry = entryJson, reconciled }, tx, cancellationToken: ct));

    // ------------------------------------------------------------------
    // Allocate / unapply / reverse — the canonical cores, keyed by the line
    // ------------------------------------------------------------------

    public async Task<SettlementMutationResult?> AllocateStatementLineAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid lineId,
        Guid actorId, IReadOnlyList<AllocationInput> allocations, CancellationToken ct)
    {
        var r = await AllocateCoreAsync(tenantId, departmentId, voucherId, lineId, actorId, allocations, ct);
        if (r is { Outcome: "allocated" }) await AuditAsync(lineId, new { action = "allocated", by = actorId, at = DateTime.UtcNow, allocations }, ct);
        return r;
    }

    public async Task<SettlementMutationResult?> UnapplyStatementLineAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid lineId,
        Guid actorId, Guid? applicationId, string? reason, CancellationToken ct)
    {
        var r = await UnapplyCoreAsync(tenantId, departmentId, voucherId, lineId, actorId, applicationId, reason, ct);
        if (r is { Outcome: "unapplied" }) await AuditAsync(lineId, new { action = "unapplied", by = actorId, at = DateTime.UtcNow, application_id = applicationId, reason }, ct);
        return r;
    }

    public async Task<SettlementMutationResult?> ReverseStatementLineAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid lineId,
        Guid actorId, string? reason, CancellationToken ct)
    {
        var r = await ReverseCoreAsync(tenantId, departmentId, voucherId, lineId, actorId, reason, ct);
        if (r is { Outcome: "reversed" }) await AuditAsync(lineId, new { action = "reversed", by = actorId, at = DateTime.UtcNow, cash_transaction_id = r.Posting?.Id, reason }, ct);
        return r;
    }

    private async Task AuditAsync(Guid lineId, object entry, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await AppendLineEvidenceAsync(conn, tx, lineId, JsonSerializer.Serialize(entry), null, ct);
        await tx.CommitAsync(ct);
    }

    // ------------------------------------------------------------------
    // Reconcile with an existing posting (no new accounting)
    // ------------------------------------------------------------------

    public async Task<StatementSettlementContext?> ReconcileStatementLineAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid lineId,
        Guid actorId, Guid cashTransactionId, CancellationToken ct)
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
        if (ctx.Posting is not null || ctx.ReconciledWith is not null)
            throw new CashSettlementException("already_accounted", "Giao dịch đã được ghi nhận/đối chiếu.");
        ThrowIfDocumentGated(ctx.Blockers);
        // Only a posting the duplicate check itself surfaced (same customer &
        // amount, or same bank reference) — never an arbitrary transaction.
        var candidate = ctx.Duplicates.FirstOrDefault(d => d.CashTransactionId == cashTransactionId)
            ?? throw new CashSettlementException("not_a_reconciliation_candidate", "Giao dịch đã ghi nhận này không khớp để đối chiếu.");
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE sim_bank_transactions SET reconciled_cash_transaction_id=@cashTransactionId, reconciled_by=@actorId, reconciled_at=NOW()
            WHERE id=@lineId
            """, new { lineId, cashTransactionId, actorId }, tx, cancellationToken: ct));
        await AppendLineEvidenceAsync(conn, tx, lineId, JsonSerializer.Serialize(new
        {
            action = "reconciled_with_existing", cash_transaction_id = cashTransactionId, kind = candidate.Kind,
            source_type = candidate.SourceType, document_number = candidate.DocumentNumber, by = actorId, at = DateTime.UtcNow,
        }), reconciled: true, ct);
        await tx.CommitAsync(ct);
        return await GetStatementLineContextAsync(tenantId, departmentId, voucherId, lineId, ct);
    }

    // ------------------------------------------------------------------
    // Company bank account (existing sim_bank_accounts only)
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<CompanyBankAccountInfo>> ListCompanyBankAccountsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryAsync<CompanyBankAccountInfo>(new CommandDefinition("""
            SELECT id AS Id, account_number AS AccountNumber, bank_name AS BankName, gl_account_code AS GlAccountCode, COALESCE(is_primary,false) AS IsPrimary
            FROM sim_bank_accounts WHERE tenant_id=@tenantId ORDER BY is_primary DESC, created_at
            """, new { tenantId }, cancellationToken: ct))).ToList();
    }

    public async Task<int?> LinkCompanyBankAccountAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid bankAccountId, Guid actorId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var inScope = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM vouchers WHERE id=@voucherId AND tenant_id=@tenantId AND department_id=@departmentId AND voucher_type='bank_statement')",
            new { voucherId, tenantId, departmentId }, tx, cancellationToken: ct));
        if (!inScope) return null;
        // The account must be MY_COMPANY's in this tenant — never trusted from the browser.
        var gl = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT gl_account_code FROM sim_bank_accounts WHERE id=@bankAccountId AND tenant_id=@tenantId",
            new { bankAccountId, tenantId }, tx, cancellationToken: ct))
            ?? throw new CashSettlementException("company_bank_account_not_found", "Tài khoản ngân hàng của công ty không tồn tại.");
        // Only lines with no accounting effect yet are re-pointed.
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE sim_bank_transactions t SET account_id=@bankAccountId
            WHERE t.voucher_id=@voucherId AND t.tenant_id=@tenantId AND t.department_id=@departmentId
              AND t.reconciled_cash_transaction_id IS NULL
              AND NOT EXISTS (SELECT 1 FROM cash_transactions c WHERE c.source_bank_transaction_id=t.id AND c.status='posted')
            """, new { voucherId, tenantId, departmentId, bankAccountId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE vouchers SET extracted_fields = extracted_fields || jsonb_build_object('company_bank_account_link',
                jsonb_build_object('bank_account_id', @bankAccountId, 'gl_account_code', @gl, 'by', @actorId, 'at', NOW()))
            WHERE id=@voucherId
            """, new { voucherId, bankAccountId, gl, actorId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return n;
    }
}
