using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Data;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;
using Npgsql;

namespace Aiq.Gateway.Endpoints;

/// <summary>
/// ACL V2-C: ADMIN's own operational Agent Builder — full CRUD/versioning/
/// publish/user-assignment for the Department Agents its own department
/// owns. Every handler derives tenant+department exclusively from
/// <see cref="AuthenticatedPrincipalContext"/> — a DTO never carries
/// tenantId/departmentId/createdBy/scope, and a raw agent UUID from another
/// department resolves to 404/400 through the repository's own WHERE
/// clause, never a Web-side filter. Mapped separately from
/// DynamicAgentEndpoints's `/system/agents/**` (SUPER_ADMIN platform
/// templates) — these are two structurally distinct resource families, not
/// one route with a role branch, per the V2 architecture decision to split
/// semantics cleanly instead of layering more conditionals onto one page.
/// </summary>
public static class DepartmentAgentEndpoints
{
    public static IEndpointRouteBuilder MapDepartmentAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var agents = app.MapGroup("/department/agents")
            .RequireAuthorization("department_admin").RequireRateLimiting("default");

        agents.MapGet("/", ListAsync);
        agents.MapPost("/", CreateAsync);
        agents.MapGet("/{agentId:guid}", GetAsync);
        agents.MapPatch("/{agentId:guid}", UpdateAsync);
        agents.MapPost("/{agentId:guid}/enable", EnableAsync);
        agents.MapPost("/{agentId:guid}/disable", DisableAsync);
        agents.MapGet("/{agentId:guid}/versions", ListVersionsAsync);
        agents.MapPost("/{agentId:guid}/versions", CreateVersionAsync);
        agents.MapGet("/{agentId:guid}/versions/{versionId:guid}", GetVersionAsync);
        agents.MapPatch("/{agentId:guid}/versions/{versionId:guid}", UpdateVersionAsync);
        agents.MapPost("/{agentId:guid}/versions/{versionId:guid}/validate", ValidateVersionAsync);
        agents.MapPost("/{agentId:guid}/versions/{versionId:guid}/publish", PublishVersionAsync);
        agents.MapGet("/{agentId:guid}/users", ListUsersAsync);
        agents.MapPut("/{agentId:guid}/users/{userId:guid}", AssignUserAsync);
        agents.MapDelete("/{agentId:guid}/users/{userId:guid}", RevokeUserAsync);

        // ACL V2-E: attach/detach own Department Knowledge to this own
        // Department Agent. Both sides re-verified against the caller's
        // exact tenant+department by IDepartmentKnowledgeRepository — a
        // known note/agent UUID from another department 404s/no-ops here,
        // never silently expands scope.
        agents.MapGet("/{agentId:guid}/knowledge", ListKnowledgeAsync);
        agents.MapGet("/{agentId:guid}/knowledge/attachable", ListAttachableKnowledgeAsync);
        agents.MapPut("/{agentId:guid}/knowledge/{noteId:guid}", AttachKnowledgeAsync);
        agents.MapDelete("/{agentId:guid}/knowledge/{noteId:guid}", DetachKnowledgeAsync);

