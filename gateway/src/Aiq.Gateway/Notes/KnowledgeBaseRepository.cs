using Aiq.Gateway.Data;
using Aiq.Gateway.Governance;
using Dapper;

namespace Aiq.Gateway.Notes;

public sealed class KnowledgeBaseRepository(NpgsqlConnectionFactory factory)
{
    public async Task<AgentExecutionManifest> ReferencesAsync(AgentExecutionManifest manifest, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var ids = (await conn.QueryAsync<Guid>(new CommandDefinition("""
            SELECT DISTINCT k.id FROM knowledge_bases k
            WHERE k.status='active' AND (
              (k.tenant_id=@tenantId AND EXISTS(SELECT 1 FROM agent_knowledge_bases a
                WHERE a.knowledge_base_id=k.id AND a.tenant_id=@tenantId AND a.agent_id=@agentId)
                AND (k.department_id IS NULL OR k.department_id=@departmentId))
              OR EXISTS(SELECT 1 FROM notes n WHERE n.knowledge_base_id=k.id AND n.id=ANY(@notes)
                AND NOT n.is_archived AND (n.tenant_id=@tenantId OR n.scope='platform')))
            """, new { tenantId = manifest.TenantId, agentId = manifest.Agent.Id,
                departmentId = manifest.DepartmentId, notes = manifest.Knowledge.Select(n => n.NoteId).ToArray() }, cancellationToken: ct))).ToArray();
        return manifest with { KnowledgeBaseIds = ids,
            Knowledge = manifest.Knowledge.Select(n => n with { Content = "" }).ToArray() };
    }

    public async Task<object> ListAsync(Guid tenantId, Guid departmentId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QueryAsync(new CommandDefinition("""
            SELECT k.id,k.name,k.description,k.status,k.created_at AS "createdAt",
              (SELECT count(*) FROM notes n WHERE n.knowledge_base_id=k.id) AS "documentCount"
            FROM knowledge_bases k WHERE k.tenant_id=@tenantId AND k.department_id=@departmentId
            ORDER BY k.created_at DESC
            """, new { tenantId, departmentId }, cancellationToken: ct));
    }

