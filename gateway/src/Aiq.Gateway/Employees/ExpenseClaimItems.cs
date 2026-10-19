using System.Text;
using Aiq.Gateway.Vouchers.DocumentProcessing;

namespace Aiq.Gateway.Employees;

// ============================================================================
// Multi-file Expense Claim intake — ONE claim, ONE FILE = ONE ITEM.
//
// Pure rules shared by the item job, the item endpoints and the tests. The
// claim itself stays the existing expense_claim voucher; items only FEED the
// existing deterministic engine (ExpenseClaimRepository.ProposeAsync) and the
// existing authoritative recognition / payment, which are unchanged.
// ============================================================================

public static class ExpenseClaimItemStatuses
{
    public const string Processing = "processing";
    public const string Ready = "ready";
    public const string NeedsReview = "needs_review";
    public const string ExtractionFailed = "extraction_failed";
    public const string Excluded = "excluded";

    /// <summary>Statuses that still need work before the claim can be approved.</summary>
    public static bool Unresolved(string status) => status is Processing or NeedsReview or ExtractionFailed;
}

/// <summary>Deterministic reasons that put an item into needs_review (never auto-rejected).</summary>
public static class ExpenseClaimItemReviewReasons
{
    public const string AmountMissing = "amount_missing";
    public const string DateMissing = "date_missing";
    public const string CategoryUnknown = "category_unknown";
    public const string CurrencyUnsupported = "currency_unsupported";
    public const string LowConfidence = "low_confidence";
    public const string LowOcrConfidence = "low_ocr_confidence";
    public const string VatUnclear = "vat_unclear";
    public const string OutsideTripDates = "outside_trip_dates";
    public const string DuplicateFile = "duplicate_file";
    public const string DuplicateInvoice = "duplicate_invoice";
    public const string DuplicateAmountDate = "duplicate_amount_date";
    public const string WorkflowRequiresReview = "workflow_require_review";
}

/// <summary>Safe, persisted item failure codes (no provider text).</summary>
public static class ExpenseClaimItemErrors
{
    public const string FileUnavailable = "file_unavailable";
    public const string OcrUnavailable = "ocr_unavailable";
    public const string TypeNotEnabled = "expense_claim_type_not_enabled";
}

/// <summary>The values an item contributes (confirmed values win over extracted ones).</summary>
public sealed record ExpenseClaimItemValues(
    string? Category, string? MerchantName, string? MerchantTaxCode, string? DocumentNumber, DateOnly? DocumentDate,
    decimal? GrossAmount, decimal? VatAmount, string? Currency, string? Description);

public sealed record ExpenseClaimItemDuplicateEvidence(bool SameFile, bool SameInvoice, bool SameAmountDate);

public static class ExpenseClaimItemRules
{
    /// <summary>Review triggers (not accounting policy): below these the model/OCR result needs a human look.</summary>
    public const decimal MinExtractionConfidence = 0.80m;
    public const decimal MinOcrConfidence = 0.60m;
    public const int MaxFilesPerBatch = 30;
    public const long MaxFileBytes = 25L * 1024 * 1024;

