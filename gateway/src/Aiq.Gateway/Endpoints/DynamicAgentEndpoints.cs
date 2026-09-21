using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Audit;
using Aiq.Gateway.Auth;
using Aiq.Gateway.Data;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;
using Npgsql;

namespace Aiq.Gateway.Endpoints;

public static class DynamicAgentEndpoints
{
    public static IEndpointRouteBuilder MapDynamicAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var system = app.MapGroup("/system/agents")
            .RequireAuthorization("platform_admin").RequireRateLimiting("default");
        system.MapGet("/", ListAgentsAsync);
        system.MapPost("/", CreateAgentAsync);
        system.MapGet("/{agentId:guid}", GetAgentAsync);
        system.MapPatch("/{agentId:guid}", UpdateAgentAsync);
        system.MapPost("/{agentId:guid}/enable", EnableAgentAsync);
        system.MapPost("/{agentId:guid}/disable", DisableAgentAsync);
        system.MapGet("/{agentId:guid}/versions", ListVersionsAsync);
        system.MapPost("/{agentId:guid}/versions", CreateVersionAsync);
        system.MapGet("/{agentId:guid}/versions/{versionId:guid}", GetVersionAsync);
        system.MapPatch("/{agentId:guid}/versions/{versionId:guid}", UpdateVersionAsync);
        system.MapPost("/{agentId:guid}/versions/{versionId:guid}/validate", ValidateVersionAsync);
        system.MapPost("/{agentId:guid}/versions/{versionId:guid}/publish", PublishVersionAsync);
        system.MapGet("/{agentId:guid}/tenants", ListTenantsAsync);
        system.MapPut("/{agentId:guid}/tenants/{tenantId:guid}", AssignTenantAsync);
        system.MapDelete("/{agentId:guid}/tenants/{tenantId:guid}", RevokeTenantAsync);

        // ACL V2-G: legacy-agent migration inventory + explicit,
        // one-department-at-a-time clone action. Never automatic — every
        // clone is a deliberate SUPER_ADMIN decision naming one specific
        // already-entitled department.
        system.MapGet("/quarantine", ListQuarantinedAsync);
        system.MapPost("/{agentId:guid}/clone-to-department", CloneToDepartmentAsync);

        // ACL V2 final rule: TENANT/TENANT_OWNER does not manage agents at
        // all — not tenant entitlement, not department entitlement, not
        // user assignment. This whole legacy scope='system' entitlement
        // surface (tenant -> department -> user) predates Department Agent
        // ownership (migration 024) and has no remaining caller in the
        // product (every UI that used it — /admin/tenant-agents,
        // /admin/tenant-users/{id}/agents, the Agents tab on
        // /admin/tenant-departments/{id} — has been removed). Narrowed from
        // "admin" (super_admin+tenant_owner+admin) to platform_admin
        // (super_admin only) rather than deleted: the schema/data
        // (agent_tenant_assignments etc.) stays fully intact for any
        // still-existing platform-agent-to-tenant grant made via
        // /system/agents/{id}/tenants, and a future SUPER_ADMIN-only
        // internal tool could still reach these routes — TENANT_OWNER and
        // ADMIN can no longer reach them by any means, UI or direct API.
        var tenant = app.MapGroup("/tenant/agents")
            .RequireAuthorization("platform_admin").RequireRateLimiting("default");
        tenant.MapGet("/", ListTenantAgentsAsync);
        tenant.MapGet("/{agentId:guid}/users", ListUsersAsync);
        tenant.MapPut("/{agentId:guid}/users/{userId:guid}", AssignUserAsync);
        tenant.MapDelete("/{agentId:guid}/users/{userId:guid}", RevokeUserAsync);
        tenant.MapGet("/{agentId:guid}/knowledge", ListTenantKnowledgeAsync);
        tenant.MapGet("/{agentId:guid}/knowledge/attachable", ListAttachableTenantKnowledgeAsync);
        tenant.MapPut("/{agentId:guid}/knowledge/{noteId:guid}", AttachTenantKnowledgeAsync);
        tenant.MapDelete("/{agentId:guid}/knowledge/{noteId:guid}", DetachTenantKnowledgeAsync);