    public async Task<bool> OwnsAsync(Guid tenantId, Guid departmentId, Guid id, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM knowledge_bases WHERE id=@id AND tenant_id=@tenantId AND department_id=@departmentId AND status<>'deleted')",
            new { tenantId, departmentId, id }, cancellationToken: ct));
    }

    public async Task<Guid> CreateAsync(Guid tenantId, Guid departmentId, Guid actorId, string name, string description, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO knowledge_bases(tenant_id,department_id,name,description,created_by)
            VALUES(@tenantId,@departmentId,@name,@description,@actorId) RETURNING id
            """, new { tenantId, departmentId, actorId, name, description }, cancellationToken: ct));
    }

    public async Task<object> DocumentsAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QueryAsync(new CommandDefinition("""
            SELECT n.id,n.title,n.tags,n.mime_type AS "mimeType",n.source,n.is_archived AS "isArchived",
              n.current_version_id AS "currentVersionId",v.id AS "versionId",v.version,v.status,
              v.created_at AS "createdAt",j.status AS "indexingStatus",j.error,
              cleanup.status AS "cleanupStatus",cleanup.error AS "cleanupError"
            FROM notes n LEFT JOIN document_versions v ON v.document_id=n.id
            LEFT JOIN LATERAL (SELECT status,error FROM indexing_jobs WHERE document_version_id=v.id ORDER BY created_at DESC LIMIT 1) j ON true
            LEFT JOIN LATERAL (SELECT status,error FROM indexing_jobs WHERE document_id=n.id AND kind='cleanup' ORDER BY created_at DESC LIMIT 1) cleanup ON true
            WHERE n.knowledge_base_id=@id ORDER BY n.created_at DESC,v.version DESC
            """, new { id }, cancellationToken: ct));
    }

    public async Task<Guid?> SaveDocumentAsync(Guid tenantId, Guid departmentId, Guid actorId, Guid id,
        Guid? documentId, string title, string content, string mimeType, CancellationToken ct, string[]? tags = null, string source = "upload")
    {
        await using var conn = await factory.OpenAsync(ct);
        // Updates lock the canonical note row; the version trigger numbers versions under that lock.
        if (documentId is not null)
            return await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition("""
                UPDATE notes SET title=@title,content=@content,mime_type=@mimeType,tags=COALESCE(@tags,tags),updated_at=now()
                WHERE id=@documentId AND knowledge_base_id=@id AND tenant_id=@tenantId AND department_id=@departmentId RETURNING id
                """, new { tenantId, departmentId, id, documentId, title, content, mimeType, tags }, cancellationToken: ct));
        return await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition("""
            INSERT INTO notes(tenant_id,department_id,created_by,scope,title,content,tags,knowledge_base_id,mime_type,source)
            SELECT @tenantId,@departmentId,@actorId,'department',@title,@content,COALESCE(@tags,'{}'::text[]),@id,@mimeType,@source
            FROM knowledge_bases WHERE id=@id AND tenant_id=@tenantId AND department_id=@departmentId AND status<>'deleted'
            ON CONFLICT(knowledge_base_id,title) WHERE source='upload'
            DO UPDATE SET content=EXCLUDED.content,mime_type=EXCLUDED.mime_type,updated_at=now() RETURNING id
            """, new { tenantId, departmentId, actorId, title, content, id, mimeType, tags, source }, cancellationToken: ct));
    }

    public async Task<bool> DocumentActionAsync(Guid id, Guid documentId, string action, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var sql = action == "reindex" ? """
            INSERT INTO indexing_jobs(document_id,document_version_id)
            SELECT n.id,v.id FROM notes n JOIN LATERAL
             (SELECT id FROM document_versions WHERE document_id=n.id ORDER BY version DESC LIMIT 1) v ON true
            WHERE n.knowledge_base_id=@id AND n.id=@documentId AND NOT n.is_archived
            """ : "UPDATE notes SET is_archived=@archived WHERE knowledge_base_id=@id AND id=@documentId";
        return await conn.ExecuteAsync(new CommandDefinition(sql, new { id, documentId, archived = action != "restore" }, cancellationToken: ct)) > 0;
    }

    public async Task SetStatusAsync(Guid id, string status, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE knowledge_bases SET status=@status WHERE id=@id", new { id, status }, cancellationToken: ct));
    }

    public async Task<object> AgentsAsync(Guid tenantId, Guid departmentId, Guid id, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QueryAsync(new CommandDefinition("""
            SELECT a.id,a.name,EXISTS(SELECT 1 FROM agent_knowledge_bases x WHERE x.agent_id=a.id AND x.tenant_id=@tenantId AND x.knowledge_base_id=@id) AS attached
            FROM agents a WHERE a.owning_tenant_id=@tenantId AND a.department_id=@departmentId
              AND a.v2_scope='department' AND a.v2_migration_status='resolved'
            """, new { tenantId, departmentId, id }, cancellationToken: ct));
    }

    public async Task<bool> AttachAsync(Guid tenantId, Guid departmentId, Guid actorId, Guid id, Guid agentId, bool attach, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        // Structural department ownership is the same rule used by the existing Department Agent builder.
        var owns = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM agents WHERE id=@agentId AND owning_tenant_id=@tenantId
                AND department_id=@departmentId AND v2_scope='department' AND v2_migration_status='resolved')
            """, new { tenantId, departmentId, agentId }, cancellationToken: ct));
        if (!owns) return false;
        await conn.ExecuteAsync(new CommandDefinition(attach ? """
            INSERT INTO agent_knowledge_bases(agent_id,tenant_id,knowledge_base_id,attached_by)
            VALUES(@agentId,@tenantId,@id,@actorId) ON CONFLICT DO NOTHING
            """ : "DELETE FROM agent_knowledge_bases WHERE agent_id=@agentId AND tenant_id=@tenantId AND knowledge_base_id=@id",
            new { tenantId, actorId, id, agentId }, cancellationToken: ct));
        return true;
    }
}
