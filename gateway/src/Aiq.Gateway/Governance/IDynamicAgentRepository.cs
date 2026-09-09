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
    Guid? WorkflowId, Guid[] DelegateAgentIds,
    // D4: business capabilities ("nghiệp vụ") this version declares support
    // for — same immutable-once-published shape as ToolIds/Skills.
    Guid[] CapabilityIds);

public sealed record CreateSystemAgentRequest(
    [property: Required, RegularExpression("^[a-z0-9]+(?:-[a-z0-9]+)*$"), StringLength(80, MinimumLength = 2)] string Slug,
    [property: Required, StringLength(160)] string Name,
    [property: StringLength(4000)] string? Description,
    JsonElement? IconMetadata,
    [property: RegularExpression("^(orchestrator|domain|sub|tool_only)$")] string? Type,
    [property: RegularExpression("^(read_only|suggest|execute_with_approval|autonomous)$")] string? ActionTier);

public sealed record UpdateSystemAgentRequest(
    [property: StringLength(160)] string? Name,
    [property: StringLength(4000)] string? Description,
    JsonElement? IconMetadata,
    [property: RegularExpression("^(read_only|suggest|execute_with_approval|autonomous)$")] string? ActionTier);

public sealed record CreateAgentVersionRequest(
    [property: Required, RegularExpression("^(ollama|claude_sdk|anthropic)$")] string Provider,
    [property: Required, StringLength(200, MinimumLength = 1)] string Model,
    [property: Required] string SystemPrompt,
    [property: Range(0d, 2d)] decimal Temperature,
    [property: Range(1, 20)] int MaxTurns,
    [property: Range(1, 3600)] int TimeoutSeconds,
    [property: Range(1, 1000000)] int MaxTokens,
    JsonElement? MemoryConfig,
    Guid[]? ToolIds,
    string[]? Skills,
    Guid? WorkflowId,
    Guid[]? DelegateAgentIds,
    Guid[]? CapabilityIds);

public sealed record UpdateAgentVersionRequest(
    [property: RegularExpression("^(ollama|claude_sdk|anthropic)$")] string? Provider,
    [property: StringLength(200, MinimumLength = 1)] string? Model,
    string? SystemPrompt,
    [property: Range(0d, 2d)] decimal? Temperature,
    [property: Range(1, 20)] int? MaxTurns,
    [property: Range(1, 3600)] int? TimeoutSeconds,
    [property: Range(1, 1000000)] int? MaxTokens,
    JsonElement? MemoryConfig,
    Guid[]? ToolIds,
    string[]? Skills,
    bool? ClearWorkflow,
    Guid? WorkflowId,
    Guid[]? DelegateAgentIds,
    Guid[]? CapabilityIds);

public sealed record AgentVersionValidation(bool Valid, string[] Errors);
public sealed record AgentTenantAssignmentRecord(Guid TenantId, string TenantCode, string TenantName, DateTime AssignedAt);
public sealed record TenantAssignedAgentRecord(Guid AgentId, string Slug, string Name, string? Description, string LifecycleState, bool Enabled, DateTime AssignedAt);
public sealed record AgentUserAssignmentRecord(Guid UserId, string Email, string? DisplayName, DateTime AssignedAt);

/// <summary>
/// D3: a tenant-entitled agent annotated with whether it is currently
/// actively assigned to ONE specific department — powers TENANT_OWNER's
/// department-agent assignment screen (GET /tenant/departments/{id}/agents),
/// which needs to show both assignable and already-assigned agents in one list.
/// </summary>
public sealed record AgentDepartmentAssignmentRecord(
    Guid AgentId, string Slug, string Name, string? Description,
    string LifecycleState, bool Enabled, bool Assigned, DateTime? AssignedAt);

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
    string? ToolExecutionToken = null,
    // D3: the caller's own department at execution time (ADMIN or USER —
    // both are department members). Audit/thread-key context only, never an
    // authorization input on the Agent side — Gateway has already made the
    // department-entitlement decision by the time this manifest is issued.
    // Null for the departmentless SUPER_ADMIN test path.
    Guid? DepartmentId = null);
public sealed record ManifestResult(AgentExecutionManifest? Manifest, string? DenialReason);

/// <summary>
/// ACL V2-G: one legacy `scope='tenant'` agent still quarantined by
/// migration 024's classification (`v2_scope IS NULL`) — never resolved to
/// a Department Agent because it had zero or multiple active department
/// entitlements at classification time. Read-only inventory; never a
/// candidate for automatic reassignment.
/// </summary>
public sealed record QuarantinedAgentRecord(
    Guid Id, string Slug, string Name, Guid OwningTenantId, string V2MigrationStatus,
    string? V2MigrationNote, int ActiveDepartmentCount, string[] ActiveDepartmentIds);

