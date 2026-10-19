using Aiq.Gateway.Payments;

namespace Aiq.Gateway.Tests.Payments;

public sealed class CashSettlementRulesTests
{
    private static readonly CompanyIdentity SauTruc = new("0123456789", new[] { "Công ty TNHH Sáu Trực", "Sáu Trực" });
    private static readonly DateOnly Today = new(2026, 9, 30);

    // ---------------- direction ----------------

    [Fact]
    public void Receipt_PayeeIsMyCompanyByTaxCode_IsMoneyIn_CounterpartyIsPayer()
    {
        var r = CashSettlementRules.ResolveDirection("receipt",
            payer: new CashParty("Công ty TNHH Việt Tín", "0307890123"),
            payee: new CashParty("Cong Ty TNHH Sáu Trực", "0123456789"), SauTruc);
        Assert.Equal(CashDirections.MoneyIn, r.Direction);
        Assert.Equal("company_profile_tax_code", r.Source);
        Assert.Equal("0307890123", r.Counterparty!.TaxCode);
    }

    [Fact]
    public void Payment_PayerIsMyCompanyByName_IsMoneyOut_CounterpartyIsPayee()
    {
        var r = CashSettlementRules.ResolveDirection("payment",
            payer: new CashParty("CÔNG TY TNHH SÁU TRỰC", null),
            payee: new CashParty("Công ty TNHH Cung ứng Đại Phát", "0200000001"), SauTruc);
        Assert.Equal(CashDirections.MoneyOut, r.Direction);
        Assert.Equal("company_profile_name", r.Source);
        Assert.Equal("0200000001", r.Counterparty!.TaxCode);
    }

    [Fact]
    public void Receipt_WhereMyCompanyIsThePayer_ConflictsAndNeedsReview()
    {
        // Titled "Phiếu thu" but MY_COMPANY is paying — never reduce AR on the title alone.
        var r = CashSettlementRules.ResolveDirection("receipt",
            payer: new CashParty("Sáu Trực", "0123456789"),
            payee: new CashParty("Đại Phát", "0200000001"), SauTruc);
        Assert.Equal(CashDirections.NeedsReview, r.Direction);
        Assert.Equal("direction_conflicts_voucher_type", r.Reason);
    }

    [Fact]
    public void BothPartiesMatchCompany_NeedsReview()
    {
        var r = CashSettlementRules.ResolveDirection("receipt",
            new CashParty("Sáu Trực", "0123456789"), new CashParty("Sáu Trực", "0123456789"), SauTruc);
        Assert.Equal(CashDirections.NeedsReview, r.Direction);
    }

    [Fact]
    public void NeitherPartyIsMyCompany_FallsBackToVoucherType()
    {
        var r = CashSettlementRules.ResolveDirection("receipt",
            new CashParty("Việt Tín", "0307890123"), new CashParty(null, null), SauTruc);
        Assert.Equal(CashDirections.MoneyIn, r.Direction);
        Assert.Equal("voucher_type", r.Source);
        Assert.Equal("0307890123", r.Counterparty!.TaxCode);
    }

    [Fact]
    public void DifferentTaxCode_NeverFallsThroughToNameMatch()
    {
        var r = CashSettlementRules.ResolveDirection("payment",
            payer: new CashParty("Công ty TNHH Sáu Trực", "0999999999"),
            payee: new CashParty("Đại Phát", "0200000001"), SauTruc);
        Assert.Equal("voucher_type", r.Source);
    }

    [Fact]
    public void UnsupportedVoucherType_NeedsReview()
    {
        var r = CashSettlementRules.ResolveDirection("invoice_vat_out",
            new CashParty("A", null), new CashParty("B", null), SauTruc);
        Assert.Equal(CashDirections.NeedsReview, r.Direction);
    }

    [Fact]
    public void CompanyNameMatch_IgnoresDiacriticsCaseAndLegalForm()
    {
        Assert.Equal("sau truc", CashSettlementRules.NormalizeCompanyName("CÔNG TY TNHH SÁU TRỰC"));
        Assert.Equal("dai phat", CashSettlementRules.NormalizeCompanyName("Công ty CP Đại Phát"));
        Assert.Equal("viet tin", CashSettlementRules.NormalizeCompanyName("Cong ty TNHH Vie\u0302\u0323t Ti\u0301n"));
    }

