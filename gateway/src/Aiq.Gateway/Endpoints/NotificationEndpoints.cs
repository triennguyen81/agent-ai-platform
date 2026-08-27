using Aiq.Gateway.Contracts;
using Aiq.Gateway.Middleware;

namespace Aiq.Gateway.Endpoints;

public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app
            .MapGroup("/notifications")
            .RequireAuthorization("user")
            .RequireRateLimiting("default");

        group.MapGet("/", ListAsync);
        group.MapGet("/unread-count", UnreadCountAsync);
        group.MapPatch("/{id:guid}/read", MarkReadAsync);
        group.MapPost("/mark-all-read", MarkAllReadAsync);

        return app;
    }

    private static async Task<IResult> ListAsync(
        INotificationRepository repo,
        HttpContext ctx,
        CancellationToken ct,
        bool unread = false,
        int limit = 50)
    {
        if (!TryGetUserId(ctx, out var userId)) return Results.Unauthorized();

        var items = await repo.ListForUserAsync(TenantContext.Require(ctx), userId, unread, limit, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> UnreadCountAsync(
        INotificationRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId)) return Results.Unauthorized();
        var count = await repo.UnreadCountAsync(TenantContext.Require(ctx), userId, ct);
        return Results.Ok(new { count });
    }

    private static async Task<IResult> MarkReadAsync(
        Guid id,
        INotificationRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId)) return Results.Unauthorized();
        var ok = await repo.MarkReadAsync(TenantContext.Require(ctx), userId, id, ct);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> MarkAllReadAsync(
        INotificationRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId)) return Results.Unauthorized();
        var n = await repo.MarkAllReadAsync(TenantContext.Require(ctx), userId, ct);
        return Results.Ok(new { updated = n });
    }

    private static bool TryGetUserId(HttpContext ctx, out Guid userId)
    {
        var raw = ctx.User.FindFirst("sub")?.Value
            ?? ctx.User.FindFirst("nameidentifier")?.Value
            ?? ctx.User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
        return Guid.TryParse(raw, out userId);
    }
}
