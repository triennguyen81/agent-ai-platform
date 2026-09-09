using Dapper;

namespace Aiq.Gateway.Data;

public interface IUserRepository
{
    Task<UserRecord?> FindByIdAsync(Guid id, CancellationToken ct);
    Task<UserRecord?> FindByEmailAsync(string email, CancellationToken ct);
    Task<UserRecord?> FindByExternalAsync(string provider, string externalId, CancellationToken ct);
    Task<UserRecord>  CreateAsync(string email, string passwordHash, string? displayName, CancellationToken ct);
    Task<UserRecord>  CreateExternalAsync(string email, string? displayName, string provider, string externalId, CancellationToken ct);
    Task              SetTenantAsync(Guid userId, Guid? tenantId, CancellationToken ct);
    Task              SetDepartmentAsync(Guid userId, Guid? departmentId, CancellationToken ct);
    Task<bool>        SetActiveAsync(Guid userId, bool isActive, CancellationToken ct);
    Task<bool>        UpdateTenantUserAsync(Guid tenantId, Guid userId, string? displayName, CancellationToken ct);
    Task<bool>        UpdateDepartmentUserAsync(Guid tenantId, Guid departmentId, Guid userId, string? displayName, CancellationToken ct);
    Task<bool>        SetDepartmentUserActiveAsync(Guid tenantId, Guid departmentId, Guid userId, bool isActive, CancellationToken ct);
    Task<UserWithRoles?> FindDepartmentUserAsync(Guid tenantId, Guid departmentId, Guid userId, CancellationToken ct);

    /// <summary>
    /// Same shape/semantics as UpdateTenantUserAsync, scoped to role='admin'
    /// instead of role='user' — kept as a separate narrow method (matching
    /// this repository's existing ListByTenantAsync/ListAdminsByTenantAsync
    /// split) rather than widening the shared /tenant/users edit endpoint,
    /// so a plain ADMIN caller (who also satisfies the "admin" policy) never
    /// gains the ability to edit a fellow ADMIN through that route. Only the
    /// TENANT_OWNER-only /tenant/admins/{id} route calls this.
    /// </summary>
    Task<bool>        UpdateTenantAdminAsync(Guid tenantId, Guid userId, string? displayName, CancellationToken ct);

    /// <summary>
    /// Narrow, atomic USER&lt;-&gt;ADMIN role flip for the TENANT_OWNER unified
    /// accounts screen. Deliberately NOT a general "set any role" method:
    /// requires the caller-supplied tenantId to match the target's own
    /// tenant, requires the target's CURRENT role to equal fromRole (so a
    /// race between the endpoint's read and this write cannot silently flip
    /// the wrong account), and only ever writes 'user' or 'admin' — callers
    /// must validate fromRole/toRole are in that set before calling. Returns
    /// false (no partial write) if the target/tenant/current-role guard
    /// doesn't match; never touches tenant_owner or super_admin rows because
    /// they never match a fromRole of 'user' or 'admin'.
    /// </summary>
    Task<bool>        SetTenantUserRoleAsync(Guid tenantId, Guid userId, string fromRole, string toRole, CancellationToken ct);

    /// <summary>
    /// D5 §9: TENANT_OWNER-only transactional department move for an
    /// ADMIN or USER. In one transaction: (1) moves the user, requiring the
    /// destination department to be active and in the SAME tenant as the
    /// caller-supplied tenantId (never trusts a client tenant id beyond
    /// that equality check — the actual scoping guarantee is that tenantId
    /// itself came from server-side auth context one layer up), (2) revokes
    /// (status flip, never deletes — history preserved) every active direct
    /// agent_assignments row belonging to this user whose agent is NOT
    /// actively entitled to the destination department, so a moved USER can
    /// never retain effective access to an old-department-only agent. Rows
    /// whose agent IS actively entitled to the destination department are
    /// left untouched. Returns false (no partial write) if the user/tenant
    /// doesn't match or the destination department isn't active in that
    /// tenant.
    /// </summary>
    Task<bool> MoveDepartmentAsync(Guid tenantId, Guid userId, Guid newDepartmentId, CancellationToken ct);

