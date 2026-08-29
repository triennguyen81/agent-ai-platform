namespace Aiq.Gateway.Jobs;

// Risk rule thresholds for expense/AP flag detection in VoucherExtractJobRunner.
// Bound from appsettings section "ExpenseRisk". Modify per-env via appsettings
// override or env vars (ExpenseRisk__CashPaymentVatDeductibleThreshold=30000000).
public sealed class ExpenseRiskOptions
{
    public const string SectionName = "ExpenseRisk";

    // TT78 điều 6: HĐ đầu vào > threshold + payment_method='TM' → không được
    // khấu trừ VAT. Default 30M VNĐ (mức luật hiện hành).
    public decimal CashPaymentVatDeductibleThreshold { get; init; } = 30_000_000m;

    // Trên ngưỡng này bắt buộc phải có PO match. Fire flag `no_matching_po`
    // để trưởng phòng review (cho invoice_vat_in).
    public decimal InvoiceRequiresPoAbove { get; init; } = 100_000_000m;

    // Trên ngưỡng này voucher cần L2 approval (kế toán trưởng duyệt).
    public decimal L2ApprovalAbove { get; init; } = 500_000_000m;

    // TT96/2015 điều 4 khoản 2.21: chi phí tiếp khách được khấu trừ tối đa
    // 15% tổng chi phí phát sinh trong kỳ. Dùng cho ExpenseClaimPanel warning.
    public decimal GiftEntertainmentBudgetPct { get; init; } = 15.0m;
}
