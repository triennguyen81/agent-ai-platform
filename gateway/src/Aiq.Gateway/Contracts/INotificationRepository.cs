using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Contracts;

public sealed record NotificationRecord(
    Guid Id,
    Guid TenantId,
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
        Guid tenantId, Guid userId,
        bool unreadOnly,
        int limit,
        CancellationToken ct);

    Task<int> UnreadCountAsync(Guid tenantId, Guid userId, CancellationToken ct);

    Task<bool> MarkReadAsync(Guid tenantId, Guid userId, Guid notificationId, CancellationToken ct);

    Task<int> MarkAllReadAsync(Guid tenantId, Guid userId, CancellationToken ct);

    /// <summary>
    /// Bulk-insert with ON CONFLICT DO NOTHING — safe for the daily cron to
    /// retry. Returns number of new rows actually inserted.
    /// </summary>
    Task<int> InsertManyAsync(IReadOnlyList<InsertNotificationDto> rows, CancellationToken ct);

    /// <summary>
    /// R18 — event-driven notification fan-out to every department_admin in
    /// one tenant+department (the closest existing-RBAC analog to "Kế toán
    /// trưởng"). Same dedup key as InsertManyAsync (tenant_id, user_id,
    /// kind, ref_id), so calling this twice for the same event is always
    /// safe. Returns number of admins actually notified (new rows only).
    /// </summary>
    Task<int> NotifyDepartmentAdminsAsync(
        Guid tenantId, Guid departmentId, string kind, string? refType, Guid? refId,
        string title, string? body, CancellationToken ct);
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
            tenant_id   AS TenantId,
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
        Guid tenantId, Guid userId, bool unreadOnly, int limit, CancellationToken ct)
    {
        var sql = unreadOnly
            ? $"""
              SELECT {SelectColumns}
              FROM notifications
              WHERE tenant_id = @tenantId AND user_id = @userId AND read_at IS NULL
              ORDER BY created_at DESC
              LIMIT @limit
              """
            : $"""
              SELECT {SelectColumns}
              FROM notifications
              WHERE tenant_id = @tenantId AND user_id = @userId
              ORDER BY created_at DESC
              LIMIT @limit
              """;

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<NotificationRecord>(
            new CommandDefinition(sql, new { tenantId, userId, limit = Math.Clamp(limit, 1, 200) }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<int> UnreadCountAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        const string sql = "SELECT COUNT(*)::int FROM notifications WHERE tenant_id = @tenantId AND user_id = @userId AND read_at IS NULL";
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, new { tenantId, userId }, cancellationToken: ct));
    }

    public async Task<bool> MarkReadAsync(Guid tenantId, Guid userId, Guid notificationId, CancellationToken ct)
    {
        const string sql = """
            UPDATE notifications
            SET read_at = NOW()
            WHERE id = @notificationId AND tenant_id = @tenantId AND user_id = @userId AND read_at IS NULL
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(
            new CommandDefinition(sql, new { tenantId, userId, notificationId }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<int> MarkAllReadAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        const string sql = """
            UPDATE notifications
            SET read_at = NOW()
            WHERE tenant_id = @tenantId AND user_id = @userId AND read_at IS NULL
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync(
            new CommandDefinition(sql, new { tenantId, userId }, cancellationToken: ct));
    }

    public async Task<int> InsertManyAsync(IReadOnlyList<InsertNotificationDto> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;

        const string sql = """
            INSERT INTO notifications (tenant_id, user_id, kind, ref_type, ref_id, title, body)
            VALUES (@TenantId, @UserId, @Kind, @RefType, @RefId, @Title, @Body)
            ON CONFLICT (tenant_id, user_id, kind, ref_id) WHERE ref_id IS NOT NULL DO NOTHING
            """;

        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync(
            new CommandDefinition(sql, rows, cancellationToken: ct));
    }

    public async Task<int> NotifyDepartmentAdminsAsync(
        Guid tenantId, Guid departmentId, string kind, string? refType, Guid? refId,
        string title, string? body, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var adminIds = (await conn.QueryAsync<Guid>(new CommandDefinition("""
            SELECT u.id
            FROM users u
            JOIN user_roles ur ON ur.user_id = u.id
            JOIN roles r ON r.id = ur.role_id AND r.name = 'admin'
            WHERE u.tenant_id = @tenantId AND u.department_id = @departmentId
            """, new { tenantId, departmentId }, cancellationToken: ct))).AsList();

        if (adminIds.Count == 0) return 0;

        var rows = adminIds.Select(adminId => new InsertNotificationDto(
            TenantId: tenantId, UserId: adminId, Kind: kind, RefType: refType, RefId: refId,
            Title: title, Body: body)).ToList();
        return await InsertManyAsync(rows, ct);
    }
}