        return app;
    }

    private static async Task<IResult> ListKnowledgeAsync(Guid agentId, IDepartmentKnowledgeRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var items = await repo.ListForDepartmentAgentAsync(tenantId, departmentId, agentId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> ListAttachableKnowledgeAsync(Guid agentId, IDepartmentKnowledgeRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var items = await repo.ListAttachableForDepartmentAgentAsync(tenantId, departmentId, agentId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> AttachKnowledgeAsync(Guid agentId, Guid noteId, IDepartmentKnowledgeRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out var actorId)) return Results.Forbid();
        var ok = await repo.AttachToDepartmentAgentAsync(tenantId, departmentId, agentId, noteId, actorId, ct);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> DetachKnowledgeAsync(Guid agentId, Guid noteId, IDepartmentKnowledgeRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var ok = await repo.DetachFromDepartmentAgentAsync(tenantId, departmentId, agentId, noteId, ct);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    private static bool TryScope(HttpContext ctx, out Guid tenantId, out Guid departmentId, out Guid actorId)
    {
        tenantId = departmentId = actorId = Guid.Empty;
        if (!AuthenticatedPrincipalContext.TryGet(ctx, out var principal)
            || principal.TenantId is not { } t || principal.DepartmentId is not { } d)
            return false;
        tenantId = t;
        departmentId = d;
        actorId = principal.UserId;
        return true;
    }

    private static async Task<IResult> ListAsync(IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        // Unified ADMIN workspace: reusable platform templates are visible
        // beside this department's own Agents. All detail/mutation endpoints
        // retain their structural department ownership predicate, so a
        // platform row displayed here can never be edited through this API.
        var platform = await repo.ListSystemAgentsAsync(ct);
        var department = await repo.ListDepartmentAgentsAsync(tenantId, departmentId, ct);
        var items = platform.Concat(department).ToArray();
        return Results.Ok(new { items, total = items.Length });
    }

    private static async Task<IResult> GetAsync(Guid agentId, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var agent = await repo.FindDepartmentAgentAsync(tenantId, departmentId, agentId, ct);
        return agent is null ? Results.NotFound() : Results.Ok(agent);
    }

    private static async Task<IResult> CreateAsync(CreateSystemAgentRequest request, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryScope(ctx, out var tenantId, out var departmentId, out var actorId)) return Results.Forbid();
        try
        {
            var agent = await repo.CreateDepartmentAgentAsync(tenantId, departmentId, actorId, request, ct);
            return Results.Created($"/department/agents/{agent.Id}", agent);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Conflict(new { error = "agent_slug_exists" });
        }
    }

    private static async Task<IResult> UpdateAsync(Guid agentId, UpdateSystemAgentRequest request, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryScope(ctx, out var tenantId, out var departmentId, out var actorId)) return Results.Forbid();
        var agent = await repo.UpdateDepartmentAgentAsync(tenantId, departmentId, agentId, actorId, request, ct);
        return agent is null ? Results.NotFound() : Results.Ok(agent);
    }

    private static Task<IResult> EnableAsync(Guid agentId, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct) => SetEnabledAsync(agentId, true, repo, ctx, ct);
    private static Task<IResult> DisableAsync(Guid agentId, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct) => SetEnabledAsync(agentId, false, repo, ctx, ct);
    private static async Task<IResult> SetEnabledAsync(Guid agentId, bool enabled, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out var actorId)) return Results.Forbid();
        var agent = await repo.SetDepartmentAgentEnabledAsync(tenantId, departmentId, agentId, actorId, enabled, ct);
        return agent is null ? Results.NotFound() : Results.Ok(agent);
    }

    private static async Task<IResult> ListVersionsAsync(Guid agentId, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        if (await repo.FindDepartmentAgentAsync(tenantId, departmentId, agentId, ct) is null) return Results.NotFound();
        var versions = await repo.ListDepartmentAgentVersionsAsync(tenantId, departmentId, agentId, ct);
        return Results.Ok(new { items = versions, total = versions.Count });
    }

    private static async Task<IResult> GetVersionAsync(Guid agentId, Guid versionId, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var version = await repo.FindDepartmentAgentVersionAsync(tenantId, departmentId, agentId, versionId, ct);
        return version is null ? Results.NotFound() : Results.Ok(version);
    }

    private static async Task<IResult> CreateVersionAsync(Guid agentId, CreateAgentVersionRequest request, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryScope(ctx, out var tenantId, out var departmentId, out var actorId)) return Results.Forbid();
        try
        {
            var version = await repo.CreateDepartmentAgentVersionAsync(tenantId, departmentId, agentId, actorId, request, ct);
            return version is null ? Results.NotFound() : Results.Created($"/department/agents/{agentId}/versions/{version.Id}", version);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Conflict(new { error = "agent_already_has_draft" });
        }
        catch (PostgresException ex) when (ex.SqlState is "23503" or "23514")
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["capabilities"] = [ex.MessageText] });
        }
        catch (InvalidOperationException ex) when (ex.Message == "capability_not_eligible")
        {
            // ACL V2-D: at least one requested capability id is neither
            // platform-scoped nor owned by this exact department — never
            // leaks which id or why (matches the codebase's existing
            // cross-tenant/cross-department "deny without detail" convention).
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["capabilityIds"] = ["capability_not_eligible"] });
        }
        catch (InvalidOperationException ex) when (ex.Message == "workflow_not_eligible")
        {
            // ACL V2-E: workflowId is neither platform-scoped nor this exact
            // department's own PUBLISHED workflow.
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["workflowId"] = ["workflow_not_eligible"] });
        }
        catch (InvalidOperationException ex) when (ex.Message == "skill_not_eligible")
        {
            // ACL V2-E: at least one requested skill code is explicitly owned
            // by ANOTHER department's Department Skill catalog entry.
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["skills"] = ["skill_not_eligible"] });
        }
        catch (InvalidOperationException ex) when (ex.Message == "tool_not_eligible")
        {
            // Tools are a global/platform-only registry an ADMIN can already
            // browse in full (GET /department/tools) — unlike cross-tenant/
            // cross-department ids, naming which tool code is inactive or
            // requires a higher action_tier leaks nothing they can't already
            // see, so this is the one eligibility error that names names.
            var codes = ex.Data["codes"] as string[];
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["toolIds"] = codes is { Length: > 0 } ? codes : ["tool_not_eligible"] });
        }
    }

    private static async Task<IResult> UpdateVersionAsync(Guid agentId, Guid versionId, UpdateAgentVersionRequest request, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryScope(ctx, out var tenantId, out var departmentId, out var actorId)) return Results.Forbid();
        var existing = await repo.FindDepartmentAgentVersionAsync(tenantId, departmentId, agentId, versionId, ct);
        if (existing is null) return Results.NotFound();
        if (existing.Status != "draft") return Results.Conflict(new { error = "published_versions_are_immutable" });
        try
        {
            var version = await repo.UpdateDepartmentAgentVersionAsync(tenantId, departmentId, agentId, versionId, actorId, request, ct);
            return version is null ? Results.NotFound() : Results.Ok(version);
        }
        catch (PostgresException ex) when (ex.SqlState is "23503" or "23514")
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["capabilities"] = [ex.MessageText] });
        }
        catch (InvalidOperationException ex) when (ex.Message == "capability_not_eligible")
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["capabilityIds"] = ["capability_not_eligible"] });
        }
        catch (InvalidOperationException ex) when (ex.Message == "workflow_not_eligible")
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["workflowId"] = ["workflow_not_eligible"] });
        }
        catch (InvalidOperationException ex) when (ex.Message == "skill_not_eligible")
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["skills"] = ["skill_not_eligible"] });
        }
        catch (InvalidOperationException ex) when (ex.Message == "tool_not_eligible")
        {
            // Tools are a global/platform-only registry an ADMIN can already
            // browse in full (GET /department/tools) — unlike cross-tenant/
            // cross-department ids, naming which tool code is inactive or
            // requires a higher action_tier leaks nothing they can't already
            // see, so this is the one eligibility error that names names.
            var codes = ex.Data["codes"] as string[];
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["toolIds"] = codes is { Length: > 0 } ? codes : ["tool_not_eligible"] });
        }
    }

    private static async Task<IResult> ValidateVersionAsync(Guid agentId, Guid versionId, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var result = await repo.ValidateDepartmentAgentVersionAsync(tenantId, departmentId, agentId, versionId, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> PublishVersionAsync(Guid agentId, Guid versionId, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out var actorId)) return Results.Forbid();
        var existing = await repo.FindDepartmentAgentVersionAsync(tenantId, departmentId, agentId, versionId, ct);
        if (existing is null) return Results.NotFound();
        if (existing.Status != "draft") return Results.Conflict(new { error = "version_is_not_draft" });
        var validation = await repo.ValidateDepartmentAgentVersionAsync(tenantId, departmentId, agentId, versionId, ct);
        if (validation is null) return Results.NotFound();
        if (!validation.Valid) return Results.ValidationProblem(new Dictionary<string, string[]> { ["version"] = validation.Errors });
        var version = await repo.PublishDepartmentAgentVersionAsync(tenantId, departmentId, agentId, versionId, actorId, ct);
        return version is null ? Results.Conflict(new { error = "publish_failed" }) : Results.Ok(version);
    }

    private static async Task<IResult> ListUsersAsync(Guid agentId, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var items = await repo.ListDepartmentAgentAssignedUsersAsync(tenantId, departmentId, agentId, ct);
        return items is null ? Results.NotFound() : Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> AssignUserAsync(Guid agentId, Guid userId, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out var actorId)) return Results.Forbid();
        var assigned = await repo.AssignUserToDepartmentAgentAsync(tenantId, departmentId, agentId, userId, actorId, ct);
        return assigned ? Results.NoContent() : Results.BadRequest(new { error = "invalid_agent_or_user" });
    }

    private static async Task<IResult> RevokeUserAsync(Guid agentId, Guid userId, IDynamicAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var revoked = await repo.RevokeUserFromDepartmentAgentAsync(tenantId, departmentId, agentId, userId, ct);
        return revoked ? Results.NoContent() : Results.NotFound();
    }

    private static bool TryValidate(object value, out Dictionary<string, string[]> errors)
    {
        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(value, new ValidationContext(value), results, true);
        errors = results.SelectMany(r => r.MemberNames.DefaultIfEmpty("request").Select(m => (m, r.ErrorMessage)))
            .GroupBy(x => x.m).ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage ?? "invalid").ToArray());
        return valid;
    }
}
