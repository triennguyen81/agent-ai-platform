using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;
using Npgsql;

namespace Aiq.Gateway.Endpoints;

public static class WorkflowEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowEndpoints(this IEndpointRouteBuilder app)
    {
        // Platform-level catalog, same shape as /tools — mutations stay
        // super_admin only. Agent versions reference a workflow by id (see
        // DynamicAgentEndpoints/DepartmentAgentEndpoints create/update
        // version) the same way they reference tool/skill ids. Listing is
        // catalog_reader (ACL V2-C: department_admin selects an existing
        // platform workflow for its own Department Agent draft — read-only).
        var super = app
            .MapGroup("/workflows")
            .RequireAuthorization("platform_admin")
            .RequireRateLimiting("default");

        app.MapGet("/workflows", ListAsync).RequireAuthorization("catalog_reader").RequireRateLimiting("default");
        super.MapPost("/", CreateAsync);
        super.MapGet("/{id:guid}", GetAsync);
        super.MapPatch("/{id:guid}", UpdateAsync);

        // ACL V2-E: ADMIN's own Department Workflow CRUD + immutable
        // draft/publish versioning. Tenant/department always derived from
        // AuthenticatedPrincipalContext (TryScope below) — never from the
        // URL or request body, exactly like DepartmentAgentEndpoints/
        // CapabilityEndpoints (V2-C/V2-D).
        var own = app
            .MapGroup("/department/workflows")
            .RequireAuthorization("department_admin")
            .RequireRateLimiting("default");

        own.MapGet("/", ListOwnAsync);
        own.MapPost("/", CreateOwnAsync);
        own.MapGet("/{workflowId:guid}", GetOwnAsync);
        own.MapPatch("/{workflowId:guid}", UpdateOwnMetadataAsync);
        own.MapPost("/{workflowId:guid}/enable", EnableOwnAsync);
        own.MapPost("/{workflowId:guid}/disable", DisableOwnAsync);
        own.MapGet("/{workflowId:guid}/versions", ListOwnVersionsAsync);
        own.MapPost("/{workflowId:guid}/versions", CreateOwnDraftVersionAsync);
        own.MapPatch("/{workflowId:guid}/versions/{versionId:guid}", UpdateOwnDraftVersionAsync);
        own.MapPost("/{workflowId:guid}/versions/{versionId:guid}/publish", PublishOwnVersionAsync);

        return app;
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

    private static async Task<IResult> ListOwnAsync(IWorkflowRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var items = await repo.ListOwnDepartmentWorkflowsAsync(tenantId, departmentId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> GetOwnAsync(Guid workflowId, IWorkflowRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var w = await repo.FindOwnDepartmentWorkflowAsync(tenantId, departmentId, workflowId, ct);
        return w is null ? Results.NotFound() : Results.Ok(w);
    }

    private static async Task<IResult> CreateOwnAsync(CreateDepartmentWorkflowRequest request, IWorkflowRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryScope(ctx, out var tenantId, out var departmentId, out var actorId)) return Results.Forbid();
        if (request.Definition is { } def)
        {
            if (!WorkflowDefinitions.TryCanonicalize(def, out var canonical, out var stepError))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["definition"] = [stepError!] });
            request = request with { Definition = canonical };
        }
        var w = await repo.CreateDepartmentWorkflowAsync(tenantId, departmentId, actorId, request, ct);
        return Results.Created($"/department/workflows/{w.Id}", w);
    }

    private static async Task<IResult> UpdateOwnMetadataAsync(Guid workflowId, UpdateDepartmentWorkflowRequest request, IWorkflowRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var w = await repo.UpdateDepartmentWorkflowMetadataAsync(tenantId, departmentId, workflowId, request, ct);
        return w is null ? Results.NotFound() : Results.Ok(w);
    }

    private static Task<IResult> EnableOwnAsync(Guid workflowId, IWorkflowRepository repo, HttpContext ctx, CancellationToken ct) => SetOwnEnabledAsync(workflowId, true, repo, ctx, ct);
    private static Task<IResult> DisableOwnAsync(Guid workflowId, IWorkflowRepository repo, HttpContext ctx, CancellationToken ct) => SetOwnEnabledAsync(workflowId, false, repo, ctx, ct);
    private static async Task<IResult> SetOwnEnabledAsync(Guid workflowId, bool enabled, IWorkflowRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var w = await repo.SetDepartmentWorkflowEnabledAsync(tenantId, departmentId, workflowId, enabled, ct);
        return w is null ? Results.NotFound() : Results.Ok(w);
    }

    private static async Task<IResult> ListOwnVersionsAsync(Guid workflowId, IWorkflowRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var items = await repo.ListDepartmentWorkflowVersionsAsync(tenantId, departmentId, workflowId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    public sealed record CreateWorkflowDraftVersionRequest(JsonElement? Definition);
    public sealed record UpdateWorkflowDraftVersionRequest([property: Required] JsonElement Definition);

    private static async Task<IResult> CreateOwnDraftVersionAsync(Guid workflowId, CreateWorkflowDraftVersionRequest request, IWorkflowRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out var actorId)) return Results.Forbid();
        JsonElement? definition = null;
        if (request.Definition is { } def)
        {
            if (!WorkflowDefinitions.TryCanonicalize(def, out var canonical, out var stepError))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["definition"] = [stepError!] });
            definition = canonical;
        }
        try
        {
            var version = await repo.CreateDepartmentWorkflowDraftVersionAsync(tenantId, departmentId, workflowId, actorId, definition, ct);
            return version is null ? Results.NotFound() : Results.Created($"/department/workflows/{workflowId}/versions/{version.Id}", version);
        }
        catch (InvalidOperationException ex) when (ex.Message == "workflow_already_has_draft")
        {
            return Results.Conflict(new { error = "workflow_already_has_draft" });
        }
    }

    private static async Task<IResult> UpdateOwnDraftVersionAsync(Guid workflowId, Guid versionId, UpdateWorkflowDraftVersionRequest request, IWorkflowRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        if (!WorkflowDefinitions.TryCanonicalize(request.Definition, out var canonical, out var stepError))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["definition"] = [stepError!] });
        var version = await repo.UpdateDepartmentWorkflowDraftVersionAsync(tenantId, departmentId, workflowId, versionId, canonical, ct);
        return version is null ? Results.NotFound() : Results.Ok(version);
    }

    private static async Task<IResult> PublishOwnVersionAsync(Guid workflowId, Guid versionId, IWorkflowRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out var actorId)) return Results.Forbid();
        try
        {
            var version = await repo.PublishDepartmentWorkflowVersionAsync(tenantId, departmentId, workflowId, versionId, actorId, ct);
            return version is null ? Results.NotFound() : Results.Ok(version);
        }
        catch (InvalidOperationException ex) when (ex.Message == "workflow_has_no_steps")
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["definition"] = ["workflow_has_no_steps"] });
        }
    }

    private static async Task<IResult> ListAsync(IWorkflowRepository repo, CancellationToken ct, bool activeOnly = false)
    {
        var items = await repo.ListAsync(activeOnly, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> GetAsync(Guid id, IWorkflowRepository repo, CancellationToken ct)
    {
        var w = await repo.FindAsync(id, ct);
        return w is null ? Results.NotFound() : Results.Ok(w);
    }

    private static async Task<IResult> CreateAsync(CreateWorkflowRequest request, IWorkflowRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryGetUserId(ctx, out var actorId)) return Results.Unauthorized();
        if (request.Definition is { } def)
        {
            if (!WorkflowDefinitions.TryCanonicalize(def, out var canonical, out var stepError))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["definition"] = [stepError!] });
            request = request with { Definition = canonical };
        }
        var w = await repo.CreateAsync(actorId, request, ct);
        return Results.Created($"/workflows/{w.Id}", w);
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdateWorkflowRequest request, IWorkflowRepository repo, CancellationToken ct)
    {
        if (request.Definition is { } def)
        {
            if (!WorkflowDefinitions.TryCanonicalize(def, out var canonical, out var stepError))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["definition"] = [stepError!] });
            request = request with { Definition = canonical };
        }
        var w = await repo.UpdateAsync(id, request, ct);
        return w is null ? Results.NotFound() : Results.Ok(w);
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
