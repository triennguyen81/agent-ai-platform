using System.Globalization;
using System.Text.RegularExpressions;

namespace Aiq.Gateway.Vouchers.Validation;

public static class ValidationSeverity
{
    public const string Error = "error";
    public const string Warning = "warning";
}

/// <summary>One finding. <c>Field</c> uses the snake_case column / extractor name (line items: <c>line_items[i].field</c>).</summary>
public sealed record ValidationIssue(string Code, string Severity, string? Field, string Message);

public sealed record VoucherValidationResult(int ValidatorVersion, IReadOnlyList<ValidationIssue> Issues)
{
    public bool HasErrors => Issues.Any(i => i.Severity == ValidationSeverity.Error);
}

/// <summary>A VAT rate valid from–to (inclusive). <c>Rate</c> is a percent; null for KCT / KKKNT.</summary>
public sealed record VatRateEntry(string Code, decimal? Rate, DateOnly EffectiveFrom, DateOnly? EffectiveTo, string? LegalRef)
{
    public bool IsEffectiveOn(DateOnly date) => EffectiveFrom <= date && (EffectiveTo is null || date <= EffectiveTo);
}

/// <summary>Everything the validator needs besides the voucher — loaded by the caller, never by the validator.</summary>
public sealed record VoucherValidationContext(
    string? CompanyTaxCode,
    IReadOnlyList<VatRateEntry> VatRates,
    DateOnly Today,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? RequiredFields = null,
    decimal ConfidenceThreshold = 0.80m,
    IReadOnlyDictionary<string, decimal>? FieldConfidence = null);

/// <summary>
/// Chứng từ flow v2 PR2: deterministic voucher checks (docs/flows/chung-tu-proposed.md
/// §3.3, decisions QĐ 7–11). Pure: no I/O, no clock, no AI. Advisory only in
/// PR2 — nothing is blocked on the result yet.
/// </summary>
public static class VoucherValidator
{
    /// <summary>Bumped whenever a rule changes, so stored results can be told apart.</summary>
    public const int Version = 1;

    private static readonly string[] InvoiceRequired =
        ["document_date", "document_number", "seller_name", "buyer_name", "seller_mst", "subtotal", "vat_rate", "vat_amount", "total_amount"];
    private static readonly string[] CashRequired = ["document_date", "document_number", "total_amount"];
    private static readonly string[] ExpenseClaimRequired = ["document_date", "total_amount"];

    /// <summary>QĐ 8 (tạm, chờ kế toán): minimum per type; tenant settings may replace a type's list.</summary>
    public static IReadOnlyList<string> DefaultRequiredFields(string voucherType) => voucherType switch
    {
        VoucherTypes.InvoiceIn or VoucherTypes.InvoiceOut or VoucherTypes.LegacyInvoice => InvoiceRequired,
        VoucherTypes.Receipt or VoucherTypes.Payment or VoucherTypes.BankTransfer => CashRequired,
        VoucherTypes.ExpenseClaim => ExpenseClaimRequired,
        _ => [],
    };

    private static readonly Regex AccountCode = new(@"^\d{3,8}$", RegexOptions.CultureInvariant);

    public static VoucherValidationResult Validate(VoucherDetail detail, VoucherValidationContext ctx)
    {
        var v = detail.Voucher;
        var lines = detail.LineItems;
        var issues = new List<ValidationIssue>();
        void Add(string code, string severity, string? field, string message) => issues.Add(new(code, severity, field, message));

        var isInvoice = v.VoucherType is VoucherTypes.InvoiceIn or VoucherTypes.InvoiceOut or VoucherTypes.LegacyInvoice;
        // QĐ 10: 1 đồng per line, the voucher total within lines × 1 đồng (at least 1).
        var tolerance = Math.Max(1, lines.Count);

        // ---- REQUIRED_FIELD -------------------------------------------------
        var required = ctx.RequiredFields is not null && ctx.RequiredFields.TryGetValue(v.VoucherType, out var custom)
            ? custom
            : DefaultRequiredFields(v.VoucherType);
        foreach (var field in required)
        {
            if (!IsPresent(v, lines, field))
                Add("REQUIRED_FIELD", ValidationSeverity.Error, field, $"Thiếu trường bắt buộc: {field}.");
        }

        // ---- MST ------------------------------------------------------------
        foreach (var (field, value) in new[] { ("seller_mst", v.SellerMst), ("buyer_mst", v.BuyerMst) })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (!MstRules.IsValidFormat(value))
            {
                Add("MST_FORMAT", ValidationSeverity.Error, field, $"MST '{value.Trim()}' không đúng định dạng (10 số, 13 số dạng 10-3, hoặc 12 số CCCD).");
                continue;
            }
            if (MstRules.ChecksumOk(value) == false)
                Add("MST_CHECKSUM", ValidationSeverity.Warning, field, $"MST '{value.Trim()}' sai số kiểm tra — kiểm tra lại.");
        }

