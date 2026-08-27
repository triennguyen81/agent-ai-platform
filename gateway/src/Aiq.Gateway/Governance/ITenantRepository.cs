using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Governance;

public sealed record TenantRecord(
    Guid Id,
    string Code,
    string Name,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int? MaxUsers,
    int? MaxAgents);

public sealed record TenantQuotaStatus(
    Guid TenantId,
    string Code,
    string Name,
    string Status,
    int? MaxUsers,
    int CurrentUsers,
    int? MaxAgents,
    int CurrentAgents);

public sealed record UpdateTenantQuotaRequest(
    [Range(0, int.MaxValue)] int? MaxUsers,
    [Range(0, int.MaxValue)] int? MaxAgents);

/// <summary>
/// Pure comparison so the "is this tenant at capacity" rule is unit-testable
/// without a database. NULL limit means unlimited.
/// </summary>
public static class TenantQuotaPolicy
{
    public static bool HasRoomFor(int? max, int current) => max is null || current < max;
}

public sealed record TenantMembershipRecord(
    Guid TenantId,
    string TenantCode,
    string TenantName,
    Guid UserId,
    string Role,
    DateTime JoinedAt);

public sealed record CreateTenantRequest(
    [Required, RegularExpression("^[a-z0-9-]+$"), StringLength(60, MinimumLength = 2)] string Code,
    [Required, StringLength(120)] string Name);

public sealed record InviteMemberRequest(
    [Required] Guid UserId,
    [Required, RegularExpression("^(tenant_admin|member|viewer)$")] string Role);

public interface ITenantRepository
{
    Task<IReadOnlyList<TenantRecord>> ListAllAsync(CancellationToken ct);
    Task<TenantRecord?> FindByIdAsync(Guid id, CancellationToken ct);
    Task<TenantRecord?> FindByCodeAsync(string code, CancellationToken ct);
    Task<TenantRecord> CreateAsync(CreateTenantRequest request, CancellationToken ct);
    Task<IReadOnlyList<TenantMembershipRecord>> ListMembershipsForUserAsync(Guid userId, CancellationToken ct);
    Task<IReadOnlyList<TenantMembershipRecord>> ListMembersAsync(Guid tenantId, CancellationToken ct);
    Task<bool> UpsertMembershipAsync(Guid tenantId, Guid userId, string role, CancellationToken ct);
    Task<bool> RemoveMembershipAsync(Guid tenantId, Guid userId, CancellationToken ct);
    Task<string?> GetMemberRoleAsync(Guid tenantId, Guid userId, CancellationToken ct);

    /// <summary>
    /// Real, derived-at-read-time quota status for one tenant: current
    /// active-user and active-agent-assignment counts against the
    /// configured (nullable = unlimited) caps. tenantId must come from
    /// server-side auth context for the tenant-scoped caller; SUPER_ADMIN
    /// may pass any tenant id.
    /// </summary>
    Task<TenantQuotaStatus?> GetQuotaStatusAsync(Guid tenantId, CancellationToken ct);
    Task<TenantRecord?> UpdateQuotaAsync(Guid tenantId, UpdateTenantQuotaRequest request, CancellationToken ct);
}

public sealed class TenantRepository : ITenantRepository
{
    private const string SelectColumns = """
            id          AS Id,
            code        AS Code,
            name        AS Name,
            status      AS Status,
            created_at  AS CreatedAt,
            updated_at  AS UpdatedAt,
            max_users   AS MaxUsers,
            max_agents  AS MaxAgents
        """;

    private readonly NpgsqlConnectionFactory _factory;