    Task              AssignRoleAsync(Guid userId, string roleName, CancellationToken ct);
    Task<bool>        RemoveRoleAsync(Guid userId, string roleName, CancellationToken ct);
    Task<IReadOnlyCollection<string>> GetRolesAsync(Guid userId, CancellationToken ct);
    Task<IReadOnlyList<UserWithRoles>> ListWithRolesAsync(CancellationToken ct);

    /// <summary>
    /// Active role='user' members of a single tenant — the only candidate
    /// pool a tenant ADMIN may assign agent execution entitlement to.
    /// tenantId must come from server-side auth context, never the browser.
    /// </summary>
    Task<IReadOnlyList<UserWithRoles>> ListByTenantAsync(Guid tenantId, CancellationToken ct);
    Task<IReadOnlyList<UserWithRoles>> ListByDepartmentAsync(Guid tenantId, Guid departmentId, CancellationToken ct);

    /// <summary>
    /// role='admin' members of a single tenant, active or disabled — powers
    /// the SUPER_ADMIN-only tenant-detail "Administrators" section. Includes
    /// disabled admins (unlike ListByTenantAsync) so SUPER_ADMIN can see and
    /// re-enable them.
    /// </summary>
    Task<IReadOnlyList<UserWithRoles>> ListAdminsByTenantAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Platform-wide read-only account directory for SUPER_ADMIN — includes
    /// which tenant (if any) each account belongs to, so the generic /users
    /// screen can show real tenant context instead of implying role changes
    /// happen here. A dedicated method/record so it can never accidentally
    /// share a query shape with the tenant-scoped assignment-candidate lists
    /// above.
    /// </summary>
    Task<IReadOnlyList<UserDirectoryEntry>> ListDirectoryAsync(CancellationToken ct);

    /// <summary>
    /// role='tenant_owner' members of a single tenant, active or disabled —
    /// powers the Stage 2 ownership preflight report and the Stage 3 atomic
    /// transfer. Same shape/semantics as ListAdminsByTenantAsync.
    /// </summary>
    Task<IReadOnlyList<UserWithRoles>> ListTenantOwnersAsync(Guid tenantId, CancellationToken ct);
}

public sealed record UserDirectoryEntry(
    Guid Id,
    string Email,
    string? DisplayName,
    bool IsActive,
    DateTime CreatedAt,
    string? ExternalProvider,
    string[] Roles,
    string? TenantCode,
    string? TenantName);

public sealed record UserWithRoles(
    Guid Id,
    string Email,
    string? DisplayName,
    bool IsActive,
    DateTime CreatedAt,
    string? ExternalProvider,
    string[] Roles,
    Guid? DepartmentId = null,
    string? DepartmentCode = null,
    string? DepartmentName = null);

