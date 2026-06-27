using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Governance;

namespace Aiq.Gateway.Endpoints;

public static class ToolEndpoints
{
    public static IEndpointRouteBuilder MapToolEndpoints(this IEndpointRouteBuilder app)
    {
        // Global tool registry — super_admin only (cross-tenant catalog)
        var super = app
            .MapGroup("/tools")
            .RequireAuthorization("super_admin")
            .RequireRateLimiting("default");

        super.MapGet("/", ListAsync);
        super.MapPost("/", CreateAsync);
        super.MapGet("/{id:guid}", GetAsync);
        super.MapPatch("/{id:guid}", UpdateAsync);
        super.MapDelete("/{id:guid}", DeleteAsync);

        // Anyone with admin in the current tenant can list/grant tools for
        // agents they have access to. We don't re-check ACL here (defer to
        // future AgentAclMiddleware in G4); admin policy at platform level
        // is sufficient for the pilot phase.
        var perAgent = app
            .MapGroup("/agents/{agentId:guid}/tools")
            .RequireAuthorization("admin")
            .RequireRateLimiting("default");

        perAgent.MapGet("/", ListAgentToolsAsync);
        perAgent.MapPost("/", GrantToolAsync);
        perAgent.MapDelete("/{toolId:guid}", RevokeToolAsync);
        perAgent.MapGet("/allowed", AllowedCodesAsync);

        return app;
    }

    private static async Task<IResult> ListAsync(
        IToolRepository repo, CancellationToken ct, bool activeOnly = false)
    {
        var items = await repo.ListAsync(activeOnly, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> GetAsync(Guid id, IToolRepository repo, CancellationToken ct)
    {
        var t = await repo.FindAsync(id, ct);
        return t is null ? Results.NotFound() : Results.Ok(t);
    }

    private static async Task<IResult> CreateAsync(
        CreateToolRequest request, IToolRepository repo, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        try
        {
            var t = await repo.CreateAsync(request, ct);
            return Results.Created($"/tools/{t.Id}", t);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Conflict(new { error = "Tool code đã tồn tại" });
        }
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, UpdateToolRequest request, IToolRepository repo, CancellationToken ct)
    {
        var t = await repo.UpdateAsync(id, request, ct);
        return t is null ? Results.NotFound() : Results.Ok(t);
    }

    private static async Task<IResult> DeleteAsync(Guid id, IToolRepository repo, CancellationToken ct)
    {
        var ok = await repo.DeleteAsync(id, ct);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> ListAgentToolsAsync(
        Guid agentId, IToolRepository repo, CancellationToken ct)
    {
        var items = await repo.ListGrantsAsync(agentId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> GrantToolAsync(
        Guid agentId, GrantToolRequest request, IToolRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryGetUserId(ctx, out var actorId)) return Results.Unauthorized();
        try
        {
            var ok = await repo.GrantAsync(agentId, request.ToolId, actorId, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> RevokeToolAsync(
        Guid agentId, Guid toolId, IToolRepository repo, CancellationToken ct)
    {
        var ok = await repo.RevokeAsync(agentId, toolId, ct);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    /// <summary>
    /// Internal endpoint for the agent runtime. Returns the list of tool
    /// codes the agent may actually invoke (active + tier-allowed).
    /// </summary>
    private static async Task<IResult> AllowedCodesAsync(
        Guid agentId, IToolRepository repo, CancellationToken ct)
    {
        var codes = await repo.ListAllowedToolCodesAsync(agentId, ct);
        return Results.Ok(new { codes });
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
