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
            .RequireAuthorization("super_admin").RequireRateLimiting("default");
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

        var tenant = app.MapGroup("/tenant/agents")
            .RequireAuthorization("admin").RequireRateLimiting("default");
        tenant.MapGet("/", ListTenantAgentsAsync);
        tenant.MapGet("/{agentId:guid}/users", ListUsersAsync);
        tenant.MapPut("/{agentId:guid}/users/{userId:guid}", AssignUserAsync);
        tenant.MapDelete("/{agentId:guid}/users/{userId:guid}", RevokeUserAsync);
        tenant.MapGet("/{agentId:guid}/knowledge", ListTenantKnowledgeAsync);
        tenant.MapGet("/{agentId:guid}/knowledge/attachable", ListAttachableTenantKnowledgeAsync);
        tenant.MapPut("/{agentId:guid}/knowledge/{noteId:guid}", AttachTenantKnowledgeAsync);
        tenant.MapDelete("/{agentId:guid}/knowledge/{noteId:guid}", DetachTenantKnowledgeAsync);

        app.MapGet("/runtime/agents/{agentId:guid}/manifest", ManifestAsync)
            .RequireAuthorization("user").RequireRateLimiting("default");
        app.MapGet("/runtime/agents/{routerAgentId:guid}/delegate/{specialistAgentId:guid}/manifest", DelegateManifestAsync)
            .RequireAuthorization("user").RequireRateLimiting("default");
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

    private static async Task<IResult> ListTenantAgentsAsync(IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest(new { error = "tenant_context_missing" });
        var items = await repo.ListAgentsForTenantAsync(tenantId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> ListUsersAsync(Guid agentId, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest(new { error = "tenant_context_missing" });
        var items = await repo.ListAssignedUsersAsync(tenantId, agentId, ct);
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
        try { return await repo.AssignUserAsync(tenantId, agentId, userId, actorId, ct) ? Results.NoContent() : Results.BadRequest(new { error = "invalid_tenant_agent_or_user" }); }
        catch (PostgresException ex) when (ex.SqlState == "23514") { return Results.BadRequest(new { error = ex.MessageText }); }
    }

    private static async Task<IResult> RevokeUserAsync(Guid agentId, Guid userId, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest(new { error = "tenant_context_missing" });
        return await repo.RevokeUserAsync(tenantId, agentId, userId, ct) ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> ManifestAsync(
        Guid agentId, IDynamicAgentRepository repo, IAuditLogger audit,
        IJwtTokenService jwt, IUserRepository users, HttpContext ctx, CancellationToken ct)
    {
        if (!TryActor(ctx, out var userId)) return Results.Unauthorized();
        var mode = ManifestAccessRules.Resolve(ctx.User.IsInRole("super_admin"), ctx.User.IsInRole("user"));
        if (mode == ManifestAccessMode.Forbidden) return Results.Forbid();

        Guid? tenantId = null;
        ManifestResult result;
        if (mode == ManifestAccessMode.SuperAdminTest)
        {
            result = await repo.GetTestManifestAsync(userId, agentId, ct);
        }
        else
        {
            if (!TenantContext.TryGet(ctx, out var resolvedTenantId)) return Results.Forbid();
            tenantId = resolvedTenantId;
            result = await repo.GetManifestAsync(userId, resolvedTenantId, agentId, ct);
            if (result.Manifest is not null)
            {
                var minted = await MintToolExecutionTokenAsync(jwt, users, ctx, userId, resolvedTenantId, result.Manifest.TimeoutSeconds, ct);
                if (minted is null) return Results.Forbid();
                result = result with { Manifest = result.Manifest with { ToolExecutionToken = minted } };
            }
        }

        await audit.WriteAsync(new AuditEntry(userId, mode == ManifestAccessMode.SuperAdminTest
                ? "GET runtime agent manifest (super_admin test)" : "GET runtime agent manifest",
            "agent", agentId.ToString(),
            new { tenantId, allowed = result.Manifest is not null, denialReason = result.DenialReason },
            ctx.Connection.RemoteIpAddress, ctx.Request.Headers.UserAgent.ToString()), ct);
        return result.Manifest is not null
            ? Results.Ok(result.Manifest)
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
        IJwtTokenService jwt, IUserRepository users, HttpContext ctx, CancellationToken ct)
    {
        if (!TryActor(ctx, out var userId)) return Results.Unauthorized();
        if (!ctx.User.IsInRole("user")) return Results.Forbid();
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.Forbid();

        var result = await repo.GetDelegateManifestAsync(userId, tenantId, routerAgentId, specialistAgentId, ct);
        if (result.Manifest is not null)
        {
            var minted = await MintToolExecutionTokenAsync(jwt, users, ctx, userId, tenantId, result.Manifest.TimeoutSeconds, ct);
            if (minted is null) return Results.Forbid();
            result = result with { Manifest = result.Manifest with { ToolExecutionToken = minted } };
        }

        await audit.WriteAsync(new AuditEntry(userId, "GET runtime agent delegate manifest",
            "agent", $"{routerAgentId}->{specialistAgentId}",
            new { tenantId, routerAgentId, specialistAgentId, allowed = result.Manifest is not null, denialReason = result.DenialReason },
            ctx.Connection.RemoteIpAddress, ctx.Request.Headers.UserAgent.ToString()), ct);
        return result.Manifest is not null
            ? Results.Ok(result.Manifest)
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
