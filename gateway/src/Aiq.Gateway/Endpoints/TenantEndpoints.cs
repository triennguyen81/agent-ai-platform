using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Audit;
using Aiq.Gateway.Auth;
using Aiq.Gateway.Governance;

namespace Aiq.Gateway.Endpoints;

public static class TenantEndpoints
{
    public sealed record OnboardTenantRequest(
        [property: Required, RegularExpression("^[a-z0-9-]+$"), StringLength(60, MinimumLength = 2)] string Code,
        [property: Required, StringLength(120)] string Name,
        [property: Required, EmailAddress] string OwnerEmail,
        [property: Required, MinLength(8)] string OwnerPassword,
        [property: Required, StringLength(200)] string OwnerDisplayName);

    public static IEndpointRouteBuilder MapTenantEndpoints(this IEndpointRouteBuilder app)
    {
        // super_admin only — tenant CRUD
        var superAdmin = app
            .MapGroup("/tenants")
            .RequireAuthorization("platform_admin")
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
            .RequireAuthorization("platform_admin").RequireRateLimiting("default");
        app.MapGet("/system/tenants/{id:guid}/summary", GetSummaryAsync)
            .RequireAuthorization("platform_admin").RequireRateLimiting("default");
        app.MapPost("/system/tenants/onboard", OnboardAsync)
            .RequireAuthorization("platform_admin").RequireRateLimiting("default");

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

        // The authoritative "who are we" tax ID InvoiceDirectionResolver
        // uses to classify Sales vs Purchase — tenant_governance-scoped
        // (TENANT_OWNER only) since every future invoice's AR/AP
        // classification depends on this being set correctly, not something
        // a department admin should be able to change in passing.
        app.MapGet("/tenant/company-profile", GetCompanyProfileAsync)
            .RequireAuthorization("tenant_governance")
            .RequireRateLimiting("default");
        app.MapPatch("/tenant/company-profile", UpdateCompanyProfileAsync)
            .RequireAuthorization("tenant_governance")
            .RequireRateLimiting("default");

        return app;
    }

    public sealed record UpdateCompanyProfileRequest(string? CompanyTaxId);

    private static async Task<IResult> GetCompanyProfileAsync(
        ITenantRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!Aiq.Gateway.Middleware.TenantContext.TryGet(ctx, out var tenantId))
            return Results.BadRequest(new { error = "tenant_context_missing" });
        var companyTaxId = await repo.GetCompanyTaxIdAsync(tenantId, ct);
        return Results.Ok(new { companyTaxId });
    }

    private static async Task<IResult> UpdateCompanyProfileAsync(
        UpdateCompanyProfileRequest request, ITenantRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!Aiq.Gateway.Middleware.TenantContext.TryGet(ctx, out var tenantId))
            return Results.BadRequest(new { error = "tenant_context_missing" });
        var normalized = string.IsNullOrWhiteSpace(request.CompanyTaxId) ? null : request.CompanyTaxId.Trim();
        if (normalized is not null && !System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^\d{10}(-\d{3})?$"))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["companyTaxId"] = ["Mã số thuế không đúng định dạng (10 số, có thể kèm -NNN chi nhánh)"] });
        await repo.SetCompanyTaxIdAsync(tenantId, normalized, ct);
        return Results.Ok(new { companyTaxId = normalized });
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
