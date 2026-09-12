using System.Text.Json;

namespace Aiq.Gateway.Payin;

/// <summary>
/// Sprint 6' A4+A5: tính expected amount + classify chênh lệch giữa số tiền
/// nhận (bank receipt) và số tiền kỳ vọng (theo contract terms) — dựa trên
/// flowchart payin-reconciliation-flowchart.md của anh Toàn.
///
/// Pure functions — không truy DB, dễ unit test. Caller (Voucher extract
/// job hoặc admin dashboard) tự chuẩn bị dữ liệu contract + invoice trước
/// khi gọi.
/// </summary>
public sealed record ContractTerms(
    decimal TradeDiscountPct,
    decimal EarlyPaymentDiscountPct,
    int? EarlyPaymentDays,
    IReadOnlyList<VolumeRebateTier> VolumeRebateTiers);

public sealed record VolumeRebateTier(decimal Threshold, decimal RebatePct);

public sealed record PayinInput(
    decimal ReceivedAmount,
    decimal InvoiceTotalAmount,
    DateOnly InvoiceDate,
    DateOnly PaymentDate,
    decimal PeriodToDateSales,
    ContractTerms Terms);

public sealed record PayinResult(
    decimal ExpectedAmount,
    PayinClassification Classification,
    decimal DeltaAmount,
    string Rationale,
    decimal? AppliedDiscountPct,
    decimal? AppliedRebatePct);

public enum PayinClassification
{
    /// <summary>Số tiền nhận = expected (trong tolerance). Bút toán Nợ 112 / Có 131.</summary>
    Matched,
    /// <summary>Nhận thiếu = expected − CK thanh toán sớm hợp lệ. Bút toán thêm Nợ 635.</summary>
    EarlyPaymentDiscount,
    /// <summary>Nhận thiếu = expected − volume rebate hợp lệ. Bút toán Nợ 521.</summary>
    VolumeRebate,
    /// <summary>Nhận thừa hoặc nhận âm — có thể là trả hàng / điều chỉnh. Cần kế toán xác nhận.</summary>
    ReturnOrCredit,
    /// <summary>Không phân loại được. Tạm treo Có 338, escalate Sales/BGĐ.</summary>
    Unknown,
}

public static class PayinCalculator
{
    /// <summary>
    /// Tolerance mặc định ±1000đ (theo ghi chú kỹ thuật của flowchart).
    /// Caller có thể override qua PayinCalculator.Classify(..., tolerance:).
    /// </summary>
    public const decimal DefaultToleranceVnd = 1000m;

