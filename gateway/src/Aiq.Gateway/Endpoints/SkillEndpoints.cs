using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;

namespace Aiq.Gateway.Endpoints;

/// <summary>
/// ACL V2-E: ADMIN's own Department Skill CRUD. Platform/filesystem Skills
/// remain the Python runtime's `SKILL.md` registry, unchanged — browsed via
/// the existing Agent-side `GET /skills/list`, not this Gateway surface.
/// This exposes only the NEW department-owned declarative-instruction
/// catalog migration 024 introduced (`skills` table) and V2-E finally
/// wires up. Tenant/department always derived from
/// AuthenticatedPrincipalContext, never the URL or request body.
/// </summary>
public static class SkillEndpoints
{
    public static IEndpointRouteBuilder MapSkillEndpoints(this IEndpointRouteBuilder app)
    {
        var platform = app.MapGroup("/system/skills")
            .RequireAuthorization("platform_admin").RequireRateLimiting("default");
        platform.MapGet("/", ListPlatformAsync);
        platform.MapPost("/", CreatePlatformAsync);
        platform.MapPatch("/{skillId:guid}", UpdatePlatformMetadataAsync);
        platform.MapPatch("/{skillId:guid}/content", UpdatePlatformContentAsync);
        platform.MapPost("/{skillId:guid}/enable", EnablePlatformAsync);
        platform.MapPost("/{skillId:guid}/disable", DisablePlatformAsync);

        var own = app
            .MapGroup("/department/skills")
            .RequireAuthorization("department_admin")
            .RequireRateLimiting("default");

        own.MapGet("/", ListOwnAsync);
        own.MapPost("/", CreateOwnAsync);
        own.MapGet("/{skillId:guid}", GetOwnAsync);
        own.MapPatch("/{skillId:guid}", UpdateOwnMetadataAsync);
        own.MapPatch("/{skillId:guid}/content", UpdateOwnContentAsync);
        own.MapPost("/{skillId:guid}/enable", EnableOwnAsync);
        own.MapPost("/{skillId:guid}/disable", DisableOwnAsync);

        return app;
    }

    private static async Task<IResult> ListPlatformAsync(ISkillRepository repo, CancellationToken ct)
    {
        var items = await repo.ListPlatformSkillsAsync(ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> CreatePlatformAsync(CreateDepartmentSkillRequest request, ISkillRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!AuthenticatedPrincipalContext.TryGet(ctx, out var principal)) return Results.Forbid();
        try { return Results.Created("/system/skills", await repo.CreatePlatformSkillAsync(principal.UserId, request, ct)); }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505") { return Results.Conflict(new { error = "skill_code_exists" }); }
    }

    private static async Task<IResult> UpdatePlatformMetadataAsync(Guid skillId, UpdateDepartmentSkillMetadataRequest request, ISkillRepository repo, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        var item = await repo.UpdatePlatformSkillMetadataAsync(skillId, request, ct);
        return item is null ? Results.NotFound() : Results.Ok(item);
    }

    private static async Task<IResult> UpdatePlatformContentAsync(Guid skillId, UpdateDepartmentSkillContentRequest request, ISkillRepository repo, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        try
        {
            var item = await repo.UpdatePlatformSkillContentAsync(skillId, request, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        }
        catch (InvalidOperationException ex) when (ex.Message == "skill_content_locked")
        {
            return Results.Conflict(new { error = "skill_content_locked_by_published_agent_version" });
        }
    }

    private static Task<IResult> EnablePlatformAsync(Guid skillId, ISkillRepository repo, CancellationToken ct) => SetPlatformEnabledAsync(skillId, true, repo, ct);
    private static Task<IResult> DisablePlatformAsync(Guid skillId, ISkillRepository repo, CancellationToken ct) => SetPlatformEnabledAsync(skillId, false, repo, ct);
    private static async Task<IResult> SetPlatformEnabledAsync(Guid skillId, bool enabled, ISkillRepository repo, CancellationToken ct)
    {
        var item = await repo.SetPlatformSkillEnabledAsync(skillId, enabled, ct);
        return item is null ? Results.NotFound() : Results.Ok(item);
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

    private static async Task<IResult> ListOwnAsync(ISkillRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var items = await repo.ListOwnDepartmentSkillsAsync(tenantId, departmentId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> GetOwnAsync(Guid skillId, ISkillRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var s = await repo.FindOwnDepartmentSkillAsync(tenantId, departmentId, skillId, ct);
        return s is null ? Results.NotFound() : Results.Ok(s);
    }

    private static async Task<IResult> CreateOwnAsync(CreateDepartmentSkillRequest request, ISkillRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryScope(ctx, out var tenantId, out var departmentId, out var actorId)) return Results.Forbid();
        try
        {
            var s = await repo.CreateDepartmentSkillAsync(tenantId, departmentId, actorId, request, ct);
            return Results.Created($"/department/skills/{s.Id}", s);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Conflict(new { error = "skill_code_exists" });
        }
    }

    private static async Task<IResult> UpdateOwnMetadataAsync(Guid skillId, UpdateDepartmentSkillMetadataRequest request, ISkillRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var s = await repo.UpdateDepartmentSkillMetadataAsync(tenantId, departmentId, skillId, request, ct);
        return s is null ? Results.NotFound() : Results.Ok(s);
    }

    private static async Task<IResult> UpdateOwnContentAsync(Guid skillId, UpdateDepartmentSkillContentRequest request, ISkillRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        try
        {
            var s = await repo.UpdateDepartmentSkillContentAsync(tenantId, departmentId, skillId, request, ct);
            return s is null ? Results.NotFound() : Results.Ok(s);
        }
        catch (InvalidOperationException ex) when (ex.Message == "skill_content_locked")
        {
            return Results.Conflict(new { error = "skill_content_locked_by_published_agent_version" });
        }
    }

    private static Task<IResult> EnableOwnAsync(Guid skillId, ISkillRepository repo, HttpContext ctx, CancellationToken ct) => SetOwnEnabledAsync(skillId, true, repo, ctx, ct);
    private static Task<IResult> DisableOwnAsync(Guid skillId, ISkillRepository repo, HttpContext ctx, CancellationToken ct) => SetOwnEnabledAsync(skillId, false, repo, ctx, ct);
    private static async Task<IResult> SetOwnEnabledAsync(Guid skillId, bool enabled, ISkillRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var s = await repo.SetDepartmentSkillEnabledAsync(tenantId, departmentId, skillId, enabled, ct);
        return s is null ? Results.NotFound() : Results.Ok(s);
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
