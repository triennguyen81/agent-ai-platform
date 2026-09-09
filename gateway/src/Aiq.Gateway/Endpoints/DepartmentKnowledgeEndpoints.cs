using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;

namespace Aiq.Gateway.Endpoints;

/// <summary>
/// ACL V2-E: ADMIN's own Department Knowledge CRUD (text/markdown content,
/// reusing `notes` with scope='department' — see
/// IDepartmentKnowledgeRepository's doc comment). Attaching a Knowledge item
/// to a specific Department Agent draft is mapped separately, under
/// DepartmentAgentEndpoints's existing `/department/agents/{agentId}/**`
/// group, matching where that Agent's Users/Versions sub-resources already
/// live.
/// </summary>
public static class DepartmentKnowledgeEndpoints
{
    public static IEndpointRouteBuilder MapDepartmentKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        var platform = app.MapGroup("/system/knowledge")
            .RequireAuthorization("platform_admin").RequireRateLimiting("default");
        platform.MapGet("/", ListPlatformAsync);
        platform.MapPost("/", CreatePlatformAsync);
        platform.MapPatch("/{noteId:guid}", UpdatePlatformAsync);
        platform.MapPost("/{noteId:guid}/archive", ArchivePlatformAsync);
        platform.MapPost("/{noteId:guid}/restore", RestorePlatformAsync);
        platform.MapDelete("/{noteId:guid}", DeletePlatformAsync);

        var own = app
            .MapGroup("/department/knowledge")
            .RequireAuthorization("department_admin")
            .RequireRateLimiting("default");

        own.MapGet("/", ListOwnAsync);
        own.MapPost("/", CreateOwnAsync);
        own.MapGet("/{noteId:guid}", GetOwnAsync);
        own.MapPatch("/{noteId:guid}", UpdateOwnAsync);
        own.MapPost("/{noteId:guid}/archive", ArchiveOwnAsync);
        own.MapPost("/{noteId:guid}/restore", RestoreOwnAsync);
        own.MapDelete("/{noteId:guid}", DeleteOwnAsync);

        return app;
    }

    private static async Task<IResult> ListPlatformAsync(IDepartmentKnowledgeRepository repo, CancellationToken ct)
    {
        var items = await repo.ListPlatformAsync(ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> CreatePlatformAsync(CreateDepartmentKnowledgeRequest request, IDepartmentKnowledgeRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!AuthenticatedPrincipalContext.TryGet(ctx, out var principal)) return Results.Forbid();
        return Results.Created("/system/knowledge", await repo.CreatePlatformAsync(principal.UserId, request, ct));
    }

    private static async Task<IResult> UpdatePlatformAsync(Guid noteId, UpdateDepartmentKnowledgeRequest request, IDepartmentKnowledgeRepository repo, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        var item = await repo.UpdatePlatformAsync(noteId, request, ct);
        return item is null ? Results.NotFound() : Results.Ok(item);
    }

    private static Task<IResult> ArchivePlatformAsync(Guid noteId, IDepartmentKnowledgeRepository repo, CancellationToken ct) => SetPlatformArchivedAsync(noteId, true, repo, ct);
    private static Task<IResult> RestorePlatformAsync(Guid noteId, IDepartmentKnowledgeRepository repo, CancellationToken ct) => SetPlatformArchivedAsync(noteId, false, repo, ct);
    private static async Task<IResult> SetPlatformArchivedAsync(Guid noteId, bool archived, IDepartmentKnowledgeRepository repo, CancellationToken ct)
    {
        var item = await repo.SetPlatformArchivedAsync(noteId, archived, ct);
        return item is null ? Results.NotFound() : Results.Ok(item);
    }

    private static async Task<IResult> DeletePlatformAsync(Guid noteId, IDepartmentKnowledgeRepository repo, CancellationToken ct)
    {
        var deleted = await repo.DeletePlatformAsync(noteId, ct);
        return deleted ? Results.NoContent() : Results.Conflict(new { error = "knowledge_attached_or_not_found" });
    }

    private static bool TryScope(HttpContext ctx, out Guid tenantId, out Guid departmentId, out Guid actorId)
    {
        tenantId = departmentId = actorId = Guid.Empty;
        if (!AuthenticatedPrincipalContext.TryGet(ctx, out var principal)
            || principal.TenantId is not { } t || principal.DepartmentId is not { } d)
            return false;
        tenantId = t;
        departmentId = d;
        actorId = principal.UserId;
        return true;
    }

    private static async Task<IResult> ListOwnAsync(IDepartmentKnowledgeRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var items = await repo.ListOwnAsync(tenantId, departmentId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> GetOwnAsync(Guid noteId, IDepartmentKnowledgeRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var n = await repo.FindOwnAsync(tenantId, departmentId, noteId, ct);
        return n is null ? Results.NotFound() : Results.Ok(n);
    }

    private static async Task<IResult> CreateOwnAsync(CreateDepartmentKnowledgeRequest request, IDepartmentKnowledgeRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryScope(ctx, out var tenantId, out var departmentId, out var actorId)) return Results.Forbid();
        var n = await repo.CreateAsync(tenantId, departmentId, actorId, request, ct);
        return Results.Created($"/department/knowledge/{n.Id}", n);
    }

    private static async Task<IResult> UpdateOwnAsync(Guid noteId, UpdateDepartmentKnowledgeRequest request, IDepartmentKnowledgeRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var n = await repo.UpdateAsync(tenantId, departmentId, noteId, request, ct);
        return n is null ? Results.NotFound() : Results.Ok(n);
    }

    private static Task<IResult> ArchiveOwnAsync(Guid noteId, IDepartmentKnowledgeRepository repo, HttpContext ctx, CancellationToken ct) => SetArchivedAsync(noteId, true, repo, ctx, ct);
    private static Task<IResult> RestoreOwnAsync(Guid noteId, IDepartmentKnowledgeRepository repo, HttpContext ctx, CancellationToken ct) => SetArchivedAsync(noteId, false, repo, ctx, ct);
    private static async Task<IResult> SetArchivedAsync(Guid noteId, bool archived, IDepartmentKnowledgeRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var n = await repo.SetArchivedAsync(tenantId, departmentId, noteId, archived, ct);
        return n is null ? Results.NotFound() : Results.Ok(n);
    }

    private static async Task<IResult> DeleteOwnAsync(Guid noteId, IDepartmentKnowledgeRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var existing = await repo.FindOwnAsync(tenantId, departmentId, noteId, ct);
        if (existing is null) return Results.NotFound();
        var deleted = await repo.DeleteAsync(tenantId, departmentId, noteId, ct);
        return deleted ? Results.NoContent() : Results.Conflict(new { error = "knowledge_attached_to_agent" });
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
