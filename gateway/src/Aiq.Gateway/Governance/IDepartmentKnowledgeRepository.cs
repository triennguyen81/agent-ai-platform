using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Governance;

/// <summary>
/// ACL V2-E: Department Knowledge ownership. Reuses the existing `notes`
/// table with `scope='department'` (migration 024 added exactly this
/// column set for this purpose — see its own report §3 "Knowledge" section)
/// rather than inventing a parallel content table. Deliberately separate
/// from Notes.INoteRepository, which remains the unchanged PERSONAL,
/// per-user `scope='tenant'` Knowledge feature — a department item's
/// `user_id` is always NULL (nullable since migration 024) so it can never
/// appear on any single user's personal `/notes` list, and a personal
/// note's `department_id` is always NULL so it can never appear here.
///
/// Content is plain text/markdown, matching the existing product's Notes
/// feature exactly — there is no file/object-storage attachment for
/// Knowledge in this codebase (vouchers/contracts are the only features
/// that touch S3, and neither is a Knowledge/RAG input), so no
/// object-storage isolation change was required for V2-E; see the V2-E
/// report §7 for the explicit reasoning.
/// </summary>
public sealed record DepartmentKnowledgeRecord(
    Guid Id, string Title, string Content, string[] Tags, bool IsArchived,
    string Scope, Guid? CreatedBy, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record CreateDepartmentKnowledgeRequest(
    [property: Required, StringLength(300, MinimumLength = 1)] string Title,
    string? Content,
    string[]? Tags);

public sealed record UpdateDepartmentKnowledgeRequest(
    [property: StringLength(300, MinimumLength = 1)] string? Title,
    string? Content,
    string[]? Tags);

public sealed record AttachableKnowledgeSummary(Guid Id, string Title, string[] Tags, string Scope);
public sealed record AttachedKnowledgeSummary(Guid Id, string Title, string[] Tags, string Scope, DateTime AttachedAt);

public interface IDepartmentKnowledgeRepository
{
    Task<IReadOnlyList<DepartmentKnowledgeRecord>> ListPlatformAsync(CancellationToken ct);
    Task<DepartmentKnowledgeRecord> CreatePlatformAsync(Guid actorId, CreateDepartmentKnowledgeRequest request, CancellationToken ct);
    Task<DepartmentKnowledgeRecord?> UpdatePlatformAsync(Guid noteId, UpdateDepartmentKnowledgeRequest request, CancellationToken ct);
    Task<DepartmentKnowledgeRecord?> SetPlatformArchivedAsync(Guid noteId, bool archived, CancellationToken ct);
    Task<bool> DeletePlatformAsync(Guid noteId, CancellationToken ct);
    Task<IReadOnlyList<DepartmentKnowledgeRecord>> ListOwnAsync(Guid tenantId, Guid departmentId, CancellationToken ct);
    Task<DepartmentKnowledgeRecord?> FindOwnAsync(Guid tenantId, Guid departmentId, Guid noteId, CancellationToken ct);
    Task<DepartmentKnowledgeRecord> CreateAsync(Guid tenantId, Guid departmentId, Guid actorId, CreateDepartmentKnowledgeRequest request, CancellationToken ct);
    Task<DepartmentKnowledgeRecord?> UpdateAsync(Guid tenantId, Guid departmentId, Guid noteId, UpdateDepartmentKnowledgeRequest request, CancellationToken ct);
    Task<DepartmentKnowledgeRecord?> SetArchivedAsync(Guid tenantId, Guid departmentId, Guid noteId, bool archived, CancellationToken ct);

    /// <summary>Hard delete — refused (returns false) if any Agent currently has this item attached; ADMIN must detach or archive instead.</summary>
    Task<bool> DeleteAsync(Guid tenantId, Guid departmentId, Guid noteId, CancellationToken ct);

    // Department Agent attachment — both sides (agent AND note) must
    // resolve to this exact tenant+department; a raw UUID for either side
    // belonging to another department/tenant never matches the ownership
    // WHERE clause, so it 404s/no-ops rather than silently succeeding.
    Task<IReadOnlyList<AttachedKnowledgeSummary>> ListForDepartmentAgentAsync(Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct);
    Task<IReadOnlyList<AttachableKnowledgeSummary>> ListAttachableForDepartmentAgentAsync(Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct);
    Task<bool> AttachToDepartmentAgentAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid noteId, Guid actorId, CancellationToken ct);
    Task<bool> DetachFromDepartmentAgentAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid noteId, CancellationToken ct);
}

