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

        // Any authenticated user — list own memberships + switch active tenant
        var me = app
            .MapGroup("/me/tenants")
            .RequireAuthorization()
            .RequireRateLimiting("default");

        me.MapGet("/", ListMyMembershipsAsync);
        me.MapPost("/switch", SwitchActiveTenantAsync);

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

    private static async Task<IResult> ListMyMembershipsAsync(
        ITenantRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var uid)) return Results.Unauthorized();
        var memberships = await repo.ListMembershipsForUserAsync(uid, ct);
        return Results.Ok(new { items = memberships, total = memberships.Count });
    }

    /// <summary>
    /// Switches the user's active tenant by re-issuing a JWT with the new
    /// tenant_id claim. The client must replace its stored token.
    /// </summary>
    private static async Task<IResult> SwitchActiveTenantAsync(
        SwitchTenantRequest request,
        ITenantRepository tenants,
        Aiq.Gateway.Data.IUserRepository users,
        Aiq.Gateway.Auth.IJwtTokenService jwt,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TryGetUserId(ctx, out var uid)) return Results.Unauthorized();

        var role = await tenants.GetMemberRoleAsync(request.TenantId, uid, ct);
        if (role is null) return Results.Forbid();

        var user = await users.FindByIdAsync(uid, ct);
        if (user is null) return Results.Unauthorized();

        var userRoles = await users.GetRolesAsync(uid, ct);
        var token = jwt.Issue(user, userRoles, request.TenantId);
        return Results.Ok(new { token = token.Token, expiresAt = token.ExpiresAt, tenantId = request.TenantId });
    }

    public sealed record SwitchTenantRequest([Required] Guid TenantId);

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
