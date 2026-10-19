using System.Text.Json;
using Aiq.Gateway.Customers;
using Aiq.Gateway.Data;
using Aiq.Gateway.Suppliers;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Payments;

// ============================================================================
// Chứng từ THU / CHI -> ADMIN duyệt -> tài khoản tiền -> đối trừ AR/AP.
//
// One service owns every mutation of a receipt/payment voucher's accounting
// effect (post, allocate, unapply, reverse), each in ONE DB transaction:
//
//   lock voucher row -> lock counterparty row -> lock invoice rows (id order)
//   -> cash_transactions / payment_applications / sub-ledger rows / journal
//   -> recompute current_ar|current_ap -> voucher status -> COMMIT
//
// Open items (sim_*_ledger invoice rows: amount + paid_amount) stay the
// source of truth for outstanding balances; payment_applications is the
// only thing allowed to move paid_amount in this flow. current_ar/current_ap
// are always RECOMPUTED from open items, never incremented.
//
// Every method is scoped by (tenantId, departmentId) resolved from the
// authenticated principal — a voucher outside that scope is "not found".
// ============================================================================

public sealed record SettlementVoucherInfo(
    Guid VoucherId,
    string VoucherType,
    string VoucherStatus,
    string? DocumentNumber,
    DateOnly? DocumentDate,
    decimal? Amount,
    string Currency,
    string? PaymentMethodRaw,
    string? PaymentMethod,
    CashParty Payer,
    CashParty Payee,
    IReadOnlyList<string> BankAccounts,
    string? Description,
    IReadOnlyList<string> InvoiceReferences,
    IReadOnlyList<string> DocumentInvoiceReferences,
    string? ContractReference,
    Guid CreatedBy);

public sealed record SettlementCounterparty(
    Guid Id, string Type, string Code, string Name, string? TaxCode, decimal CurrentBalance, string MatchedBy);

public sealed record SettlementOpenItem(
    Guid Id, string DocumentNumber, DateOnly DocumentDate, DateOnly? DueDate,
    decimal OriginalAmount, decimal PaidAmount, decimal Outstanding, string SettlementStatus);

public sealed record CashTransactionRecord(
    Guid Id, Guid VoucherId, string Direction, string CounterpartyType, Guid? CustomerId, Guid? SupplierId,
    decimal Amount, string Currency, DateOnly TransactionDate, string PaymentMethod, string CashAccount,
    string CounterAccount, Guid? BankAccountId, string DocumentNumber, string Status,
    Guid? PostedBy, DateTime PostedAt, Guid? ReversedBy, DateTime? ReversedAt, string? ReversalReason,
    decimal AppliedAmount, decimal UnappliedAmount, string? DocumentReference,
    string SourceType, Guid? SourceBankTransactionId, string? BankReference);

public sealed record PaymentApplicationRecord(
    Guid Id, Guid CashTransactionId, Guid InvoiceId, string InvoiceDocumentNumber, decimal AppliedAmount,
    string MatchMethod, string Status, Guid? AppliedBy, DateTime AppliedAt,
    Guid? ReversedBy, DateTime? ReversedAt, string? ReversalReason);

/// <summary>Who pays / who receives, with MY_COMPANY's side filled from the Company Profile.</summary>
public sealed record SettlementParties(CashParty Payer, CashParty Payee, string? CompanyName, string? CompanyTaxCode, bool CompanyIsPayee);

public sealed record ReferencedInvoice(string DocumentNumber, decimal OriginalAmount, decimal Outstanding, string SettlementStatus);

/// <summary>
/// Document invoice reference vs. the invoice(s) money is (or would be)
/// applied to. Status: no_reference | not_applied | matched | mismatch.
/// </summary>
public sealed record ReferenceCheck(
    string Status,
    IReadOnlyList<string> DocumentReferences,
    IReadOnlyList<ReferencedInvoice> ReferencedInvoices,
    IReadOnlyList<string> AppliedInvoices);

public sealed record JournalLine(string DebitAccount, string CreditAccount, decimal Amount, string Description);

public sealed record SettlementContext(
    SettlementVoucherInfo Voucher,
    CashDirectionResolution Direction,
    SettlementCounterparty? Counterparty,
    IReadOnlyList<SettlementOpenItem> OpenItems,
    MatchSuggestion? Suggestion,
    CashTransactionRecord? Posting,
    IReadOnlyList<PaymentApplicationRecord> Applications,
    IReadOnlyList<CashTransactionRecord> ReversedPostings,
    IReadOnlyList<JournalLine> ProposedJournal,
    IReadOnlyList<string> Blockers,
    SettlementParties Parties,
    ReferenceCheck Reference,
    IReadOnlyList<Aiq.Gateway.Contracts.ContractScheduleImpact> ContractSchedules,
    IReadOnlyList<PaymentDiscountProposal> DiscountProposals);

public sealed record AllocationInput(Guid InvoiceId, decimal Amount);

public sealed record PostCashRequest(
    Guid? CounterpartyId,
    IReadOnlyList<AllocationInput>? Allocations,
    bool LeaveUnapplied,
    string? PaymentMethod);

public sealed record SettlementMutationResult(
    string Outcome,
    CashTransactionRecord? Posting,
    IReadOnlyList<PaymentApplicationRecord> Applications,
    decimal PreviousBalance,
    decimal NewBalance,
    IReadOnlyList<JournalLine> Journal);

/// <summary>Details of a document_review_required refusal (see DocumentAccountingGate).</summary>
public sealed record DocumentGateDetails(IReadOnlyList<string> Reasons);

public sealed class CashSettlementException(string code, string message, object? details = null) : Exception(message)
{
    public string Code { get; } = code;
    public object? Details { get; } = details;
}

public interface ICashSettlementRepository
{
    /// <summary>Read-only: resolution + candidates + current posting. Null when the voucher is not in scope or not a receipt/payment.</summary>
    Task<SettlementContext?> GetContextAsync(Guid tenantId, Guid departmentId, Guid voucherId, CancellationToken ct);

    /// <summary>ADMIN "Duyệt &amp; ghi nhận". Idempotent: an already-posted voucher returns Outcome="already_posted" and changes nothing.</summary>
    Task<SettlementMutationResult?> PostAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid actorId, PostCashRequest request, CancellationToken ct);

    /// <summary>Allocate a posting's UNAPPLIED money to open items later (one payment -> many invoices).</summary>
    Task<SettlementMutationResult?> AllocateAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid actorId, IReadOnlyList<AllocationInput> allocations, CancellationToken ct);

    /// <summary>Reverse one (or all, when applicationId is null) active application. Money stays posted, becomes UNAPPLIED.</summary>
    Task<SettlementMutationResult?> UnapplyAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid actorId, Guid? applicationId, string? reason, CancellationToken ct);

    /// <summary>Void the whole posting: unapply everything, reverse the cash journal, voucher back to 'extracted'.</summary>
    Task<SettlementMutationResult?> ReverseAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid actorId, string? reason, CancellationToken ct);

    /// <summary>
    /// ADMIN confirmation of a contract payment-discount proposal: re-evaluated
    /// server-side under the settlement locks. Never applies a stale or
    /// browser-supplied value; without an approved accounting policy it stops
    /// at payment_discount_accounting_policy_missing.
    /// </summary>
    Task<SettlementMutationResult?> ConfirmPaymentDiscountAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid actorId, ConfirmPaymentDiscountRequest request, CancellationToken ct);
}

