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
    string? PrincipalEmail,
    string? PrincipalDisplayName,
    string AccessLevel,
    Guid? GrantedBy,
    string? GrantedByEmail,
    DateTime GrantedAt);

public sealed record CreateAgentRequest(
    [property: Required, RegularExpression("^[a-z0-9-]+$"), StringLength(80, MinimumLength = 2)] string Code,
    [property: Required, StringLength(160)] string Name,
    [property: Required, RegularExpression("^(orchestrator|domain|sub|tool_only)$")] string Type,
    string? Description,
    [property: RegularExpression("^(read_only|suggest|execute_with_approval|autonomous)$")] string? ActionTier,
    string? SponsorTeam,
    string? BaselineRole,
    JsonElement? Config);

public sealed record UpdateAgentRequest(
    string? Name,
    string? Description,
    [property: RegularExpression("^(read_only|suggest|execute_with_approval|autonomous)$")] string? ActionTier,
    string? SponsorTeam,
    string? BaselineRole,
    JsonElement? Config,
    Guid? OwnerUserId);

public sealed record TransitionStateRequest(
    [property: Required, RegularExpression("^(draft|testing|pending_review|production|deprecated|retired)$")] string ToState,
    string? Reason);

public sealed record DisableAgentRequest(string? Reason);

public sealed record GrantAclRequest(
    [property: Required] Guid PrincipalId,
    [property: RegularExpression("^(user|group)$")] string? PrincipalType,
    [property: Required, RegularExpression("^(owner|editor|user)$")] string AccessLevel);

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

    /// <summary>
    /// Returns the agents in the tenant that the given principal has any
    /// ACL grant on, along with the grant's access level. Used by the
    /// "user picks an agent to use" flow and the admin "assign agents"
    /// screen. Only agents in lifecycle 'production' or 'testing' are
    /// returned when the caller is not owner/editor.
    /// </summary>
    Task<IReadOnlyList<PrincipalAgentRecord>> ListForPrincipalAsync(
        Guid tenantId,
        Guid principalId,
        CancellationToken ct);

    /// <summary>
    /// D3: the ADMIN (department manager) equivalent of ListForPrincipalAsync
    /// — every agent actively entitled to BOTH the tenant and this specific
    /// department, deliberately WITHOUT a direct agent_assignments row
    /// requirement (an admin executes via department entitlement alone, see
    /// IDynamicAgentRepository.GetAdminManifestAsync). Same
    /// PrincipalAgentRecord shape as the USER path so /me/agents and its Web
    /// consumers (Chat's authorization gate, the "My Agents" list) need no
    /// per-role branching beyond which repository method gets called.
    /// </summary>
    Task<IReadOnlyList<PrincipalAgentRecord>> ListForDepartmentManagerAsync(
        Guid tenantId,
        Guid departmentId,
        CancellationToken ct);
}

