using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Data;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Governance;

// ACL V2-D: Scope ("platform" or "department") lets the Web UI distinguish
// consume-only platform capabilities from this department's own — never an
// authorization input, purely presentational (brief §14).
public sealed record BusinessCapabilityRecord(
    Guid Id, string Code, string Name, string? Description, string? Category, bool IsActive, string Scope);

// ACL V2-D: ADMIN's own Department capability CRUD. tenant_id/department_id/
// created_by/scope are never accepted here — every repository method takes
// them as explicit parameters supplied by the endpoint from
// AuthenticatedPrincipalContext, exactly like V2-C's CreateDepartmentAgentAsync.
public sealed record CreateDepartmentCapabilityRequest(
    [property: Required, RegularExpression(@"^[a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*)+$"), StringLength(120, MinimumLength = 3)] string Code,
    [property: Required, StringLength(160)] string Name,
    [property: StringLength(2000)] string? Description,
    [property: StringLength(80)] string? Category);

public sealed record UpdateDepartmentCapabilityRequest(
    [property: StringLength(160)] string? Name,
    [property: StringLength(2000)] string? Description,
    [property: StringLength(80)] string? Category);

public sealed record EligibleCapabilityAgentRecord(Guid AgentId, string Slug, string Name);

/// <summary>
/// D4 TENANT_OWNER management view: one department's capability landscape —
/// every capability at least one department-assigned agent supports, the
/// full set of eligible agents for it, and the current binding (if any).
/// Powers the assign/replace/revoke screen without a second round trip, same
/// shape convention as IDynamicAgentRepository.AgentDepartmentAssignmentRecord.
/// </summary>
public sealed record DepartmentCapabilityRecord(
    Guid CapabilityId, string Code, string Name, string? Category,
    Guid? BoundAgentId, string? BoundAgentName,
    IReadOnlyList<EligibleCapabilityAgentRecord> EligibleAgents);

/// <summary>D4 canonical capability resolution result — same ManifestResult-style shape.</summary>
public sealed record CapabilityResolution(Guid? AgentId, string? DenialReason);

public interface ICapabilityRepository
{
    /// <summary>Full platform catalog (active and inactive) — super_admin's Agent Builder picker and the owner's binding UI both need to see what exists.</summary>
    Task<IReadOnlyList<BusinessCapabilityRecord>> ListCatalogAsync(CancellationToken ct);
    Task<BusinessCapabilityRecord> CreatePlatformCapabilityAsync(Guid actorId, CreateDepartmentCapabilityRequest request, CancellationToken ct);
    Task<BusinessCapabilityRecord?> UpdatePlatformCapabilityAsync(Guid capabilityId, UpdateDepartmentCapabilityRequest request, CancellationToken ct);
    Task<BusinessCapabilityRecord?> SetPlatformCapabilityEnabledAsync(Guid capabilityId, bool enabled, CancellationToken ct);

    /// <summary>
    /// D4 ADMIN Nghiệp vụ menu: distinct active capabilities declared by the
    /// currently published version of any agent actively entitled to this
    /// tenant AND actively assigned to this department. No direct user
    /// assignment required — mirrors IDynamicAgentRepository.GetAdminManifestAsync's chain.
    /// </summary>
    Task<IReadOnlyList<BusinessCapabilityRecord>> ListForDepartmentManagerAsync(Guid tenantId, Guid departmentId, CancellationToken ct);

    /// <summary>
    /// D4 USER Nghiệp vụ menu: distinct active capabilities declared by the
    /// currently published version of any agent for which this user holds
    /// the full tenant + department + direct-assignment chain — mirrors
    /// IAgentRepository.ListForPrincipalAsync's chain exactly.
    /// </summary>
    Task<IReadOnlyList<BusinessCapabilityRecord>> ListForPrincipalAsync(Guid tenantId, Guid userId, CancellationToken ct);

