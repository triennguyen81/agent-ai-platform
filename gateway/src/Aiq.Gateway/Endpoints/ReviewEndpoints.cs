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
            .RequireAuthorization("admin")
            .RequireRateLimiting("default");

        boards.MapGet("/", ListBoardsAsync);
        boards.MapPost("/", CreateBoardAsync);
        boards.MapGet("/{id:guid}/members", ListMembersAsync);
        boards.MapPost("/{id:guid}/members", AddMemberAsync);
        boards.MapDelete("/{id:guid}/members/{userId:guid}", RemoveMemberAsync);

        var perAgent = app
            .MapGroup("/agents/{agentId:guid}/review-requests")
            .RequireAuthorization("admin")
            .RequireRateLimiting("default");

        perAgent.MapGet("/", ListForAgentAsync);
        perAgent.MapPost("/", SubmitAsync);

        var perRequest = app
            .MapGroup("/review-requests/{requestId:guid}")
            .RequireAuthorization("admin")
            .RequireRateLimiting("default");

        perRequest.MapGet("/", GetAsync);
        perRequest.MapGet("/votes", ListVotesAsync);
        perRequest.MapPost("/votes", CastVoteAsync);

        // Personal review queue — any authenticated user (board members)
        app.MapGet("/me/review-queue", MyQueueAsync)
            .RequireAuthorization()
            .RequireRateLimiting("default");

        return app;
    }

    private static async Task<IResult> ListBoardsAsync(
        IReviewBoardRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest();
        var items = await repo.ListBoardsAsync(tenantId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> CreateBoardAsync(
        CreateReviewBoardRequest request, IReviewBoardRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest();
        try
        {
            var b = await repo.CreateBoardAsync(tenantId, request, ct);
            return Results.Created($"/review-boards/{b.Id}", b);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Conflict(new { error = "Board name đã tồn tại trong tenant" });
        }
    }

    private static async Task<IResult> ListMembersAsync(
        Guid id, IReviewBoardRepository repo, CancellationToken ct)
    {
        var items = await repo.ListMembersAsync(id, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> AddMemberAsync(
        Guid id, AddBoardMemberRequest request, IReviewBoardRepository repo, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        var ok = await repo.AddMemberAsync(id, request, ct);
        return ok ? Results.NoContent() : Results.BadRequest();
    }

    private static async Task<IResult> RemoveMemberAsync(
        Guid id, Guid userId, IReviewBoardRepository repo, CancellationToken ct)
    {
        var ok = await repo.RemoveMemberAsync(id, userId, ct);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> ListForAgentAsync(
        Guid agentId, IReviewBoardRepository repo, CancellationToken ct)
    {
        var items = await repo.ListForAgentAsync(agentId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> SubmitAsync(
        Guid agentId, SubmitReviewRequest request,
        IReviewBoardRepository repo, IAgentRepository agents,
        HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest();
        if (!TryGetUserId(ctx, out var userId)) return Results.Unauthorized();

        // Verify the agent exists in this tenant and is in pending_review state
        var agent = await agents.FindAsync(tenantId, agentId, ct);
        if (agent is null) return Results.NotFound();
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
            var req = await repo.SubmitRequestAsync(tenantId, agentId, userId, request, ct);
            return Results.Created($"/review-requests/{req.Id}", req);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Conflict(new { error = "Agent đã có request pending cho target_state này" });
        }
    }

    private static async Task<IResult> GetAsync(
        Guid requestId, IReviewBoardRepository repo, CancellationToken ct)
    {
        var r = await repo.FindRequestAsync(requestId, ct);
        return r is null ? Results.NotFound() : Results.Ok(r);
    }

    private static async Task<IResult> ListVotesAsync(
        Guid requestId, IReviewBoardRepository repo, CancellationToken ct)
    {
        var items = await repo.ListVotesAsync(requestId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> CastVoteAsync(
        Guid requestId, CastVoteRequest request,
        IReviewBoardRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryGetUserId(ctx, out var voterId)) return Results.Unauthorized();

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
        var items = await repo.ListPendingForUserAsync(userId, ct);
        return Results.Ok(new { items, total = items.Count });
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
