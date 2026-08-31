using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Notes;
using Aiq.Gateway.Middleware;
using Microsoft.AspNetCore.Http;

namespace Aiq.Gateway.Endpoints;

public static class NoteEndpoints
{
    public static IEndpointRouteBuilder MapNoteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app
            .MapGroup("/notes")
            .RequireAuthorization()
            .RequireRateLimiting("default");

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPatch("/{id:guid}", UpdateAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);

        return app;
    }

    private static async Task<IResult> ListAsync(
        INoteRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }

        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest();
        var notes = await repo.ListAsync(tenantId, userId, ct);
        return Results.Ok(new NotesListResponse(notes, notes.Count));
    }

    private static async Task<IResult> CreateAsync(
        CreateNoteRequest request,
        INoteRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryValidate(request, out var errors))
        {
            return Results.ValidationProblem(errors);
        }
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }

        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest();
        var note = await repo.CreateAsync(
            tenantId, userId,
            request.Title,
            request.Content ?? string.Empty,
            request.Tags ?? Array.Empty<string>(),
            ct);

        return Results.Created($"/notes/{note.Id}", note);
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        INoteRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest();
        var note = await repo.FindAsync(tenantId, userId, id, ct);
        return note is null ? Results.NotFound() : Results.Ok(note);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateNoteRequest request,
        INoteRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }

        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest();
        var note = await repo.UpdateAsync(
            tenantId, userId, id, request.Title, request.Content, request.Tags, ct);

        return note is null ? Results.NotFound() : Results.Ok(note);
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        INoteRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }

        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest();
        var deleted = await repo.DeleteAsync(tenantId, userId, id, ct);
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
