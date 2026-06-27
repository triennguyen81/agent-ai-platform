using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;

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

        return app;
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
        Guid id, IAgentRepository repo, CancellationToken ct)
    {
        var events = await repo.ListLifecycleEventsAsync(id, ct);
        return Results.Ok(new { items = events, total = events.Count });
    }

    private static async Task<IResult> AclAsync(
        Guid id, IAgentRepository repo, CancellationToken ct)
    {
        var acl = await repo.ListAclAsync(id, ct);
        return Results.Ok(new { items = acl, total = acl.Count });
    }

    private static async Task<IResult> GrantAclAsync(
        Guid id, GrantAclRequest request, IAgentRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryGetUserId(ctx, out var actorUserId)) return Results.Unauthorized();
        var ok = await repo.GrantAclAsync(id, actorUserId, request, ct);
        return ok ? Results.NoContent() : Results.BadRequest();
    }

    private static async Task<IResult> RevokeAclAsync(
        Guid id, Guid principalId, IAgentRepository repo, CancellationToken ct, string principalType = "user")
    {
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