        // ---- Company is a party of the invoice -----------------------------
        if (v.VoucherType is VoucherTypes.InvoiceIn or VoucherTypes.InvoiceOut)
        {
            var company = MstRules.Normalize(ctx.CompanyTaxCode);
            if (company is null)
            {
                Add("COMPANY_TAX_CODE_MISSING", ValidationSeverity.Warning, null, "Chưa cấu hình MST công ty trong hồ sơ công ty — chưa kiểm tra được bên mua/bán.");
            }
            else if (v.VoucherType == VoucherTypes.InvoiceIn && MstRules.Normalize(v.BuyerMst) is { } buyer && buyer != company)
            {
                Add("BUYER_IS_COMPANY", ValidationSeverity.Error, "buyer_mst", "Hoá đơn đầu vào: MST người mua không phải MST công ty.");
            }
            else if (v.VoucherType == VoucherTypes.InvoiceOut && MstRules.Normalize(v.SellerMst) is { } seller && seller != company)
            {
                Add("SELLER_IS_COMPANY", ValidationSeverity.Error, "seller_mst", "Hoá đơn đầu ra: MST người bán không phải MST công ty.");
            }
        }

        // ---- Amounts --------------------------------------------------------
        if (v.Subtotal is { } subtotal && v.VatAmount is { } vat && v.TotalAmount is { } total)
        {
            var expected = subtotal - (v.DiscountAmount ?? 0m) + vat;
            if (Math.Abs(expected - total) > tolerance)
                Add("AMOUNT_SUM", ValidationSeverity.Error, "total_amount",
                    $"Tổng tiền {Money(total)} ≠ tiền hàng − chiết khấu + thuế = {Money(expected)}.");
        }
        if (v.Subtotal is { } docSubtotal && lines.Count > 0 && lines.All(l => l.Subtotal is not null))
        {
            var sum = lines.Sum(l => l.Subtotal!.Value);
            if (Math.Abs(sum - docSubtotal) > tolerance)
                Add("LINE_SUM", ValidationSeverity.Error, "subtotal",
                    $"Tổng tiền các dòng hàng {Money(sum)} ≠ tiền hàng chứng từ {Money(docSubtotal)}.");
        }
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l is { Quantity: { } q, UnitPrice: { } p, Subtotal: { } s } && Math.Abs(q * p - s) > 1m)
                Add("LINE_AMOUNT", ValidationSeverity.Warning, $"line_items[{i}].subtotal",
                    $"Dòng {i + 1}: số lượng × đơn giá = {Money(q * p)} ≠ thành tiền {Money(s)}.");
            if (l is { Subtotal: { } ls, VatRate: { } lr, VatAmount: { } lv } && Math.Abs(ls * lr / 100m - lv) > 1m)
                Add("VAT_LINE", ValidationSeverity.Warning, $"line_items[{i}].vat_amount",
                    $"Dòng {i + 1}: tiền thuế {Money(lv)} ≠ thành tiền × {lr:0.##}% = {Money(ls * lr / 100m)}.");
        }
        if (v is { Subtotal: { } baseAmount, VatRate: { } rate, VatAmount: { } vatAmount })
        {
            var expectedVat = (baseAmount - (v.DiscountAmount ?? 0m)) * rate / 100m;
            if (Math.Abs(expectedVat - vatAmount) > tolerance)
                Add("VAT_AMOUNT", ValidationSeverity.Warning, "vat_amount",
                    $"Tiền thuế {Money(vatAmount)} ≠ (tiền hàng − chiết khấu) × {rate:0.##}% = {Money(expectedVat)}.");
        }

        // ---- VAT rate by invoice date (QĐ 11) -------------------------------
        var rates = new List<(string Field, decimal Rate)>();
        if (v.VatRate is { } docRate) rates.Add(("vat_rate", docRate));
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].VatRate is { } lineRate) rates.Add(($"line_items[{i}].vat_rate", lineRate));
        if (rates.Count > 0 && v.DocumentDate is { } invoiceDate)
        {
            if (ctx.VatRates.Count == 0)
            {
                Add("VAT_RATE_TABLE_EMPTY", ValidationSeverity.Warning, null, "Chưa có bảng thuế suất — chưa kiểm tra được thuế suất.");
            }
            else
            {
                foreach (var (field, r) in rates)
                {
                    if (!ctx.VatRates.Any(e => e.Rate == r && e.IsEffectiveOn(invoiceDate)))
                        Add("VAT_RATE", ValidationSeverity.Error, field,
                            $"Thuế suất {r:0.##}% không có hiệu lực vào ngày hoá đơn {invoiceDate:dd/MM/yyyy}.");
                }
            }
        }

        // ---- Dates -----------------------------------------------------------
        if (v.DocumentDate is { } date && date > ctx.Today)
            Add("DATE_FUTURE", ValidationSeverity.Error, "document_date", $"Ngày chứng từ {date:dd/MM/yyyy} ở tương lai.");

        // ---- Journal (non-rejected suggestions) ----------------------------
        var journal = detail.JournalSuggestions.Where(j => j.RejectedAt is null).ToList();
        foreach (var (j, i) in journal.Select((j, i) => (j, i)))
        {
            var debit = j.DebitAccount.Trim();
            var credit = j.CreditAccount.Trim();
            if (debit.Length == 0 || credit.Length == 0 || debit == credit || j.Amount <= 0)
                Add("JOURNAL_BALANCE", ValidationSeverity.Error, $"journal[{i}]",
                    $"Bút toán {i + 1}: cần TK Nợ và TK Có khác nhau và số tiền > 0.");
            foreach (var account in new[] { debit, credit }.Where(a => a.Length > 0 && !AccountCode.IsMatch(a)))
                Add("JOURNAL_ACCOUNT", ValidationSeverity.Warning, $"journal[{i}]", $"Bút toán {i + 1}: mã TK '{account}' không đúng dạng số.");
        }
        if (journal.Count > 0 && v.TotalAmount is { } invoiceTotal)
        {
            // Purchase: the invoice total is owed (331) or paid (111/112); sale: receivable (131) or received.
            decimal? booked = v.VoucherType switch
            {
                VoucherTypes.InvoiceIn => journal.Where(j => IsSettlementAccount(j.CreditAccount, "331")).Sum(j => j.Amount),
                VoucherTypes.InvoiceOut => journal.Where(j => IsSettlementAccount(j.DebitAccount, "131")).Sum(j => j.Amount),
                _ => null,
            };
            if (booked is { } b && Math.Abs(b - invoiceTotal) > 1m)
                Add("JOURNAL_TOTAL", ValidationSeverity.Error, "journal",
                    $"Bút toán ghi {Money(b)} vào TK thanh toán ≠ tổng tiền hoá đơn {Money(invoiceTotal)}.");
        }

        // ---- Confidence (QĐ 7) -----------------------------------------------
        if (ctx.FieldConfidence is { } confidence)
        {
            foreach (var field in required)
            {
                if (confidence.TryGetValue(field, out var c) && c < ctx.ConfidenceThreshold)
                    Add("LOW_CONFIDENCE", ValidationSeverity.Warning, field,
                        $"Độ tin cậy trích xuất của {field} là {c:0.00} (< {ctx.ConfidenceThreshold:0.00}) — cần kiểm tra.");
            }
        }

        return new VoucherValidationResult(Version, issues);
    }

    private static bool IsSettlementAccount(string account, string counterparty)
    {
        var a = account.Trim();
        return a.StartsWith(counterparty, StringComparison.Ordinal)
            || a.StartsWith("111", StringComparison.Ordinal)
            || a.StartsWith("112", StringComparison.Ordinal);
    }

    private static bool IsPresent(VoucherRecord v, IReadOnlyList<VoucherLineItemRecord> lines, string field) => field switch
    {
        "document_date" => v.DocumentDate is not null,
        "document_number" => !string.IsNullOrWhiteSpace(v.DocumentNumber),
        "document_series" => !string.IsNullOrWhiteSpace(v.DocumentSeries),
        "seller_name" => !string.IsNullOrWhiteSpace(v.SellerName),
        "seller_mst" => !string.IsNullOrWhiteSpace(v.SellerMst),
        "seller_address" => !string.IsNullOrWhiteSpace(v.SellerAddress),
        "buyer_name" => !string.IsNullOrWhiteSpace(v.BuyerName),
        "buyer_mst" => !string.IsNullOrWhiteSpace(v.BuyerMst),
        "buyer_address" => !string.IsNullOrWhiteSpace(v.BuyerAddress),
        "payment_method" => !string.IsNullOrWhiteSpace(v.PaymentMethod),
        "subtotal" => v.Subtotal is not null,
        // A mixed-rate invoice carries its rates on the lines.
        "vat_rate" => v.VatRate is not null || (lines.Count > 0 && lines.All(l => l.VatRate is not null)),
        "vat_amount" => v.VatAmount is not null,
        "discount_amount" => v.DiscountAmount is not null,
        "total_amount" => v.TotalAmount is not null,
        "amount_in_words" => !string.IsNullOrWhiteSpace(v.AmountInWords),
        // Unknown names in a tenant list are reported, never silently passed.
        _ => false,
    };

    // Vietnamese grouping (10.000.000,5) without CultureInfo("vi-VN"): the app
    // runs in globalization-invariant mode, where named cultures throw.
    private static readonly NumberFormatInfo VndFormat = new() { NumberGroupSeparator = ".", NumberDecimalSeparator = "," };

    private static string Money(decimal amount) => amount.ToString("#,##0.##", VndFormat);
}
