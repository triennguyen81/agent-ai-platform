using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Governance;

public sealed record WorkflowRecord(
    Guid Id, string Name, string? Description, string DefinitionJson, bool IsActive,
    string Scope, Guid? TenantId, Guid? DepartmentId,
    Guid? CurrentDraftVersionId, Guid? PublishedVersionId,
    Guid? CreatedBy, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record WorkflowVersionRecord(
    Guid Id, Guid WorkflowId, int Version, string Status, string DefinitionJson,
    Guid? CreatedBy, DateTime CreatedAt, Guid? PublishedBy, DateTime? PublishedAt);

public sealed record CreateWorkflowRequest(
    [property: Required, StringLength(160, MinimumLength = 2)] string Name,
    [property: StringLength(2000)] string? Description,
    JsonElement? Definition);

public sealed record UpdateWorkflowRequest(
    [property: StringLength(160, MinimumLength = 2)] string? Name,
    [property: StringLength(2000)] string? Description,
    JsonElement? Definition,
    bool? IsActive);

/// <summary>ACL V2-E: creates a Department Workflow head + its version-1 draft in one call. Definition is optional — an empty draft is valid; ADMIN fills it in via the draft-edit endpoint.</summary>
public sealed record CreateDepartmentWorkflowRequest(
    [property: Required, StringLength(160, MinimumLength = 2)] string Name,
    [property: StringLength(2000)] string? Description,
    JsonElement? Definition);

public sealed record UpdateDepartmentWorkflowRequest(
    [property: StringLength(160, MinimumLength = 2)] string? Name,
    [property: StringLength(2000)] string? Description);

/// <summary>Step type names the runtime knows how to execute — kept in sync with runtime/executors.py's _WORKFLOW_STEP_TYPES.</summary>
public static class WorkflowStepTypes
{
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(
        ["prompt", "require_knowledge", "require_tool"], StringComparer.Ordinal);
}

public interface IWorkflowRepository
{
    Task<IReadOnlyList<WorkflowRecord>> ListAsync(bool activeOnly, CancellationToken ct);
    Task<WorkflowRecord?> FindAsync(Guid id, CancellationToken ct);
    Task<WorkflowRecord> CreateAsync(Guid actorId, CreateWorkflowRequest request, CancellationToken ct);
    Task<WorkflowRecord?> UpdateAsync(Guid id, UpdateWorkflowRequest request, CancellationToken ct);

    // ACL V2-E: Department Workflow ownership. Tenant/department always
    // derived from the caller's principal — see WorkflowEndpoints.TryScope,
    // the exact pattern DepartmentAgentEndpoints/CapabilityEndpoints already
    // established in V2-C/V2-D.
    /// <summary>Unified ADMIN list: platform plus the authenticated department. Mutation methods remain department-owned only.</summary>
    Task<IReadOnlyList<WorkflowRecord>> ListOwnDepartmentWorkflowsAsync(Guid tenantId, Guid departmentId, CancellationToken ct);
    Task<WorkflowRecord?> FindOwnDepartmentWorkflowAsync(Guid tenantId, Guid departmentId, Guid workflowId, CancellationToken ct);
    Task<WorkflowRecord> CreateDepartmentWorkflowAsync(Guid tenantId, Guid departmentId, Guid actorId, CreateDepartmentWorkflowRequest request, CancellationToken ct);
    Task<WorkflowRecord?> UpdateDepartmentWorkflowMetadataAsync(Guid tenantId, Guid departmentId, Guid workflowId, UpdateDepartmentWorkflowRequest request, CancellationToken ct);
    Task<WorkflowRecord?> SetDepartmentWorkflowEnabledAsync(Guid tenantId, Guid departmentId, Guid workflowId, bool enabled, CancellationToken ct);
    Task<IReadOnlyList<WorkflowVersionRecord>> ListDepartmentWorkflowVersionsAsync(Guid tenantId, Guid departmentId, Guid workflowId, CancellationToken ct);
    Task<WorkflowVersionRecord?> CreateDepartmentWorkflowDraftVersionAsync(Guid tenantId, Guid departmentId, Guid workflowId, Guid actorId, JsonElement? definition, CancellationToken ct);
    Task<WorkflowVersionRecord?> UpdateDepartmentWorkflowDraftVersionAsync(Guid tenantId, Guid departmentId, Guid workflowId, Guid versionId, JsonElement definition, CancellationToken ct);
    Task<WorkflowVersionRecord?> PublishDepartmentWorkflowVersionAsync(Guid tenantId, Guid departmentId, Guid workflowId, Guid versionId, Guid actorId, CancellationToken ct);

    /// <summary>
    /// True only if workflowId is either a scope='platform' workflow (always
    /// eligible — read/select-only for ADMIN, per V2-C's catalog_reader
    /// precedent) or a scope='department' workflow owned by exactly this
    /// tenant+department AND currently has a published version. Used to gate
    /// which workflow a Department Agent draft may attach — mirrors
    /// IDynamicAgentRepository.ReplaceDepartmentAgentCapabilitiesAsync's
    /// ownership-eligibility pattern from V2-D.
    /// </summary>
    Task<bool> IsWorkflowEligibleForDepartmentAsync(Guid tenantId, Guid departmentId, Guid workflowId, CancellationToken ct);
}

public sealed class WorkflowRepository : IWorkflowRepository
{
    private const string Columns = """
        id AS Id, name AS Name, description AS Description, definition::text AS DefinitionJson,
        is_active AS IsActive, scope AS Scope, tenant_id AS TenantId, department_id AS DepartmentId,
        current_draft_version_id AS CurrentDraftVersionId, published_version_id AS PublishedVersionId,
        created_by AS CreatedBy, created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    private const string VersionColumns = """
        id AS Id, workflow_id AS WorkflowId, version AS Version, status AS Status,
        definition::text AS DefinitionJson, created_by AS CreatedBy, created_at AS CreatedAt,
        published_by AS PublishedBy, published_at AS PublishedAt
        """;

    private const string OwnDepartmentWorkflowWhere =
        "scope='department' AND tenant_id=@tenantId AND department_id=@departmentId";
    private const string VisibleDepartmentWorkflowWhere =
        "(scope='platform' OR (scope='department' AND tenant_id=@tenantId AND department_id=@departmentId))";

    private readonly NpgsqlConnectionFactory _factory;
    public WorkflowRepository(NpgsqlConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<WorkflowRecord>> ListAsync(bool activeOnly, CancellationToken ct)
    {
        var sql = $"SELECT {Columns} FROM workflows WHERE scope='platform' {(activeOnly ? "AND is_active = TRUE" : "")} ORDER BY name";
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<WorkflowRecord>(new CommandDefinition(sql, cancellationToken: ct))).AsList();
    }

    public async Task<WorkflowRecord?> FindAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<WorkflowRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM workflows WHERE id=@id", new { id }, cancellationToken: ct));
    }

    public async Task<WorkflowRecord> CreateAsync(Guid actorId, CreateWorkflowRequest request, CancellationToken ct)
    {
        var definition = request.Definition?.GetRawText() ?? """{"steps":[],"requiresApproval":false}""";
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleAsync<WorkflowRecord>(new CommandDefinition($"""
            INSERT INTO workflows (name, description, definition, created_by)
            VALUES (@Name, @Description, @definition::jsonb, @actorId)
            RETURNING {Columns}
            """, new { request.Name, request.Description, definition, actorId }, cancellationToken: ct));
    }

    public async Task<WorkflowRecord?> UpdateAsync(Guid id, UpdateWorkflowRequest request, CancellationToken ct)
    {
        var definition = request.Definition?.GetRawText();
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<WorkflowRecord>(new CommandDefinition($"""
            UPDATE workflows SET
                name = COALESCE(@Name, name),
                description = COALESCE(@Description, description),
                definition = COALESCE(@definition::jsonb, definition),
                is_active = COALESCE(@IsActive, is_active),
                updated_at = NOW()
            WHERE id = @id
            RETURNING {Columns}
            """, new { id, request.Name, request.Description, definition, request.IsActive }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<WorkflowRecord>> ListOwnDepartmentWorkflowsAsync(Guid tenantId, Guid departmentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<WorkflowRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM workflows WHERE {VisibleDepartmentWorkflowWhere} ORDER BY scope, name",
            new { tenantId, departmentId }, cancellationToken: ct))).AsList();
    }

    public async Task<WorkflowRecord?> FindOwnDepartmentWorkflowAsync(Guid tenantId, Guid departmentId, Guid workflowId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<WorkflowRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM workflows WHERE id=@workflowId AND {OwnDepartmentWorkflowWhere}",
            new { workflowId, tenantId, departmentId }, cancellationToken: ct));
    }

    // Definition lives only on workflow_versions from here on; `workflows.definition`
    // is written once at creation only to satisfy its pre-existing NOT NULL
    // default and is never read again for scope='department' rows —
    // DynamicAgentRepository.TryResolveWorkflowAsync's department branch reads
    // workflow_versions.definition via published_version_id exclusively.
    public async Task<WorkflowRecord> CreateDepartmentWorkflowAsync(Guid tenantId, Guid departmentId, Guid actorId, CreateDepartmentWorkflowRequest request, CancellationToken ct)
    {
        var definition = request.Definition?.GetRawText() ?? """{"steps":[],"requiresApproval":false}""";
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var workflowId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO workflows (name, description, definition, scope, tenant_id, department_id, created_by)
            VALUES (@Name, @Description, @definition::jsonb, 'department', @tenantId, @departmentId, @actorId)
            RETURNING id
            """, new { request.Name, request.Description, definition, tenantId, departmentId, actorId }, tx, cancellationToken: ct));
        var versionId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO workflow_versions (workflow_id, version, status, definition, created_by)
            VALUES (@workflowId, 1, 'draft', @definition::jsonb, @actorId)
            RETURNING id
            """, new { workflowId, definition, actorId }, tx, cancellationToken: ct));
        var row = await conn.QuerySingleAsync<WorkflowRecord>(new CommandDefinition($"""
            UPDATE workflows SET current_draft_version_id=@versionId WHERE id=@workflowId
            RETURNING {Columns}
            """, new { workflowId, versionId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return row;
    }

    public async Task<WorkflowRecord?> UpdateDepartmentWorkflowMetadataAsync(Guid tenantId, Guid departmentId, Guid workflowId, UpdateDepartmentWorkflowRequest request, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<WorkflowRecord>(new CommandDefinition($"""
            UPDATE workflows SET
                name = COALESCE(@Name, name),
                description = COALESCE(@Description, description),
                updated_at = NOW()
            WHERE id=@workflowId AND {OwnDepartmentWorkflowWhere}
            RETURNING {Columns}
            """, new { workflowId, tenantId, departmentId, request.Name, request.Description }, cancellationToken: ct));
    }

    public async Task<WorkflowRecord?> SetDepartmentWorkflowEnabledAsync(Guid tenantId, Guid departmentId, Guid workflowId, bool enabled, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<WorkflowRecord>(new CommandDefinition($"""
            UPDATE workflows SET is_active=@enabled, updated_at=NOW()
            WHERE id=@workflowId AND {OwnDepartmentWorkflowWhere}
            RETURNING {Columns}
            """, new { workflowId, tenantId, departmentId, enabled }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<WorkflowVersionRecord>> ListDepartmentWorkflowVersionsAsync(Guid tenantId, Guid departmentId, Guid workflowId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var owned = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"SELECT EXISTS(SELECT 1 FROM workflows WHERE id=@workflowId AND {OwnDepartmentWorkflowWhere})",
            new { workflowId, tenantId, departmentId }, cancellationToken: ct));
        if (!owned) return [];
        return (await conn.QueryAsync<WorkflowVersionRecord>(new CommandDefinition(
            $"SELECT {VersionColumns} FROM workflow_versions WHERE workflow_id=@workflowId ORDER BY version DESC",
            new { workflowId }, cancellationToken: ct))).AsList();
    }

    public async Task<WorkflowVersionRecord?> CreateDepartmentWorkflowDraftVersionAsync(Guid tenantId, Guid departmentId, Guid workflowId, Guid actorId, JsonElement? definition, CancellationToken ct)
    {
        var definitionText = definition?.GetRawText() ?? """{"steps":[],"requiresApproval":false}""";
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var owned = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"SELECT EXISTS(SELECT 1 FROM workflows WHERE id=@workflowId AND {OwnDepartmentWorkflowWhere})",
            new { workflowId, tenantId, departmentId }, tx, cancellationToken: ct));
        if (!owned) { await tx.RollbackAsync(ct); return null; }
        Guid versionId;
        try
        {
            versionId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
                INSERT INTO workflow_versions (workflow_id, version, status, definition, created_by)
                SELECT @workflowId, COALESCE(MAX(version), 0) + 1, 'draft', @definitionText::jsonb, @actorId
                FROM workflow_versions WHERE workflow_id=@workflowId
                RETURNING id
                """, new { workflowId, definitionText, actorId }, tx, cancellationToken: ct));
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
        {
            await tx.RollbackAsync(ct);
            throw new InvalidOperationException("workflow_already_has_draft");
        }
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE workflows SET current_draft_version_id=@versionId WHERE id=@workflowId",
            new { workflowId, versionId }, tx, cancellationToken: ct));
        var row = await conn.QuerySingleAsync<WorkflowVersionRecord>(new CommandDefinition(
            $"SELECT {VersionColumns} FROM workflow_versions WHERE id=@versionId", new { versionId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return row;
    }

    public async Task<WorkflowVersionRecord?> UpdateDepartmentWorkflowDraftVersionAsync(Guid tenantId, Guid departmentId, Guid workflowId, Guid versionId, JsonElement definition, CancellationToken ct)
    {
        var definitionText = definition.GetRawText();
        await using var conn = await _factory.OpenAsync(ct);
        var owned = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"SELECT EXISTS(SELECT 1 FROM workflows WHERE id=@workflowId AND {OwnDepartmentWorkflowWhere})",
            new { workflowId, tenantId, departmentId }, cancellationToken: ct));
        if (!owned) return null;
        return await conn.QuerySingleOrDefaultAsync<WorkflowVersionRecord>(new CommandDefinition($"""
            UPDATE workflow_versions SET definition=@definitionText::jsonb
            WHERE id=@versionId AND workflow_id=@workflowId AND status='draft'
            RETURNING {VersionColumns}
            """, new { versionId, workflowId, definitionText }, cancellationToken: ct));
    }

    public async Task<WorkflowVersionRecord?> PublishDepartmentWorkflowVersionAsync(Guid tenantId, Guid departmentId, Guid workflowId, Guid versionId, Guid actorId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var owned = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"SELECT EXISTS(SELECT 1 FROM workflows WHERE id=@workflowId AND {OwnDepartmentWorkflowWhere})",
            new { workflowId, tenantId, departmentId }, tx, cancellationToken: ct));
        if (!owned) { await tx.RollbackAsync(ct); return null; }
        // An empty step list can never meaningfully execute — reject
        // publishing it rather than silently freezing a no-op workflow
        // version forever (published versions are immutable, so this can't
        // be fixed later without a whole new draft/version).
        var stepCount = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT jsonb_array_length(definition->'steps') FROM workflow_versions WHERE id=@versionId AND workflow_id=@workflowId AND status='draft'",
            new { versionId, workflowId }, tx, cancellationToken: ct));
        if (stepCount is null or 0)
        {
            await tx.RollbackAsync(ct);
            throw new InvalidOperationException("workflow_has_no_steps");
        }
        var row = await conn.QuerySingleOrDefaultAsync<WorkflowVersionRecord>(new CommandDefinition($"""
            UPDATE workflow_versions SET status='published', published_by=@actorId, published_at=NOW()
            WHERE id=@versionId AND workflow_id=@workflowId AND status='draft'
            RETURNING {VersionColumns}
            """, new { versionId, workflowId, actorId }, tx, cancellationToken: ct));
        if (row is null) { await tx.RollbackAsync(ct); return null; }
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE workflows SET published_version_id=@versionId, current_draft_version_id=NULL, updated_at=NOW() WHERE id=@workflowId",
            new { workflowId, versionId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return row;
    }

    public async Task<bool> IsWorkflowEligibleForDepartmentAsync(Guid tenantId, Guid departmentId, Guid workflowId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (
                SELECT 1 FROM workflows w
                WHERE w.id=@workflowId AND w.is_active
                  AND (w.scope='platform'
                       OR (w.scope='department' AND w.tenant_id=@tenantId AND w.department_id=@departmentId
                           AND w.published_version_id IS NOT NULL))
            )
            """, new { workflowId, tenantId, departmentId }, cancellationToken: ct));
    }
}