public sealed class DepartmentKnowledgeRepository : IDepartmentKnowledgeRepository
{
    private const string Columns = """
        id AS Id, title AS Title, content AS Content, tags AS Tags, is_archived AS IsArchived, scope AS Scope,
        created_by AS CreatedBy, created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    private const string OwnDepartmentNoteWhere =
        "scope='department' AND tenant_id=@tenantId AND department_id=@departmentId";
    private const string OwnDepartmentNoteWhereAliased =
        "n.scope='department' AND n.tenant_id=@tenantId AND n.department_id=@departmentId";
    private const string VisibleDepartmentNoteWhere =
        "(scope='platform' OR (scope='department' AND tenant_id=@tenantId AND department_id=@departmentId))";
    private const string VisibleDepartmentNoteWhereAliased =
        "(n.scope='platform' OR (n.scope='department' AND n.tenant_id=@tenantId AND n.department_id=@departmentId))";

    private const string DepartmentAgentOwnershipWhere =
        "a.v2_scope='department' AND a.owning_tenant_id=@tenantId AND a.department_id=@departmentId";

    private readonly NpgsqlConnectionFactory _factory;
    public DepartmentKnowledgeRepository(NpgsqlConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<DepartmentKnowledgeRecord>> ListPlatformAsync(CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<DepartmentKnowledgeRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM notes WHERE scope='platform' AND NOT is_archived ORDER BY updated_at DESC", cancellationToken: ct))).AsList();
    }

    public async Task<DepartmentKnowledgeRecord> CreatePlatformAsync(Guid actorId, CreateDepartmentKnowledgeRequest request, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleAsync<DepartmentKnowledgeRecord>(new CommandDefinition($"""
            INSERT INTO notes(user_id,tenant_id,title,content,tags,scope,department_id,created_by)
            VALUES (NULL,NULL,@Title,COALESCE(@Content,''),COALESCE(@Tags,ARRAY[]::TEXT[]),'platform',NULL,@actorId)
            RETURNING {Columns}
            """, new { actorId, request.Title, request.Content, request.Tags }, cancellationToken: ct));
    }

    public async Task<DepartmentKnowledgeRecord?> UpdatePlatformAsync(Guid noteId, UpdateDepartmentKnowledgeRequest request, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<DepartmentKnowledgeRecord>(new CommandDefinition($"""
            UPDATE notes SET title=COALESCE(@Title,title),content=COALESCE(@Content,content),tags=COALESCE(@Tags,tags),updated_at=NOW()
            WHERE id=@noteId AND scope='platform' RETURNING {Columns}
            """, new { noteId, request.Title, request.Content, request.Tags }, cancellationToken: ct));
    }

    public async Task<DepartmentKnowledgeRecord?> SetPlatformArchivedAsync(Guid noteId, bool archived, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<DepartmentKnowledgeRecord>(new CommandDefinition(
            $"UPDATE notes SET is_archived=@archived,updated_at=NOW() WHERE id=@noteId AND scope='platform' RETURNING {Columns}",
            new { noteId, archived }, cancellationToken: ct));
    }

    public async Task<bool> DeletePlatformAsync(Guid noteId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        if (await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM agent_knowledge WHERE note_id=@noteId)", new { noteId }, cancellationToken: ct))) return false;
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM notes WHERE id=@noteId AND scope='platform'", new { noteId }, cancellationToken: ct)) > 0;
    }

    public async Task<IReadOnlyList<DepartmentKnowledgeRecord>> ListOwnAsync(Guid tenantId, Guid departmentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<DepartmentKnowledgeRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM notes WHERE {VisibleDepartmentNoteWhere} AND NOT is_archived ORDER BY scope, updated_at DESC",
            new { tenantId, departmentId }, cancellationToken: ct))).AsList();
    }

    public async Task<DepartmentKnowledgeRecord?> FindOwnAsync(Guid tenantId, Guid departmentId, Guid noteId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<DepartmentKnowledgeRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM notes WHERE id=@noteId AND {OwnDepartmentNoteWhere}",
            new { noteId, tenantId, departmentId }, cancellationToken: ct));
    }

    public async Task<DepartmentKnowledgeRecord> CreateAsync(Guid tenantId, Guid departmentId, Guid actorId, CreateDepartmentKnowledgeRequest request, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleAsync<DepartmentKnowledgeRecord>(new CommandDefinition($"""
            INSERT INTO notes (user_id, tenant_id, title, content, tags, scope, department_id, created_by)
            VALUES (NULL, @tenantId, @Title, COALESCE(@Content, ''), COALESCE(@Tags, ARRAY[]::TEXT[]), 'department', @departmentId, @actorId)
            RETURNING {Columns}
            """, new { tenantId, departmentId, actorId, request.Title, request.Content, request.Tags }, cancellationToken: ct));
    }

    public async Task<DepartmentKnowledgeRecord?> UpdateAsync(Guid tenantId, Guid departmentId, Guid noteId, UpdateDepartmentKnowledgeRequest request, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<DepartmentKnowledgeRecord>(new CommandDefinition($"""
            UPDATE notes SET
                title = COALESCE(@Title, title),
                content = COALESCE(@Content, content),
                tags = COALESCE(@Tags, tags),
                updated_at = NOW()
            WHERE id=@noteId AND {OwnDepartmentNoteWhere}
            RETURNING {Columns}
            """, new { noteId, tenantId, departmentId, request.Title, request.Content, request.Tags }, cancellationToken: ct));
    }

    public async Task<DepartmentKnowledgeRecord?> SetArchivedAsync(Guid tenantId, Guid departmentId, Guid noteId, bool archived, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<DepartmentKnowledgeRecord>(new CommandDefinition($"""
            UPDATE notes SET is_archived=@archived, updated_at=NOW()
            WHERE id=@noteId AND {OwnDepartmentNoteWhere}
            RETURNING {Columns}
            """, new { noteId, tenantId, departmentId, archived }, cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(Guid tenantId, Guid departmentId, Guid noteId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var attached = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM agent_knowledge WHERE note_id=@noteId)",
            new { noteId }, cancellationToken: ct));
        if (attached) return false;
        var deleted = await conn.ExecuteAsync(new CommandDefinition(
            $"DELETE FROM notes WHERE id=@noteId AND {OwnDepartmentNoteWhere}",
            new { noteId, tenantId, departmentId }, cancellationToken: ct));
        return deleted > 0;
    }

    public async Task<IReadOnlyList<AttachedKnowledgeSummary>> ListForDepartmentAgentAsync(Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<AttachedKnowledgeSummary>(new CommandDefinition($"""
            SELECT n.id AS Id, n.title AS Title, n.tags AS Tags, n.scope AS Scope, ak.attached_at AS AttachedAt
            FROM agent_knowledge ak
            JOIN notes n ON n.id = ak.note_id
            JOIN agents a ON a.id = ak.agent_id AND {DepartmentAgentOwnershipWhere}
            WHERE ak.agent_id = @agentId AND {VisibleDepartmentNoteWhereAliased}
            ORDER BY n.title
            """, new { agentId, tenantId, departmentId }, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<AttachableKnowledgeSummary>> ListAttachableForDepartmentAgentAsync(Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var agentOwned = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"SELECT EXISTS(SELECT 1 FROM agents a WHERE a.id=@agentId AND {DepartmentAgentOwnershipWhere})",
            new { agentId, tenantId, departmentId }, cancellationToken: ct));
        if (!agentOwned) return [];
        return (await conn.QueryAsync<AttachableKnowledgeSummary>(new CommandDefinition($"""
            SELECT n.id AS Id, n.title AS Title, n.tags AS Tags, n.scope AS Scope
            FROM notes n
            WHERE {VisibleDepartmentNoteWhereAliased}
              AND NOT n.is_archived
              AND NOT EXISTS (SELECT 1 FROM agent_knowledge ak WHERE ak.agent_id=@agentId AND ak.note_id=n.id)
            ORDER BY n.title
            LIMIT 500
            """, new { agentId, tenantId, departmentId }, cancellationToken: ct))).AsList();
    }

    public async Task<bool> AttachToDepartmentAgentAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid noteId, Guid actorId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var agentOwned = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"SELECT EXISTS(SELECT 1 FROM agents a WHERE a.id=@agentId AND {DepartmentAgentOwnershipWhere})",
            new { agentId, tenantId, departmentId }, cancellationToken: ct));
        if (!agentOwned) return false;
        var inserted = await conn.ExecuteAsync(new CommandDefinition($"""
            INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
            SELECT @agentId, n.id, @actorId
            FROM notes n
            WHERE n.id=@noteId AND {VisibleDepartmentNoteWhereAliased}
            ON CONFLICT (agent_id, note_id) DO NOTHING
            """, new { agentId, noteId, actorId, tenantId, departmentId }, cancellationToken: ct));
        if (inserted > 0) return true;
        // Idempotent: already attached counts as success (matches
        // CapabilityEndpoints's binding idempotency precedent), but a note
        // that doesn't exist/isn't this department's own is still a no-op.
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition($"""
            SELECT EXISTS(
                SELECT 1 FROM agent_knowledge ak
                JOIN notes n ON n.id=ak.note_id
                WHERE ak.agent_id=@agentId AND ak.note_id=@noteId AND {VisibleDepartmentNoteWhereAliased})
            """, new { agentId, noteId, tenantId, departmentId }, cancellationToken: ct));
    }

    public async Task<bool> DetachFromDepartmentAgentAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid noteId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var agentOwned = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"SELECT EXISTS(SELECT 1 FROM agents a WHERE a.id=@agentId AND {DepartmentAgentOwnershipWhere})",
            new { agentId, tenantId, departmentId }, cancellationToken: ct));
        if (!agentOwned) return false;
        var deleted = await conn.ExecuteAsync(new CommandDefinition($"""
            DELETE FROM agent_knowledge ak
            USING notes n
            WHERE ak.agent_id=@agentId AND ak.note_id=@noteId AND n.id=ak.note_id
              AND {VisibleDepartmentNoteWhereAliased}
            """, new { agentId, noteId, tenantId, departmentId }, cancellationToken: ct));
        return deleted > 0;
    }
}
