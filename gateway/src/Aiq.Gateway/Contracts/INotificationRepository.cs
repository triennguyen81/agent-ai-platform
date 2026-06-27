using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Contracts;

public sealed record NotificationRecord(
    Guid Id,
    Guid UserId,
    string Kind,
    string? RefType,
    Guid? RefId,
    string Title,
    string? Body,
    DateTime? ReadAt,
    DateTime CreatedAt);

public sealed record InsertNotificationDto(
    Guid TenantId,
    Guid UserId,
    string Kind,
    string? RefType,
    Guid? RefId,
    string Title,
    string? Body);

public interface INotificationRepository
{
    Task<IReadOnlyList<NotificationRecord>> ListForUserAsync(
        Guid userId,
        bool unreadOnly,
        int limit,
        CancellationToken ct);

    Task<int> UnreadCountAsync(Guid userId, CancellationToken ct);

    Task<bool> MarkReadAsync(Guid userId, Guid notificationId, CancellationToken ct);

    Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// Bulk-insert with ON CONFLICT DO NOTHING — safe for the daily cron to
    /// retry. Returns number of new rows actually inserted.
    /// </summary>
    Task<int> InsertManyAsync(IReadOnlyList<InsertNotificationDto> rows, CancellationToken ct);
}

public sealed class NotificationRepository : INotificationRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public NotificationRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    private const string SelectColumns = """
            id          AS Id,
            user_id     AS UserId,
            kind        AS Kind,
            ref_type    AS RefType,
            ref_id      AS RefId,
            title       AS Title,
            body        AS Body,
            read_at     AS ReadAt,
            created_at  AS CreatedAt
        """;

    public async Task<IReadOnlyList<NotificationRecord>> ListForUserAsync(
        Guid userId, bool unreadOnly, int limit, CancellationToken ct)
    {
        var sql = unreadOnly
            ? $"""
              SELECT {SelectColumns}
              FROM notifications
              WHERE user_id = @userId AND read_at IS NULL
              ORDER BY created_at DESC
              LIMIT @limit
              """
            : $"""
              SELECT {SelectColumns}
              FROM notifications
              WHERE user_id = @userId
              ORDER BY created_at DESC
              LIMIT @limit
              """;

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<NotificationRecord>(
            new CommandDefinition(sql, new { userId, limit = Math.Clamp(limit, 1, 200) }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<int> UnreadCountAsync(Guid userId, CancellationToken ct)
    {
        const string sql = "SELECT COUNT(*)::int FROM notifications WHERE user_id = @userId AND read_at IS NULL";
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, new { userId }, cancellationToken: ct));
    }

    public async Task<bool> MarkReadAsync(Guid userId, Guid notificationId, CancellationToken ct)
    {
        const string sql = """
            UPDATE notifications
            SET read_at = NOW()
            WHERE id = @notificationId AND user_id = @userId AND read_at IS NULL
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(
            new CommandDefinition(sql, new { userId, notificationId }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct)
    {
        const string sql = """
            UPDATE notifications
            SET read_at = NOW()
            WHERE user_id = @userId AND read_at IS NULL
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync(
            new CommandDefinition(sql, new { userId }, cancellationToken: ct));
    }

    public async Task<int> InsertManyAsync(IReadOnlyList<InsertNotificationDto> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;

        const string sql = """
            INSERT INTO notifications (tenant_id, user_id, kind, ref_type, ref_id, title, body)
            VALUES (@TenantId, @UserId, @Kind, @RefType, @RefId, @Title, @Body)
            ON CONFLICT (user_id, kind, ref_id) WHERE ref_id IS NOT NULL DO NOTHING
            """;

        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync(
            new CommandDefinition(sql, rows, cancellationToken: ct));
    }
}