    /// <summary>
    /// The one canonical capability->agent resolver. Never trusts a
    /// client-supplied tenant/department/agent id — every id here must come
    /// from the caller's own server-derived AuthenticatedPrincipalContext.
    /// Binding-first: an active department_capability_bindings row is
    /// authoritative — if the caller isn't independently eligible for the
    /// bound agent, resolution fails closed rather than falling back to
    /// another eligible agent. No binding + no preferredAgentId: exactly one
    /// eligible agent resolves deterministically; zero is unavailable; more
    /// than one is an explicit ambiguity error (unchanged for any caller
    /// that never supplies a preference — Voucher/Invoice/etc. keep this
    /// exact behavior). preferredAgentId (contracts/upload's "Xử lý bằng
    /// trợ lý" selector) is ALWAYS re-validated against the freshly computed
    /// eligible set for this exact principal/tenant/department/capability
    /// before being honored — a client-supplied id that isn't in that set
    /// (wrong capability, wrong department/tenant, not assigned/eligible)
    /// is rejected exactly like "not available," never trusted directly.
    /// Never picks by name/order/priority.
    /// </summary>
    Task<CapabilityResolution> ResolveAgentAsync(
        Guid tenantId, Guid departmentId, Guid? userId, bool isAdmin, string capabilityCode,
        Guid? preferredAgentId, CancellationToken ct);

    /// <summary>
    /// Every agent this exact principal could be routed to for one
    /// capability — the data behind the "Xử lý bằng trợ lý" selector shown
    /// when ResolveAgentAsync would otherwise report capability_ambiguous.
    /// Identical eligibility computation ResolveAgentAsync uses (same
    /// tenant/department/production/enabled/ownership-or-entitlement
    /// filters, same USER direct-assignment vs ADMIN department-manager
    /// chain) — this only skips the "collapse to one or fail" step. Callers
    /// still MUST pass whatever the user picks back through ResolveAgentAsync
    /// as preferredAgentId; this list is never itself an authorization
    /// decision.
    /// </summary>
    Task<IReadOnlyList<EligibleCapabilityAgentRecord>> ListEligibleAgentsAsync(
        Guid tenantId, Guid departmentId, Guid? userId, bool isAdmin, string capabilityCode, CancellationToken ct);

    /// <summary>TENANT_OWNER management view — see §7 of the D4 spec.</summary>
    Task<IReadOnlyList<DepartmentCapabilityRecord>> ListDepartmentCapabilitiesAsync(Guid tenantId, Guid departmentId, CancellationToken ct);

    /// <summary>
    /// TENANT_OWNER-only. Idempotent PUT: binding already pointing at this
    /// exact agent is a no-op success; binding pointing at a different agent
    /// is replaced (old row revoked, new row inserted) in one transaction.
    /// Returns false if the agent is not eligible (not department-assigned,
    /// or its published version doesn't declare this capability) — the DB
    /// trigger enforces this too as defense in depth.
    /// </summary>
    Task<bool> SetBindingAsync(Guid tenantId, Guid departmentId, Guid capabilityId, Guid agentId, Guid actorId, CancellationToken ct);

    /// <summary>TENANT_OWNER-only. Revokes the active binding (history preserved, never deleted).</summary>
    Task<bool> RevokeBindingAsync(Guid tenantId, Guid departmentId, Guid capabilityId, CancellationToken ct);

    // ======================================================================
    // ACL V2-D — Department capability ownership. A Department capability is
    // structurally scoped (business_capabilities.scope='department',
    // tenant_id, department_id, created_by — migration 024), never inferred.
    // Every method filters on the caller's own (tenantId, departmentId) —
    // a raw capability UUID from another department resolves to null/false,
    // never a Web-side filter.
    // ======================================================================

    /// <summary>Unified ADMIN list: platform plus this department's own capabilities. Mutations remain own-department only.</summary>
    Task<IReadOnlyList<BusinessCapabilityRecord>> ListOwnDepartmentCapabilitiesAsync(Guid tenantId, Guid departmentId, CancellationToken ct);
    Task<BusinessCapabilityRecord?> FindOwnDepartmentCapabilityAsync(Guid tenantId, Guid departmentId, Guid capabilityId, CancellationToken ct);
    Task<BusinessCapabilityRecord> CreateDepartmentCapabilityAsync(Guid tenantId, Guid departmentId, Guid actorId, CreateDepartmentCapabilityRequest request, CancellationToken ct);
    Task<BusinessCapabilityRecord?> UpdateDepartmentCapabilityAsync(Guid tenantId, Guid departmentId, Guid capabilityId, UpdateDepartmentCapabilityRequest request, CancellationToken ct);
    Task<BusinessCapabilityRecord?> SetDepartmentCapabilityEnabledAsync(Guid tenantId, Guid departmentId, Guid capabilityId, bool enabled, CancellationToken ct);
}

