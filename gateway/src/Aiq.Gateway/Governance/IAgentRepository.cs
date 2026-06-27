using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Governance;

public sealed record AgentRecord(
    Guid Id,
    Guid TenantId,
    string Code,
    string Name,
    string Type,
    string? Description,
    string LifecycleState,
    Guid? OwnerUserId,
    string? SponsorTeam,
    string? BaselineRole,
    string ActionTier,
    string ConfigJson,
    DateTime? DisabledAt,
    Guid? DisabledBy,
    string? DisabledReason,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record AgentLifecycleEvent(
    Guid Id,
    Guid AgentId,
    string? FromState,
    string ToState,
    Guid? TransitionedBy,
    string? Reason,
    DateTime OccurredAt);

public sealed record AgentAclRecord(
    Guid AgentId,
    Guid PrincipalId,
    string PrincipalType,
    string AccessLevel,
    Guid? GrantedBy,
    DateTime GrantedAt);

public sealed record CreateAgentRequest(
    [Required, RegularExpression("^[a-z0-9-]+$"), StringLength(80, MinimumLength = 2)] string Code,
    [Required, StringLength(160)] string Name,
    [Required, RegularExpression("^(orchestrator|domain|sub|tool_only)$")] string Type,
    string? Description,
    [RegularExpression("^(read_only|suggest|execute_with_approval|autonomous)$")] string? ActionTier,
    string? SponsorTeam,
    string? BaselineRole,
    JsonElement? Config);

public sealed record UpdateAgentRequest(
    string? Name,
    string? Description,
    [RegularExpression("^(read_only|suggest|execute_with_approval|autonomous)$")] string? ActionTier,
    string? SponsorTeam,
    string? BaselineRole,
    JsonElement? Config,
    Guid? OwnerUserId);

public sealed record TransitionStateRequest(
    [Required, RegularExpression("^(draft|testing|pending_review|production|deprecated|retired)$")] string ToState,
    string? Reason);

public sealed record DisableAgentRequest(string? Reason);

public sealed record GrantAclRequest(
    [Required] Guid PrincipalId,
    [RegularExpression("^(user|group)$")] string? PrincipalType,
    [Required, RegularExpression("^(owner|editor|user)$")] string AccessLevel);

public interface IAgentRepository
{
    Task<IReadOnlyList<AgentRecord>> ListAsync(
        Guid tenantId,
        string? lifecycleState,
        CancellationToken ct);

    Task<AgentRecord?> FindAsync(Guid tenantId, Guid agentId, CancellationToken ct);
    Task<AgentRecord?> FindByCodeAsync(Guid tenantId, string code, CancellationToken ct);

    Task<AgentRecord> CreateAsync(
        Guid tenantId,
        Guid ownerUserId,
        CreateAgentRequest request,
        CancellationToken ct);

    Task<AgentRecord?> UpdateAsync(
        Guid tenantId,
        Guid agentId,
        UpdateAgentRequest request,
        CancellationToken ct);

    Task<AgentRecord?> TransitionStateAsync(
        Guid tenantId,
        Guid agentId,
        Guid actorUserId,
        TransitionStateRequest request,
        CancellationToken ct);

    Task<AgentRecord?> DisableAsync(
        Guid tenantId,
        Guid agentId,
        Guid actorUserId,
        DisableAgentRequest request,
        CancellationToken ct);

    Task<AgentRecord?> EnableAsync(Guid tenantId, Guid agentId, CancellationToken ct);

    Task<IReadOnlyList<AgentLifecycleEvent>> ListLifecycleEventsAsync(Guid agentId, CancellationToken ct);

    Task<IReadOnlyList<AgentAclRecord>> ListAclAsync(Guid agentId, CancellationToken ct);

    Task<bool> GrantAclAsync(
        Guid agentId,
        Guid actorUserId,
        GrantAclRequest request,
        CancellationToken ct);

    Task<bool> RevokeAclAsync(
        Guid agentId,
        Guid principalId,
        string principalType,
        CancellationToken ct);

    /// <summary>
    /// Returns the access level (owner/editor/user) the given principal has on
    /// the agent, or null if no grant exists. Used by middleware authz.
    /// </summary>
    Task<string?> GetAccessLevelAsync(
        Guid agentId,
        Guid principalId,
        CancellationToken ct);
}

public sealed class AgentRepository : IAgentRepository
{
    private const string SelectColumns = """
            id              AS Id,
            tenant_id       AS TenantId,
            code            AS Code,
            name            AS Name,
            type            AS Type,
            description     AS Description,
            lifecycle_state AS LifecycleState,
            owner_user_id   AS OwnerUserId,
            sponsor_team    AS SponsorTeam,
            baseline_role   AS BaselineRole,
            action_tier     AS ActionTier,
            config::text    AS ConfigJson,
            disabled_at     AS DisabledAt,
            disabled_by     AS DisabledBy,
            disabled_reason AS DisabledReason,
            created_at      AS CreatedAt,
            updated_at      AS UpdatedAt
        """;

    private static readonly Dictionary<string, HashSet<string>> AllowedTransitions = new()
    {
        ["draft"]          = ["testing", "retired"],
        ["testing"]        = ["pending_review", "draft", "retired"],
        ["pending_review"] = ["production", "testing", "retired"], // production guarded by G3 review approval
        ["production"]     = ["deprecated"],
        ["deprecated"]     = ["retired", "production"],
        ["retired"]        = []
    };

    private readonly NpgsqlConnectionFactory _factory;

    public AgentRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<AgentRecord>> ListAsync(
        Guid tenantId, string? lifecycleState, CancellationToken ct)
    {
        var sql = $"""
            SELECT {SelectColumns}
            FROM agents
            WHERE tenant_id = @tenantId
              {(string.IsNullOrEmpty(lifecycleState) ? "" : "AND lifecycle_state = @lifecycleState")}
            ORDER BY created_at DESC
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<AgentRecord>(
            new CommandDefinition(sql, new { tenantId, lifecycleState }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<AgentRecord?> FindAsync(Guid tenantId, Guid agentId, CancellationToken ct)
    {
        var sql = $"SELECT {SelectColumns} FROM agents WHERE tenant_id = @tenantId AND id = @agentId";
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<AgentRecord>(
            new CommandDefinition(sql, new { tenantId, agentId }, cancellationToken: ct));
    }

    public async Task<AgentRecord?> FindByCodeAsync(Guid tenantId, string code, CancellationToken ct)
    {
        var sql = $"SELECT {SelectColumns} FROM agents WHERE tenant_id = @tenantId AND code = @code";
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<AgentRecord>(
            new CommandDefinition(sql, new { tenantId, code }, cancellationToken: ct));
    }

    public async Task<AgentRecord> CreateAsync(
        Guid tenantId, Guid ownerUserId, CreateAgentRequest request, CancellationToken ct)
    {
        var sql = $"""
            INSERT INTO agents (
                tenant_id, code, name, type, description,
                action_tier, sponsor_team, baseline_role, config, owner_user_id
            )
            VALUES (
                @tenantId, @Code, @Name, @Type, @Description,
                COALESCE(@ActionTier, 'suggest'),
                @SponsorTeam, @BaselineRole,
                @configJson::jsonb,
                @ownerUserId
            )
            RETURNING {SelectColumns}
            """;

        var configJson = request.Config?.GetRawText() ?? "{}";

        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var agent = await conn.QuerySingleAsync<AgentRecord>(
            new CommandDefinition(sql, new
            {
                tenantId,
                request.Code,
                request.Name,
                request.Type,
                request.Description,
                request.ActionTier,
                request.SponsorTeam,
                request.BaselineRole,
                configJson,
                ownerUserId
            }, transaction: tx, cancellationToken: ct));

        // Auto-grant owner ACL
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO agent_acl (agent_id, principal_id, principal_type, access_level, granted_by)
            VALUES (@agentId, @ownerUserId, 'user', 'owner', @ownerUserId)
            ON CONFLICT (agent_id, principal_id, principal_type) DO NOTHING
            """, new { agentId = agent.Id, ownerUserId }, transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return agent;
    }

    public async Task<AgentRecord?> UpdateAsync(
        Guid tenantId, Guid agentId, UpdateAgentRequest request, CancellationToken ct)
    {
        var configJson = request.Config?.GetRawText();
        var sql = $"""
            UPDATE agents SET
                name            = COALESCE(@Name, name),
                description     = COALESCE(@Description, description),
                action_tier     = COALESCE(@ActionTier, action_tier),
                sponsor_team    = COALESCE(@SponsorTeam, sponsor_team),
                baseline_role   = COALESCE(@BaselineRole, baseline_role),
                config          = COALESCE(@configJson::jsonb, config),
                owner_user_id   = COALESCE(@OwnerUserId, owner_user_id)
            WHERE tenant_id = @tenantId AND id = @agentId
            RETURNING {SelectColumns}
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<AgentRecord>(
            new CommandDefinition(sql, new
            {
                tenantId,
                agentId,
                request.Name,
                request.Description,
                request.ActionTier,
                request.SponsorTeam,
                request.BaselineRole,
                configJson,
                request.OwnerUserId
            }, cancellationToken: ct));
    }

    public async Task<AgentRecord?> TransitionStateAsync(
        Guid tenantId,
        Guid agentId,
        Guid actorUserId,
        TransitionStateRequest request,
        CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var current = await conn.QueryFirstOrDefaultAsync<AgentRecord>(
            new CommandDefinition($"SELECT {SelectColumns} FROM agents WHERE tenant_id = @tenantId AND id = @agentId FOR UPDATE",
                new { tenantId, agentId }, transaction: tx, cancellationToken: ct));
        if (current is null) return null;

        // State machine guard
        if (!AllowedTransitions.TryGetValue(current.LifecycleState, out var allowed)
            || !allowed.Contains(request.ToState))
        {
            throw new InvalidOperationException(
                $"Transition {current.LifecycleState} → {request.ToState} not allowed.");
        }

        var updated = await conn.QuerySingleAsync<AgentRecord>(
            new CommandDefinition($"""
                UPDATE agents
                SET lifecycle_state = @toState
                WHERE id = @agentId
                RETURNING {SelectColumns}
                """,
                new { toState = request.ToState, agentId },
                transaction: tx, cancellationToken: ct));

        // Override the trigger-inserted event with actor info + reason
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE agent_lifecycle_events
            SET transitioned_by = @actorUserId, reason = @reason
            WHERE id = (
                SELECT id FROM agent_lifecycle_events
                WHERE agent_id = @agentId
                ORDER BY occurred_at DESC
                LIMIT 1
            )
            """, new { agentId, actorUserId, reason = request.Reason }, transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return updated;
    }

    public async Task<AgentRecord?> DisableAsync(
        Guid tenantId, Guid agentId, Guid actorUserId, DisableAgentRequest request, CancellationToken ct)
    {
        var sql = $"""
            UPDATE agents SET
                disabled_at     = NOW(),
                disabled_by     = @actorUserId,
                disabled_reason = @reason
            WHERE tenant_id = @tenantId AND id = @agentId AND disabled_at IS NULL
            RETURNING {SelectColumns}
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<AgentRecord>(
            new CommandDefinition(sql, new { tenantId, agentId, actorUserId, reason = request.Reason }, cancellationToken: ct));
    }

    public async Task<AgentRecord?> EnableAsync(Guid tenantId, Guid agentId, CancellationToken ct)
    {
        var sql = $"""
            UPDATE agents SET
                disabled_at = NULL, disabled_by = NULL, disabled_reason = NULL
            WHERE tenant_id = @tenantId AND id = @agentId
            RETURNING {SelectColumns}
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<AgentRecord>(
            new CommandDefinition(sql, new { tenantId, agentId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AgentLifecycleEvent>> ListLifecycleEventsAsync(Guid agentId, CancellationToken ct)
    {
        const string sql = """
            SELECT
                id               AS Id,
                agent_id         AS AgentId,
                from_state       AS FromState,
                to_state         AS ToState,
                transitioned_by  AS TransitionedBy,
                reason           AS Reason,
                occurred_at      AS OccurredAt
            FROM agent_lifecycle_events
            WHERE agent_id = @agentId
            ORDER BY occurred_at DESC
            LIMIT 200
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<AgentLifecycleEvent>(
            new CommandDefinition(sql, new { agentId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<AgentAclRecord>> ListAclAsync(Guid agentId, CancellationToken ct)
    {
        const string sql = """
            SELECT
                agent_id        AS AgentId,
                principal_id    AS PrincipalId,
                principal_type  AS PrincipalType,
                access_level    AS AccessLevel,
                granted_by      AS GrantedBy,
                granted_at      AS GrantedAt
            FROM agent_acl
            WHERE agent_id = @agentId
            ORDER BY granted_at
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<AgentAclRecord>(
            new CommandDefinition(sql, new { agentId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<bool> GrantAclAsync(
        Guid agentId, Guid actorUserId, GrantAclRequest request, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO agent_acl (agent_id, principal_id, principal_type, access_level, granted_by)
            VALUES (@agentId, @PrincipalId, COALESCE(@PrincipalType, 'user'), @AccessLevel, @actorUserId)
            ON CONFLICT (agent_id, principal_id, principal_type)
            DO UPDATE SET access_level = EXCLUDED.access_level, granted_by = EXCLUDED.granted_by
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            agentId,
            request.PrincipalId,
            request.PrincipalType,
            request.AccessLevel,
            actorUserId
        }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<bool> RevokeAclAsync(
        Guid agentId, Guid principalId, string principalType, CancellationToken ct)
    {
        const string sql = """
            DELETE FROM agent_acl
            WHERE agent_id = @agentId
              AND principal_id = @principalId
              AND principal_type = @principalType
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(sql, new { agentId, principalId, principalType }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<string?> GetAccessLevelAsync(Guid agentId, Guid principalId, CancellationToken ct)
    {
        const string sql = """
            SELECT access_level
            FROM agent_acl
            WHERE agent_id = @agentId
              AND principal_id = @principalId
              AND principal_type = 'user'
            LIMIT 1
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(
            new CommandDefinition(sql, new { agentId, principalId }, cancellationToken: ct));
    }
}
