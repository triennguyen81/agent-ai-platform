using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Audit;
using Aiq.Gateway.Data;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;
using Npgsql;

namespace Aiq.Gateway.Endpoints;

/// <summary>
/// Phase D4: business capability catalog, ADMIN/USER "Nghiệp vụ" visibility,
/// the canonical capability->agent resolver, and TENANT_OWNER department
/// capability-binding management. Never trusts a client-supplied
/// tenant/department id — everything routes through
/// AuthenticatedPrincipalContext, exactly like D2/D3's department and
/// agent-entitlement endpoints.
/// </summary>
public static class CapabilityEndpoints
{
    public sealed record SetBindingRequest([property: Required] Guid AgentId);

    public static IEndpointRouteBuilder MapCapabilityEndpoints(this IEndpointRouteBuilder app)
    {
        // ACL V2-C: department_admin needs to browse the platform capability
        // catalog to attach an existing platform capability to its own
        // Department Agent draft (Decisions §5/§13) — read-only; the catalog
        // itself (create/edit/delete a capability code) stays platform_admin.
        app.MapGet("/system/capabilities", ListCatalogAsync)
            .RequireAuthorization("catalog_reader").RequireRateLimiting("default");
        var platformCapabilities = app.MapGroup("/system/capabilities")
            .RequireAuthorization("platform_admin").RequireRateLimiting("default");
        platformCapabilities.MapPost("/", CreatePlatformAsync);
        platformCapabilities.MapPatch("/{capabilityId:guid}", UpdatePlatformAsync);
        platformCapabilities.MapPost("/{capabilityId:guid}/enable", EnablePlatformAsync);
        platformCapabilities.MapPost("/{capabilityId:guid}/disable", DisablePlatformAsync);

        app.MapGet("/me/capabilities", ListMyCapabilitiesAsync)
            .RequireAuthorization("department_member").RequireRateLimiting("default");
        app.MapGet("/me/capabilities/{code}/agent", ResolveCapabilityAsync)
            .RequireAuthorization("department_member").RequireRateLimiting("default");
        // "Xử lý bằng trợ lý" selector data: every agent this principal could
        // be routed to for one capability, so the caller can decide 0/1/many
        // BEFORE ever calling the run endpoint — /me/capabilities/{code}/agent
        // above stays the single canonical resolve-or-deny decision (unbound
        // ambiguity there still 409s exactly as before for any caller that
        // never supplies preferredAgentId).
        app.MapGet("/me/capabilities/{code}/agents", ListEligibleAgentsAsync)
            .RequireAuthorization("department_member").RequireRateLimiting("default");

        // ACL V2-D: TENANT_OWNER keeps read-only oversight of its
        // departments' capability landscape (which capabilities exist, which
        // agent each is bound to) — mapped standalone with the literal
        // tenant_governance policy, exactly like D3's
        // /tenant/departments/{id}/agents group. Binding MUTATION moved to
        // ADMIN below (V2-D brief §12: "TENANT_OWNER should no longer be the
        // normal binding editor") — this group no longer maps PUT/DELETE.
        var departmentCapabilities = app.MapGroup("/tenant/departments/{departmentId:guid}/capabilities")
            .RequireAuthorization("tenant_governance").RequireRateLimiting("default");
        departmentCapabilities.MapGet("/", ListDepartmentCapabilitiesAsync);

        // ACL V2-D: ADMIN's own Department capability CRUD + operational
        // binding. Tenant/department always derived from
        // AuthenticatedPrincipalContext (TryScope below) — never from the URL
        // or request body, exactly like DepartmentAgentEndpoints (V2-C).
        var ownDepartmentCapabilities = app.MapGroup("/department/capabilities")
            .RequireAuthorization("department_admin").RequireRateLimiting("default");
        ownDepartmentCapabilities.MapGet("/", ListOwnAsync);
        ownDepartmentCapabilities.MapPost("/", CreateOwnAsync);
        ownDepartmentCapabilities.MapGet("/bindings", ListOwnBindingsAsync);
        ownDepartmentCapabilities.MapGet("/{capabilityId:guid}", GetOwnAsync);
        ownDepartmentCapabilities.MapPatch("/{capabilityId:guid}", UpdateOwnAsync);
        ownDepartmentCapabilities.MapPost("/{capabilityId:guid}/enable", EnableOwnAsync);
        ownDepartmentCapabilities.MapPost("/{capabilityId:guid}/disable", DisableOwnAsync);
        ownDepartmentCapabilities.MapPut("/{capabilityId:guid}/binding", SetOwnBindingAsync);
        ownDepartmentCapabilities.MapDelete("/{capabilityId:guid}/binding", RevokeOwnBindingAsync);

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

    private static async Task<IResult> ListOwnAsync(ICapabilityRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var items = await repo.ListOwnDepartmentCapabilitiesAsync(tenantId, departmentId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> GetOwnAsync(Guid capabilityId, ICapabilityRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var item = await repo.FindOwnDepartmentCapabilityAsync(tenantId, departmentId, capabilityId, ct);
        return item is null ? Results.NotFound() : Results.Ok(item);
    }

    private static async Task<IResult> CreateOwnAsync(CreateDepartmentCapabilityRequest request, ICapabilityRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryScope(ctx, out var tenantId, out var departmentId, out var actorId)) return Results.Forbid();
        try
        {
            var capability = await repo.CreateDepartmentCapabilityAsync(tenantId, departmentId, actorId, request, ct);
            return Results.Created($"/department/capabilities/{capability.Id}", capability);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Conflict(new { error = "capability_code_exists" });
        }
    }

    private static async Task<IResult> UpdateOwnAsync(Guid capabilityId, UpdateDepartmentCapabilityRequest request, ICapabilityRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var capability = await repo.UpdateDepartmentCapabilityAsync(tenantId, departmentId, capabilityId, request, ct);
        return capability is null ? Results.NotFound() : Results.Ok(capability);
    }

    private static Task<IResult> EnableOwnAsync(Guid capabilityId, ICapabilityRepository repo, HttpContext ctx, CancellationToken ct) => SetEnabledAsync(capabilityId, true, repo, ctx, ct);
    private static Task<IResult> DisableOwnAsync(Guid capabilityId, ICapabilityRepository repo, HttpContext ctx, CancellationToken ct) => SetEnabledAsync(capabilityId, false, repo, ctx, ct);
    private static async Task<IResult> SetEnabledAsync(Guid capabilityId, bool enabled, ICapabilityRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var capability = await repo.SetDepartmentCapabilityEnabledAsync(tenantId, departmentId, capabilityId, enabled, ct);
        return capability is null ? Results.NotFound() : Results.Ok(capability);
    }

    /// <summary>ADMIN's own binding-management view — same shape/data as the owner's oversight GET above, just principal-scoped instead of URL-scoped.</summary>
    private static async Task<IResult> ListOwnBindingsAsync(ICapabilityRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var items = await repo.ListDepartmentCapabilitiesAsync(tenantId, departmentId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> SetOwnBindingAsync(Guid capabilityId, SetBindingRequest request, ICapabilityRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryScope(ctx, out var tenantId, out var departmentId, out var actorId)) return Results.Forbid();
        try
        {
            var ok = await repo.SetBindingAsync(tenantId, departmentId, capabilityId, request.AgentId, actorId, ct);
            return ok ? Results.NoContent() : Results.BadRequest(new { error = "agent_not_eligible_for_capability" });
        }
        catch (PostgresException ex) when (ex.SqlState == "23514") { return Results.BadRequest(new { error = ex.MessageText }); }
    }

    private static async Task<IResult> RevokeOwnBindingAsync(Guid capabilityId, ICapabilityRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryScope(ctx, out var tenantId, out var departmentId, out _)) return Results.Forbid();
        var revoked = await repo.RevokeBindingAsync(tenantId, departmentId, capabilityId, ct);
        return revoked ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> ListCatalogAsync(ICapabilityRepository repo, CancellationToken ct)
    {
        var items = await repo.ListCatalogAsync(ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> CreatePlatformAsync(CreateDepartmentCapabilityRequest request, ICapabilityRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!AuthenticatedPrincipalContext.TryGet(ctx, out var principal)) return Results.Forbid();
        try { return Results.Created("/system/capabilities", await repo.CreatePlatformCapabilityAsync(principal.UserId, request, ct)); }
        catch (PostgresException ex) when (ex.SqlState == "23505") { return Results.Conflict(new { error = "capability_code_exists" }); }
    }

    private static async Task<IResult> UpdatePlatformAsync(Guid capabilityId, UpdateDepartmentCapabilityRequest request, ICapabilityRepository repo, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        var item = await repo.UpdatePlatformCapabilityAsync(capabilityId, request, ct);
        return item is null ? Results.NotFound() : Results.Ok(item);
    }

    private static Task<IResult> EnablePlatformAsync(Guid capabilityId, ICapabilityRepository repo, CancellationToken ct) => SetPlatformEnabledAsync(capabilityId, true, repo, ct);
    private static Task<IResult> DisablePlatformAsync(Guid capabilityId, ICapabilityRepository repo, CancellationToken ct) => SetPlatformEnabledAsync(capabilityId, false, repo, ct);
    private static async Task<IResult> SetPlatformEnabledAsync(Guid capabilityId, bool enabled, ICapabilityRepository repo, CancellationToken ct)
    {
        var item = await repo.SetPlatformCapabilityEnabledAsync(capabilityId, enabled, ct);
        return item is null ? Results.NotFound() : Results.Ok(item);
    }

    private static async Task<IResult> ListMyCapabilitiesAsync(ICapabilityRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!AuthenticatedPrincipalContext.TryGet(ctx, out var principal) || principal.TenantId is not { } tenantId)
            return Results.BadRequest(new { error = "tenant_context_missing" });
        // D4 §8: only ADMIN (department-derived) and USER (direct-assignment-
        // derived) get a Nghiệp vụ menu. TENANT_OWNER never receives implicit
        // production execution (§7), so it has no /me/capabilities of its
        // own — its capability visibility is the department-binding
        // management view below. SUPER_ADMIN is platform-scoped, not a
        // tenant/department member, so it has none either.
        if (principal.IsInRole("admin") && principal.DepartmentId is { } adminDeptId)
        {
            var items = await repo.ListForDepartmentManagerAsync(tenantId, adminDeptId, ct);
            return Results.Ok(new { items, total = items.Count });
        }
        if (principal.IsInRole("user"))
        {
            var items = await repo.ListForPrincipalAsync(tenantId, principal.UserId, ct);
            return Results.Ok(new { items, total = items.Count });
        }
        return Results.Forbid();
    }

    private static async Task<IResult> ResolveCapabilityAsync(
        string code, Guid? preferredAgentId, ICapabilityRepository repo, HttpContext ctx, IAuditLogger audit, CancellationToken ct)
    {
        if (!AuthenticatedPrincipalContext.TryGet(ctx, out var principal) || principal.TenantId is not { } tenantId || principal.DepartmentId is not { } departmentId)
            return Results.Forbid();
        var isAdmin = principal.IsInRole("admin");
        if (!isAdmin && !principal.IsInRole("user")) return Results.Forbid();

        var result = await repo.ResolveAgentAsync(tenantId, departmentId, isAdmin ? null : principal.UserId, isAdmin, code, preferredAgentId, ct);
        await audit.WriteAsync(new AuditEntry(principal.UserId, "GET capability agent resolution",
            "capability", code,
            new { tenantId, departmentId, preferredAgentId, allowed = result.AgentId is not null, resolvedAgentId = result.AgentId, denialReason = result.DenialReason },
            ctx.Connection.RemoteIpAddress, ctx.Request.Headers.UserAgent.ToString()), ct);

        return result.AgentId is { } agentId
            ? Results.Ok(new { agentId })
            : Results.Json(new { error = result.DenialReason }, statusCode: result.DenialReason == "capability_ambiguous"
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// "Xử lý bằng trợ lý" selector data. USER gets only its own directly-
    /// assigned eligible agents; ADMIN gets every usable agent in its own
    /// department per current ACL V2 — same chains ResolveAgentAsync/
    /// ListForPrincipalAsync/ListForDepartmentManagerAsync already use.
    /// Never an authorization decision by itself — whatever the caller picks
    /// still goes through ResolveAgentAsync's preferredAgentId re-check.
    /// </summary>
    private static async Task<IResult> ListEligibleAgentsAsync(
        string code, ICapabilityRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!AuthenticatedPrincipalContext.TryGet(ctx, out var principal) || principal.TenantId is not { } tenantId || principal.DepartmentId is not { } departmentId)
            return Results.Forbid();
        var isAdmin = principal.IsInRole("admin");
        if (!isAdmin && !principal.IsInRole("user")) return Results.Forbid();

        var items = await repo.ListEligibleAgentsAsync(tenantId, departmentId, isAdmin ? null : principal.UserId, isAdmin, code, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> ListDepartmentCapabilitiesAsync(
        Guid departmentId, ICapabilityRepository repo, IDepartmentRepository departments, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId)) return Results.BadRequest(new { error = "tenant_context_missing" });
        if (await departments.FindAsync(tenantId, departmentId, ct) is null) return Results.NotFound();
        var items = await repo.ListDepartmentCapabilitiesAsync(tenantId, departmentId, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static bool TryValidate(object value, out Dictionary<string, string[]> errors)
    {
        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(value, new ValidationContext(value), results, true);
        errors = results.SelectMany(r => r.MemberNames.DefaultIfEmpty("request").Select(m => (m, r.ErrorMessage)))
            .GroupBy(x => x.m).ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage ?? "invalid").ToArray());
        return valid;
    }
}