    // ---------------- Company Profile found in OCR text ----------------

    private const string ReceiptOcr = "CÔNG TY TNHH SÂU TRÚC\nMST: 0123456789 - Địa chỉ: Lạc An\nPHIẾU THU CÔNG NỢ\nĐơn vị nộp tiền CÔNG TY TNHH VIỆT TÍN\nMã số thuế 0307890123";

    [Fact]
    public void Receipt_CompanyOnlyInOcrHeader_IsIdentifiedByTaxCode_NotVoucherTypeFallback()
    {
        var r = CashSettlementRules.ResolveDirection("receipt",
            payer: new CashParty("CÔNG TY TNHH VIỆT TÍN", "0307890123"), payee: new CashParty(null, null), SauTruc, ReceiptOcr);
        Assert.Equal(CashDirections.MoneyIn, r.Direction);
        Assert.Equal("company_profile_tax_code_document", r.Source);
        Assert.Equal("0307890123", r.Counterparty!.TaxCode);
    }

    [Fact]
    public void MisspeltCompanyNameMatch_IsReportedByTaxCode_WhenDocumentPrintsOurMst()
    {
        var r = CashSettlementRules.ResolveDirection("receipt",
            payer: new CashParty("CÔNG TY TNHH VIỆT TÍN", "0307890123"), payee: new CashParty("CÔNG TY TNHH SÂU TRÚC", null), SauTruc, ReceiptOcr);
        Assert.Equal(CashDirections.MoneyIn, r.Direction);
        Assert.Equal("company_profile_tax_code_document", r.Source);
    }

    [Fact]
    public void CompanyTrulyAbsentFromDocument_FallsBackToVoucherType()
    {
        var r = CashSettlementRules.ResolveDirection("receipt",
            new CashParty("Việt Tín", "0307890123"), new CashParty(null, null), SauTruc, "PHIẾU THU\nĐơn vị nộp: Việt Tín MST 0307890123");
        Assert.Equal("voucher_type", r.Source);
    }

    [Fact]
    public void TaxCodeInsideLongerDigitRun_DoesNotCount()
    {
        Assert.False(CashSettlementRules.CompanyTaxCodeInText(SauTruc, "STK 90123456789012"));
        Assert.True(CashSettlementRules.CompanyTaxCodeInText(SauTruc, "MST:0123456789."));
    }

    [Fact]
    public void FindReferencedDocumentNumbers_FindsPaidInvoiceToo()
    {
        var refs = CashSettlementRules.FindReferencedDocumentNumbers(
            new[] { "DEMO-INV-VT-001", "DEMO-ST-VT-001", "0000501" }, new[] { "Thu một phần công nợ theo hóa đơn DEMO-ST-VT-001" });
        Assert.Equal(new[] { "DEMO-ST-VT-001" }, refs);
    }

    // ---------------- explicit invoice references in bank descriptions ----------------

    [Theory]
    [InlineData("TT DEMO-INV-VT-001", "DEMO-INV-VT-001")]                  // known invoice
    [InlineData("VIET TIN THANH TOAN HD DEMO-INV-VT-001", "DEMO-INV-VT-001")]
    [InlineData("TT INV-DOES-NOT-EXIST", "INV-DOES-NOT-EXIST")]           // INV-style code, not booked
    [InlineData("THANH TOAN HOA DON 0009999", "0009999")]                 // after keyword, unknown
    [InlineData("TT HĐ AB-123", "AB-123")]
    public void ExplicitInvoiceReferences_Detected(string description, string expected) =>
        Assert.Contains(expected, CashSettlementRules.ExplicitInvoiceReferences(description, new[] { "DEMO-INV-VT-001", "0000501" }));

    [Theory]
    [InlineData("THANH TOAN TIEN HANG")]
    [InlineData("VIET TIN CK 20000000")]
    [InlineData("")]
    public void ExplicitInvoiceReferences_NoneInPlainDescriptions(string description) =>
        Assert.Empty(CashSettlementRules.ExplicitInvoiceReferences(description, new[] { "DEMO-INV-VT-001" }));

