namespace Aiq.Gateway.Payments;

// ============================================================================
// Contract payment (early-settlement) discount — deterministic eligibility.
//
// Authoritative inputs ONLY: the Contract's structured term revision
// (contract_payment_discount_terms), the posted invoice (sim_ar_ledger row,
// explicitly linked to the Contract by contract_id), and the receipt's own
// business date + amount. Nothing here reads LLM output, voucher extracted
// fields or browser input, and nothing here touches the database.
//
// First-version policy (Xuly-ctkt.pdf tr.6 defines only "validate time and
// amount" and "số tiền thu = tổng công nợ − chiết khấu"):
//   * basis = invoice_date; qualifying deadline = invoice_date + N days,
//     INCLUSIVE (a receipt dated on the deadline qualifies);
//   * discount = invoice amount × rate, rounded to whole VND
//     (MidpointRounding.AwayFromZero — the convention PayinCalculator uses);
//   * eligible only for FULL settlement of an invoice with no prior
//     settlement, within the window: cash ≥ invoice − discount. Partial
//     payments are NOT discounted (partial_payment_discount_policy_undefined);
//   * cash above (invoice − discount) stays unapplied, exactly as today.
// ============================================================================

public static class PaymentDiscountStatus
{
    public const string Eligible = "eligible";
    public const string NotEligible = "not_eligible";
    /// <summary>No discount concept applies (no contract link / no term) — behaves exactly as before.</summary>
    public const string NotApplicable = "not_applicable";
}

public static class PaymentDiscountReasons
{
    public const string Eligible = "eligible_full_settlement_within_window";
    public const string NoContractLink = "no_contract_link";
    public const string ContractNotFound = "contract_not_found";
    public const string ContractScopeMismatch = "contract_scope_mismatch";
    public const string ContractNotSales = "contract_not_sales";
    public const string ContractNotEffective = "contract_not_effective";
    public const string NoDiscountTerm = "no_discount_term";
    public const string TermDisabled = "discount_term_disabled";
    public const string UnsupportedBasis = "discount_basis_unsupported";
    public const string ContractCustomerMismatch = "contract_customer_mismatch";
    public const string CurrencyMismatch = "currency_mismatch";
    public const string ReceiptDateMissing = "receipt_date_missing";
    public const string ReceiptBeforeBasisDate = "receipt_before_basis_date";
    public const string PaymentAfterDeadline = "payment_after_deadline";
    public const string PartialPolicyUndefined = "partial_payment_discount_policy_undefined";
    public const string DiscountRoundsToZero = "discount_rounds_to_zero";
}

public static class PaymentDiscountBases
{
    public const string InvoiceDate = "invoice_date";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal) { InvoiceDate };
}

/// <summary>The contract states the existing workflow treats as approved (immutable after ADMIN review).</summary>
public static class ContractEffectiveStates
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { "APPROVED", "HANDED_OFF" };
}

public sealed record DiscountTermSnapshot(
    Guid TermId, int Revision, bool Enabled, decimal RatePercent, string Basis, int DaysCount, string Currency);

public sealed record DiscountContractSnapshot(
    Guid ContractId, Guid TenantId, Guid DepartmentId, string WorkflowState, string? Direction, IReadOnlyList<string> BuyerTaxCodes);

public sealed record DiscountInvoiceSnapshot(
    Guid InvoiceId, string DocumentNumber, Guid CustomerId, string? CustomerTaxCode, Guid? ContractId,
    DateOnly InvoiceDate, decimal OriginalAmount, decimal PaidAmount);

/// <summary>The receipt side: business date (voucher document date / bank tx date — never "today"), cash available, currency.</summary>
public sealed record DiscountReceipt(Guid TenantId, Guid DepartmentId, DateOnly? ReceiptDate, decimal CashAvailable, string Currency);

public sealed record PaymentDiscountEvaluation(
    string Status,
    string ReasonCode,
    Guid InvoiceId,
    string InvoiceDocumentNumber,
    Guid? ContractId,
    Guid? TermId,
    int? TermRevision,
    decimal? RatePercent,
    string? Basis,
    int? DaysCount,
    DateOnly? BasisDate,
    DateOnly? QualifyingDeadline,
    DateOnly? ReceiptDate,
    decimal OpenAmount,
    decimal CashAvailable,
    decimal? DiscountBasisAmount,
    decimal CalculatedDiscount,
    decimal CashToApply,
    decimal RemainingCashRequired,
    decimal ExcessCash)
{
    public bool Eligible => Status == PaymentDiscountStatus.Eligible;
}

public static class PaymentDiscountCalculator
{
    public static decimal RoundMoney(decimal amount) => Math.Round(amount, 0, MidpointRounding.AwayFromZero);