public sealed record PrincipalAgentRecord(
    Guid AgentId,
    string AgentCode,
    string AgentName,
    string? Description,
    string AgentType,
    string LifecycleState,
    int? PublishedVersion,
    string ActionTier,
    bool AgentDisabled,
    string AccessLevel);

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
    private readonly IReviewBoardRepository? _reviews;

    public AgentRepository(NpgsqlConnectionFactory factory, IReviewBoardRepository? reviews = null)
    {
        _factory = factory;
        _reviews = reviews;
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

        // G3 review-board guard: production promotion needs an approved
        // agent_review_request. Skip when review repo is unavailable (legacy
        // bootstrap) so the system stays bootable.
        if (current.LifecycleState == "pending_review"
            && request.ToState == "production"
            && _reviews is not null)
        {
            var approved = await _reviews.HasApprovedRequestAsync(agentId, "production", ct);
            if (!approved)
            {
                throw new InvalidOperationException(
                    "Promotion to production requires an approved review request.");
            }
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
        // Enrich with principal + grantor email/name so the admin UI shows
        // human-readable identities instead of raw UUIDs.
        const string sql = """
            SELECT
                acl.agent_id          AS AgentId,
                acl.principal_id      AS PrincipalId,
                acl.principal_type    AS PrincipalType,
                pu.email              AS PrincipalEmail,
                pu.display_name       AS PrincipalDisplayName,
                acl.access_level      AS AccessLevel,
                acl.granted_by        AS GrantedBy,
                gu.email              AS GrantedByEmail,
                acl.granted_at        AS GrantedAt
            FROM agent_acl acl
            LEFT JOIN users pu ON pu.id = acl.principal_id AND acl.principal_type = 'user'
            LEFT JOIN users gu ON gu.id = acl.granted_by
            WHERE acl.agent_id = @agentId
            ORDER BY acl.granted_at
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
        await using var tx = await conn.BeginTransactionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            agentId,
            request.PrincipalId,
            request.PrincipalType,
            request.AccessLevel,
            actorUserId
        }, transaction: tx, cancellationToken: ct));
        if ((request.PrincipalType is null or "user") && request.AccessLevel == "user")
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO agent_assignments (tenant_id, agent_id, user_id, assigned_by, status)
                SELECT a.tenant_id, a.id, @PrincipalId, @actorUserId, 'active'
                FROM agents a WHERE a.id = @agentId
                ON CONFLICT (tenant_id, agent_id, user_id)
                DO UPDATE SET status = 'active', assigned_by = EXCLUDED.assigned_by, revoked_at = NULL
                """, new { agentId, request.PrincipalId, actorUserId }, transaction: tx, cancellationToken: ct));
        }
        else if (request.PrincipalType is null or "user")
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE agent_assignments SET status = 'revoked', revoked_at = NOW()
                WHERE agent_id = @agentId AND user_id = @PrincipalId AND status = 'active'
                """, new { agentId, request.PrincipalId }, transaction: tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
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
        await using var tx = await conn.BeginTransactionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(sql, new { agentId, principalId, principalType }, transaction: tx, cancellationToken: ct));
        if (principalType == "user")
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE agent_assignments SET status = 'revoked', revoked_at = NOW()
                WHERE agent_id = @agentId AND user_id = @principalId AND status = 'active'
                """, new { agentId, principalId }, transaction: tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
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

    public async Task<IReadOnlyList<PrincipalAgentRecord>> ListForPrincipalAsync(
        Guid tenantId, Guid principalId, CancellationToken ct)
    {
        // Two independent entitlement chains, either of which makes a direct
        // agent_assignments row visible:
        //
        // 1. Legacy scope='system' platform agent — D3 canonical USER chain:
        //    tenant entitlement -> department assignment (matching the
        //    assigned user's OWN department) -> direct user assignment, all
        //    active. Same chain IDynamicAgentRepository.GetManifestAsync
        //    enforces at execution time, so a stale direct assignment left
        //    over after a department entitlement is revoked still doesn't
        //    make the agent visible.
        //
        // 2. V2-C Department Agent (agents.v2_scope='department') — ownership
        //    is structural on the agent row itself (owning_tenant_id,
        //    department_id set at creation; see
        //    IDynamicAgentRepository.CreateDepartmentAgentAsync). Such an
        //    agent can never acquire an agent_tenant_assignments or
        //    agent_department_assignments row (migration 007/024's ownership
        //    constraints make the two mutually exclusive with scope='system'
        //    rows), so requiring them would make every Department Agent
        //    permanently invisible to its own directly-assigned USERs. This
        //    mirrors the escape hatch migration 025 already added to
        //    enforce_department_capability_binding_eligibility() for the
        //    exact same structural reason. Still requires: same tenant, same
        //    department as the assigned user, and the active direct
        //    agent_assignments row from chain 1.
        const string sql = """
            SELECT
                a.id              AS AgentId,
                a.code            AS AgentCode,
                a.name            AS AgentName,
                a.description     AS Description,
                a.type            AS AgentType,
                a.lifecycle_state AS LifecycleState,
                version.version        AS PublishedVersion,
                a.action_tier     AS ActionTier,
                (a.disabled_at IS NOT NULL) AS AgentDisabled,
                'user'            AS AccessLevel
            FROM agent_assignments assignment
            JOIN agents a ON a.id = assignment.agent_id
            JOIN users principal_user
              ON principal_user.id = assignment.user_id
             AND principal_user.tenant_id = assignment.tenant_id
            LEFT JOIN agent_tenant_assignments tenant_assignment
              ON tenant_assignment.agent_id = a.id
             AND tenant_assignment.tenant_id = assignment.tenant_id
             AND tenant_assignment.status = 'active'
            LEFT JOIN agent_department_assignments department_assignment
              ON department_assignment.agent_id = a.id
             AND department_assignment.tenant_id = assignment.tenant_id
             AND department_assignment.department_id = principal_user.department_id
             AND department_assignment.status = 'active'
            LEFT JOIN agent_versions version ON version.id = a.published_version_id
            WHERE assignment.tenant_id = @tenantId
              AND assignment.user_id = @principalId
              AND assignment.status = 'active'
              AND (
                (tenant_assignment.agent_id IS NOT NULL AND department_assignment.agent_id IS NOT NULL)
                OR (a.v2_scope = 'department'
                    AND a.owning_tenant_id = assignment.tenant_id
                    AND a.department_id = principal_user.department_id)
              )
            ORDER BY a.name
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<PrincipalAgentRecord>(
            new CommandDefinition(sql, new { tenantId, principalId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<PrincipalAgentRecord>> ListForDepartmentManagerAsync(
        Guid tenantId, Guid departmentId, CancellationToken ct)
    {
        // ACL V2 review: this used to be chain 1 only (legacy scope='system'
        // platform agent entitled to the department via
        // agent_department_assignments) — an ADMIN who creates and publishes
        // their OWN Department Agent (v2_scope='department', ownership
        // structural on the agent row, never an agent_department_assignments
        // row — see ListForPrincipalAsync's chain 2 comment above) never got
        // an entitlement row here, so it silently never appeared in that
        // ADMIN's own My Agents/sidebar even while production+enabled and
        // fully executable via GetDepartmentAgentManifestAsync(isAdmin:
        // true). Same UNION shape as ListForPrincipalAsync's two chains, so
        // sidebar visibility and runtime authorization agree for ADMIN too.
        const string sql = """
            SELECT
                a.id              AS AgentId,
                a.code            AS AgentCode,
                a.name            AS AgentName,
                a.description     AS Description,
                a.type            AS AgentType,
                a.lifecycle_state AS LifecycleState,
                version.version        AS PublishedVersion,
                a.action_tier     AS ActionTier,
                (a.disabled_at IS NOT NULL) AS AgentDisabled,
                'department'      AS AccessLevel
            FROM agent_department_assignments department_assignment
            JOIN agents a ON a.id = department_assignment.agent_id
            JOIN agent_tenant_assignments tenant_assignment
              ON tenant_assignment.agent_id = a.id
             AND tenant_assignment.tenant_id = department_assignment.tenant_id
             AND tenant_assignment.status = 'active'
            LEFT JOIN agent_versions version ON version.id = a.published_version_id
            WHERE department_assignment.tenant_id = @tenantId
              AND department_assignment.department_id = @departmentId
              AND department_assignment.status = 'active'

            UNION

            SELECT
                a.id              AS AgentId,
                a.code            AS AgentCode,
                a.name            AS AgentName,
                a.description     AS Description,
                a.type            AS AgentType,
                a.lifecycle_state AS LifecycleState,
                version.version        AS PublishedVersion,
                a.action_tier     AS ActionTier,
                (a.disabled_at IS NOT NULL) AS AgentDisabled,
                'department'      AS AccessLevel
            FROM agents a
            LEFT JOIN agent_versions version ON version.id = a.published_version_id
            WHERE a.v2_scope = 'department'
              AND a.owning_tenant_id = @tenantId
              AND a.department_id = @departmentId

            ORDER BY AgentName
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<PrincipalAgentRecord>(
            new CommandDefinition(sql, new { tenantId, departmentId }, cancellationToken: ct));
        return rows.AsList();
    }
}
