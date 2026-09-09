using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;

namespace Aiq.Gateway.Endpoints;

public static class ReviewEndpoints
{
    public static IEndpointRouteBuilder MapReviewEndpoints(this IEndpointRouteBuilder app)
    {
        var boards = app
            .MapGroup("/review-boards")
            .RequireAuthorization("department_admin")
            .RequireRateLimiting("default");

        boards.MapGet("/", ListBoardsAsync);
        boards.MapPost("/", CreateBoardAsync);
        boards.MapGet("/{id:guid}/members", ListMembersAsync);
        boards.MapPost("/{id:guid}/members", AddMemberAsync);
        boards.MapDelete("/{id:guid}/members/{userId:guid}", RemoveMemberAsync);

        var perAgent = app
            .MapGroup("/agents/{agentId:guid}/review-requests")
            .RequireAuthorization("department_admin")
            .RequireRateLimiting("default");

        perAgent.MapGet("/", ListForAgentAsync);
        perAgent.MapPost("/", SubmitAsync);

        var perRequest = app
            .MapGroup("/review-requests/{requestId:guid}")
            .RequireAuthorization("department_admin")
            .RequireRateLimiting("default");

        perRequest.MapGet("/", GetAsync);
        perRequest.MapGet("/votes", ListVotesAsync);
        perRequest.MapPost("/votes", CastVoteAsync);

        // Personal review queue — any authenticated user (board members)
        app.MapGet("/me/review-queue", MyQueueAsync)
            .RequireAuthorization("authenticated")
            .RequireRateLimiting("default");

        return app;
    }

    private static async Task<IResult> ListBoardsAsync(
        IReviewBoardRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryDepartmentScope(ctx, out var tenantId, out var departmentId)) return Results.BadRequest();
        var items = await repo.ListBoardsAsync(tenantId, departmentId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> CreateBoardAsync(
        CreateReviewBoardRequest request, IReviewBoardRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryDepartmentScope(ctx, out var tenantId, out var departmentId)) return Results.BadRequest();
        try
        {
            var b = await repo.CreateBoardAsync(tenantId, departmentId, request, ct);
            return Results.Created($"/review-boards/{b.Id}", b);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Conflict(new { error = "Board name đã tồn tại trong tenant" });
        }
    }

    private static async Task<IResult> ListMembersAsync(
        Guid id, IReviewBoardRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryDepartmentScope(ctx, out var tenantId, out var departmentId)
            || !await repo.IsBoardInDepartmentAsync(id, tenantId, departmentId, ct)) return Results.NotFound();
        var items = await repo.ListMembersAsync(id, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> AddMemberAsync(
        Guid id, AddBoardMemberRequest request, IReviewBoardRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryDepartmentScope(ctx, out var tenantId, out var departmentId)
            || !await repo.IsBoardInDepartmentAsync(id, tenantId, departmentId, ct)) return Results.NotFound();
        var ok = await repo.AddMemberAsync(id, request, ct);
        return ok ? Results.NoContent() : Results.BadRequest();
    }

    private static async Task<IResult> RemoveMemberAsync(
        Guid id, Guid userId, IReviewBoardRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryDepartmentScope(ctx, out var tenantId, out var departmentId)
            || !await repo.IsBoardInDepartmentAsync(id, tenantId, departmentId, ct)) return Results.NotFound();
        var ok = await repo.RemoveMemberAsync(id, userId, ct);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> ListForAgentAsync(
        Guid agentId, IReviewBoardRepository repo, IHitlRepository scopes, HttpContext ctx, CancellationToken ct)
    {
        if (!TryDepartmentScope(ctx, out var tenantId, out var departmentId)
            || !await scopes.IsAgentVisibleToDepartmentAsync(tenantId, departmentId, agentId, ct)) return Results.NotFound();
        var items = await repo.ListForAgentAsync(agentId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> SubmitAsync(
        Guid agentId, SubmitReviewRequest request,
        IReviewBoardRepository repo, IAgentRepository agents, IHitlRepository scopes,
        HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryDepartmentScope(ctx, out var tenantId, out var departmentId)) return Results.BadRequest();
        if (!TryGetUserId(ctx, out var userId)) return Results.Unauthorized();

        // Verify the agent exists in this tenant and is in pending_review state
        var agent = await agents.FindAsync(tenantId, agentId, ct);
        if (agent is null || !await scopes.IsAgentVisibleToDepartmentAsync(tenantId, departmentId, agentId, ct)) return Results.NotFound();
        if (agent.LifecycleState != "pending_review")
        {
            return Results.BadRequest(new
            {
                error = $"Agent must be in pending_review state to submit (current: {agent.LifecycleState}). "
                      + "Promote testing → pending_review first."
            });
        }

        try
        {
            var req = await repo.SubmitRequestAsync(tenantId, departmentId, agentId, userId, request, ct);
            return Results.Created($"/review-requests/{req.Id}", req);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Conflict(new { error = "Agent đã có request pending cho target_state này" });
        }
    }

    private static async Task<IResult> GetAsync(
        Guid requestId, IReviewBoardRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryDepartmentScope(ctx, out var tenantId, out var departmentId)
            || !await repo.IsRequestInDepartmentAsync(requestId, tenantId, departmentId, ct)) return Results.NotFound();
        var r = await repo.FindRequestAsync(requestId, ct);
        return r is null ? Results.NotFound() : Results.Ok(r);
    }

    private static async Task<IResult> ListVotesAsync(
        Guid requestId, IReviewBoardRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryDepartmentScope(ctx, out var tenantId, out var departmentId)
            || !await repo.IsRequestInDepartmentAsync(requestId, tenantId, departmentId, ct)) return Results.NotFound();
        var items = await repo.ListVotesAsync(requestId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> CastVoteAsync(
        Guid requestId, CastVoteRequest request,
        IReviewBoardRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryGetUserId(ctx, out var voterId)) return Results.Unauthorized();
        if (!TryDepartmentScope(ctx, out var tenantId, out var departmentId)
            || !await repo.IsRequestInDepartmentAsync(requestId, tenantId, departmentId, ct)) return Results.NotFound();

        try
        {
            var r = await repo.CastVoteAsync(requestId, voterId, request, ct);
            return r is null ? Results.NotFound() : Results.Ok(r);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> MyQueueAsync(
        IReviewBoardRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId)) return Results.Unauthorized();
        if (!TryDepartmentScope(ctx, out var tenantId, out var departmentId)) return Results.Forbid();
        var items = await repo.ListPendingForUserAsync(userId, tenantId, departmentId, ct);
        return Results.Ok(new { items, total = items.Count });
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