/// <summary>
/// ACL V2-G: the outcome of cloning one `quarantined_shared` legacy agent
/// into a new, structurally-owned Department Agent for one of the
/// departments that already held (and keeps holding) an active entitlement
/// to it. See CloneSharedAgentToDepartmentAsync for the full policy.
/// </summary>
public sealed record CloneAgentResult(Guid ClonedAgentId, Guid ClonedVersionId, string ClonedSlug);

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
    /// ACL V2-G: read-only inventory of every legacy agent migration 024
    /// could not deterministically resolve (shared across departments, or
    /// with none) — SUPER_ADMIN reviews this before deciding whether/how to
    /// clone. Never assigns ownership itself.
    /// </summary>
    Task<IReadOnlyList<QuarantinedAgentRecord>> ListQuarantinedAgentsAsync(CancellationToken ct);

    /// <summary>
    /// ACL V2-G: clones a `quarantined_shared` legacy agent into a brand
    /// new, structurally-owned Department Agent for ONE specific
    /// tenant+department that already holds an active
    /// agent_department_assignments entitlement to it — "shared across
    /// multiple departments -> do NOT assign to one department, create
    /// per-department clones where safe" (final-phase brief §PART A). The
    /// clone carries `source_agent_id` lineage, copies the source's current
    /// PUBLISHED version's full configuration (provider/model/prompt/tool
    /// grants/skill assignments/capability declarations/workflow reference)
    /// as its own immediately-published v1 (preserving continuity of
    /// service for that department), rebinds this department's USER
    /// assignments and capability bindings from the shared original onto
    /// the clone, and copies (never moves) Knowledge attachments. The
    /// source agent, its other departments' assignments/bindings, and all
    /// audit/execution history are left completely untouched — nothing is
    /// deleted. Idempotent: a second call for the same source+department
    /// returns null (a clone with this source_agent_id+department_id
    /// already exists) rather than creating a duplicate.
    /// </summary>
    Task<CloneAgentResult?> CloneSharedAgentToDepartmentAsync(
        Guid actorId, Guid sourceAgentId, Guid targetTenantId, Guid targetDepartmentId, CancellationToken ct);

    /// <summary>
    /// System agents assigned to a single tenant — tenantId must come from
    /// server-side auth context. Powers TENANT_OWNER's tenant-wide agent
    /// management view (see also ListAgentsForDepartmentAsync for ADMIN's
    /// department-scoped equivalent).
    /// </summary>
    Task<IReadOnlyList<TenantAssignedAgentRecord>> ListAgentsForTenantAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// D3: agents ACTIVELY assigned to one department — the canonical query
    /// behind ADMIN's own "/tenant/agents" view (never fetch all tenant
    /// agents and filter in Web). Same shape as ListAgentsForTenantAsync so
    /// both branches of DynamicAgentEndpoints.ListTenantAgentsAsync return
    /// identical JSON regardless of caller role.
    /// </summary>
    Task<IReadOnlyList<TenantAssignedAgentRecord>> ListAgentsForDepartmentAsync(Guid tenantId, Guid departmentId, CancellationToken ct);

    /// <summary>
    /// D3 TENANT_OWNER management view: every agent actively entitled to the
    /// tenant, each annotated with whether it is also actively assigned to
    /// ONE specific department — powers the assign/revoke screen without a
    /// second round trip. departmentId must belong to tenantId (validated by
    /// the caller via IDepartmentRepository before this runs).
    /// </summary>
    Task<IReadOnlyList<AgentDepartmentAssignmentRecord>> ListDepartmentEntitlementsAsync(Guid tenantId, Guid departmentId, CancellationToken ct);

    /// <summary>
    /// TENANT_OWNER-only. Idempotent PUT semantics matching AssignTenantAsync:
    /// requires an active TENANT entitlement for this agent first (enforced
    /// both here and by the DB trigger as defense in depth), and an
    /// already-active row is a no-op success rather than an error.
    /// </summary>
    Task<bool> AssignDepartmentAgentAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid actorId, CancellationToken ct);

    /// <summary>
    /// TENANT_OWNER-only. Revokes the department entitlement AND, in the
    /// same transaction, every currently-active direct USER assignment
    /// (agent_assignments) for that agent held by a user in this department
    /// — mirrors RevokeTenantAsync's existing cascade to agent_assignments,
    /// one level down. A revoked USER row is history, never deleted.
    /// </summary>
    Task<bool> RevokeDepartmentAgentAsync(Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct);

    Task<IReadOnlyList<AgentUserAssignmentRecord>?> ListAssignedUsersAsync(Guid tenantId, Guid agentId, CancellationToken ct);
    Task<IReadOnlyList<AgentUserAssignmentRecord>?> ListAssignedDepartmentUsersAsync(Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct);
    Task<bool> AssignUserAsync(Guid tenantId, Guid agentId, Guid userId, Guid actorId, CancellationToken ct);

    /// <summary>
    /// D3: an ADMIN may only ever assign a USER in their OWN department to an
    /// agent that is itself actively assigned to that SAME department (never
    /// just tenant-entitled) — the WHERE clause checks agent_department_assignments,
    /// not agent_tenant_assignments, so a tenant-only-entitled agent that was
    /// never handed to this department cannot be assigned to its users.
    /// </summary>
    Task<bool> AssignDepartmentUserAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid userId, Guid actorId, CancellationToken ct);
    Task<bool> RevokeUserAsync(Guid tenantId, Guid agentId, Guid userId, CancellationToken ct);
    Task<bool> RevokeDepartmentUserAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid userId, CancellationToken ct);

    /// <summary>
    /// USER production execution: tenant entitlement + department
    /// entitlement + a direct agent_assignments row, all active, on a
    /// published/enabled agent. departmentId is the caller's own
    /// server-derived department (AuthenticatedPrincipalContext), never
    /// client-supplied.
    /// </summary>
    Task<ManifestResult> GetManifestAsync(Guid userId, Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct);

    /// <summary>
    /// D3 ADMIN production execution: tenant entitlement + department
    /// entitlement, on a published/enabled agent — deliberately does NOT
    /// require a direct agent_assignments row (an admin manages the
    /// department, they don't need to be individually granted its agents).
    /// Any existing direct ADMIN assignment is ignored here, never required
    /// and never revoked.
    /// </summary>
    Task<ManifestResult> GetAdminManifestAsync(Guid actorId, Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct);

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
    /// Delegation remains USER-only in D3 (routerAgentId re-authorization
    /// goes through the same GetManifestAsync USER chain, department
    /// included) — an ADMIN-router delegation path is not part of this phase.
    /// </summary>
    Task<ManifestResult> GetDelegateManifestAsync(Guid userId, Guid tenantId, Guid departmentId, Guid routerAgentId, Guid specialistAgentId, CancellationToken ct);

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

    // ======================================================================
    // ACL V2-C — Department Agent ownership. A Department Agent's tenant and
    // department are structural (agents.v2_scope='department',
    // owning_tenant_id, department_id — see migration 024), never an
    // entitlement inferred from agent_tenant_assignments/
    // agent_department_assignments the way SUPER_ADMIN's scope='system'
    // agents work above. Every method here filters
    // (v2_scope='department' AND owning_tenant_id=@tenantId AND
    // department_id=@departmentId) instead of scope='system' — a Department
    // Agent belonging to another department is invisible to these queries
    // even given its raw UUID, exactly like D2/D3's existing department
    // isolation elsewhere. CreateSystemAgentRequest/UpdateSystemAgentRequest/
    // CreateAgentVersionRequest/UpdateAgentVersionRequest/
    // AgentVersionValidation/SystemAgentRecord/AgentVersionRecord are reused
    // as-is — the shape ADMIN needs is identical to SUPER_ADMIN's, only the
    // ownership/scope story differs, so a parallel DTO set would be pure
    // duplication.
    // ======================================================================

    Task<IReadOnlyList<SystemAgentRecord>> ListDepartmentAgentsAsync(Guid tenantId, Guid departmentId, CancellationToken ct);
    Task<SystemAgentRecord?> FindDepartmentAgentAsync(Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct);

    /// <summary>
    /// tenant_id/department_id/created_by/scope are never accepted from the
    /// request DTO (§8 of the V2-C brief) — they come exclusively from the
    /// caller's AuthenticatedPrincipalContext, passed in explicitly here.
    /// </summary>
    Task<SystemAgentRecord> CreateDepartmentAgentAsync(Guid tenantId, Guid departmentId, Guid actorId, CreateSystemAgentRequest request, CancellationToken ct);
    Task<SystemAgentRecord?> UpdateDepartmentAgentAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid actorId, UpdateSystemAgentRequest request, CancellationToken ct);
    Task<SystemAgentRecord?> SetDepartmentAgentEnabledAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid actorId, bool enabled, CancellationToken ct);

    Task<IReadOnlyList<AgentVersionRecord>> ListDepartmentAgentVersionsAsync(Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct);
    Task<AgentVersionRecord?> FindDepartmentAgentVersionAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid versionId, CancellationToken ct);
    Task<AgentVersionRecord?> CreateDepartmentAgentVersionAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid actorId, CreateAgentVersionRequest request, CancellationToken ct);
    Task<AgentVersionRecord?> UpdateDepartmentAgentVersionAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid versionId, Guid actorId, UpdateAgentVersionRequest request, CancellationToken ct);
    Task<AgentVersionValidation?> ValidateDepartmentAgentVersionAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid versionId, CancellationToken ct);
    Task<AgentVersionRecord?> PublishDepartmentAgentVersionAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid versionId, Guid actorId, CancellationToken ct);

    /// <summary>Users in the caller's own department currently assigned this Department Agent.</summary>
    Task<IReadOnlyList<AgentUserAssignmentRecord>?> ListDepartmentAgentAssignedUsersAsync(Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct);

    /// <summary>
    /// ADMIN assigns one of its OWN department's USERs to its OWN Department
    /// Agent. Reuses the existing agent_assignments table (a generic
    /// tenant+agent+user grant, not itself scope-specific) but with a
    /// precondition checking structural agent ownership instead of
    /// agent_tenant_assignments — that legacy entitlement table can never
    /// hold a row for a Department Agent (its own INSERT requires
    /// scope='system', see AssignUserAsync above; scope='system' and a
    /// non-null owning_tenant_id/department_id are mutually exclusive by
    /// migration 007's own constraint).
    /// </summary>
    Task<bool> AssignUserToDepartmentAgentAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid userId, Guid actorId, CancellationToken ct);
    Task<bool> RevokeUserFromDepartmentAgentAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid userId, CancellationToken ct);

    /// <summary>
    /// Classifies an agent id for GET /runtime/agents/{id}/manifest's
    /// dispatch: true only for a real, resolved V2 Department Agent (any
    /// department) — never guessed from legacy scope='tenant' rows still
    /// mid-migration (v2_migration_status <> 'resolved' stays on the legacy
    /// entitlement-chain path). False (including "not found") always falls
    /// through to the existing scope='system' entitlement-chain resolution
    /// unchanged, so this check can never narrow what already worked.
    /// </summary>
    Task<bool> IsResolvedDepartmentAgentAsync(Guid agentId, CancellationToken ct);

    /// <summary>
    /// Production root for a Department Agent: ownership (v2_scope, tenant,
    /// department) IS the authorization, not an inferred entitlement. ADMIN
    /// (isAdmin=true) executes automatically; USER (isAdmin=false) still
    /// needs an active agent_assignments row from AssignUserToDepartmentAgentAsync.
    /// Published-only for both — draft execution is GetDepartmentAgentTestManifestAsync's job.
    /// </summary>
    Task<ManifestResult> GetDepartmentAgentManifestAsync(Guid userId, Guid tenantId, Guid departmentId, Guid agentId, bool isAdmin, CancellationToken ct);

    /// <summary>
    /// ADMIN-only draft-or-published test resolution for a Department Agent
    /// this ADMIN's own department owns — same "no assignment gate, current
    /// draft first" shape as GetTestManifestAsync's SUPER_ADMIN template
    /// test, but scoped to structural department ownership instead of global
    /// scope='system' access, and reachable only via
    /// GET /runtime/agents/{id}/manifest?mode=test (see DynamicAgentEndpoints.ManifestAsync).
    /// </summary>
    Task<ManifestResult> GetDepartmentAgentTestManifestAsync(Guid actorId, Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct);
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
        await ReplaceCapabilitiesAsync(conn, tx, agentId, row.Id, actorId, request.ToolIds, request.Skills, request.DelegateAgentIds, request.CapabilityIds, ct);
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
        await ReplaceCapabilitiesAsync(conn, tx, agentId, versionId, actorId, request.ToolIds, request.Skills, request.DelegateAgentIds, request.CapabilityIds, ct);
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
              (v.workflow_id IS NULL OR EXISTS(SELECT 1 FROM workflows w WHERE w.id=v.workflow_id AND w.is_active)) AS WorkflowValid,
              (SELECT COUNT(*) FROM agent_version_capabilities avc WHERE avc.agent_version_id=v.id) AS CapabilityCount,
              (SELECT COUNT(*) FROM agent_version_capabilities avc JOIN business_capabilities bc ON bc.id=avc.capability_id
                WHERE avc.agent_version_id=v.id AND bc.is_active) AS ValidCapabilityCount
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
        if (row.CapabilityCount != row.ValidCapabilityCount) errors.Add("inactive_capability");
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
                AND NOT EXISTS (
                  SELECT 1 FROM agent_version_capabilities avc LEFT JOIN business_capabilities bc ON bc.id=avc.capability_id
                  WHERE avc.agent_version_id=v.id AND (bc.id IS NULL OR NOT bc.is_active)
                )
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

        // PUT is idempotent: an already-active assignment is already in the
        // requested state and neither consumes additional quota nor writes a
        // duplicate history row.
        var alreadyActive = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM agent_tenant_assignments WHERE agent_id=@agentId AND tenant_id=@tenantId AND status='active')",
            new { agentId, tenantId }, cancellationToken: ct));
        if (alreadyActive) return true;

        // A revoked row is historical, not active. Reassignment inserts a new
        // active row (the partial unique index permits this) and therefore uses
        // one current quota slot just like any other new active assignment.
        var quota = await conn.QuerySingleOrDefaultAsync<TenantAgentQuotaRow>(new CommandDefinition("""
            SELECT t.max_agents AS MaxAgents,
                (SELECT COUNT(*)::int FROM agent_tenant_assignments a WHERE a.tenant_id=@tenantId AND a.status='active') AS Current
            FROM tenants t WHERE t.id=@tenantId AND t.status='active'
            """, new { tenantId }, cancellationToken: ct));
        if (quota is not null && !TenantQuotaPolicy.HasRoomFor(quota.MaxAgents, quota.Current))
            throw new InvalidOperationException("agent_quota_exceeded");

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
        // D3: cascade to BOTH descendants of the tenant entitlement — every
        // department's entitlement to this agent, and every direct user
        // assignment — in the same transaction, so neither can outlive the
        // tenant entitlement they depend on. History is preserved (status
        // flip, never delete) exactly like the existing agent_assignments cascade.
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE agent_department_assignments SET status='revoked',revoked_at=NOW()
            WHERE agent_id=@agentId AND tenant_id=@tenantId AND status='active'
            """, new { agentId, tenantId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE agent_assignments SET status='revoked',revoked_at=NOW()
            WHERE agent_id=@agentId AND tenant_id=@tenantId AND status='active'
            """, new { agentId, tenantId }, tx, cancellationToken: ct));
        // D5: same reasoning as RevokeDepartmentAgentAsync's own new
        // cascade — a capability binding can never outlive the department
        // entitlement it depends on, and that entitlement was just revoked
        // for every department above, not only one.
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE department_capability_bindings SET status='revoked',revoked_at=NOW()
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

    public async Task<IReadOnlyList<QuarantinedAgentRecord>> ListQuarantinedAgentsAsync(CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<QuarantinedAgentRecord>(new CommandDefinition("""
            SELECT a.id AS Id, a.slug AS Slug, a.name AS Name, a.owning_tenant_id AS OwningTenantId,
                   a.v2_migration_status AS V2MigrationStatus, a.v2_migration_note AS V2MigrationNote,
                   COALESCE((issues.details->>'activeDepartmentCount')::int, 0) AS ActiveDepartmentCount,
                   COALESCE(ARRAY(SELECT x FROM jsonb_array_elements_text(
                       COALESCE(issues.details->'activeDepartmentIds', '[]'::jsonb)) x),
                       ARRAY[]::text[]) AS ActiveDepartmentIds
            FROM agents a
            LEFT JOIN acl_v2_agent_scope_issues issues ON issues.resource_id = a.id
            WHERE a.v2_scope IS NULL
            ORDER BY a.v2_migration_status, a.name
            """, cancellationToken: ct))).AsList();
    }

    /// <summary>Clone-eligibility source-agent snapshot: current published version's full config.</summary>
    private sealed record CloneSourceRow(
        Guid AgentId, string Slug, string Name, string? Description, string IconMetadataJson,
        string Type, string ActionTier, Guid PublishedVersionId, string Provider, string Model,
        string SystemPrompt, decimal Temperature, int MaxTurns, int TimeoutSeconds, int MaxTokens,
        string MemoryConfigJson, Guid? WorkflowId);

    public async Task<CloneAgentResult?> CloneSharedAgentToDepartmentAsync(
        Guid actorId, Guid sourceAgentId, Guid targetTenantId, Guid targetDepartmentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Eligibility: source must genuinely be a still-quarantined shared
        // legacy agent, and the target department must ALREADY hold an
        // active entitlement to it — a clone is never a way to GRANT new
        // access, only to structurally re-home access that already exists.
        var eligible = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (
                SELECT 1 FROM agents a
                WHERE a.id=@sourceAgentId AND a.scope='tenant' AND a.v2_scope IS NULL
                  AND a.v2_migration_status='quarantined_shared'
                  AND a.owning_tenant_id=@targetTenantId
                  AND a.published_version_id IS NOT NULL
            ) AND EXISTS (
                SELECT 1 FROM agent_department_assignments ada
                WHERE ada.agent_id=@sourceAgentId AND ada.tenant_id=@targetTenantId
                  AND ada.department_id=@targetDepartmentId AND ada.status='active'
            )
            """, new { sourceAgentId, targetTenantId, targetDepartmentId }, tx, cancellationToken: ct));
        if (!eligible) { await tx.RollbackAsync(ct); return null; }

        // Idempotency: never create a second clone of the same source for
        // the same department.
        var alreadyCloned = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM agents WHERE source_agent_id=@sourceAgentId
                AND owning_tenant_id=@targetTenantId AND department_id=@targetDepartmentId)
            """, new { sourceAgentId, targetTenantId, targetDepartmentId }, tx, cancellationToken: ct));
        if (alreadyCloned) { await tx.RollbackAsync(ct); return null; }

        var source = await conn.QuerySingleAsync<CloneSourceRow>(new CommandDefinition("""
            SELECT a.id AS AgentId, a.slug AS Slug, a.name AS Name, a.description AS Description,
                   a.icon_metadata::text AS IconMetadataJson, a.type AS Type, a.action_tier AS ActionTier,
                   v.id AS PublishedVersionId, v.provider AS Provider, v.model AS Model,
                   v.system_prompt AS SystemPrompt, v.temperature AS Temperature, v.max_turns AS MaxTurns,
                   v.timeout_seconds AS TimeoutSeconds, v.max_tokens AS MaxTokens,
                   v.memory_config::text AS MemoryConfigJson, v.workflow_id AS WorkflowId
            FROM agents a JOIN agent_versions v ON v.id=a.published_version_id
            WHERE a.id=@sourceAgentId
            """, new { sourceAgentId }, tx, cancellationToken: ct));

        var deptCode = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT code FROM departments WHERE id=@targetDepartmentId", new { targetDepartmentId }, tx, cancellationToken: ct));
        var cloneSlug = $"{source.Slug}-{deptCode}";

        var cloneAgentId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO agents (tenant_id, code, slug, name, description, icon_metadata, scope,
                owning_tenant_id, department_id, v2_scope, v2_migration_status, v2_migration_note, source_agent_id,
                type, action_tier, lifecycle_state, config, created_by, updated_by)
            VALUES (@targetTenantId, @cloneSlug, @cloneSlug, @Name, @Description, @IconMetadataJson::jsonb, 'tenant',
                @targetTenantId, @targetDepartmentId, 'department', 'resolved',
                'V2-G: cloned from shared legacy agent for one department', @sourceAgentId,
                @Type, @ActionTier, 'draft', '{}'::jsonb, @actorId, @actorId)
            RETURNING id
            """, new
        {
            targetTenantId, targetDepartmentId, cloneSlug, source.Name, source.Description,
            source.IconMetadataJson, sourceAgentId, source.Type, source.ActionTier, actorId,
        }, tx, cancellationToken: ct));

        // trg_validate_agent_version_pointers (migration 011) is a DEFERRED
        // constraint trigger: it queues one check per UPDATE of
        // agents.current_draft_version_id/published_version_id, but each
        // queued check re-runs its own lookup query against LIVE data at
        // COMMIT time, not a snapshot from when it was queued. The INSERT
        // above's AFTER INSERT trigger (008) just set current_draft_version_id
        // to the auto-created draft — that check is valid RIGHT NOW (the
        // draft genuinely exists) but would go stale and fail at commit once
        // this same version is flipped to 'published' below. Forcing it
        // IMMEDIATE here discharges it while it's still true, before this
        // transaction does anything that would invalidate it.
        await conn.ExecuteAsync(new CommandDefinition(
            "SET CONSTRAINTS trg_validate_agent_version_pointers IMMEDIATE", transaction: tx, cancellationToken: ct));

        // trg_create_initial_agent_version (migration 008, AFTER INSERT on
        // agents) already created a draft v1 for the clone the moment it
        // was inserted above — update that row in place rather than
        // inserting a second version 1 (which previously violated
        // agent_versions_agent_id_version_key here). The immutability
        // triggers on agent_version_tool_grants/agent_version_skill_assignments/
        // agent_version_capabilities all reject inserts once their owning
        // version is 'published', so the copy step below must land while
        // the clone's own version is still this draft — only THEN is it
        // flipped to 'published', exactly mirroring the ordinary
        // create-draft -> attach -> publish lifecycle every other Agent
        // version goes through, just performed atomically in one transaction.
        var cloneVersionId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "SELECT current_draft_version_id FROM agents WHERE id=@cloneAgentId",
            new { cloneAgentId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE agent_versions SET provider=@Provider, model=@Model, system_prompt=@SystemPrompt,
                temperature=@Temperature, max_turns=@MaxTurns, timeout_seconds=@TimeoutSeconds,
                max_tokens=@MaxTokens, memory_config=@MemoryConfigJson::jsonb, workflow_id=@WorkflowId
            WHERE id=@cloneVersionId
            """, new
        {
            cloneVersionId, source.Provider, source.Model, source.SystemPrompt, source.Temperature,
            source.MaxTurns, source.TimeoutSeconds, source.MaxTokens, source.MemoryConfigJson,
            source.WorkflowId,
        }, tx, cancellationToken: ct));

        // Copy the source's published-version tool grants / skill
        // assignments / capability declarations onto the clone's own new
        // version — a genuine copy, not a reference, matching the clone's
        // own independent immutable-version lifecycle going forward.
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO agent_version_tool_grants (agent_version_id, tool_id, granted_by)
            SELECT @cloneVersionId, tool_id, @actorId FROM agent_version_tool_grants WHERE agent_version_id=@sourceVersionId
            """, new { cloneVersionId, sourceVersionId = source.PublishedVersionId, actorId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO agent_version_skill_assignments (agent_version_id, skill_name, assigned_by)
            SELECT @cloneVersionId, skill_name, @actorId FROM agent_version_skill_assignments WHERE agent_version_id=@sourceVersionId
            """, new { cloneVersionId, sourceVersionId = source.PublishedVersionId, actorId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO agent_version_capabilities (agent_version_id, capability_id, assigned_by)
            SELECT @cloneVersionId, capability_id, @actorId FROM agent_version_capabilities WHERE agent_version_id=@sourceVersionId
            """, new { cloneVersionId, sourceVersionId = source.PublishedVersionId, actorId }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE agent_versions SET status='published', published_by=@actorId, published_at=NOW() WHERE id=@cloneVersionId
            """, new { cloneVersionId, actorId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE agents SET published_version_id=@cloneVersionId, current_draft_version_id=NULL,
                lifecycle_state='production', updated_by=@actorId WHERE id=@cloneAgentId
            """, new { cloneAgentId, cloneVersionId, actorId }, tx, cancellationToken: ct));

        // Rebind (not duplicate) this department's own USER assignments and
        // capability bindings from the shared original onto the clone —
        // other departments' assignments/bindings on the source are
        // untouched.
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE agent_assignments SET agent_id=@cloneAgentId
            WHERE agent_id=@sourceAgentId AND tenant_id=@targetTenantId
              AND user_id IN (SELECT id FROM users WHERE department_id=@targetDepartmentId)
            """, new { cloneAgentId, sourceAgentId, targetTenantId, targetDepartmentId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE department_capability_bindings SET agent_id=@cloneAgentId
            WHERE agent_id=@sourceAgentId AND tenant_id=@targetTenantId AND department_id=@targetDepartmentId
            """, new { cloneAgentId, sourceAgentId, targetTenantId, targetDepartmentId }, tx, cancellationToken: ct));

        // Copy (never move/delete) Knowledge attachments — the source
        // remains fully intact for its other departments and its own
        // quarantined-shared history.
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO agent_knowledge (agent_id, note_id, attached_by)
            SELECT @cloneAgentId, note_id, attached_by FROM agent_knowledge WHERE agent_id=@sourceAgentId
            ON CONFLICT (agent_id, note_id) DO NOTHING
            """, new { cloneAgentId, sourceAgentId }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return new CloneAgentResult(cloneAgentId, cloneVersionId, cloneSlug);
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

    public async Task<IReadOnlyList<TenantAssignedAgentRecord>> ListAgentsForDepartmentAsync(Guid tenantId, Guid departmentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<TenantAssignedAgentRecord>(new CommandDefinition("""
            SELECT a.id AS AgentId, a.slug AS Slug, a.name AS Name, a.description AS Description,
              a.lifecycle_state AS LifecycleState, (a.disabled_at IS NULL) AS Enabled, ada.assigned_at AS AssignedAt
            FROM agent_department_assignments ada JOIN agents a ON a.id=ada.agent_id AND a.scope='system'
            WHERE ada.tenant_id=@tenantId AND ada.department_id=@departmentId AND ada.status='active'
            ORDER BY a.name
            """, new { tenantId, departmentId }, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<AgentDepartmentAssignmentRecord>> ListDepartmentEntitlementsAsync(Guid tenantId, Guid departmentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<AgentDepartmentAssignmentRecord>(new CommandDefinition("""
            SELECT a.id AS AgentId, a.slug AS Slug, a.name AS Name, a.description AS Description,
              a.lifecycle_state AS LifecycleState, (a.disabled_at IS NULL) AS Enabled,
              (ada.id IS NOT NULL) AS Assigned, ada.assigned_at AS AssignedAt
            FROM agent_tenant_assignments ata
            JOIN agents a ON a.id=ata.agent_id AND a.scope='system'
            LEFT JOIN agent_department_assignments ada
              ON ada.agent_id=a.id AND ada.tenant_id=@tenantId AND ada.department_id=@departmentId AND ada.status='active'
            WHERE ata.tenant_id=@tenantId AND ata.status='active'
            ORDER BY a.name
            """, new { tenantId, departmentId }, cancellationToken: ct))).AsList();
    }

    public async Task<bool> AssignDepartmentAgentAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid actorId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);

        // PUT is idempotent, matching AssignTenantAsync: an already-active
        // assignment is already in the requested state.
        var alreadyActive = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM agent_department_assignments WHERE department_id=@departmentId AND agent_id=@agentId AND status='active')",
            new { departmentId, agentId }, cancellationToken: ct));
        if (alreadyActive) return true;

        var n = await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO agent_department_assignments(tenant_id,department_id,agent_id,status,assigned_by)
            SELECT @tenantId,@departmentId,@agentId,'active',@actorId
            WHERE EXISTS(SELECT 1 FROM agent_tenant_assignments WHERE agent_id=@agentId AND tenant_id=@tenantId AND status='active')
              AND EXISTS(SELECT 1 FROM departments WHERE id=@departmentId AND tenant_id=@tenantId AND status='active')
            ON CONFLICT (department_id,agent_id) WHERE status='active' DO NOTHING
            """, new { tenantId, departmentId, agentId, actorId }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<bool> RevokeDepartmentAgentAsync(Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE agent_department_assignments SET status='revoked',revoked_at=NOW()
            WHERE tenant_id=@tenantId AND department_id=@departmentId AND agent_id=@agentId AND status='active'
            """, new { tenantId, departmentId, agentId }, tx, cancellationToken: ct));
        // Preferred behavior from the D3 spec: revoke child USER assignments
        // transactionally rather than leaving them active-but-ineffective —
        // same "cascade, preserve history" shape as RevokeTenantAsync above,
        // scoped to just this department's users.
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE agent_assignments aa SET status='revoked',revoked_at=NOW()
            WHERE aa.tenant_id=@tenantId AND aa.agent_id=@agentId AND aa.status='active'
              AND EXISTS(SELECT 1 FROM users u WHERE u.id=aa.user_id AND u.tenant_id=@tenantId AND u.department_id=@departmentId)
            """, new { tenantId, departmentId, agentId }, tx, cancellationToken: ct));
        // D5: this predates D4's capability bindings — without this cascade,
        // revoking the department entitlement here would leave an active
        // department_capability_bindings row pointing at an agent no longer
        // entitled to this department (its own eligibility trigger only
        // guards INSERT/UPDATE, not a revoke happening on the OTHER table).
        // Same "cascade, preserve history" shape as the two updates above.
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE department_capability_bindings SET status='revoked',revoked_at=NOW()
            WHERE tenant_id=@tenantId AND department_id=@departmentId AND agent_id=@agentId AND status='active'
            """, new { tenantId, departmentId, agentId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return n > 0;
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

    // ======================================================================
    // ACL V2-C — Department Agent ownership implementation.
    // ======================================================================
    private const string DepartmentOwnershipWhere =
        "a.v2_scope='department' AND a.owning_tenant_id=@tenantId AND a.department_id=@departmentId";

    public async Task<IReadOnlyList<SystemAgentRecord>> ListDepartmentAgentsAsync(Guid tenantId, Guid departmentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<SystemAgentRecord>(new CommandDefinition(
            $"SELECT {AgentColumns} FROM agents a WHERE {DepartmentOwnershipWhere} ORDER BY a.created_at DESC",
            new { tenantId, departmentId }, cancellationToken: ct))).AsList();
    }

    public async Task<SystemAgentRecord?> FindDepartmentAgentAsync(Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<SystemAgentRecord>(new CommandDefinition(
            $"SELECT {AgentColumns} FROM agents a WHERE {DepartmentOwnershipWhere} AND a.id=@agentId",
            new { tenantId, departmentId, agentId }, cancellationToken: ct));
    }

    public async Task<SystemAgentRecord> CreateDepartmentAgentAsync(Guid tenantId, Guid departmentId, Guid actorId, CreateSystemAgentRequest request, CancellationToken ct)
    {
        var icon = request.IconMetadata?.GetRawText() ?? "{}";
        await using var conn = await _factory.OpenAsync(ct);
        // scope='tenant' (legacy compatibility column, still NOT NULL-checked
        // by migration 007's agents_scope_ownership_valid: tenant_id must
        // equal owning_tenant_id) alongside v2_scope='department' (the real,
        // structural V2 ownership migration 024 added) — both constraints
        // hold simultaneously; see the interface doc comment above for why a
        // Department Agent can never be scope='system' instead.
        return await conn.QuerySingleAsync<SystemAgentRecord>(new CommandDefinition($"""
            INSERT INTO agents (tenant_id, code, slug, name, description, icon_metadata, scope,
                owning_tenant_id, department_id, v2_scope, v2_migration_status, v2_migration_note,
                type, action_tier, lifecycle_state, config, owner_user_id, created_by, updated_by)
            VALUES (@tenantId, @Slug, @Slug, @Name, @Description, @icon::jsonb, 'tenant',
                @tenantId, @departmentId, 'department', 'resolved',
                'Created directly as a V2-C department agent (ACL V2-C)',
                COALESCE(@Type,'domain'), COALESCE(@ActionTier,'suggest'), 'draft', @emptyConfig::jsonb,
                @actorId, @actorId, @actorId)
            RETURNING {AgentColumns.Replace("a.", "")}
            """, new { tenantId, departmentId, actorId, request.Slug, request.Name, request.Description, icon, emptyConfig = "{}", request.Type, request.ActionTier }, cancellationToken: ct));
    }

    public async Task<SystemAgentRecord?> UpdateDepartmentAgentAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid actorId, UpdateSystemAgentRequest request, CancellationToken ct)
    {
        var icon = request.IconMetadata?.GetRawText();
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<SystemAgentRecord>(new CommandDefinition($"""
            UPDATE agents a SET name=COALESCE(@Name,name), description=COALESCE(@Description,description),
                icon_metadata=COALESCE(@icon::jsonb,icon_metadata), action_tier=COALESCE(@ActionTier,action_tier), updated_by=@actorId
            WHERE a.id=@agentId AND {DepartmentOwnershipWhere}
            RETURNING {AgentColumns.Replace("a.", "")}
            """, new { agentId, tenantId, departmentId, actorId, request.Name, request.Description, icon, request.ActionTier }, cancellationToken: ct));
    }

    public async Task<SystemAgentRecord?> SetDepartmentAgentEnabledAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid actorId, bool enabled, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<SystemAgentRecord>(new CommandDefinition($"""
            UPDATE agents a SET disabled_at=CASE WHEN @enabled THEN NULL ELSE NOW() END,
                disabled_by=CASE WHEN @enabled THEN NULL ELSE @actorId END,
                disabled_reason=CASE WHEN @enabled THEN NULL ELSE 'disabled by department administrator' END,
                updated_by=@actorId
            WHERE a.id=@agentId AND {DepartmentOwnershipWhere}
            RETURNING {AgentColumns.Replace("a.", "")}
            """, new { agentId, tenantId, departmentId, actorId, enabled }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AgentVersionRecord>> ListDepartmentAgentVersionsAsync(Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = (await conn.QueryAsync<VersionRow>(new CommandDefinition(
            $"SELECT {VersionColumns} FROM agent_versions v JOIN agents a ON a.id=v.agent_id AND {DepartmentOwnershipWhere} WHERE v.agent_id=@agentId ORDER BY v.version DESC",
            new { tenantId, departmentId, agentId }, cancellationToken: ct))).AsList();
        return await HydrateVersionsAsync(conn, rows, ct);
    }

    public async Task<AgentVersionRecord?> FindDepartmentAgentVersionAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid versionId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<VersionRow>(new CommandDefinition(
            $"SELECT {VersionColumns} FROM agent_versions v JOIN agents a ON a.id=v.agent_id AND {DepartmentOwnershipWhere} WHERE v.agent_id=@agentId AND v.id=@versionId",
            new { tenantId, departmentId, agentId, versionId }, cancellationToken: ct));
        if (row is null) return null;
        return (await HydrateVersionsAsync(conn, [row], ct))[0];
    }

    public async Task<AgentVersionRecord?> CreateDepartmentAgentVersionAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid actorId, CreateAgentVersionRequest request, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var validAgent = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"SELECT EXISTS(SELECT 1 FROM agents a WHERE a.id=@agentId AND {DepartmentOwnershipWhere})",
            new { agentId, tenantId, departmentId }, tx, cancellationToken: ct));
        if (!validAgent) return null;
        await EnsureWorkflowEligibleAsync(conn, tx, tenantId, departmentId, request.WorkflowId, ct);
        await EnsureSkillsEligibleAsync(conn, tx, tenantId, departmentId, request.Skills, ct);
        await EnsureToolsEligibleAsync(conn, tx, request.ToolIds, ct);
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
        // ACL V2-D: tools/skills/delegates go through the shared helper
        // unchanged (capabilityIds: null — a Department Agent's capability
        // attachment is scope-checked separately below, since the shared
        // helper has no tenant/department context and SUPER_ADMIN's System
        // Agent methods must keep calling it exactly as before).
        await ReplaceCapabilitiesAsync(conn, tx, agentId, row.Id, actorId, request.ToolIds, request.Skills, request.DelegateAgentIds, null, ct);
        await ReplaceDepartmentAgentCapabilitiesAsync(conn, tx, tenantId, departmentId, row.Id, actorId, request.CapabilityIds, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE agents SET current_draft_version_id=@versionId, updated_by=@actorId WHERE id=@agentId",
            new { agentId, actorId, versionId = row.Id }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return (await HydrateVersionsAsync(conn, [row], ct))[0];
    }

    public async Task<AgentVersionRecord?> UpdateDepartmentAgentVersionAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid versionId, Guid actorId, UpdateAgentVersionRequest request, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (request.ClearWorkflow != true)
            await EnsureWorkflowEligibleAsync(conn, tx, tenantId, departmentId, request.WorkflowId, ct);
        await EnsureSkillsEligibleAsync(conn, tx, tenantId, departmentId, request.Skills, ct);
        await EnsureToolsEligibleAsync(conn, tx, request.ToolIds, ct);
        var memory = request.MemoryConfig?.GetRawText();
        var row = await conn.QuerySingleOrDefaultAsync<VersionRow>(new CommandDefinition($"""
            UPDATE agent_versions v SET provider=COALESCE(@Provider,provider), model=COALESCE(@Model,model),
                system_prompt=COALESCE(@SystemPrompt,system_prompt), temperature=COALESCE(@Temperature,temperature),
                max_turns=COALESCE(@MaxTurns,max_turns), timeout_seconds=COALESCE(@TimeoutSeconds,timeout_seconds),
                max_tokens=COALESCE(@MaxTokens,max_tokens), memory_config=COALESCE(@memory::jsonb,memory_config),
                workflow_id=CASE WHEN @ClearWorkflow THEN NULL ELSE COALESCE(@WorkflowId,workflow_id) END
            FROM agents a WHERE v.id=@versionId AND v.agent_id=@agentId AND v.status='draft'
                AND a.id=v.agent_id AND {DepartmentOwnershipWhere}
            RETURNING {VersionColumns}
            """, new { agentId, versionId, tenantId, departmentId, request.Provider, request.Model, request.SystemPrompt, request.Temperature,
                request.MaxTurns, request.TimeoutSeconds, request.MaxTokens, memory,
                request.WorkflowId, ClearWorkflow = request.ClearWorkflow ?? false }, tx, cancellationToken: ct));
        if (row is null) return null;
        await ReplaceCapabilitiesAsync(conn, tx, agentId, versionId, actorId, request.ToolIds, request.Skills, request.DelegateAgentIds, null, ct);
        await ReplaceDepartmentAgentCapabilitiesAsync(conn, tx, tenantId, departmentId, versionId, actorId, request.CapabilityIds, ct);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE agents SET updated_by=@actorId WHERE id=@agentId", new { actorId, agentId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return (await HydrateVersionsAsync(conn, [row], ct))[0];
    }

    public async Task<AgentVersionValidation?> ValidateDepartmentAgentVersionAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid versionId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ValidationRow>(new CommandDefinition($"""
            SELECT v.provider AS Provider, v.model AS Model, v.system_prompt AS SystemPrompt,
              v.workflow_id AS WorkflowId,
              (SELECT COUNT(*) FROM agent_version_tool_grants g WHERE g.agent_version_id=v.id) AS ToolCount,
              (SELECT COUNT(*) FROM agent_version_tool_grants g JOIN tools t ON t.id=g.tool_id
                WHERE g.agent_version_id=v.id AND t.is_active AND
                  CASE a.action_tier WHEN 'autonomous' THEN 4 WHEN 'execute_with_approval' THEN 3 WHEN 'suggest' THEN 2 ELSE 1 END >=
                  CASE t.min_action_tier WHEN 'autonomous' THEN 4 WHEN 'execute_with_approval' THEN 3 WHEN 'suggest' THEN 2 ELSE 1 END) AS ValidToolCount,
              (v.workflow_id IS NULL OR EXISTS(SELECT 1 FROM workflows w WHERE w.id=v.workflow_id AND w.is_active)) AS WorkflowValid,
              (SELECT COUNT(*) FROM agent_version_capabilities avc WHERE avc.agent_version_id=v.id) AS CapabilityCount,
              (SELECT COUNT(*) FROM agent_version_capabilities avc JOIN business_capabilities bc ON bc.id=avc.capability_id
                WHERE avc.agent_version_id=v.id AND bc.is_active) AS ValidCapabilityCount
            FROM agent_versions v JOIN agents a ON a.id=v.agent_id AND {DepartmentOwnershipWhere}
            WHERE v.id=@versionId AND v.agent_id=@agentId
            """, new { agentId, versionId, tenantId, departmentId }, cancellationToken: ct));
        if (row is null) return null;
        var errors = new List<string>();
        if (row.Provider is not ("ollama" or "claude_sdk" or "anthropic")) errors.Add("unsupported_provider");
        if (string.IsNullOrWhiteSpace(row.Model) || row.Model.Contains("://") || row.Model.Any(char.IsWhiteSpace)) errors.Add("invalid_model");
        if (string.IsNullOrWhiteSpace(row.SystemPrompt)) errors.Add("system_prompt_required");
        if (row.ToolCount != row.ValidToolCount) errors.Add("inactive_or_unauthorized_tool");
        if (!row.WorkflowValid) errors.Add("inactive_or_missing_workflow");
        if (row.CapabilityCount != row.ValidCapabilityCount) errors.Add("inactive_capability");
        return new AgentVersionValidation(errors.Count == 0, errors.ToArray());
    }

    public async Task<AgentVersionRecord?> PublishDepartmentAgentVersionAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid versionId, Guid actorId, CancellationToken ct)
    {
        var validation = await ValidateDepartmentAgentVersionAsync(tenantId, departmentId, agentId, versionId, ct);
        if (validation is null || !validation.Valid) return null;
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var stillValid = await conn.ExecuteScalarAsync<bool>(new CommandDefinition($"""
            SELECT EXISTS (
              SELECT 1 FROM agent_versions v JOIN agents a ON a.id=v.agent_id AND {DepartmentOwnershipWhere}
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
                AND NOT EXISTS (
                  SELECT 1 FROM agent_version_capabilities avc LEFT JOIN business_capabilities bc ON bc.id=avc.capability_id
                  WHERE avc.agent_version_id=v.id AND (bc.id IS NULL OR NOT bc.is_active)
                )
              FOR UPDATE
            )
            """, new { agentId, versionId, tenantId, departmentId }, tx, cancellationToken: ct));
        if (!stillValid)
        {
            await tx.RollbackAsync(ct);
            return null;
        }
        var row = await conn.QuerySingleOrDefaultAsync<VersionRow>(new CommandDefinition($"""
            UPDATE agent_versions v SET status='published', published_by=@actorId, published_at=NOW()
            FROM agents a WHERE v.id=@versionId AND v.agent_id=@agentId AND v.status='draft'
                AND a.id=v.agent_id AND {DepartmentOwnershipWhere}
            RETURNING {VersionColumns}
            """, new { agentId, versionId, actorId, tenantId, departmentId }, tx, cancellationToken: ct));
        if (row is null) return null;
        await conn.ExecuteAsync(new CommandDefinition($"""
            UPDATE agents SET published_version_id=@versionId, current_draft_version_id=NULL,
                lifecycle_state='production', updated_by=@actorId WHERE id=@agentId AND {DepartmentOwnershipWhere.Replace("a.", "")}
            """, new { agentId, versionId, actorId, tenantId, departmentId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return (await HydrateVersionsAsync(conn, [row], ct))[0];
    }

    public async Task<IReadOnlyList<AgentUserAssignmentRecord>?> ListDepartmentAgentAssignedUsersAsync(Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var owned = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"SELECT EXISTS(SELECT 1 FROM agents a WHERE a.id=@agentId AND {DepartmentOwnershipWhere})",
            new { agentId, tenantId, departmentId }, cancellationToken: ct));
        if (!owned) return null;
        return (await conn.QueryAsync<AgentUserAssignmentRecord>(new CommandDefinition("""
            SELECT u.id AS UserId,u.email AS Email,u.display_name AS DisplayName,aa.created_at AS AssignedAt
            FROM agent_assignments aa JOIN users u ON u.id=aa.user_id
            WHERE aa.tenant_id=@tenantId AND aa.agent_id=@agentId AND aa.status='active'
              AND u.department_id=@departmentId
            ORDER BY u.email
            """, new { tenantId, departmentId, agentId }, cancellationToken: ct))).AsList();
    }

    public async Task<bool> AssignUserToDepartmentAgentAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid userId, Guid actorId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition($"""
            INSERT INTO agent_assignments(tenant_id,agent_id,user_id,assigned_by,status)
            SELECT @tenantId,@agentId,@userId,@actorId,'active'
            WHERE EXISTS(SELECT 1 FROM agents a WHERE a.id=@agentId AND {DepartmentOwnershipWhere})
              AND EXISTS(SELECT 1 FROM users u JOIN user_roles ur ON ur.user_id=u.id JOIN roles r ON r.id=ur.role_id
                WHERE u.id=@userId AND u.tenant_id=@tenantId AND u.department_id=@departmentId AND u.is_active AND r.name='user')
            ON CONFLICT(tenant_id,agent_id,user_id) DO UPDATE SET status='active',assigned_by=EXCLUDED.assigned_by,revoked_at=NULL
            """, new { tenantId, departmentId, agentId, userId, actorId }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<bool> RevokeUserFromDepartmentAgentAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid userId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition($"""
            UPDATE agent_assignments aa SET status='revoked',revoked_at=NOW()
            WHERE aa.tenant_id=@tenantId AND aa.agent_id=@agentId AND aa.user_id=@userId AND aa.status='active'
              AND EXISTS(SELECT 1 FROM users u WHERE u.id=aa.user_id AND u.tenant_id=@tenantId AND u.department_id=@departmentId)
              AND EXISTS(SELECT 1 FROM agents a WHERE a.id=@agentId AND {DepartmentOwnershipWhere})
            """, new { tenantId, departmentId, agentId, userId }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<bool> IsResolvedDepartmentAgentAsync(Guid agentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM agents WHERE id=@agentId AND v2_scope='department' AND v2_migration_status='resolved')",
            new { agentId }, cancellationToken: ct));
    }

    public async Task<ManifestResult> GetDepartmentAgentManifestAsync(Guid userId, Guid tenantId, Guid departmentId, Guid agentId, bool isAdmin, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ManifestRow>(new CommandDefinition("""
            SELECT a.id AS AgentId,a.slug,a.name,a.description,a.icon_metadata::text AS IconMetadataJson,
              a.action_tier AS ActionTier,a.lifecycle_state AS LifecycleState,a.disabled_at IS NULL AS AgentEnabled,
              u.is_active AS UserActive,t.status='active' AS TenantActive,
              (a.v2_scope='department' AND a.owning_tenant_id=@tenantId AND a.department_id=@departmentId) AS TenantAssigned,
              (a.v2_scope='department' AND a.owning_tenant_id=@tenantId AND a.department_id=@departmentId) AS DepartmentAssigned,
              (@isAdmin OR EXISTS(SELECT 1 FROM agent_assignments aa WHERE aa.agent_id=a.id AND aa.tenant_id=@tenantId AND aa.user_id=@userId AND aa.status='active')) AS UserAssigned,
              v.id AS VersionId,v.version,v.status AS VersionStatus,v.provider,v.model,v.system_prompt AS SystemPrompt,
              v.temperature,v.max_turns AS MaxTurns,v.timeout_seconds AS TimeoutSeconds,v.max_tokens AS MaxTokens,v.memory_config::text AS MemoryConfigJson,
              v.workflow_id AS WorkflowId
            FROM users u JOIN tenants t ON t.id=@tenantId
            CROSS JOIN agents a LEFT JOIN agent_versions v ON v.id=a.published_version_id AND v.agent_id=a.id
            WHERE u.id=@userId AND u.tenant_id=@tenantId AND a.id=@agentId
            """, new { userId, tenantId, departmentId, agentId, isAdmin }, cancellationToken: ct));
        if (row is null) return new(null, "not_found_or_cross_tenant");
        // TenantAssigned/DepartmentAssigned both collapse to the same
        // structural-ownership boolean here (there is no separate
        // entitlement chain for a Department Agent) — a false value means
        // either agent doesn't exist as MY department's agent at all, or it
        // belongs to a different department/tenant. Same denial reason
        // either way; never leaks which case it is.
        var denial = !row.UserActive ? "user_disabled" : !row.TenantActive ? "tenant_disabled"
            : !row.DepartmentAssigned ? "agent_not_owned_by_department"
            : !row.UserAssigned ? "agent_not_assigned"
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
            FROM agent_knowledge ak JOIN notes n ON n.id=ak.note_id
            WHERE ak.agent_id=@agentId
              AND (n.scope='platform' OR (n.scope='department' AND n.tenant_id=@tenantId AND n.department_id=@departmentId))
            ORDER BY ak.attached_at
            """, new { tenantId, departmentId, agentId }, cancellationToken: ct))).AsList();
        var (workflowOk, workflow) = await TryResolveWorkflowAsync(conn, row.WorkflowId, ct);
        if (!workflowOk) return new(null, "inactive_or_missing_workflow");
        var delegates = await ResolveDelegatesAsync(conn, row.VersionId!.Value, ct);
        return new(new AgentExecutionManifest(userId, tenantId,
            new ManifestAgent(row.AgentId,row.Slug,row.Name,row.Description,ParseJson(row.IconMetadataJson)),
            row.VersionId!.Value,row.Version!.Value,row.Provider!,row.Model!,row.SystemPrompt!,row.Temperature!.Value,
            row.MaxTurns!.Value,row.TimeoutSeconds!.Value,row.MaxTokens!.Value,ParseJson(row.MemoryConfigJson!),tools,skills,knowledge,workflow,delegates,
            DepartmentId: departmentId), null);
    }

    public async Task<ManifestResult> GetDepartmentAgentTestManifestAsync(Guid actorId, Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var agentRow = await conn.QuerySingleOrDefaultAsync<TestAgentRow>(new CommandDefinition($"""
            SELECT a.id AS AgentId,a.slug,a.name,a.description,a.icon_metadata::text AS IconMetadataJson,
              a.action_tier AS ActionTier, COALESCE(a.current_draft_version_id,a.published_version_id) AS VersionId
            FROM agents a WHERE a.id=@agentId AND {DepartmentOwnershipWhere}
            """, new { agentId, tenantId, departmentId }, cancellationToken: ct));
        if (agentRow is null) return new(null, "not_found_or_cross_department");
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
        var knowledge = (await conn.QueryAsync<ManifestKnowledge>(new CommandDefinition("""
            SELECT n.id AS NoteId,n.title AS Title,n.content AS Content,n.tags AS Tags
            FROM agent_knowledge ak JOIN notes n ON n.id=ak.note_id
            WHERE ak.agent_id=@agentId
              AND (n.scope='platform' OR (n.scope='department' AND n.tenant_id=@tenantId AND n.department_id=@departmentId))
            ORDER BY ak.attached_at
            """, new { tenantId, departmentId, agentId }, cancellationToken: ct))).AsList();

        var (workflowOk2, workflow) = await TryResolveWorkflowAsync(conn, version.WorkflowId, ct);
        if (!workflowOk2) return new(null, "inactive_or_missing_workflow");
        var delegates = await ResolveDelegatesAsync(conn, version.VersionId, ct);
        return new(new AgentExecutionManifest(actorId, tenantId,
            new ManifestAgent(agentRow.AgentId, agentRow.Slug, agentRow.Name, agentRow.Description, ParseJson(agentRow.IconMetadataJson)),
            version.VersionId, version.Version, version.Provider, version.Model, version.SystemPrompt, version.Temperature,
            version.MaxTurns, version.TimeoutSeconds, version.MaxTokens, ParseJson(version.MemoryConfigJson), tools, skills, knowledge, workflow, delegates,
            DepartmentId: departmentId), null);
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

    public async Task<IReadOnlyList<AgentUserAssignmentRecord>?> ListAssignedDepartmentUsersAsync(
        Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        if (!await IsTenantAgentAsync(conn, tenantId, agentId, ct)) return null;
        return (await conn.QueryAsync<AgentUserAssignmentRecord>(new CommandDefinition("""
            SELECT u.id AS UserId,u.email AS Email,u.display_name AS DisplayName,aa.created_at AS AssignedAt
            FROM agent_assignments aa JOIN users u ON u.id=aa.user_id
            WHERE aa.tenant_id=@tenantId AND aa.agent_id=@agentId AND aa.status='active'
              AND u.tenant_id=@tenantId AND u.department_id=@departmentId
            ORDER BY u.email
            """, new { tenantId, departmentId, agentId }, cancellationToken: ct))).AsList();
    }

    public async Task<bool> AssignUserAsync(Guid tenantId, Guid agentId, Guid userId, Guid actorId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO agent_assignments(tenant_id,agent_id,user_id,assigned_by,status)
            SELECT @tenantId,@agentId,@userId,@actorId,'active'
            WHERE EXISTS(SELECT 1 FROM agent_tenant_assignments WHERE tenant_id=@tenantId AND agent_id=@agentId AND status='active')
              AND EXISTS(SELECT 1 FROM users u JOIN user_roles ur ON ur.user_id=u.id JOIN roles r ON r.id=ur.role_id
                WHERE u.id=@userId AND u.tenant_id=@tenantId AND u.is_active AND r.name IN ('user','admin'))
            ON CONFLICT(tenant_id,agent_id,user_id) DO UPDATE SET status='active',assigned_by=EXCLUDED.assigned_by,revoked_at=NULL
            """, new { tenantId, agentId, userId, actorId }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<bool> AssignDepartmentUserAsync(
        Guid tenantId, Guid departmentId, Guid agentId, Guid userId, Guid actorId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO agent_assignments(tenant_id,agent_id,user_id,assigned_by,status)
            SELECT @tenantId,@agentId,@userId,@actorId,'active'
            -- D3: the agent must be actively assigned to THIS department, not
            -- merely tenant-entitled — an ADMIN cannot hand a user a
            -- tenant-only-entitled agent their own department was never
            -- given, even though the tenant technically owns it.
            WHERE EXISTS(SELECT 1 FROM agent_department_assignments
                WHERE tenant_id=@tenantId AND department_id=@departmentId AND agent_id=@agentId AND status='active')
              AND EXISTS(SELECT 1 FROM users u JOIN user_roles ur ON ur.user_id=u.id JOIN roles r ON r.id=ur.role_id
                WHERE u.id=@userId AND u.tenant_id=@tenantId AND u.department_id=@departmentId
                  AND u.is_active AND r.name='user')
            ON CONFLICT(tenant_id,agent_id,user_id) DO UPDATE SET status='active',assigned_by=EXCLUDED.assigned_by,revoked_at=NULL
            """, new { tenantId, departmentId, agentId, userId, actorId }, cancellationToken: ct));
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

    public async Task<bool> RevokeDepartmentUserAsync(
        Guid tenantId, Guid departmentId, Guid agentId, Guid userId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE agent_assignments aa SET status='revoked',revoked_at=NOW()
            WHERE aa.tenant_id=@tenantId AND aa.agent_id=@agentId AND aa.user_id=@userId AND aa.status='active'
              AND EXISTS(SELECT 1 FROM users u WHERE u.id=aa.user_id AND u.tenant_id=@tenantId AND u.department_id=@departmentId)
              AND EXISTS(SELECT 1 FROM agent_tenant_assignments WHERE tenant_id=@tenantId AND agent_id=@agentId AND status='active')
            """, new { tenantId, departmentId, agentId, userId }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<ManifestResult> GetManifestAsync(Guid userId, Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ManifestRow>(new CommandDefinition("""
            SELECT a.id AS AgentId,a.slug,a.name,a.description,a.icon_metadata::text AS IconMetadataJson,
              a.action_tier AS ActionTier,a.lifecycle_state AS LifecycleState,a.disabled_at IS NULL AS AgentEnabled,
              u.is_active AS UserActive,t.status='active' AS TenantActive,
              EXISTS(SELECT 1 FROM agent_tenant_assignments ata WHERE ata.agent_id=a.id AND ata.tenant_id=@tenantId AND ata.status='active') AS TenantAssigned,
              EXISTS(SELECT 1 FROM agent_department_assignments ada WHERE ada.agent_id=a.id AND ada.tenant_id=@tenantId AND ada.department_id=@departmentId AND ada.status='active') AS DepartmentAssigned,
              EXISTS(SELECT 1 FROM agent_assignments aa WHERE aa.agent_id=a.id AND aa.tenant_id=@tenantId AND aa.user_id=@userId AND aa.status='active') AS UserAssigned,
              v.id AS VersionId,v.version,v.status AS VersionStatus,v.provider,v.model,v.system_prompt AS SystemPrompt,
              v.temperature,v.max_turns AS MaxTurns,v.timeout_seconds AS TimeoutSeconds,v.max_tokens AS MaxTokens,v.memory_config::text AS MemoryConfigJson,
              v.workflow_id AS WorkflowId
            FROM users u JOIN tenants t ON t.id=@tenantId
            CROSS JOIN agents a LEFT JOIN agent_versions v ON v.id=a.published_version_id AND v.agent_id=a.id
            WHERE u.id=@userId AND u.tenant_id=@tenantId AND a.id=@agentId
            """, new { userId, tenantId, departmentId, agentId }, cancellationToken: ct));
        if (row is null) return new(null, "not_found_or_cross_tenant");
        // D3 canonical USER chain: tenant entitlement -> department
        // assignment -> direct user assignment, all active, in that order.
        var denial = !row.UserActive ? "user_disabled" : !row.TenantActive ? "tenant_disabled"
            : !row.TenantAssigned ? "agent_not_assigned_to_tenant" : !row.DepartmentAssigned ? "agent_not_assigned_to_department"
            : !row.UserAssigned ? "agent_not_assigned"
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
            row.MaxTurns!.Value,row.TimeoutSeconds!.Value,row.MaxTokens!.Value,ParseJson(row.MemoryConfigJson!),tools,skills,knowledge,workflow,delegates,
            DepartmentId: departmentId), null);
    }

    /// <summary>
    /// D3 ADMIN chain: tenant entitlement -> department assignment, active,
    /// on a published/enabled agent — the same shape as GetManifestAsync
    /// minus the direct-user-assignment requirement (see interface doc).
    /// Deliberately a separate method rather than a boolean flag threaded
    /// through GetManifestAsync: the two authorization chains are the actual
    /// D3 policy distinction and reviewing them side by side as two short,
    /// independent methods is clearer than one method with a branch buried
    /// inside a long SQL/denial-ladder block.
    /// </summary>
    public async Task<ManifestResult> GetAdminManifestAsync(Guid actorId, Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ManifestRow>(new CommandDefinition("""
            SELECT a.id AS AgentId,a.slug,a.name,a.description,a.icon_metadata::text AS IconMetadataJson,
              a.action_tier AS ActionTier,a.lifecycle_state AS LifecycleState,a.disabled_at IS NULL AS AgentEnabled,
              u.is_active AS UserActive,t.status='active' AS TenantActive,
              EXISTS(SELECT 1 FROM agent_tenant_assignments ata WHERE ata.agent_id=a.id AND ata.tenant_id=@tenantId AND ata.status='active') AS TenantAssigned,
              EXISTS(SELECT 1 FROM agent_department_assignments ada WHERE ada.agent_id=a.id AND ada.tenant_id=@tenantId AND ada.department_id=@departmentId AND ada.status='active') AS DepartmentAssigned,
              TRUE AS UserAssigned,
              v.id AS VersionId,v.version,v.status AS VersionStatus,v.provider,v.model,v.system_prompt AS SystemPrompt,
              v.temperature,v.max_turns AS MaxTurns,v.timeout_seconds AS TimeoutSeconds,v.max_tokens AS MaxTokens,v.memory_config::text AS MemoryConfigJson,
              v.workflow_id AS WorkflowId
            FROM users u JOIN tenants t ON t.id=@tenantId
            CROSS JOIN agents a LEFT JOIN agent_versions v ON v.id=a.published_version_id AND v.agent_id=a.id
            WHERE u.id=@actorId AND u.tenant_id=@tenantId AND a.id=@agentId
            """, new { actorId, tenantId, departmentId, agentId }, cancellationToken: ct));
        if (row is null) return new(null, "not_found_or_cross_tenant");
        var denial = !row.UserActive ? "user_disabled" : !row.TenantActive ? "tenant_disabled"
            : !row.TenantAssigned ? "agent_not_assigned_to_tenant" : !row.DepartmentAssigned ? "agent_not_assigned_to_department"
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
        return new(new AgentExecutionManifest(actorId, tenantId,
            new ManifestAgent(row.AgentId,row.Slug,row.Name,row.Description,ParseJson(row.IconMetadataJson)),
            row.VersionId!.Value,row.Version!.Value,row.Provider!,row.Model!,row.SystemPrompt!,row.Temperature!.Value,
            row.MaxTurns!.Value,row.TimeoutSeconds!.Value,row.MaxTokens!.Value,ParseJson(row.MemoryConfigJson!),tools,skills,knowledge,workflow,delegates,
            DepartmentId: departmentId), null);
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

    /// <summary>
    /// ACL V2-E: for a scope='department' workflow, the manifest must reflect
    /// the IMMUTABLE published version's definition, never the mutable
    /// `workflows.definition` head — otherwise editing a workflow after an
    /// Agent version referencing it is published would silently change that
    /// published version's behavior on the next request (the exact gap
    /// migration 026 and this fix close). `scope='platform'` keeps reading
    /// `workflows.definition` directly, byte-for-byte identical to pre-V2-E
    /// behavior, since platform workflows remain SUPER_ADMIN's directly-
    /// mutable template/library objects and never gained version rows.
    /// </summary>
    private static async Task<(bool Ok, ManifestWorkflow? Workflow)> TryResolveWorkflowAsync(NpgsqlConnection conn, Guid? workflowId, CancellationToken ct)
    {
        if (workflowId is null) return (true, null);
        var row = await conn.QuerySingleOrDefaultAsync<(Guid Id, string Name, string DefinitionJson)?>(new CommandDefinition("""
            SELECT w.id AS Id, w.name AS Name,
                   CASE WHEN w.scope = 'department' THEN wv.definition::text ELSE w.definition::text END AS DefinitionJson
            FROM workflows w
            LEFT JOIN workflow_versions wv ON wv.id = w.published_version_id
            WHERE w.id = @workflowId AND w.is_active
              AND (w.scope = 'platform' OR wv.id IS NOT NULL)
            """, new { workflowId }, cancellationToken: ct));
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

    public async Task<ManifestResult> GetDelegateManifestAsync(Guid userId, Guid tenantId, Guid departmentId, Guid routerAgentId, Guid specialistAgentId, CancellationToken ct)
    {
        var routerResult = await GetManifestAsync(userId, tenantId, departmentId, routerAgentId, ct);
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
              -- Single-hop delegation gate is the ROUTER's own full chain
              -- (re-derived above via GetManifestAsync, department included)
              -- plus this agent's explicit membership in the router's
              -- persisted delegate list — the specialist itself was never
              -- required to carry its own department/user assignment, by
              -- design (see GetDelegateManifestAsync's doc comment).
              TRUE AS DepartmentAssigned,
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
            tools, skills, knowledge, workflow, [], DepartmentId: departmentId), null);
    }

    private sealed record TestAgentRow(Guid AgentId, string Slug, string Name, string? Description, string IconMetadataJson, string ActionTier, Guid? VersionId);
    private sealed record TestVersionRow(Guid VersionId, int Version, string Provider, string Model, string SystemPrompt, decimal Temperature, int MaxTurns, int TimeoutSeconds, int MaxTokens, string MemoryConfigJson, Guid? WorkflowId);

    private static async Task<bool> IsTenantAgentAsync(NpgsqlConnection conn, Guid tenantId, Guid agentId, CancellationToken ct) =>
        await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM agent_tenant_assignments ata JOIN agents a ON a.id=ata.agent_id
              WHERE ata.agent_id=@agentId AND ata.tenant_id=@tenantId AND ata.status='active')
            """, new { tenantId, agentId }, cancellationToken: ct));

    private static JsonElement ParseJson(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task ReplaceCapabilitiesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid agentId, Guid versionId, Guid actorId, Guid[]? toolIds, string[]? skills, Guid[]? delegateAgentIds, Guid[]? capabilityIds, CancellationToken ct)
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
        if (capabilityIds is not null)
        {
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM agent_version_capabilities WHERE agent_version_id=@versionId", new { versionId }, tx, cancellationToken: ct));
            foreach (var capabilityId in capabilityIds.Distinct())
                await conn.ExecuteAsync(new CommandDefinition("INSERT INTO agent_version_capabilities(agent_version_id,capability_id,assigned_by) VALUES(@versionId,@capabilityId,@actorId)", new { versionId, capabilityId, actorId }, tx, cancellationToken: ct));
        }
    }

    /// <summary>
    /// ACL V2-D: capability attachment for a Department Agent's draft
    /// version — same DELETE-then-INSERT shape as ReplaceCapabilitiesAsync's
    /// capability block, but each INSERT is gated on the capability being
    /// either platform-scoped (ADMIN may consume, never edit — brief §9) or
    /// this EXACT department's own capability (brief §5's ownership check).
    /// A capability id belonging to another department, another tenant, or
    /// that plain doesn't exist is silently ineligible for the INSERT's
    /// WHERE clause; comparing rows-inserted to the distinct requested count
    /// catches that and throws, rolling back the whole draft-save
    /// transaction rather than partially attaching a subset. Called with
    /// capabilityIds: null (no-op) when the caller only touched
    /// runtime/tools/skills/workflow fields, matching every other field's
    /// "omit to leave unchanged" convention.
    /// </summary>
    private static async Task ReplaceDepartmentAgentCapabilitiesAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid departmentId, Guid versionId, Guid actorId, Guid[]? capabilityIds, CancellationToken ct)
    {
        if (capabilityIds is null) return;
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM agent_version_capabilities WHERE agent_version_id=@versionId", new { versionId }, tx, cancellationToken: ct));
        var distinctIds = capabilityIds.Distinct().ToArray();
        if (distinctIds.Length == 0) return;
        var inserted = await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO agent_version_capabilities(agent_version_id, capability_id, assigned_by)
            SELECT @versionId, bc.id, @actorId
            FROM business_capabilities bc
            WHERE bc.id = ANY(@distinctIds)
              AND (bc.scope = 'platform' OR (bc.scope = 'department' AND bc.tenant_id = @tenantId AND bc.department_id = @departmentId))
            """, new { versionId, actorId, distinctIds, tenantId, departmentId }, tx, cancellationToken: ct));
        if (inserted != distinctIds.Length)
            throw new InvalidOperationException("capability_not_eligible");
    }

    /// <summary>
    /// ACL V2-E: gate on a Department Agent draft's workflow_id — either a
    /// scope='platform' workflow (read/select-only, unchanged since V2-C) or
    /// a scope='department' workflow owned by this exact tenant+department
    /// AND currently published (an unpublished department workflow has no
    /// immutable content yet, so it is never attachable). Inline SQL rather
    /// than a cross-repository call to IWorkflowRepository, matching how
    /// ReplaceDepartmentAgentCapabilitiesAsync above checks
    /// business_capabilities inline instead of calling ICapabilityRepository.
    /// </summary>
    private static async Task EnsureWorkflowEligibleAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid departmentId, Guid? workflowId, CancellationToken ct)
    {
        if (workflowId is null) return;
        var eligible = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (
                SELECT 1 FROM workflows w
                WHERE w.id=@workflowId AND w.is_active
                  AND (w.scope='platform'
                       OR (w.scope='department' AND w.tenant_id=@tenantId AND w.department_id=@departmentId
                           AND w.published_version_id IS NOT NULL))
            )
            """, new { workflowId, tenantId, departmentId }, tx, cancellationToken: ct));
        if (!eligible) throw new InvalidOperationException("workflow_not_eligible");
    }

    /// <summary>
    /// ACL V2-E: Skill names are free-form strings validated only at runtime
    /// by the Python filesystem registry (no server-side catalog existed
    /// before V2-A's `skills` table, and most existing filesystem skill
    /// names have no corresponding row at all in a fresh/test database — see
    /// ACL_V2_V2E report §5). A full allowlist against `skills` would
    /// therefore break every pre-existing filesystem-skill attachment. This
    /// check instead only rejects a code EXPLICITLY owned by ANOTHER
    /// department/tenant as a `scope='department'` row — anything unknown to
    /// the catalog, or owned by this exact department, or platform-scoped,
    /// passes through exactly as it always has.
    /// </summary>
    private static async Task EnsureSkillsEligibleAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid departmentId, string[]? skills, CancellationToken ct)
    {
        if (skills is null || skills.Length == 0) return;
        var leaked = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (
                SELECT 1 FROM skills s
                WHERE s.code = ANY(@skills) AND s.scope='department'
                  AND NOT (s.tenant_id=@tenantId AND s.department_id=@departmentId)
            )
            """, new { skills, tenantId, departmentId }, tx, cancellationToken: ct));
        if (leaked) throw new InvalidOperationException("skill_not_eligible");
    }

    private static async Task EnsureToolsEligibleAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid[]? toolIds, CancellationToken ct)
    {
        if (toolIds is null) return;
        var distinctIds = toolIds.Distinct().ToArray();
        if (distinctIds.Length == 0) return;
        var eligibleCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*)::int FROM tools WHERE id=ANY(@distinctIds) AND is_active",
            new { distinctIds }, tx, cancellationToken: ct));
        if (eligibleCount != distinctIds.Length) throw new InvalidOperationException("tool_not_eligible");
    }

    private static async Task<IReadOnlyList<AgentVersionRecord>> HydrateVersionsAsync(NpgsqlConnection conn, IReadOnlyList<VersionRow> rows, CancellationToken ct)
    {
        var result = new List<AgentVersionRecord>(rows.Count);
        foreach (var row in rows)
        {
            var tools = (await conn.QueryAsync<Guid>(new CommandDefinition("SELECT tool_id FROM agent_version_tool_grants WHERE agent_version_id=@id ORDER BY tool_id", new { row.Id }, cancellationToken: ct))).ToArray();
            var skills = (await conn.QueryAsync<string>(new CommandDefinition("SELECT skill_name FROM agent_version_skill_assignments WHERE agent_version_id=@id ORDER BY skill_name", new { row.Id }, cancellationToken: ct))).ToArray();
            var delegates = (await conn.QueryAsync<Guid>(new CommandDefinition("SELECT specialist_agent_id FROM agent_version_delegates WHERE agent_version_id=@id ORDER BY specialist_agent_id", new { row.Id }, cancellationToken: ct))).ToArray();
            var capabilities = (await conn.QueryAsync<Guid>(new CommandDefinition("SELECT capability_id FROM agent_version_capabilities WHERE agent_version_id=@id ORDER BY capability_id", new { row.Id }, cancellationToken: ct))).ToArray();
            result.Add(new(row.Id,row.AgentId,row.Version,row.Status,row.Provider,row.Model,row.SystemPrompt,row.Temperature,row.MaxTurns,row.TimeoutSeconds,row.MaxTokens,row.MemoryConfigJson,row.CreatedBy,row.CreatedAt,row.PublishedBy,row.PublishedAt,tools,skills,row.WorkflowId,delegates,capabilities));
        }
        return result;
    }

    private sealed record VersionRow(Guid Id, Guid AgentId, int Version, string Status, string Provider, string Model, string SystemPrompt, decimal Temperature, int MaxTurns, int TimeoutSeconds, int MaxTokens, string MemoryConfigJson, Guid? CreatedBy, DateTime CreatedAt, Guid? PublishedBy, DateTime? PublishedAt, Guid? WorkflowId);
    private sealed record ValidationRow(string Provider, string Model, string SystemPrompt, Guid? WorkflowId, long ToolCount, long ValidToolCount, bool WorkflowValid, long CapabilityCount, long ValidCapabilityCount);
    private sealed record ManifestRow(Guid AgentId,string Slug,string Name,string? Description,string IconMetadataJson,string ActionTier,string LifecycleState,bool AgentEnabled,bool UserActive,bool TenantActive,bool TenantAssigned,bool DepartmentAssigned,bool UserAssigned,Guid? VersionId,int? Version,string? VersionStatus,string? Provider,string? Model,string? SystemPrompt,decimal? Temperature,int? MaxTurns,int? TimeoutSeconds,int? MaxTokens,string? MemoryConfigJson,Guid? WorkflowId);
}
