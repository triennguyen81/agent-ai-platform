using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Governance;

public static class ExecutionAuthorizationRules
{
    public static bool CanExecute(
        bool isSuperAdmin,
        bool isTenantAdmin,
        bool sameTenant,
        bool tenantActive,
        bool userActive,
        bool agentPublished,
        bool agentEnabled,
        bool activeAssignment) =>
        userActive && tenantActive && agentPublished && agentEnabled
        && (isSuperAdmin || (sameTenant && (isTenantAdmin || activeAssignment)));
}

public sealed record AgentExecutionAuthorization(
    Guid AgentId,
    Guid TenantId,
    string AgentCode,
    string LifecycleState,
    bool Allowed,
    string? DenialReason,
    string ConfigJson,
    string[] AllowedToolCodes);

public interface IAgentAssignmentRepository
{
    Task<AgentExecutionAuthorization?> AuthorizeExecutionAsync(
        Guid userId, Guid? currentTenantId, bool isSuperAdmin, bool isTenantAdmin,
        Guid agentId, CancellationToken ct);
}

public sealed class AgentAssignmentRepository : IAgentAssignmentRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public AgentAssignmentRepository(NpgsqlConnectionFactory factory) => _factory = factory;

    public async Task<AgentExecutionAuthorization?> AuthorizeExecutionAsync(
        Guid userId, Guid? currentTenantId, bool isSuperAdmin, bool isTenantAdmin,
        Guid agentId, CancellationToken ct)
    {
        const string sql = """
            SELECT a.id AS AgentId, a.tenant_id AS TenantId, a.code AS AgentCode,
                   a.lifecycle_state AS LifecycleState, a.config::text AS ConfigJson,
                   u.is_active AS UserActive, t.status = 'active' AS TenantActive,
                   a.lifecycle_state = 'production' AS AgentPublished,
                   a.disabled_at IS NULL AS AgentEnabled,
                   EXISTS (
                       SELECT 1 FROM agent_assignments aa
                       WHERE aa.tenant_id = a.tenant_id AND aa.agent_id = a.id
                         AND aa.user_id = @userId AND aa.status = 'active'
                   ) AS ActiveAssignment
            FROM agents a
            JOIN tenants t ON t.id = a.tenant_id
            JOIN users u ON u.id = @userId
            WHERE a.id = @agentId
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ExecutionRow>(new CommandDefinition(
            sql, new { userId, agentId }, cancellationToken: ct));
        if (row is null) return null;

        var sameTenant = currentTenantId == row.TenantId;
        var allowed = ExecutionAuthorizationRules.CanExecute(
            isSuperAdmin, isTenantAdmin, sameTenant, row.TenantActive, row.UserActive,
            row.AgentPublished, row.AgentEnabled, row.ActiveAssignment);
        var denial = allowed ? null
            : !row.UserActive ? "user_disabled"
            : !row.TenantActive ? "tenant_disabled"
            : !row.AgentPublished ? "agent_not_published"
            : !row.AgentEnabled ? "agent_disabled"
            : !sameTenant ? "cross_tenant"
            : "agent_not_assigned";

        var codes = allowed
            ? (await conn.QueryAsync<string>(new CommandDefinition("""
                SELECT tool.code
                FROM agent_tool_grants grant_row
                JOIN tools tool ON tool.id = grant_row.tool_id AND tool.is_active
                JOIN agents agent ON agent.id = grant_row.agent_id
                WHERE grant_row.agent_id = @agentId
                  AND CASE agent.action_tier
                        WHEN 'autonomous' THEN 4 WHEN 'execute_with_approval' THEN 3
                        WHEN 'suggest' THEN 2 ELSE 1 END
                      >= CASE tool.min_action_tier
                        WHEN 'autonomous' THEN 4 WHEN 'execute_with_approval' THEN 3
                        WHEN 'suggest' THEN 2 ELSE 1 END
                """, new { agentId }, cancellationToken: ct))).ToArray()
            : [];

        return new AgentExecutionAuthorization(
            row.AgentId, row.TenantId, row.AgentCode, row.LifecycleState,
            allowed, denial, row.ConfigJson, codes);
    }

    private sealed record ExecutionRow(
        Guid AgentId, Guid TenantId, string AgentCode, string LifecycleState,
        string ConfigJson, bool UserActive, bool TenantActive, bool AgentPublished,
        bool AgentEnabled, bool ActiveAssignment);
}
