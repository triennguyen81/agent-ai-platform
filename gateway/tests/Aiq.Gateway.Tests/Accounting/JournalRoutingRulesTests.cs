using Aiq.Gateway.Accounting;

namespace Aiq.Gateway.Tests.Accounting;

/// <summary>
/// P1 audit fix — JournalRoutingRules is pure/deterministic, so it's tested
/// directly here rather than through the E2E scripts (which only assert
/// through the HTTP surface).
/// </summary>
public sealed class JournalRoutingRulesTests
{
    [Theory]
    [InlineData("FULL_MATCH", "CK", "1121", "131")]
    [InlineData("FULL_MATCH", "TM", "1111", "131")]
    [InlineData("PARTIAL_PAYMENT", "CK", "1121", "131")]
    public void ReceiptOutcome_NormalMatch_RoutesToCashOrBankVs131(string outcome, string method, string expectedDebit, string expectedCredit)
    {
        var result = JournalRoutingRules.ForReceiptOutcome(outcome, method, 1_000_000m, 1_000_000m, 0m, fxRateConfigured: false);

        Assert.Equal(JournalRoutingOutcome.Proposed, result.Outcome);
        var entry = Assert.Single(result.Entries);
        Assert.Equal(expectedDebit, entry.DebitAccount);
        Assert.Equal(expectedCredit, entry.CreditAccount);
        Assert.True(JournalRoutingRules.IsAllowed(entry.DebitAccount, entry.CreditAccount));
    }

    [Fact]
    public void ReceiptOutcome_Overpayment_SplitsMatchedAndExcessInto131And3388()
    {
        var result = JournalRoutingRules.ForReceiptOutcome("OVERPAYMENT", "CK", 2_500_000m, 2_000_000m, 500_000m, fxRateConfigured: false);

        Assert.Equal(JournalRoutingOutcome.Proposed, result.Outcome);
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(("1121", "131", 2_000_000m), (result.Entries[0].DebitAccount, result.Entries[0].CreditAccount, result.Entries[0].Amount));
        Assert.Equal(("1121", "3388", 500_000m), (result.Entries[1].DebitAccount, result.Entries[1].CreditAccount, result.Entries[1].Amount));
    }

    [Fact]
    public void ReceiptOutcome_Advance_RoutesToTraTruoc131_NeverAutoSelects3387()
    {
        var result = JournalRoutingRules.ForReceiptOutcome("ADVANCE_PAYMENT", "CK", 1_000_000m, 0m, 1_000_000m, fxRateConfigured: false);

        Assert.Equal(JournalRoutingOutcome.Proposed, result.Outcome);
        var entry = Assert.Single(result.Entries);
        Assert.Equal("131", entry.CreditAccount);
    }

    [Theory]
    [InlineData("UNKNOWN_PAYER")]
    [InlineData("CUSTOMER_NO_OPEN_RECEIVABLE")]
    [InlineData("REVERSED_TRANSACTION")]
    public void ReceiptOutcome_AmbiguousSituations_NeverInventAnEntry(string outcome)
    {
        var result = JournalRoutingRules.ForReceiptOutcome(outcome, "CK", 300_000m, 0m, 300_000m, fxRateConfigured: false);

        Assert.Equal(JournalRoutingOutcome.NeedsReview, result.Outcome);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void ReceiptOutcome_ForeignCurrency_BlockedWithoutAConfiguredRateSource()
    {
        var result = JournalRoutingRules.ForReceiptOutcome("FOREIGN_CURRENCY", "CK", 500m, 0m, 500m, fxRateConfigured: false);

        Assert.Equal(JournalRoutingOutcome.Blocked, result.Outcome);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void ReceiptOutcome_UnknownPaymentMethod_NeedsReviewInsteadOfGuessingCashVsBank()
    {
        var result = JournalRoutingRules.ForReceiptOutcome("FULL_MATCH", null, 1_000_000m, 1_000_000m, 0m, fxRateConfigured: false);

        Assert.Equal(JournalRoutingOutcome.NeedsReview, result.Outcome);
        Assert.Equal("payment_method_unknown", result.Reason);
    }

    [Fact]
    public void ExpenseSettlement_SplitsDebitAcrossAdvanceThenPayable_MatchingWorkedExample()
    {
        // PDF worked example: Nợ 6417 5,813,636 + Nợ 6428 800,000 + Nợ 1331
        // 616,364 / Có 141 5,000,000 + Có 334 2,230,000 (approvedTotal
        // 7,230,000 - advance 5,000,000 = payable 2,230,000).
        var approvedByCategory = new Dictionary<string, decimal> { ["travel"] = 5_813_636m, ["meal"] = 800_000m };
        var categoryAccounts = new Dictionary<string, string> { ["travel"] = "641", ["meal"] = "642" };

        var result = JournalRoutingRules.ForExpenseSettlement(approvedByCategory, categoryAccounts, 616_364m, 5_000_000m, 2_230_000m);

        Assert.Equal(JournalRoutingOutcome.Proposed, result.Outcome);
        foreach (var e in result.Entries)
        {
            Assert.True(JournalRoutingRules.IsAllowed(e.DebitAccount, e.CreditAccount), $"{e.DebitAccount}/{e.CreditAccount} not allowed");
        }
        Assert.Equal(5_000_000m, result.Entries.Where(e => e.CreditAccount == "141").Sum(e => e.Amount));
        Assert.Equal(2_230_000m, result.Entries.Where(e => e.CreditAccount == "334").Sum(e => e.Amount));
        Assert.Equal(7_230_000m, result.Entries.Sum(e => e.Amount));
    }

    [Fact]
    public void ExpenseSettlement_NegativePayable_RoutesRecoveryLeg()
    {
        var approvedByCategory = new Dictionary<string, decimal> { ["meal"] = 1_000_000m };
        var categoryAccounts = new Dictionary<string, string> { ["meal"] = "641" };

        var result = JournalRoutingRules.ForExpenseSettlement(approvedByCategory, categoryAccounts, 0m, 3_000_000m, -2_000_000m);

        Assert.Equal(JournalRoutingOutcome.Proposed, result.Outcome);
        Assert.Contains(result.Entries, e => e.DebitAccount == "334" && e.CreditAccount == "141" && e.Amount == 2_000_000m);
    }

    [Fact]
    public void ExpenseSettlement_UnconfiguredCategoryAccount_NeedsReview_NeverInventsAMapping()
    {
        var approvedByCategory = new Dictionary<string, decimal> { ["gift"] = 300_000m };
        var categoryAccounts = new Dictionary<string, string>(); // nothing configured

        var result = JournalRoutingRules.ForExpenseSettlement(approvedByCategory, categoryAccounts, 0m, 0m, 300_000m);

        Assert.Equal(JournalRoutingOutcome.NeedsReview, result.Outcome);
        Assert.Empty(result.Entries);
        Assert.Contains("gift", result.Reason);
    }

    [Fact]
    public void AllowedPairs_RejectsAnArbitraryOutOfListAccount()
    {
        Assert.False(JournalRoutingRules.IsAllowed("9999", "131"));
    }
}
