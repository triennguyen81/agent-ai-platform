using Dapper;

namespace Aiq.Gateway.Data;

public sealed record DepartmentRecord(
    Guid Id,
    Guid TenantId,
    string Code,
    string Name,
    string Status,
    bool IsDefault);

public interface IDepartmentRepository
{
    Task<DepartmentRecord?> FindAsync(Guid tenantId, Guid departmentId, CancellationToken ct);
    Task<IReadOnlyList<DepartmentRecord>> ListActiveAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// D5: TENANT_OWNER creates a department in their own tenant. tenantId
    /// must come from server-side auth context, never the request body —
    /// callers never get to set is_default (always false here; only tenant
    /// onboarding sets it true, exactly once) or any ownership field.
    /// Throws PostgresException 23505 on a duplicate code/name within the
    /// tenant (uq_departments_tenant_code / uq_departments_tenant_name).
    /// </summary>
    Task<DepartmentRecord> CreateAsync(Guid tenantId, string code, string name, CancellationToken ct);

    /// <summary>D5: rename only — code and is_default are immutable after creation.</summary>
    Task<DepartmentRecord?> UpdateNameAsync(Guid tenantId, Guid departmentId, string name, CancellationToken ct);

    /// <summary>
    /// D5: soft lifecycle flip (active/inactive). Callers must run
    /// FindDeactivationBlockerAsync first when deactivating — this method
    /// itself only performs the flip (plus the DB's own
    /// departments_default_is_active CHECK, which permanently blocks
    /// deactivating the tenant's default department regardless of members,
    /// surfacing as PostgresException 23514).
    /// </summary>
    Task<DepartmentRecord?> SetStatusAsync(Guid tenantId, Guid departmentId, string status, CancellationToken ct);

    /// <summary>
    /// D5 §5: returns null if the department is safe to deactivate, or one
    /// blocker reason otherwise — checked in a fixed, most-actionable-first
    /// order (members, then agent entitlements, then capability bindings)
    /// so a single 409 always names something the caller can actually go
    /// fix next.
    /// </summary>
    Task<string?> FindDeactivationBlockerAsync(Guid tenantId, Guid departmentId, CancellationToken ct);
}

public sealed class DepartmentRepository(NpgsqlConnectionFactory factory) : IDepartmentRepository
{
    public async Task<DepartmentRecord?> FindAsync(Guid tenantId, Guid departmentId, CancellationToken ct)
    {
        const string sql = """
            SELECT id, tenant_id AS TenantId, code, name, status, is_default AS IsDefault
            FROM departments
            WHERE id = @departmentId AND tenant_id = @tenantId
            LIMIT 1;
            """;
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<DepartmentRecord>(
            new CommandDefinition(sql, new { tenantId, departmentId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<DepartmentRecord>> ListActiveAsync(Guid tenantId, CancellationToken ct)
    {
        const string sql = """
            SELECT id, tenant_id AS TenantId, code, name, status, is_default AS IsDefault
            FROM departments
            WHERE tenant_id = @tenantId AND status = 'active'
            ORDER BY name, id;
            """;
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryAsync<DepartmentRecord>(
            new CommandDefinition(sql, new { tenantId }, cancellationToken: ct))).AsList();
    }

    public async Task<DepartmentRecord> CreateAsync(Guid tenantId, string code, string name, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO departments(tenant_id, code, name, status, is_default)
            VALUES (@tenantId, @code, @name, 'active', false)
            RETURNING id, tenant_id AS TenantId, code, name, status, is_default AS IsDefault;
            """;
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QuerySingleAsync<DepartmentRecord>(
            new CommandDefinition(sql, new { tenantId, code, name }, cancellationToken: ct));
    }

    public async Task<DepartmentRecord?> UpdateNameAsync(Guid tenantId, Guid departmentId, string name, CancellationToken ct)
    {
        const string sql = """
            UPDATE departments SET name = @name, updated_at = NOW()
            WHERE id = @departmentId AND tenant_id = @tenantId
            RETURNING id, tenant_id AS TenantId, code, name, status, is_default AS IsDefault;
            """;
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<DepartmentRecord>(
            new CommandDefinition(sql, new { tenantId, departmentId, name }, cancellationToken: ct));
    }

    public async Task<DepartmentRecord?> SetStatusAsync(Guid tenantId, Guid departmentId, string status, CancellationToken ct)
    {
        const string sql = """
            UPDATE departments SET status = @status, updated_at = NOW()
            WHERE id = @departmentId AND tenant_id = @tenantId
            RETURNING id, tenant_id AS TenantId, code, name, status, is_default AS IsDefault;
            """;
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<DepartmentRecord>(
            new CommandDefinition(sql, new { tenantId, departmentId, status }, cancellationToken: ct));
    }

    public async Task<string?> FindDeactivationBlockerAsync(Guid tenantId, Guid departmentId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var hasMembers = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM users WHERE tenant_id=@tenantId AND department_id=@departmentId AND is_active)
            """, new { tenantId, departmentId }, cancellationToken: ct));
        if (hasMembers) return "active_members_remain";

        var hasAgentEntitlements = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM agent_department_assignments WHERE tenant_id=@tenantId AND department_id=@departmentId AND status='active')
            """, new { tenantId, departmentId }, cancellationToken: ct));
        if (hasAgentEntitlements) return "active_agent_entitlements_remain";

        var hasCapabilityBindings = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM department_capability_bindings WHERE tenant_id=@tenantId AND department_id=@departmentId AND status='active')
            """, new { tenantId, departmentId }, cancellationToken: ct));
        if (hasCapabilityBindings) return "active_capability_bindings_remain";

        return null;
    }
}
