using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Dlp;

public sealed record DlpEvent(
    Guid TenantId,
    Guid? UserId,
    string? RequestId,
    string Direction,            // "scrub" | "unmask"
    int PiiCount,
    string[] PiiTypes,
    int TextLengthChars,
    string? Capability);

public sealed record DlpEventSummary(
    int TotalEvents,
    long TotalPiiDetected,
    IReadOnlyDictionary<string, int> TypeCounts,
    IReadOnlyList<DlpDailyBucket> DailyBuckets);

public sealed record DlpDailyBucket(DateOnly Date, int EventCount, int PiiCount);

public interface IDlpEventRepository
{
    Task LogAsync(DlpEvent evt, CancellationToken ct);

    /// <summary>
    /// Summary cho admin dashboard — N ngày gần nhất.
    /// </summary>
    Task<DlpEventSummary> GetSummaryAsync(Guid tenantId, int days, CancellationToken ct);
}

public sealed class DlpEventRepository : IDlpEventRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public DlpEventRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task LogAsync(DlpEvent evt, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO sim_dlp_events
              (tenant_id, user_id, request_id, direction, pii_count,
               pii_types, text_length_chars, capability)
            VALUES
              (@TenantId, @UserId, @RequestId, @Direction, @PiiCount,
               @PiiTypes, @TextLengthChars, @Capability);
            """;
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, evt, cancellationToken: ct));
    }

    public async Task<DlpEventSummary> GetSummaryAsync(Guid tenantId, int days, CancellationToken ct)
    {
        var fromDate = DateTime.UtcNow.AddDays(-days);
        await using var conn = await _factory.OpenAsync(ct);

        // 1. Totals
        var totals = await conn.QuerySingleAsync<(int totalEvents, long totalPii)>(
            new CommandDefinition("""
                SELECT COUNT(*)::int AS totalEvents,
                       COALESCE(SUM(pii_count), 0)::bigint AS totalPii
                FROM sim_dlp_events
                WHERE tenant_id = @tenantId
                  AND created_at >= @fromDate;
                """,
                new { tenantId, fromDate }, cancellationToken: ct));

        // 2. Type breakdown (unnest the array)
        var typeRows = await conn.QueryAsync<(string type, int count)>(
            new CommandDefinition("""
                SELECT unnest(pii_types) AS type,
                       COUNT(*)::int AS count
                FROM sim_dlp_events
                WHERE tenant_id = @tenantId
                  AND created_at >= @fromDate
                GROUP BY unnest(pii_types)
                ORDER BY count DESC;
                """,
                new { tenantId, fromDate }, cancellationToken: ct));
        var typeCounts = typeRows.ToDictionary(r => r.type, r => r.count);

        // 3. Daily buckets
        var dailyRows = await conn.QueryAsync<(DateTime day, int events, int pii)>(
            new CommandDefinition("""
                SELECT DATE_TRUNC('day', created_at) AS day,
                       COUNT(*)::int AS events,
                       COALESCE(SUM(pii_count), 0)::int AS pii
                FROM sim_dlp_events
                WHERE tenant_id = @tenantId
                  AND created_at >= @fromDate
                GROUP BY DATE_TRUNC('day', created_at)
                ORDER BY day;
                """,
                new { tenantId, fromDate }, cancellationToken: ct));
        var dailyBuckets = dailyRows
            .Select(r => new DlpDailyBucket(DateOnly.FromDateTime(r.day), r.events, r.pii))
            .ToList();

        return new DlpEventSummary(totals.totalEvents, totals.totalPii, typeCounts, dailyBuckets);
    }
}
