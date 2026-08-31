using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Audit;
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

public sealed record UpdateTenantUserRequest(string? DisplayName);

public sealed record AssignTenantOwnerRequest([Required] Guid UserId);

public sealed record TransferTenantOwnerRequest(
    [Required] Guid SuccessorUserId,
    [Required, RegularExpression("^(demote|deactivate)$")] string PredecessorAction);

public sealed record TenantOwnershipPreflightRow(
    Guid TenantId,
    string TenantCode,
    string TenantName,
    OwnerSummary? SelectedOwner,
    int CurrentOwnerCount,
    string ValidationStatus);

public sealed record OwnerSummary(Guid Id, string Email, bool IsActive);

public static class UserEndpoints
{
    // Deliberately does NOT include "tenant_owner": granting/replacing an
    // owner has real business rules (target must belong to the tenant, must
    // not be super_admin, must be audited, must leave exactly one active
    // owner) that this generic role-toggle endpoint does not enforce. Owner
    // assignment/transfer goes exclusively through the dedicated
    // /system/tenants/{tenantId}/owner endpoints below.
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

        // The sole TENANT_OWNER manages plain ADMIN accounts in their own
        // authenticated tenant. Neither tenant nor role is accepted from the
        // request, and ADMIN is not included in this authorization policy.
        var ownTenantAdmins = app
            .MapGroup("/tenant/admins")
            .RequireAuthorization("tenant_owner")
            .RequireRateLimiting("default");
        ownTenantAdmins.MapGet("/", ListOwnTenantAdminsAsync);
        ownTenantAdmins.MapPost("/", CreateOwnTenantAdminAsync);
        ownTenantAdmins.MapPost("/{id:guid}/disable", DisableOwnTenantAdminAsync);
        ownTenantAdmins.MapPost("/{id:guid}/enable", EnableOwnTenantAdminAsync);

        // Tenant Owner RBAC Stage 2 — SUPER_ADMIN explicitly assigns the sole
        // accountable owner of a tenant. No heuristic selection; audited;
        // the actual tenant->owner mapping is decided here at runtime, never
        // hard-coded into a migration.
        app.MapGet("/system/tenants/owners/preflight", OwnershipPreflightAsync)
            .RequireAuthorization("super_admin")
            .RequireRateLimiting("default");
        app.MapPost("/system/tenants/{tenantId:guid}/owner", AssignTenantOwnerAsync)
            .RequireAuthorization("super_admin")
            .RequireRateLimiting("default");
        app.MapPost("/system/tenants/{tenantId:guid}/owner/transfer", TransferTenantOwnerAsync)
            .RequireAuthorization("super_admin")
            .RequireRateLimiting("default");

        // Tenant ADMIN: the candidate pool for agent-execution assignment,
        // scoped strictly to the caller's own tenant (server-side context —
        // never a client-supplied tenant_id).
        var tenantUsers = app
            .MapGroup("/tenant/users")
            .RequireAuthorization("admin")
            .RequireRateLimiting("default");

        tenantUsers.MapGet("/", ListTenantUsersAsync);
        tenantUsers.MapPost("/", CreateTenantUserAsync);
        tenantUsers.MapPatch("/{id:guid}", UpdateTenantUserAsync);
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