    [Fact]
    public void ExplicitInvoiceReferences_DuplicateNormalizedKnownNumbers_DoNotThrow() =>
        Assert.Single(CashSettlementRules.ExplicitInvoiceReferences("TT HD-001", new[] { "HD-001", "HD 001", "hd001" }));

    // ---------------- payment method ----------------

    [Theory]
    [InlineData("TM", "cash")]
    [InlineData("Tiền mặt", "cash")]
    [InlineData("CK", "bank")]
    [InlineData("Chuyển khoản", "bank")]
    [InlineData("chuyen khoan ngan hang", "bank")]
    [InlineData(null, null)]
    [InlineData("MIXED", null)]
    [InlineData("CHUYỂN KHOẢN", "bank")]
    [InlineData("Chuye\u0302\u0309n khoa\u0309n", "bank")] // NFD input (OCR)
    [InlineData("TIỀN MẶT", "cash")]
    public void NormalizePaymentMethod(string? raw, string? expected) =>
        Assert.Equal(expected, CashSettlementRules.NormalizePaymentMethod(raw));

    // ---------------- settlement status ----------------

    [Theory]
    [InlineData(39_600_000, 0, "UNPAID")]
    [InlineData(39_600_000, 20_000_000, "PARTIAL")]
    [InlineData(39_600_000, 39_600_000, "PAID")]
    public void SettlementStatus_NotYetDue(decimal original, decimal paid, string expected) =>
        Assert.Equal(expected, CashSettlementRules.SettlementStatus(original, paid, Today.AddDays(10), Today));

    [Fact]
    public void SettlementStatus_PastDueWithOutstanding_IsOverdue_ButPaidStaysPaid()
    {
        Assert.Equal("OVERDUE", CashSettlementRules.SettlementStatus(100m, 40m, Today.AddDays(-1), Today));
        Assert.Equal("PAID", CashSettlementRules.SettlementStatus(100m, 100m, Today.AddDays(-1), Today));
    }

    [Theory]
    [InlineData(100, 0, "open")]
    [InlineData(100, 1, "partial")]
    [InlineData(100, 100, "paid")]
    public void OpenItemStatus(decimal original, decimal paid, string expected) =>
        Assert.Equal(expected, CashSettlementRules.OpenItemStatus(original, paid));

    // ---------------- matching ----------------

    private static OpenItem Inv(string doc, decimal original, decimal paid = 0m, int dueInDays = 30) =>
        new(Guid.NewGuid(), doc, Today.AddDays(-5), Today.AddDays(dueInDays), original, paid);

    [Fact]
    public void Priority1_InvoiceNumberInDocument_IsCertain_EvenWithOtherOpenInvoices()
    {
        var target = Inv("DEMO-ST-VT-001", 39_600_000m);
        var items = new[] { Inv("DEMO-INV-VT-001", 85_000_000m, dueInDays: 1), target, Inv("0000501", 39_600_000m) };
        var s = CashSettlementRules.SuggestAllocations(20_000_000m, items, new[] { "Thanh toán HĐ DEMO-ST-VT-001 đợt 1" });
        Assert.Equal(MatchCertainty.Certain, s.Certainty);
        var a = Assert.Single(s.Allocations);
        Assert.Equal(target.Id, a.InvoiceId);
        Assert.Equal(20_000_000m, a.ApplyAmount);
        Assert.Equal("invoice_reference", a.MatchMethod);
        Assert.Equal(0m, s.UnappliedAmount);
    }

    [Fact]
    public void Priority1_NumericInvoiceNumber_IgnoresLeadingZeros()
    {
        var target = Inv("0000501", 39_600_000m);
        var s = CashSettlementRules.SuggestAllocations(10m, new[] { Inv("X-1", 50m), target }, new[] { "TT HD 501" });
        Assert.Equal(target.Id, Assert.Single(s.Allocations).InvoiceId);
    }

    [Fact]
    public void Priority1_ReferenceMatch_OverpaymentStaysUnapplied_NotSpilledToOtherInvoices()
    {
        var target = Inv("HD-1", 10_000_000m);
        var s = CashSettlementRules.SuggestAllocations(15_000_000m, new[] { target, Inv("HD-2", 50_000_000m) }, new[] { "HD-1" });
        Assert.Equal(10_000_000m, Assert.Single(s.Allocations).ApplyAmount);
        Assert.Equal(5_000_000m, s.UnappliedAmount);
    }

