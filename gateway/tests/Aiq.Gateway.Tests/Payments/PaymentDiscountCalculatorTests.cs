using Aiq.Gateway.Payments;

namespace Aiq.Gateway.Tests.Payments;

public sealed class PaymentDiscountCalculatorTests
{
    private static readonly Guid Tenant = Guid.NewGuid(), Dept = Guid.NewGuid(), ContractId = Guid.NewGuid(), Customer = Guid.NewGuid();
    private static readonly DateOnly InvoiceDate = new(2026, 10, 1);

    private static DiscountInvoiceSnapshot Invoice(decimal amount = 100_000_000m, decimal paid = 0m, Guid? contract = null, string? mst = "0309990001") =>
        new(Guid.NewGuid(), "HD-1", Customer, mst, contract ?? ContractId, InvoiceDate, amount, paid);

    private static DiscountContractSnapshot Contract(string state = "APPROVED", string? direction = "sales", Guid? dept = null) =>
        new(ContractId, Tenant, dept ?? Dept, state, direction, ["0309990001"]);

    private static DiscountTermSnapshot Term(decimal rate = 2m, int days = 10, bool enabled = true, string basis = "invoice_date", string currency = "VND") =>
        new(Guid.NewGuid(), 1, enabled, rate, basis, days, currency);

    private static DiscountReceipt Receipt(int day, decimal cash, string currency = "VND") =>
        new(Tenant, Dept, new DateOnly(2026, 10, day), cash, currency);

    [Fact]
    public void Spec_example_98m_on_day_8_is_eligible_for_2m()
    {
        var e = PaymentDiscountCalculator.Evaluate(Invoice(), Contract(), Term(), Receipt(8, 98_000_000m));
        Assert.True(e.Eligible);
        Assert.Equal((2_000_000m, 98_000_000m, 0m, new DateOnly(2026, 10, 11)), (e.CalculatedDiscount, e.CashToApply, e.ExcessCash, e.QualifyingDeadline!.Value));
    }

    [Theory]
    [InlineData(11, true)]
    [InlineData(12, false)]
    public void Deadline_is_inclusive(int day, bool eligible) =>
        Assert.Equal(eligible, PaymentDiscountCalculator.Evaluate(Invoice(), Contract(), Term(), Receipt(day, 98_000_000m)).Eligible);

    [Fact]
    public void Receipt_before_invoice_date_or_without_date_is_not_eligible()
    {
        Assert.Equal(PaymentDiscountReasons.ReceiptBeforeBasisDate,
            PaymentDiscountCalculator.Evaluate(Invoice(), Contract(), Term(), new DiscountReceipt(Tenant, Dept, new DateOnly(2026, 9, 30), 98_000_000m, "VND")).ReasonCode);
        Assert.Equal(PaymentDiscountReasons.ReceiptDateMissing,
            PaymentDiscountCalculator.Evaluate(Invoice(), Contract(), Term(), new DiscountReceipt(Tenant, Dept, null, 98_000_000m, "VND")).ReasonCode);
    }

    [Fact]
    public void Rounding_is_whole_vnd_away_from_zero()
    {
        // 1,234,567 × 1.5% = 18,518.505 → 18,519
        var e = PaymentDiscountCalculator.Evaluate(Invoice(1_234_567m), Contract(), Term(1.5m), Receipt(5, 1_216_048m));
        Assert.Equal((18_519m, 1_216_048m), (e.CalculatedDiscount, e.CashToApply));
        Assert.True(e.Eligible);
    }

