using Aiq.Gateway.Payin;

namespace Aiq.Gateway.Tests.Payin;

public sealed class PayinCalculatorTests
{
    private static ContractTerms NoDiscount() =>
        new(TradeDiscountPct: 0m,
            EarlyPaymentDiscountPct: 0m,
            EarlyPaymentDays: null,
            VolumeRebateTiers: Array.Empty<VolumeRebateTier>());

    private static ContractTerms EarlyPay2Pct10Days() =>
        new(TradeDiscountPct: 0m,
            EarlyPaymentDiscountPct: 2m,
            EarlyPaymentDays: 10,
            VolumeRebateTiers: Array.Empty<VolumeRebateTier>());

    private static ContractTerms Volume2Tiers() =>
        new(TradeDiscountPct: 0m,
            EarlyPaymentDiscountPct: 0m,
            EarlyPaymentDays: null,
            VolumeRebateTiers: new[]
            {
                new VolumeRebateTier(500_000_000m, 1.5m),
                new VolumeRebateTier(2_000_000_000m, 3.0m),
            });

    [Fact]
    public void ExpectedAmount_NoDiscount_EqualsInvoice()
    {
        var input = new PayinInput(
            ReceivedAmount: 100_000_000m,
            InvoiceTotalAmount: 100_000_000m,
            InvoiceDate: new DateOnly(2026, 9, 1),
            PaymentDate: new DateOnly(2026, 9, 15),
            PeriodToDateSales: 0m,
            Terms: NoDiscount());

        Assert.Equal(100_000_000m, PayinCalculator.ComputeExpectedAmount(input));
    }

    [Fact]
    public void ExpectedAmount_EarlyPayment_ApplyDiscount()
    {
        // 100tr HĐ, trả trong 5 ngày (≤10 window), CK 2% → expected 98tr
        var input = new PayinInput(
            ReceivedAmount: 98_000_000m,
            InvoiceTotalAmount: 100_000_000m,
            InvoiceDate: new DateOnly(2026, 9, 1),
            PaymentDate: new DateOnly(2026, 9, 6),
            PeriodToDateSales: 0m,
            Terms: EarlyPay2Pct10Days());

        Assert.Equal(98_000_000m, PayinCalculator.ComputeExpectedAmount(input));
    }

    [Fact]
    public void ExpectedAmount_LateForEarlyPayment_NoDiscount()
    {
        // Trả sau 15 ngày (>10 window) → không hưởng CK
        var input = new PayinInput(
            ReceivedAmount: 100_000_000m,
            InvoiceTotalAmount: 100_000_000m,
            InvoiceDate: new DateOnly(2026, 9, 1),
            PaymentDate: new DateOnly(2026, 9, 16),
            PeriodToDateSales: 0m,
            Terms: EarlyPay2Pct10Days());

        Assert.Equal(100_000_000m, PayinCalculator.ComputeExpectedAmount(input));
    }

    [Fact]
    public void Classify_ExactMatch_ReturnsMatched()
    {
        var input = new PayinInput(
            ReceivedAmount: 100_000_000m,
            InvoiceTotalAmount: 100_000_000m,
            InvoiceDate: new DateOnly(2026, 9, 1),
            PaymentDate: new DateOnly(2026, 9, 15),
            PeriodToDateSales: 0m,
            Terms: NoDiscount());

        var result = PayinCalculator.Classify(input);
        Assert.Equal(PayinClassification.Matched, result.Classification);
        Assert.Equal(0m, result.DeltaAmount);
    }

    [Fact]
    public void Classify_WithinTolerance_StillMatched()
    {
        // Chỉ lệch 500đ (< tolerance 1000đ) → matched
        var input = new PayinInput(
            ReceivedAmount: 99_999_500m,
            InvoiceTotalAmount: 100_000_000m,
            InvoiceDate: new DateOnly(2026, 9, 1),
            PaymentDate: new DateOnly(2026, 9, 15),
            PeriodToDateSales: 0m,
            Terms: NoDiscount());

        Assert.Equal(PayinClassification.Matched, PayinCalculator.Classify(input).Classification);
    }

    [Fact]
    public void Classify_EarlyPayment2Pct_DetectsCorrectly()
    {
        // KH trả 98tr trong 5 ngày → khớp CK 2% early payment
        var input = new PayinInput(
            ReceivedAmount: 98_000_000m,
            InvoiceTotalAmount: 100_000_000m,
            InvoiceDate: new DateOnly(2026, 9, 1),
            PaymentDate: new DateOnly(2026, 9, 6),
            PeriodToDateSales: 0m,
            Terms: EarlyPay2Pct10Days());

        var result = PayinCalculator.Classify(input);
        Assert.Equal(PayinClassification.Matched, result.Classification);
        // Expected đã bao gồm CK 2%, delta = 0
    }

