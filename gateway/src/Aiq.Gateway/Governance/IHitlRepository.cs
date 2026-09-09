using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Governance;

public sealed record HitlGateRecord(
    Guid Id,
    Guid TenantId,
    Guid AgentId,
    string Name,
    string? Description,
    string TriggerRuleJson,
    string ApproverRole,
    bool IsActive,
    Guid? CreatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record HitlPendingActionRecord(
    Guid Id,
    Guid TenantId,
    Guid GateId,
    string GateName,
    Guid AgentId,
    string AgentCode,
    string AgentName,
    Guid? TriggeredBy,
    string? TriggeredByEmail,
    string ToolCode,
    string? ToolRiskTier,
    string AgentActionTier,
    string PayloadJson,
    string Status,
    Guid? DecidedBy,
    string? DecidedByEmail,
    DateTime? DecidedAt,
    string? DecisionComment,
    DateTime? ExpiresAt,
    DateTime CreatedAt);

public sealed record CreateGateRequest(
    [property: Required, StringLength(120, MinimumLength = 2)] string Name,
    string? Description,
    JsonElement? TriggerRule,
    [property: RegularExpression("^(owner|tenant_admin)$")] string? ApproverRole);

public sealed record UpdateGateRequest(
    string? Name,
    string? Description,
    JsonElement? TriggerRule,
    string? ApproverRole,
    bool? IsActive);

public sealed record CreatePendingActionRequest(
    [property: Required] Guid GateId,
    [property: Required, StringLength(120)] string ToolCode,
    [property: Required] JsonElement Payload,
    DateTime? ExpiresAt);

public sealed record DecideActionRequest(
    [property: Required, RegularExpression("^(approve|reject)$")] string Decision,
    string? Comment);

public enum HitlOutcome { Allowed, PendingApproval, Rejected }
public sealed record HitlEvaluationResult(HitlOutcome Outcome, Guid? ActionId, string? GateName);

/// <summary>
/// Matches a hitl_gates.trigger_rule against a real tool call. Shape:
/// {"tool": "code", "field_path": "amount", "op": "eq"|"gte"|"gt"|"lte"|"lt", "value": ...}.
/// "tool" and "field_path" are both optional — omitting "tool" matches any
/// tool this gate's row would otherwise apply to; omitting "field_path"
/// makes the gate fire on every call to the matched tool. Anything the rule
/// can't evaluate (missing field, unknown op, non-comparable types) is
/// treated as a MATCH — fail closed toward requiring approval, never toward
/// silently skipping a configured gate.
/// </summary>
public static class TriggerRuleEvaluator
{
    public static bool Matches(JsonElement rule, string toolCode, JsonElement payload)
    {
        if (rule.ValueKind != JsonValueKind.Object) return false;
        if (rule.TryGetProperty("tool", out var toolProp) && toolProp.ValueKind == JsonValueKind.String
            && !string.Equals(toolProp.GetString(), toolCode, StringComparison.Ordinal))
        {
            return false;
        }
        if (!rule.TryGetProperty("field_path", out var fieldProp) || fieldProp.ValueKind != JsonValueKind.String)
        {
            return true;
        }
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(fieldProp.GetString()!, out var actual))
        {
            return true;
        }
        if (!rule.TryGetProperty("op", out var opProp) || opProp.ValueKind != JsonValueKind.String
            || !rule.TryGetProperty("value", out var expected))
        {
            return true;
        }
        var op = opProp.GetString();
        if (actual.ValueKind == JsonValueKind.Number && expected.ValueKind == JsonValueKind.Number)
        {
            var a = actual.GetDouble();
            var e = expected.GetDouble();
            return op switch
            {
                "eq" => a == e,
                "gte" => a >= e,
                "gt" => a > e,
                "lte" => a <= e,
                "lt" => a < e,
                _ => true,
            };
        }
        var actualStr = actual.ValueKind == JsonValueKind.String ? actual.GetString() : actual.GetRawText();
        var expectedStr = expected.ValueKind == JsonValueKind.String ? expected.GetString() : expected.GetRawText();
        return op == "eq" ? actualStr == expectedStr : true;
    }
}

public interface IHitlRepository
{
    Task<IReadOnlyList<HitlGateRecord>> ListGatesAsync(Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct);
    Task<HitlGateRecord> CreateGateAsync(
        Guid tenantId, Guid departmentId, Guid agentId, Guid createdBy, CreateGateRequest request, CancellationToken ct);
    Task<HitlGateRecord?> UpdateGateAsync(Guid tenantId, Guid departmentId, Guid gateId, UpdateGateRequest request, CancellationToken ct);
    Task<bool> DeleteGateAsync(Guid tenantId, Guid departmentId, Guid gateId, CancellationToken ct);