    public TenantRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<TenantRecord>> ListAllAsync(CancellationToken ct)
    {
        var sql = $"SELECT {SelectColumns} FROM tenants ORDER BY code";
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<TenantRecord>(
            new CommandDefinition(sql, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<TenantRecord?> FindByIdAsync(Guid id, CancellationToken ct)
    {
        var sql = $"SELECT {SelectColumns} FROM tenants WHERE id = @id";
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<TenantRecord>(
            new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<TenantRecord?> FindByCodeAsync(string code, CancellationToken ct)
    {
        var sql = $"SELECT {SelectColumns} FROM tenants WHERE code = @code";
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<TenantRecord>(
            new CommandDefinition(sql, new { code }, cancellationToken: ct));
    }

    public async Task<TenantRecord> CreateAsync(CreateTenantRequest request, CancellationToken ct)
    {
        var sql = $"""
            INSERT INTO tenants (code, name)
            VALUES (@Code, @Name)
            RETURNING {SelectColumns}
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleAsync<TenantRecord>(
            new CommandDefinition(sql, request, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<TenantMembershipRecord>> ListMembershipsForUserAsync(Guid userId, CancellationToken ct)
    {
        const string sql = """
            SELECT
                t.id   AS TenantId,
                t.code AS TenantCode,
                t.name AS TenantName,
                tm.user_id   AS UserId,
                tm.role      AS Role,
                tm.joined_at AS JoinedAt
            FROM tenant_members tm
            JOIN tenants t ON t.id = tm.tenant_id
            WHERE tm.user_id = @userId
              AND t.status = 'active'
            ORDER BY t.code
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<TenantMembershipRecord>(
            new CommandDefinition(sql, new { userId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<TenantMembershipRecord>> ListMembersAsync(Guid tenantId, CancellationToken ct)
    {
        const string sql = """
            SELECT
                t.id   AS TenantId,
                t.code AS TenantCode,
                t.name AS TenantName,
                tm.user_id   AS UserId,
                tm.role      AS Role,
                tm.joined_at AS JoinedAt
            FROM tenant_members tm
            JOIN tenants t ON t.id = tm.tenant_id
            WHERE tm.tenant_id = @tenantId
            ORDER BY tm.joined_at
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<TenantMembershipRecord>(
            new CommandDefinition(sql, new { tenantId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<bool> UpsertMembershipAsync(Guid tenantId, Guid userId, string role, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO tenant_members (tenant_id, user_id, role)
            VALUES (@tenantId, @userId, @role)
            ON CONFLICT (tenant_id, user_id) DO UPDATE SET role = EXCLUDED.role
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(
            new CommandDefinition(sql, new { tenantId, userId, role }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<bool> RemoveMembershipAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        const string sql = "DELETE FROM tenant_members WHERE tenant_id = @tenantId AND user_id = @userId";
        await using var conn = await _factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(
            new CommandDefinition(sql, new { tenantId, userId }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<string?> GetMemberRoleAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        const string sql = "SELECT role FROM tenant_members WHERE tenant_id = @tenantId AND user_id = @userId";
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(
            new CommandDefinition(sql, new { tenantId, userId }, cancellationToken: ct));
    }

    public async Task<TenantQuotaStatus?> GetQuotaStatusAsync(Guid tenantId, CancellationToken ct)
    {
        // Counts are derived live from the tables that already own the
        // truth (users.tenant_id + is_active; agent_tenant_assignments +
        // status='active') rather than cached, so nothing can drift.
        const string sql = """
            SELECT
                t.id         AS TenantId,
                t.code       AS Code,
                t.name       AS Name,
                t.status     AS Status,
                t.max_users  AS MaxUsers,
                (SELECT COUNT(*)::int FROM users u WHERE u.tenant_id = t.id AND u.is_active) AS CurrentUsers,
                t.max_agents AS MaxAgents,
                (SELECT COUNT(*)::int FROM agent_tenant_assignments a
                    WHERE a.tenant_id = t.id AND a.status = 'active') AS CurrentAgents
            FROM tenants t
            WHERE t.id = @tenantId
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<TenantQuotaStatus>(
            new CommandDefinition(sql, new { tenantId }, cancellationToken: ct));
    }

    public async Task<TenantRecord?> UpdateQuotaAsync(Guid tenantId, UpdateTenantQuotaRequest request, CancellationToken ct)
    {
        var sql = $"""
            UPDATE tenants
            SET max_users = @MaxUsers, max_agents = @MaxAgents, updated_at = NOW()
            WHERE id = @tenantId
            RETURNING {SelectColumns}
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<TenantRecord>(
            new CommandDefinition(sql, new { tenantId, request.MaxUsers, request.MaxAgents }, cancellationToken: ct));
    }
}