public sealed class CapabilityRepository(NpgsqlConnectionFactory factory) : ICapabilityRepository
{
    public async Task<IReadOnlyList<BusinessCapabilityRecord>> ListCatalogAsync(CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryAsync<BusinessCapabilityRecord>(new CommandDefinition("""
            SELECT id, code, name, description, category, is_active AS IsActive, scope
            FROM business_capabilities
            WHERE scope='platform'
            ORDER BY category NULLS LAST, name
            """, cancellationToken: ct))).AsList();
    }

    public async Task<BusinessCapabilityRecord> CreatePlatformCapabilityAsync(Guid actorId, CreateDepartmentCapabilityRequest request, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QuerySingleAsync<BusinessCapabilityRecord>(new CommandDefinition($"""
            INSERT INTO business_capabilities(code,name,description,category,scope,tenant_id,department_id,created_by)
            VALUES (@Code,@Name,@Description,@Category,'platform',NULL,NULL,@actorId)
            RETURNING {CapabilityColumns}
            """, new { request.Code, request.Name, request.Description, request.Category, actorId }, cancellationToken: ct));
    }

    public async Task<BusinessCapabilityRecord?> UpdatePlatformCapabilityAsync(Guid capabilityId, UpdateDepartmentCapabilityRequest request, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<BusinessCapabilityRecord>(new CommandDefinition($"""
            UPDATE business_capabilities SET name=COALESCE(@Name,name), description=COALESCE(@Description,description),
                category=COALESCE(@Category,category), updated_at=NOW()
            WHERE id=@capabilityId AND scope='platform'
            RETURNING {CapabilityColumns}
            """, new { capabilityId, request.Name, request.Description, request.Category }, cancellationToken: ct));
    }

    public async Task<BusinessCapabilityRecord?> SetPlatformCapabilityEnabledAsync(Guid capabilityId, bool enabled, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<BusinessCapabilityRecord>(new CommandDefinition(
            $"UPDATE business_capabilities SET is_active=@enabled,updated_at=NOW() WHERE id=@capabilityId AND scope='platform' RETURNING {CapabilityColumns}",
            new { capabilityId, enabled }, cancellationToken: ct));
    }

    // ACL V2-D: a business capability is reachable through EITHER the legacy
    // D3/D4 entitlement chain (agent_tenant_assignments + agent_department_
    // assignments, scope='system' agents only) OR a V2-C Department Agent's
    // structural ownership (v2_scope='department', owning_tenant_id,
    // department_id — no assignment row exists or can exist for these, see
    // ACL_V2_V2C_DEPARTMENT_AGENT_OWNERSHIP_REPORT.md §9). Every capability
    // query below ORs the two paths so a Department Agent's declared
    // capabilities become visible without weakening the existing chain for
    // legacy/platform agents at all.
    private const string AgentOwnershipOrEntitlement = """
        (
            (EXISTS(SELECT 1 FROM agent_tenant_assignments ata WHERE ata.agent_id=a.id AND ata.tenant_id=@tenantId AND ata.status='active')
             AND EXISTS(SELECT 1 FROM agent_department_assignments ada WHERE ada.agent_id=a.id AND ada.tenant_id=@tenantId AND ada.department_id=@departmentId AND ada.status='active'))
            OR (a.v2_scope='department' AND a.owning_tenant_id=@tenantId AND a.department_id=@departmentId)
        )
        """;

    public async Task<IReadOnlyList<BusinessCapabilityRecord>> ListForDepartmentManagerAsync(Guid tenantId, Guid departmentId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryAsync<BusinessCapabilityRecord>(new CommandDefinition($"""
            SELECT DISTINCT bc.id, bc.code, bc.name, bc.description, bc.category, bc.is_active AS IsActive, bc.scope
            FROM agents a
            JOIN agent_versions v ON v.id = a.published_version_id AND v.status = 'published' AND a.disabled_at IS NULL AND a.lifecycle_state = 'production'
            JOIN agent_version_capabilities avc ON avc.agent_version_id = v.id
            JOIN business_capabilities bc ON bc.id = avc.capability_id AND bc.is_active
            WHERE {AgentOwnershipOrEntitlement}
            ORDER BY bc.category NULLS LAST, bc.name
            """, new { tenantId, departmentId }, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<BusinessCapabilityRecord>> ListForPrincipalAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        // USER additionally always requires its own direct agent_assignments
        // row — unchanged D3 rule, now checked once regardless of which
        // ownership path the agent takes (a Department Agent's direct
        // assignment is created by ACL V2-C's AssignUserToDepartmentAgentAsync,
        // in the very same agent_assignments table).
        var departmentId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT department_id FROM users WHERE id=@userId AND tenant_id=@tenantId", new { tenantId, userId }, cancellationToken: ct));
        if (departmentId is null) return [];
        return (await conn.QueryAsync<BusinessCapabilityRecord>(new CommandDefinition($"""
            SELECT DISTINCT bc.id, bc.code, bc.name, bc.description, bc.category, bc.is_active AS IsActive, bc.scope
            FROM agent_assignments aa
            JOIN agents a ON a.id = aa.agent_id AND a.disabled_at IS NULL AND a.lifecycle_state = 'production'
            JOIN agent_versions v ON v.id = a.published_version_id AND v.status = 'published'
            JOIN agent_version_capabilities avc ON avc.agent_version_id = v.id
            JOIN business_capabilities bc ON bc.id = avc.capability_id AND bc.is_active
            WHERE aa.tenant_id = @tenantId AND aa.user_id = @userId AND aa.status = 'active'
              AND {AgentOwnershipOrEntitlement}
            ORDER BY bc.category NULLS LAST, bc.name
            """, new { tenantId, userId, departmentId }, cancellationToken: ct))).AsList();
    }

    /// <summary>
    /// Shared by ResolveAgentAsync and ListEligibleAgentsAsync so the two
    /// can never drift — "what could this principal be routed to" is
    /// computed exactly once. Returns (capabilityId: null, []) when the
    /// code doesn't resolve to an active catalog entry.
    /// </summary>
    private static async Task<(Guid? CapabilityId, IReadOnlyList<EligibleCapabilityAgentRecord> Eligible)> ComputeEligibleAsync(
        NpgsqlConnection conn, Guid tenantId, Guid departmentId, Guid? userId, bool isAdmin, string capabilityCode, CancellationToken ct)
    {
        var capabilityId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM business_capabilities WHERE code=@capabilityCode AND is_active",
            new { capabilityCode }, cancellationToken: ct));
        if (capabilityId is null) return (null, []);

        var eligible = isAdmin
            ? (await conn.QueryAsync<EligibleCapabilityAgentRecord>(new CommandDefinition($"""
                SELECT DISTINCT a.id AS AgentId, a.slug AS Slug, a.name AS Name
                FROM agent_version_capabilities avc
                JOIN agent_versions v ON v.id = avc.agent_version_id AND v.status = 'published'
                JOIN agents a ON a.id = v.agent_id AND a.published_version_id = v.id AND a.disabled_at IS NULL AND a.lifecycle_state = 'production'
                WHERE avc.capability_id = @capabilityId AND {AgentOwnershipOrEntitlement}
                ORDER BY a.name
                """, new { tenantId, departmentId, capabilityId }, cancellationToken: ct))).AsList()
            : (await conn.QueryAsync<EligibleCapabilityAgentRecord>(new CommandDefinition($"""
                SELECT DISTINCT a.id AS AgentId, a.slug AS Slug, a.name AS Name
                FROM agent_version_capabilities avc
                JOIN agent_versions v ON v.id = avc.agent_version_id AND v.status = 'published'
                JOIN agents a ON a.id = v.agent_id AND a.published_version_id = v.id AND a.disabled_at IS NULL AND a.lifecycle_state = 'production'
                JOIN agent_assignments aa ON aa.agent_id = a.id AND aa.tenant_id = @tenantId AND aa.user_id = @userId AND aa.status = 'active'
                WHERE avc.capability_id = @capabilityId AND {AgentOwnershipOrEntitlement}
                ORDER BY a.name
                """, new { tenantId, departmentId, userId, capabilityId }, cancellationToken: ct))).AsList();

        return (capabilityId, eligible);
    }

    public async Task<IReadOnlyList<EligibleCapabilityAgentRecord>> ListEligibleAgentsAsync(
        Guid tenantId, Guid departmentId, Guid? userId, bool isAdmin, string capabilityCode, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var (_, eligible) = await ComputeEligibleAsync(conn, tenantId, departmentId, userId, isAdmin, capabilityCode, ct);
        return eligible;
    }

    public async Task<CapabilityResolution> ResolveAgentAsync(
        Guid tenantId, Guid departmentId, Guid? userId, bool isAdmin, string capabilityCode,
        Guid? preferredAgentId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var (capabilityId, eligible) = await ComputeEligibleAsync(conn, tenantId, departmentId, userId, isAdmin, capabilityCode, ct);
        if (capabilityId is null) return new(null, "capability_not_found");

        // contracts/upload's "Xử lý bằng trợ lý" selector: the client MAY
        // name which eligible agent it wants, but that name is never
        // trusted directly — it only wins if it's actually a member of the
        // eligible set just (re)computed above for THIS principal/tenant/
        // department/capability. Anything else (unauthorized agent id,
        // cross-tenant/cross-department agent, an agent that doesn't even
        // declare this capability) is rejected exactly like "not
        // available" — it can never reveal whether the id was simply
        // ineligible vs. didn't exist vs. belongs to someone else.
        if (preferredAgentId is { } preferred)
        {
            return eligible.Any(a => a.AgentId == preferred)
                ? new(preferred, null)
                : new(null, "capability_not_available");
        }

        var boundAgentId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition("""
            SELECT agent_id FROM department_capability_bindings
            WHERE tenant_id=@tenantId AND department_id=@departmentId AND capability_id=@capabilityId AND status='active'
            """, new { tenantId, departmentId, capabilityId }, cancellationToken: ct));

        if (boundAgentId is { } bound)
        {
            return eligible.Any(a => a.AgentId == bound)
                ? new(bound, null)
                : new(null, "capability_not_available");
        }

        return eligible.Count switch
        {
            0 => new(null, "capability_not_available"),
            1 => new(eligible[0].AgentId, null),
            _ => new(null, "capability_ambiguous"),
        };
    }

    public async Task<IReadOnlyList<DepartmentCapabilityRecord>> ListDepartmentCapabilitiesAsync(Guid tenantId, Guid departmentId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var capabilities = (await conn.QueryAsync<BusinessCapabilityRecord>(new CommandDefinition($"""
            SELECT DISTINCT bc.id, bc.code, bc.name, bc.description, bc.category, bc.is_active AS IsActive, bc.scope
            FROM agents a
            JOIN agent_versions v ON v.id = a.published_version_id AND v.status = 'published' AND a.disabled_at IS NULL AND a.lifecycle_state = 'production'
            JOIN agent_version_capabilities avc ON avc.agent_version_id = v.id
            JOIN business_capabilities bc ON bc.id = avc.capability_id AND bc.is_active
            WHERE {AgentOwnershipOrEntitlement}
            ORDER BY bc.category NULLS LAST, bc.name
            """, new { tenantId, departmentId }, cancellationToken: ct))).AsList();

        var result = new List<DepartmentCapabilityRecord>(capabilities.Count);
        foreach (var capability in capabilities)
        {
            var eligible = (await conn.QueryAsync<EligibleCapabilityAgentRecord>(new CommandDefinition($"""
                SELECT DISTINCT a.id AS AgentId, a.slug AS Slug, a.name AS Name
                FROM agent_version_capabilities avc
                JOIN agent_versions v ON v.id = avc.agent_version_id AND v.status = 'published'
                JOIN agents a ON a.id = v.agent_id AND a.published_version_id = v.id AND a.disabled_at IS NULL AND a.lifecycle_state = 'production'
                WHERE avc.capability_id = @capabilityId AND {AgentOwnershipOrEntitlement}
                ORDER BY a.name
                """, new { tenantId, departmentId, capabilityId = capability.Id }, cancellationToken: ct))).AsList();

            var bound = await conn.QuerySingleOrDefaultAsync<(Guid AgentId, string Name)?>(new CommandDefinition("""
                SELECT b.agent_id AS AgentId, a.name AS Name
                FROM department_capability_bindings b JOIN agents a ON a.id = b.agent_id
                WHERE b.tenant_id=@tenantId AND b.department_id=@departmentId AND b.capability_id=@capabilityId AND b.status='active'
                """, new { tenantId, departmentId, capabilityId = capability.Id }, cancellationToken: ct));

            result.Add(new(capability.Id, capability.Code, capability.Name, capability.Category,
                bound?.AgentId, bound?.Name, eligible));
        }
        return result;
    }

    public async Task<bool> SetBindingAsync(Guid tenantId, Guid departmentId, Guid capabilityId, Guid agentId, Guid actorId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var alreadyBoundToThisAgent = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM department_capability_bindings
                WHERE tenant_id=@tenantId AND department_id=@departmentId AND capability_id=@capabilityId
                  AND agent_id=@agentId AND status='active')
            """, new { tenantId, departmentId, capabilityId, agentId }, tx, cancellationToken: ct));
        if (alreadyBoundToThisAgent)
        {
            await tx.CommitAsync(ct);
            return true;
        }

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE department_capability_bindings SET status='revoked', revoked_at=NOW()
            WHERE tenant_id=@tenantId AND department_id=@departmentId AND capability_id=@capabilityId AND status='active'
            """, new { tenantId, departmentId, capabilityId }, tx, cancellationToken: ct));

        var inserted = await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO department_capability_bindings(tenant_id,department_id,capability_id,agent_id,status,assigned_by)
            VALUES (@tenantId,@departmentId,@capabilityId,@agentId,'active',@actorId)
            """, new { tenantId, departmentId, capabilityId, agentId, actorId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return inserted > 0;
    }

    public async Task<bool> RevokeBindingAsync(Guid tenantId, Guid departmentId, Guid capabilityId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE department_capability_bindings SET status='revoked', revoked_at=NOW()
            WHERE tenant_id=@tenantId AND department_id=@departmentId AND capability_id=@capabilityId AND status='active'
            """, new { tenantId, departmentId, capabilityId }, cancellationToken: ct));
        return n > 0;
    }

    // ======================================================================
    // ACL V2-D — Department capability CRUD implementation.
    // ======================================================================
    private const string CapabilityColumns = "id, code, name, description, category, is_active AS IsActive, scope";
    private const string OwnDepartmentCapabilityWhere = "scope='department' AND tenant_id=@tenantId AND department_id=@departmentId";
    private const string VisibleDepartmentCapabilityWhere =
        "(scope='platform' OR (scope='department' AND tenant_id=@tenantId AND department_id=@departmentId))";

    public async Task<IReadOnlyList<BusinessCapabilityRecord>> ListOwnDepartmentCapabilitiesAsync(Guid tenantId, Guid departmentId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryAsync<BusinessCapabilityRecord>(new CommandDefinition(
            $"SELECT {CapabilityColumns} FROM business_capabilities WHERE {VisibleDepartmentCapabilityWhere} ORDER BY scope, name",
            new { tenantId, departmentId }, cancellationToken: ct))).AsList();
    }

    public async Task<BusinessCapabilityRecord?> FindOwnDepartmentCapabilityAsync(Guid tenantId, Guid departmentId, Guid capabilityId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<BusinessCapabilityRecord>(new CommandDefinition(
            $"SELECT {CapabilityColumns} FROM business_capabilities WHERE {OwnDepartmentCapabilityWhere} AND id=@capabilityId",
            new { tenantId, departmentId, capabilityId }, cancellationToken: ct));
    }

    public async Task<BusinessCapabilityRecord> CreateDepartmentCapabilityAsync(Guid tenantId, Guid departmentId, Guid actorId, CreateDepartmentCapabilityRequest request, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        // The pre-existing globally-unique `code` constraint (migration 022)
        // is preserved deliberately (ACL V2-D brief §7 / Architecture
        // Decisions §5's "no shadowing" recommendation) — a department code
        // colliding with a platform code, or another department's code,
        // fails the same way a duplicate platform code always has (23505,
        // caught at the endpoint). No new namespace/uniqueness model.
        return await conn.QuerySingleAsync<BusinessCapabilityRecord>(new CommandDefinition($"""
            INSERT INTO business_capabilities (code, name, description, category, scope, tenant_id, department_id, created_by)
            VALUES (@Code, @Name, @Description, @Category, 'department', @tenantId, @departmentId, @actorId)
            RETURNING {CapabilityColumns}
            """, new { tenantId, departmentId, actorId, request.Code, request.Name, request.Description, request.Category }, cancellationToken: ct));
    }

    public async Task<BusinessCapabilityRecord?> UpdateDepartmentCapabilityAsync(Guid tenantId, Guid departmentId, Guid capabilityId, UpdateDepartmentCapabilityRequest request, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<BusinessCapabilityRecord>(new CommandDefinition($"""
            UPDATE business_capabilities SET name=COALESCE(@Name,name), description=COALESCE(@Description,description), category=COALESCE(@Category,category)
            WHERE {OwnDepartmentCapabilityWhere} AND id=@capabilityId
            RETURNING {CapabilityColumns}
            """, new { tenantId, departmentId, capabilityId, request.Name, request.Description, request.Category }, cancellationToken: ct));
    }

    public async Task<BusinessCapabilityRecord?> SetDepartmentCapabilityEnabledAsync(Guid tenantId, Guid departmentId, Guid capabilityId, bool enabled, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<BusinessCapabilityRecord>(new CommandDefinition($"""
            UPDATE business_capabilities SET is_active=@enabled
            WHERE {OwnDepartmentCapabilityWhere} AND id=@capabilityId
            RETURNING {CapabilityColumns}
            """, new { tenantId, departmentId, capabilityId, enabled }, cancellationToken: ct));
    }
}
