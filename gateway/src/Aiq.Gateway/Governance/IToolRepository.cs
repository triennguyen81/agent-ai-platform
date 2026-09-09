using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Governance;

public sealed record ToolRecord(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    string? Category,
    string RiskTier,
    string MinActionTier,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record AgentToolGrantRecord(
    Guid AgentId,
    Guid ToolId,
    string ToolCode,
    string ToolName,
    string RiskTier,
    string MinActionTier,
    DateTime GrantedAt);

public sealed record CreateToolRequest(
    [property: Required, RegularExpression("^[a-z0-9_]+$"), StringLength(120, MinimumLength = 3)] string Code,
    [property: Required, StringLength(160)] string Name,
    string? Description,
    [property: RegularExpression("^(read|write|external)$")] string? Category,
    [property: RegularExpression("^(low|medium|high|critical)$")] string? RiskTier,
    [property: RegularExpression("^(read_only|suggest|execute_with_approval|autonomous)$")] string? MinActionTier);

public sealed record UpdateToolRequest(
    string? Name,
    string? Description,
    string? Category,
    [property: RegularExpression("^(low|medium|high|critical)$")] string? RiskTier,
    [property: RegularExpression("^(read_only|suggest|execute_with_approval|autonomous)$")] string? MinActionTier,
    bool? IsActive);

public sealed record GrantToolRequest([property: Required] Guid ToolId);

public interface IToolRepository
{
    Task<IReadOnlyList<ToolRecord>> ListAsync(bool activeOnly, CancellationToken ct);
    Task<ToolRecord?> FindAsync(Guid id, CancellationToken ct);
    Task<ToolRecord> CreateAsync(CreateToolRequest request, CancellationToken ct);
    Task<ToolRecord?> UpdateAsync(Guid id, UpdateToolRequest request, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<AgentToolGrantRecord>> ListGrantsAsync(Guid agentId, CancellationToken ct);
    Task<bool> GrantAsync(Guid agentId, Guid toolId, Guid grantedBy, CancellationToken ct);
    Task<bool> RevokeAsync(Guid agentId, Guid toolId, CancellationToken ct);

    /// <summary>
    /// Returns the codes of tools the agent is allowed to call, filtered by
    /// the agent's action_tier (tier must be ≥ tool.min_action_tier) and
    /// the tool's is_active flag. Used by the agent runtime when building
    /// the allowed_tools list.
    /// </summary>
    Task<IReadOnlyList<string>> ListAllowedToolCodesAsync(Guid agentId, CancellationToken ct);
}

public sealed class ToolRepository : IToolRepository
{
    private const string ToolColumns = """
            id              AS Id,
            code            AS Code,
            name            AS Name,
            description     AS Description,
            category        AS Category,
            risk_tier       AS RiskTier,
            min_action_tier AS MinActionTier,
            is_active       AS IsActive,
            created_at      AS CreatedAt,
            updated_at      AS UpdatedAt
        """;

    // Action tier ordering: higher index = more permissive
    private static readonly string[] TierOrder =
        ["read_only", "suggest", "execute_with_approval", "autonomous"];

    private static int TierLevel(string t) => Array.IndexOf(TierOrder, t);

    private readonly NpgsqlConnectionFactory _factory;

    public ToolRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<ToolRecord>> ListAsync(bool activeOnly, CancellationToken ct)
    {
        var sql = $"""
            SELECT {ToolColumns}
            FROM tools
            {(activeOnly ? "WHERE is_active = TRUE" : "")}
            ORDER BY risk_tier, code
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<ToolRecord>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<ToolRecord?> FindAsync(Guid id, CancellationToken ct)
    {
        var sql = $"SELECT {ToolColumns} FROM tools WHERE id = @id";
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<ToolRecord>(
            new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<ToolRecord> CreateAsync(CreateToolRequest request, CancellationToken ct)
    {
        var sql = $"""
            INSERT INTO tools (code, name, description, category, risk_tier, min_action_tier)
            VALUES (@Code, @Name, @Description, @Category,
                    COALESCE(@RiskTier, 'low'),
                    COALESCE(@MinActionTier, 'suggest'))
            RETURNING {ToolColumns}
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleAsync<ToolRecord>(
            new CommandDefinition(sql, request, cancellationToken: ct));
    }

    public async Task<ToolRecord?> UpdateAsync(Guid id, UpdateToolRequest request, CancellationToken ct)
    {
        var sql = $"""
            UPDATE tools SET
                name            = COALESCE(@Name, name),
                description     = COALESCE(@Description, description),
                category        = COALESCE(@Category, category),
                risk_tier       = COALESCE(@RiskTier, risk_tier),
                min_action_tier = COALESCE(@MinActionTier, min_action_tier),
                is_active       = COALESCE(@IsActive, is_active)
            WHERE id = @id
            RETURNING {ToolColumns}
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<ToolRecord>(
            new CommandDefinition(sql, new
            {
                id,
                request.Name,
                request.Description,
                request.Category,
                request.RiskTier,
                request.MinActionTier,
                request.IsActive,
            }, cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        const string sql = "DELETE FROM tools WHERE id = @id";
        await using var conn = await _factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<IReadOnlyList<AgentToolGrantRecord>> ListGrantsAsync(Guid agentId, CancellationToken ct)
    {
        const string sql = """
            SELECT
                g.agent_id        AS AgentId,
                g.tool_id         AS ToolId,
                t.code            AS ToolCode,
                t.name            AS ToolName,
                t.risk_tier       AS RiskTier,
                t.min_action_tier AS MinActionTier,
                g.granted_at      AS GrantedAt
            FROM agent_tool_grants g
            JOIN tools t ON t.id = g.tool_id
            WHERE g.agent_id = @agentId
            ORDER BY t.risk_tier, t.code
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<AgentToolGrantRecord>(
            new CommandDefinition(sql, new { agentId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<bool> GrantAsync(Guid agentId, Guid toolId, Guid grantedBy, CancellationToken ct)
    {
        // Tier guard: agent.action_tier must be ≥ tool.min_action_tier.
        // We enforce in code so the error message is clear; could also be a DB CHECK.
        const string lookupSql = """
            SELECT a.action_tier AS AgentTier, t.min_action_tier AS ToolTier
            FROM agents a, tools t
            WHERE a.id = @agentId AND t.id = @toolId
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var pair = await conn.QueryFirstOrDefaultAsync<(string AgentTier, string ToolTier)>(
            new CommandDefinition(lookupSql, new { agentId, toolId }, cancellationToken: ct));
        if (pair.AgentTier is null) return false;

        if (TierLevel(pair.AgentTier) < TierLevel(pair.ToolTier))
        {
            throw new InvalidOperationException(
                $"Agent tier '{pair.AgentTier}' is below tool's required '{pair.ToolTier}'.");
        }

        const string sql = """
            INSERT INTO agent_tool_grants (agent_id, tool_id, granted_by)
            VALUES (@agentId, @toolId, @grantedBy)
            ON CONFLICT (agent_id, tool_id) DO NOTHING
            """;
        var n = await conn.ExecuteAsync(
            new CommandDefinition(sql, new { agentId, toolId, grantedBy }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<bool> RevokeAsync(Guid agentId, Guid toolId, CancellationToken ct)
    {
        const string sql = "DELETE FROM agent_tool_grants WHERE agent_id = @agentId AND tool_id = @toolId";
        await using var conn = await _factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(
            new CommandDefinition(sql, new { agentId, toolId }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<IReadOnlyList<string>> ListAllowedToolCodesAsync(Guid agentId, CancellationToken ct)
    {
        const string sql = """
            SELECT t.code
            FROM agent_tool_grants g
            JOIN tools t ON t.id = g.tool_id
            JOIN agents a ON a.id = g.agent_id
            WHERE g.agent_id = @agentId
              AND t.is_active = TRUE
              AND a.disabled_at IS NULL
              AND ARRAY_POSITION(ARRAY['read_only','suggest','execute_with_approval','autonomous'], a.action_tier)
                >= ARRAY_POSITION(ARRAY['read_only','suggest','execute_with_approval','autonomous'], t.min_action_tier)
            ORDER BY t.code
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<string>(
            new CommandDefinition(sql, new { agentId }, cancellationToken: ct));
        return rows.AsList();
    }
}
