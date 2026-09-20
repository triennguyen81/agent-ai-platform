using Aiq.Gateway.Contracts;
using Aiq.Gateway.Data;
using Dapper;
using Hangfire;
using Microsoft.Extensions.Options;

namespace Aiq.Gateway.Jobs;

/// <summary>
/// R07/R08 — daily 07:00 VN scan for AR invoices past due_date with no
/// payment received (sim_ar_ledger open-invoice rows: document_type='invoice',
/// credit=0, debit&gt;0). This is the correct home for the business document's
/// "Quá hạn không thu được... Leo thang theo bậc" escalation ladder — a
/// time-based event with no receipt voucher to trigger on, so it does NOT
/// touch receipt_match_results (see migration 039's comment for why
/// OVERDUE_UNPAID was removed from that table's outcome enum instead of
/// being wired up there).
///
/// Notifies every department_admin in the invoice's own tenant+department
/// (the closest existing-RBAC analog to "Kế toán trưởng"). Idempotent per
/// escalation stage via the same (tenant_id, user_id, kind, ref_id) unique
/// index ContractDeadlineJob already relies on — the `kind` changes as an
/// invoice crosses each threshold, so a re-run never re-sends the same
/// stage twice, but a later run naturally sends the NEXT stage once enough
/// days have passed.
/// </summary>
public sealed class AccountsReceivableOverdueJob
{
    public const string RecurringJobId = "ar-overdue-scan";

    private readonly NpgsqlConnectionFactory _factory;
    private readonly INotificationRepository _notifications;
    private readonly ArOverdueOptions _options;
    private readonly ILogger<AccountsReceivableOverdueJob> _logger;

    public AccountsReceivableOverdueJob(
        NpgsqlConnectionFactory factory,
        INotificationRepository notifications,
        IOptions<ArOverdueOptions> options,
        ILogger<AccountsReceivableOverdueJob> logger)
    {
        _factory = factory;
        _notifications = notifications;
        _options = options.Value;
        _logger = logger;
    }

    [JobDisplayName("AR overdue scan")]
    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task RunAsync(CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);

        const string sql = """
            SELECT
                al.id             AS LedgerId,
                al.document_number AS DocumentNumber,
                al.debit          AS Amount,
                al.due_date       AS DueDate,
                c.name            AS CustomerName,
                c.tenant_id       AS TenantId,
                u.id              AS AdminUserId,
                (CURRENT_DATE - al.due_date) AS DaysOverdue
            FROM sim_ar_ledger al
            JOIN sim_customers c ON c.id = al.customer_id
            JOIN users u ON u.tenant_id = c.tenant_id AND u.department_id = c.department_id
            JOIN user_roles ur ON ur.user_id = u.id
            JOIN roles r ON r.id = ur.role_id AND r.name = 'admin'
            WHERE al.document_type = 'invoice' AND al.credit = 0 AND al.debit > 0
              AND al.due_date IS NOT NULL
              AND (CURRENT_DATE - al.due_date) >= @reminderDays
            """;

        var rows = (await conn.QueryAsync<OverdueRow>(new CommandDefinition(
            sql, new { reminderDays = _options.ReminderDays }, cancellationToken: ct))).AsList();

        if (rows.Count == 0)
        {
            _logger.LogInformation("AR overdue scan: nothing to notify");
            return;
        }

        var inserts = rows.Select(r => new InsertNotificationDto(
            TenantId: r.TenantId,
            UserId: r.AdminUserId,
            Kind: StageKind(r.DaysOverdue),
            RefType: "ar_invoice_overdue",
            RefId: r.LedgerId,
            Title: BuildTitle(r),
            Body: BuildBody(r))).ToList();

        var inserted = await _notifications.InsertManyAsync(inserts, ct);

        _logger.LogInformation(
            "AR overdue scan: scanned {Scanned} overdue invoice/admin pairs, inserted {Inserted} new notifications",
            rows.Count, inserted);
    }

    // Highest stage reached wins — an invoice 95 days overdue gets the
    // legal-escalation notification, not four separate ones.
    private string StageKind(int daysOverdue) => daysOverdue switch
    {
        _ when daysOverdue >= _options.LegalDays => "ar_overdue_legal",
        _ when daysOverdue >= _options.PenaltyDays => "ar_overdue_penalty",
        _ when daysOverdue >= _options.NoticeDays => "ar_overdue_notice",
        _ => "ar_overdue_reminder1",
    };

    private string BuildTitle(OverdueRow r) => StageKind(r.DaysOverdue) switch
    {
        "ar_overdue_legal" => $"Chuyển pháp chế: {r.CustomerName} quá hạn {r.DaysOverdue} ngày",
        "ar_overdue_penalty" => $"Tính lãi phạt: {r.CustomerName} quá hạn {r.DaysOverdue} ngày",
        "ar_overdue_notice" => $"Công văn nhắc nợ: {r.CustomerName} quá hạn {r.DaysOverdue} ngày",
        _ => $"Nhắc nợ lần 1: {r.CustomerName} quá hạn {r.DaysOverdue} ngày",
    };

    private static string BuildBody(OverdueRow r) =>
        $"Hoá đơn {r.DocumentNumber} — {r.Amount:N0} — hạn {r.DueDate:yyyy-MM-dd}";

    private sealed record OverdueRow(
        Guid LedgerId,
        string DocumentNumber,
        decimal Amount,
        DateOnly DueDate,
        string CustomerName,
        Guid TenantId,
        Guid AdminUserId,
        int DaysOverdue);
}
