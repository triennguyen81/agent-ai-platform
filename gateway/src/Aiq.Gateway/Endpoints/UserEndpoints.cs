using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Auth;
using Aiq.Gateway.Data;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;
using Npgsql;

namespace Aiq.Gateway.Endpoints;

public sealed record AssignRoleRequest(string Role);

public sealed record CreateTenantUserRequest(
    [Required, EmailAddress] string Email,
    [Required, MinLength(8)] string Password,
    string? DisplayName);

public static class UserEndpoints
{
    private static readonly HashSet<string> AllowedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "super_admin", "admin", "user",
    };

    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        // Only super_admin can list users or change roles.
        var group = app
            .MapGroup("/users")
            .RequireAuthorization("super_admin")
            .RequireRateLimiting("default");

        group.MapGet("/", ListAsync);
        group.MapPost("/{id:guid}/roles", AssignRoleAsync);
        group.MapDelete("/{id:guid}/roles/{role}", RemoveRoleAsync);

        // SUPER_ADMIN: create/manage the ADMIN(s) of a specific tenant, from
        // the tenant detail page. tenantId comes only from the URL path (a
        // SUPER_ADMIN action, not the target's own session) — the created
        // account is always forced to role=admin and that exact tenant, so
        // an ADMIN can never exist without a tenant.
        var tenantAdmins = app
            .MapGroup("/system/tenants/{tenantId:guid}/admins")
            .RequireAuthorization("super_admin")
            .RequireRateLimiting("default");

        tenantAdmins.MapGet("/", ListTenantAdminsAsync);
        tenantAdmins.MapPost("/", CreateTenantAdminAsync);
        tenantAdmins.MapPost("/{id:guid}/disable", DisableTenantAdminAsync);
        tenantAdmins.MapPost("/{id:guid}/enable", EnableTenantAdminAsync);

        // Tenant ADMIN: the candidate pool for agent-execution assignment,
        // scoped strictly to the caller's own tenant (server-side context —
        // never a client-supplied tenant_id).
        var tenantUsers = app
            .MapGroup("/tenant/users")
            .RequireAuthorization("admin")
            .RequireRateLimiting("default");

        tenantUsers.MapGet("/", ListTenantUsersAsync);
        tenantUsers.MapPost("/", CreateTenantUserAsync);
        tenantUsers.MapPost("/{id:guid}/disable", DisableTenantUserAsync);
        tenantUsers.MapPost("/{id:guid}/enable", EnableTenantUserAsync);

        return app;
    }

    private static async Task<IResult> ListAsync(
        IUserRepository repo,
        CancellationToken ct)
    {
        var rows = await repo.ListDirectoryAsync(ct);
        return Results.Ok(new { items = rows, total = rows.Count });
    }

    private static async Task<IResult> ListTenantUsersAsync(
        IUserRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId))
            return Results.BadRequest(new { error = "tenant_context_missing" });
        var rows = await repo.ListByTenantAsync(tenantId, ct);
        return Results.Ok(new { items = rows, total = rows.Count });
    }

    private static async Task<IResult> ListTenantAdminsAsync(
        Guid tenantId, IUserRepository users, ITenantRepository tenants, CancellationToken ct)
    {
        if (await tenants.FindByIdAsync(tenantId, ct) is null) return Results.NotFound();
        var rows = await users.ListAdminsByTenantAsync(tenantId, ct);
        return Results.Ok(new { items = rows, total = rows.Count });
    }

    private static async Task<IResult> CreateTenantAdminAsync(
        Guid tenantId,
        CreateTenantUserRequest request,
        IUserRepository users,
        ITenantRepository tenants,
        IPasswordHasher hasher,
        CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        var tenant = await tenants.FindByIdAsync(tenantId, ct);
        if (tenant is null || tenant.Status != "active") return Results.NotFound();

        var existing = await users.FindByEmailAsync(request.Email, ct);
        if (existing is not null) return Results.Conflict(new { error = "email_already_registered" });

        var quota = await tenants.GetQuotaStatusAsync(tenantId, ct);
        if (quota is not null && !TenantQuotaPolicy.HasRoomFor(quota.MaxUsers, quota.CurrentUsers))
            return Results.Conflict(new { error = "user_quota_exceeded" });

        var hash = hasher.Hash(request.Password);
        var user = await users.CreateAsync(request.Email, hash, request.DisplayName, ct);
        // Tenant must be set before the role grant — the role-assignment
        // trigger requires admin/user accounts to already have a tenant_id,
        // which is exactly what keeps an ADMIN from ever existing tenant-less.
        await users.SetTenantAsync(user.Id, tenantId, ct);
        await users.AssignRoleAsync(user.Id, "admin", ct);
        await tenants.UpsertMembershipAsync(tenantId, user.Id, "tenant_admin", ct);

        var roles = await users.GetRolesAsync(user.Id, ct);
        return Results.Created($"/system/tenants/{tenantId}/admins/{user.Id}", new
        {
            id = user.Id,
            email = user.Email,
            displayName = request.DisplayName,
            isActive = true,
            roles,
        });
    }

    private static async Task<IResult> DisableTenantAdminAsync(
        Guid tenantId, Guid id, IUserRepository users, CancellationToken ct) =>
        await SetTenantAdminActiveAsync(tenantId, id, isActive: false, users, ct);

    private static async Task<IResult> EnableTenantAdminAsync(
        Guid tenantId, Guid id, IUserRepository users, CancellationToken ct) =>
        await SetTenantAdminActiveAsync(tenantId, id, isActive: true, users, ct);

    private static async Task<IResult> SetTenantAdminActiveAsync(
        Guid tenantId, Guid id, bool isActive, IUserRepository users, CancellationToken ct)
    {
        var target = await users.FindByIdAsync(id, ct);
        if (target is null || target.TenantId != tenantId) return Results.NotFound();

        var targetRoles = await users.GetRolesAsync(id, ct);
        // Scoped strictly to plain admins of this tenant — never touches a
        // super_admin account even if IDs were guessed/mismatched.
        if (!targetRoles.Contains("admin") || targetRoles.Contains("super_admin"))
            return Results.NotFound();

        var ok = await users.SetActiveAsync(id, isActive, ct);
        return ok ? Results.Ok(new { userId = id, isActive }) : Results.NotFound();
    }

    private static async Task<IResult> CreateTenantUserAsync(
        CreateTenantUserRequest request,
        IUserRepository users,
        ITenantRepository tenants,
        IPasswordHasher hasher,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!TenantContext.TryGet(ctx, out var tenantId))
            return Results.BadRequest(new { error = "tenant_context_missing" });

        var existing = await users.FindByEmailAsync(request.Email, ct);
        if (existing is not null) return Results.Conflict(new { error = "email_already_registered" });

        var quota = await tenants.GetQuotaStatusAsync(tenantId, ct);
        if (quota is not null && !TenantQuotaPolicy.HasRoomFor(quota.MaxUsers, quota.CurrentUsers))
            return Results.Conflict(new { error = "user_quota_exceeded" });

        var hash = hasher.Hash(request.Password);
        var user = await users.CreateAsync(request.Email, hash, request.DisplayName, ct);
        await users.SetTenantAsync(user.Id, tenantId, ct);
        // Tenant ADMIN can only ever provision the "user" login role — never
        // admin/super_admin — regardless of anything in the request body.
        await users.AssignRoleAsync(user.Id, "user", ct);
        await tenants.UpsertMembershipAsync(tenantId, user.Id, "member", ct);

        var roles = await users.GetRolesAsync(user.Id, ct);
        return Results.Created($"/tenant/users/{user.Id}", new
        {
            id = user.Id,
            email = user.Email,
            displayName = request.DisplayName,
            isActive = true,
            roles,
        });
    }

    private static async Task<IResult> DisableTenantUserAsync(
        Guid id, IUserRepository users, HttpContext ctx, CancellationToken ct) =>
        await SetTenantUserActiveAsync(id, isActive: false, users, ctx, ct);

    private static async Task<IResult> EnableTenantUserAsync(
        Guid id, IUserRepository users, HttpContext ctx, CancellationToken ct) =>
        await SetTenantUserActiveAsync(id, isActive: true, users, ctx, ct);

    private static async Task<IResult> SetTenantUserActiveAsync(
        Guid id, bool isActive, IUserRepository users, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId))
            return Results.BadRequest(new { error = "tenant_context_missing" });

        var target = await users.FindByIdAsync(id, ct);
        // 404 (not 403) for cross-tenant/non-existent targets alike, so this
        // endpoint never confirms whether a given id exists in another tenant.
        if (target is null || target.TenantId != tenantId) return Results.NotFound();

        var targetRoles = await users.GetRolesAsync(id, ct);
        if (targetRoles.Contains("admin") || targetRoles.Contains("super_admin"))
            return Results.NotFound();

        var ok = await users.SetActiveAsync(id, isActive, ct);
        return ok ? Results.Ok(new { userId = id, isActive }) : Results.NotFound();
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

    private static async Task<IResult> AssignRoleAsync(
        Guid id,
        AssignRoleRequest request,
        IUserRepository repo,
        CancellationToken ct)
    {
        var role = (request.Role ?? string.Empty).Trim().ToLowerInvariant();
        if (!AllowedRoles.Contains(role))
        {
            return Results.BadRequest(new { error = $"Unknown role: {role}" });
        }

        try
        {
            await repo.AssignRoleAsync(id, role, ct);
        }
        catch (PostgresException ex) when (ex.SqlState is "23505" or "23514")
        {
            return Results.Conflict(new { error = ex.MessageText });
        }
        var roles = await repo.GetRolesAsync(id, ct);
        return Results.Ok(new { userId = id, roles });
    }

    private static async Task<IResult> RemoveRoleAsync(
        Guid id,
        string role,
        IUserRepository repo,
        CancellationToken ct)
    {
        var normalized = (role ?? string.Empty).Trim().ToLowerInvariant();
        if (!AllowedRoles.Contains(normalized))
        {
            return Results.BadRequest(new { error = $"Unknown role: {normalized}" });
        }

        try
        {
            await repo.RemoveRoleAsync(id, normalized, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "23514")
        {
            return Results.Conflict(new { error = ex.MessageText });
        }
        var roles = await repo.GetRolesAsync(id, ct);
        return Results.Ok(new { userId = id, roles });
    }
}
