using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Audit;
using Aiq.Gateway.Auth;
using Aiq.Gateway.Governance;

namespace Aiq.Gateway.Endpoints;

public static class TenantEndpoints
{
    public sealed record OnboardTenantRequest(
        [Required, RegularExpression("^[a-z0-9-]+$"), StringLength(60, MinimumLength = 2)] string Code,
        [Required, StringLength(120)] string Name,
        [Required, EmailAddress] string OwnerEmail,
        [Required, MinLength(8)] string OwnerPassword,
        [Required, StringLength(200)] string OwnerDisplayName);

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

        app.MapGet("/system/tenants/summary", ListSummariesAsync)
            .RequireAuthorization("super_admin").RequireRateLimiting("default");
        app.MapGet("/system/tenants/{id:guid}/summary", GetSummaryAsync)
            .RequireAuthorization("super_admin").RequireRateLimiting("default");
        app.MapPost("/system/tenants/onboard", OnboardAsync)
            .RequireAuthorization("super_admin").RequireRateLimiting("default");

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

    private static async Task<IResult> ListSummariesAsync(ITenantRepository repo, CancellationToken ct)
    {
        var items = await repo.ListSummariesAsync(ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> GetSummaryAsync(Guid id, ITenantRepository repo, CancellationToken ct)
    {
        var summary = await repo.GetSummaryAsync(id, ct);
        return summary is null ? Results.NotFound() : Results.Ok(summary);
    }

    private static async Task<IResult> OnboardAsync(
        OnboardTenantRequest request,
        ITenantRepository repo,
        IPasswordHasher hasher,
        IAuditLogger audit,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        try
        {
            var created = await repo.OnboardAsync(
                new CreateTenantRequest(request.Code.Trim().ToLowerInvariant(), request.Name.Trim()),
                request.OwnerEmail.Trim().ToLowerInvariant(),
                hasher.Hash(request.OwnerPassword),
                request.OwnerDisplayName.Trim(),
                ct);
            Guid.TryParse(ctx.User.FindFirst("sub")?.Value, out var actorId);
            await audit.WriteAsync(new AuditEntry(
                actorId, "tenant.onboard", "tenant", created.Tenant.Id.ToString(),
                new { ownerUserId = created.OwnerUserId, ownerEmail = request.OwnerEmail.Trim().ToLowerInvariant() },
                ctx.Connection.RemoteIpAddress, ctx.Request.Headers.UserAgent.ToString()), ct);
            return Results.Created($"/tenants/{created.Tenant.Id}", new
            {
                tenant = created.Tenant,
                ownerUserId = created.OwnerUserId,
            });
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Conflict(new { error = "tenant_code_or_owner_email_already_exists" });
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23514")
        {
            return Results.Conflict(new { error = ex.MessageText });
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
