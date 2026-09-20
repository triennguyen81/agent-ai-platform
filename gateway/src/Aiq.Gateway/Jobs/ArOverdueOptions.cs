namespace Aiq.Gateway.Jobs;

// R08 — day thresholds for the AR overdue escalation ladder
// (AccountsReceivableOverdueJob). Bound from appsettings section
// "ArOverdue". Defaults match the SAME 30/60/90-day aging buckets already
// used elsewhere for AR/AP aging (Customers/ICustomerRepository.cs,
// Suppliers/ISupplierRepository.cs) — not an invented number, the
// existing bucket boundary this system already treats as meaningful.
public sealed class ArOverdueOptions
{
    public const string SectionName = "ArOverdue";

    // "Nhắc lần 1" — first reminder.
    public int ReminderDays { get; init; } = 1;

    // "Công văn" — formal notice.
    public int NoticeDays { get; init; } = 30;

    // "Tính lãi phạt theo hợp đồng" — penalty calculation reminder.
    public int PenaltyDays { get; init; } = 60;

    // "Chuyển pháp chế" — escalate to legal.
    public int LegalDays { get; init; } = 90;
}