    Task<HitlPendingActionRecord> CreatePendingActionAsync(
        Guid tenantId, Guid departmentId, Guid triggeredBy, CreatePendingActionRequest request, CancellationToken ct);

    Task<IReadOnlyList<HitlPendingActionRecord>> ListPendingForTenantAsync(
        Guid tenantId, Guid departmentId, CancellationToken ct);

    Task<HitlPendingActionRecord?> DecideAsync(
        Guid tenantId, Guid departmentId, Guid actionId, Guid deciderId, DecideActionRequest request, CancellationToken ct);

    Task<HitlPendingActionRecord?> FindActionAsync(Guid actionId, CancellationToken ct);

    /// <summary>
    /// The real-execution integration point (Phase 5D): evaluates a
    /// tenant+agent's active gates against one tool call. No gate matches ->
    /// Allowed. A gate matches and a prior APPROVED decision for this exact
    /// (tool, payload) exists -> consumes it, Allowed. A prior REJECTED
    /// decision exists -> Rejected. Otherwise creates a new pending action
    /// and returns PendingApproval — the tool must not execute either way.
    /// </summary>
    Task<HitlEvaluationResult> EvaluateAsync(Guid tenantId, Guid agentId, Guid actorId, string toolCode, JsonElement payload, CancellationToken ct);

    /// <summary>
    /// True if this tenant may manage gates for this agent — either a
    /// legacy tenant-owned agent, or a system agent actively assigned to
    /// this tenant (agents.tenant_id is NULL for system agents, so the
    /// legacy IAgentRepository.FindAsync tenant_id match alone can never
    /// see them — same class of gap fixed for knowledge in Phase 5A).
    /// </summary>
    Task<bool> IsAgentVisibleToTenantAsync(Guid tenantId, Guid agentId, CancellationToken ct);
    Task<bool> IsAgentVisibleToDepartmentAsync(
        Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct);
}

public sealed class HitlRepository : IHitlRepository
{
    private const string GateColumns = """
            id              AS Id,
            tenant_id       AS TenantId,
            agent_id        AS AgentId,
            name            AS Name,
            description     AS Description,
            trigger_rule::text AS TriggerRuleJson,
            approver_role   AS ApproverRole,
            is_active       AS IsActive,
            created_by      AS CreatedBy,
            created_at      AS CreatedAt,
            updated_at      AS UpdatedAt
        """;

    private const string ActionColumns = """
            p.id              AS Id,
            p.tenant_id       AS TenantId,
            p.gate_id         AS GateId,
            g.name            AS GateName,
            p.agent_id        AS AgentId,
            a.code            AS AgentCode,
            a.name            AS AgentName,
            p.triggered_by    AS TriggeredBy,
            ut.email          AS TriggeredByEmail,
            p.tool_code       AS ToolCode,
            (SELECT t.risk_tier FROM tools t WHERE t.code = p.tool_code LIMIT 1) AS ToolRiskTier,
            a.action_tier     AS AgentActionTier,
            p.payload::text   AS PayloadJson,
            p.status          AS Status,
            p.decided_by      AS DecidedBy,
            ud.email          AS DecidedByEmail,
            p.decided_at      AS DecidedAt,
            p.decision_comment AS DecisionComment,
            p.expires_at      AS ExpiresAt,
            p.created_at      AS CreatedAt
        """;

    private readonly NpgsqlConnectionFactory _factory;