        // ACL V2 final rule (same as above): department-agent ENTITLEMENT
        // (the legacy scope='system' agent -> department grant) is also no
        // longer a TENANT_OWNER responsibility — "TENANT_OWNER must NOT...
        // assign agents to departments." Its only UI caller (the Agents tab
        // on /admin/tenant-departments/{id}) has been removed. Department
        // Agent ownership (v2_scope='department', migration 024) was never
        // gated by this group and is completely unaffected — ADMIN still
        // creates/publishes/assigns their own Department Agents entirely
        // through /department/agents, with no TENANT_OWNER step at all.
        var departmentAgents = app.MapGroup("/tenant/departments/{departmentId:guid}/agents")
            .RequireAuthorization("platform_admin").RequireRateLimiting("default");
        departmentAgents.MapGet("/", ListDepartmentAgentsAsync);
        departmentAgents.MapPut("/{agentId:guid}", AssignDepartmentAgentAsync);
        departmentAgents.MapDelete("/{agentId:guid}", RevokeDepartmentAgentAsync);

        app.MapGet("/runtime/agents/{agentId:guid}/manifest", ManifestAsync)
            .RequireAuthorization("department_member").RequireRateLimiting("default");
        app.MapGet("/runtime/agents/{routerAgentId:guid}/delegate/{specialistAgentId:guid}/manifest", DelegateManifestAsync)
            .RequireAuthorization("department_member").RequireRateLimiting("default");
        return app;
    }

    private static async Task<IResult> ListAgentsAsync(IDynamicAgentRepository repo, CancellationToken ct) =>
        Results.Ok(new { items = await repo.ListSystemAgentsAsync(ct) });

    private static async Task<IResult> GetAgentAsync(Guid agentId, IDynamicAgentRepository repo, CancellationToken ct)
    {
        var agent = await repo.FindSystemAgentAsync(agentId, ct);
        return agent is null ? Results.NotFound() : Results.Ok(agent);
    }

    private static async Task<IResult> CreateAgentAsync(CreateSystemAgentRequest request, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryActor(ctx, out var actorId)) return Results.Unauthorized();
        try
        {
            var agent = await repo.CreateSystemAgentAsync(actorId, request, ct);
            return Results.Created($"/system/agents/{agent.Id}", agent);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Conflict(new { error = "agent_slug_exists" });
        }
    }

    private static async Task<IResult> ListQuarantinedAsync(IDynamicAgentRepository repo, CancellationToken ct)
    {
        var items = await repo.ListQuarantinedAgentsAsync(ct);
        return Results.Ok(new { items, total = items.Count });
    }

    public sealed record CloneToDepartmentRequest([property: Required] Guid TenantId, [property: Required] Guid DepartmentId);

    private static async Task<IResult> CloneToDepartmentAsync(Guid agentId, CloneToDepartmentRequest request, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryActor(ctx, out var actorId)) return Results.Unauthorized();
        var result = await repo.CloneSharedAgentToDepartmentAsync(actorId, agentId, request.TenantId, request.DepartmentId, ct);
        return result is null ? Results.NotFound() : Results.Created($"/system/agents/{result.ClonedAgentId}", result);
    }

    private static async Task<IResult> UpdateAgentAsync(Guid agentId, UpdateSystemAgentRequest request, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryActor(ctx, out var actorId)) return Results.Unauthorized();
        var agent = await repo.UpdateSystemAgentAsync(agentId, actorId, request, ct);
        return agent is null ? Results.NotFound() : Results.Ok(agent);
    }

    private static Task<IResult> EnableAgentAsync(Guid agentId, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct) => SetEnabledAsync(agentId, true, repo, ctx, ct);
    private static Task<IResult> DisableAgentAsync(Guid agentId, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct) => SetEnabledAsync(agentId, false, repo, ctx, ct);
    private static async Task<IResult> SetEnabledAsync(Guid agentId, bool enabled, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryActor(ctx, out var actorId)) return Results.Unauthorized();
        var agent = await repo.SetEnabledAsync(agentId, actorId, enabled, ct);
        return agent is null ? Results.NotFound() : Results.Ok(agent);
    }

    private static async Task<IResult> ListVersionsAsync(Guid agentId, IDynamicAgentRepository repo, CancellationToken ct)
    {
        if (await repo.FindSystemAgentAsync(agentId, ct) is null) return Results.NotFound();
        var versions = await repo.ListVersionsAsync(agentId, ct);
        return Results.Ok(new { items = versions, total = versions.Count });
    }

    private static async Task<IResult> GetVersionAsync(Guid agentId, Guid versionId, IDynamicAgentRepository repo, CancellationToken ct)
    {
        var version = await repo.FindVersionAsync(agentId, versionId, ct);
        return version is null ? Results.NotFound() : Results.Ok(version);
    }

    private static async Task<IResult> CreateVersionAsync(Guid agentId, CreateAgentVersionRequest request, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryActor(ctx, out var actorId)) return Results.Unauthorized();
        try
        {
            var version = await repo.CreateVersionAsync(agentId, actorId, request, ct);
            return version is null ? Results.NotFound() : Results.Created($"/system/agents/{agentId}/versions/{version.Id}", version);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Conflict(new { error = "agent_already_has_draft" });
        }
        catch (PostgresException ex) when (ex.SqlState is "23503" or "23514")
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["capabilities"] = [ex.MessageText] });
        }
    }

    private static async Task<IResult> UpdateVersionAsync(Guid agentId, Guid versionId, UpdateAgentVersionRequest request, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryActor(ctx, out var actorId)) return Results.Unauthorized();
        var existing = await repo.FindVersionAsync(agentId, versionId, ct);
        if (existing is null) return Results.NotFound();
        if (existing.Status != "draft") return Results.Conflict(new { error = "published_versions_are_immutable" });
        try
        {
            var version = await repo.UpdateVersionAsync(agentId, versionId, actorId, request, ct);
            return version is null ? Results.NotFound() : Results.Ok(version);
        }
        catch (PostgresException ex) when (ex.SqlState is "23503" or "23514")
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["capabilities"] = [ex.MessageText] });
        }
    }

    private static async Task<IResult> ValidateVersionAsync(Guid agentId, Guid versionId, IDynamicAgentRepository repo, CancellationToken ct)
    {
        var result = await repo.ValidateVersionAsync(agentId, versionId, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> PublishVersionAsync(Guid agentId, Guid versionId, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryActor(ctx, out var actorId)) return Results.Unauthorized();
        var existing = await repo.FindVersionAsync(agentId, versionId, ct);
        if (existing is null) return Results.NotFound();
        if (existing.Status != "draft") return Results.Conflict(new { error = "version_is_not_draft" });
        var validation = await repo.ValidateVersionAsync(agentId, versionId, ct);
        if (validation is null) return Results.NotFound();
        if (!validation.Valid) return Results.ValidationProblem(new Dictionary<string, string[]> { ["version"] = validation.Errors });
        var version = await repo.PublishVersionAsync(agentId, versionId, actorId, ct);
        return version is null ? Results.Conflict(new { error = "publish_failed" }) : Results.Ok(version);
    }

    private static async Task<IResult> ListTenantsAsync(Guid agentId, IDynamicAgentRepository repo, CancellationToken ct)
    {
        if (await repo.FindSystemAgentAsync(agentId, ct) is null) return Results.NotFound();
        var items = await repo.ListTenantsAsync(agentId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> AssignTenantAsync(Guid agentId, Guid tenantId, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryActor(ctx, out var actorId)) return Results.Unauthorized();
        try { return await repo.AssignTenantAsync(agentId, tenantId, actorId, ct) ? Results.NoContent() : Results.NotFound(); }
        catch (PostgresException ex) when (ex.SqlState == "23514") { return Results.BadRequest(new { error = ex.MessageText }); }
        catch (InvalidOperationException ex) when (ex.Message == "agent_quota_exceeded")
        {
            return Results.Conflict(new { error = "agent_quota_exceeded" });
        }
    }

    private static async Task<IResult> RevokeTenantAsync(Guid agentId, Guid tenantId, IDynamicAgentRepository repo, CancellationToken ct) =>
        await repo.RevokeTenantAsync(agentId, tenantId, ct) ? Results.NoContent() : Results.NotFound();

    /// <summary>
    /// D3: canonical agent-visibility query for the whole /tenant/agents
    /// page. TENANT_OWNER sees every tenant-entitled agent (unchanged);
    /// ADMIN sees only agents actively assigned to their OWN department —
    /// resolved by the repository's own department-scoped query, never by
    /// fetching the tenant-wide list and filtering here.
    /// </summary>
    private static async Task<IResult> ListTenantAgentsAsync(IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!AuthenticatedPrincipalContext.TryGet(ctx, out var principal) || principal.TenantId is not { } tenantId)
            return Results.BadRequest(new { error = "tenant_context_missing" });
        var items = principal.IsInRole("tenant_owner")
            ? await repo.ListAgentsForTenantAsync(tenantId, ct)
            : await repo.ListAgentsForDepartmentAsync(tenantId, principal.DepartmentId!.Value, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    /// <summary>TENANT_OWNER management view: every tenant-entitled agent, annotated with this department's assignment status.</summary>
    private static async Task<IResult> ListDepartmentAgentsAsync(
        Guid departmentId, IDynamicAgentRepository repo, IDepartmentRepository departments, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest(new { error = "tenant_context_missing" });
        if (await departments.FindAsync(tenantId, departmentId, ct) is null) return Results.NotFound();
        var items = await repo.ListDepartmentEntitlementsAsync(tenantId, departmentId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> AssignDepartmentAgentAsync(
        Guid departmentId, Guid agentId, IDynamicAgentRepository repo, IDepartmentRepository departments, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest(new { error = "tenant_context_missing" });
        if (!TryActor(ctx, out var actorId)) return Results.Unauthorized();
        if (await departments.FindAsync(tenantId, departmentId, ct) is not { Status: "active" })
            return Results.BadRequest(new { error = "active_department_required" });
        try
        {
            var assigned = await repo.AssignDepartmentAgentAsync(tenantId, departmentId, agentId, actorId, ct);
            return assigned ? Results.NoContent() : Results.BadRequest(new { error = "invalid_tenant_department_or_agent" });
        }
        catch (PostgresException ex) when (ex.SqlState == "23514") { return Results.BadRequest(new { error = ex.MessageText }); }
    }

    private static async Task<IResult> RevokeDepartmentAgentAsync(
        Guid departmentId, Guid agentId, IDynamicAgentRepository repo, IDepartmentRepository departments, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest(new { error = "tenant_context_missing" });
        if (await departments.FindAsync(tenantId, departmentId, ct) is null) return Results.NotFound();
        var revoked = await repo.RevokeDepartmentAgentAsync(tenantId, departmentId, agentId, ct);
        return revoked ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> ListUsersAsync(Guid agentId, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!AuthenticatedPrincipalContext.TryGet(ctx, out var principal) || principal.TenantId is not { } tenantId)
            return Results.BadRequest(new { error = "tenant_context_missing" });
        var items = principal.IsInRole("tenant_owner")
            ? await repo.ListAssignedUsersAsync(tenantId, agentId, ct)
            : await repo.ListAssignedDepartmentUsersAsync(tenantId, principal.DepartmentId!.Value, agentId, ct);
        return items is null ? Results.NotFound() : Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> ListTenantKnowledgeAsync(Guid agentId, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest(new { error = "tenant_context_missing" });
        var items = await repo.ListTenantKnowledgeAsync(tenantId, agentId, ct);
        return items is null ? Results.NotFound() : Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> ListAttachableTenantKnowledgeAsync(Guid agentId, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest(new { error = "tenant_context_missing" });
        var items = await repo.ListAttachableTenantKnowledgeAsync(tenantId, agentId, ct);
        return items is null ? Results.NotFound() : Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> AttachTenantKnowledgeAsync(Guid agentId, Guid noteId, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest(new { error = "tenant_context_missing" });
        if (!TryActor(ctx, out var actorId)) return Results.Unauthorized();
        return await repo.AttachTenantKnowledgeAsync(tenantId, agentId, noteId, actorId, ct)
            ? Results.NoContent()
            : Results.BadRequest(new { error = "invalid_tenant_agent_or_note" });
    }

    private static async Task<IResult> DetachTenantKnowledgeAsync(Guid agentId, Guid noteId, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest(new { error = "tenant_context_missing" });
        return await repo.DetachTenantKnowledgeAsync(tenantId, agentId, noteId, ct) ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> AssignUserAsync(Guid agentId, Guid userId, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest(new { error = "tenant_context_missing" });
        if (!TryActor(ctx, out var actorId)) return Results.Unauthorized();
        var principal = AuthenticatedPrincipalContext.Require(ctx);
        try
        {
            var assigned = principal.IsInRole("tenant_owner")
                ? await repo.AssignUserAsync(tenantId, agentId, userId, actorId, ct)
                : await repo.AssignDepartmentUserAsync(tenantId, principal.DepartmentId!.Value, agentId, userId, actorId, ct);
            return assigned ? Results.NoContent() : Results.BadRequest(new { error = "invalid_tenant_agent_or_user" });
        }
        catch (PostgresException ex) when (ex.SqlState == "23514") { return Results.BadRequest(new { error = ex.MessageText }); }
    }

    private static async Task<IResult> RevokeUserAsync(Guid agentId, Guid userId, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest(new { error = "tenant_context_missing" });
        var principal = AuthenticatedPrincipalContext.Require(ctx);
        var revoked = principal.IsInRole("tenant_owner")
            ? await repo.RevokeUserAsync(tenantId, agentId, userId, ct)
            : await repo.RevokeDepartmentUserAsync(tenantId, principal.DepartmentId!.Value, agentId, userId, ct);
        return revoked ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> ManifestAsync(
        Guid agentId, IDynamicAgentRepository repo, IAuditLogger audit,
        IJwtTokenService jwt, IUserRepository users, ITenantCompanyProfileRepository companyProfiles,
        HttpContext ctx, CancellationToken ct)
    {
        if (!TryActor(ctx, out var userId)) return Results.Unauthorized();
        if (!AuthenticatedPrincipalContext.TryGet(ctx, out var principal)) return Results.Forbid();

        // ACL V2-C: ADMIN's own Department Agent Builder needs to run its
        // CURRENT DRAFT (pre-publish) — the same "test the thing you're
        // editing" concept SUPER_ADMIN already has via ManifestAccessMode
        // .SuperAdminTest, just scoped to structural department ownership
        // instead of global platform access. Agent always calls this one
        // canonical route regardless of test-vs-execute intent (see
        // agent/src/aiq_agent/runtime/manifest.py), so the signal has to
        // travel as an explicit, additive query parameter — absent (or on
        // any other role), behavior is byte-for-byte the existing D3 dispatch
        // below. This never returns a DRAFT to the general execution path:
        // real Chat/production execution for ADMIN still requires a
        // published version (see GetDepartmentAgentManifestAsync).
        if (string.Equals(ctx.Request.Query["mode"], "test", StringComparison.Ordinal) && principal.IsInRole("admin"))
        {
            if (principal.TenantId is not { } testTenantId || principal.DepartmentId is not { } testDepartmentId)
                return Results.Forbid();
            var testResult = await repo.GetDepartmentAgentTestManifestAsync(userId, testTenantId, testDepartmentId, agentId, ct);
            if (testResult.Manifest is not null)
            {
                var mintedTestToken = await MintToolExecutionTokenAsync(jwt, users, ctx, userId, testTenantId, testResult.Manifest.TimeoutSeconds, ct);
                if (mintedTestToken is null) return Results.Forbid();
                var testCompanyProfile = ToManifestCompanyProfile(await companyProfiles.GetAsync(testTenantId, ct));
                testResult = testResult with { Manifest = testResult.Manifest with { ToolExecutionToken = mintedTestToken, CompanyProfile = testCompanyProfile } };
            }
            await audit.WriteAsync(new AuditEntry(userId, "GET runtime agent manifest (department agent test)",
                "agent", agentId.ToString(),
                new { tenantId = testTenantId, departmentId = testDepartmentId, allowed = testResult.Manifest is not null, denialReason = testResult.DenialReason },
                ctx.Connection.RemoteIpAddress, ctx.Request.Headers.UserAgent.ToString()), ct);
            return testResult.Manifest is not null
                ? Results.Ok(await ctx.RequestServices.GetRequiredService<Aiq.Gateway.Notes.KnowledgeBaseRepository>().ReferencesAsync(testResult.Manifest, ct))
                : Results.Json(new { error = testResult.DenialReason }, statusCode: StatusCodes.Status403Forbidden);
        }

        var mode = ManifestAccessRules.Resolve(
            principal.IsInRole("super_admin"), principal.IsInRole("admin"), principal.IsInRole("user"));
        if (mode == ManifestAccessMode.Forbidden) return Results.Forbid();

        Guid? tenantId = null;
        Guid? departmentId = null;
        ManifestResult result;
        if (mode == ManifestAccessMode.SuperAdminTest)
        {
            result = await repo.GetTestManifestAsync(userId, agentId, ct);
        }
        else
        {
            // TenantContextMiddleware's DepartmentAccessPolicy check already
            // guarantees an admin/user principal has a valid, active,
            // same-tenant department by the time it reaches here — see D2.
            if (principal.TenantId is not { } resolvedTenantId || principal.DepartmentId is not { } resolvedDepartmentId)
                return Results.Forbid();
            tenantId = resolvedTenantId;
            departmentId = resolvedDepartmentId;
            var isAdmin = mode == ManifestAccessMode.AdminDepartmentExecution;
            // ACL V2-C: Department Agent ownership is the production root —
            // checked FIRST and, when true, is authoritative (never falls
            // through to the legacy entitlement chain below, so a Department
            // Agent belonging to another department can never be reached via
            // that chain either). False (not a resolved Department Agent —
            // including "doesn't exist") preserves the exact pre-V2-C D3
            // behavior for every scope='system' entitlement-based agent.
            result = await repo.IsResolvedDepartmentAgentAsync(agentId, ct)
                ? await repo.GetDepartmentAgentManifestAsync(userId, resolvedTenantId, resolvedDepartmentId, agentId, isAdmin, ct)
                : isAdmin
                    ? await repo.GetAdminManifestAsync(userId, resolvedTenantId, resolvedDepartmentId, agentId, ct)
                    : await repo.GetManifestAsync(userId, resolvedTenantId, resolvedDepartmentId, agentId, ct);
            if (result.Manifest is not null)
            {
                var minted = await MintToolExecutionTokenAsync(jwt, users, ctx, userId, resolvedTenantId, result.Manifest.TimeoutSeconds, ct);
                if (minted is null) return Results.Forbid();
                var companyProfile = ToManifestCompanyProfile(await companyProfiles.GetAsync(resolvedTenantId, ct));
                result = result with { Manifest = result.Manifest with { ToolExecutionToken = minted, CompanyProfile = companyProfile } };
            }
        }

        await audit.WriteAsync(new AuditEntry(userId, mode switch
            {
                ManifestAccessMode.SuperAdminTest => "GET runtime agent manifest (super_admin test)",
                ManifestAccessMode.AdminDepartmentExecution => "GET runtime agent manifest (admin department execution)",
                _ => "GET runtime agent manifest",
            },
            "agent", agentId.ToString(),
            new { tenantId, departmentId, allowed = result.Manifest is not null, denialReason = result.DenialReason },
            ctx.Connection.RemoteIpAddress, ctx.Request.Headers.UserAgent.ToString()), ct);
        return result.Manifest is not null
            ? Results.Ok(await ctx.RequestServices.GetRequiredService<Aiq.Gateway.Notes.KnowledgeBaseRepository>().ReferencesAsync(result.Manifest, ct))
            : Results.Json(new { error = result.DenialReason }, statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Phase 5C: resolves a specialist's manifest for an authorized in-flight
    /// delegation from a router. Every check the router's own manifest
    /// endpoint makes is re-derived from scratch inside
    /// GetDelegateManifestAsync — this handler only adds token minting and
    /// the audit trail, exactly mirroring ManifestAsync above.
    /// </summary>
    private static async Task<IResult> DelegateManifestAsync(
        Guid routerAgentId, Guid specialistAgentId, IDynamicAgentRepository repo, IAuditLogger audit,
        IJwtTokenService jwt, IUserRepository users, ITenantCompanyProfileRepository companyProfiles,
        HttpContext ctx, CancellationToken ct)
    {
        if (!TryActor(ctx, out var userId)) return Results.Unauthorized();
        // Delegation stays USER-only in D3 (see GetDelegateManifestAsync's
        // doc comment) — an ADMIN-router delegation path is not part of this phase.
        if (!ctx.User.IsInRole("user")) return Results.Forbid();
        if (!AuthenticatedPrincipalContext.TryGet(ctx, out var principal)
            || principal.TenantId is not { } tenantId || principal.DepartmentId is not { } departmentId)
            return Results.Forbid();

        var result = await repo.GetDelegateManifestAsync(userId, tenantId, departmentId, routerAgentId, specialistAgentId, ct);
        if (result.Manifest is not null)
        {
            var minted = await MintToolExecutionTokenAsync(jwt, users, ctx, userId, tenantId, result.Manifest.TimeoutSeconds, ct);
            if (minted is null) return Results.Forbid();
            var companyProfile = ToManifestCompanyProfile(await companyProfiles.GetAsync(tenantId, ct));
            result = result with { Manifest = result.Manifest with { ToolExecutionToken = minted, CompanyProfile = companyProfile } };
        }

        await audit.WriteAsync(new AuditEntry(userId, "GET runtime agent delegate manifest",
            "agent", $"{routerAgentId}->{specialistAgentId}",
            new { tenantId, departmentId, routerAgentId, specialistAgentId, allowed = result.Manifest is not null, denialReason = result.DenialReason },
            ctx.Connection.RemoteIpAddress, ctx.Request.Headers.UserAgent.ToString()), ct);
        return result.Manifest is not null
            ? Results.Ok(await ctx.RequestServices.GetRequiredService<Aiq.Gateway.Notes.KnowledgeBaseRepository>().ReferencesAsync(result.Manifest, ct))
            : Results.Json(new { error = result.DenialReason }, statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Re-uses this request's own already-validated role claims rather than a
    /// synthetic role: TenantContextMiddleware rejects any JWT whose role
    /// claims don't exactly equal the user's persisted DB roles (stale-claim
    /// defense), so minting a fake "tool_execution" role would make the token
    /// unusable everywhere, including the very resource endpoints it's meant
    /// to unlock. The "scope=tool_execution" claim is what resource_or_tool
    /// actually checks — see Program.cs. Returns null if the acting user
    /// can't be resolved (caller should Forbid).
    /// </summary>
    private static async Task<string?> MintToolExecutionTokenAsync(
        IJwtTokenService jwt, IUserRepository users, HttpContext ctx, Guid userId, Guid tenantId, int timeoutSeconds, CancellationToken ct)
    {
        var actingUser = await users.FindByIdAsync(userId, ct);
        if (actingUser is null) return null;
        var realRoles = ctx.User.FindAll("role").Select(c => c.Value).ToArray();
        var toolTtl = TimeSpan.FromSeconds(Math.Min(timeoutSeconds + 60, 3600));
        var token = jwt.Issue(actingUser, realRoles, tenantId, toolTtl, new Dictionary<string, string> { ["scope"] = "tool_execution" });
        return token.Token;
    }

    /// <summary>
    /// Null when the tenant has no Company Profile configured yet — the
    /// runtime renders that as an explicit not_configured identity, never a
    /// guess (see ManifestCompanyProfile / _company_context_block).
    /// </summary>
    public static ManifestCompanyProfile? ToManifestCompanyProfile(CompanyProfileRecord? record) =>
        record is null ? null : new ManifestCompanyProfile(
            record.LegalName, record.TaxCode, record.ShortName, record.Aliases,
            record.RegisteredAddress, record.LegalRepresentative, record.RepresentativeTitle,
            record.Phone, record.Email, record.Website);

    private static bool TryActor(HttpContext ctx, out Guid actorId) => Guid.TryParse(ctx.User.FindFirst("sub")?.Value, out actorId);
    private static bool TryValidate(object value, out Dictionary<string, string[]> errors)
    {
        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(value, new ValidationContext(value), results, true);
        errors = results.SelectMany(r => r.MemberNames.DefaultIfEmpty("request").Select(m => (m, r.ErrorMessage)))
            .GroupBy(x => x.m).ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage ?? "invalid").ToArray());
        return valid;
    }
}
