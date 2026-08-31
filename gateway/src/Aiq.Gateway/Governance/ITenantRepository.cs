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

public sealed record TenantSummary(
    Guid Id,
    string Code,
    string Name,
    string Status,
    DateTime CreatedAt,
    Guid? OwnerUserId,
    string? OwnerEmail,
    string? OwnerDisplayName,
    bool? OwnerIsActive,
    int AdminCount,
    int UserCount,
    int AgentCount);

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
    Task<(TenantRecord Tenant, Guid OwnerUserId)> OnboardAsync(
        CreateTenantRequest request, string ownerEmail, string ownerPasswordHash,
        string? ownerDisplayName, CancellationToken ct);
    Task<IReadOnlyList<TenantSummary>> ListSummariesAsync(CancellationToken ct);
    Task<TenantSummary?> GetSummaryAsync(Guid tenantId, CancellationToken ct);
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

    /// <summary>
    /// Stage 3 atomic ownership transfer: lock tenant → verify SUPER_ADMIN
    /// actor → validate successor → promote successor → demote/deactivate
    /// predecessor per explicit request → update owner_user_id → verify
    /// exactly one active owner → commit (audit is written by the caller
    /// after this returns, same as the Stage 2 assignment endpoint).
    /// Everything happens inside one transaction on one connection; any
    /// failure at any step rolls the whole thing back — no partial transfer
    /// is ever observable. The deferred constraint triggers from migration
    /// 018 re-verify the same invariant authoritatively at COMMIT, so this
    /// in-transaction check is defense in depth, not the only guard.
    /// </summary>
    Task<TenantRecord> TransferOwnershipAsync(
        Guid tenantId, Guid actorUserId, Guid successorUserId, string predecessorAction, CancellationToken ct);
}

public sealed class TenantOwnershipTransferException : InvalidOperationException
{
    public string Reason { get; }
    public TenantOwnershipTransferException(string reason) : base(reason) => Reason = reason;
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

    public async Task<(TenantRecord Tenant, Guid OwnerUserId)> OnboardAsync(
        CreateTenantRequest request,
        string ownerEmail,
        string ownerPasswordHash,
        string? ownerDisplayName,
        CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        TenantRecord tenant;
        Guid ownerUserId;
        try
        {
            tenant = await conn.QuerySingleAsync<TenantRecord>(new CommandDefinition($"""
                INSERT INTO tenants (code, name, status)
                VALUES (@Code, @Name, 'active')
                RETURNING {SelectColumns}
                """, request, tx, cancellationToken: ct));

            ownerUserId = await conn.QuerySingleAsync<Guid>(new CommandDefinition("""
                INSERT INTO users (email, password_hash, display_name, tenant_id, is_active)
                VALUES (@ownerEmail, @ownerPasswordHash, @ownerDisplayName, @tenantId, true)
                RETURNING id
                """, new { ownerEmail, ownerPasswordHash, ownerDisplayName, tenantId = tenant.Id }, tx, cancellationToken: ct));

            var roleInserted = await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO user_roles (user_id, role_id)
                SELECT @ownerUserId, id FROM roles WHERE name = 'tenant_owner'
                """, new { ownerUserId }, tx, cancellationToken: ct));
            if (roleInserted != 1) throw new InvalidOperationException("tenant_owner role is unavailable");

            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO tenant_members (tenant_id, user_id, role)
                VALUES (@tenantId, @ownerUserId, 'tenant_owner')
                ON CONFLICT (tenant_id, user_id) DO UPDATE SET role = EXCLUDED.role
                """, new { tenantId = tenant.Id, ownerUserId }, tx, cancellationToken: ct));