    [Fact]
    public void Priority1_OnePaymentReferencingTwoInvoices_AllocatesAcrossBoth()
    {
        var a = Inv("HD-A", 30m, dueInDays: 5);
        var b = Inv("HD-B", 50m, dueInDays: 10);
        var s = CashSettlementRules.SuggestAllocations(60m, new[] { a, b, Inv("HD-C", 999m) }, new[] { "TT HD-A, HD-B" });
        Assert.Equal(MatchCertainty.Certain, s.Certainty);
        Assert.Equal(new[] { 30m, 30m }, s.Allocations.Select(x => x.ApplyAmount).ToArray());
    }

    [Fact]
    public void Priority2_ExactOutstandingAmount_Single_IsCertain()
    {
        var target = Inv("HD-2", 55_000_000m, paid: 35_400_000m); // outstanding 19,600,000
        var s = CashSettlementRules.SuggestAllocations(19_600_000m, new[] { Inv("HD-1", 85_000_000m), target }, Array.Empty<string>());
        Assert.Equal(MatchCertainty.Certain, s.Certainty);
        Assert.Equal("exact_amount", s.Method);
        Assert.Equal(target.Id, Assert.Single(s.Allocations).InvoiceId);
    }

    [Fact]
    public void Priority2_ExactAmountOnSeveralInvoices_IsAmbiguous()
    {
        var s = CashSettlementRules.SuggestAllocations(39_600_000m,
            new[] { Inv("0000501", 39_600_000m), Inv("DEMO-ST-VT-001", 39_600_000m) }, Array.Empty<string>());
        Assert.Equal(MatchCertainty.Ambiguous, s.Certainty);
    }

    [Fact]
    public void Priority3_SingleOpenInvoiceCoveringAmount_IsCertain()
    {
        var target = Inv("PINV-1", 55_000_000m);
        var s = CashSettlementRules.SuggestAllocations(20_000_000m, new[] { target }, Array.Empty<string>());
        Assert.Equal(MatchCertainty.Certain, s.Certainty);
        Assert.Equal("outstanding_compatible", s.Method);
        Assert.Equal(20_000_000m, Assert.Single(s.Allocations).ApplyAmount);
    }

    [Fact]
    public void Priority4_SeveralOpenInvoicesNoReference_RequiresAdminSelection()
    {
        var s = CashSettlementRules.SuggestAllocations(20_000_000m,
            new[] { Inv("HD-1", 85_000_000m), Inv("HD-2", 42_000_000m) }, new[] { "thanh toan tien hang" });
        Assert.Equal(MatchCertainty.Ambiguous, s.Certainty);
    }

    [Fact]
    public void SingleOpenInvoiceSmallerThanPayment_IsAmbiguous_NotAutoSettled()
    {
        var s = CashSettlementRules.SuggestAllocations(20m, new[] { Inv("HD-1", 10m) }, Array.Empty<string>());
        Assert.Equal(MatchCertainty.Ambiguous, s.Certainty);
    }

    [Fact]
    public void ExplicitReferenceToNonOpenInvoice_NeverAutoAppliesElsewhere()
    {
        // DEMO-ST-VT-001 already PAID; only DEMO-INV-VT-001 is open and covers the amount.
        var s = CashSettlementRules.SuggestAllocations(20_000_000m, new[] { Inv("DEMO-INV-VT-001", 85_000_000m) },
            new[] { "DEMO-ST-VT-001" }, new[] { "DEMO-ST-VT-001" });
        Assert.Equal(MatchCertainty.Ambiguous, s.Certainty);
        Assert.Equal("document_reference_not_open", s.Reason);
    }

    [Fact]
    public void NoOpenInvoice_EverythingUnapplied()
    {
        var s = CashSettlementRules.SuggestAllocations(20m, Array.Empty<OpenItem>(), new[] { "HD-1" });
        Assert.Equal(MatchCertainty.NoOpenItem, s.Certainty);
        Assert.Empty(s.Allocations);
        Assert.Equal(20m, s.UnappliedAmount);
    }
}
