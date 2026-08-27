using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Aiq.Gateway.Data;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Governance;

public sealed record SystemAgentRecord(
    Guid Id, string Slug, string Name, string? Description, string IconMetadataJson,
    string Scope, string Type, string ActionTier, string LifecycleState,
    bool Enabled, Guid? CurrentDraftVersionId, Guid? PublishedVersionId,
    Guid? CreatedBy, Guid? UpdatedBy, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record AgentVersionRecord(
    Guid Id, Guid AgentId, int Version, string Status, string Provider, string Model,
    string SystemPrompt, decimal Temperature, int MaxTurns, int TimeoutSeconds,
    int MaxTokens, string MemoryConfigJson, Guid? CreatedBy, DateTime CreatedAt,
    Guid? PublishedBy, DateTime? PublishedAt, Guid[] ToolIds, string[] Skills,
    Guid? WorkflowId, Guid[] DelegateAgentIds);

public sealed record CreateSystemAgentRequest(
    [Required, RegularExpression("^[a-z0-9]+(?:-[a-z0-9]+)*$"), StringLength(80, MinimumLength = 2)] string Slug,
    [Required, StringLength(160)] string Name,
    [StringLength(4000)] string? Description,
    JsonElement? IconMetadata,
    [RegularExpression("^(orchestrator|domain|sub|tool_only)$")] string? Type,
    [RegularExpression("^(read_only|suggest|execute_with_approval|autonomous)$")] string? ActionTier);

public sealed record UpdateSystemAgentRequest(
    [StringLength(160)] string? Name,
    [StringLength(4000)] string? Description,
    JsonElement? IconMetadata,
    [RegularExpression("^(read_only|suggest|execute_with_approval|autonomous)$")] string? ActionTier);

public sealed record CreateAgentVersionRequest(
    [Required, RegularExpression("^(ollama|claude_sdk|anthropic)$")] string Provider,
    [Required, StringLength(200, MinimumLength = 1)] string Model,
    [Required] string SystemPrompt,
    [Range(0d, 2d)] decimal Temperature,
    [Range(1, 20)] int MaxTurns,
    [Range(1, 3600)] int TimeoutSeconds,
    [Range(1, 1000000)] int MaxTokens,
    JsonElement? MemoryConfig,
    Guid[]? ToolIds,
    string[]? Skills,
    Guid? WorkflowId,
    Guid[]? DelegateAgentIds);

public sealed record UpdateAgentVersionRequest(
    [RegularExpression("^(ollama|claude_sdk|anthropic)$")] string? Provider,
    [StringLength(200, MinimumLength = 1)] string? Model,
    string? SystemPrompt,
    [Range(0d, 2d)] decimal? Temperature,
    [Range(1, 20)] int? MaxTurns,
    [Range(1, 3600)] int? TimeoutSeconds,
    [Range(1, 1000000)] int? MaxTokens,
    JsonElement? MemoryConfig,
    Guid[]? ToolIds,
    string[]? Skills,
    bool? ClearWorkflow,
    Guid? WorkflowId,
    Guid[]? DelegateAgentIds);

public sealed record AgentVersionValidation(bool Valid, string[] Errors);
public sealed record AgentTenantAssignmentRecord(Guid TenantId, string TenantCode, string TenantName, DateTime AssignedAt);
public sealed record TenantAssignedAgentRecord(Guid AgentId, string Slug, string Name, string? Description, string LifecycleState, bool Enabled, DateTime AssignedAt);
public sealed record AgentUserAssignmentRecord(Guid UserId, string Email, string? DisplayName, DateTime AssignedAt);

public sealed record ManifestAgent(Guid Id, string Slug, string Name, string? Description, JsonElement IconMetadata);
public sealed record ManifestTool(Guid Id, string Code, string Name, string? Description, string? Category, string RiskTier);
public sealed record ManifestKnowledge(Guid NoteId, string Title, string Content, string[] Tags);
public sealed record ManifestWorkflow(Guid Id, string Name, JsonElement Definition);
public sealed record ManifestDelegate(Guid Id, string Slug, string Name, string? Description);
public sealed record AgentExecutionManifest(
    Guid UserId, Guid TenantId, ManifestAgent Agent, Guid PublishedVersionId,
    int Version, string Provider, string Model, string SystemPrompt, decimal Temperature,
    int MaxTurns, int TimeoutSeconds, int MaxTokens, JsonElement MemoryConfig,
    IReadOnlyList<ManifestTool> Tools, IReadOnlyList<string> Skills,
    IReadOnlyList<ManifestKnowledge> Knowledge,
    ManifestWorkflow? Workflow,
    IReadOnlyList<ManifestDelegate> Delegates,
    // Minted only for the real end-user path (never the super_admin test path,
    // which already carries native admin rights). Short-lived, scoped to this
    // user's own tenant_id claim. Lets an authorized granted-tool HTTP call
    // reach tenant-scoped resource endpoints without ever handing this
    // credential to the browser — see DynamicAgentEndpoints.ManifestAsync.
    string? ToolExecutionToken = null);
public sealed record ManifestResult(AgentExecutionManifest? Manifest, string? DenialReason);

public interface IDynamicAgentRepository
{
    Task<IReadOnlyList<SystemAgentRecord>> ListSystemAgentsAsync(CancellationToken ct);
    Task<SystemAgentRecord?> FindSystemAgentAsync(Guid agentId, CancellationToken ct);
    Task<SystemAgentRecord> CreateSystemAgentAsync(Guid actorId, CreateSystemAgentRequest request, CancellationToken ct);
    Task<SystemAgentRecord?> UpdateSystemAgentAsync(Guid agentId, Guid actorId, UpdateSystemAgentRequest request, CancellationToken ct);
    Task<SystemAgentRecord?> SetEnabledAsync(Guid agentId, Guid actorId, bool enabled, CancellationToken ct);
    Task<IReadOnlyList<AgentVersionRecord>> ListVersionsAsync(Guid agentId, CancellationToken ct);
    Task<AgentVersionRecord?> FindVersionAsync(Guid agentId, Guid versionId, CancellationToken ct);
    Task<AgentVersionRecord?> CreateVersionAsync(Guid agentId, Guid actorId, CreateAgentVersionRequest request, CancellationToken ct);
    Task<AgentVersionRecord?> UpdateVersionAsync(Guid agentId, Guid versionId, Guid actorId, UpdateAgentVersionRequest request, CancellationToken ct);
    Task<AgentVersionValidation?> ValidateVersionAsync(Guid agentId, Guid versionId, CancellationToken ct);
    Task<AgentVersionRecord?> PublishVersionAsync(Guid agentId, Guid versionId, Guid actorId, CancellationToken ct);
    Task<bool> AssignTenantAsync(Guid agentId, Guid tenantId, Guid actorId, CancellationToken ct);
    Task<bool> RevokeTenantAsync(Guid agentId, Guid tenantId, CancellationToken ct);
    Task<IReadOnlyList<AgentTenantAssignmentRecord>> ListTenantsAsync(Guid agentId, CancellationToken ct);

    /// <summary>
    /// System agents assigned to a single tenant — tenantId must come from
    /// server-side auth context. Powers the tenant ADMIN's "which agents can
    /// I assign to my users" view.
    /// </summary>
    Task<IReadOnlyList<TenantAssignedAgentRecord>> ListAgentsForTenantAsync(Guid tenantId, CancellationToken ct);
    Task<IReadOnlyList<AgentUserAssignmentRecord>?> ListAssignedUsersAsync(Guid tenantId, Guid agentId, CancellationToken ct);
    Task<bool> AssignUserAsync(Guid tenantId, Guid agentId, Guid userId, Guid actorId, CancellationToken ct);
    Task<bool> RevokeUserAsync(Guid tenantId, Guid agentId, Guid userId, CancellationToken ct);
    Task<ManifestResult> GetManifestAsync(Guid userId, Guid tenantId, Guid agentId, CancellationToken ct);

    /// <summary>
    /// Resolves a SPECIALIST's manifest for an authorized in-flight delegation
    /// FROM a router the caller is genuinely executing. Re-derives the
    /// router's own authorization from scratch (never trusts that a prior
    /// call succeeded), then requires the specialist to be in that router's
    /// PUBLISHED version's persisted delegate list, tenant-assigned (same
    /// tenant as the router), published, and enabled. Deliberately does NOT
    /// require the specialist to have its own user-level agent_assignments
    /// row — delegation is reached only through the router, never directly,
    /// so it cannot broaden what the user could otherwise call themselves.
    /// </summary>
    Task<ManifestResult> GetDelegateManifestAsync(Guid userId, Guid tenantId, Guid routerAgentId, Guid specialistAgentId, CancellationToken ct);

    /// <summary>
    /// SUPER_ADMIN-only test resolution: builds a manifest from the agent's
    /// current draft (falling back to its published version) with no tenant/
    /// user assignment or production-lifecycle gate — a super_admin already
    /// has full CRUD over every system agent, so letting them execute the
    /// exact version they are editing is not a privilege escalation. Knowledge
    /// is intentionally always empty (no tenant context to scope it to).
    /// </summary>
    Task<ManifestResult> GetTestManifestAsync(Guid actorId, Guid agentId, CancellationToken ct);

    // ---- Tenant ADMIN knowledge management for system agents (Phase 5A) ----
    // agent_knowledge has no tenant column of its own — the SAME system agent
    // can be attached to multiple tenants with different tenants' notes all
    // sharing that one agent_id's rows. Every method here filters through
    // notes.tenant_id so a tenant ADMIN only ever sees/attaches/detaches
    // their OWN tenant's notes, never another tenant's, even on a shared
    // agent. tenantId must always come from server-side auth context.

    /// <summary>Notes from the caller's tenant currently attached to this agent.</summary>
    Task<IReadOnlyList<NoteSummary>?> ListTenantKnowledgeAsync(Guid tenantId, Guid agentId, CancellationToken ct);

    /// <summary>Caller's tenant notes not yet attached to this agent.</summary>
    Task<IReadOnlyList<NoteSummary>?> ListAttachableTenantKnowledgeAsync(Guid tenantId, Guid agentId, CancellationToken ct);

    Task<bool> AttachTenantKnowledgeAsync(Guid tenantId, Guid agentId, Guid noteId, Guid actorId, CancellationToken ct);

    Task<bool> DetachTenantKnowledgeAsync(Guid tenantId, Guid agentId, Guid noteId, CancellationToken ct);
}

public sealed class DynamicAgentRepository : IDynamicAgentRepository
{
    private const string AgentColumns = """
        a.id AS Id, a.slug AS Slug, a.name AS Name, a.description AS Description,
        a.icon_metadata::text AS IconMetadataJson, a.scope AS Scope, a.type AS Type,
        a.action_tier AS ActionTier, a.lifecycle_state AS LifecycleState,
        (a.disabled_at IS NULL) AS Enabled,
        a.current_draft_version_id AS CurrentDraftVersionId,
        a.published_version_id AS PublishedVersionId, a.created_by AS CreatedBy,
        a.updated_by AS UpdatedBy, a.created_at AS CreatedAt, a.updated_at AS UpdatedAt
        """;
    private const string VersionColumns = """
        v.id AS Id, v.agent_id AS AgentId, v.version AS Version, v.status AS Status,
        v.provider AS Provider, v.model AS Model, v.system_prompt AS SystemPrompt,
        v.temperature AS Temperature, v.max_turns AS MaxTurns,
        v.timeout_seconds AS TimeoutSeconds, v.max_tokens AS MaxTokens,
        v.memory_config::text AS MemoryConfigJson, v.created_by AS CreatedBy,
        v.created_at AS CreatedAt, v.published_by AS PublishedBy, v.published_at AS PublishedAt,
        v.workflow_id AS WorkflowId
        """;

    private readonly NpgsqlConnectionFactory _factory;
    public DynamicAgentRepository(NpgsqlConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<SystemAgentRecord>> ListSystemAgentsAsync(CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<SystemAgentRecord>(new CommandDefinition(
            $"SELECT {AgentColumns} FROM agents a WHERE a.scope='system' ORDER BY a.created_at DESC", cancellationToken: ct))).AsList();
    }

    public async Task<SystemAgentRecord?> FindSystemAgentAsync(Guid agentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<SystemAgentRecord>(new CommandDefinition(
            $"SELECT {AgentColumns} FROM agents a WHERE a.scope='system' AND a.id=@agentId", new { agentId }, cancellationToken: ct));
    }

    public async Task<SystemAgentRecord> CreateSystemAgentAsync(Guid actorId, CreateSystemAgentRequest request, CancellationToken ct)
    {
        var icon = request.IconMetadata?.GetRawText() ?? "{}";
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleAsync<SystemAgentRecord>(new CommandDefinition($"""
            INSERT INTO agents (tenant_id, code, slug, name, description, icon_metadata, scope,
                owning_tenant_id, type, action_tier, lifecycle_state, config, owner_user_id, created_by, updated_by)
            VALUES (NULL, @Slug, @Slug, @Name, @Description, @icon::jsonb, 'system', NULL,
                COALESCE(@Type,'domain'), COALESCE(@ActionTier,'suggest'), 'draft', @emptyConfig::jsonb,
                @actorId, @actorId, @actorId)
            RETURNING {AgentColumns.Replace("a.", "")}
            """, new { actorId, request.Slug, request.Name, request.Description, icon, emptyConfig = "{}", request.Type, request.ActionTier }, cancellationToken: ct));
    }

    public async Task<SystemAgentRecord?> UpdateSystemAgentAsync(Guid agentId, Guid actorId, UpdateSystemAgentRequest request, CancellationToken ct)
    {
        var icon = request.IconMetadata?.GetRawText();
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<SystemAgentRecord>(new CommandDefinition($"""
            UPDATE agents SET name=COALESCE(@Name,name), description=COALESCE(@Description,description),
                icon_metadata=COALESCE(@icon::jsonb,icon_metadata), action_tier=COALESCE(@ActionTier,action_tier), updated_by=@actorId
            WHERE id=@agentId AND scope='system'
            RETURNING {AgentColumns.Replace("a.", "")}
            """, new { agentId, actorId, request.Name, request.Description, icon, request.ActionTier }, cancellationToken: ct));
    }

    public async Task<SystemAgentRecord?> SetEnabledAsync(Guid agentId, Guid actorId, bool enabled, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<SystemAgentRecord>(new CommandDefinition($"""
            UPDATE agents SET disabled_at=CASE WHEN @enabled THEN NULL ELSE NOW() END,
                disabled_by=CASE WHEN @enabled THEN NULL ELSE @actorId END,
                disabled_reason=CASE WHEN @enabled THEN NULL ELSE 'disabled by system administrator' END,
                updated_by=@actorId WHERE id=@agentId AND scope='system'
            RETURNING {AgentColumns.Replace("a.", "")}
            """, new { agentId, actorId, enabled }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AgentVersionRecord>> ListVersionsAsync(Guid agentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = (await conn.QueryAsync<VersionRow>(new CommandDefinition(
            $"SELECT {VersionColumns} FROM agent_versions v JOIN agents a ON a.id=v.agent_id AND a.scope='system' WHERE v.agent_id=@agentId ORDER BY v.version DESC",
            new { agentId }, cancellationToken: ct))).AsList();
        return await HydrateVersionsAsync(conn, rows, ct);
    }

    public async Task<AgentVersionRecord?> FindVersionAsync(Guid agentId, Guid versionId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<VersionRow>(new CommandDefinition(
            $"SELECT {VersionColumns} FROM agent_versions v JOIN agents a ON a.id=v.agent_id AND a.scope='system' WHERE v.agent_id=@agentId AND v.id=@versionId",
            new { agentId, versionId }, cancellationToken: ct));
        if (row is null) return null;
        return (await HydrateVersionsAsync(conn, [row], ct))[0];
    }

    public async Task<AgentVersionRecord?> CreateVersionAsync(Guid agentId, Guid actorId, CreateAgentVersionRequest request, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var validAgent = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM agents WHERE id=@agentId AND scope='system')", new { agentId }, tx, cancellationToken: ct));
        if (!validAgent) return null;
        var memory = request.MemoryConfig?.GetRawText() ?? "{}";
        var row = await conn.QuerySingleAsync<VersionRow>(new CommandDefinition($"""
            INSERT INTO agent_versions (agent_id, version, status, provider, model, system_prompt,
                temperature, max_turns, timeout_seconds, max_tokens, memory_config, created_by, workflow_id)
            VALUES (@agentId, (SELECT COALESCE(MAX(version),0)+1 FROM agent_versions WHERE agent_id=@agentId),
                'draft', @Provider, @Model, @SystemPrompt, @Temperature, @MaxTurns,
                @TimeoutSeconds, @MaxTokens, @memory::jsonb, @actorId, @WorkflowId)
            RETURNING {VersionColumns.Replace("v.", "")}
            """, new { agentId, actorId, request.Provider, request.Model, request.SystemPrompt, request.Temperature,
                request.MaxTurns, request.TimeoutSeconds, request.MaxTokens, memory, request.WorkflowId }, tx, cancellationToken: ct));
        await ReplaceCapabilitiesAsync(conn, tx, agentId, row.Id, actorId, request.ToolIds, request.Skills, request.DelegateAgentIds, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE agents SET current_draft_version_id=@versionId, updated_by=@actorId WHERE id=@agentId",
            new { agentId, actorId, versionId = row.Id }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return (await HydrateVersionsAsync(conn, [row], ct))[0];
    }

    public async Task<AgentVersionRecord?> UpdateVersionAsync(Guid agentId, Guid versionId, Guid actorId, UpdateAgentVersionRequest request, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var memory = request.MemoryConfig?.GetRawText();
        // workflow_id: three states — omit (leave as-is), set to a specific id,
        // or explicitly clear via ClearWorkflow=true (COALESCE can't express
        // "set to NULL", so the clear flag is a separate switch).
        var row = await conn.QuerySingleOrDefaultAsync<VersionRow>(new CommandDefinition($"""
            UPDATE agent_versions v SET provider=COALESCE(@Provider,provider), model=COALESCE(@Model,model),
                system_prompt=COALESCE(@SystemPrompt,system_prompt), temperature=COALESCE(@Temperature,temperature),
                max_turns=COALESCE(@MaxTurns,max_turns), timeout_seconds=COALESCE(@TimeoutSeconds,timeout_seconds),
                max_tokens=COALESCE(@MaxTokens,max_tokens), memory_config=COALESCE(@memory::jsonb,memory_config),
                workflow_id=CASE WHEN @ClearWorkflow THEN NULL ELSE COALESCE(@WorkflowId,workflow_id) END
            FROM agents a WHERE v.id=@versionId AND v.agent_id=@agentId AND v.status='draft'
                AND a.id=v.agent_id AND a.scope='system'
            RETURNING {VersionColumns}
            """, new { agentId, versionId, request.Provider, request.Model, request.SystemPrompt, request.Temperature,
                request.MaxTurns, request.TimeoutSeconds, request.MaxTokens, memory,
                request.WorkflowId, ClearWorkflow = request.ClearWorkflow ?? false }, tx, cancellationToken: ct));
        if (row is null) return null;
        await ReplaceCapabilitiesAsync(conn, tx, agentId, versionId, actorId, request.ToolIds, request.Skills, request.DelegateAgentIds, ct);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE agents SET updated_by=@actorId WHERE id=@agentId", new { actorId, agentId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return (await HydrateVersionsAsync(conn, [row], ct))[0];
    }

    public async Task<AgentVersionValidation?> ValidateVersionAsync(Guid agentId, Guid versionId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ValidationRow>(new CommandDefinition("""
            SELECT v.provider AS Provider, v.model AS Model, v.system_prompt AS SystemPrompt,
              v.workflow_id AS WorkflowId,
              (SELECT COUNT(*) FROM agent_version_tool_grants g WHERE g.agent_version_id=v.id) AS ToolCount,
              (SELECT COUNT(*) FROM agent_version_tool_grants g JOIN tools t ON t.id=g.tool_id
                WHERE g.agent_version_id=v.id AND t.is_active AND
                  CASE a.action_tier WHEN 'autonomous' THEN 4 WHEN 'execute_with_approval' THEN 3 WHEN 'suggest' THEN 2 ELSE 1 END >=
                  CASE t.min_action_tier WHEN 'autonomous' THEN 4 WHEN 'execute_with_approval' THEN 3 WHEN 'suggest' THEN 2 ELSE 1 END) AS ValidToolCount,
              (v.workflow_id IS NULL OR EXISTS(SELECT 1 FROM workflows w WHERE w.id=v.workflow_id AND w.is_active)) AS WorkflowValid
            FROM agent_versions v JOIN agents a ON a.id=v.agent_id AND a.scope='system'
            WHERE v.id=@versionId AND v.agent_id=@agentId
            """, new { agentId, versionId }, cancellationToken: ct));
        if (row is null) return null;
        var errors = new List<string>();
        if (row.Provider is not ("ollama" or "claude_sdk" or "anthropic")) errors.Add("unsupported_provider");
        if (string.IsNullOrWhiteSpace(row.Model) || row.Model.Contains("://") || row.Model.Any(char.IsWhiteSpace)) errors.Add("invalid_model");
        if (string.IsNullOrWhiteSpace(row.SystemPrompt)) errors.Add("system_prompt_required");
        if (row.ToolCount != row.ValidToolCount) errors.Add("inactive_or_unauthorized_tool");
        if (!row.WorkflowValid) errors.Add("inactive_or_missing_workflow");
        return new AgentVersionValidation(errors.Count == 0, errors.ToArray());
    }

    public async Task<AgentVersionRecord?> PublishVersionAsync(Guid agentId, Guid versionId, Guid actorId, CancellationToken ct)
    {
        var validation = await ValidateVersionAsync(agentId, versionId, ct);
        if (validation is null || !validation.Valid) return null;
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var stillValid = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (
              SELECT 1 FROM agent_versions v JOIN agents a ON a.id=v.agent_id AND a.scope='system'
              WHERE v.id=@versionId AND v.agent_id=@agentId AND v.status='draft'
                AND length(trim(v.system_prompt)) > 0
                AND v.provider IN ('ollama','claude_sdk','anthropic')
                AND NOT EXISTS (
                  SELECT 1 FROM agent_version_tool_grants g LEFT JOIN tools t ON t.id=g.tool_id
                  WHERE g.agent_version_id=v.id AND (t.id IS NULL OR NOT t.is_active OR
                    CASE a.action_tier WHEN 'autonomous' THEN 4 WHEN 'execute_with_approval' THEN 3 WHEN 'suggest' THEN 2 ELSE 1 END <
                    CASE t.min_action_tier WHEN 'autonomous' THEN 4 WHEN 'execute_with_approval' THEN 3 WHEN 'suggest' THEN 2 ELSE 1 END)
                )
                AND (v.workflow_id IS NULL OR EXISTS(SELECT 1 FROM workflows w WHERE w.id=v.workflow_id AND w.is_active))
              FOR UPDATE
            )
            """, new { agentId, versionId }, tx, cancellationToken: ct));
        if (!stillValid)
        {
            await tx.RollbackAsync(ct);
            return null;
        }
        var row = await conn.QuerySingleOrDefaultAsync<VersionRow>(new CommandDefinition($"""
            UPDATE agent_versions v SET status='published', published_by=@actorId, published_at=NOW()
            FROM agents a WHERE v.id=@versionId AND v.agent_id=@agentId AND v.status='draft'
                AND a.id=v.agent_id AND a.scope='system'
            RETURNING {VersionColumns}
            """, new { agentId, versionId, actorId }, tx, cancellationToken: ct));
        if (row is null) return null;
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE agents SET published_version_id=@versionId, current_draft_version_id=NULL,
                lifecycle_state='production', updated_by=@actorId WHERE id=@agentId AND scope='system'
            """, new { agentId, versionId, actorId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return (await HydrateVersionsAsync(conn, [row], ct))[0];
    }

    private sealed record TenantAgentQuotaRow(int? MaxAgents, int Current);

    public async Task<bool> AssignTenantAsync(Guid agentId, Guid tenantId, Guid actorId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);

        // Already-active assignment (or a re-assign of a revoked one) never
        // consumes fresh quota headroom, so only gate on genuinely new rows.
        var alreadyActive = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM agent_tenant_assignments WHERE agent_id=@agentId AND tenant_id=@tenantId AND status='active')",
            new { agentId, tenantId }, cancellationToken: ct));
        if (!alreadyActive)
        {
            var quota = await conn.QuerySingleOrDefaultAsync<TenantAgentQuotaRow>(new CommandDefinition("""
                SELECT t.max_agents AS MaxAgents,
                    (SELECT COUNT(*)::int FROM agent_tenant_assignments a WHERE a.tenant_id=@tenantId AND a.status='active') AS Current
                FROM tenants t WHERE t.id=@tenantId AND t.status='active'
                """, new { tenantId }, cancellationToken: ct));
            if (quota is not null && !TenantQuotaPolicy.HasRoomFor(quota.MaxAgents, quota.Current))
                throw new InvalidOperationException("agent_quota_exceeded");
        }

        var n = await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO agent_tenant_assignments(agent_id,tenant_id,status,assigned_by)
            SELECT @agentId,@tenantId,'active',@actorId
            WHERE EXISTS(SELECT 1 FROM agents WHERE id=@agentId AND scope='system')
              AND EXISTS(SELECT 1 FROM tenants WHERE id=@tenantId AND status='active')
            ON CONFLICT (agent_id,tenant_id) WHERE status='active' DO NOTHING
            """, new { agentId, tenantId, actorId }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<bool> RevokeTenantAsync(Guid agentId, Guid tenantId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE agent_tenant_assignments SET status='revoked',revoked_at=NOW()
            WHERE agent_id=@agentId AND tenant_id=@tenantId AND status='active'
            """, new { agentId, tenantId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE agent_assignments SET status='revoked',revoked_at=NOW()
            WHERE agent_id=@agentId AND tenant_id=@tenantId AND status='active'
            """, new { agentId, tenantId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return n > 0;
    }

    public async Task<IReadOnlyList<AgentTenantAssignmentRecord>> ListTenantsAsync(Guid agentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<AgentTenantAssignmentRecord>(new CommandDefinition("""
            SELECT t.id AS TenantId,t.code AS TenantCode,t.name AS TenantName,ata.assigned_at AS AssignedAt
            FROM agent_tenant_assignments ata JOIN tenants t ON t.id=ata.tenant_id
            JOIN agents a ON a.id=ata.agent_id AND a.scope='system'
            WHERE ata.agent_id=@agentId AND ata.status='active' ORDER BY t.name
            """, new { agentId }, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<TenantAssignedAgentRecord>> ListAgentsForTenantAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<TenantAssignedAgentRecord>(new CommandDefinition("""
            SELECT a.id AS AgentId, a.slug AS Slug, a.name AS Name, a.description AS Description,
              a.lifecycle_state AS LifecycleState, (a.disabled_at IS NULL) AS Enabled, ata.assigned_at AS AssignedAt
            FROM agent_tenant_assignments ata JOIN agents a ON a.id=ata.agent_id AND a.scope='system'
            WHERE ata.tenant_id=@tenantId AND ata.status='active' ORDER BY a.name
            """, new { tenantId }, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<NoteSummary>?> ListTenantKnowledgeAsync(Guid tenantId, Guid agentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        if (!await IsTenantAgentAsync(conn, tenantId, agentId, ct)) return null;
        return (await conn.QueryAsync<NoteSummary>(new CommandDefinition("""
            SELECT n.id AS Id, n.title AS Title, n.tags AS Tags, n.updated_at AS UpdatedAt
            FROM agent_knowledge ak JOIN notes n ON n.id=ak.note_id AND n.tenant_id=@tenantId
            WHERE ak.agent_id=@agentId ORDER BY n.updated_at DESC
            """, new { tenantId, agentId }, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<NoteSummary>?> ListAttachableTenantKnowledgeAsync(Guid tenantId, Guid agentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        if (!await IsTenantAgentAsync(conn, tenantId, agentId, ct)) return null;
        return (await conn.QueryAsync<NoteSummary>(new CommandDefinition("""
            SELECT n.id AS Id, n.title AS Title, n.tags AS Tags, n.updated_at AS UpdatedAt
            FROM notes n
            WHERE n.tenant_id=@tenantId
              AND NOT EXISTS (SELECT 1 FROM agent_knowledge ak WHERE ak.agent_id=@agentId AND ak.note_id=n.id)
            ORDER BY n.updated_at DESC
            """, new { tenantId, agentId }, cancellationToken: ct))).AsList();
    }

    public async Task<bool> AttachTenantKnowledgeAsync(Guid tenantId, Guid agentId, Guid noteId, Guid actorId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
            SELECT @agentId, @noteId, @actorId
            WHERE EXISTS (SELECT 1 FROM agent_tenant_assignments WHERE agent_id=@agentId AND tenant_id=@tenantId AND status='active')
              AND EXISTS (SELECT 1 FROM notes WHERE id=@noteId AND tenant_id=@tenantId)
            ON CONFLICT (agent_id, note_id) DO NOTHING
            """, new { tenantId, agentId, noteId, actorId }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<bool> DetachTenantKnowledgeAsync(Guid tenantId, Guid agentId, Guid noteId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM agent_knowledge ak
            WHERE ak.agent_id=@agentId AND ak.note_id=@noteId
              AND EXISTS (SELECT 1 FROM notes n WHERE n.id=@noteId AND n.tenant_id=@tenantId)
            """, new { tenantId, agentId, noteId }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<IReadOnlyList<AgentUserAssignmentRecord>?> ListAssignedUsersAsync(Guid tenantId, Guid agentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var accessible = await IsTenantAgentAsync(conn, tenantId, agentId, ct);
        if (!accessible) return null;
        return (await conn.QueryAsync<AgentUserAssignmentRecord>(new CommandDefinition("""
            SELECT u.id AS UserId,u.email AS Email,u.display_name AS DisplayName,aa.created_at AS AssignedAt
            FROM agent_assignments aa JOIN users u ON u.id=aa.user_id
            WHERE aa.tenant_id=@tenantId AND aa.agent_id=@agentId AND aa.status='active' ORDER BY u.email
            """, new { tenantId, agentId }, cancellationToken: ct))).AsList();
    }

    public async Task<bool> AssignUserAsync(Guid tenantId, Guid agentId, Guid userId, Guid actorId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO agent_assignments(tenant_id,agent_id,user_id,assigned_by,status)
            SELECT @tenantId,@agentId,@userId,@actorId,'active'
            WHERE EXISTS(SELECT 1 FROM agent_tenant_assignments WHERE tenant_id=@tenantId AND agent_id=@agentId AND status='active')
              AND EXISTS(SELECT 1 FROM users u JOIN user_roles ur ON ur.user_id=u.id JOIN roles r ON r.id=ur.role_id
                WHERE u.id=@userId AND u.tenant_id=@tenantId AND u.is_active AND r.name='user')
            ON CONFLICT(tenant_id,agent_id,user_id) DO UPDATE SET status='active',assigned_by=EXCLUDED.assigned_by,revoked_at=NULL
            """, new { tenantId, agentId, userId, actorId }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<bool> RevokeUserAsync(Guid tenantId, Guid agentId, Guid userId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE agent_assignments SET status='revoked',revoked_at=NOW()
            WHERE tenant_id=@tenantId AND agent_id=@agentId AND user_id=@userId AND status='active'
              AND EXISTS(SELECT 1 FROM agent_tenant_assignments WHERE tenant_id=@tenantId AND agent_id=@agentId AND status='active')
            """, new { tenantId, agentId, userId }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<ManifestResult> GetManifestAsync(Guid userId, Guid tenantId, Guid agentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ManifestRow>(new CommandDefinition("""
            SELECT a.id AS AgentId,a.slug,a.name,a.description,a.icon_metadata::text AS IconMetadataJson,
              a.action_tier AS ActionTier,a.lifecycle_state AS LifecycleState,a.disabled_at IS NULL AS AgentEnabled,
              u.is_active AS UserActive,t.status='active' AS TenantActive,
              EXISTS(SELECT 1 FROM agent_tenant_assignments ata WHERE ata.agent_id=a.id AND ata.tenant_id=@tenantId AND ata.status='active') AS TenantAssigned,
              EXISTS(SELECT 1 FROM agent_assignments aa WHERE aa.agent_id=a.id AND aa.tenant_id=@tenantId AND aa.user_id=@userId AND aa.status='active') AS UserAssigned,
              v.id AS VersionId,v.version,v.status AS VersionStatus,v.provider,v.model,v.system_prompt AS SystemPrompt,
              v.temperature,v.max_turns AS MaxTurns,v.timeout_seconds AS TimeoutSeconds,v.max_tokens AS MaxTokens,v.memory_config::text AS MemoryConfigJson,
              v.workflow_id AS WorkflowId
            FROM users u JOIN tenants t ON t.id=@tenantId
            CROSS JOIN agents a LEFT JOIN agent_versions v ON v.id=a.published_version_id AND v.agent_id=a.id
            WHERE u.id=@userId AND u.tenant_id=@tenantId AND a.id=@agentId
            """, new { userId, tenantId, agentId }, cancellationToken: ct));
        if (row is null) return new(null, "not_found_or_cross_tenant");
        var denial = !row.UserActive ? "user_disabled" : !row.TenantActive ? "tenant_disabled"
            : !row.TenantAssigned ? "agent_not_assigned_to_tenant" : !row.UserAssigned ? "agent_not_assigned"
            : !row.AgentEnabled ? "agent_disabled" : row.LifecycleState != "production" ? "agent_not_production"
            : row.VersionId is null || row.VersionStatus != "published" ? "agent_not_published" : null;
        if (denial is not null) return new(null, denial);
        var tools = (await conn.QueryAsync<ManifestTool>(new CommandDefinition("""
            SELECT t.id AS Id,t.code AS Code,t.name AS Name,t.description AS Description,t.category AS Category,t.risk_tier AS RiskTier
            FROM agent_version_tool_grants g JOIN tools t ON t.id=g.tool_id AND t.is_active
            WHERE g.agent_version_id=@versionId AND
              CASE @actionTier WHEN 'autonomous' THEN 4 WHEN 'execute_with_approval' THEN 3 WHEN 'suggest' THEN 2 ELSE 1 END >=
              CASE t.min_action_tier WHEN 'autonomous' THEN 4 WHEN 'execute_with_approval' THEN 3 WHEN 'suggest' THEN 2 ELSE 1 END ORDER BY t.code
            """, new { versionId=row.VersionId, actionTier=row.ActionTier }, cancellationToken: ct))).AsList();
        var grantCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*)::int FROM agent_version_tool_grants WHERE agent_version_id=@versionId", new { versionId=row.VersionId }, cancellationToken: ct));
        if (grantCount != tools.Count) return new(null, "invalid_tool_configuration");
        var skills = (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT skill_name FROM agent_version_skill_assignments WHERE agent_version_id=@versionId ORDER BY skill_name",
            new { versionId=row.VersionId }, cancellationToken: ct))).AsList();
        var knowledge = (await conn.QueryAsync<ManifestKnowledge>(new CommandDefinition("""
            SELECT n.id AS NoteId,n.title AS Title,n.content AS Content,n.tags AS Tags
            FROM agent_knowledge ak JOIN notes n ON n.id=ak.note_id AND n.tenant_id=@tenantId
            WHERE ak.agent_id=@agentId ORDER BY ak.attached_at
            """, new { tenantId, agentId }, cancellationToken: ct))).AsList();
        var (workflowOk, workflow) = await TryResolveWorkflowAsync(conn, row.WorkflowId, ct);
        if (!workflowOk) return new(null, "inactive_or_missing_workflow");
        var delegates = await ResolveDelegatesAsync(conn, row.VersionId!.Value, ct);
        return new(new AgentExecutionManifest(userId, tenantId,
            new ManifestAgent(row.AgentId,row.Slug,row.Name,row.Description,ParseJson(row.IconMetadataJson)),
            row.VersionId!.Value,row.Version!.Value,row.Provider!,row.Model!,row.SystemPrompt!,row.Temperature!.Value,
            row.MaxTurns!.Value,row.TimeoutSeconds!.Value,row.MaxTokens!.Value,ParseJson(row.MemoryConfigJson!),tools,skills,knowledge,workflow,delegates), null);
    }

    /// <summary>
    /// Resolves a version's optional workflow. No workflow_id → success with
    /// null (unchanged single-turn execution). A workflow_id pointing at a
    /// missing/inactive row → fail closed, same as a missing tool handler.
    /// </summary>
    private static async Task<IReadOnlyList<ManifestDelegate>> ResolveDelegatesAsync(NpgsqlConnection conn, Guid versionId, CancellationToken ct) =>
        (await conn.QueryAsync<ManifestDelegate>(new CommandDefinition("""
            SELECT a.id AS Id, a.slug AS Slug, a.name AS Name, a.description AS Description
            FROM agent_version_delegates d JOIN agents a ON a.id=d.specialist_agent_id
            WHERE d.agent_version_id=@versionId ORDER BY a.slug
            """, new { versionId }, cancellationToken: ct))).AsList();

    private static async Task<(bool Ok, ManifestWorkflow? Workflow)> TryResolveWorkflowAsync(NpgsqlConnection conn, Guid? workflowId, CancellationToken ct)
    {
        if (workflowId is null) return (true, null);
        var row = await conn.QuerySingleOrDefaultAsync<(Guid Id, string Name, string DefinitionJson)?>(new CommandDefinition(
            "SELECT id AS Id, name AS Name, definition::text AS DefinitionJson FROM workflows WHERE id=@workflowId AND is_active",
            new { workflowId }, cancellationToken: ct));
        if (row is null) return (false, null);
        return (true, new ManifestWorkflow(row.Value.Id, row.Value.Name, ParseJson(row.Value.DefinitionJson)));
    }

    public async Task<ManifestResult> GetTestManifestAsync(Guid actorId, Guid agentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var agentRow = await conn.QuerySingleOrDefaultAsync<TestAgentRow>(new CommandDefinition("""
            SELECT a.id AS AgentId,a.slug,a.name,a.description,a.icon_metadata::text AS IconMetadataJson,
              a.action_tier AS ActionTier, COALESCE(a.current_draft_version_id,a.published_version_id) AS VersionId
            FROM agents a WHERE a.id=@agentId AND a.scope='system'
            """, new { agentId }, cancellationToken: ct));
        if (agentRow is null) return new(null, "not_found");
        if (agentRow.VersionId is null) return new(null, "no_version_to_test");

        var version = await conn.QuerySingleOrDefaultAsync<TestVersionRow>(new CommandDefinition("""
            SELECT id AS VersionId, version AS Version, provider AS Provider, model AS Model,
              system_prompt AS SystemPrompt, temperature AS Temperature, max_turns AS MaxTurns,
              timeout_seconds AS TimeoutSeconds, max_tokens AS MaxTokens, memory_config::text AS MemoryConfigJson,
              workflow_id AS WorkflowId
            FROM agent_versions WHERE id=@versionId
            """, new { versionId = agentRow.VersionId }, cancellationToken: ct));
        if (version is null) return new(null, "no_version_to_test");

        var tools = (await conn.QueryAsync<ManifestTool>(new CommandDefinition("""
            SELECT t.id AS Id,t.code AS Code,t.name AS Name,t.description AS Description,t.category AS Category,t.risk_tier AS RiskTier
            FROM agent_version_tool_grants g JOIN tools t ON t.id=g.tool_id AND t.is_active
            WHERE g.agent_version_id=@versionId AND
              CASE @actionTier WHEN 'autonomous' THEN 4 WHEN 'execute_with_approval' THEN 3 WHEN 'suggest' THEN 2 ELSE 1 END >=
              CASE t.min_action_tier WHEN 'autonomous' THEN 4 WHEN 'execute_with_approval' THEN 3 WHEN 'suggest' THEN 2 ELSE 1 END ORDER BY t.code
            """, new { versionId = version.VersionId, actionTier = agentRow.ActionTier }, cancellationToken: ct))).AsList();
        var skills = (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT skill_name FROM agent_version_skill_assignments WHERE agent_version_id=@versionId ORDER BY skill_name",
            new { versionId = version.VersionId }, cancellationToken: ct))).AsList();

        var (workflowOk2, workflow) = await TryResolveWorkflowAsync(conn, version.WorkflowId, ct);
        if (!workflowOk2) return new(null, "inactive_or_missing_workflow");
        var delegates = await ResolveDelegatesAsync(conn, version.VersionId, ct);
        return new(new AgentExecutionManifest(actorId, Guid.Empty,
            new ManifestAgent(agentRow.AgentId, agentRow.Slug, agentRow.Name, agentRow.Description, ParseJson(agentRow.IconMetadataJson)),
            version.VersionId, version.Version, version.Provider, version.Model, version.SystemPrompt, version.Temperature,
            version.MaxTurns, version.TimeoutSeconds, version.MaxTokens, ParseJson(version.MemoryConfigJson), tools, skills, [], workflow, delegates), null);
    }

    public async Task<ManifestResult> GetDelegateManifestAsync(Guid userId, Guid tenantId, Guid routerAgentId, Guid specialistAgentId, CancellationToken ct)
    {
        var routerResult = await GetManifestAsync(userId, tenantId, routerAgentId, ct);
        if (routerResult.Manifest is null) return new(null, "router_not_authorized");

        await using var conn = await _factory.OpenAsync(ct);
        var delegateAllowed = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM agent_version_delegates WHERE agent_version_id=@versionId AND specialist_agent_id=@specialistAgentId)",
            new { versionId = routerResult.Manifest.PublishedVersionId, specialistAgentId }, cancellationToken: ct));
        if (!delegateAllowed) return new(null, "specialist_not_authorized_for_router");

        var row = await conn.QuerySingleOrDefaultAsync<ManifestRow>(new CommandDefinition("""
            SELECT a.id AS AgentId,a.slug,a.name,a.description,a.icon_metadata::text AS IconMetadataJson,
              a.action_tier AS ActionTier,a.lifecycle_state AS LifecycleState,a.disabled_at IS NULL AS AgentEnabled,
              TRUE AS UserActive, t.status='active' AS TenantActive,
              EXISTS(SELECT 1 FROM agent_tenant_assignments ata WHERE ata.agent_id=a.id AND ata.tenant_id=@tenantId AND ata.status='active') AS TenantAssigned,
              TRUE AS UserAssigned,
              v.id AS VersionId,v.version,v.status AS VersionStatus,v.provider,v.model,v.system_prompt AS SystemPrompt,
              v.temperature,v.max_turns AS MaxTurns,v.timeout_seconds AS TimeoutSeconds,v.max_tokens AS MaxTokens,v.memory_config::text AS MemoryConfigJson,
              v.workflow_id AS WorkflowId
            FROM tenants t CROSS JOIN agents a LEFT JOIN agent_versions v ON v.id=a.published_version_id AND v.agent_id=a.id
            WHERE t.id=@tenantId AND a.id=@specialistAgentId
            """, new { tenantId, specialistAgentId }, cancellationToken: ct));
        if (row is null) return new(null, "not_found_or_cross_tenant");
        var denial = !row.TenantActive ? "tenant_disabled"
            : !row.TenantAssigned ? "specialist_not_assigned_to_tenant"
            : !row.AgentEnabled ? "agent_disabled" : row.LifecycleState != "production" ? "agent_not_production"
            : row.VersionId is null || row.VersionStatus != "published" ? "agent_not_published" : null;
        if (denial is not null) return new(null, denial);

        var tools = (await conn.QueryAsync<ManifestTool>(new CommandDefinition("""
            SELECT t.id AS Id,t.code AS Code,t.name AS Name,t.description AS Description,t.category AS Category,t.risk_tier AS RiskTier
            FROM agent_version_tool_grants g JOIN tools t ON t.id=g.tool_id AND t.is_active
            WHERE g.agent_version_id=@versionId AND
              CASE @actionTier WHEN 'autonomous' THEN 4 WHEN 'execute_with_approval' THEN 3 WHEN 'suggest' THEN 2 ELSE 1 END >=
              CASE t.min_action_tier WHEN 'autonomous' THEN 4 WHEN 'execute_with_approval' THEN 3 WHEN 'suggest' THEN 2 ELSE 1 END ORDER BY t.code
            """, new { versionId = row.VersionId, actionTier = row.ActionTier }, cancellationToken: ct))).AsList();
        var grantCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*)::int FROM agent_version_tool_grants WHERE agent_version_id=@versionId", new { versionId = row.VersionId }, cancellationToken: ct));
        if (grantCount != tools.Count) return new(null, "invalid_tool_configuration");
        var skills = (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT skill_name FROM agent_version_skill_assignments WHERE agent_version_id=@versionId ORDER BY skill_name",
            new { versionId = row.VersionId }, cancellationToken: ct))).AsList();
        var knowledge = (await conn.QueryAsync<ManifestKnowledge>(new CommandDefinition("""
            SELECT n.id AS NoteId,n.title AS Title,n.content AS Content,n.tags AS Tags
            FROM agent_knowledge ak JOIN notes n ON n.id=ak.note_id AND n.tenant_id=@tenantId
            WHERE ak.agent_id=@agentId ORDER BY ak.attached_at
            """, new { tenantId, agentId = specialistAgentId }, cancellationToken: ct))).AsList();
        var (workflowOk, workflow) = await TryResolveWorkflowAsync(conn, row.WorkflowId, ct);
        if (!workflowOk) return new(null, "inactive_or_missing_workflow");

        // Deliberately []: a specialist reached via delegation never itself
        // exposes further delegation, so a chain can't form even if the
        // specialist's own version has delegates configured (single-hop,
        // enforced here at the Gateway — not just left to Python).
        return new(new AgentExecutionManifest(userId, tenantId,
            new ManifestAgent(row.AgentId, row.Slug, row.Name, row.Description, ParseJson(row.IconMetadataJson)),
            row.VersionId!.Value, row.Version!.Value, row.Provider!, row.Model!, row.SystemPrompt!, row.Temperature!.Value,
            row.MaxTurns!.Value, row.TimeoutSeconds!.Value, row.MaxTokens!.Value, ParseJson(row.MemoryConfigJson!),
            tools, skills, knowledge, workflow, []), null);
    }

    private sealed record TestAgentRow(Guid AgentId, string Slug, string Name, string? Description, string IconMetadataJson, string ActionTier, Guid? VersionId);
    private sealed record TestVersionRow(Guid VersionId, int Version, string Provider, string Model, string SystemPrompt, decimal Temperature, int MaxTurns, int TimeoutSeconds, int MaxTokens, string MemoryConfigJson, Guid? WorkflowId);

    private static async Task<bool> IsTenantAgentAsync(NpgsqlConnection conn, Guid tenantId, Guid agentId, CancellationToken ct) =>
        await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM agent_tenant_assignments ata JOIN agents a ON a.id=ata.agent_id
              WHERE ata.agent_id=@agentId AND ata.tenant_id=@tenantId AND ata.status='active')
            """, new { tenantId, agentId }, cancellationToken: ct));

    private static JsonElement ParseJson(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task ReplaceCapabilitiesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid agentId, Guid versionId, Guid actorId, Guid[]? toolIds, string[]? skills, Guid[]? delegateAgentIds, CancellationToken ct)
    {
        if (toolIds is not null)
        {
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM agent_version_tool_grants WHERE agent_version_id=@versionId", new { versionId }, tx, cancellationToken: ct));
            foreach (var toolId in toolIds.Distinct())
                await conn.ExecuteAsync(new CommandDefinition("INSERT INTO agent_version_tool_grants(agent_version_id,tool_id,granted_by) VALUES(@versionId,@toolId,@actorId)", new { versionId, toolId, actorId }, tx, cancellationToken: ct));
        }
        if (skills is not null)
        {
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM agent_version_skill_assignments WHERE agent_version_id=@versionId", new { versionId }, tx, cancellationToken: ct));
            foreach (var skill in skills.Distinct(StringComparer.Ordinal))
                await conn.ExecuteAsync(new CommandDefinition("INSERT INTO agent_version_skill_assignments(agent_version_id,skill_name,assigned_by) VALUES(@versionId,@skill,@actorId)", new { versionId, skill, actorId }, tx, cancellationToken: ct));
        }
        if (delegateAgentIds is not null)
        {
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM agent_version_delegates WHERE agent_version_id=@versionId", new { versionId }, tx, cancellationToken: ct));
            foreach (var specialistId in delegateAgentIds.Distinct().Where(id => id != agentId))
                await conn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO agent_version_delegates(agent_version_id,specialist_agent_id,added_by)
                    SELECT @versionId,@specialistId,@actorId WHERE EXISTS(SELECT 1 FROM agents WHERE id=@specialistId AND scope='system')
                    """, new { versionId, specialistId, actorId }, tx, cancellationToken: ct));
        }
    }

    private static async Task<IReadOnlyList<AgentVersionRecord>> HydrateVersionsAsync(NpgsqlConnection conn, IReadOnlyList<VersionRow> rows, CancellationToken ct)
    {
        var result = new List<AgentVersionRecord>(rows.Count);
        foreach (var row in rows)
        {
            var tools = (await conn.QueryAsync<Guid>(new CommandDefinition("SELECT tool_id FROM agent_version_tool_grants WHERE agent_version_id=@id ORDER BY tool_id", new { row.Id }, cancellationToken: ct))).ToArray();
            var skills = (await conn.QueryAsync<string>(new CommandDefinition("SELECT skill_name FROM agent_version_skill_assignments WHERE agent_version_id=@id ORDER BY skill_name", new { row.Id }, cancellationToken: ct))).ToArray();
            var delegates = (await conn.QueryAsync<Guid>(new CommandDefinition("SELECT specialist_agent_id FROM agent_version_delegates WHERE agent_version_id=@id ORDER BY specialist_agent_id", new { row.Id }, cancellationToken: ct))).ToArray();
            result.Add(new(row.Id,row.AgentId,row.Version,row.Status,row.Provider,row.Model,row.SystemPrompt,row.Temperature,row.MaxTurns,row.TimeoutSeconds,row.MaxTokens,row.MemoryConfigJson,row.CreatedBy,row.CreatedAt,row.PublishedBy,row.PublishedAt,tools,skills,row.WorkflowId,delegates));
        }
        return result;
    }

    private sealed record VersionRow(Guid Id, Guid AgentId, int Version, string Status, string Provider, string Model, string SystemPrompt, decimal Temperature, int MaxTurns, int TimeoutSeconds, int MaxTokens, string MemoryConfigJson, Guid? CreatedBy, DateTime CreatedAt, Guid? PublishedBy, DateTime? PublishedAt, Guid? WorkflowId);
    private sealed record ValidationRow(string Provider, string Model, string SystemPrompt, Guid? WorkflowId, long ToolCount, long ValidToolCount, bool WorkflowValid);
    private sealed record ManifestRow(Guid AgentId,string Slug,string Name,string? Description,string IconMetadataJson,string ActionTier,string LifecycleState,bool AgentEnabled,bool UserActive,bool TenantActive,bool TenantAssigned,bool UserAssigned,Guid? VersionId,int? Version,string? VersionStatus,string? Provider,string? Model,string? SystemPrompt,decimal? Temperature,int? MaxTurns,int? TimeoutSeconds,int? MaxTokens,string? MemoryConfigJson,Guid? WorkflowId);
}