    [Fact]
    public void Gates_in_order()
    {
        Assert.Equal(PaymentDiscountReasons.NoContractLink,
            PaymentDiscountCalculator.Evaluate(Invoice() with { ContractId = null }, Contract(), Term(), Receipt(5, 98_000_000m)).ReasonCode);
        Assert.Equal(PaymentDiscountStatus.NotApplicable,
            PaymentDiscountCalculator.Evaluate(Invoice(), Contract(), null, Receipt(5, 98_000_000m)).Status);
        Assert.Equal(PaymentDiscountReasons.TermDisabled,
            PaymentDiscountCalculator.Evaluate(Invoice(), Contract(), Term(enabled: false), Receipt(5, 98_000_000m)).ReasonCode);
        Assert.Equal(PaymentDiscountReasons.ContractNotEffective,
            PaymentDiscountCalculator.Evaluate(Invoice(), Contract("WAITING_REVIEW"), Term(), Receipt(5, 98_000_000m)).ReasonCode);
        Assert.Equal(PaymentDiscountReasons.ContractNotSales,
            PaymentDiscountCalculator.Evaluate(Invoice(), Contract(direction: "purchase"), Term(), Receipt(5, 98_000_000m)).ReasonCode);
        Assert.Equal(PaymentDiscountReasons.ContractScopeMismatch,
            PaymentDiscountCalculator.Evaluate(Invoice(), Contract(dept: Guid.NewGuid()), Term(), Receipt(5, 98_000_000m)).ReasonCode);
        Assert.Equal(PaymentDiscountReasons.ContractNotFound,
            PaymentDiscountCalculator.Evaluate(Invoice(), null, Term(), Receipt(5, 98_000_000m)).ReasonCode);
        Assert.Equal(PaymentDiscountReasons.UnsupportedBasis,
            PaymentDiscountCalculator.Evaluate(Invoice(), Contract(), Term(basis: "invoice_due_date"), Receipt(5, 98_000_000m)).ReasonCode);
        Assert.Equal(PaymentDiscountReasons.ContractCustomerMismatch,
            PaymentDiscountCalculator.Evaluate(Invoice(mst: "0300000001"), Contract(), Term(), Receipt(5, 98_000_000m)).ReasonCode);
        Assert.Equal(PaymentDiscountReasons.ContractCustomerMismatch,
            PaymentDiscountCalculator.Evaluate(Invoice(mst: null), Contract(), Term(), Receipt(5, 98_000_000m)).ReasonCode);
        Assert.Equal(PaymentDiscountReasons.CurrencyMismatch,
            PaymentDiscountCalculator.Evaluate(Invoice(), Contract(), Term(currency: "USD"), Receipt(5, 98_000_000m)).ReasonCode);
    }

    [Theory]
    [InlineData("sales", true)]
    [InlineData("needs_review", true)]   // direction unresolved: buyer = customer + posted AR decide
    [InlineData(null, true)]
    [InlineData("purchase", false)]
    public void Only_an_explicit_purchase_contract_is_excluded(string? direction, bool eligible) =>
        Assert.Equal(eligible, PaymentDiscountCalculator.Evaluate(Invoice(), Contract(direction: direction), Term(), Receipt(5, 98_000_000m)).Eligible);

    [Fact]
    public void Partial_payments_get_no_invented_discount()
    {
        var shortCash = PaymentDiscountCalculator.Evaluate(Invoice(), Contract(), Term(), Receipt(5, 97_999_999m));
        Assert.Equal((false, PaymentDiscountReasons.PartialPolicyUndefined, 0m, 1m),
            (shortCash.Eligible, shortCash.ReasonCode, shortCash.CalculatedDiscount, shortCash.RemainingCashRequired));
        var priorPaid = PaymentDiscountCalculator.Evaluate(Invoice(paid: 10_000_000m), Contract(), Term(), Receipt(5, 90_000_000m));
        Assert.Equal((false, PaymentDiscountReasons.PartialPolicyUndefined), (priorPaid.Eligible, priorPaid.ReasonCode));
    }

    [Fact]
    public void Excess_cash_stays_excess_and_discount_never_exceeds_the_term()
    {
        var e = PaymentDiscountCalculator.Evaluate(Invoice(), Contract(), Term(), Receipt(5, 105_000_000m));
        Assert.Equal((2_000_000m, 98_000_000m, 7_000_000m), (e.CalculatedDiscount, e.CashToApply, e.ExcessCash));
        Assert.Equal(e.OpenAmount, e.CashToApply + e.CalculatedDiscount);
    }

    [Fact]
    public void Tiny_invoice_whose_discount_rounds_to_zero_is_not_eligible() =>
        Assert.Equal(PaymentDiscountReasons.DiscountRoundsToZero,
            PaymentDiscountCalculator.Evaluate(Invoice(20m), Contract(), Term(), Receipt(5, 20m)).ReasonCode);

    [Fact]
    public void No_accounting_policy_exists_and_no_discount_pair_is_allowed()
    {
        Assert.Null(PaymentDiscountAccountingPolicy.ResolveDiscountDebitAccount());
        Assert.DoesNotContain(Aiq.Gateway.Accounting.JournalRoutingRules.AllowedPairs, p => p.Credit == "131" && p.Debit is "635" or "521" or "5211");
    }
}
