using Aiq.Gateway.Payments;
using Aiq.Gateway.Vouchers;

namespace Aiq.Gateway.Tests.Vouchers;

/// <summary>
/// Auto-detect + manual override + confidence/evidence, against the OCR text
/// of the real demo PDFs (Fixtures/*.txt = pdftotext output of
/// 01_Hoa_don_ban_AR.pdf … 07_De_nghi_hoan_ung.pdf).
/// </summary>
public sealed class VoucherTypeClassifierTests
{
    private static readonly CompanyIdentity SauTruc = new("0123456789", new[] { "Công ty TNHH Sáu Trực", "Sáu Trực" });

    private static string Ocr(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vouchers", "Fixtures", name + ".txt"));

    private static VoucherTypeClassification Classify(string fixture, string? llm, string request = "auto",
        string? sellerTax = null, string? buyerTax = null, CompanyIdentity? company = null)
    {
        var c = company ?? SauTruc;
        var det = VoucherTypeClassifier.Detect(Ocr(fixture), c);
        return VoucherTypeClassifier.Combine(VoucherTypeRequest.Parse(request), det, llm, sellerTax, buyerTax, c.TaxCode);
    }

    // ---------------- auto-detect each supported type ----------------

    [Theory]
    [InlineData("01_Hoa_don_ban_AR", "invoice_vat_out")]
    [InlineData("02_Hoa_don_mua_AP", "invoice_vat_in")]
    [InlineData("03_Phieu_thu", "receipt")]
    [InlineData("04_Phieu_chi", "payment")]
    [InlineData("05_Uy_nhiem_chi", "bank_transfer")]
    [InlineData("06_Sao_ke_ngan_hang", "bank_statement")]
    [InlineData("07_De_nghi_hoan_ung", "expense_claim")]
    public void DeterministicDetection_FromOcrAlone(string fixture, string expected)
    {
        var det = VoucherTypeClassifier.Detect(Ocr(fixture), SauTruc);
        Assert.Equal(expected, det.Type);
        Assert.True(det.TitleInHeader);
        Assert.False(det.TitleConflict);
        Assert.Contains(det.Evidence, e => e.StartsWith("Tiêu đề OCR:"));
    }

    [Theory]
    [InlineData("01_Hoa_don_ban_AR", "invoice_vat_out")]
    [InlineData("02_Hoa_don_mua_AP", "invoice_vat_in")]
    [InlineData("03_Phieu_thu", "receipt")]
    [InlineData("04_Phieu_chi", "payment")]
    [InlineData("05_Uy_nhiem_chi", "bank_transfer")]
    [InlineData("06_Sao_ke_ngan_hang", "bank_statement")]
    [InlineData("07_De_nghi_hoan_ung", "expense_claim")]
    public void Auto_TitleAndExtractorAgree_HighConfidence_NoConfirmation(string fixture, string expected)
    {
        var c = Classify(fixture, expected);
        Assert.Equal(expected, c.VoucherType);
        Assert.Equal(VoucherTypeSources.AutoDetected, c.Source);
        Assert.Equal(VoucherTypeStatuses.Ok, c.Status);
        Assert.True(c.Confidence >= 0.90m, $"{fixture}: {c.Confidence}");
        Assert.False(c.BlocksPosting);
        Assert.Null(c.UploadedAs);
    }

    [Fact]
    public void PhieuChi_EvidenceListsTitleAndPayerPayeeLines()
    {
        var c = Classify("04_Phieu_chi", "payment");
        Assert.Contains("Tiêu đề OCR: \"PHIẾU CHI\"", c.Evidence);
        Assert.Contains("Dòng \"Đơn vị trả tiền\"", c.Evidence);
        Assert.Contains("Dòng \"Đơn vị nhận tiền\"", c.Evidence);
        Assert.Equal(0.98m, c.Confidence);
    }

    // ---------------- invoice direction from Company Profile ----------------

    [Fact]
    public void Invoice_DirectionDecidedByCompanyProfile_NotByLlm()
    {
        // LLM says purchase, but OCR seller MST == our MST → sales.
        var c = Classify("01_Hoa_don_ban_AR", "invoice_vat_in");
        Assert.Equal("invoice_vat_out", c.VoucherType);
        Assert.Equal(VoucherTypeStatuses.Ok, c.Status);
        Assert.Contains(c.Evidence, e => e.Contains("Người bán MST 0123456789"));
    }

    [Fact]
    public void Invoice_PaddleOcrLayout_MstOnNextLine_DirectionStillFromOcr()
    {
        // Real OCR output of 01_Hoa_don_ban_AR.pdf (label and value on separate lines, name misspelt).
        const string ocr = "CÔNG TY TNHH SẦU TRÚC\nMST: 0123456789 - Địa chỉ: Lạc An, Ninh Thọ\nHÓA ĐƠN BÁN HÀNG - PHẢI THU (AR)\nSố: TEST-SINV-1001\n\nNGƯỜI BÁN\nTên đơn vị\nCÔNG TY TNHH SẦU TRÚC\nMST\n0123456789\nĐịa chỉ\nLạc An, Ninh Thọ\n\nNGƯỜI MUA\nTên đơn vị\nCÔNG TY TNHH VIỆT TÍN\nMST\n0307890123\nTiền trước VAT\n30.000.000 VND";
        var det = VoucherTypeClassifier.Detect(ocr, SauTruc);
        Assert.Equal(("invoice_vat_out", "0123456789", "0307890123"), (det.Type, det.SellerTaxCode, det.BuyerTaxCode));
        var c = VoucherTypeClassifier.Combine(VoucherTypeRequest.Parse("auto"), det, "invoice_vat_out", null, null, "0123456789");
        Assert.True(c.Confidence >= 0.95m, c.Confidence.ToString());
    }

    [Fact]
    public void SupplierNameVatTu_IsNotCountedAsVatEvidence()
    {
        var det = VoucherTypeClassifier.Detect(Ocr("02_Hoa_don_mua_AP"), SauTruc);
        Assert.DoesNotContain(det.Evidence, e => e.Contains("VẬT TƯ"));
    }

    [Fact]
    public void Invoice_OtherCompanyProfile_CannotResolveDirection_NeedsConfirmation()
    {
        var other = new CompanyIdentity("0999999999", new[] { "Công ty khác" });
        var c = Classify("01_Hoa_don_ban_AR", "invoice_vat_out", company: other);
        Assert.Equal(VoucherTypeStatuses.NeedsConfirmation, c.Status);
        Assert.True(c.BlocksPosting);
        Assert.True(c.Confidence < 0.70m);
    }

    [Fact]
    public void Invoice_DirectionFromExtractedMsts_WhenOcrSectionsMissing()
    {
        var det = VoucherTypeClassifier.Detect("HÓA ĐƠN GIÁ TRỊ GIA TĂNG\nKý hiệu 1C26TAA\nThuế GTGT 10%", SauTruc);
        Assert.True(det.InvoiceCandidate);
        Assert.Null(det.Type);
        var c = VoucherTypeClassifier.Combine(VoucherTypeRequest.Parse("auto"), det, "invoice_vat", "0200000002", "0123456789", "0123456789");
        Assert.Equal("invoice_vat_in", c.VoucherType);
    }

    // ---------------- title vs extractor ----------------

    [Fact]
    public void TitlePhieuChi_ExtractorSaysReceipt_IsConflict_BlocksPosting()
    {
        var c = Classify("04_Phieu_chi", "receipt");
        Assert.Equal(VoucherTypeStatuses.Conflict, c.Status);
        Assert.Equal("payment", c.VoucherType); // the title, never the LLM alone
        Assert.True(c.Confidence <= 0.50m);
        Assert.True(c.BlocksPosting);
        Assert.Contains(c.Evidence, e => e.Contains("mâu thuẫn"));
    }

    [Fact]
    public void NoTitle_ExtractorOnly_MediumConfidence_WarningNotBlocking()
    {
        var det = VoucherTypeClassifier.Detect("Chứng từ\nSố tiền 1.000.000", SauTruc);
        var c = VoucherTypeClassifier.Combine(VoucherTypeRequest.Parse("auto"), det, "receipt", null, null, "0123456789");
        Assert.Equal("receipt", c.VoucherType);
        Assert.Equal(0.72m, c.Confidence);
        Assert.Equal(VoucherTypeStatuses.ConfirmRecommended, c.Status);
        Assert.False(c.BlocksPosting);
    }

    [Fact]
    public void NothingDetected_Unclassified_RequiresManualType()
    {
        var det = VoucherTypeClassifier.Detect("Ghi chú nội bộ", SauTruc);
        var c = VoucherTypeClassifier.Combine(VoucherTypeRequest.Parse("auto"), det, null, null, null, "0123456789");
        Assert.Equal(VoucherTypes.Unclassified, c.VoucherType);
        Assert.True(c.BlocksPosting);
    }

    [Fact]
    public void ReferenceToInvoiceInsideReceipt_IsNotMistakenForInvoiceTitle()
    {
        // 03_Phieu_thu contains "Hóa đơn tham chiếu TEST-SINV-1001".
        var det = VoucherTypeClassifier.Detect(Ocr("03_Phieu_thu"), SauTruc);
        Assert.Equal("receipt", det.Type);
        Assert.False(det.InvoiceCandidate);
    }

    // ---------------- manual hint ----------------

    [Fact]
    public void ManualHintMatchesDetection_UserSelected_Ok()
    {
        var c = Classify("03_Phieu_thu", "receipt", request: "receipt");
        Assert.Equal(("receipt", VoucherTypeSources.UserSelected, VoucherTypeStatuses.Ok), (c.VoucherType, c.Source, c.Status));
        Assert.Equal("receipt", c.UploadedAs);
    }

    [Fact]
    public void ManualHintConflictsWithStrongDetection_TypeCorrected_MismatchSurfaced()
    {
        // The real incident: PHIẾU CHI uploaded as "Hóa đơn bán".
        var c = Classify("04_Phieu_chi", "payment", request: "invoice_vat_out");
        Assert.Equal("payment", c.VoucherType);                      // settlement sees the right type
        Assert.Equal(VoucherTypeSources.ExtractorCorrected, c.Source);
        Assert.Equal(VoucherTypeStatuses.HintMismatch, c.Status);
        Assert.Equal("invoice_vat_out", c.UploadedAs);
        Assert.True(c.BlocksPosting);                                // but nobody posts until confirmed
    }

    [Fact]
    public void ManualHintConflictsWithWeakDetection_KeepsHint_MismatchSurfaced()
    {
        var det = VoucherTypeClassifier.Detect("Chứng từ không tiêu đề", SauTruc);
        var c = VoucherTypeClassifier.Combine(VoucherTypeRequest.Parse("receipt"), det, "payment", null, null, "0123456789");
        Assert.Equal("receipt", c.VoucherType);
        Assert.Equal(VoucherTypeStatuses.HintMismatch, c.Status);
    }

    // ---------------- admin override ----------------

    [Fact]
    public void AdminForcedType_Wins_AuditedAsAdminCorrected()
    {
        var c = Classify("04_Phieu_chi", "payment", request: VoucherTypeRequest.ForAdmin("receipt"));
        Assert.Equal(("receipt", VoucherTypeSources.AdminCorrected, VoucherTypeStatuses.Ok), (c.VoucherType, c.Source, c.Status));
        Assert.Equal("payment", c.DetectedVoucherType); // detection still recorded for audit
        Assert.False(c.BlocksPosting);
    }

    [Theory]
    [InlineData("auto", null, null)]
    [InlineData("", null, null)]
    [InlineData("payment", "payment", null)]
    [InlineData("admin:bank_transfer", null, "bank_transfer")]
    [InlineData("not_a_type", null, null)]
    public void RequestParsing(string raw, string? hint, string? forced)
    {
        var r = VoucherTypeRequest.Parse(raw);
        Assert.Equal((hint, forced), (r.Hint, r.Forced));
    }

    [Fact]
    public void Classification_RoundTripsThroughExtractedFields()
    {
        var c = Classify("04_Phieu_chi", "payment", request: "invoice_vat_out");
        var json = $"{{\"voucher_type_classification\": {System.Text.Json.JsonSerializer.Serialize(c, VoucherTypeClassification.Json)}}}";
        var back = VoucherTypeClassification.FromExtractedFields(json)!;
        Assert.Equal((c.VoucherType, c.Status, c.Source, c.Confidence, c.UploadedAs), (back.VoucherType, back.Status, back.Source, back.Confidence, back.UploadedAs));
        Assert.True(back.BlocksPosting);
        Assert.False((back with { ConfirmedBy = Guid.NewGuid() }).BlocksPosting);
        Assert.Null(VoucherTypeClassification.FromExtractedFields("{}"));
    }
}
