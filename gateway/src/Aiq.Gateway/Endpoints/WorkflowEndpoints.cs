using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Aiq.Gateway.Governance;

namespace Aiq.Gateway.Endpoints;

public static class WorkflowEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowEndpoints(this IEndpointRouteBuilder app)
    {
        // Platform-level catalog, same shape as /tools — super_admin only.
        // Agent versions reference a workflow by id (see DynamicAgentEndpoints
        // create/update version) the same way they reference tool/skill ids.
        var super = app
            .MapGroup("/workflows")
            .RequireAuthorization("super_admin")
            .RequireRateLimiting("default");

        super.MapGet("/", ListAsync);
        super.MapPost("/", CreateAsync);
        super.MapGet("/{id:guid}", GetAsync);
        super.MapPatch("/{id:guid}", UpdateAsync);

        return app;
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
        if (request.Definition is { } def && !ValidateStepTypes(def, out var stepError))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["definition"] = [stepError!] });
        var w = await repo.CreateAsync(actorId, request, ct);
        return Results.Created($"/workflows/{w.Id}", w);
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdateWorkflowRequest request, IWorkflowRepository repo, CancellationToken ct)
    {
        if (request.Definition is { } def && !ValidateStepTypes(def, out var stepError))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["definition"] = [stepError!] });
        var w = await repo.UpdateAsync(id, request, ct);
        return w is null ? Results.NotFound() : Results.Ok(w);
    }

    /// <summary>Gateway-side shape check, mirrored (defense in depth) by the
    /// runtime's own fail-closed check at manifest-resolution time.</summary>
    private static bool ValidateStepTypes(JsonElement definition, out string? error)
    {
        error = null;
        if (definition.ValueKind != JsonValueKind.Object || !definition.TryGetProperty("steps", out var steps)
            || steps.ValueKind != JsonValueKind.Array)
        {
            error = "definition must be an object with a 'steps' array";
            return false;
        }
        foreach (var step in steps.EnumerateArray())
        {
            if (!step.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || !WorkflowStepTypes.Known.Contains(type.GetString()!))
            {
                error = $"unknown or missing step type (allowed: {string.Join(", ", WorkflowStepTypes.Known)})";
                return false;
            }
        }
        return true;
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
