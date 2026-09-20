using Aiq.Gateway.Accounting;

namespace Aiq.Gateway.Tests.Accounting;

/// <summary>
/// InvoiceDirectionResolver is pure/deterministic (no DB), so it is tested
/// directly here — same posture as JournalRoutingRulesTests. Covers the
/// three DIRECTION scenarios from the Contract -> AR/AP lifecycle task, plus
/// a direct repro of the reported bug: an invoice whose seller is the
/// current company was being misclassified as PURCHASE (proposing 331)
/// because voucher_type was trusted as-is from LLM extraction instead of
/// being verified against the tenant's own registered tax ID.
/// </summary>
public sealed class InvoiceDirectionResolverTests
{
    private const string CompanyTaxId = "0312345678"; // "CÔNG TY CÁT TƯỜNG"
    private const string CounterpartyTaxId = "0307890123"; // "CÔNG TY CP ĐẦU TƯ VIỆT TÍN"

    [Fact]
    public void Direction1_SellerTaxIdMatchesCompany_ResolvesSales()
    {
        var result = InvoiceDirectionResolver.Resolve(sellerTaxId: CompanyTaxId, buyerTaxId: CounterpartyTaxId, companyTaxId: CompanyTaxId);

        Assert.Equal(InvoiceDirection.Sales, result.Direction);
        Assert.Equal(CounterpartyTaxId, result.CounterpartyTaxId);
    }

    [Fact]
    public void Direction2_BuyerTaxIdMatchesCompany_ResolvesPurchase()
    {
        var result = InvoiceDirectionResolver.Resolve(sellerTaxId: CounterpartyTaxId, buyerTaxId: CompanyTaxId, companyTaxId: CompanyTaxId);

        Assert.Equal(InvoiceDirection.Purchase, result.Direction);
        Assert.Equal(CounterpartyTaxId, result.CounterpartyTaxId);
    }

    [Fact]
    public void Direction3_NeitherMatchesCompany_ResolvesNeedsReview()
    {
        var result = InvoiceDirectionResolver.Resolve(sellerTaxId: "0100000001", buyerTaxId: "0100000002", companyTaxId: CompanyTaxId);

        Assert.Equal(InvoiceDirection.NeedsReview, result.Direction);
        Assert.Null(result.CounterpartyTaxId);
    }

    [Fact]
    public void ReportedBug_CatTuongSellsToVietTin_ResolvesSalesNotPurchase()
    {
        // Exact repro: Seller CÔNG TY CÁT TƯỜNG (0312345678), Buyer CÔNG TY CP
        // ĐẦU TƯ VIỆT TÍN (0307890123), current company = Cát Tường. Before
        // the fix, the system trusted voucher_type='invoice_vat_in' (set by
        // the LLM from document text) and proposed 331 — this proves the
        // deterministic resolver gives the correct answer independent of
        // whatever voucher_type the LLM chose.
        var result = InvoiceDirectionResolver.Resolve(
            sellerTaxId: "0312345678", buyerTaxId: "0307890123", companyTaxId: "0312345678");

        Assert.Equal(InvoiceDirection.Sales, result.Direction);
        Assert.Equal("0307890123", result.CounterpartyTaxId);
    }

    [Fact]
    public void CompanyTaxIdNotConfigured_NeedsReview_NeverGuesses()
    {
        var result = InvoiceDirectionResolver.Resolve(sellerTaxId: CompanyTaxId, buyerTaxId: CounterpartyTaxId, companyTaxId: null);

        Assert.Equal(InvoiceDirection.NeedsReview, result.Direction);
        Assert.Equal("company_tax_id_not_configured", result.Reason);
    }

    [Fact]
    public void MissingSellerOrBuyerTaxId_NeedsReview()
    {
        Assert.Equal(InvoiceDirection.NeedsReview, InvoiceDirectionResolver.Resolve(null, CounterpartyTaxId, CompanyTaxId).Direction);
        Assert.Equal(InvoiceDirection.NeedsReview, InvoiceDirectionResolver.Resolve(CompanyTaxId, null, CompanyTaxId).Direction);
        Assert.Equal(InvoiceDirection.NeedsReview, InvoiceDirectionResolver.Resolve("", "", CompanyTaxId).Direction);
    }

    [Fact]
    public void BothPartiesMatchCompany_NeedsReview_NeverPicksOneArbitrarily()
    {
        var result = InvoiceDirectionResolver.Resolve(sellerTaxId: CompanyTaxId, buyerTaxId: CompanyTaxId, companyTaxId: CompanyTaxId);

        Assert.Equal(InvoiceDirection.NeedsReview, result.Direction);
        Assert.Equal("both_parties_match_company", result.Reason);
    }

    [Theory]
    [InlineData("0312345678", "0312345678-001", InvoiceDirection.NeedsReview)] // branch of same legal entity as buyer too -> both match
    public void BranchSuffix_NormalizedBeforeComparison(string companyTaxId, string sellerTaxId, InvoiceDirection expectedWhenBuyerIsCompany)
    {
        // A branch-office MST (root-001) is the same legal entity as its
        // parent for direction purposes — comparison strips the suffix.
        var result = InvoiceDirectionResolver.Resolve(sellerTaxId: sellerTaxId, buyerTaxId: companyTaxId, companyTaxId: companyTaxId);
        Assert.Equal(expectedWhenBuyerIsCompany, result.Direction);
    }

    [Fact]
    public void BranchSuffix_SellerBranchOfCompanyStillResolvesAsCompany()
    {
        // Seller is a BRANCH of the company (root matches), buyer is a real
        // third party -> still SALES, not NeedsReview.
        var result = InvoiceDirectionResolver.Resolve(
            sellerTaxId: $"{CompanyTaxId}-001", buyerTaxId: CounterpartyTaxId, companyTaxId: CompanyTaxId);

        Assert.Equal(InvoiceDirection.Sales, result.Direction);
    }
}
