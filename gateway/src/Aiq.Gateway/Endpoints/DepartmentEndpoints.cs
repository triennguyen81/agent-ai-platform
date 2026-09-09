using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Audit;
using Aiq.Gateway.Data;
using Aiq.Gateway.Middleware;
using Npgsql;

namespace Aiq.Gateway.Endpoints;

/// <summary>
/// Phase D5: canonical TENANT_OWNER department CRUD. Deliberately a
/// separate file from UserEndpoints.cs (which keeps the pre-existing
/// GET /tenant/departments list route) so this net-new surface area is
/// easy to review on its own. tenant_id is never accepted from the
/// request — every handler derives it from TenantContext/
/// AuthenticatedPrincipalContext (server-side, JWT-cross-checked against
/// the DB by TenantContextMiddleware), exactly like every other D2-D4
/// tenant-scoped endpoint.
/// </summary>
public static class DepartmentEndpoints
{
    // [property: ...] is required, not stylistic — see UserEndpoints.cs's
    // ChangeTenantUserRoleRequest comment: a validation attribute placed
    // directly on a positional-record parameter binds to the parameter, not
    // the generated property, so Validator.TryValidateObject (which
    // reflects over properties) silently never runs it. New D5 code fixes
    // this properly rather than propagating the project-wide pre-existing
    // pattern into more records.
    public sealed record CreateDepartmentRequest(
        [property: Required, RegularExpression("^[a-z0-9]+(?:-[a-z0-9]+)*$"), StringLength(60, MinimumLength = 2)] string Code,
        [property: Required, StringLength(120, MinimumLength = 2)] string Name);

    public sealed record UpdateDepartmentRequest(
        [property: Required, StringLength(120, MinimumLength = 2)] string Name);

    public static IEndpointRouteBuilder MapDepartmentEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /tenant/departments/{id} is reachable by admin too (own
        // department only) — "admin" policy is hierarchical (super_admin ∨
        // tenant_owner ∨ admin), so the handler itself enforces the
        // per-role scoping. Every mutation stays tenant_owner-literal.
        app.MapGet("/tenant/departments/{departmentId:guid}", GetDepartmentAsync)
            .RequireAuthorization("admin").RequireRateLimiting("default");

        var owner = app.MapGroup("/tenant/departments")
            .RequireAuthorization("tenant_governance").RequireRateLimiting("default");
        owner.MapPost("/", CreateDepartmentAsync);
        owner.MapPatch("/{departmentId:guid}", UpdateDepartmentAsync);
        owner.MapPost("/{departmentId:guid}/deactivate", DeactivateDepartmentAsync);
        owner.MapPost("/{departmentId:guid}/activate", ActivateDepartmentAsync);

        return app;
    }

    private static async Task<IResult> GetDepartmentAsync(
        Guid departmentId, IDepartmentRepository departments, HttpContext ctx, CancellationToken ct)
    {
        if (!AuthenticatedPrincipalContext.TryGet(ctx, out var principal) || principal.TenantId is not { } tenantId)
            return Results.BadRequest(new { error = "tenant_context_missing" });
        // ADMIN may only ever read their OWN department — never enumerate
        // another department in the same tenant by guessing its id.
        if (!principal.IsInRole("tenant_owner") && departmentId != principal.DepartmentId)
            return Results.NotFound();
        var department = await departments.FindAsync(tenantId, departmentId, ct);
        return department is null ? Results.NotFound() : Results.Ok(department);
    }

    private static async Task<IResult> CreateDepartmentAsync(
        CreateDepartmentRequest request, IDepartmentRepository departments, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest(new { error = "tenant_context_missing" });
        try
        {
            var created = await departments.CreateAsync(tenantId, request.Code.Trim().ToLowerInvariant(), request.Name.Trim(), ct);
            return Results.Created($"/tenant/departments/{created.Id}", created);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Conflict(new { error = "department_code_or_name_already_exists" });
        }
    }

    private static async Task<IResult> UpdateDepartmentAsync(
        Guid departmentId, UpdateDepartmentRequest request, IDepartmentRepository departments, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest(new { error = "tenant_context_missing" });
        try
        {
            var updated = await departments.UpdateNameAsync(tenantId, departmentId, request.Name.Trim(), ct);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Conflict(new { error = "department_name_already_exists" });
        }
    }

    private static async Task<IResult> DeactivateDepartmentAsync(
        Guid departmentId, IDepartmentRepository departments, IAuditLogger audit, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest(new { error = "tenant_context_missing" });
        if (await departments.FindAsync(tenantId, departmentId, ct) is null) return Results.NotFound();

        var blocker = await departments.FindDeactivationBlockerAsync(tenantId, departmentId, ct);
        if (blocker is not null) return Results.Conflict(new { error = blocker });

        DepartmentRecord? updated;
        try
        {
            updated = await departments.SetStatusAsync(tenantId, departmentId, "inactive", ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "23514")
        {
            // The tenant's default department can never be deactivated —
            // migration 019's departments_default_is_active CHECK enforces
            // this at the DB layer regardless of member count (see D5 §4:
            // the default marker is not user-editable/transferable yet).
            return Results.Conflict(new { error = "cannot_deactivate_default_department" });
        }
        if (!TryActor(ctx, out var actorId)) return Results.Unauthorized();
        await audit.WriteAsync(new AuditEntry(
            actorId, "department.deactivate", "department", departmentId.ToString(), null,
            ctx.Connection.RemoteIpAddress, ctx.Request.Headers.UserAgent.ToString()), ct);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    private static async Task<IResult> ActivateDepartmentAsync(
        Guid departmentId, IDepartmentRepository departments, IAuditLogger audit, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest(new { error = "tenant_context_missing" });
        var updated = await departments.SetStatusAsync(tenantId, departmentId, "active", ct);
        if (updated is null) return Results.NotFound();
        if (!TryActor(ctx, out var actorId)) return Results.Unauthorized();
        await audit.WriteAsync(new AuditEntry(
            actorId, "department.activate", "department", departmentId.ToString(), null,
            ctx.Connection.RemoteIpAddress, ctx.Request.Headers.UserAgent.ToString()), ct);
        return Results.Ok(updated);
    }

    private static bool TryActor(HttpContext ctx, out Guid actorId)
    {
        var raw = ctx.User.FindFirst("sub")?.Value;
        return Guid.TryParse(raw, out actorId);
    }

    private static bool TryValidate(object instance, out Dictionary<string, string[]> errors)
    {
        var validationContext = new ValidationContext(instance);
        var results = new List<ValidationResult>();
        var ok = Validator.TryValidateObject(instance, validationContext, results, validateAllProperties: true);
        errors = results
            .SelectMany(r => r.MemberNames.Select(m => (Member: m, r.ErrorMessage)))
            .GroupBy(t => t.Member)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage ?? "Invalid").ToArray());
        return ok;
    }
}