    public static PaymentDiscountEvaluation Evaluate(
        DiscountInvoiceSnapshot invoice, DiscountContractSnapshot? contract, DiscountTermSnapshot? term, DiscountReceipt receipt)
    {
        var open = invoice.OriginalAmount - invoice.PaidAmount;
        PaymentDiscountEvaluation Result(string status, string reason, DateOnly? basisDate = null, DateOnly? deadline = null,
            decimal? basisAmount = null, decimal discount = 0m, decimal cashToApply = 0m, decimal remaining = 0m, decimal excess = 0m) =>
            new(status, reason, invoice.InvoiceId, invoice.DocumentNumber, invoice.ContractId,
                term?.TermId, term?.Revision, term?.RatePercent, term?.Basis, term?.DaysCount,
                basisDate, deadline, receipt.ReceiptDate, open, receipt.CashAvailable, basisAmount, discount, cashToApply, remaining, excess);

        // Explicit invoice → contract link only (never inferred from customer, amount or schedule).
        if (invoice.ContractId is null) return Result(PaymentDiscountStatus.NotApplicable, PaymentDiscountReasons.NoContractLink);
        if (contract is null || contract.ContractId != invoice.ContractId)
            return Result(PaymentDiscountStatus.NotEligible, PaymentDiscountReasons.ContractNotFound);
        if (contract.TenantId != receipt.TenantId || contract.DepartmentId != receipt.DepartmentId)
            return Result(PaymentDiscountStatus.NotEligible, PaymentDiscountReasons.ContractScopeMismatch);
        if (term is null) return Result(PaymentDiscountStatus.NotApplicable, PaymentDiscountReasons.NoDiscountTerm);
        if (!term.Enabled) return Result(PaymentDiscountStatus.NotApplicable, PaymentDiscountReasons.TermDisabled);
        // The sales relationship is established by authoritative data: the
        // invoice is a POSTED AR row and (below) its customer must be the
        // contract's buyer. contracts.direction is only a hint here — it can
        // legitimately be needs_review (resolved from tenants.company_tax_id,
        // which may be unset) — but an explicit purchase contract never
        // discounts a receivable.
        if (string.Equals(contract.Direction, "purchase", StringComparison.Ordinal))
            return Result(PaymentDiscountStatus.NotEligible, PaymentDiscountReasons.ContractNotSales);
        if (!ContractEffectiveStates.All.Contains(contract.WorkflowState))
            return Result(PaymentDiscountStatus.NotEligible, PaymentDiscountReasons.ContractNotEffective);
        if (!PaymentDiscountBases.Supported.Contains(term.Basis))
            return Result(PaymentDiscountStatus.NotEligible, PaymentDiscountReasons.UnsupportedBasis);
        // The invoice's customer must be the contract's buyer (sales direction).
        if (string.IsNullOrWhiteSpace(invoice.CustomerTaxCode)
            || !contract.BuyerTaxCodes.Any(t => string.Equals(t?.Trim(), invoice.CustomerTaxCode.Trim(), StringComparison.Ordinal)))
            return Result(PaymentDiscountStatus.NotEligible, PaymentDiscountReasons.ContractCustomerMismatch);
        if (!string.Equals(term.Currency, receipt.Currency, StringComparison.OrdinalIgnoreCase))
            return Result(PaymentDiscountStatus.NotEligible, PaymentDiscountReasons.CurrencyMismatch);

        var basisDate = invoice.InvoiceDate;
        var deadline = basisDate.AddDays(term.DaysCount);
        if (receipt.ReceiptDate is not { } received)
            return Result(PaymentDiscountStatus.NotEligible, PaymentDiscountReasons.ReceiptDateMissing, basisDate, deadline);
        if (received < basisDate)
            return Result(PaymentDiscountStatus.NotEligible, PaymentDiscountReasons.ReceiptBeforeBasisDate, basisDate, deadline);
        if (received > deadline)
            return Result(PaymentDiscountStatus.NotEligible, PaymentDiscountReasons.PaymentAfterDeadline, basisDate, deadline);

        var discount = RoundMoney(invoice.OriginalAmount * term.RatePercent / 100m);
        var required = invoice.OriginalAmount - discount;
        // An invoice already partly settled: whether/how a discount applies to
        // the remainder is a policy the requirement does not define.
        if (invoice.PaidAmount > 0m)
            return Result(PaymentDiscountStatus.NotEligible, PaymentDiscountReasons.PartialPolicyUndefined, basisDate, deadline,
                invoice.OriginalAmount, 0m, 0m, Math.Max(0m, open - receipt.CashAvailable));
        if (discount <= 0m)
            return Result(PaymentDiscountStatus.NotEligible, PaymentDiscountReasons.DiscountRoundsToZero, basisDate, deadline, invoice.OriginalAmount);
        if (receipt.CashAvailable < required)
            return Result(PaymentDiscountStatus.NotEligible, PaymentDiscountReasons.PartialPolicyUndefined, basisDate, deadline,
                invoice.OriginalAmount, 0m, 0m, required - receipt.CashAvailable);

        return Result(PaymentDiscountStatus.Eligible, PaymentDiscountReasons.Eligible, basisDate, deadline,
            invoice.OriginalAmount, discount, required, 0m, receipt.CashAvailable - required);
    }
}

/// <summary>
/// The accounting boundary for a confirmed payment discount. AIQ has NO
/// accountant-approved deterministic journal rule for it:
/// JournalRoutingRules.AllowedPairs contains no discount pair, the
/// requirement (Xuly-ctkt.pdf) names no account, and PayinCalculator's
/// "Nợ 635" text is a description, not an approved routing rule. Until such a
/// policy exists, confirmation stops here with
/// payment_discount_accounting_policy_missing — no account is ever taken
/// from a contract, a browser or an LLM.
/// </summary>
public static class PaymentDiscountAccountingPolicy
{
    public const string MissingCode = "payment_discount_accounting_policy_missing";

    /// <summary>What an accountant must define before a discount can be applied.</summary>
    public static readonly IReadOnlyList<string> MissingDecisions =
    [
        "debit_account_for_payment_discount",
        "journal_rule_payment_discount_vs_131",
        "discount_settlement_representation_and_reversal",
    ];

    /// <summary>Null = no approved policy (always, in this version).</summary>
    public static string? ResolveDiscountDebitAccount() => null;
}
