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
    string PayloadJson,
    string Status,
    Guid? DecidedBy,
    string? DecidedByEmail,
    DateTime? DecidedAt,
    string? DecisionComment,
    DateTime? ExpiresAt,
    DateTime CreatedAt);

public sealed record CreateGateRequest(
    [Required, StringLength(120, MinimumLength = 2)] string Name,
    string? Description,
    JsonElement? TriggerRule,
    [RegularExpression("^(owner|tenant_admin)$")] string? ApproverRole);

public sealed record UpdateGateRequest(
    string? Name,
    string? Description,
    JsonElement? TriggerRule,
    string? ApproverRole,
    bool? IsActive);

public sealed record CreatePendingActionRequest(
    [Required] Guid GateId,
    [Required, StringLength(120)] string ToolCode,
    [Required] JsonElement Payload,
    DateTime? ExpiresAt);

public sealed record DecideActionRequest(
    [Required, RegularExpression("^(approve|reject)$")] string Decision,
    string? Comment);

public interface IHitlRepository
{
    Task<IReadOnlyList<HitlGateRecord>> ListGatesAsync(Guid agentId, CancellationToken ct);
    Task<HitlGateRecord> CreateGateAsync(
        Guid tenantId, Guid agentId, Guid createdBy, CreateGateRequest request, CancellationToken ct);
    Task<HitlGateRecord?> UpdateGateAsync(Guid gateId, UpdateGateRequest request, CancellationToken ct);
    Task<bool> DeleteGateAsync(Guid gateId, CancellationToken ct);

    Task<HitlPendingActionRecord> CreatePendingActionAsync(
        Guid tenantId, Guid triggeredBy, CreatePendingActionRequest request, CancellationToken ct);

    Task<IReadOnlyList<HitlPendingActionRecord>> ListPendingForTenantAsync(
        Guid tenantId, CancellationToken ct);

    Task<HitlPendingActionRecord?> DecideAsync(
        Guid actionId, Guid deciderId, DecideActionRequest request, CancellationToken ct);

    Task<HitlPendingActionRecord?> FindActionAsync(Guid actionId, CancellationToken ct);
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

    public async Task<IReadOnlyList<HitlGateRecord>> ListGatesAsync(Guid agentId, CancellationToken ct)
    {
        var sql = $"SELECT {GateColumns} FROM hitl_gates WHERE agent_id = @agentId ORDER BY name";
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<HitlGateRecord>(
            new CommandDefinition(sql, new { agentId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<HitlGateRecord> CreateGateAsync(
        Guid tenantId, Guid agentId, Guid createdBy, CreateGateRequest request, CancellationToken ct)
    {
        var ruleJson = request.TriggerRule?.GetRawText() ?? "{}";
        var sql = $"""
            INSERT INTO hitl_gates
                (tenant_id, agent_id, name, description, trigger_rule, approver_role, created_by)
            VALUES
                (@tenantId, @agentId, @Name, @Description, @ruleJson::jsonb,
                 COALESCE(@ApproverRole, 'owner'), @createdBy)
            RETURNING {GateColumns}
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleAsync<HitlGateRecord>(
            new CommandDefinition(sql, new
            {
                tenantId, agentId, createdBy,
                request.Name, request.Description, request.ApproverRole, ruleJson,
            }, cancellationToken: ct));
    }

    public async Task<HitlGateRecord?> UpdateGateAsync(Guid gateId, UpdateGateRequest request, CancellationToken ct)
    {
        var ruleJson = request.TriggerRule?.GetRawText();
        var sql = $"""
            UPDATE hitl_gates SET
                name          = COALESCE(@Name, name),
                description   = COALESCE(@Description, description),
                trigger_rule  = COALESCE(@ruleJson::jsonb, trigger_rule),
                approver_role = COALESCE(@ApproverRole, approver_role),
                is_active     = COALESCE(@IsActive, is_active)
            WHERE id = @gateId
            RETURNING {GateColumns}
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<HitlGateRecord>(
            new CommandDefinition(sql, new
            {
                gateId,
                request.Name, request.Description, request.ApproverRole, request.IsActive, ruleJson,
            }, cancellationToken: ct));
    }

    public async Task<bool> DeleteGateAsync(Guid gateId, CancellationToken ct)
    {
        const string sql = "DELETE FROM hitl_gates WHERE id = @gateId";
        await using var conn = await _factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(sql, new { gateId }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<HitlPendingActionRecord> CreatePendingActionAsync(
        Guid tenantId, Guid triggeredBy, CreatePendingActionRequest request, CancellationToken ct)
    {
        var payloadJson = request.Payload.GetRawText();
        const string insertSql = """
            INSERT INTO hitl_pending_actions
                (tenant_id, gate_id, agent_id, triggered_by, tool_code, payload, expires_at)
            SELECT
                @tenantId, g.id, g.agent_id, @triggeredBy, @ToolCode, @payloadJson::jsonb, @ExpiresAt
            FROM hitl_gates g
            WHERE g.id = @GateId
            RETURNING id
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var id = await conn.QuerySingleAsync<Guid>(
            new CommandDefinition(insertSql, new
            {
                tenantId, triggeredBy, request.GateId, request.ToolCode, payloadJson, request.ExpiresAt,
            }, cancellationToken: ct));
        return (await FindActionAsync(id, ct))!;
    }

    public async Task<IReadOnlyList<HitlPendingActionRecord>> ListPendingForTenantAsync(
        Guid tenantId, CancellationToken ct)
    {
        var sql = $"""
            SELECT {ActionColumns}
            FROM hitl_pending_actions p
            JOIN hitl_gates g ON g.id = p.gate_id
            JOIN agents a ON a.id = p.agent_id
            LEFT JOIN users ut ON ut.id = p.triggered_by
            LEFT JOIN users ud ON ud.id = p.decided_by
            WHERE p.tenant_id = @tenantId AND p.status = 'pending'
            ORDER BY p.created_at DESC
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<HitlPendingActionRecord>(
            new CommandDefinition(sql, new { tenantId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<HitlPendingActionRecord?> DecideAsync(
        Guid actionId, Guid deciderId, DecideActionRequest request, CancellationToken ct)
    {
        var newStatus = request.Decision == "approve" ? "approved" : "rejected";
        const string sql = """
            UPDATE hitl_pending_actions
            SET status = @newStatus,
                decided_by = @deciderId,
                decided_at = NOW(),
                decision_comment = @Comment
            WHERE id = @actionId AND status = 'pending'
            RETURNING id
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var updated = await conn.QueryFirstOrDefaultAsync<Guid?>(
            new CommandDefinition(sql, new { actionId, deciderId, newStatus, request.Comment },
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
}
