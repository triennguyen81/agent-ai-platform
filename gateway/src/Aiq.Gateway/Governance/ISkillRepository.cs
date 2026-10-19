using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Governance;

/// <summary>
/// ACL V2-E: Department Skill ownership. Skills are declarative
/// instruction/prompt-fragment text — never executable code, never a
/// filesystem/binary implementation (migration 024's
/// skills_scope_ownership_valid CHECK enforces `source_kind='declarative'
/// AND declarative_content IS NOT NULL` for every scope='department' row).
/// A platform (scope='platform', source_kind='filesystem') row is a
/// read-only compatibility catalog entry for an existing SKILL.md package —
/// ADMIN may consume it, never edit it.
/// </summary>
public sealed record SkillRecord(
    Guid Id, string Code, string Name, string? Description, string Scope,
    bool IsActive, string? DeclarativeContent, Guid? CreatedBy, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record CreateDepartmentSkillRequest(
    // Matches agent_version_skill_assignments.skill_name's existing CHECK
    // constraint (migration 010) exactly — a Department Skill's code must be
    // insertable into that same immutable join table.
    [property: Required, RegularExpression("^[a-z0-9]+(?:-[a-z0-9]+)*$"), StringLength(120, MinimumLength = 2)] string Code,
    [property: Required, StringLength(160)] string Name,
    [property: StringLength(2000)] string? Description,
    [property: Required, StringLength(20000, MinimumLength = 1)] string DeclarativeContent);

public sealed record UpdateDepartmentSkillMetadataRequest(
    [property: StringLength(160)] string? Name,
    [property: StringLength(2000)] string? Description);

public sealed record UpdateDepartmentSkillContentRequest(
    [property: Required, StringLength(20000, MinimumLength = 1)] string DeclarativeContent);

public interface ISkillRepository
{
    Task<IReadOnlyList<SkillRecord>> ListPlatformSkillsAsync(CancellationToken ct);
    Task<SkillRecord> CreatePlatformSkillAsync(Guid actorId, CreateDepartmentSkillRequest request, CancellationToken ct);
    Task<SkillRecord?> UpdatePlatformSkillMetadataAsync(Guid skillId, UpdateDepartmentSkillMetadataRequest request, CancellationToken ct);
    Task<SkillRecord?> UpdatePlatformSkillContentAsync(Guid skillId, UpdateDepartmentSkillContentRequest request, CancellationToken ct);
    Task<SkillRecord?> SetPlatformSkillEnabledAsync(Guid skillId, bool enabled, CancellationToken ct);
    /// <summary>Unified ADMIN list: platform plus the authenticated department. Mutation methods remain department-owned only.</summary>
    Task<IReadOnlyList<SkillRecord>> ListOwnDepartmentSkillsAsync(Guid tenantId, Guid departmentId, CancellationToken ct);
    Task<SkillRecord?> FindOwnDepartmentSkillAsync(Guid tenantId, Guid departmentId, Guid skillId, CancellationToken ct);
    Task<SkillRecord> CreateDepartmentSkillAsync(Guid tenantId, Guid departmentId, Guid actorId, CreateDepartmentSkillRequest request, CancellationToken ct);
    Task<SkillRecord?> UpdateDepartmentSkillMetadataAsync(Guid tenantId, Guid departmentId, Guid skillId, UpdateDepartmentSkillMetadataRequest request, CancellationToken ct);

    /// <summary>
    /// Edits declarative content. Published Agent versions are unaffected:
    /// each carries its own publish-time snapshot (agent_version_skill_snapshots),
    /// so the edit reaches only drafts and versions published afterwards.
    /// </summary>
    Task<SkillRecord?> UpdateDepartmentSkillContentAsync(Guid tenantId, Guid departmentId, Guid skillId, UpdateDepartmentSkillContentRequest request, CancellationToken ct);
    Task<SkillRecord?> SetDepartmentSkillEnabledAsync(Guid tenantId, Guid departmentId, Guid skillId, bool enabled, CancellationToken ct);
}

public sealed class SkillRepository : ISkillRepository
{
    private const string Columns = """
        id AS Id, code AS Code, name AS Name, description AS Description, scope AS Scope,
        is_active AS IsActive, declarative_content AS DeclarativeContent,
        created_by AS CreatedBy, created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    private const string OwnDepartmentSkillWhere =
        "scope='department' AND tenant_id=@tenantId AND department_id=@departmentId";
    private const string VisibleDepartmentSkillWhere =
        "(scope='platform' OR (scope='department' AND tenant_id=@tenantId AND department_id=@departmentId))";

    private readonly NpgsqlConnectionFactory _factory;
    public SkillRepository(NpgsqlConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<SkillRecord>> ListPlatformSkillsAsync(CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<SkillRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM skills WHERE scope='platform' ORDER BY name", cancellationToken: ct))).AsList();
    }

    public async Task<SkillRecord> CreatePlatformSkillAsync(Guid actorId, CreateDepartmentSkillRequest request, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleAsync<SkillRecord>(new CommandDefinition($"""
            INSERT INTO skills(code,name,description,scope,tenant_id,department_id,created_by,source_kind,declarative_content)
            VALUES (@Code,@Name,@Description,'platform',NULL,NULL,@actorId,'declarative',@DeclarativeContent)
            RETURNING {Columns}
            """, new { request.Code, request.Name, request.Description, request.DeclarativeContent, actorId }, cancellationToken: ct));
    }

    public async Task<SkillRecord?> UpdatePlatformSkillMetadataAsync(Guid skillId, UpdateDepartmentSkillMetadataRequest request, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<SkillRecord>(new CommandDefinition($"""
            UPDATE skills SET name=COALESCE(@Name,name),description=COALESCE(@Description,description),updated_at=NOW()
            WHERE id=@skillId AND scope='platform' RETURNING {Columns}
            """, new { skillId, request.Name, request.Description }, cancellationToken: ct));
    }

    public async Task<SkillRecord?> UpdatePlatformSkillContentAsync(Guid skillId, UpdateDepartmentSkillContentRequest request, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        // No published-reference lock: published Agent versions run their
        // own publish-time snapshot (SkillSnapshotStore), so an edit here only
        // reaches drafts and versions published afterwards.
        return await conn.QuerySingleOrDefaultAsync<SkillRecord>(new CommandDefinition($"""
            UPDATE skills SET declarative_content=@DeclarativeContent,updated_at=NOW()
            WHERE id=@skillId AND scope='platform' AND source_kind='declarative' RETURNING {Columns}
            """, new { skillId, request.DeclarativeContent }, cancellationToken: ct));
    }

    public async Task<SkillRecord?> SetPlatformSkillEnabledAsync(Guid skillId, bool enabled, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<SkillRecord>(new CommandDefinition(
            $"UPDATE skills SET is_active=@enabled,updated_at=NOW() WHERE id=@skillId AND scope='platform' RETURNING {Columns}",
            new { skillId, enabled }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<SkillRecord>> ListOwnDepartmentSkillsAsync(Guid tenantId, Guid departmentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<SkillRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM skills WHERE {VisibleDepartmentSkillWhere} ORDER BY scope, name",
            new { tenantId, departmentId }, cancellationToken: ct))).AsList();
    }

    public async Task<SkillRecord?> FindOwnDepartmentSkillAsync(Guid tenantId, Guid departmentId, Guid skillId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<SkillRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM skills WHERE id=@skillId AND {OwnDepartmentSkillWhere}",
            new { skillId, tenantId, departmentId }, cancellationToken: ct));
    }

    public async Task<SkillRecord> CreateDepartmentSkillAsync(Guid tenantId, Guid departmentId, Guid actorId, CreateDepartmentSkillRequest request, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleAsync<SkillRecord>(new CommandDefinition($"""
            INSERT INTO skills (code, name, description, scope, tenant_id, department_id, created_by, source_kind, declarative_content)
            VALUES (@Code, @Name, @Description, 'department', @tenantId, @departmentId, @actorId, 'declarative', @DeclarativeContent)
            RETURNING {Columns}
            """, new { request.Code, request.Name, request.Description, tenantId, departmentId, actorId, request.DeclarativeContent },
            cancellationToken: ct));
    }

    public async Task<SkillRecord?> UpdateDepartmentSkillMetadataAsync(Guid tenantId, Guid departmentId, Guid skillId, UpdateDepartmentSkillMetadataRequest request, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<SkillRecord>(new CommandDefinition($"""
            UPDATE skills SET
                name = COALESCE(@Name, name),
                description = COALESCE(@Description, description),
                updated_at = NOW()
            WHERE id=@skillId AND {OwnDepartmentSkillWhere}
            RETURNING {Columns}
            """, new { skillId, tenantId, departmentId, request.Name, request.Description }, cancellationToken: ct));
    }

    public async Task<SkillRecord?> UpdateDepartmentSkillContentAsync(Guid tenantId, Guid departmentId, Guid skillId, UpdateDepartmentSkillContentRequest request, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var owned = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"SELECT EXISTS(SELECT 1 FROM skills WHERE id=@skillId AND {OwnDepartmentSkillWhere})",
            new { skillId, tenantId, departmentId }, cancellationToken: ct));
        if (!owned) return null;
        // No published-reference lock: published Agent versions run their
        // own publish-time snapshot (SkillSnapshotStore), so an edit here only
        // reaches drafts and versions published afterwards.
        return await conn.QuerySingleOrDefaultAsync<SkillRecord>(new CommandDefinition($"""
            UPDATE skills SET declarative_content = @DeclarativeContent, updated_at = NOW()
            WHERE id=@skillId AND {OwnDepartmentSkillWhere}
            RETURNING {Columns}
            """, new { skillId, tenantId, departmentId, request.DeclarativeContent }, cancellationToken: ct));
    }

    public async Task<SkillRecord?> SetDepartmentSkillEnabledAsync(Guid tenantId, Guid departmentId, Guid skillId, bool enabled, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<SkillRecord>(new CommandDefinition($"""
            UPDATE skills SET is_active=@enabled, updated_at=NOW()
            WHERE id=@skillId AND {OwnDepartmentSkillWhere}
            RETURNING {Columns}
            """, new { skillId, tenantId, departmentId, enabled }, cancellationToken: ct));
    }
}
