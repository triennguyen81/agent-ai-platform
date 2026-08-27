using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Governance;

namespace Aiq.Gateway.Endpoints;

public static class TenantEndpoints
{
    public static IEndpointRouteBuilder MapTenantEndpoints(this IEndpointRouteBuilder app)
    {
        // super_admin only — tenant CRUD
        var superAdmin = app
            .MapGroup("/tenants")
            .RequireAuthorization("super_admin")
            .RequireRateLimiting("default");

        superAdmin.MapGet("/", ListAsync);
        superAdmin.MapPost("/", CreateAsync);
        superAdmin.MapGet("/{id:guid}", GetAsync);
        superAdmin.MapGet("/{id:guid}/members", ListMembersAsync);
        superAdmin.MapPost("/{id:guid}/members", InviteMemberAsync);
        superAdmin.MapDelete("/{id:guid}/members/{userId:guid}", RemoveMemberAsync);
        superAdmin.MapGet("/{id:guid}/quota", GetQuotaAsync);
        superAdmin.MapPatch("/{id:guid}/quota", UpdateQuotaAsync);

        // Any authenticated user may inspect their single canonical membership.
        var me = app
            .MapGroup("/me/tenants")
            .RequireAuthorization()
            .RequireRateLimiting("default");

        me.MapGet("/", ListMyMembershipsAsync);
        // ADMIN and USER are bound to users.tenant_id. Tenant switching is
        // intentionally no longer exposed as an ordinary authenticated flow.

        // Tenant ADMIN: quota/status for their OWN tenant only. tenantId
        // comes from server-side auth context — there is no id in this URL
        // for a client to substitute.
        app.MapGet("/tenant/quota", GetMyQuotaAsync)
            .RequireAuthorization("admin")
            .RequireRateLimiting("default");

        return app;
    }

    private static async Task<IResult> ListAsync(ITenantRepository repo, CancellationToken ct)
    {
        var tenants = await repo.ListAllAsync(ct);
        return Results.Ok(new { items = tenants, total = tenants.Count });
    }

    private static async Task<IResult> GetAsync(Guid id, ITenantRepository repo, CancellationToken ct)
    {
        var t = await repo.FindByIdAsync(id, ct);
        return t is null ? Results.NotFound() : Results.Ok(t);
    }

    private static async Task<IResult> CreateAsync(
        CreateTenantRequest request, ITenantRepository repo, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        try
        {
            var t = await repo.CreateAsync(request, ct);
            return Results.Created($"/tenants/{t.Id}", t);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Conflict(new { error = "Tenant code đã tồn tại" });
        }
    }

    private static async Task<IResult> ListMembersAsync(Guid id, ITenantRepository repo, CancellationToken ct)
    {
        var members = await repo.ListMembersAsync(id, ct);
        return Results.Ok(new { items = members, total = members.Count });
    }

    private static async Task<IResult> InviteMemberAsync(
        Guid id, InviteMemberRequest request, ITenantRepository repo, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        var ok = await repo.UpsertMembershipAsync(id, request.UserId, request.Role, ct);
        return ok ? Results.NoContent() : Results.BadRequest();
    }

    private static async Task<IResult> RemoveMemberAsync(
        Guid id, Guid userId, ITenantRepository repo, CancellationToken ct)
    {
        var ok = await repo.RemoveMembershipAsync(id, userId, ct);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> GetQuotaAsync(Guid id, ITenantRepository repo, CancellationToken ct)
    {
        var status = await repo.GetQuotaStatusAsync(id, ct);
        return status is null ? Results.NotFound() : Results.Ok(status);
    }

    private static async Task<IResult> UpdateQuotaAsync(
        Guid id, UpdateTenantQuotaRequest request, ITenantRepository repo, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        var updated = await repo.UpdateQuotaAsync(id, request, ct);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    private static async Task<IResult> GetMyQuotaAsync(
        ITenantRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!Aiq.Gateway.Middleware.TenantContext.TryGet(ctx, out var tenantId))
            return Results.BadRequest(new { error = "tenant_context_missing" });
        var status = await repo.GetQuotaStatusAsync(tenantId, ct);
        return status is null ? Results.NotFound() : Results.Ok(status);
    }

    private static async Task<IResult> ListMyMembershipsAsync(
        ITenantRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var uid)) return Results.Unauthorized();
        var memberships = await repo.ListMembershipsForUserAsync(uid, ct);
        return Results.Ok(new { items = memberships, total = memberships.Count });
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
