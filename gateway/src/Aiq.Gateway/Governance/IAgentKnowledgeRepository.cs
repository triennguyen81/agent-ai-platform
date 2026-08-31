using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Governance;

public sealed record AgentKnowledgeNote(
    Guid AgentId,
    Guid NoteId,
    string Title,
    string Content,
    string[] Tags,
    DateTime AttachedAt);

public sealed record NoteSummary(
    Guid Id,
    string Title,
    string[] Tags,
    DateTime UpdatedAt);

public interface IAgentKnowledgeRepository
{
    /// <summary>
    /// Notes attached to the agent, with full content (for chat RAG context).
    /// </summary>
    Task<IReadOnlyList<AgentKnowledgeNote>> ListForAgentAsync(
        Guid tenantId, Guid agentId, CancellationToken ct);

    /// <summary>
    /// Same shape as ListForAgentAsync but returns only summaries — used by
    /// the admin UI table that lists what's attached.
    /// </summary>
    Task<IReadOnlyList<NoteSummary>> ListSummariesForAgentAsync(
        Guid agentId, CancellationToken ct);

    /// <summary>
    /// Notes in the tenant that could be attached to the agent (excludes
    /// already-attached). Used by the admin UI attach dropdown.
    /// </summary>
    Task<IReadOnlyList<NoteSummary>> ListAttachableAsync(
        Guid tenantId, Guid agentId, CancellationToken ct);

    Task<bool> AttachAsync(
        Guid tenantId, Guid agentId, Guid noteId, Guid attachedBy, CancellationToken ct);

    Task<bool> DetachAsync(Guid tenantId, Guid agentId, Guid noteId, CancellationToken ct);
}

public sealed class AgentKnowledgeRepository : IAgentKnowledgeRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public AgentKnowledgeRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<AgentKnowledgeNote>> ListForAgentAsync(
        Guid tenantId, Guid agentId, CancellationToken ct)
    {
        const string sql = """
            SELECT
                ak.agent_id     AS AgentId,
                n.id            AS NoteId,
                n.title         AS Title,
                n.content       AS Content,
                n.tags          AS Tags,
                ak.attached_at  AS AttachedAt
            FROM agent_knowledge ak
            JOIN notes n ON n.id = ak.note_id
            JOIN agents a ON a.id = ak.agent_id
            WHERE ak.agent_id = @agentId AND a.tenant_id = @tenantId AND n.tenant_id = @tenantId
            ORDER BY ak.attached_at DESC
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<AgentKnowledgeNote>(
            new CommandDefinition(sql, new { tenantId, agentId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<NoteSummary>> ListSummariesForAgentAsync(
        Guid agentId, CancellationToken ct)
    {
        const string sql = """
            SELECT
                n.id         AS Id,
                n.title      AS Title,
                n.tags       AS Tags,
                n.updated_at AS UpdatedAt
            FROM agent_knowledge ak
            JOIN notes n ON n.id = ak.note_id
            WHERE ak.agent_id = @agentId
            ORDER BY n.updated_at DESC
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<NoteSummary>(
            new CommandDefinition(sql, new { agentId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<NoteSummary>> ListAttachableAsync(
        Guid tenantId, Guid agentId, CancellationToken ct)
    {
        // Notes in this tenant that are NOT yet attached to this agent
        const string sql = """
            SELECT
                n.id         AS Id,
                n.title      AS Title,
                n.tags       AS Tags,
                n.updated_at AS UpdatedAt
            FROM notes n
            WHERE n.tenant_id = @tenantId
              AND NOT EXISTS (
                  SELECT 1 FROM agent_knowledge ak
                  WHERE ak.agent_id = @agentId AND ak.note_id = n.id
              )
            ORDER BY n.updated_at DESC
            LIMIT 500
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<NoteSummary>(
            new CommandDefinition(sql, new { tenantId, agentId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<bool> AttachAsync(
        Guid tenantId, Guid agentId, Guid noteId, Guid attachedBy, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
            SELECT @agentId, @noteId, @attachedBy
            WHERE EXISTS (SELECT 1 FROM agents WHERE id = @agentId AND tenant_id = @tenantId)
              AND EXISTS (SELECT 1 FROM notes WHERE id = @noteId AND tenant_id = @tenantId)
            ON CONFLICT (agent_id, note_id) DO NOTHING
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            sql, new { tenantId, agentId, noteId, attachedBy }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<bool> DetachAsync(Guid tenantId, Guid agentId, Guid noteId, CancellationToken ct)
    {
        const string sql = """
            DELETE FROM agent_knowledge
            WHERE agent_id = @agentId AND note_id = @noteId
              AND EXISTS (SELECT 1 FROM agents WHERE id = @agentId AND tenant_id = @tenantId)
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            sql, new { tenantId, agentId, noteId }, cancellationToken: ct));
        return n > 0;
    }
}
