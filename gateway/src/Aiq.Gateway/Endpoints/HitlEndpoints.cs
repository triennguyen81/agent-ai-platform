using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;

namespace Aiq.Gateway.Endpoints;

public static class HitlEndpoints
{
    public static IEndpointRouteBuilder MapHitlEndpoints(this IEndpointRouteBuilder app)
    {
        var perAgent = app
            .MapGroup("/agents/{agentId:guid}/gates")
            .RequireAuthorization("department_admin")
            .RequireRateLimiting("default");

        perAgent.MapGet("/", ListGatesAsync);
        perAgent.MapPost("/", CreateGateAsync);

        var perGate = app
            .MapGroup("/gates/{gateId:guid}")
            .RequireAuthorization("department_admin")
            .RequireRateLimiting("default");

        perGate.MapPatch("/", UpdateGateAsync);
        perGate.MapDelete("/", DeleteGateAsync);

        var hitl = app
            .MapGroup("/hitl")
            .RequireAuthorization("department_admin")
            .RequireRateLimiting("default");

        hitl.MapPost("/actions", CreateActionAsync);     // runtime callers (agent runtime)
        hitl.MapGet("/queue", QueueAsync);                // human reviewers
        hitl.MapPost("/{actionId:guid}/decide", DecideAsync);

        // Phase 5D: the actual real-execution integration point. Called by
        // the dynamic runtime, using the same short-lived tool_execution
        // token minted alongside the manifest (see DynamicAgentEndpoints) —
        // never a role a real login can carry, never handed to the browser.
        app.MapPost("/runtime/agents/{agentId:guid}/hitl/evaluate", EvaluateAsync)
            .RequireAuthorization("resource_or_tool").RequireRateLimiting("default");

        return app;
    }

    public sealed record EvaluateRequest(
        [property: Required, StringLength(200, MinimumLength = 1)] string ToolCode,
        [property: Required] System.Text.Json.JsonElement Payload);

    private static async Task<IResult> EvaluateAsync(
        Guid agentId, EvaluateRequest request, IHitlRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest(new { error = "tenant_context_missing" });
        if (!TryGetUserId(ctx, out var actorId)) return Results.Unauthorized();

        var result = await repo.EvaluateAsync(tenantId, agentId, actorId, request.ToolCode, request.Payload, ct);
        var outcome = result.Outcome switch
        {
            HitlOutcome.Allowed => "allowed",
            HitlOutcome.Rejected => "rejected",
            _ => "pending",
        };
        return Results.Ok(new { outcome, actionId = result.ActionId, gateName = result.GateName });
    }

    private static async Task<IResult> ListGatesAsync(
        Guid agentId, IHitlRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryDepartmentScope(ctx, out var tenantId, out var departmentId)
            || !await repo.IsAgentVisibleToDepartmentAsync(tenantId, departmentId, agentId, ct)) return Results.NotFound();
        var items = await repo.ListGatesAsync(tenantId, departmentId, agentId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> CreateGateAsync(
        Guid agentId, CreateGateRequest request, IHitlRepository repo,
        HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryDepartmentScope(ctx, out var tenantId, out var departmentId)) return Results.BadRequest();
        if (!await repo.IsAgentVisibleToDepartmentAsync(tenantId, departmentId, agentId, ct)) return Results.NotFound();
        if (!TryGetUserId(ctx, out var userId)) return Results.Unauthorized();
        try
        {
            var g = await repo.CreateGateAsync(tenantId, departmentId, agentId, userId, request, ct);
            return Results.Created($"/gates/{g.Id}", g);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Conflict(new { error = "Gate name đã tồn tại cho agent này" });
        }
    }

    private static async Task<IResult> UpdateGateAsync(
        Guid gateId, UpdateGateRequest request, IHitlRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryDepartmentScope(ctx, out var tenantId, out var departmentId)) return Results.BadRequest();
        var g = await repo.UpdateGateAsync(tenantId, departmentId, gateId, request, ct);
        return g is null ? Results.NotFound() : Results.Ok(g);
    }

    private static async Task<IResult> DeleteGateAsync(
        Guid gateId, IHitlRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryDepartmentScope(ctx, out var tenantId, out var departmentId)) return Results.BadRequest();
        var ok = await repo.DeleteGateAsync(tenantId, departmentId, gateId, ct);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    /// <summary>
    /// Called by the agent runtime when a tool call matches a gate's
    /// trigger rule. Creates a pending action and returns its id so the
    /// runtime can poll for the decision.
    /// </summary>
    private static async Task<IResult> CreateActionAsync(
        CreatePendingActionRequest request, IHitlRepository repo,
        HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryDepartmentScope(ctx, out var tenantId, out var departmentId)) return Results.BadRequest();
        if (!TryGetUserId(ctx, out var triggeredBy)) return Results.Unauthorized();

        var action = await repo.CreatePendingActionAsync(tenantId, departmentId, triggeredBy, request, ct);
        return Results.Created($"/hitl/{action.Id}", action);
    }

    private static async Task<IResult> QueueAsync(
        IHitlRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryDepartmentScope(ctx, out var tenantId, out var departmentId)) return Results.BadRequest();
        var items = await repo.ListPendingForTenantAsync(tenantId, departmentId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> DecideAsync(
        Guid actionId, DecideActionRequest request, IHitlRepository repo,
        HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryGetUserId(ctx, out var deciderId)) return Results.Unauthorized();
        if (!TryDepartmentScope(ctx, out var tenantId, out var departmentId)) return Results.BadRequest();

        var action = await repo.DecideAsync(tenantId, departmentId, actionId, deciderId, request, ct);
        return action is null
            ? Results.BadRequest(new { error = "Action không tồn tại hoặc đã được quyết định" })
            : Results.Ok(action);
    }

    private static bool TryGetUserId(HttpContext ctx, out Guid userId)
    {
        var raw = ctx.User.FindFirst("sub")?.Value;
        return Guid.TryParse(raw, out userId);
    }

    private static bool TryDepartmentScope(HttpContext ctx, out Guid tenantId, out Guid departmentId)
    {
        if (AuthenticatedPrincipalContext.TryGet(ctx, out var principal)
            && principal.TenantId is { } tenant
            && principal.DepartmentId is { } department)
        {
            tenantId = tenant;
            departmentId = department;
            return true;
        }
        tenantId = Guid.Empty;
        departmentId = Guid.Empty;
        return false;
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