    [Fact]
    public void Classify_LateWithDiscount_ReturnsUnknown()
    {
        // KH trả 98tr NHƯNG đã trễ (15 ngày > 10 window) → không đủ CK
        // → không phân loại được, escalate
        var input = new PayinInput(
            ReceivedAmount: 98_000_000m,
            InvoiceTotalAmount: 100_000_000m,
            InvoiceDate: new DateOnly(2026, 9, 1),
            PaymentDate: new DateOnly(2026, 9, 16),
            PeriodToDateSales: 0m,
            Terms: EarlyPay2Pct10Days());

        var result = PayinCalculator.Classify(input);
        Assert.Equal(PayinClassification.Unknown, result.Classification);
    }

    [Fact]
    public void Classify_VolumeRebate_TriggerAtFirstTier()
    {
        // Đã bán 1 tỷ (vượt ngưỡng 500tr), nhận HĐ mới 100tr → hưởng rebate 1.5%
        // → nhận 98.5tr thay vì 100tr
        var input = new PayinInput(
            ReceivedAmount: 98_500_000m,
            InvoiceTotalAmount: 100_000_000m,
            InvoiceDate: new DateOnly(2026, 9, 1),
            PaymentDate: new DateOnly(2026, 9, 15),
            PeriodToDateSales: 1_000_000_000m,
            Terms: Volume2Tiers());

        var result = PayinCalculator.Classify(input);
        Assert.Equal(PayinClassification.VolumeRebate, result.Classification);
        Assert.Equal(1.5m, result.AppliedRebatePct);
    }

    [Fact]
    public void Classify_VolumeRebate_TriggerAtSecondTier()
    {
        // Đã bán 3 tỷ (vượt ngưỡng 2 tỷ), rebate 3%
        var input = new PayinInput(
            ReceivedAmount: 97_000_000m,
            InvoiceTotalAmount: 100_000_000m,
            InvoiceDate: new DateOnly(2026, 9, 1),
            PaymentDate: new DateOnly(2026, 9, 15),
            PeriodToDateSales: 3_000_000_000m,
            Terms: Volume2Tiers());

        var result = PayinCalculator.Classify(input);
        Assert.Equal(PayinClassification.VolumeRebate, result.Classification);
        Assert.Equal(3.0m, result.AppliedRebatePct);
    }

    [Fact]
    public void Classify_Overpay_ReturnsReturnOrCredit()
    {
        // Nhận thừa 5tr → có thể là advance / trả hàng
        var input = new PayinInput(
            ReceivedAmount: 105_000_000m,
            InvoiceTotalAmount: 100_000_000m,
            InvoiceDate: new DateOnly(2026, 9, 1),
            PaymentDate: new DateOnly(2026, 9, 15),
            PeriodToDateSales: 0m,
            Terms: NoDiscount());

        Assert.Equal(PayinClassification.ReturnOrCredit, PayinCalculator.Classify(input).Classification);
    }

    [Fact]
    public void Classify_Underpay_NoMatchingDiscount_ReturnsUnknown()
    {
        // Nhận 90tr (thiếu 10%) — không khớp CK 2% cũng không khớp rebate
        var input = new PayinInput(
            ReceivedAmount: 90_000_000m,
            InvoiceTotalAmount: 100_000_000m,
            InvoiceDate: new DateOnly(2026, 9, 1),
            PaymentDate: new DateOnly(2026, 9, 6),
            PeriodToDateSales: 0m,
            Terms: EarlyPay2Pct10Days());

        Assert.Equal(PayinClassification.Unknown, PayinCalculator.Classify(input).Classification);
    }

    [Fact]
    public void FindApplicableRebateTier_PicksHighestQualifying()
    {
        var tiers = new[]
        {
            new VolumeRebateTier(500_000_000m, 1.5m),
            new VolumeRebateTier(2_000_000_000m, 3.0m),
        };
        var t = PayinCalculator.FindApplicableRebateTier(2_500_000_000m, tiers);
        Assert.NotNull(t);
        Assert.Equal(3.0m, t!.RebatePct);
    }

    [Fact]
    public void FindApplicableRebateTier_BelowFirstThreshold_ReturnsNull()
    {
        var tiers = new[] { new VolumeRebateTier(500_000_000m, 1.5m) };
        Assert.Null(PayinCalculator.FindApplicableRebateTier(400_000_000m, tiers));
    }

    [Fact]
    public void ParseTiers_ValidJson_ReturnsList()
    {
        var json = "[{\"threshold\": 500000000, \"rebate_pct\": 1.5}, {\"threshold\": 2000000000, \"rebate_pct\": 3.0}]";
        var tiers = PayinCalculator.ParseTiers(json);
        Assert.Equal(2, tiers.Count);
        Assert.Equal(500_000_000m, tiers[0].Threshold);
        Assert.Equal(3.0m, tiers[1].RebatePct);
    }

    [Fact]
    public void ParseTiers_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Empty(PayinCalculator.ParseTiers(null));
        Assert.Empty(PayinCalculator.ParseTiers(""));
        Assert.Empty(PayinCalculator.ParseTiers("[]"));
    }

    [Fact]
    public void ParseTiers_MalformedJson_ReturnsEmptyNotThrow()
    {
        Assert.Empty(PayinCalculator.ParseTiers("not json"));
        Assert.Empty(PayinCalculator.ParseTiers("{\"broken\":"));
    }
}