    public HitlRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<HitlGateRecord>> ListGatesAsync(Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct)
    {
        var sql = $"SELECT {GateColumns} FROM hitl_gates WHERE tenant_id = @tenantId AND department_id = @departmentId AND agent_id = @agentId ORDER BY name";
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<HitlGateRecord>(
            new CommandDefinition(sql, new { tenantId, departmentId, agentId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<HitlGateRecord> CreateGateAsync(
        Guid tenantId, Guid departmentId, Guid agentId, Guid createdBy, CreateGateRequest request, CancellationToken ct)
    {
        var ruleJson = request.TriggerRule?.GetRawText() ?? "{}";
        var sql = $"""
            INSERT INTO hitl_gates
                (tenant_id, department_id, agent_id, name, description, trigger_rule, approver_role, created_by)
            VALUES
                (@tenantId, @departmentId, @agentId, @Name, @Description, @ruleJson::jsonb,
                 COALESCE(@ApproverRole, 'owner'), @createdBy)
            RETURNING {GateColumns}
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleAsync<HitlGateRecord>(
            new CommandDefinition(sql, new
            {
                tenantId, departmentId, agentId, createdBy,
                request.Name, request.Description, request.ApproverRole, ruleJson,
            }, cancellationToken: ct));
    }

    public async Task<HitlGateRecord?> UpdateGateAsync(Guid tenantId, Guid departmentId, Guid gateId, UpdateGateRequest request, CancellationToken ct)
    {
        var ruleJson = request.TriggerRule?.GetRawText();
        var sql = $"""
            UPDATE hitl_gates SET
                name          = COALESCE(@Name, name),
                description   = COALESCE(@Description, description),
                trigger_rule  = COALESCE(@ruleJson::jsonb, trigger_rule),
                approver_role = COALESCE(@ApproverRole, approver_role),
                is_active     = COALESCE(@IsActive, is_active)
            WHERE id = @gateId AND tenant_id = @tenantId AND department_id = @departmentId
            RETURNING {GateColumns}
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<HitlGateRecord>(
            new CommandDefinition(sql, new
            {
                tenantId, departmentId, gateId,
                request.Name, request.Description, request.ApproverRole, request.IsActive, ruleJson,
            }, cancellationToken: ct));
    }

    public async Task<bool> DeleteGateAsync(Guid tenantId, Guid departmentId, Guid gateId, CancellationToken ct)
    {
        const string sql = "DELETE FROM hitl_gates WHERE id = @gateId AND tenant_id = @tenantId AND department_id = @departmentId";
        await using var conn = await _factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(sql, new { tenantId, departmentId, gateId }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<HitlPendingActionRecord> CreatePendingActionAsync(
        Guid tenantId, Guid departmentId, Guid triggeredBy, CreatePendingActionRequest request, CancellationToken ct)
    {
        var payloadJson = request.Payload.GetRawText();
        const string insertSql = """
            INSERT INTO hitl_pending_actions
                (tenant_id, department_id, gate_id, agent_id, triggered_by, tool_code, payload, expires_at)
            SELECT
                @tenantId, @departmentId, g.id, g.agent_id, @triggeredBy, @ToolCode, @payloadJson::jsonb, @ExpiresAt
            FROM hitl_gates g
            WHERE g.id = @GateId AND g.tenant_id = @tenantId AND g.department_id = @departmentId
            RETURNING id
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var id = await conn.QuerySingleAsync<Guid>(
            new CommandDefinition(insertSql, new
            {
                tenantId, departmentId, triggeredBy, request.GateId, request.ToolCode, payloadJson, request.ExpiresAt,
            }, cancellationToken: ct));
        return (await FindActionAsync(id, ct))!;
    }

    public async Task<IReadOnlyList<HitlPendingActionRecord>> ListPendingForTenantAsync(
        Guid tenantId, Guid departmentId, CancellationToken ct)
    {
        var sql = $"""
            SELECT {ActionColumns}
            FROM hitl_pending_actions p
            JOIN hitl_gates g ON g.id = p.gate_id
            JOIN agents a ON a.id = p.agent_id
            LEFT JOIN users ut ON ut.id = p.triggered_by
            LEFT JOIN users ud ON ud.id = p.decided_by
            WHERE p.tenant_id = @tenantId AND p.department_id = @departmentId AND p.status = 'pending'
            ORDER BY p.created_at DESC
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<HitlPendingActionRecord>(
            new CommandDefinition(sql, new { tenantId, departmentId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<HitlPendingActionRecord?> DecideAsync(
        Guid tenantId, Guid departmentId, Guid actionId, Guid deciderId, DecideActionRequest request, CancellationToken ct)
    {
        var newStatus = request.Decision == "approve" ? "approved" : "rejected";
        const string sql = """
            UPDATE hitl_pending_actions
            SET status = @newStatus,
                decided_by = @deciderId,
                decided_at = NOW(),
                decision_comment = @Comment
            WHERE id = @actionId AND tenant_id = @tenantId AND department_id = @departmentId AND status = 'pending'
            RETURNING id
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var updated = await conn.QueryFirstOrDefaultAsync<Guid?>(
            new CommandDefinition(sql, new { tenantId, departmentId, actionId, deciderId, newStatus, request.Comment },
                cancellationToken: ct));
        if (updated is null) return null;
        return await FindActionAsync(actionId, ct);
    }

    public async Task<HitlPendingActionRecord?> FindActionAsync(Guid actionId, CancellationToken ct)
    {
        var sql = $"""
            SELECT {ActionColumns}
            FROM hitl_pending_actions p
            JOIN hitl_gates g ON g.id = p.gate_id
            JOIN agents a ON a.id = p.agent_id
            LEFT JOIN users ut ON ut.id = p.triggered_by
            LEFT JOIN users ud ON ud.id = p.decided_by
            WHERE p.id = @actionId
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<HitlPendingActionRecord>(
            new CommandDefinition(sql, new { actionId }, cancellationToken: ct));
    }

    public async Task<bool> IsAgentVisibleToTenantAsync(Guid tenantId, Guid agentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM agents WHERE id=@agentId AND tenant_id=@tenantId)
                OR EXISTS(SELECT 1 FROM agent_tenant_assignments WHERE agent_id=@agentId AND tenant_id=@tenantId AND status='active')
            """, new { tenantId, agentId }, cancellationToken: ct));
    }

    public async Task<bool> IsAgentVisibleToDepartmentAsync(
        Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(
                SELECT 1 FROM agents
                WHERE id=@agentId AND owning_tenant_id=@tenantId
                  AND department_id=@departmentId AND v2_scope='department'
                  AND v2_migration_status='resolved')
              OR EXISTS(
                SELECT 1
                FROM agent_department_assignments d
                JOIN agent_tenant_assignments t
                  ON t.agent_id=d.agent_id AND t.tenant_id=d.tenant_id AND t.status='active'
                WHERE d.agent_id=@agentId AND d.tenant_id=@tenantId
                  AND d.department_id=@departmentId AND d.status='active')
            """, new { tenantId, departmentId, agentId }, cancellationToken: ct));
    }

    private sealed record GateLookupRow(Guid Id, string Name, string TriggerRuleJson);
    private sealed record PriorDecisionRow(Guid Id, string Status);

    public async Task<HitlEvaluationResult> EvaluateAsync(Guid tenantId, Guid agentId, Guid actorId, string toolCode, JsonElement payload, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var gates = (await conn.QueryAsync<GateLookupRow>(new CommandDefinition(
            "SELECT id AS Id, name AS Name, trigger_rule::text AS TriggerRuleJson FROM hitl_gates WHERE tenant_id=@tenantId AND agent_id=@agentId AND is_active ORDER BY name",
            new { tenantId, agentId }, cancellationToken: ct))).AsList();

        GateLookupRow? matched = null;
        foreach (var g in gates)
        {
            using var doc = JsonDocument.Parse(g.TriggerRuleJson);
            if (TriggerRuleEvaluator.Matches(doc.RootElement, toolCode, payload))
            {
                matched = g;
                break;
            }
        }
        if (matched is null) return new(HitlOutcome.Allowed, null, null);

        var payloadJson = payload.GetRawText();
        var prior = await conn.QueryFirstOrDefaultAsync<PriorDecisionRow>(new CommandDefinition("""
            SELECT id AS Id, status AS Status FROM hitl_pending_actions
            WHERE tenant_id=@tenantId AND agent_id=@agentId AND gate_id=@gateId AND tool_code=@toolCode
              AND payload=@payloadJson::jsonb AND status IN ('approved','rejected') AND consumed_at IS NULL
            ORDER BY decided_at DESC LIMIT 1
            """, new { tenantId, agentId, gateId = matched.Id, toolCode, payloadJson }, cancellationToken: ct));

        if (prior is { Status: "approved" })
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE hitl_pending_actions SET consumed_at=NOW() WHERE id=@id",
                new { id = prior.Id }, cancellationToken: ct));
            return new(HitlOutcome.Allowed, prior.Id, matched.Name);
        }
        if (prior is { Status: "rejected" })
        {
            return new(HitlOutcome.Rejected, prior.Id, matched.Name);
        }

        var newId = await conn.QuerySingleAsync<Guid>(new CommandDefinition("""
            INSERT INTO hitl_pending_actions (tenant_id, gate_id, agent_id, triggered_by, tool_code, payload)
            VALUES (@tenantId, @gateId, @agentId, @actorId, @toolCode, @payloadJson::jsonb)
            RETURNING id
            """, new { tenantId, gateId = matched.Id, agentId, actorId, toolCode, payloadJson }, cancellationToken: ct));
        return new(HitlOutcome.PendingApproval, newId, matched.Name);
    }
}
