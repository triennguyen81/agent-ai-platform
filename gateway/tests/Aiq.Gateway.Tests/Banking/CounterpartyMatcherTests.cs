using Aiq.Gateway.Banking;

namespace Aiq.Gateway.Tests.Banking;

public sealed class CounterpartyMatcherTests
{
    private static readonly MatchParty VietTin = new(Guid.NewGuid(), "DEMO-CUS-VIETTIN", "Công ty TNHH Việt Tín", "0307890123");
    private static readonly MatchParty MinhLong = new(Guid.NewGuid(), "DEMO-CUS-MINHLONG", "Công ty TNHH Minh Long", "0315550001");

    private static AccountOwnerHit Acct(MatchParty p, string type = "customer", string? bank = "VCB", string? name = null) =>
        new(Guid.NewGuid(), type, p.Id, p.Name, bank, name ?? BankAccountNormalizer.NormalizeName(p.Name));

    private static MatchInputs In(
        string? account = "123456789", string? bank = "VCB", string? name = null,
        IReadOnlyList<AccountOwnerHit>? accounts = null, IReadOnlyList<MatchParty>? tax = null,
        IReadOnlyList<InvoiceRefHit>? invoices = null, IReadOnlyList<MatchParty>? codes = null,
        IReadOnlyList<MatchParty>? exact = null, IReadOnlyList<MatchParty>? similar = null,
        IReadOnlyList<MatchParty>? amount = null, string ownerType = "customer") =>
        new(ownerType, account, bank, name is null ? null : BankAccountNormalizer.NormalizeName(name),
            accounts ?? Array.Empty<AccountOwnerHit>(), tax ?? Array.Empty<MatchParty>(), invoices ?? Array.Empty<InvoiceRefHit>(),
            codes ?? Array.Empty<MatchParty>(), exact ?? Array.Empty<MatchParty>(), similar ?? Array.Empty<MatchParty>(),
            amount ?? Array.Empty<MatchParty>());