public sealed class UserRepository : IUserRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public UserRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    private const string UserSelectColumns = """
            id,
            email,
            password_hash      AS PasswordHash,
            display_name       AS DisplayName,
            is_active          AS IsActive,
            created_at         AS CreatedAt,
            tenant_id          AS TenantId,
            department_id      AS DepartmentId,
            external_provider  AS ExternalProvider,
            external_id        AS ExternalId
        """;

    public async Task<UserRecord?> FindByIdAsync(Guid id, CancellationToken ct)
    {
        var sql = $"SELECT {UserSelectColumns} FROM users WHERE id = @id LIMIT 1;";
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<UserRecord>(
            new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<UserRecord?> FindByEmailAsync(string email, CancellationToken ct)
    {
        var sql = $"SELECT {UserSelectColumns} FROM users WHERE email = @email LIMIT 1;";
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<UserRecord>(
            new CommandDefinition(sql, new { email }, cancellationToken: ct));
    }

    public async Task<UserRecord?> FindByExternalAsync(string provider, string externalId, CancellationToken ct)
    {
        var sql = $"""
            SELECT {UserSelectColumns}
            FROM users
            WHERE external_provider = @provider AND external_id = @externalId
            LIMIT 1;
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<UserRecord>(
            new CommandDefinition(sql, new { provider, externalId }, cancellationToken: ct));
    }

    public async Task<UserRecord> CreateAsync(
        string email, string passwordHash, string? displayName, CancellationToken ct)
    {
        var sql = $"""
            INSERT INTO users (email, password_hash, display_name)
            VALUES (@email, @passwordHash, @displayName)
            RETURNING {UserSelectColumns};
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleAsync<UserRecord>(
            new CommandDefinition(sql, new { email, passwordHash, displayName },
                cancellationToken: ct));
    }

    public async Task<UserRecord> CreateExternalAsync(
        string email, string? displayName, string provider, string externalId, CancellationToken ct)
    {
        // Random placeholder password — never used because login goes through
        // /auth/external-link and never validates this hash.
        var randomHash = "$2a$12$" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var sql = $"""
            INSERT INTO users (email, password_hash, display_name, external_provider, external_id)
            VALUES (@email, @passwordHash, @displayName, @provider, @externalId)
            RETURNING {UserSelectColumns};
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleAsync<UserRecord>(
            new CommandDefinition(
                sql,
                new
                {
                    email,
                    passwordHash = randomHash,
                    displayName,
                    provider,
                    externalId,
                },
                cancellationToken: ct));
    }

    public async Task AssignRoleAsync(Guid userId, string roleName, CancellationToken ct)
    {
        if (!LoginRoleRules.IsAllowed(roleName))
        {
            throw new ArgumentOutOfRangeException(nameof(roleName), "Unsupported login role.");
        }
        const string deleteSql = """
            DELETE FROM user_roles WHERE user_id = @userId;
            """;
        const string insertSql = """
            INSERT INTO user_roles (user_id, role_id)
            SELECT @userId, r.id FROM roles r WHERE r.name = @roleName
            ;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                deleteSql, new { userId }, tx, cancellationToken: ct));
            var inserted = await conn.ExecuteAsync(new CommandDefinition(
                insertSql, new { userId, roleName }, tx, cancellationToken: ct));
            if (inserted != 1) throw new InvalidOperationException("Role does not exist.");
        }
        catch
        {
            // Only reachable for a failure BEFORE commit was attempted — the
            // transaction is still active here, so an explicit rollback is
            // both safe and necessary.
            await tx.RollbackAsync(ct);
            throw;
        }
        // Deliberately outside the try/rollback block: with the deferred
        // constraint triggers from migration 018, a deferred invariant
        // violation (e.g. "the sole active tenant_owner cannot be removed")
        // surfaces exactly HERE, at commit — and when CommitAsync itself
        // throws, Npgsql has already aborted the transaction server-side.
        // Calling RollbackAsync again on an already-completed transaction
        // throws its own InvalidOperationException, masking the original
        // PostgresException the caller needs to see (e.g. UserEndpoints'
        // 23505/23514 -> 409 translation). Letting CommitAsync's exception
        // propagate untouched — `await using var tx` disposal is safe on an
        // already-completed transaction — preserves that original error.
        await tx.CommitAsync(ct);
    }

    public async Task SetTenantAsync(Guid userId, Guid? tenantId, CancellationToken ct)
    {
        const string sql = "UPDATE users SET tenant_id = @tenantId, updated_at = NOW() WHERE id = @userId";
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { userId, tenantId }, cancellationToken: ct));
    }

    public async Task SetDepartmentAsync(Guid userId, Guid? departmentId, CancellationToken ct)
    {
        const string sql = """
            UPDATE users u
            SET department_id = @departmentId, updated_at = NOW()
            WHERE u.id = @userId
              AND (@departmentId IS NULL OR (
                  u.tenant_id IS NOT NULL AND EXISTS (SELECT 1 FROM departments d
                  WHERE d.id = @departmentId AND d.tenant_id = u.tenant_id AND d.status = 'active')));
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new { userId, departmentId }, cancellationToken: ct));
        if (rows != 1) throw new InvalidOperationException("Department is not active in the user's tenant.");
    }

    public async Task<bool> MoveDepartmentAsync(Guid tenantId, Guid userId, Guid newDepartmentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var moved = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE users u SET department_id = @newDepartmentId, updated_at = NOW()
            WHERE u.id = @userId AND u.tenant_id = @tenantId
              AND EXISTS (SELECT 1 FROM departments d
                  WHERE d.id = @newDepartmentId AND d.tenant_id = @tenantId AND d.status = 'active')
            """, new { tenantId, userId, newDepartmentId }, tx, cancellationToken: ct));
        if (moved != 1)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        // D5 §9/§10: retain a direct assignment only where the same agent is
        // actively entitled to the destination department — everything else
        // is revoked (status flip, never deleted) in the same transaction as
        // the move, so no intermediate state (new department_id + a stale
        // still-active old-department-only assignment) can ever be observed.
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE agent_assignments aa SET status='revoked', revoked_at=NOW()
            WHERE aa.tenant_id=@tenantId AND aa.user_id=@userId AND aa.status='active'
              AND NOT EXISTS (
                  SELECT 1 FROM agent_department_assignments ada
                  WHERE ada.agent_id=aa.agent_id AND ada.tenant_id=@tenantId
                    AND ada.department_id=@newDepartmentId AND ada.status='active'
              )
            """, new { tenantId, userId, newDepartmentId }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> SetActiveAsync(Guid userId, bool isActive, CancellationToken ct)
    {
        const string sql = "UPDATE users SET is_active = @isActive, updated_at = NOW() WHERE id = @userId";
        await using var conn = await _factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(sql, new { userId, isActive }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<bool> UpdateTenantUserAsync(
        Guid tenantId, Guid userId, string? displayName, CancellationToken ct)
    {
        const string sql = """
            UPDATE users u
            SET display_name = @displayName, updated_at = NOW()
            WHERE u.id = @userId
              AND u.tenant_id = @tenantId
              AND EXISTS (
                  SELECT 1
                  FROM user_roles ur
                  JOIN roles r ON r.id = ur.role_id AND r.name = 'user'
                  WHERE ur.user_id = u.id
              );
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            sql, new { tenantId, userId, displayName }, cancellationToken: ct));
        return rows == 1;
    }

    public async Task<bool> UpdateDepartmentUserAsync(
        Guid tenantId, Guid departmentId, Guid userId, string? displayName, CancellationToken ct)
    {
        const string sql = """
            UPDATE users u SET display_name = @displayName, updated_at = NOW()
            WHERE u.id = @userId AND u.tenant_id = @tenantId AND u.department_id = @departmentId
              AND EXISTS (SELECT 1 FROM user_roles ur JOIN roles r ON r.id=ur.role_id
                          WHERE ur.user_id=u.id AND r.name='user');
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql,
            new { tenantId, departmentId, userId, displayName }, cancellationToken: ct)) == 1;
    }

    public async Task<bool> SetDepartmentUserActiveAsync(
        Guid tenantId, Guid departmentId, Guid userId, bool isActive, CancellationToken ct)
    {
        const string sql = """
            UPDATE users u SET is_active=@isActive, updated_at=NOW()
            WHERE u.id=@userId AND u.tenant_id=@tenantId AND u.department_id=@departmentId
              AND EXISTS (SELECT 1 FROM user_roles ur JOIN roles r ON r.id=ur.role_id
                          WHERE ur.user_id=u.id AND r.name='user');
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql,
            new { tenantId, departmentId, userId, isActive }, cancellationToken: ct)) == 1;
    }

    public async Task<UserWithRoles?> FindDepartmentUserAsync(
        Guid tenantId, Guid departmentId, Guid userId, CancellationToken ct)
    {
        const string sql = """
            SELECT u.id, u.email, u.display_name AS DisplayName, u.is_active AS IsActive,
                   u.created_at AS CreatedAt, u.external_provider AS ExternalProvider,
                   ARRAY['user']::TEXT[] AS Roles, u.department_id AS DepartmentId,
                   d.code AS DepartmentCode, d.name AS DepartmentName
            FROM users u
            JOIN departments d ON d.id=u.department_id AND d.tenant_id=u.tenant_id
            JOIN user_roles ur ON ur.user_id=u.id
            JOIN roles r ON r.id=ur.role_id AND r.name='user'
            WHERE u.id=@userId AND u.tenant_id=@tenantId AND u.department_id=@departmentId;
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<UserWithRoles>(new CommandDefinition(sql,
            new { tenantId, departmentId, userId }, cancellationToken: ct));
    }

    public async Task<bool> UpdateTenantAdminAsync(
        Guid tenantId, Guid userId, string? displayName, CancellationToken ct)
    {
        const string sql = """
            UPDATE users u
            SET display_name = @displayName, updated_at = NOW()
            WHERE u.id = @userId
              AND u.tenant_id = @tenantId
              AND EXISTS (
                  SELECT 1
                  FROM user_roles ur
                  JOIN roles r ON r.id = ur.role_id AND r.name = 'admin'
                  WHERE ur.user_id = u.id
              );
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            sql, new { tenantId, userId, displayName }, cancellationToken: ct));
        return rows == 1;
    }

    public async Task<bool> SetTenantUserRoleAsync(
        Guid tenantId, Guid userId, string fromRole, string toRole, CancellationToken ct)
    {
        const string deleteSql = """
            DELETE FROM user_roles
            WHERE user_id = @userId
              AND EXISTS (SELECT 1 FROM users u WHERE u.id = @userId AND u.tenant_id = @tenantId)
              AND EXISTS (
                  SELECT 1 FROM user_roles ur JOIN roles r ON r.id = ur.role_id
                  WHERE ur.user_id = @userId AND r.name = @fromRole
              );
            """;
        const string insertSql = """
            INSERT INTO user_roles (user_id, role_id)
            SELECT @userId, r.id FROM roles r WHERE r.name = @toRole;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        bool ok;
        try
        {
            var deleted = await conn.ExecuteAsync(new CommandDefinition(
                deleteSql, new { userId, tenantId, fromRole }, tx, cancellationToken: ct));
            if (deleted != 1)
            {
                await tx.RollbackAsync(ct);
                return false;
            }
            var inserted = await conn.ExecuteAsync(new CommandDefinition(
                insertSql, new { userId, toRole }, tx, cancellationToken: ct));
            ok = inserted == 1;
            if (!ok)
            {
                await tx.RollbackAsync(ct);
                return false;
            }
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
        // Outside the catch/rollback for the same reason as AssignRoleAsync
        // above: if CommitAsync itself throws, the transaction is already
        // aborted server-side, and a second RollbackAsync would mask the
        // real PostgresException the caller needs to translate.
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> RemoveRoleAsync(Guid userId, string roleName, CancellationToken ct)
    {
        if (!LoginRoleRules.IsAllowed(roleName)) return false;
        const string sql = """
            DELETE FROM user_roles
            WHERE user_id = @userId
              AND role_id = (SELECT id FROM roles WHERE name = @roleName);
            """;

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(
            new CommandDefinition(sql, new { userId, roleName }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<IReadOnlyCollection<string>> GetRolesAsync(Guid userId, CancellationToken ct)
    {
        const string sql = """
            SELECT r.name
            FROM roles r
            INNER JOIN user_roles ur ON ur.role_id = r.id
            WHERE ur.user_id = @userId;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        var roles = await conn.QueryAsync<string>(
            new CommandDefinition(sql, new { userId }, cancellationToken: ct));
        return roles.ToArray();
    }

    public async Task<IReadOnlyList<UserWithRoles>> ListWithRolesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT
                u.id              AS Id,
                u.email           AS Email,
                u.display_name    AS DisplayName,
                u.is_active       AS IsActive,
                u.created_at      AS CreatedAt,
                u.external_provider AS ExternalProvider,
                COALESCE(
                    array_agg(r.name) FILTER (WHERE r.name IS NOT NULL),
                    ARRAY[]::TEXT[]
                ) AS Roles
            FROM users u
            LEFT JOIN user_roles ur ON ur.user_id = u.id
            LEFT JOIN roles r       ON r.id = ur.role_id
            GROUP BY u.id
            ORDER BY u.created_at DESC
            LIMIT 500;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<UserWithRoles>(
            new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToArray();
    }

    public async Task<IReadOnlyList<UserDirectoryEntry>> ListDirectoryAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT
                u.id              AS Id,
                u.email           AS Email,
                u.display_name    AS DisplayName,
                u.is_active       AS IsActive,
                u.created_at      AS CreatedAt,
                u.external_provider AS ExternalProvider,
                COALESCE(
                    array_agg(r.name) FILTER (WHERE r.name IS NOT NULL),
                    ARRAY[]::TEXT[]
                ) AS Roles,
                t.code AS TenantCode,
                t.name AS TenantName
            FROM users u
            LEFT JOIN user_roles ur ON ur.user_id = u.id
            LEFT JOIN roles r       ON r.id = ur.role_id
            LEFT JOIN tenants t     ON t.id = u.tenant_id
            GROUP BY u.id, t.code, t.name
            ORDER BY u.created_at DESC
            LIMIT 500;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<UserDirectoryEntry>(
            new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToArray();
    }

    public async Task<IReadOnlyList<UserWithRoles>> ListByTenantAsync(Guid tenantId, CancellationToken ct)
    {
        // role='user' only. Disabled users remain visible so tenant managers
        // can re-enable them; assignment UIs filter IsActive separately.
        const string sql = """
            SELECT
                u.id              AS Id,
                u.email           AS Email,
                u.display_name    AS DisplayName,
                u.is_active       AS IsActive,
                u.created_at      AS CreatedAt,
                u.external_provider AS ExternalProvider,
                ARRAY['user']::TEXT[] AS Roles,
                u.department_id AS DepartmentId,
                d.code AS DepartmentCode,
                d.name AS DepartmentName
            FROM users u
            JOIN user_roles ur ON ur.user_id = u.id
            JOIN roles r       ON r.id = ur.role_id AND r.name = 'user'
            LEFT JOIN departments d ON d.id=u.department_id AND d.tenant_id=u.tenant_id
            WHERE u.tenant_id = @tenantId
            ORDER BY u.email;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<UserWithRoles>(
            new CommandDefinition(sql, new { tenantId }, cancellationToken: ct));
        return rows.ToArray();
    }

    public async Task<IReadOnlyList<UserWithRoles>> ListByDepartmentAsync(
        Guid tenantId, Guid departmentId, CancellationToken ct)
    {
        const string sql = """
            SELECT u.id, u.email, u.display_name AS DisplayName, u.is_active AS IsActive,
                   u.created_at AS CreatedAt, u.external_provider AS ExternalProvider,
                   ARRAY['user']::TEXT[] AS Roles, u.department_id AS DepartmentId,
                   d.code AS DepartmentCode, d.name AS DepartmentName
            FROM users u
            JOIN departments d ON d.id=u.department_id AND d.tenant_id=u.tenant_id
            JOIN user_roles ur ON ur.user_id=u.id
            JOIN roles r ON r.id=ur.role_id AND r.name='user'
            WHERE u.tenant_id=@tenantId AND u.department_id=@departmentId
            ORDER BY u.email;
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<UserWithRoles>(new CommandDefinition(sql,
            new { tenantId, departmentId }, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<UserWithRoles>> ListAdminsByTenantAsync(Guid tenantId, CancellationToken ct)
    {
        const string sql = """
            SELECT
                u.id              AS Id,
                u.email           AS Email,
                u.display_name    AS DisplayName,
                u.is_active       AS IsActive,
                u.created_at      AS CreatedAt,
                u.external_provider AS ExternalProvider,
                ARRAY['admin']::TEXT[] AS Roles,
                u.department_id AS DepartmentId,
                d.code AS DepartmentCode,
                d.name AS DepartmentName
            FROM users u
            JOIN user_roles ur ON ur.user_id = u.id
            JOIN roles r       ON r.id = ur.role_id AND r.name = 'admin'
            LEFT JOIN departments d ON d.id=u.department_id AND d.tenant_id=u.tenant_id
            WHERE u.tenant_id = @tenantId
            ORDER BY u.email;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<UserWithRoles>(
            new CommandDefinition(sql, new { tenantId }, cancellationToken: ct));
        return rows.ToArray();
    }

    public async Task<IReadOnlyList<UserWithRoles>> ListTenantOwnersAsync(Guid tenantId, CancellationToken ct)
    {
        const string sql = """
            SELECT
                u.id              AS Id,
                u.email           AS Email,
                u.display_name    AS DisplayName,
                u.is_active       AS IsActive,
                u.created_at      AS CreatedAt,
                u.external_provider AS ExternalProvider,
                ARRAY['tenant_owner']::TEXT[] AS Roles
            FROM users u
            JOIN user_roles ur ON ur.user_id = u.id
            JOIN roles r       ON r.id = ur.role_id AND r.name = 'tenant_owner'
            WHERE u.tenant_id = @tenantId
            ORDER BY u.email;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<UserWithRoles>(
            new CommandDefinition(sql, new { tenantId }, cancellationToken: ct));
        return rows.ToArray();
    }
}