    /// <summary>
    /// Tính số tiền kỳ vọng nếu khách hàng thanh toán vào PaymentDate.
    /// Formula: Expected = Invoice - (Invoice × EarlyPaymentDiscountPct)
    /// nếu payment_days <= early_payment_days. Trade discount đã trừ vào
    /// InvoiceTotalAmount tại thời điểm phát hành HĐ nên không tính lại.
    /// </summary>
    public static decimal ComputeExpectedAmount(PayinInput input)
    {
        var days = input.PaymentDate.DayNumber - input.InvoiceDate.DayNumber;
        var eligibleForEarlyDiscount =
            input.Terms.EarlyPaymentDays.HasValue
            && input.Terms.EarlyPaymentDiscountPct > 0m
            && days >= 0
            && days <= input.Terms.EarlyPaymentDays.Value;

        if (!eligibleForEarlyDiscount)
        {
            return input.InvoiceTotalAmount;
        }

        var discount = input.InvoiceTotalAmount * (input.Terms.EarlyPaymentDiscountPct / 100m);
        return Math.Round(input.InvoiceTotalAmount - discount, 0, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Phân loại chênh lệch dựa trên contract terms. Return đủ thông tin để
    /// caller (UI hoặc journal generator) hiển thị/generate bút toán.
    /// </summary>
    public static PayinResult Classify(PayinInput input, decimal tolerance = DefaultToleranceVnd)
    {
        var expected = ComputeExpectedAmount(input);
        var delta = input.ReceivedAmount - expected;
        var absDelta = Math.Abs(delta);

        // 1. Trong tolerance → matched
        if (absDelta <= tolerance)
        {
            return new PayinResult(
                expected,
                PayinClassification.Matched,
                delta,
                $"Nhận đủ (|{delta:N0}| ≤ {tolerance:N0}đ). Hạch toán Nợ 112 / Có 131.",
                AppliedDiscountPct: null,
                AppliedRebatePct: null);
        }

        // 2. Nhận thừa hoặc âm → trả hàng / điều chỉnh
        if (delta > tolerance || input.ReceivedAmount < 0)
        {
            return new PayinResult(
                expected,
                PayinClassification.ReturnOrCredit,
                delta,
                delta > 0
                    ? $"Nhận dư {delta:N0}đ so với kỳ vọng — có thể là advance hoặc trả hàng."
                    : "Số tiền âm — có thể là hoàn tiền cho khách. Kế toán xác nhận.",
                AppliedDiscountPct: null,
                AppliedRebatePct: null);
        }

        // 3. Nhận thiếu → thử match với CK thanh toán sớm
        var days = input.PaymentDate.DayNumber - input.InvoiceDate.DayNumber;
        if (input.Terms.EarlyPaymentDays.HasValue
            && input.Terms.EarlyPaymentDiscountPct > 0m
            && days >= 0
            && days <= input.Terms.EarlyPaymentDays.Value)
        {
            // Tính chính xác lại: có thể expected đã áp CK rồi (nếu ComputeExpectedAmount
            // đã trả về giảm giá). Delta lúc này = số phụ trội của CK KH lấy nhiều hơn
            // ta ước tính → check xem có khớp với CK% nào không.
            var impliedDiscountAmount = input.InvoiceTotalAmount - input.ReceivedAmount;
            var impliedDiscountPct = impliedDiscountAmount / input.InvoiceTotalAmount * 100m;

            if (Math.Abs(impliedDiscountPct - input.Terms.EarlyPaymentDiscountPct) <= 0.5m)
            {
                return new PayinResult(
                    expected,
                    PayinClassification.EarlyPaymentDiscount,
                    delta,
                    $"CK thanh toán sớm {input.Terms.EarlyPaymentDiscountPct}% (trả trong {days}/{input.Terms.EarlyPaymentDays.Value} ngày). Hạch toán Nợ 112 / Nợ 635 / Có 131.",
                    AppliedDiscountPct: input.Terms.EarlyPaymentDiscountPct,
                    AppliedRebatePct: null);
            }
        }

        // 4. Thử match với volume rebate (nếu có tier phù hợp)
        var applicableTier = FindApplicableRebateTier(
            input.PeriodToDateSales, input.Terms.VolumeRebateTiers);
        if (applicableTier is not null)
        {
            var expectedRebateAmount = input.InvoiceTotalAmount * (applicableTier.RebatePct / 100m);
            var impliedRebateAmount = input.InvoiceTotalAmount - input.ReceivedAmount;
            if (Math.Abs(expectedRebateAmount - impliedRebateAmount) <= tolerance * 10)
            {
                return new PayinResult(
                    expected,
                    PayinClassification.VolumeRebate,
                    delta,
                    $"Volume rebate {applicableTier.RebatePct}% (doanh số luỹ kế {input.PeriodToDateSales:N0}đ ≥ ngưỡng {applicableTier.Threshold:N0}đ). Hạch toán Nợ 521 / Có 131.",
                    AppliedDiscountPct: null,
                    AppliedRebatePct: applicableTier.RebatePct);
            }
        }

        // 5. Không phân loại được → escalate
        return new PayinResult(
            expected,
            PayinClassification.Unknown,
            delta,
            $"Chênh lệch {delta:N0}đ ({delta / expected * 100m:N2}%) không khớp CK/rebate nào. Tạm treo Có 338 chờ kế toán xác nhận.",
            AppliedDiscountPct: null,
            AppliedRebatePct: null);
    }

    /// <summary>
    /// Tìm bậc rebate cao nhất mà doanh số luỹ kế đã chạm/vượt. Return null
    /// nếu tiers rỗng hoặc chưa chạm bậc nào.
    /// </summary>
    public static VolumeRebateTier? FindApplicableRebateTier(
        decimal periodToDateSales,
        IReadOnlyList<VolumeRebateTier> tiers)
    {
        VolumeRebateTier? applicable = null;
        foreach (var tier in tiers)
        {
            if (periodToDateSales >= tier.Threshold
                && (applicable is null || tier.Threshold > applicable.Threshold))
            {
                applicable = tier;
            }
        }
        return applicable;
    }

    /// <summary>
    /// Parse JSONB `volume_rebate_tiers` từ Postgres thành list.
    /// Return empty list nếu null hoặc parse fail (không throw).
    /// </summary>
    public static IReadOnlyList<VolumeRebateTier> ParseTiers(string? tiersJson)
    {
        if (string.IsNullOrWhiteSpace(tiersJson) || tiersJson == "[]")
        {
            return Array.Empty<VolumeRebateTier>();
        }
        try
        {
            using var doc = JsonDocument.Parse(tiersJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<VolumeRebateTier>();
            }
            var list = new List<VolumeRebateTier>(doc.RootElement.GetArrayLength());
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("threshold", out var t)
                    && el.TryGetProperty("rebate_pct", out var r))
                {
                    list.Add(new VolumeRebateTier(t.GetDecimal(), r.GetDecimal()));
                }
            }
            return list;
        }
        catch (JsonException)
        {
            return Array.Empty<VolumeRebateTier>();
        }
    }
}