public sealed partial class CashSettlementRepository(NpgsqlConnectionFactory factory) : ICashSettlementRepository
{
    private static readonly string[] SupportedTypes = { "receipt", "payment", "bank_transfer" };

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));

    // ------------------------------------------------------------------
    // Read
    // ------------------------------------------------------------------

    public async Task<SettlementContext?> GetContextAsync(Guid tenantId, Guid departmentId, Guid voucherId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await LoadContextAsync(conn, null, tenantId, departmentId, voucherId, lockVoucher: false, counterpartyOverride: null, ct);
    }

    private sealed record VoucherRow(
        Guid Id, string VoucherType, string Status, string? DocumentNumber, DateOnly? DocumentDate,
        decimal? TotalAmount, string? Currency, string? PaymentMethod,
        string? SellerName, string? SellerMst, string? BuyerName, string? BuyerMst,
        string? ExtractedFields, Guid UserId, string? RawOcrText, string? DocumentReviewStatus, string[]? ProcessingBlockers);

    private async Task<SettlementContext?> LoadContextAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, Guid departmentId, Guid voucherId,
        bool lockVoucher, Guid? counterpartyOverride, CancellationToken ct)
    {
        var v = await conn.QuerySingleOrDefaultAsync<VoucherRow>(new CommandDefinition($"""
            SELECT id AS Id, voucher_type AS VoucherType, status AS Status, document_number AS DocumentNumber,
                   document_date AS DocumentDate, total_amount AS TotalAmount, currency AS Currency,
                   payment_method AS PaymentMethod, seller_name AS SellerName, seller_mst AS SellerMst,
                   buyer_name AS BuyerName, buyer_mst AS BuyerMst, extracted_fields::text AS ExtractedFields,
                   user_id AS UserId, raw_ocr_text AS RawOcrText, document_review_status AS DocumentReviewStatus,
                   processing_blockers AS ProcessingBlockers
            FROM vouchers
            WHERE id=@voucherId AND tenant_id=@tenantId AND department_id=@departmentId
            {(lockVoucher ? "FOR UPDATE" : "")}
            """, new { voucherId, tenantId, departmentId }, tx, cancellationToken: ct));
        if (v is null) return null;
        var vt = v.VoucherType.ToLowerInvariant();
        if (!SupportedTypes.Contains(vt)) return null;

        var info = BuildVoucherInfo(v, vt);

        var profile = await conn.QuerySingleOrDefaultAsync<(string? LegalName, string? TaxCode, string? ShortName, string[]? Aliases)?>(
            new CommandDefinition(
                "SELECT legal_name, tax_code, short_name, aliases FROM tenant_company_profiles WHERE tenant_id=@tenantId",
                new { tenantId }, tx, cancellationToken: ct));
        CompanyIdentity? company = profile is { } p
            ? new CompanyIdentity(p.TaxCode, new[] { p.LegalName, p.ShortName }.Concat(p.Aliases ?? Array.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList())
            : null;

        var direction = CashSettlementRules.ResolveDirection(vt, info.Payer, info.Payee, company, v.RawOcrText);

        var postings = await LoadPostingsAsync(conn, tx, tenantId, departmentId, voucherId, ct);
        var live = postings.FirstOrDefault(p => p.Status == "posted");
        var applications = await LoadApplicationsAsync(conn, tx, postings.Select(p => p.Id).ToArray(), ct);

        SettlementCounterparty? counterparty = null;
        if (live is not null)
        {
            // Once posted, the posting itself is the truth (it may have been
            // an ADMIN-selected counterparty, not the extracted one).
            direction = direction with { Direction = live.Direction };
            counterparty = await FindCounterpartyByIdAsync(conn, tx, tenantId, departmentId, live.Direction,
                (live.CustomerId ?? live.SupplierId)!.Value, ct);
            if (counterparty is not null) counterparty = counterparty with { MatchedBy = "posted" };
        }
        else if (direction.Direction != CashDirections.NeedsReview)
        {
            counterparty = counterpartyOverride is { } overrideId
                ? await FindCounterpartyByIdAsync(conn, tx, tenantId, departmentId, direction.Direction, overrideId, ct)
                : await ResolveCounterpartyAsync(conn, tx, tenantId, departmentId, direction.Direction, direction.Counterparty, ct);
        }

        var openItems = counterparty is null
            ? new List<SettlementOpenItem>()
            : await LoadOpenItemsAsync(conn, tx, tenantId, direction.Direction, counterparty.Id, lockRows: false, ct);

        MatchSuggestion? suggestion = null;
        if (counterparty is not null && info.Amount is > 0m)
        {
            var amountToMatch = live?.UnappliedAmount ?? info.Amount.Value;
            if (amountToMatch > 0m)
            {
                suggestion = CashSettlementRules.SuggestAllocations(
                    amountToMatch,
                    openItems.Select(o => new OpenItem(o.Id, o.DocumentNumber, o.DocumentDate, o.DueDate, o.OriginalAmount, o.PaidAmount)).ToList(),
                    info.InvoiceReferences.Append(info.Description),
                    info.DocumentInvoiceReferences);
            }
        }

        var blockers = new List<string>();
        if (direction.Direction == CashDirections.NeedsReview) blockers.Add("direction_needs_review:" + direction.Reason);
        else if (counterparty is null) blockers.Add(direction.Direction == CashDirections.MoneyIn ? "customer_not_resolved" : "supplier_not_resolved");
        if (info.Amount is not > 0m) blockers.Add("amount_missing");
        if (!string.Equals(info.Currency, "VND", StringComparison.OrdinalIgnoreCase)) blockers.Add("foreign_currency_not_supported");
        if (info.PaymentMethod is null) blockers.Add("payment_method_unknown");
        // Voucher type still awaiting human confirmation (title vs extractor
        // conflict, upload hint mismatch, or low confidence): never post on it.
        if (Aiq.Gateway.Vouchers.VoucherTypeClassification.FromExtractedFields(v.ExtractedFields) is { BlocksPosting: true })
            blockers.Add("voucher_type_unconfirmed");
        // Canonical document-level gate (DocumentAccountingGate): review
        // awaiting/rejected/creation-failed and extraction-owned blockers.
        blockers.AddRange(DocumentGateBlockers(Aiq.Gateway.Vouchers.DocumentProcessing.DocumentAccountingGate.Reasons(
            v.DocumentReviewStatus, v.ProcessingBlockers)));

        var journal = new List<JournalLine>();
        if (info.Amount is > 0m && direction.Direction != CashDirections.NeedsReview && info.PaymentMethod is { } method)
        {
            journal.Add(BuildJournalLine(direction.Direction, CashSettlementRules.DefaultCashAccount(method), info.Amount.Value, counterparty?.Name));
        }

        var parties = BuildParties(info, direction.Direction, counterparty, profile is { } pp ? (pp.LegalName ?? pp.ShortName, pp.TaxCode) : (null, null),
            direction.Source.StartsWith("company_profile", StringComparison.Ordinal));
        var reference = counterparty is null
            ? new ReferenceCheck(info.DocumentInvoiceReferences.Count == 0 ? "no_reference" : "not_applied",
                info.DocumentInvoiceReferences, Array.Empty<ReferencedInvoice>(), Array.Empty<string>())
            : await BuildReferenceCheckAsync(conn, tx, tenantId, direction.Direction, counterparty.Id, info,
                live is null ? Array.Empty<string>() : applications
                    .Where(a => a.CashTransactionId == live.Id && a.Status == "active")
                    .Select(a => a.InvoiceDocumentNumber).Distinct().ToList(), ct);

        var contractSchedules = direction.Direction == CashDirections.MoneyIn
            ? await LoadContractSchedulesAsync(conn, tx, tenantId, departmentId, live, applications, ct)
            : Array.Empty<Aiq.Gateway.Contracts.ContractScheduleImpact>();

        // Contract payment discount: read-only proposal for an unposted receipt
        // (receipt date = the voucher's own document date, never "today").
        var discountProposals = direction.Direction == CashDirections.MoneyIn && counterparty is not null && live is null && info.Amount is > 0m
            ? await LoadDiscountProposalsAsync(conn, tx, tenantId, counterparty.Id,
                new DiscountReceipt(tenantId, departmentId, info.DocumentDate, info.Amount.Value, info.Currency), ct)
            : [];

        return new SettlementContext(
            info, direction, counterparty, openItems, suggestion, live, applications,
            postings.Where(p => p.Status == "reversed").ToList(), journal, blockers, parties, reference, contractSchedules,
            discountProposals);
    }

    private static SettlementParties BuildParties(
        SettlementVoucherInfo info, string direction, SettlementCounterparty? counterparty,
        (string? Name, string? TaxCode) company, bool companyIdentified)
    {
        var moneyIn = direction == CashDirections.MoneyIn;
        var moneyOut = direction == CashDirections.MoneyOut;
        if (!moneyIn && !moneyOut) return new SettlementParties(info.Payer, info.Payee, company.Name, company.TaxCode, false);
        // Counterparty side: the resolved master record when there is one.
        var cp = counterparty is null
            ? (moneyIn ? info.Payer : info.Payee)
            : new CashParty(counterparty.Name, counterparty.TaxCode);
        // MY_COMPANY side: the trusted Company Profile identity, never the
        // (possibly misspelt) OCR rendering of our own name.
        var extractedUs = moneyIn ? info.Payee : info.Payer;
        var us = companyIdentified && company.Name is not null
            ? new CashParty(company.Name, company.TaxCode)
            : extractedUs;
        return moneyIn
            ? new SettlementParties(cp, us, company.Name, company.TaxCode, true)
            : new SettlementParties(us, cp, company.Name, company.TaxCode, false);
    }

    private sealed record InvoiceRefRow(string DocumentNumber, decimal Original, decimal Paid, DateOnly? DueDate);

    private static async Task<ReferenceCheck> BuildReferenceCheckAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, string direction, Guid counterpartyId,
        SettlementVoucherInfo info, IReadOnlyList<string> applied, CancellationToken ct)
    {
        var (table, amountCol, fkCol) = LedgerTable(direction);
        var all = (await conn.QueryAsync<InvoiceRefRow>(new CommandDefinition($"""
            SELECT document_number AS DocumentNumber, {amountCol} AS Original, paid_amount AS Paid, due_date AS DueDate
            FROM {table}
            WHERE tenant_id=@tenantId AND {fkCol}=@counterpartyId AND document_type='invoice'
            """, new { tenantId, counterpartyId }, tx, cancellationToken: ct))).ToList();
        // Explicit reference fields first; free text only when there are none.
        var source = info.DocumentInvoiceReferences.Count > 0
            ? info.DocumentInvoiceReferences.Cast<string?>()
            : info.InvoiceReferences.Append(info.Description);
        var referencedNumbers = CashSettlementRules.FindReferencedDocumentNumbers(all.Select(a => a.DocumentNumber), source);
        var today = Today;
        var referenced = all.Where(a => referencedNumbers.Contains(a.DocumentNumber))
            .Select(a => new ReferencedInvoice(a.DocumentNumber, a.Original, a.Original - a.Paid,
                CashSettlementRules.SettlementStatus(a.Original, a.Paid, a.DueDate, today)))
            .ToList();
        var docRefs = info.DocumentInvoiceReferences.Count > 0 ? info.DocumentInvoiceReferences : referencedNumbers;

        string status;
        if (docRefs.Count == 0) status = "no_reference";
        else if (applied.Count == 0) status = "not_applied";
        else
        {
            var refKeys = docRefs.Concat(referencedNumbers).Select(CashSettlementRules.NormalizeDocNumber).ToHashSet();
            status = applied.All(a => refKeys.Contains(CashSettlementRules.NormalizeDocNumber(a))) ? "matched" : "mismatch";
        }
        return new ReferenceCheck(status, docRefs, referenced, applied);
    }

    private static SettlementVoucherInfo BuildVoucherInfo(VoucherRow v, string vt)
    {
        var ex = ParseFields(v.ExtractedFields);
        string? S(params string[] keys)
        {
            foreach (var k in keys)
            {
                if (ex.TryGetValue(k, out var el) && el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString()))
                    return el.GetString()!.Trim();
                if (ex.TryGetValue(k, out el) && el.ValueKind == JsonValueKind.Number) return el.GetRawText();
            }
            return null;
        }
        decimal? D(params string[] keys)
        {
            foreach (var k in keys)
            {
                if (!ex.TryGetValue(k, out var el)) continue;
                if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d)) return d;
                if (el.ValueKind == JsonValueKind.String && decimal.TryParse(
                        new string(el.GetString()!.Where(c => char.IsDigit(c) || c == '.').ToArray()),
                        System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out d)) return d;
            }
            return null;
        }

        // Money flows payer -> payee. Voucher columns follow the same
        // convention for every type (buyer pays, seller receives), and the
        // extraction skill's payer_/payee_/beneficiary_ fields win when present.
        var payer = new CashParty(S("payer_name", "payer") ?? v.BuyerName, S("payer_mst", "payer_tax_code") ?? v.BuyerMst);
        var payee = new CashParty(
            S("payee_name", "beneficiary_name", "recipient_name", "payee") ?? v.SellerName,
            S("payee_mst", "beneficiary_mst", "recipient_mst", "payee_tax_code") ?? v.SellerMst);

        var methodRaw = v.PaymentMethod ?? S("payment_method", "method");
        var method = CashSettlementRules.NormalizePaymentMethod(methodRaw) ?? (vt == "bank_transfer" ? "bank" : null);

        // Keys that hold ONLY an invoice number (as opposed to free text) —
        // shown to ADMIN as "Tham chiếu trên chứng từ".
        var documentRefs = new[] { "invoice_number", "linked_invoice_number", "related_invoice_number", "invoice_reference",
                                   "reference_document", "reference_invoice", "related_invoice", "invoice_ref" }
            .Select(k => S(k)).Where(r => r is not null).Select(r => r!).Distinct().ToList();
        var references = new List<string>();
        foreach (var key in new[] { "related_invoice_number", "invoice_number", "linked_invoice_number", "invoice_reference", "reference_document", "reference_invoice", "related_invoice", "invoice_ref", "reference", "payment_reference", "reason", "content", "description", "memo" })
        {
            if (S(key) is { } r) references.Add(r);
        }
        if (ex.TryGetValue("invoice_numbers", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            references.AddRange(arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
        }

        var bankAccounts = new[] { "bank_account", "from_account", "to_account", "payer_bank_account", "payee_bank_account" }
            .Select(k => S(k)).Where(a => a is not null).Select(a => a!).ToList();

        return new SettlementVoucherInfo(
            v.Id, vt, v.Status, v.DocumentNumber, v.DocumentDate,
            v.TotalAmount ?? D("amount", "total_amount", "so_tien"),
            v.Currency ?? S("currency") ?? "VND",
            methodRaw, method, payer, payee, bankAccounts,
            S("reason", "content", "description"),
            references,
            documentRefs,
            S("contract_number", "contract_reference", "contract_ref", "related_contract_number"),
            v.UserId);
    }

    private static Dictionary<string, JsonElement> ParseFields(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return new();
            return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException) { return new(); }
    }

    private static async Task<SettlementCounterparty?> ResolveCounterpartyAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, Guid departmentId, string direction, CashParty? party, CancellationToken ct)
    {
        if (party is null || party.IsEmpty) return null;
        var (table, balanceCol, codeCol, type) = CounterpartyTable(direction);

        var tax = CashSettlementRules.NormalizeTaxCode(party.TaxCode);
        if (tax.Length > 0)
        {
            var byTax = await conn.QueryAsync<SettlementCounterparty>(new CommandDefinition($"""
                SELECT id AS Id, '{type}' AS Type, {codeCol} AS Code, name AS Name, mst AS TaxCode,
                       COALESCE({balanceCol},0) AS CurrentBalance, 'tax_code' AS MatchedBy
                FROM {table}
                WHERE tenant_id=@tenantId AND department_id=@departmentId
                  AND regexp_replace(COALESCE(mst,''), '\D', '', 'g') = @tax
                """, new { tenantId, departmentId, tax }, tx, cancellationToken: ct));
            var list = byTax.ToList();
            if (list.Count == 1) return list[0];
            // A tax code that is present but unknown is decisive: never fall
            // back to a name that might point at a different legal entity.
            return null;
        }

        var name = CashSettlementRules.NormalizeCompanyName(party.Name);
        if (name.Length == 0) return null;
        var all = await conn.QueryAsync<SettlementCounterparty>(new CommandDefinition($"""
            SELECT id AS Id, '{type}' AS Type, {codeCol} AS Code, name AS Name, mst AS TaxCode,
                   COALESCE({balanceCol},0) AS CurrentBalance, 'name' AS MatchedBy
            FROM {table}
            WHERE tenant_id=@tenantId AND department_id=@departmentId
            """, new { tenantId, departmentId }, tx, cancellationToken: ct));
        var matches = all.Where(c => CashSettlementRules.NormalizeCompanyName(c.Name) == name).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static async Task<SettlementCounterparty?> FindCounterpartyByIdAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, Guid departmentId, string direction, Guid id, CancellationToken ct)
    {
        var (table, balanceCol, codeCol, type) = CounterpartyTable(direction);
        return await conn.QuerySingleOrDefaultAsync<SettlementCounterparty>(new CommandDefinition($"""
            SELECT id AS Id, '{type}' AS Type, {codeCol} AS Code, name AS Name, mst AS TaxCode,
                   COALESCE({balanceCol},0) AS CurrentBalance, 'admin_selected' AS MatchedBy
            FROM {table}
            WHERE tenant_id=@tenantId AND department_id=@departmentId AND id=@id
            """, new { tenantId, departmentId, id }, tx, cancellationToken: ct));
    }

    private static (string Table, string BalanceCol, string CodeCol, string Type) CounterpartyTable(string direction) =>
        direction == CashDirections.MoneyIn
            ? ("sim_customers", "current_ar", "customer_code", "customer")
            : ("sim_suppliers", "current_ap", "supplier_code", "supplier");

    private static (string Table, string AmountCol, string FkCol) LedgerTable(string direction) =>
        direction == CashDirections.MoneyIn
            ? ("sim_ar_ledger", "debit", "customer_id")
            : ("sim_ap_ledger", "credit", "supplier_id");

    private static async Task<List<SettlementOpenItem>> LoadOpenItemsAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, string direction, Guid counterpartyId, bool lockRows, CancellationToken ct)
    {
        var (table, amountCol, fkCol) = LedgerTable(direction);
        var rows = await conn.QueryAsync<OpenItemRow>(
            new CommandDefinition($"""
                SELECT id AS Id, document_number AS DocumentNumber, document_date AS DocumentDate,
                       due_date AS DueDate, {amountCol} AS Original, paid_amount AS Paid
                FROM {table}
                WHERE tenant_id=@tenantId AND {fkCol}=@counterpartyId
                  AND document_type='invoice' AND status IN ('open','partial')
                ORDER BY id
                {(lockRows ? "FOR UPDATE" : "")}
                """, new { tenantId, counterpartyId }, tx, cancellationToken: ct));
        var today = Today;
        return rows
            .Select(r => new SettlementOpenItem(r.Id, r.DocumentNumber, r.DocumentDate, r.DueDate, r.Original, r.Paid,
                r.Original - r.Paid, CashSettlementRules.SettlementStatus(r.Original, r.Paid, r.DueDate, today)))
            .OrderBy(r => r.DueDate ?? DateOnly.MaxValue).ThenBy(r => r.DocumentDate)
            .ToList();
    }

    private sealed record OpenItemRow(Guid Id, string DocumentNumber, DateOnly DocumentDate, DateOnly? DueDate, decimal Original, decimal Paid);

    private const string PostingColumns = """
        c.id AS Id, c.voucher_id AS VoucherId, c.direction AS Direction, c.counterparty_type AS CounterpartyType,
        c.customer_id AS CustomerId, c.supplier_id AS SupplierId, c.amount AS Amount, c.currency AS Currency,
        c.transaction_date AS TransactionDate, c.payment_method AS PaymentMethod, c.cash_account AS CashAccount,
        c.counter_account AS CounterAccount, c.bank_account_id AS BankAccountId, c.document_number AS DocumentNumber,
        c.status AS Status, c.posted_by AS PostedBy, c.posted_at AS PostedAt, c.reversed_by AS ReversedBy,
        c.reversed_at AS ReversedAt, c.reversal_reason AS ReversalReason,
        COALESCE((SELECT SUM(a.applied_amount) FROM payment_applications a
                  WHERE a.cash_transaction_id=c.id AND a.status='active'),0) AS AppliedAmount,
        c.amount - COALESCE((SELECT SUM(a.applied_amount) FROM payment_applications a
                  WHERE a.cash_transaction_id=c.id AND a.status='active'),0) AS UnappliedAmount,
        c.document_reference AS DocumentReference,
        c.source_type AS SourceType, c.source_bank_transaction_id AS SourceBankTransactionId, c.bank_reference AS BankReference
        """;

    private static async Task<List<CashTransactionRecord>> LoadPostingsAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, Guid departmentId, Guid voucherId, CancellationToken ct) =>
        (await conn.QueryAsync<CashTransactionRecord>(new CommandDefinition($"""
            SELECT {PostingColumns}
            FROM cash_transactions c
            WHERE c.voucher_id=@voucherId AND c.tenant_id=@tenantId AND c.department_id=@departmentId
              AND c.source_type='voucher'
            ORDER BY c.posted_at DESC
            """, new { voucherId, tenantId, departmentId }, tx, cancellationToken: ct))).ToList();

    private static async Task<CashTransactionRecord?> LoadPostingByIdAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, CancellationToken ct) =>
        await conn.QuerySingleOrDefaultAsync<CashTransactionRecord>(new CommandDefinition(
            $"SELECT {PostingColumns} FROM cash_transactions c WHERE c.id=@id", new { id }, tx, cancellationToken: ct));

    private static async Task<List<PaymentApplicationRecord>> LoadApplicationsAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid[] cashTransactionIds, CancellationToken ct)
    {
        if (cashTransactionIds.Length == 0) return new();
        return (await conn.QueryAsync<PaymentApplicationRecord>(new CommandDefinition("""
            SELECT a.id AS Id, a.cash_transaction_id AS CashTransactionId,
                   COALESCE(a.ar_invoice_id, a.ap_invoice_id) AS InvoiceId,
                   COALESCE(ar.document_number, ap.document_number) AS InvoiceDocumentNumber,
                   a.applied_amount AS AppliedAmount, a.match_method AS MatchMethod, a.status AS Status,
                   a.applied_by AS AppliedBy, a.applied_at AS AppliedAt, a.reversed_by AS ReversedBy,
                   a.reversed_at AS ReversedAt, a.reversal_reason AS ReversalReason
            FROM payment_applications a
            LEFT JOIN sim_ar_ledger ar ON ar.id = a.ar_invoice_id
            LEFT JOIN sim_ap_ledger ap ON ap.id = a.ap_invoice_id
            WHERE a.cash_transaction_id = ANY(@ids)
            ORDER BY a.applied_at, a.id
            """, new { ids = cashTransactionIds }, tx, cancellationToken: ct))).ToList();
    }

    private static JournalLine BuildJournalLine(string direction, string cashAccount, decimal amount, string? counterpartyName) =>
        direction == CashDirections.MoneyIn
            ? new JournalLine(cashAccount, "131", amount, $"Thu tiền {counterpartyName ?? "khách hàng"}")
            : new JournalLine("331", cashAccount, amount, $"Chi trả {counterpartyName ?? "nhà cung cấp"}");

    // ------------------------------------------------------------------
    // Post
    // ------------------------------------------------------------------

    public async Task<SettlementMutationResult?> PostAsync(
        Guid tenantId, Guid departmentId, Guid voucherId, Guid actorId, PostCashRequest request, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Voucher row lock serialises double-click / retry: the second caller
        // waits here, then sees the first caller's posting below.
        var ctx = await LoadContextAsync(conn, tx, tenantId, departmentId, voucherId, lockVoucher: true, request.CounterpartyId, ct);
        if (ctx is null) return null;

        if (ctx.Posting is { } existing)
        {
            await tx.RollbackAsync(ct);
            return new SettlementMutationResult("already_posted", existing,
                ctx.Applications.Where(a => a.CashTransactionId == existing.Id).ToList(),
                ctx.Counterparty?.CurrentBalance ?? 0m, ctx.Counterparty?.CurrentBalance ?? 0m, Array.Empty<JournalLine>());
        }

        if (ctx.Blockers.Contains("voucher_type_unconfirmed"))
            throw new CashSettlementException("voucher_type_unconfirmed", "Loại chứng từ chưa được xác nhận — ADMIN cần xác nhận loại đúng trước khi ghi nhận.");
        ThrowIfDocumentGated(ctx.Blockers);
        var direction = ctx.Direction.Direction;
        if (direction == CashDirections.NeedsReview)
            throw new CashSettlementException("direction_needs_review", "Không xác định chắc chắn chiều thu/chi của chứng từ.", new { ctx.Direction.Reason });
        if (ctx.Counterparty is not { } cp)
            throw new CashSettlementException(request.CounterpartyId is null ? "counterparty_required" : "counterparty_not_in_scope",
                direction == CashDirections.MoneyIn ? "Chưa xác định được khách hàng — chọn khách hàng." : "Chưa xác định được nhà cung cấp — chọn nhà cung cấp.");
        if (ctx.Voucher.Amount is not { } amount || amount <= 0m)
            throw new CashSettlementException("amount_missing", "Chứng từ chưa có số tiền.");
        if (!string.Equals(ctx.Voucher.Currency, "VND", StringComparison.OrdinalIgnoreCase))
            throw new CashSettlementException("foreign_currency_not_supported", "Chưa hỗ trợ ngoại tệ (không có nguồn tỷ giá).");
        var method = CashSettlementRules.NormalizePaymentMethod(request.PaymentMethod) ?? ctx.Voucher.PaymentMethod
            ?? throw new CashSettlementException("payment_method_required", "Chọn hình thức: Tiền mặt hoặc Chuyển khoản.");

        // Lock order: voucher (above) -> counterparty -> invoice rows.
        var (cpTable, _, _, _) = CounterpartyTable(direction);
        var previousBalance = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
            $"SELECT COALESCE({(direction == CashDirections.MoneyIn ? "current_ar" : "current_ap")},0) FROM {cpTable} WHERE id=@id AND tenant_id=@tenantId FOR UPDATE",
            new { id = cp.Id, tenantId }, tx, cancellationToken: ct));
        var openItems = await LoadOpenItemsAsync(conn, tx, tenantId, direction, cp.Id, lockRows: true, ct);

        var plan = PlanAllocations(ctx.Suggestion, ctx.OpenItems, openItems, amount, request);

        var (bankAccountId, bankGl) = method == "bank"
            ? await ResolveBankAccountAsync(conn, tx, tenantId, ctx.Voucher.BankAccounts, ct)
            : (null, null);
        var cashAccount = bankGl ?? CashSettlementRules.DefaultCashAccount(method);
        var docNumber = ctx.Voucher.DocumentNumber ?? $"{(direction == CashDirections.MoneyIn ? "PT" : "PC")}-{voucherId:N}"[..20];

        // Pending AI suggestions for this voucher are superseded by the real
        // posting — kept as rejected history, never deleted.
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE voucher_journal_suggestions
               SET rejected_at = NOW(), reject_reason = 'superseded_by_posting'
             WHERE voucher_id=@voucherId AND accepted_at IS NULL AND rejected_at IS NULL
            """, new { voucherId }, tx, cancellationToken: ct));

        var posted = await PostCoreAsync(conn, tx, new PostingSource(
            tenantId, departmentId, voucherId, SourceTypes.Voucher, null, direction, cp, amount,
            ctx.Voucher.DocumentDate ?? Today, method, cashAccount, bankAccountId, docNumber, ctx.Voucher.Description,
            // Audit: what the DOCUMENT said it pays, kept next to what ADMIN
            // actually applied (payment_applications.match_method).
            ctx.Reference.DocumentReferences.Count == 0 ? null : string.Join(", ", ctx.Reference.DocumentReferences),
            null), plan, actorId, ct);
        if (posted is not { } done)
        {
            // Defence in depth — the voucher lock should make this unreachable.
            await tx.RollbackAsync(ct);
            var again = await GetContextAsync(tenantId, departmentId, voucherId, ct);
            return new SettlementMutationResult("already_posted", again?.Posting,
                again?.Applications ?? Array.Empty<PaymentApplicationRecord>(), previousBalance, previousBalance, Array.Empty<JournalLine>());
        }
        var (cashId, line) = done;

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE vouchers SET status='verified' WHERE id=@voucherId AND tenant_id=@tenantId AND department_id=@departmentId",
            new { voucherId, tenantId, departmentId }, tx, cancellationToken: ct));
        // Luồng 02 classification (receipt_match_results) is advisory; the
        // posting is canonical. Close its pending state in the same
        // transaction so the page never shows "Đang chờ duyệt" next to POSTED.
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE receipt_match_results
               SET review_status='confirmed', reviewed_by=@actorId, reviewed_at=NOW(), updated_at=NOW()
             WHERE voucher_id=@voucherId AND tenant_id=@tenantId AND department_id=@departmentId AND review_status='proposed'
            """, new { voucherId, tenantId, departmentId, actorId }, tx, cancellationToken: ct));

        var newBalance = await RecomputeAsync(conn, tx, tenantId, direction, cp.Id, cashId, actorId, "posted", ct);
        await tx.CommitAsync(ct);

        return await ResultAsync("posted", tenantId, departmentId, voucherId, cashId, previousBalance, newBalance, new[] { line }, ct);
    }

    public static class SourceTypes
    {
        public const string Voucher = "voucher";
        public const string BankStatementLine = "bank_statement_line";
    }

    /// <summary>Everything the canonical posting needs; only the SOURCE differs between Phiếu thu/chi and a statement line.</summary>
    private sealed record PostingSource(
        Guid TenantId, Guid DepartmentId, Guid VoucherId, string SourceType, Guid? BankTransactionId,
        string Direction, SettlementCounterparty Counterparty, decimal Amount, DateOnly TxDate, string Method,
        string CashAccount, Guid? BankAccountId, string DocumentNumber, string? Description, string? DocumentReference,
        string? BankReference);

    /// <summary>
    /// THE canonical posting (one accounting truth for every source): cash
    /// transaction -> payment applications (+ sub-ledger, open items) -> bank
    /// balance -> journal (Dr 111x/112x Cr 131, or Dr 331 Cr 111x/112x).
    /// Caller holds the source lock + counterparty/open-item locks and
    /// recomputes current_ar/current_ap. Returns null when the source already
    /// has a live posting (DB uniqueness per source).
    /// </summary>
    private static async Task<(Guid CashTxId, JournalLine Line)?> PostCoreAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, PostingSource s, List<PlannedAllocation> plan, Guid actorId, CancellationToken ct)
    {
        var cp = s.Counterparty;
        var cashTxId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition("""
            INSERT INTO cash_transactions (tenant_id, department_id, voucher_id, direction, counterparty_type,
                customer_id, supplier_id, amount, currency, transaction_date, payment_method, cash_account,
                counter_account, bank_account_id, document_number, description, document_reference, status, posted_by,
                source_type, source_bank_transaction_id, bank_reference)
            VALUES (@TenantId, @DepartmentId, @VoucherId, @Direction, @cpType,
                @customerId, @supplierId, @Amount, 'VND', @TxDate, @Method, @CashAccount,
                @counterAccount, @BankAccountId, @DocumentNumber, @Description, @DocumentReference, 'posted', @actorId,
                @SourceType, @BankTransactionId, @BankReference)
            ON CONFLICT DO NOTHING
            RETURNING id
            """, new
        {
            s.TenantId, s.DepartmentId, s.VoucherId, s.Direction, cpType = cp.Type,
            customerId = s.Direction == CashDirections.MoneyIn ? cp.Id : (Guid?)null,
            supplierId = s.Direction == CashDirections.MoneyOut ? cp.Id : (Guid?)null,
            s.Amount, s.TxDate, s.Method, s.CashAccount,
            counterAccount = s.Direction == CashDirections.MoneyIn ? "131" : "331",
            s.BankAccountId, s.DocumentNumber, s.Description, s.DocumentReference, actorId,
            s.SourceType, s.BankTransactionId, s.BankReference,
        }, tx, cancellationToken: ct));
        if (cashTxId is not { } cashId) return null;

        foreach (var a in plan)
        {
            await ApplyOneAsync(conn, tx, s.TenantId, s.DepartmentId, s.Direction, cp.Id, cashId, s.DocumentNumber, s.TxDate, a.Item, a.Amount, a.Method, actorId, ct);
        }
        if (s.BankAccountId is { } bankId)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE sim_bank_accounts SET current_balance = COALESCE(current_balance,0) + @delta WHERE id=@bankId AND tenant_id=@tenantId",
                new { delta = s.Direction == CashDirections.MoneyIn ? s.Amount : -s.Amount, bankId, tenantId = s.TenantId }, tx, cancellationToken: ct));
        }
        var line = BuildJournalLine(s.Direction, s.CashAccount, s.Amount, cp.Name);
        await InsertJournalAsync(conn, tx, s.VoucherId, cashId, line, "posting", actorId, ct);
        return (cashId, line);
    }

    private sealed record PlannedAllocation(SettlementOpenItem Item, decimal Amount, string Method);

    private static List<PlannedAllocation> PlanAllocations(
        MatchSuggestion? suggestion, IReadOnlyList<SettlementOpenItem> candidates, List<SettlementOpenItem> lockedItems,
        decimal available, PostCashRequest request)
    {
        if (request.Allocations is { Count: > 0 } explicitAllocations)
        {
            var suggested = suggestion is { Certainty: MatchCertainty.Certain } s
                ? s.Allocations.ToDictionary(x => x.InvoiceId)
                : new Dictionary<Guid, AllocationSuggestion>();
            return ValidateAllocations(lockedItems, explicitAllocations, available,
                (id, amt) => suggested.TryGetValue(id, out var sa) && sa.ApplyAmount == amt ? sa.MatchMethod : "admin_selected");
        }
        if (request.LeaveUnapplied) return new();

        if (suggestion is null || suggestion.Certainty == MatchCertainty.NoOpenItem) return new(); // all UNAPPLIED
        if (suggestion.Certainty != MatchCertainty.Certain)
        {
            throw new CashSettlementException("admin_selection_required",
                "Có nhiều hoá đơn/công nợ phù hợp — ADMIN cần chọn hoá đơn để hạch toán.",
                new { candidates, suggestion });
        }
        return ValidateAllocations(lockedItems,
            suggestion.Allocations.Select(a => new AllocationInput(a.InvoiceId, a.ApplyAmount)).ToList(), available,
            (_, _) => suggestion.Method ?? "admin_selected");
    }

    private static List<PlannedAllocation> ValidateAllocations(
        List<SettlementOpenItem> lockedItems, IReadOnlyList<AllocationInput> allocations, decimal available,
        Func<Guid, decimal, string> methodFor)
    {
        if (allocations.Select(a => a.InvoiceId).Distinct().Count() != allocations.Count)
            throw new CashSettlementException("duplicate_invoice_in_allocation", "Một hoá đơn xuất hiện nhiều lần trong phân bổ.");
        var byId = lockedItems.ToDictionary(i => i.Id);
        var plan = new List<PlannedAllocation>();
        foreach (var a in allocations.OrderBy(a => a.InvoiceId))
        {
            if (a.Amount <= 0m)
                throw new CashSettlementException("invalid_allocation_amount", "Số tiền phân bổ phải > 0.");
            if (!byId.TryGetValue(a.InvoiceId, out var item))
                throw new CashSettlementException("invoice_not_open_for_counterparty",
                    "Hoá đơn không thuộc đối tác này hoặc đã tất toán.", new { a.InvoiceId });
            if (a.Amount > item.Outstanding)
                throw new CashSettlementException("allocation_exceeds_outstanding",
                    $"Số tiền phân bổ vượt số còn nợ của hoá đơn {item.DocumentNumber}.", new { a.InvoiceId, item.Outstanding });
            plan.Add(new PlannedAllocation(item, a.Amount, methodFor(a.InvoiceId, a.Amount)));
        }
        if (plan.Sum(p => p.Amount) > available)
            throw new CashSettlementException("allocation_exceeds_available", "Tổng phân bổ vượt số tiền chưa phân bổ của chứng từ.", new { available });
        return plan;
    }

    private static async Task ApplyOneAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid departmentId, string direction, Guid counterpartyId,
        Guid cashTxId, string docNumber, DateOnly txDate, SettlementOpenItem item, decimal amount, string method,
        Guid actorId, CancellationToken ct)
    {
        var (ledger, amountCol, fkCol) = LedgerTable(direction);
        // Re-read under the lock already held — outstanding may have moved
        // since the (unlocked) context read.
        var inv = await conn.QuerySingleAsync<(decimal Original, decimal Paid)>(new CommandDefinition(
            $"SELECT {amountCol}, paid_amount FROM {ledger} WHERE id=@id AND tenant_id=@tenantId AND {fkCol}=@counterpartyId FOR UPDATE",
            new { id = item.Id, tenantId, counterpartyId }, tx, cancellationToken: ct));
        if (amount > inv.Original - inv.Paid)
            throw new CashSettlementException("allocation_exceeds_outstanding",
                $"Số tiền phân bổ vượt số còn nợ của hoá đơn {item.DocumentNumber}.", new { InvoiceId = item.Id, Outstanding = inv.Original - inv.Paid });

        var appId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition($"""
            INSERT INTO payment_applications (tenant_id, department_id, cash_transaction_id,
                {(direction == CashDirections.MoneyIn ? "ar_invoice_id" : "ap_invoice_id")},
                applied_amount, match_method, status, applied_by)
            VALUES (@tenantId, @departmentId, @cashTxId, @invoiceId, @amount, @method, 'active', @actorId)
            RETURNING id
            """, new { tenantId, departmentId, cashTxId, invoiceId = item.Id, amount, method, actorId }, tx, cancellationToken: ct));

        var newPaid = inv.Paid + amount;
        await conn.ExecuteAsync(new CommandDefinition(
            $"UPDATE {ledger} SET paid_amount=@newPaid, status=@status WHERE id=@id",
            new { newPaid, status = CashSettlementRules.OpenItemStatus(inv.Original, newPaid), id = item.Id }, tx, cancellationToken: ct));

        await InsertSubLedgerRowAsync(conn, tx, tenantId, direction, counterpartyId, cashTxId, appId, item,
            docNumber, txDate, amount, reversal: false, ct);
    }

    private static async Task InsertSubLedgerRowAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, string direction, Guid counterpartyId,
        Guid cashTxId, Guid appId, SettlementOpenItem item, string docNumber, DateOnly date, decimal amount,
        bool reversal, CancellationToken ct)
    {
        var (ledger, _, fkCol) = LedgerTable(direction);
        var moneyIn = direction == CashDirections.MoneyIn;
        // AR: receipt credits 131 (reduces AR), reversal debits it back.
        // AP: payment debits 331 (reduces AP), reversal credits it back.
        var docType = (moneyIn ? "receipt" : "payment") + (reversal ? "_reversal" : "");
        var debit = moneyIn == reversal ? amount : 0m;
        var credit = moneyIn == reversal ? 0m : amount;
        await conn.ExecuteAsync(new CommandDefinition($"""
            INSERT INTO {ledger} (tenant_id, {fkCol}, document_date, document_number, document_type,
                debit, credit, reference_note, allocated_invoice_id, payment_application_id)
            VALUES (@tenantId, @counterpartyId, @date, @docNumber, @docType,
                @debit, @credit, @note, @invoiceId, @appId)
            """, new
        {
            tenantId, counterpartyId, date, docNumber, docType, debit, credit,
            note = $"cash_tx:{cashTxId:N}|app:{appId:N}|inv:{item.DocumentNumber}" + (reversal ? "|reversal" : ""),
            invoiceId = item.Id, appId,
        }, tx, cancellationToken: ct));
    }

    private static Task InsertJournalAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid voucherId, Guid cashTxId, JournalLine line, string source, Guid actorId, CancellationToken ct) =>
        InsertPostingLineAsync(conn, tx, voucherId, cashTxId, null, line, source, actorId, ct);

    /// <summary>
    /// The one canonical writer of authoritative journal lines: linked to the
    /// cash posting that produced it, or (non-cash) to the expense claim whose
    /// recognition produced it.
    /// </summary>
    internal static async Task InsertPostingLineAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid voucherId, Guid? cashTxId, Guid? expenseClaimId,
        JournalLine line, string source, Guid actorId, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO voucher_journal_suggestions (voucher_id, entry_no, debit_account, credit_account, amount,
                description, confidence, accepted_by, accepted_at, source, cash_transaction_id, expense_claim_id)
            SELECT @voucherId, COALESCE(MAX(entry_no),0)+1, @debit, @credit, @amount, @description, 1.00,
                   @actorId, NOW(), @source, @cashTxId, @expenseClaimId
            FROM voucher_journal_suggestions WHERE voucher_id=@voucherId
            """, new
        {
            voucherId, debit = line.DebitAccount, credit = line.CreditAccount, amount = line.Amount,
            description = line.Description, actorId, source, cashTxId, expenseClaimId,
        }, tx, cancellationToken: ct));
    }

    private static async Task<(Guid? Id, string? Gl)> ResolveBankAccountAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, IReadOnlyList<string> extractedAccounts, CancellationToken ct)
    {
        var digits = extractedAccounts
            .Select(a => new string(a.TakeWhile(c => char.IsDigit(c) || c == ' ' || c == '-' || c == '.').Where(char.IsDigit).ToArray()))
            .Where(d => d.Length >= 6).ToArray();
        var row = await conn.QueryFirstOrDefaultAsync<(Guid Id, string Gl)?>(new CommandDefinition("""
            SELECT id, gl_account_code FROM sim_bank_accounts
            WHERE tenant_id=@tenantId AND gl_account_code LIKE '112%'
              AND (regexp_replace(account_number, '\D', '', 'g') = ANY(@digits) OR is_primary)
            ORDER BY (regexp_replace(account_number, '\D', '', 'g') = ANY(@digits)) DESC, created_at
            LIMIT 1
            FOR UPDATE
            """, new { tenantId, digits }, tx, cancellationToken: ct));
        return row is { } r ? (r.Id, r.Gl) : (null, null);
    }

    /// <summary>
    /// Final step of every mutation (post, statement post, allocate, unapply,
    /// reverse), inside its transaction: balance recompute, then — for AR — the
    /// contract schedules of every invoice this cash transaction has ever applied
    /// to (active or reversed), so a reallocation recomputes both the old and the
    /// new schedule. A failure rolls back the whole accounting mutation.
    /// </summary>
    private static async Task<decimal> RecomputeAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, string direction, Guid counterpartyId,
        Guid cashTransactionId, Guid actorId, string cause, CancellationToken ct)
    {
        if (direction != CashDirections.MoneyIn)
            return await SupplierRepository.RecomputeCurrentApAsync(conn, tx, tenantId, counterpartyId, ct);

        var balance = await CustomerRepository.RecomputeCurrentArAsync(conn, tx, tenantId, counterpartyId, ct);
        var invoiceIds = (await conn.QueryAsync<Guid>(new CommandDefinition("""
            SELECT DISTINCT ar_invoice_id FROM payment_applications
            WHERE cash_transaction_id=@cashTransactionId AND ar_invoice_id IS NOT NULL
            """, new { cashTransactionId }, tx, cancellationToken: ct))).ToList();
        await Aiq.Gateway.Contracts.ContractScheduleRecompute.RecomputeForInvoicesAsync(
            conn, tx, tenantId, invoiceIds, actorId, cause, cashTransactionId, ct);
        return balance;
    }

    /// <summary>Contract schedules reached through the invoices these applications touched (explicit links only).</summary>
    private static Task<IReadOnlyList<Aiq.Gateway.Contracts.ContractScheduleImpact>> LoadContractSchedulesAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, Guid departmentId, CashTransactionRecord? live,
        IReadOnlyList<PaymentApplicationRecord> applications, CancellationToken ct)
    {
        var applied = applications
            .GroupBy(a => a.InvoiceId)
            .ToDictionary(g => g.Key, g => g.Where(a => a.Status == "active" && a.CashTransactionId == live?.Id).Sum(a => a.AppliedAmount));
        return Aiq.Gateway.Contracts.ContractScheduleRecompute.LoadImpactsAsync(conn, tx, tenantId, departmentId, applied, ct);
    }

    private async Task<SettlementMutationResult> ResultAsync(
        string outcome, Guid tenantId, Guid departmentId, Guid voucherId, Guid cashTxId,
        decimal previous, decimal next, IReadOnlyList<JournalLine> journal, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var posting = await LoadPostingByIdAsync(conn, null, cashTxId, ct);
        var apps = await LoadApplicationsAsync(conn, null, new[] { cashTxId }, ct);
        return new SettlementMutationResult(outcome, posting, apps, previous, next, journal);
    }

    // ------------------------------------------------------------------
    // Allocate later / unapply / reverse
    // ------------------------------------------------------------------

    private sealed record LockedPosting(Guid Id, string Direction, Guid CounterpartyId, decimal Amount, string DocumentNumber,
        DateOnly TransactionDate, string CashAccount, Guid? BankAccountId, string SourceType);

    /// <summary>
    /// Lock order shared by every source: voucher -> (statement line) ->
    /// live posting -> counterparty -> invoice rows. bankTransactionId null =
    /// the voucher's own posting (Phiếu thu/chi); set = that statement line's.
    /// </summary>
    private static async Task<LockedPosting?> LockPostingAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid departmentId, Guid voucherId, CancellationToken ct,
        Guid? bankTransactionId = null)
    {
        var voucherExists = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM vouchers WHERE id=@voucherId AND tenant_id=@tenantId AND department_id=@departmentId FOR UPDATE",
            new { voucherId, tenantId, departmentId }, tx, cancellationToken: ct));
        if (voucherExists is null) throw new KeyNotFoundException();
        if (bankTransactionId is { } lineId)
        {
            var lineExists = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition("""
                SELECT id FROM sim_bank_transactions
                WHERE id=@lineId AND voucher_id=@voucherId AND tenant_id=@tenantId AND department_id=@departmentId FOR UPDATE
                """, new { lineId, voucherId, tenantId, departmentId }, tx, cancellationToken: ct));
            if (lineExists is null) throw new KeyNotFoundException();
        }
        return await conn.QuerySingleOrDefaultAsync<LockedPosting>(new CommandDefinition($"""
            SELECT id AS Id, direction AS Direction, COALESCE(customer_id, supplier_id) AS CounterpartyId,
                   amount AS Amount, document_number AS DocumentNumber, transaction_date AS TransactionDate,
                   cash_account AS CashAccount, bank_account_id AS BankAccountId, source_type AS SourceType
            FROM cash_transactions
            WHERE voucher_id=@voucherId AND tenant_id=@tenantId AND department_id=@departmentId AND status='posted'
              AND {(bankTransactionId is null ? "source_type='voucher'" : "source_bank_transaction_id=@bankTransactionId")}
            FOR UPDATE
            """, new { voucherId, tenantId, departmentId, bankTransactionId }, tx, cancellationToken: ct));
    }

    private static async Task<decimal> LockCounterpartyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, string direction, Guid counterpartyId, CancellationToken ct)
    {
        var (table, balanceCol, _, _) = CounterpartyTable(direction);
        return await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
            $"SELECT COALESCE({balanceCol},0) FROM {table} WHERE id=@counterpartyId AND tenant_id=@tenantId FOR UPDATE",
            new { counterpartyId, tenantId }, tx, cancellationToken: ct));
    }

    public Task<SettlementMutationResult?> AllocateAsync(
        Guid tenantId, Guid departmentId, Guid voucherId, Guid actorId, IReadOnlyList<AllocationInput> allocations, CancellationToken ct) =>
        AllocateCoreAsync(tenantId, departmentId, voucherId, null, actorId, allocations, ct);

    internal const string DocumentGatePrefix = "document_review_required:";

    internal static IEnumerable<string> DocumentGateBlockers(IEnumerable<string> reasons) =>
        reasons.Select(r => DocumentGatePrefix + r);

    /// <summary>Structured refusal for a document-level blocker (see DocumentAccountingGate).</summary>
    internal static void ThrowIfDocumentGated(IEnumerable<string> blockers)
    {
        var reasons = blockers.Where(b => b.StartsWith(DocumentGatePrefix, StringComparison.Ordinal))
            .Select(b => b[DocumentGatePrefix.Length..]).ToList();
        if (reasons.Count > 0)
            throw new CashSettlementException(Aiq.Gateway.Vouchers.DocumentProcessing.DocumentReviewRequired.Code,
                Aiq.Gateway.Vouchers.DocumentProcessing.DocumentReviewRequired.Message, new DocumentGateDetails(reasons));
    }

    private async Task<SettlementMutationResult?> AllocateCoreAsync(
        Guid tenantId, Guid departmentId, Guid voucherId, Guid? bankTransactionId, Guid actorId, IReadOnlyList<AllocationInput> allocations, CancellationToken ct)
    {
        if (allocations.Count == 0) throw new CashSettlementException("allocations_required", "Chọn ít nhất một hoá đơn để phân bổ.");
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        LockedPosting? posting;
        try { posting = await LockPostingAsync(conn, tx, tenantId, departmentId, voucherId, ct, bankTransactionId); }
        catch (KeyNotFoundException) { return null; }
        if (posting is null) throw new CashSettlementException("not_posted", "Chứng từ chưa được duyệt & ghi nhận.");
        // New applications are an authoritative AR/AP mutation: same document
        // gate as posting (unapply / reverse stay available to undo).
        ThrowIfDocumentGated(DocumentGateBlockers(
            await Aiq.Gateway.Vouchers.DocumentProcessing.DocumentAccountingGate.LoadReasonsAsync(conn, tx, tenantId, voucherId, ct) ?? []));

        var previous = await LockCounterpartyAsync(conn, tx, tenantId, posting.Direction, posting.CounterpartyId, ct);
        var items = await LoadOpenItemsAsync(conn, tx, tenantId, posting.Direction, posting.CounterpartyId, lockRows: true, ct);

        var active = (await conn.QueryAsync<(Guid InvoiceId, decimal Amount)>(new CommandDefinition("""
            SELECT COALESCE(ar_invoice_id, ap_invoice_id), applied_amount FROM payment_applications
            WHERE cash_transaction_id=@id AND status='active'
            """, new { id = posting.Id }, tx, cancellationToken: ct))).ToList();

        // Retry-safety: an allocation identical to an already-active one is a
        // no-op; a different amount on the same invoice must go through
        // unapply first (auditable), never a second concurrent slice.
        var fresh = new List<AllocationInput>();
        foreach (var a in allocations)
        {
            var existing = active.Where(x => x.InvoiceId == a.InvoiceId).ToList();
            if (existing.Count == 0) { fresh.Add(a); continue; }
            if (existing.Sum(x => x.Amount) != a.Amount)
                throw new CashSettlementException("already_applied_to_invoice", "Chứng từ đã được phân bổ vào hoá đơn này — huỷ phân bổ trước khi đổi số tiền.", new { a.InvoiceId });
        }
        if (fresh.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return await ResultAsync("already_allocated", tenantId, departmentId, voucherId, posting.Id, previous, previous, Array.Empty<JournalLine>(), ct);
        }

        var unapplied = posting.Amount - active.Sum(x => x.Amount);
        var plan = ValidateAllocations(items, fresh, unapplied, (_, _) => "admin_selected");
        foreach (var p in plan)
        {
            await ApplyOneAsync(conn, tx, tenantId, departmentId, posting.Direction, posting.CounterpartyId, posting.Id,
                posting.DocumentNumber, posting.TransactionDate, p.Item, p.Amount, p.Method, actorId, ct);
        }
        var next = await RecomputeAsync(conn, tx, tenantId, posting.Direction, posting.CounterpartyId, posting.Id, actorId, "allocated", ct);
        await tx.CommitAsync(ct);
        return await ResultAsync("allocated", tenantId, departmentId, voucherId, posting.Id, previous, next, Array.Empty<JournalLine>(), ct);
    }

    public Task<SettlementMutationResult?> UnapplyAsync(
        Guid tenantId, Guid departmentId, Guid voucherId, Guid actorId, Guid? applicationId, string? reason, CancellationToken ct) =>
        UnapplyCoreAsync(tenantId, departmentId, voucherId, null, actorId, applicationId, reason, ct);

    private async Task<SettlementMutationResult?> UnapplyCoreAsync(
        Guid tenantId, Guid departmentId, Guid voucherId, Guid? bankTransactionId, Guid actorId, Guid? applicationId, string? reason, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        LockedPosting? posting;
        try { posting = await LockPostingAsync(conn, tx, tenantId, departmentId, voucherId, ct, bankTransactionId); }
        catch (KeyNotFoundException) { return null; }
        if (posting is null) throw new CashSettlementException("not_posted", "Chứng từ chưa được duyệt & ghi nhận.");

        var previous = await LockCounterpartyAsync(conn, tx, tenantId, posting.Direction, posting.CounterpartyId, ct);
        var count = await UnapplyInternalAsync(conn, tx, tenantId, posting, actorId, applicationId, reason, ct);
        if (count == 0)
        {
            await tx.RollbackAsync(ct);
            return await ResultAsync("nothing_to_unapply", tenantId, departmentId, voucherId, posting.Id, previous, previous, Array.Empty<JournalLine>(), ct);
        }
        var next = await RecomputeAsync(conn, tx, tenantId, posting.Direction, posting.CounterpartyId, posting.Id, actorId, "unapplied", ct);
        await tx.CommitAsync(ct);
        return await ResultAsync("unapplied", tenantId, departmentId, voucherId, posting.Id, previous, next, Array.Empty<JournalLine>(), ct);
    }

    private static async Task<int> UnapplyInternalAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, LockedPosting posting, Guid actorId,
        Guid? applicationId, string? reason, CancellationToken ct)
    {
        var (ledger, amountCol, _) = LedgerTable(posting.Direction);
        var apps = (await conn.QueryAsync<(Guid Id, Guid InvoiceId, decimal Amount)>(new CommandDefinition("""
            SELECT id, COALESCE(ar_invoice_id, ap_invoice_id), applied_amount FROM payment_applications
            WHERE cash_transaction_id=@cashTxId AND status='active' AND (@applicationId::uuid IS NULL OR id=@applicationId)
            ORDER BY COALESCE(ar_invoice_id, ap_invoice_id)
            FOR UPDATE
            """, new { cashTxId = posting.Id, applicationId }, tx, cancellationToken: ct))).ToList();

        foreach (var app in apps)
        {
            var inv = await conn.QuerySingleAsync<(string Doc, decimal Original, decimal Paid)>(new CommandDefinition(
                $"SELECT document_number, {amountCol}, paid_amount FROM {ledger} WHERE id=@id FOR UPDATE",
                new { id = app.InvoiceId }, tx, cancellationToken: ct));
            var restored = Math.Max(inv.Paid - app.Amount, 0m);
            await conn.ExecuteAsync(new CommandDefinition(
                $"UPDATE {ledger} SET paid_amount=@restored, status=@status WHERE id=@id",
                new { restored, status = CashSettlementRules.OpenItemStatus(inv.Original, restored), id = app.InvoiceId }, tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE payment_applications
                   SET status='reversed', reversed_by=@actorId, reversed_at=NOW(), reversal_reason=@reason
                 WHERE id=@id
                """, new { actorId, reason, id = app.Id }, tx, cancellationToken: ct));
            await InsertSubLedgerRowAsync(conn, tx, tenantId, posting.Direction, posting.CounterpartyId, posting.Id, app.Id,
                new SettlementOpenItem(app.InvoiceId, inv.Doc, default, null, inv.Original, restored, inv.Original - restored, ""),
                posting.DocumentNumber, Today, app.Amount, reversal: true, ct);
        }
        return apps.Count;
    }

    public Task<SettlementMutationResult?> ReverseAsync(
        Guid tenantId, Guid departmentId, Guid voucherId, Guid actorId, string? reason, CancellationToken ct) =>
        ReverseCoreAsync(tenantId, departmentId, voucherId, null, actorId, reason, ct);

    private async Task<SettlementMutationResult?> ReverseCoreAsync(
        Guid tenantId, Guid departmentId, Guid voucherId, Guid? bankTransactionId, Guid actorId, string? reason, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        LockedPosting? posting;
        try { posting = await LockPostingAsync(conn, tx, tenantId, departmentId, voucherId, ct, bankTransactionId); }
        catch (KeyNotFoundException) { return null; }
        if (posting is null)
        {
            await tx.RollbackAsync(ct);
            return new SettlementMutationResult("nothing_to_reverse", null, Array.Empty<PaymentApplicationRecord>(), 0m, 0m, Array.Empty<JournalLine>());
        }

        var previous = await LockCounterpartyAsync(conn, tx, tenantId, posting.Direction, posting.CounterpartyId, ct);
        await UnapplyInternalAsync(conn, tx, tenantId, posting, actorId, null, reason ?? "posting_reversed", ct);

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE cash_transactions SET status='reversed', reversed_by=@actorId, reversed_at=NOW(), reversal_reason=@reason
            WHERE id=@id
            """, new { actorId, reason, id = posting.Id }, tx, cancellationToken: ct));
        if (posting.BankAccountId is { } bankId)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE sim_bank_accounts SET current_balance = COALESCE(current_balance,0) - @delta WHERE id=@bankId AND tenant_id=@tenantId",
                new { delta = posting.Direction == CashDirections.MoneyIn ? posting.Amount : -posting.Amount, bankId, tenantId }, tx, cancellationToken: ct));
        }
        var original = BuildJournalLine(posting.Direction, posting.CashAccount, posting.Amount, null);
        var reversalLine = new JournalLine(original.CreditAccount, original.DebitAccount, posting.Amount,
            $"Đảo bút toán {posting.DocumentNumber}" + (string.IsNullOrWhiteSpace(reason) ? "" : $" — {reason}"));
        await InsertJournalAsync(conn, tx, voucherId, posting.Id, reversalLine, "reversal", actorId, ct);
        if (posting.SourceType == SourceTypes.Voucher)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE vouchers SET status='extracted' WHERE id=@voucherId AND tenant_id=@tenantId AND department_id=@departmentId",
                new { voucherId, tenantId, departmentId }, tx, cancellationToken: ct));
            // Posting voided -> the voucher awaits approval again.
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE receipt_match_results
                   SET review_status='proposed', reviewed_by=NULL, reviewed_at=NULL, updated_at=NOW()
                 WHERE voucher_id=@voucherId AND tenant_id=@tenantId AND department_id=@departmentId AND review_status='confirmed'
                """, new { voucherId, tenantId, departmentId }, tx, cancellationToken: ct));
        }
        else if (bankTransactionId is { } lineId)
        {
            // The statement line stays as bank evidence; it is simply no
            // longer accounted for (eligible for a corrected repost).
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE sim_bank_transactions SET reconciled = FALSE WHERE id=@lineId",
                new { lineId }, tx, cancellationToken: ct));
        }

        var next = await RecomputeAsync(conn, tx, tenantId, posting.Direction, posting.CounterpartyId, posting.Id, actorId, "reversed", ct);
        await tx.CommitAsync(ct);
        return await ResultAsync("reversed", tenantId, departmentId, voucherId, posting.Id, previous, next, new[] { reversalLine }, ct);
    }
}
