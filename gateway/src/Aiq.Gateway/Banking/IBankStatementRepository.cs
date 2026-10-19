using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aiq.Gateway.Data;
using Aiq.Gateway.Payments;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Banking;

// ============================================================================
// Bank statement lines (sim_bank_transactions) — import, identify Customer /
// Supplier candidate, ADMIN review. Scoped by (tenant, department) from the
// authenticated principal. NEVER posts journals, changes AR/AP, or creates
// payment applications — identification only.
// ============================================================================

public sealed record StatementImportSummary(
    int Lines, int Inserted, int Duplicates, IReadOnlyList<Guid> DuplicateOfVouchers,
    Guid? CompanyBankAccountId, string? CompanyAccountNumber, string Source, IReadOnlyList<string> Warnings);

public sealed record StatementTransactionRecord(
    Guid Id, Guid? VoucherId, int? LineNo, DateOnly TxDate, string? BankReference, string Description,
    string Direction, decimal Amount, decimal Debit, decimal Credit, decimal? BalanceAfter, string Currency,
    string? CounterpartyAccount, string? CounterpartyAccountNormalized, string? CounterpartyName,
    string? CounterpartyBankCode, string? CounterpartyBankName,
    Guid? CustomerId, string? CustomerName, Guid? SupplierId, string? SupplierName,
    Guid? CounterpartyBankAccountId, string? MatchStatus, string? MatchMethod, decimal? MatchConfidence,
    string MatchEvidenceJson, Guid? ConfirmedBy, DateTime? ConfirmedAt,
    bool CounterpartyAccountKnownForOwner,
    Guid? CompanyBankAccountId, string AccountingStatus, Guid? CashTransactionId);

public sealed record ManualMatchResult(StatementTransactionRecord Transaction, BankAccountRecord? LearnedAccount);

public interface IBankStatementRepository
{
    Task<StatementImportSummary> ImportAsync(Guid tenantId, Guid departmentId, Guid voucherId, ParsedStatement statement, CancellationToken ct);
    Task<IReadOnlyList<StatementTransactionRecord>?> ListAsync(Guid tenantId, Guid departmentId, Guid voucherId, CancellationToken ct);
    /// <summary>Re-run the matcher for every line not confirmed by ADMIN.</summary>
    Task<int?> RematchAsync(Guid tenantId, Guid departmentId, Guid voucherId, CancellationToken ct);
    Task<ManualMatchResult?> ConfirmMatchAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid transactionId,
        Guid actorId, Guid ownerId, bool saveBankAccount, CancellationToken ct);
}

public sealed class BankStatementRepository(NpgsqlConnectionFactory factory, IBankAccountRepository bankAccounts) : IBankStatementRepository
{
    public static readonly JsonSerializerOptions Json = new();

    // ------------------------------------------------------------------
    // Import
    // ------------------------------------------------------------------

    /// <summary>
    /// Deterministic line identity. The bank's own transaction/reference id
    /// wins; otherwise date+direction+amount+balance+counterparty+description
    /// plus the occurrence index of identical lines within the statement
    /// (two genuinely identical transfers on one day stay two rows, and the
    /// same file re-imported yields the same fingerprints).
    /// </summary>
    public static string Fingerprint(Guid tenantId, string? companyAccount, StatementLine l, int occurrence)
    {
        var key = !string.IsNullOrWhiteSpace(l.BankReference)
            ? $"ref|{tenantId}|{companyAccount}|{l.BankReference!.Trim().ToUpperInvariant()}|{l.Direction}|{l.Amount:0.00}"
            : string.Join('|', "row", tenantId, companyAccount, l.Date?.ToString("yyyy-MM-dd"), l.Direction, l.Amount.ToString("0.00"),
                l.BalanceAfter?.ToString("0.00"), BankAccountNormalizer.NormalizeAccount(l.CounterpartyAccount),
                CashSettlementRules.FoldVietnamese(l.Description), occurrence);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    }

