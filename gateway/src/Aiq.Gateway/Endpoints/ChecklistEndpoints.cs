using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Contracts;
using Aiq.Gateway.Middleware;
using Microsoft.AspNetCore.Http;

namespace Aiq.Gateway.Endpoints;

public static class ChecklistEndpoints
{
    public static IEndpointRouteBuilder MapChecklistEndpoints(this IEndpointRouteBuilder app)
    {
        var perContract = app
            .MapGroup("/contracts/{contractId:guid}/checklist")
            .RequireAuthorization("admin")
            .RequireRateLimiting("default");

        perContract.MapGet("/", ListAsync);
        perContract.MapPost("/", BulkCreateAsync);

        var perItem = app
            .MapGroup("/checklist/{itemId:guid}")
            .RequireAuthorization("admin")
            .RequireRateLimiting("default");

        perItem.MapPatch("/", UpdateAsync);
        perItem.MapDelete("/", DeleteAsync);

        return app;
    }

    private static async Task<IResult> ListAsync(
        Guid contractId,
        IChecklistRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId)) return Results.Unauthorized();
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest(new { error = "tenant context missing" });

        var items = await repo.ListAsync(tenantId, userId, contractId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> BulkCreateAsync(
        Guid contractId,
        BulkCreateChecklistRequest request,
        IChecklistRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryGetUserId(ctx, out var userId)) return Results.Unauthorized();
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest(new { error = "tenant context missing" });

        if (request.Items.Count == 0)
        {
            return Results.BadRequest(new { error = "items không được rỗng" });
        }
        if (request.Items.Count > 100)
        {
            return Results.BadRequest(new { error = "Tối đa 100 items / lần" });
        }

        var created = await repo.BulkCreateAsync(tenantId, userId, contractId, request, ct);
        if (created.Count == 0)
        {
            return Results.NotFound(new { error = "Contract không tồn tại hoặc không thuộc về user" });
        }

        return Results.Created($"/contracts/{contractId}/checklist", new { items = created, total = created.Count });
    }

    private static async Task<IResult> UpdateAsync(
        Guid itemId,
        UpdateChecklistItemRequest request,
        IChecklistRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId)) return Results.Unauthorized();
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest(new { error = "tenant context missing" });

        if (request.Status is { Length: > 0 } status
            && status is not ("open" or "in_progress" or "done" or "skipped"))
        {
            return Results.BadRequest(new { error = $"Status không hợp lệ: {status}" });
        }

        var updated = await repo.UpdateAsync(tenantId, userId, itemId, request, ct);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    private static async Task<IResult> DeleteAsync(
        Guid itemId,
        IChecklistRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId)) return Results.Unauthorized();
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest(new { error = "tenant context missing" });

        var deleted = await repo.DeleteAsync(tenantId, userId, itemId, ct);
        return deleted ? Results.NoContent() : Results.NotFound();
    }

    private static bool TryGetUserId(HttpContext ctx, out Guid userId)
    {
        var raw = ctx.User.FindFirst("sub")?.Value
            ?? ctx.User.FindFirst("nameidentifier")?.Value
            ?? ctx.User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
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
