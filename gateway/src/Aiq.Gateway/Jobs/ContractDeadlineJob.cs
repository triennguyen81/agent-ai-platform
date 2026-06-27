using Aiq.Gateway.Contracts;
using Aiq.Gateway.Data;
using Dapper;
using Hangfire;

namespace Aiq.Gateway.Jobs;

/// <summary>
/// Daily 07:00 VN — scans contract_checklist_items for items whose due_date
/// minus remind_days_before has arrived, and inserts notifications for the
/// contract owner. Idempotent via the unique (user_id, kind, ref_id) index
/// on notifications, so re-runs in the same day are no-ops.
/// </summary>
public sealed class ContractDeadlineJob
{
    public const string RecurringJobId = "contract-deadline-scan";

    private readonly NpgsqlConnectionFactory _factory;
    private readonly INotificationRepository _notifications;
    private readonly ILogger<ContractDeadlineJob> _logger;

    public ContractDeadlineJob(
        NpgsqlConnectionFactory factory,
        INotificationRepository notifications,
        ILogger<ContractDeadlineJob> logger)
    {
        _factory = factory;
        _notifications = notifications;
        _logger = logger;
    }

    [JobDisplayName("Contract deadline scan")]
    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task RunAsync(CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);

        // Pull both due-soon AND overdue open/in-progress items along with
        // the contract owner. We let SQL classify "overdue vs due_soon" via
        // a CASE so the application only has to translate it into a kind.
        const string sql = """
            SELECT
                ci.id            AS item_id,
                ci.contract_id   AS contract_id,
                ci.title         AS title,
                ci.due_date      AS due_date,
                ci.remind_days_before AS remind_days,
                c.user_id        AS user_id,
                c.tenant_id      AS tenant_id,
                c.title          AS contract_title,
                CASE
                    WHEN ci.due_date < CURRENT_DATE THEN 'checklist_overdue'
                    ELSE 'checklist_due_soon'
                END AS kind,
                (ci.due_date - CURRENT_DATE) AS days_remaining
            FROM contract_checklist_items ci
            JOIN contracts c ON c.id = ci.contract_id
            WHERE ci.status IN ('open', 'in_progress')
              AND ci.due_date IS NOT NULL
              AND ci.due_date - ci.remind_days_before <= CURRENT_DATE
            """;

        var rows = (await conn.QueryAsync<DeadlineRow>(
            new CommandDefinition(sql, cancellationToken: ct))).AsList();

        if (rows.Count == 0)
        {
            _logger.LogInformation("Contract deadline scan: nothing to notify");
            return;
        }

        var inserts = rows.Select(r => new InsertNotificationDto(
            TenantId: r.TenantId,
            UserId: r.UserId,
            Kind: r.Kind,
            RefType: "checklist_item",
            RefId: r.ItemId,
            Title: BuildTitle(r),
            Body: BuildBody(r))).ToList();

        var inserted = await _notifications.InsertManyAsync(inserts, ct);

        _logger.LogInformation(
            "Contract deadline scan: scanned {Scanned} items, inserted {Inserted} new notifications",
            rows.Count, inserted);
    }

    private static string BuildTitle(DeadlineRow r) =>
        r.Kind == "checklist_overdue"
            ? $"Quá hạn {-r.DaysRemaining} ngày: {r.Title}"
            : r.DaysRemaining == 0
                ? $"Đến hạn hôm nay: {r.Title}"
                : $"Còn {r.DaysRemaining} ngày: {r.Title}";

    private static string BuildBody(DeadlineRow r) =>
        $"Hợp đồng: {r.ContractTitle} — Hạn: {r.DueDate:yyyy-MM-dd}";

    private sealed record DeadlineRow(
        Guid ItemId,
        Guid ContractId,
        string Title,
        DateOnly DueDate,
        int RemindDays,
        Guid UserId,
        Guid TenantId,
        string ContractTitle,
        string Kind,
        int DaysRemaining);
}