            var pointer = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT owner_user_id FROM tenants WHERE id = @tenantId",
                new { tenantId = tenant.Id }, tx, cancellationToken: ct));
            if (pointer != ownerUserId)
                throw new InvalidOperationException("tenant owner pointer was not established");
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        // Migration 018 performs the authoritative deferred invariant check
        // here. The tenant and its sole owner become visible together.
        await tx.CommitAsync(ct);
        return (tenant, ownerUserId);
    }

    private const string SummarySql = """
        SELECT
            t.id AS Id,
            t.code AS Code,
            t.name AS Name,
            t.status AS Status,
            t.created_at AS CreatedAt,
            owner.id AS OwnerUserId,
            owner.email AS OwnerEmail,
            owner.display_name AS OwnerDisplayName,
            owner.is_active AS OwnerIsActive,
            (SELECT COUNT(*)::int
             FROM users u JOIN user_roles ur ON ur.user_id = u.id
             JOIN roles r ON r.id = ur.role_id AND r.name = 'admin'
             WHERE u.tenant_id = t.id AND u.is_active) AS AdminCount,
            (SELECT COUNT(*)::int
             FROM users u JOIN user_roles ur ON ur.user_id = u.id
             JOIN roles r ON r.id = ur.role_id AND r.name = 'user'
             WHERE u.tenant_id = t.id AND u.is_active) AS UserCount,
            (SELECT COUNT(*)::int FROM agent_tenant_assignments ata
             WHERE ata.tenant_id = t.id AND ata.status = 'active') AS AgentCount
        FROM tenants t
        LEFT JOIN users owner ON owner.id = t.owner_user_id
        """;

    public async Task<IReadOnlyList<TenantSummary>> ListSummariesAsync(CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<TenantSummary>(new CommandDefinition(
            SummarySql + " ORDER BY t.code", cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<TenantSummary?> GetSummaryAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<TenantSummary>(new CommandDefinition(
            SummarySql + " WHERE t.id = @tenantId", new { tenantId }, cancellationToken: ct));
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

    private sealed record TenantLockRow(Guid Id, string Status, Guid? OwnerUserId);
    private sealed record SuccessorRow(Guid Id, Guid? TenantId, bool IsActive);

    public async Task<TenantRecord> TransferOwnershipAsync(
        Guid tenantId, Guid actorUserId, Guid successorUserId, string predecessorAction, CancellationToken ct)
    {
        if (predecessorAction is not ("demote" or "deactivate"))
            throw new TenantOwnershipTransferException("predecessor_action_must_be_demote_or_deactivate");

        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        TenantRecord updatedTenant;
        try
        {
            // 1. lock tenant — serializes concurrent transfers/assignments for
            // this tenant so no two transfers can interleave.
            var tenant = await conn.QuerySingleOrDefaultAsync<TenantLockRow>(new CommandDefinition(
                "SELECT id AS Id, status AS Status, owner_user_id AS OwnerUserId FROM tenants WHERE id = @tenantId FOR UPDATE",
                new { tenantId }, tx, cancellationToken: ct));
            if (tenant is null) throw new TenantOwnershipTransferException("tenant_not_found");
            if (tenant.Status != "active") throw new TenantOwnershipTransferException("tenant_not_active");

            // 2. verify SUPER_ADMIN actor — re-checked live against the DB
            // (not just the caller's JWT) as defense in depth: the actor's
            // role could have been revoked after the token was issued.
            var actorRole = await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                """
                SELECT r.name FROM user_roles ur
                JOIN roles r ON r.id = ur.role_id
                JOIN users u ON u.id = ur.user_id
                WHERE ur.user_id = @actorUserId AND u.is_active
                """, new { actorUserId }, tx, cancellationToken: ct));
            if (actorRole != "super_admin") throw new TenantOwnershipTransferException("actor_not_super_admin");

            // 3. validate successor
            var successor = await conn.QuerySingleOrDefaultAsync<SuccessorRow>(new CommandDefinition(
                "SELECT id AS Id, tenant_id AS TenantId, is_active AS IsActive FROM users WHERE id = @successorUserId",
                new { successorUserId }, tx, cancellationToken: ct));
            if (successor is null || successor.TenantId != tenantId)
                throw new TenantOwnershipTransferException("successor_must_belong_to_tenant");
            if (!successor.IsActive)
                throw new TenantOwnershipTransferException("successor_must_be_active");

            var predecessorId = tenant.OwnerUserId;
            if (predecessorId is null) throw new TenantOwnershipTransferException("tenant_has_no_current_owner");
            if (predecessorId == successorUserId) throw new TenantOwnershipTransferException("successor_is_already_owner");

            // 4. promote successor
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM user_roles WHERE user_id = @successorUserId", new { successorUserId }, tx, cancellationToken: ct));
            var promoted = await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO user_roles (user_id, role_id) SELECT @successorUserId, id FROM roles WHERE name = 'tenant_owner'",
                new { successorUserId }, tx, cancellationToken: ct));
            if (promoted != 1) throw new TenantOwnershipTransferException("promote_successor_failed");

            // 5. demote/deactivate predecessor according to explicit request
            if (predecessorAction == "demote")
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM user_roles WHERE user_id = @predecessorId", new { predecessorId }, tx, cancellationToken: ct));
                var demoted = await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO user_roles (user_id, role_id) SELECT @predecessorId, id FROM roles WHERE name = 'admin'",
                    new { predecessorId }, tx, cancellationToken: ct));
                if (demoted != 1) throw new TenantOwnershipTransferException("demote_predecessor_failed");
            }
            else
            {
                var deactivated = await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE users SET is_active = false, updated_at = NOW() WHERE id = @predecessorId",
                    new { predecessorId }, tx, cancellationToken: ct));
                if (deactivated != 1) throw new TenantOwnershipTransferException("deactivate_predecessor_failed");
            }

            // 6. update owner_user_id — set explicitly here rather than relying
            // solely on the AFTER trigger from migration 018, so the pointer is
            // guaranteed correct even under the "deactivate" branch (which
            // leaves the predecessor still holding the tenant_owner role,
            // just inactive, so the user_roles-driven sync trigger never fires
            // for it).
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tenants SET owner_user_id = @successorUserId, updated_at = NOW() WHERE id = @tenantId",
                new { successorUserId, tenantId }, tx, cancellationToken: ct));

            // 7. verify exactly one active owner — in-transaction belt-and-
            // suspenders check; the deferred constraint triggers re-verify
            // this authoritatively at COMMIT regardless.
            var activeOwners = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                SELECT COUNT(*) FROM users u
                JOIN user_roles ur ON ur.user_id = u.id
                JOIN roles r ON r.id = ur.role_id AND r.name = 'tenant_owner'
                WHERE u.tenant_id = @tenantId AND u.is_active
                """, new { tenantId }, tx, cancellationToken: ct));
            if (activeOwners != 1)
                throw new TenantOwnershipTransferException($"post_transfer_invariant_violated_owner_count_{activeOwners}");

            updatedTenant = await conn.QuerySingleAsync<TenantRecord>(new CommandDefinition(
                $"SELECT {SelectColumns} FROM tenants WHERE id = @tenantId", new { tenantId }, tx, cancellationToken: ct));
        }
        catch
        {
            // Only reachable for a failure before commit was attempted — the
            // transaction is still active here, so an explicit rollback is
            // both safe and necessary.
            await tx.RollbackAsync(ct);
            throw;
        }
        // 8. commit — deliberately outside the try/rollback above: with the
        // deferred constraint triggers from migration 018, an invariant
        // violation can surface exactly HERE, at commit, even after the
        // in-transaction check in step 7 passed (e.g. a concurrent
        // transaction). When CommitAsync itself throws, Npgsql has already
        // aborted the transaction server-side, and calling RollbackAsync
        // again on an already-completed transaction throws its own
        // InvalidOperationException that would mask the real
        // PostgresException the caller needs to see and translate
        // (23505/23514 -> 409).
        await tx.CommitAsync(ct);
        return updatedTenant;
    }
}
