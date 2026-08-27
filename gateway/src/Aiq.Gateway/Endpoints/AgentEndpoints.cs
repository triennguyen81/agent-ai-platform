using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;
using Aiq.Gateway.Data;

namespace Aiq.Gateway.Endpoints;

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app
            .MapGroup("/agents")
            .RequireAuthorization("admin")  // tenant_admin role at platform level
            .RequireRateLimiting("default");

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPatch("/{id:guid}", UpdateAsync);
        group.MapPost("/{id:guid}/transitions", TransitionAsync);
        group.MapPost("/{id:guid}/disable", DisableAsync);
        group.MapPost("/{id:guid}/enable", EnableAsync);
        group.MapGet("/{id:guid}/lifecycle", LifecycleAsync);
        group.MapGet("/{id:guid}/acl", AclAsync);
        group.MapPost("/{id:guid}/acl", GrantAclAsync);
        group.MapDelete("/{id:guid}/acl/{principalId:guid}", RevokeAclAsync);

        // Admin flow: list all agents a user has ACL on in the current tenant
        app.MapGet("/users/{userId:guid}/agents", UserAgentsAsync)
            .RequireAuthorization("admin")
            .RequireRateLimiting("default");

        // Any authenticated user: see their own assigned agents (read-only)
        app.MapGet("/me/agents", MyAgentsAsync)
            .RequireAuthorization()
            .RequireRateLimiting("default");

        app.MapGet("/runtime/agents/{id:guid}/authorization", AuthorizeExecutionAsync)
            .RequireAuthorization("user")
            .RequireRateLimiting("default");

        return app;
    }

    private static async Task<IResult> AuthorizeExecutionAsync(
        Guid id, IAgentAssignmentRepository assignments, HttpContext ctx, CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId)) return Results.Unauthorized();
        var isSuperAdmin = ctx.User.IsInRole("super_admin");
        var isTenantAdmin = ctx.User.IsInRole("admin");
        Guid? tenantId = TenantContext.TryGet(ctx, out var tid) ? tid : null;
        var result = await assignments.AuthorizeExecutionAsync(
            userId, tenantId, isSuperAdmin, isTenantAdmin, id, ct);
        if (result is null) return Results.NotFound();
        return result.Allowed ? Results.Ok(result) : Results.Json(
            new { error = result.DenialReason }, statusCode: StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> UserAgentsAsync(
        Guid userId, IAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId))
        {
            return Results.BadRequest(new { error = "tenant context missing" });
        }
        var items = await repo.ListForPrincipalAsync(tenantId, userId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> MyAgentsAsync(
        IAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        if (!TenantContext.TryGet(ctx, out var tenantId))
        {
            return Results.BadRequest(new { error = "tenant context missing" });
        }
        var items = await repo.ListForPrincipalAsync(tenantId, userId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> ListAsync(
        IAgentRepository repo, HttpContext ctx, CancellationToken ct, string? state = null)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest(new { error = "tenant_id claim missing" });
        var items = await repo.ListAsync(tenantId, state, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> GetAsync(
        Guid id, IAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest();
        var agent = await repo.FindAsync(tenantId, id, ct);
        if (agent is null) return Results.NotFound();
        var acl = await repo.ListAclAsync(id, ct);
        return Results.Ok(new { agent, acl });
    }

    private static async Task<IResult> CreateAsync(
        CreateAgentRequest request, IAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest();
        if (!TryGetUserId(ctx, out var actorUserId)) return Results.Unauthorized();

        try
        {
            var agent = await repo.CreateAsync(tenantId, actorUserId, request, ct);
            return Results.Created($"/agents/{agent.Id}", agent);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Conflict(new { error = "Agent code đã tồn tại trong tenant" });
        }
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, UpdateAgentRequest request, IAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest();
        var agent = await repo.UpdateAsync(tenantId, id, request, ct);
        return agent is null ? Results.NotFound() : Results.Ok(agent);
    }

    private static async Task<IResult> TransitionAsync(
        Guid id, TransitionStateRequest request, IAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest();
        if (!TryGetUserId(ctx, out var actorUserId)) return Results.Unauthorized();

        try
        {
            var agent = await repo.TransitionStateAsync(tenantId, id, actorUserId, request, ct);
            return agent is null ? Results.NotFound() : Results.Ok(agent);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> DisableAsync(
        Guid id, DisableAgentRequest? request, IAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest();
        if (!TryGetUserId(ctx, out var actorUserId)) return Results.Unauthorized();

        var agent = await repo.DisableAsync(tenantId, id, actorUserId,
            request ?? new DisableAgentRequest(null), ct);
        return agent is null ? Results.NotFound() : Results.Ok(agent);
    }

    private static async Task<IResult> EnableAsync(
        Guid id, IAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest();
        var agent = await repo.EnableAsync(tenantId, id, ct);
        return agent is null ? Results.NotFound() : Results.Ok(agent);
    }

    private static async Task<IResult> LifecycleAsync(
        Guid id, IAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId)
            || await repo.FindAsync(tenantId, id, ct) is null) return Results.NotFound();
        var events = await repo.ListLifecycleEventsAsync(id, ct);
        return Results.Ok(new { items = events, total = events.Count });
    }

    private static async Task<IResult> AclAsync(
        Guid id, IAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId)
            || await repo.FindAsync(tenantId, id, ct) is null) return Results.NotFound();
        var acl = await repo.ListAclAsync(id, ct);
        return Results.Ok(new { items = acl, total = acl.Count });
    }

    private static async Task<IResult> GrantAclAsync(
        Guid id, GrantAclRequest request, IAgentRepository repo, IUserRepository users,
        HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryGetUserId(ctx, out var actorUserId)) return Results.Unauthorized();
        if (!TenantContext.TryGet(ctx, out var tenantId)
            || await repo.FindAsync(tenantId, id, ct) is null) return Results.NotFound();
        if (request.PrincipalType is null or "user")
        {
            var principal = await users.FindByIdAsync(request.PrincipalId, ct);
            if (principal?.TenantId != tenantId) return Results.BadRequest(new { error = "principal must belong to the agent tenant" });
        }
        var ok = await repo.GrantAclAsync(id, actorUserId, request, ct);
        return ok ? Results.NoContent() : Results.BadRequest();
    }

    private static async Task<IResult> RevokeAclAsync(
        Guid id, Guid principalId, IAgentRepository repo, HttpContext ctx,
        CancellationToken ct, string principalType = "user")
    {
        if (!TenantContext.TryGet(ctx, out var tenantId)
            || await repo.FindAsync(tenantId, id, ct) is null) return Results.NotFound();
        var ok = await repo.RevokeAclAsync(id, principalId, principalType, ct);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    private static bool TryGetUserId(HttpContext ctx, out Guid userId)
    {
        var raw = ctx.User.FindFirst("sub")?.Value;
        return Guid.TryParse(raw, out userId);
    }

    private static bool TryValidate(object instance, out Dictionary<string, string[]> errors)
    {
        var ctx = new ValidationContext(instance);
        var results = new List<ValidationResult>();
        var ok = Validator.TryValidateObject(instance, ctx, results, validateAllProperties: true);
        errors = results
            .SelectMany(r => r.MemberNames.Select(m => (Member: m, r.ErrorMessage)))
            .GroupBy(t => t.Member)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage ?? "Invalid").ToArray());
        return ok;
    }
}
