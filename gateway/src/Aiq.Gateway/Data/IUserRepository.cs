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
    Task<bool>        SetActiveAsync(Guid userId, bool isActive, CancellationToken ct);
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
    string[] Roles);

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
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task SetTenantAsync(Guid userId, Guid? tenantId, CancellationToken ct)
    {
        const string sql = "UPDATE users SET tenant_id = @tenantId, updated_at = NOW() WHERE id = @userId";
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { userId, tenantId }, cancellationToken: ct));
    }

    public async Task<bool> SetActiveAsync(Guid userId, bool isActive, CancellationToken ct)
    {
        const string sql = "UPDATE users SET is_active = @isActive, updated_at = NOW() WHERE id = @userId";
        await using var conn = await _factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(sql, new { userId, isActive }, cancellationToken: ct));
        return n > 0;
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
        // role='user' only — mirrors the guard AssignUserAsync already
        // enforces at write time, so the picker never offers an admin/other
        // principal that a subsequent assign call would silently reject.
        const string sql = """
            SELECT
                u.id              AS Id,
                u.email           AS Email,
                u.display_name    AS DisplayName,
                u.is_active       AS IsActive,
                u.created_at      AS CreatedAt,
                u.external_provider AS ExternalProvider,
                ARRAY['user']::TEXT[] AS Roles
            FROM users u
            JOIN user_roles ur ON ur.user_id = u.id
            JOIN roles r       ON r.id = ur.role_id AND r.name = 'user'
            WHERE u.tenant_id = @tenantId AND u.is_active
            ORDER BY u.email;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<UserWithRoles>(
            new CommandDefinition(sql, new { tenantId }, cancellationToken: ct));
        return rows.ToArray();
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
                ARRAY['admin']::TEXT[] AS Roles
            FROM users u
            JOIN user_roles ur ON ur.user_id = u.id
            JOIN roles r       ON r.id = ur.role_id AND r.name = 'admin'
            WHERE u.tenant_id = @tenantId
            ORDER BY u.email;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<UserWithRoles>(
            new CommandDefinition(sql, new { tenantId }, cancellationToken: ct));
        return rows.ToArray();
    }
}