    public async Task<StatementImportSummary> ImportAsync(Guid tenantId, Guid departmentId, Guid voucherId, ParsedStatement statement, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var voucherOk = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM vouchers WHERE id=@voucherId AND tenant_id=@tenantId AND department_id=@departmentId)",
            new { voucherId, tenantId, departmentId }, tx, cancellationToken: ct));
        if (!voucherOk) throw new InvalidOperationException("voucher not in scope");

        // The statement's OWN account is MY_COMPANY's (sim_bank_accounts) —
        // never confused with a counterparty account.
        var companyAccount = statement.CompanyAccountNumber is { Length: > 0 } a ? BankAccountNormalizer.NormalizeAccount(a) : null;
        Guid? companyAccountId = companyAccount is null ? null : await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition("""
            SELECT id FROM sim_bank_accounts
            WHERE tenant_id=@tenantId AND regexp_replace(upper(account_number), '[\s.\-_/]', '', 'g') = @companyAccount
            LIMIT 1
            """, new { tenantId, companyAccount }, tx, cancellationToken: ct));

        var inserted = new List<Guid>();
        var duplicates = new List<Guid>();
        var occurrences = new Dictionary<string, int>();
        foreach (var line in statement.Lines)
        {
            var baseKey = Fingerprint(tenantId, companyAccount, line, 0);
            var occurrence = occurrences.TryGetValue(baseKey, out var n) ? n + 1 : 0;
            occurrences[baseKey] = occurrence;
            var fingerprint = occurrence == 0 ? baseKey : Fingerprint(tenantId, companyAccount, line, occurrence);

            var id = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition("""
                INSERT INTO sim_bank_transactions (account_id, tenant_id, department_id, voucher_id, line_no, company_account_number,
                    tx_date, tx_ref, description, debit, credit, balance_after, direction, amount, currency,
                    counterparty_account, counterparty_account_normalized, counterparty_name, counterparty_name_normalized,
                    counterparty_bank_code, counterparty_bank_name, fingerprint)
                VALUES (@companyAccountId, @tenantId, @departmentId, @voucherId, @lineNo, @companyAccount,
                    @txDate, @reference, @description, @debit, @credit, @balance, @direction, @amount, @currency,
                    @cpAccount, @cpAccountNorm, @cpName, @cpNameNorm, @cpBankCode, @cpBankName, @fingerprint)
                ON CONFLICT (tenant_id, fingerprint) WHERE fingerprint IS NOT NULL DO NOTHING
                RETURNING id
                """, new
            {
                companyAccountId, tenantId, departmentId, voucherId, lineNo = line.LineNo, companyAccount,
                txDate = line.Date ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)),
                reference = line.BankReference, description = line.Description,
                debit = line.Debit, credit = line.Credit, balance = line.BalanceAfter,
                direction = line.Direction, amount = line.Amount, currency = line.Currency,
                cpAccount = line.CounterpartyAccount,
                cpAccountNorm = NullIfEmpty(BankAccountNormalizer.NormalizeAccount(line.CounterpartyAccount)),
                cpName = line.CounterpartyName,
                cpNameNorm = NullIfEmpty(BankAccountNormalizer.NormalizeName(line.CounterpartyName)),
                cpBankCode = line.CounterpartyBankCode, cpBankName = line.CounterpartyBankName, fingerprint,
            }, tx, cancellationToken: ct));
            if (id is { } newId) inserted.Add(newId);
            else
            {
                var dupVoucher = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                    "SELECT voucher_id FROM sim_bank_transactions WHERE tenant_id=@tenantId AND fingerprint=@fingerprint",
                    new { tenantId, fingerprint }, tx, cancellationToken: ct));
                if (dupVoucher is { } dv) duplicates.Add(dv);
            }
        }

        foreach (var id in inserted) await MatchOneAsync(conn, tx, tenantId, departmentId, id, ct);
        await tx.CommitAsync(ct);

        return new StatementImportSummary(statement.Lines.Count, inserted.Count, statement.Lines.Count - inserted.Count,
            duplicates.Distinct().Where(d => d != voucherId).ToList(), companyAccountId, companyAccount, statement.Source, statement.Warnings);
    }

    // ------------------------------------------------------------------
    // Matching
    // ------------------------------------------------------------------

    private sealed record LineRow(Guid Id, string Direction, decimal Amount, string Description, string? CounterpartyAccountNormalized,
        string? CounterpartyBankCode, string? CounterpartyNameNormalized, Guid? ConfirmedBy);

    private sealed record PartyRow(Guid Id, string Code, string Name, string? Mst);

    private static async Task MatchOneAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid departmentId, Guid lineId, CancellationToken ct)
    {
        var line = await conn.QuerySingleAsync<LineRow>(new CommandDefinition("""
            SELECT id AS Id, direction AS Direction, amount AS Amount, description AS Description,
                   counterparty_account_normalized AS CounterpartyAccountNormalized, counterparty_bank_code AS CounterpartyBankCode,
                   counterparty_name_normalized AS CounterpartyNameNormalized, confirmed_by AS ConfirmedBy
            FROM sim_bank_transactions WHERE id=@lineId FOR UPDATE
            """, new { lineId }, tx, cancellationToken: ct));
        if (line.ConfirmedBy is not null) return; // ADMIN decision is final until ADMIN changes it
        if (await LineAccountedAsync(conn, tx, lineId, ct)) return; // posted/reconciled: identity is frozen

        var result = CounterpartyMatcher.Match(await GatherInputsAsync(conn, tx, tenantId, departmentId, line, ct));
        var isCustomer = result.OwnerType == BankOwnerTypes.Customer;
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE sim_bank_transactions SET
                customer_id = @customerId, supplier_id = @supplierId, counterparty_bank_account_id = @bankAccountId,
                match_status = @status, match_method = @method, match_confidence = @confidence,
                match_evidence = jsonb_build_object('auto', @evidence::jsonb)
                                 || CASE WHEN match_evidence ? 'decisions' THEN jsonb_build_object('decisions', match_evidence->'decisions') ELSE '{}'::jsonb END,
                matched_by = NULL, matched_at = NOW()
            WHERE id=@lineId
            """, new
        {
            lineId,
            customerId = isCustomer && result.Status == MatchStatuses.Matched ? result.OwnerId : null,
            supplierId = !isCustomer && result.Status == MatchStatuses.Matched ? result.OwnerId : null,
            bankAccountId = result.Status == MatchStatuses.Matched ? result.BankAccountId : null,
            status = result.Status, method = result.Method, confidence = result.Confidence,
            evidence = JsonSerializer.Serialize(result, Json),
        }, tx, cancellationToken: ct));
    }

    /// <summary>A line with a live posting or a reconciliation link must keep its customer.</summary>
    private static Task<bool> LineAccountedAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid lineId, CancellationToken ct) =>
        conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM cash_transactions c WHERE c.source_bank_transaction_id=@lineId AND c.status='posted')
                OR EXISTS (SELECT 1 FROM sim_bank_transactions t WHERE t.id=@lineId AND t.reconciled_cash_transaction_id IS NOT NULL)
            """, new { lineId }, tx, cancellationToken: ct));

    private static async Task<MatchInputs> GatherInputsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, Guid departmentId,
        LineRow line, CancellationToken ct)
    {
        var ownerType = line.Direction == "money_in" ? BankOwnerTypes.Customer : BankOwnerTypes.Supplier;
        var (table, codeCol, ledger, amountCol, fkCol) = ownerType == BankOwnerTypes.Customer
            ? ("sim_customers", "customer_code", "sim_ar_ledger", "debit", "customer_id")
            : ("sim_suppliers", "supplier_code", "sim_ap_ledger", "credit", "supplier_id");
        var scope = new { tenantId, departmentId };

        // 1. Every ACTIVE master account with this exact number (both owner
        //    types — a supplier's account paying us in is worth a warning).
        var accountHits = string.IsNullOrEmpty(line.CounterpartyAccountNormalized)
            ? new List<AccountOwnerHit>()
            : (await conn.QueryAsync<AccountOwnerHit>(new CommandDefinition("""
                SELECT a.id AS AccountId, a.owner_type AS OwnerType, COALESCE(a.customer_id, a.supplier_id) AS OwnerId,
                       COALESCE(c.name, s.name) AS OwnerName, a.bank_code AS BankCode, a.account_name_normalized AS AccountNameNormalized
                FROM counterparty_bank_accounts a
                LEFT JOIN sim_customers c ON c.id = a.customer_id
                LEFT JOIN sim_suppliers s ON s.id = a.supplier_id
                WHERE a.tenant_id=@tenantId AND a.department_id=@departmentId AND a.is_active
                  AND a.account_number_normalized=@acct
                """, new { tenantId, departmentId, acct = line.CounterpartyAccountNormalized }, tx, cancellationToken: ct))).ToList();

        var parties = (await conn.QueryAsync<PartyRow>(new CommandDefinition($"""
            SELECT id AS Id, {codeCol} AS Code, name AS Name, mst AS Mst FROM {table}
            WHERE tenant_id=@tenantId AND department_id=@departmentId AND COALESCE(status,'active') <> 'inactive'
            """, scope, tx, cancellationToken: ct))).ToList();
        MatchParty P(PartyRow r) => new(r.Id, r.Code, r.Name, r.Mst);

        // 2. Tax codes written in the transfer description.
        var taxCodes = Regex.Matches(line.Description ?? "", @"(?<![0-9])(\d{10}(?:-\d{3})?)(?![0-9])").Select(m => m.Value).ToHashSet();
        var taxHits = parties.Where(p => p.Mst is not null && taxCodes.Contains(CashSettlementRules.NormalizeTaxCode(p.Mst))).Select(P).ToList();

        // 3. Invoice numbers (open/partial) quoted in the description.
        var openInvoices = (await conn.QueryAsync<(string Doc, Guid OwnerId, string OwnerName)>(new CommandDefinition($"""
            SELECT l.document_number, l.{fkCol}, p.name FROM {ledger} l JOIN {table} p ON p.id = l.{fkCol}
            WHERE l.tenant_id=@tenantId AND p.department_id=@departmentId AND l.document_type='invoice' AND l.status IN ('open','partial')
            """, scope, tx, cancellationToken: ct))).ToList();
        var referenced = CashSettlementRules.FindReferencedDocumentNumbers(openInvoices.Select(i => i.Doc), new[] { line.Description }).ToHashSet();
        var invoiceHits = openInvoices.Where(i => referenced.Contains(i.Doc)).Select(i => new InvoiceRefHit(i.Doc, i.OwnerId, i.OwnerName)).ToList();

        // 4. Customer/supplier code in the description.
        var codeRefs = CashSettlementRules.FindReferencedDocumentNumbers(parties.Select(p => p.Code), new[] { line.Description }).ToHashSet();
        var codeHits = parties.Where(p => codeRefs.Contains(p.Code)).Select(P).ToList();

        // 5. Exact normalized legal name; 6. similar name (suggestion only).
        var name = line.CounterpartyNameNormalized;
        var exact = string.IsNullOrEmpty(name) ? new List<MatchParty>()
            : parties.Where(p => BankAccountNormalizer.NormalizeName(p.Name) == name).Select(P).ToList();
        var similar = string.IsNullOrEmpty(name) ? new List<MatchParty>()
            : parties.Where(p => BankAccountNormalizer.NormalizeName(p.Name) is var n && n != name
                                 && CounterpartyMatcher.TokenOverlap(n, name) >= 0.5m).Select(P).ToList();

        // Amount: a SUGGESTION only (never identifies anybody on its own).
        var amountHits = (await conn.QueryAsync<Guid>(new CommandDefinition($"""
            SELECT DISTINCT l.{fkCol} FROM {ledger} l JOIN {table} p ON p.id = l.{fkCol}
            WHERE l.tenant_id=@tenantId AND p.department_id=@departmentId AND l.document_type='invoice'
              AND l.status IN ('open','partial') AND l.{amountCol} - l.paid_amount = @amount
            """, new { tenantId, departmentId, amount = line.Amount }, tx, cancellationToken: ct))).ToHashSet();
        var amountParties = parties.Where(p => amountHits.Contains(p.Id)).Select(P).ToList();

        return new MatchInputs(ownerType, line.CounterpartyAccountNormalized, line.CounterpartyBankCode, name,
            accountHits, taxHits, invoiceHits, codeHits, exact, similar, amountParties);
    }

    public async Task<int?> RematchAsync(Guid tenantId, Guid departmentId, Guid voucherId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (!await VoucherInScopeAsync(conn, tx, tenantId, departmentId, voucherId, ct)) return null;
        var ids = (await conn.QueryAsync<Guid>(new CommandDefinition("""
            SELECT id FROM sim_bank_transactions
            WHERE voucher_id=@voucherId AND tenant_id=@tenantId AND department_id=@departmentId AND confirmed_by IS NULL
            ORDER BY line_no
            """, new { voucherId, tenantId, departmentId }, tx, cancellationToken: ct))).ToList();
        foreach (var id in ids) await MatchOneAsync(conn, tx, tenantId, departmentId, id, ct);
        await tx.CommitAsync(ct);
        return ids.Count;
    }

    // ------------------------------------------------------------------
    // Read
    // ------------------------------------------------------------------

    private const string ListSql = """
        SELECT t.id AS Id, t.voucher_id AS VoucherId, t.line_no AS LineNo, t.tx_date AS TxDate, t.tx_ref AS BankReference,
               t.description AS Description, t.direction AS Direction, t.amount AS Amount, t.debit AS Debit, t.credit AS Credit,
               t.balance_after AS BalanceAfter, t.currency AS Currency, t.counterparty_account AS CounterpartyAccount,
               t.counterparty_account_normalized AS CounterpartyAccountNormalized, t.counterparty_name AS CounterpartyName,
               t.counterparty_bank_code AS CounterpartyBankCode, t.counterparty_bank_name AS CounterpartyBankName,
               t.customer_id AS CustomerId, c.name AS CustomerName, t.supplier_id AS SupplierId, s.name AS SupplierName,
               t.counterparty_bank_account_id AS CounterpartyBankAccountId, t.match_status AS MatchStatus,
               t.match_method AS MatchMethod, t.match_confidence AS MatchConfidence, t.match_evidence::text AS MatchEvidenceJson,
               t.confirmed_by AS ConfirmedBy, t.confirmed_at AS ConfirmedAt,
               (t.counterparty_account_normalized IS NOT NULL AND EXISTS (
                   SELECT 1 FROM counterparty_bank_accounts a
                   WHERE a.tenant_id=t.tenant_id AND a.department_id=t.department_id AND a.is_active
                     AND a.account_number_normalized=t.counterparty_account_normalized
                     AND ((t.customer_id IS NOT NULL AND a.customer_id=t.customer_id) OR (t.supplier_id IS NOT NULL AND a.supplier_id=t.supplier_id))
               )) AS CounterpartyAccountKnownForOwner,
               t.account_id AS CompanyBankAccountId,
               CASE WHEN EXISTS (SELECT 1 FROM cash_transactions c WHERE c.source_bank_transaction_id=t.id AND c.status='posted') THEN 'POSTED'
                    WHEN t.reconciled_cash_transaction_id IS NOT NULL THEN 'RECONCILED'
                    WHEN EXISTS (SELECT 1 FROM cash_transactions c WHERE c.source_bank_transaction_id=t.id) THEN 'REVERSED'
                    ELSE 'NOT_POSTED' END AS AccountingStatus,
               COALESCE((SELECT c.id FROM cash_transactions c WHERE c.source_bank_transaction_id=t.id AND c.status='posted'),
                        t.reconciled_cash_transaction_id) AS CashTransactionId
        FROM sim_bank_transactions t
        LEFT JOIN sim_customers c ON c.id = t.customer_id
        LEFT JOIN sim_suppliers s ON s.id = t.supplier_id
        """;

    public async Task<IReadOnlyList<StatementTransactionRecord>?> ListAsync(Guid tenantId, Guid departmentId, Guid voucherId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        if (!await VoucherInScopeAsync(conn, null, tenantId, departmentId, voucherId, ct)) return null;
        return (await conn.QueryAsync<StatementTransactionRecord>(new CommandDefinition($"""
            {ListSql}
            WHERE t.voucher_id=@voucherId AND t.tenant_id=@tenantId AND t.department_id=@departmentId
            ORDER BY t.line_no, t.tx_date
            """, new { voucherId, tenantId, departmentId }, cancellationToken: ct))).ToList();
    }

    private static Task<bool> VoucherInScopeAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, Guid departmentId, Guid voucherId, CancellationToken ct) =>
        conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM vouchers WHERE id=@voucherId AND tenant_id=@tenantId AND department_id=@departmentId)",
            new { voucherId, tenantId, departmentId }, tx, cancellationToken: ct));

    // ------------------------------------------------------------------
    // ADMIN review
    // ------------------------------------------------------------------

    public async Task<ManualMatchResult?> ConfirmMatchAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid transactionId,
        Guid actorId, Guid ownerId, bool saveBankAccount, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var line = await conn.QuerySingleOrDefaultAsync<(string Direction, string? CpAccount, string? CpAccountNorm, string? CpName, string? CpBankCode, string? CpBankName, Guid? CustomerId, Guid? SupplierId, string? Method)?>(
            new CommandDefinition("""
                SELECT direction, counterparty_account, counterparty_account_normalized, counterparty_name,
                       counterparty_bank_code, counterparty_bank_name, customer_id, supplier_id, match_method
                FROM sim_bank_transactions
                WHERE id=@transactionId AND voucher_id=@voucherId AND tenant_id=@tenantId AND department_id=@departmentId
                FOR UPDATE
                """, new { transactionId, voucherId, tenantId, departmentId }, tx, cancellationToken: ct));
        if (line is not { } l) return null;
        if (await LineAccountedAsync(conn, tx, transactionId, ct))
            throw new BankAccountException("line_already_accounted",
                "Giao dịch đã được ghi nhận kế toán — đảo bút toán trước khi đổi khách hàng.");

        var ownerType = l.Direction == "money_in" ? BankOwnerTypes.Customer : BankOwnerTypes.Supplier;
        var table = ownerType == BankOwnerTypes.Customer ? "sim_customers" : "sim_suppliers";
        var ownerName = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"SELECT name FROM {table} WHERE id=@ownerId AND tenant_id=@tenantId AND department_id=@departmentId",
            new { ownerId, tenantId, departmentId }, tx, cancellationToken: ct));
        if (ownerName is null)
            throw new BankAccountException("owner_not_in_scope", ownerType == BankOwnerTypes.Customer
                ? "Khách hàng không thuộc phòng ban này." : "Nhà cung cấp không thuộc phòng ban này.");

        // Learn the account only on an explicit ADMIN request; a conflict
        // with another counterparty aborts the whole confirmation (409) so
        // nothing half-applies.
        BankAccountRecord? learned = null;
        if (saveBankAccount)
        {
            if (string.IsNullOrEmpty(l.CpAccountNorm))
                throw new BankAccountException("no_counterparty_account", "Giao dịch không có số tài khoản đối ứng để lưu.");
            learned = await bankAccounts.CreateInTransactionAsync(conn, tx, tenantId, departmentId, ownerType, ownerId, actorId,
                new BankAccountInput(l.CpBankCode, l.CpBankName, l.CpAccount, l.CpName, null, null, null),
                "statement_review", transactionId, ct);
        }

        var decision = JsonSerializer.Serialize(new
        {
            action = "admin_selected", owner_type = ownerType, owner_id = ownerId, owner_name = ownerName,
            by = actorId, at = DateTime.UtcNow,
            previous = new { customer_id = l.CustomerId, supplier_id = l.SupplierId, method = l.Method },
            saved_bank_account_id = learned?.Id,
        });
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE sim_bank_transactions SET
                customer_id = @customerId, supplier_id = @supplierId,
                counterparty_bank_account_id = COALESCE(@learnedId, counterparty_bank_account_id),
                match_status = 'matched', match_method = 'admin_selected', match_confidence = 1.0,
                matched_by = @actorId, matched_at = NOW(), confirmed_by = @actorId, confirmed_at = NOW(),
                match_evidence = jsonb_set(match_evidence, '{decisions}',
                    COALESCE(match_evidence->'decisions', '[]'::jsonb) || jsonb_build_array(@decision::jsonb), true)
            WHERE id=@transactionId
            """, new
        {
            transactionId, actorId, decision, learnedId = learned?.Id,
            customerId = ownerType == BankOwnerTypes.Customer ? ownerId : (Guid?)null,
            supplierId = ownerType == BankOwnerTypes.Supplier ? ownerId : (Guid?)null,
        }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);

        await using var read = await factory.OpenAsync(ct);
        var record = await read.QuerySingleAsync<StatementTransactionRecord>(new CommandDefinition(
            $"{ListSql} WHERE t.id=@transactionId", new { transactionId }, cancellationToken: ct));
        return new ManualMatchResult(record, learned);
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
