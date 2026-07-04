using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;

namespace Aiq.Gateway.Endpoints;

public static class AgentKnowledgeEndpoints
{
    public static IEndpointRouteBuilder MapAgentKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        // Admin-only management endpoints. Attach/detach requires admin
        // role — users are consumers and must not be able to modify the
        // knowledge base themselves.
        var admin = app
            .MapGroup("/agents/{agentId:guid}/knowledge")
            .RequireAuthorization("admin")
            .RequireRateLimiting("default");

        admin.MapGet("/", ListAsync);
        admin.MapGet("/attachable", ListAttachableAsync);
        admin.MapPost("/", AttachAsync);
        admin.MapDelete("/{noteId:guid}", DetachAsync);

        // Read-only endpoint used by the chat pipeline to pull an agent's
        // knowledge context. Any authenticated user in the tenant may call
        // this — access to the agent itself is gated by /me/agents (ACL).
        // We keep this at the "user" policy so consumers can chat.
        app.MapGet("/agents/{agentId:guid}/knowledge/context", ContextAsync)
            .RequireAuthorization("user")
            .RequireRateLimiting("default");

        return app;
    }

    private static async Task<IResult> ListAsync(
        Guid agentId, IAgentKnowledgeRepository repo, CancellationToken ct)
    {
        var items = await repo.ListSummariesForAgentAsync(agentId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> ListAttachableAsync(
        Guid agentId, IAgentKnowledgeRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId))
        {
            return Results.BadRequest(new { error = "tenant context missing" });
        }
        var items = await repo.ListAttachableAsync(tenantId, agentId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    public sealed record AttachRequest([Required] Guid NoteId);

    private static async Task<IResult> AttachAsync(
        Guid agentId, AttachRequest request, IAgentKnowledgeRepository repo,
        HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryGetUserId(ctx, out var userId)) return Results.Unauthorized();

        var ok = await repo.AttachAsync(agentId, request.NoteId, userId, ct);
        return ok
            ? Results.NoContent()
            : Results.BadRequest(new { error = "Note đã gắn hoặc không tồn tại" });
    }

    private static async Task<IResult> DetachAsync(
        Guid agentId, Guid noteId, IAgentKnowledgeRepository repo, CancellationToken ct)
    {
        var ok = await repo.DetachAsync(agentId, noteId, ct);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    /// <summary>
    /// Returns full note contents (title + content + tags) attached to the
    /// agent. The chat/api layer uses this as RAG context when the user
    /// chats with this agent instead of pulling the caller's own notes.
    /// </summary>
    private static async Task<IResult> ContextAsync(
        Guid agentId, IAgentKnowledgeRepository repo, CancellationToken ct)
    {
        var items = await repo.ListForAgentAsync(agentId, ct);
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