    [Fact]
    public void ExactKnownBankAccount_Matched_100()
    {
        var r = CounterpartyMatcher.Match(In(name: "CÔNG TY TNHH VIỆT TÍN", accounts: new[] { Acct(VietTin) }));
        Assert.Equal((MatchStatuses.Matched, MatchMethods.BankAccountExact, 1.00m, VietTin.Id), (r.Status, r.Method, r.Confidence, r.OwnerId));
        Assert.NotNull(r.BankAccountId);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void BankAccount_AbbreviatedUnaccentedName_StillClean()
    {
        var r = CounterpartyMatcher.Match(In(name: "CTY TNHH VIET TIN", accounts: new[] { Acct(VietTin) }));
        Assert.Equal(MatchStatuses.Matched, r.Status);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void BankAccount_ButCompletelyDifferentName_NeedsReview()
    {
        var r = CounterpartyMatcher.Match(In(name: "NGUYEN VAN A", accounts: new[] { Acct(VietTin) }));
        Assert.Equal(MatchStatuses.NeedsReview, r.Status);
        Assert.Contains(r.Warnings, w => w.Contains("khác hẳn"));
        Assert.Contains(r.Candidates, c => c.OwnerId == VietTin.Id);
    }

    [Fact]
    public void SameAccountOnTwoCustomers_NeedsReview_NeverPicksOne()
    {
        var r = CounterpartyMatcher.Match(In(bank: null, accounts: new[] { Acct(VietTin), Acct(MinhLong, bank: "ACB") }));
        Assert.Equal(MatchStatuses.NeedsReview, r.Status);
        Assert.Null(r.OwnerId);
        Assert.Equal(2, r.Candidates.Count);
    }

    [Fact]
    public void BankCode_Disambiguates_WhenBothSidesHaveOne()
    {
        var r = CounterpartyMatcher.Match(In(bank: "ACB", accounts: new[] { Acct(VietTin, bank: "VCB"), Acct(MinhLong, bank: "ACB") }));
        Assert.Equal((MatchStatuses.Matched, MinhLong.Id), (r.Status, r.OwnerId));
    }

    [Fact]
    public void TaxCode_Matched()
    {
        var r = CounterpartyMatcher.Match(In(account: null, tax: new[] { VietTin }));
        Assert.Equal((MatchStatuses.Matched, MatchMethods.TaxCode, VietTin.Id), (r.Status, r.Method, r.OwnerId));
    }

    [Fact]
    public void InvoiceReference_Matched_High()
    {
        var r = CounterpartyMatcher.Match(In(account: null, invoices: new[] { new InvoiceRefHit("DEMO-INV-VT-001", VietTin.Id, VietTin.Name) }));
        Assert.Equal((MatchStatuses.Matched, MatchMethods.InvoiceReference, 0.95m), (r.Status, r.Method, r.Confidence));
    }

    [Fact]
    public void NormalizedLegalName_Exact_Matched()
    {
        var r = CounterpartyMatcher.Match(In(account: null, name: "CONG TY TNHH VIET TIN", exact: new[] { VietTin }));
        Assert.Equal((MatchStatuses.Matched, MatchMethods.NameExact), (r.Status, r.Method));
    }

    [Fact]
    public void BankAccountAndInvoiceAgree_BankAccountWins_Corroborated()
    {
        var r = CounterpartyMatcher.Match(In(accounts: new[] { Acct(VietTin) },
            invoices: new[] { new InvoiceRefHit("DEMO-INV-VT-001", VietTin.Id, VietTin.Name) }));
        Assert.Equal((MatchStatuses.Matched, MatchMethods.BankAccountExact), (r.Status, r.Method));
        Assert.Equal(2, r.Signals.Count);
    }

    [Fact]
    public void ConflictingStrongSignals_NeedsReview_WithBothCandidates()
    {
        // Account says Việt Tín, invoice says Minh Long.
        var r = CounterpartyMatcher.Match(In(accounts: new[] { Acct(VietTin) },
            invoices: new[] { new InvoiceRefHit("DEMO-INV-ML-001", MinhLong.Id, MinhLong.Name) }));
        Assert.Equal((MatchStatuses.NeedsReview, MatchMethods.Conflict), (r.Status, r.Method));
        Assert.Null(r.OwnerId);
        Assert.Equal(new[] { VietTin.Id, MinhLong.Id }.OrderBy(x => x), r.Candidates.Select(c => c.OwnerId!.Value).OrderBy(x => x));
    }

    [Fact]
    public void AmountAlone_NeverMatches_SuggestionOnly()
    {
        var r = CounterpartyMatcher.Match(In(account: "123888999", name: "NGUYEN VAN A", amount: new[] { VietTin }));
        Assert.Equal(MatchStatuses.NeedsReview, r.Status);
        Assert.Null(r.OwnerId);
        Assert.Equal(MatchMethods.AmountHint, Assert.Single(r.Candidates).Method);
    }

    [Fact]
    public void SimilarName_SuggestionOnly()
    {
        var r = CounterpartyMatcher.Match(In(account: null, name: "VIET TIN TRADING", similar: new[] { VietTin }));
        Assert.Equal(MatchStatuses.NeedsReview, r.Status);
        Assert.Null(r.OwnerId);
    }

    [Fact]
    public void NoEvidence_Unmatched()
    {
        var r = CounterpartyMatcher.Match(In(account: "123888999", name: "NGUYEN VAN A"));
        Assert.Equal(MatchStatuses.Unmatched, r.Status);
        Assert.Null(r.OwnerId);
    }

    [Fact]
    public void IncomingFromSupplierAccount_IsWarnedNotMatched()
    {
        var kimLong = new MatchParty(Guid.NewGuid(), "DEMO-SUP-KIMLONG", "Kim Long", "0200000002");
        var r = CounterpartyMatcher.Match(In(account: "987654321", bank: "ACB", accounts: new[] { Acct(kimLong, "supplier", "ACB") }));
        Assert.Equal(MatchStatuses.NeedsReview, r.Status);
        Assert.Null(r.OwnerId);
        Assert.Contains(r.Warnings, w => w.Contains("nhà cung cấp"));
    }

    [Fact]
    public void MoneyOut_SupplierBankAccount_Candidate()
    {
        var kimLong = new MatchParty(Guid.NewGuid(), "DEMO-SUP-KIMLONG", "Công ty CP Vật tư Kim Long", "0200000002");
        var r = CounterpartyMatcher.Match(In(account: "987654321", bank: "ACB", name: "KIM LONG",
            accounts: new[] { Acct(kimLong, "supplier", "ACB") }, ownerType: "supplier"));
        Assert.Equal((MatchStatuses.Matched, "supplier", kimLong.Id), (r.Status, r.OwnerType, r.OwnerId));
    }
}