    private static async Task<IResult> ListOwnTenantAdminsAsync(
        IUserRepository users, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId))
            return Results.BadRequest(new { error = "tenant_context_missing" });
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

    private static async Task<IResult> CreateOwnTenantAdminAsync(
        CreateTenantUserRequest request,
        IUserRepository users,
        ITenantRepository tenants,
        IPasswordHasher hasher,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId))
            return Results.BadRequest(new { error = "tenant_context_missing" });
        return await CreateTenantAdminAsync(tenantId, request, users, tenants, hasher, ct);
    }

    private static async Task<IResult> DisableTenantAdminAsync(
        Guid tenantId, Guid id, IUserRepository users, CancellationToken ct) =>
        await SetTenantAdminActiveAsync(tenantId, id, isActive: false, users, ct);

    private static async Task<IResult> EnableTenantAdminAsync(
        Guid tenantId, Guid id, IUserRepository users, CancellationToken ct) =>
        await SetTenantAdminActiveAsync(tenantId, id, isActive: true, users, ct);

    private static async Task<IResult> DisableOwnTenantAdminAsync(
        Guid id, IUserRepository users, HttpContext ctx, CancellationToken ct) =>
        await SetOwnTenantAdminActiveAsync(id, false, users, ctx, ct);

    private static async Task<IResult> EnableOwnTenantAdminAsync(
        Guid id, IUserRepository users, HttpContext ctx, CancellationToken ct) =>
        await SetOwnTenantAdminActiveAsync(id, true, users, ctx, ct);

    private static async Task<IResult> SetOwnTenantAdminActiveAsync(
        Guid id, bool isActive, IUserRepository users, HttpContext ctx, CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId))
            return Results.BadRequest(new { error = "tenant_context_missing" });
        return await SetTenantAdminActiveAsync(tenantId, id, isActive, users, ct);
    }

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

    private static async Task<IResult> OwnershipPreflightAsync(
        IUserRepository users, ITenantRepository tenants, CancellationToken ct)
    {
        var activeTenants = (await tenants.ListAllAsync(ct)).Where(t => t.Status == "active").ToArray();
        var report = new List<TenantOwnershipPreflightRow>(activeTenants.Length);
        foreach (var tenant in activeTenants)
        {
            var owners = await users.ListTenantOwnersAsync(tenant.Id, ct);
            var active = owners.Where(o => o.IsActive).ToArray();
            var selected = active.Length > 0
                ? new OwnerSummary(active[0].Id, active[0].Email, active[0].IsActive)
                : null;
            var status = active.Length switch
            {
                0 => "unowned",
                1 => "ready",
                _ => "conflict", // defensive only — Stage 1 has no per-tenant uniqueness yet
            };
            report.Add(new TenantOwnershipPreflightRow(tenant.Id, tenant.Code, tenant.Name, selected, active.Length, status));
        }
        return Results.Ok(new { items = report, total = report.Count });
    }

    private static async Task<IResult> AssignTenantOwnerAsync(
        Guid tenantId,
        AssignTenantOwnerRequest request,
        IUserRepository users,
        ITenantRepository tenants,
        IAuditLogger audit,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);

        var tenant = await tenants.FindByIdAsync(tenantId, ct);
        if (tenant is null || tenant.Status != "active") return Results.NotFound();

        var existingOwners = await users.ListTenantOwnersAsync(tenantId, ct);
        if (existingOwners.Any(o => o.IsActive))
            return Results.Conflict(new { error = "tenant_already_has_owner" });

        var target = await users.FindByIdAsync(request.UserId, ct);
        // "Target must belong to the target tenant" also structurally rules
        // out selecting a super_admin: super_admin accounts never carry a
        // tenant_id, so they can never satisfy this equality check.
        if (target is null || target.TenantId != tenantId)
            return Results.BadRequest(new { error = "target_user_must_belong_to_tenant" });
        if (!target.IsActive)
            return Results.BadRequest(new { error = "target_user_is_disabled" });

        var targetRoles = await users.GetRolesAsync(request.UserId, ct);
        if (targetRoles.Contains("super_admin"))
            return Results.BadRequest(new { error = "super_admin_cannot_be_selected" });

        try
        {
            await users.AssignRoleAsync(request.UserId, "tenant_owner", ct);
        }
        catch (PostgresException ex) when (ex.SqlState is "23505" or "23514")
        {
            // Defense in depth: the existingOwners check above should already
            // catch this, but a concurrent assignment can still race past it
            // and only get caught by the deferred invariant at commit.
            return Results.Conflict(new { error = ex.MessageText });
        }

        var actorRaw = ctx.User.FindFirst("sub")?.Value;
        Guid.TryParse(actorRaw, out var actorId);
        await audit.WriteAsync(new AuditEntry(
            actorId, "tenant_owner.assign", "tenant", tenantId.ToString(),
            new { userId = request.UserId, email = target.Email },
            ctx.Connection.RemoteIpAddress, ctx.Request.Headers.UserAgent.ToString()), ct);

        var roles = await users.GetRolesAsync(request.UserId, ct);
        return Results.Ok(new { tenantId, userId = request.UserId, email = target.Email, roles });
    }

    /// <summary>
    /// Stage 3 atomic ownership transfer. Unlike AssignTenantOwnerAsync
    /// (Stage 2, single-shot initial assignment onto an unowned tenant),
    /// this requires an existing active owner and moves ownership from them
    /// to the successor in one transaction: lock tenant → verify
    /// SUPER_ADMIN actor → validate successor → promote successor →
    /// demote/deactivate predecessor per explicit request → update
    /// owner_user_id → verify exactly one active owner → commit. All of
    /// this happens inside ITenantRepository.TransferOwnershipAsync; this
    /// handler does the pre-flight HTTP-shape checks, calls it, and writes
    /// the audit entry once the transfer has actually committed.
    /// </summary>
    private static async Task<IResult> TransferTenantOwnerAsync(
        Guid tenantId,
        TransferTenantOwnerRequest request,
        ITenantRepository tenants,
        IAuditLogger audit,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);

        var actorRaw = ctx.User.FindFirst("sub")?.Value;
        if (!Guid.TryParse(actorRaw, out var actorId)) return Results.Unauthorized();

        TenantRecord updatedTenant;
        try
        {
            updatedTenant = await tenants.TransferOwnershipAsync(
                tenantId, actorId, request.SuccessorUserId, request.PredecessorAction, ct);
        }
        catch (TenantOwnershipTransferException ex)
        {
            var status = ex.Reason switch
            {
                "tenant_not_found" => StatusCodes.Status404NotFound,
                "actor_not_super_admin" => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status400BadRequest,
            };
            return Results.Json(new { error = ex.Reason }, statusCode: status);
        }
        catch (PostgresException ex) when (ex.SqlState is "23505" or "23514")
        {
            // Defense in depth: the in-transaction check inside
            // TransferOwnershipAsync should already catch this, but a
            // concurrent transaction can still trip the deferred invariant
            // exactly at commit — translate it the same way the generic
            // role endpoint does, instead of letting it surface as a 500.
            return Results.Conflict(new { error = ex.MessageText });
        }

        await audit.WriteAsync(new AuditEntry(
            actorId, "tenant_owner.transfer", "tenant", tenantId.ToString(),
            new { successorUserId = request.SuccessorUserId, predecessorAction = request.PredecessorAction },
            ctx.Connection.RemoteIpAddress, ctx.Request.Headers.UserAgent.ToString()), ct);

        return Results.Ok(new
        {
            tenantId,
            tenantCode = updatedTenant.Code,
            ownerUserId = request.SuccessorUserId,
            predecessorAction = request.PredecessorAction,
        });
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

    private static async Task<IResult> UpdateTenantUserAsync(
        Guid id,
        UpdateTenantUserRequest request,
        IUserRepository users,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId))
            return Results.BadRequest(new { error = "tenant_context_missing" });
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? null
            : request.DisplayName.Trim();
        if (displayName?.Length > 200)
            return Results.BadRequest(new { error = "display_name_too_long" });
        var updated = await users.UpdateTenantUserAsync(tenantId, id, displayName, ct);
        return updated ? Results.Ok(new { userId = id, displayName }) : Results.NotFound();
    }

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
        if (!targetRoles.Contains("user") || targetRoles.Contains("admin")
            || targetRoles.Contains("tenant_owner") || targetRoles.Contains("super_admin"))
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