    /// <summary>Blank currency follows the existing claim rule (VND); anything else is surfaced, never converted.</summary>
    public static string NormalizeCurrency(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "VND" : currency.Trim().ToUpperInvariant();

    /// <summary>Maps a model-proposed label onto the department's configured categories (case-insensitive); null = unknown.</summary>
    public static string? MatchCategory(string? proposed, IReadOnlyCollection<string> configured)
    {
        if (string.IsNullOrWhiteSpace(proposed)) return null;
        return configured.FirstOrDefault(c => string.Equals(c.Trim(), proposed.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static List<string> ReviewReasons(
        ExpenseClaimItemValues v, decimal? extractionConfidence, decimal? ocrConfidence, IReadOnlyCollection<string> configuredCategories,
        DateOnly? tripStart, DateOnly? tripEnd, ExpenseClaimItemDuplicateEvidence duplicates, bool workflowRequiresReview)
    {
        var reasons = new List<string>();
        if (v.GrossAmount is not > 0m) reasons.Add(ExpenseClaimItemReviewReasons.AmountMissing);
        if (v.DocumentDate is null) reasons.Add(ExpenseClaimItemReviewReasons.DateMissing);
        if (MatchCategory(v.Category, configuredCategories) is null) reasons.Add(ExpenseClaimItemReviewReasons.CategoryUnknown);
        if (NormalizeCurrency(v.Currency) != "VND") reasons.Add(ExpenseClaimItemReviewReasons.CurrencyUnsupported);
        if (extractionConfidence is null || extractionConfidence < MinExtractionConfidence) reasons.Add(ExpenseClaimItemReviewReasons.LowConfidence);
        if (ocrConfidence is { } ocr && ocr < MinOcrConfidence) reasons.Add(ExpenseClaimItemReviewReasons.LowOcrConfidence);
        if (v.VatAmount is { } vat && (vat < 0m || (v.GrossAmount is { } g && vat > g))) reasons.Add(ExpenseClaimItemReviewReasons.VatUnclear);
        if (v.DocumentDate is { } d && ((tripStart is { } s && d < s) || (tripEnd is { } e && d > e)))
            reasons.Add(ExpenseClaimItemReviewReasons.OutsideTripDates);
        if (duplicates.SameFile) reasons.Add(ExpenseClaimItemReviewReasons.DuplicateFile);
        if (duplicates.SameInvoice) reasons.Add(ExpenseClaimItemReviewReasons.DuplicateInvoice);
        if (duplicates.SameAmountDate) reasons.Add(ExpenseClaimItemReviewReasons.DuplicateAmountDate);
        if (workflowRequiresReview) reasons.Add(ExpenseClaimItemReviewReasons.WorkflowRequiresReview);
        return reasons;
    }

    /// <summary>
    /// Input line for the EXISTING claim engine. Only ready items are passed.
    /// The document number is passed as an invoice number only when the bill
    /// carries a seller tax code — a ticket/receipt number alone is not a
    /// GTGT invoice (and must not create input-VAT credit or a tax-portal
    /// requirement on its own).
    /// </summary>
    public static ExpenseClaimItemInput ToEngineInput(int seq, ExpenseClaimItemValues v) =>
        new(seq, v.Category!, v.Description ?? v.MerchantName, v.GrossAmount!.Value, ReceiptAttached: true,
            VendorMst: string.IsNullOrWhiteSpace(v.MerchantTaxCode) ? null : v.MerchantTaxCode,
            InvoiceNo: string.IsNullOrWhiteSpace(v.MerchantTaxCode) || string.IsNullOrWhiteSpace(v.DocumentNumber) ? null : v.DocumentNumber,
            DocumentDate: v.DocumentDate, VatAmount: v.VatAmount);

    private const int MaxRawTextChars = 20_000;

    /// <summary>
    /// One bill → one extraction. Gateway-owned fixed schema; the published
    /// Agent's expense_claim instructions/skills are appended when configured.
    /// The model proposes values only — never an account, never tax status.
    /// </summary>
    public static string BuildItemPrompt(ManifestDocumentType? typeConfig, IReadOnlyCollection<string> categories, string rawText)
    {
        var truncated = rawText.Length > MaxRawTextChars ? rawText[..MaxRawTextChars] : rawText;
        var sb = new StringBuilder();
        sb.AppendLine("Bạn là chuyên viên kế toán VN. Raw OCR text bên dưới là MỘT hoá đơn/chứng từ chi phí (vé xe/tàu/máy bay, hoá đơn khách sạn, hoá đơn/biên lai ăn uống, taxi/Grab, gửi xe, chi phí công tác khác) nộp kèm đề nghị hoàn ứng công tác.");
        sb.AppendLine("Trích xuất ĐÚNG MỘT khoản chi từ chứng từ này. Không gộp, không tách thành nhiều khoản.");
        sb.AppendLine("Trả về MỘT JSON object phẳng với các trường (bỏ qua hoặc để null nếu chứng từ không có — KHÔNG bịa):");
        sb.AppendLine($"- category: chọn MỘT trong [{string.Join(", ", categories)}] nếu phù hợp rõ ràng; nếu không chắc trả \"other\"");
        sb.AppendLine("- merchant_name: tên người bán/nhà cung cấp");
        sb.AppendLine("- merchant_tax_code: MST người bán (đúng 10 hoặc 13 số như in trên chứng từ)");
        sb.AppendLine("- document_number: số hoá đơn / số vé / số biên lai");
        sb.AppendLine("- document_date: ngày chứng từ (YYYY-MM-DD)");
        sb.AppendLine("- gross_amount: TỔNG số tiền thanh toán (số, không đơn vị)");
        sb.AppendLine("- vat_amount: tiền thuế GTGT CHỈ khi chứng từ ghi rõ; không tự suy ra theo %");
        sb.AppendLine("- currency: mã tiền tệ (mặc định VND nếu chứng từ không ghi khác)");
        sb.AppendLine("- description: mô tả ngắn khoản chi");
        sb.AppendLine("- confidence: độ tin cậy của bạn với các giá trị trên (0..1)");
        sb.AppendLine("KHÔNG đề xuất tài khoản kế toán, KHÔNG kết luận tình trạng xác minh thuế.");
        if (typeConfig is not null)
        {
            if (!string.IsNullOrWhiteSpace(typeConfig.ExtractionInstructions))
                sb.AppendLine($"Hướng dẫn (cấu hình đã publish — {typeConfig.Code}): {typeConfig.ExtractionInstructions}");
            foreach (var skill in typeConfig.Skills)
            {
                if (skill.SourceKind == "declarative" && skill.Content is not null)
                    sb.AppendLine($"===== SKILL {skill.Code} =====\n{skill.Content}\n===== END SKILL =====");
                else
                    sb.AppendLine($"Áp dụng skill `{skill.Code}`.");
            }
        }
        sb.AppendLine("Chỉ trả 1 code block JSON, KHÔNG kèm markdown khác, KHÔNG gọi tool:");
        sb.AppendLine("```json\n{ ...fields object... }\n```");
        sb.AppendLine();
        sb.AppendLine("===== RAW OCR TEXT =====");
        sb.AppendLine(truncated);
        sb.AppendLine("===== END =====");
        return sb.ToString();
    }
}
