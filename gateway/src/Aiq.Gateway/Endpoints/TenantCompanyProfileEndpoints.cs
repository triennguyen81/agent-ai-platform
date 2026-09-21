using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;

namespace Aiq.Gateway.Endpoints;

/// <summary>
/// AIQ's own legal-entity identity per tenant ("công ty chủ thể" — "Hồ sơ
/// doanh nghiệp" in the UI). Deliberately a separate route/table from the
/// existing narrow GET/PATCH /tenant/company-profile (tenants.company_tax_id,
/// wired into InvoiceDirectionResolver's AR/AP check) — that feature is
/// untouched. Read/write both require the literal "tenant_owner" policy —
/// ADMIN and USER cannot reach either verb, matching the product rule that
/// only TENANT_OWNER may create/update Company Profile. tenantId always
/// comes from AuthenticatedPrincipalContext, never from the request.
/// </summary>
public static class TenantCompanyProfileEndpoints
{
    public static IEndpointRouteBuilder MapTenantCompanyProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/tenant/business-profile")
            .RequireAuthorization("tenant_governance").RequireRateLimiting("default");
        group.MapGet("/", GetAsync);
        group.MapPut("/", UpsertAsync);

        // Read-only, any authenticated tenant member (not tenant_owner-only):
        // this is the AI runtime's own fetch for the generic no-agent-id chat
        // path (agent/src/aiq_agent/agentic/runner.py's run_task) — the
        // agent-scoped path instead gets it bundled into the manifest by
        // DynamicAgentEndpoints.ManifestAsync/DelegateManifestAsync. tenantId
        // still comes only from AuthenticatedPrincipalContext; a super_admin
        // caller has no tenant context at all and simply gets profile=null
        // (not_configured), matching GetTestManifestAsync's existing
        // "no tenant context for super_admin" behavior elsewhere.
        app.MapGet("/runtime/company-profile", GetRuntimeAsync)
            .RequireAuthorization("user").RequireRateLimiting("default");
        return app;
    }

    private static async Task<IResult> GetRuntimeAsync(
        ITenantCompanyProfileRepository repo, HttpContext ctx, CancellationToken ct)
    {
        var profile = AuthenticatedPrincipalContext.TryGet(ctx, out var principal) && principal.TenantId is { } tenantId
            ? await repo.GetAsync(tenantId, ct)
            : null;
        return Results.Ok(new { profile });
    }

    private static async Task<IResult> GetAsync(
        ITenantCompanyProfileRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!AuthenticatedPrincipalContext.TryGet(ctx, out var principal) || principal.TenantId is not { } tenantId)
            return Results.BadRequest(new { error = "tenant_context_missing" });
        var profile = await repo.GetAsync(tenantId, ct);
        return Results.Ok(new { profile });
    }

    public sealed record UpsertRequest(
        string? LegalName,
        [property: StringLength(20)] string? TaxCode,
        string? ShortName,
        string[]? Aliases,
        string? RegisteredAddress,
        string? LegalRepresentative,
        string? RepresentativeTitle,
        string? Phone,
        [property: EmailAddress] string? Email,
        string? Website);

    private static async Task<IResult> UpsertAsync(
        UpsertRequest request, ITenantCompanyProfileRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!AuthenticatedPrincipalContext.TryGet(ctx, out var principal) || principal.TenantId is not { } tenantId)
            return Results.BadRequest(new { error = "tenant_context_missing" });
        var actorRaw = ctx.User.FindFirst("sub")?.Value;
        if (!Guid.TryParse(actorRaw, out var actorId)) return Results.Unauthorized();

        var profile = await repo.UpsertAsync(tenantId, actorId,
            new UpsertCompanyProfileRequest(
                request.LegalName, request.TaxCode, request.ShortName, request.Aliases,
                request.RegisteredAddress, request.LegalRepresentative, request.RepresentativeTitle,
                request.Phone, request.Email, request.Website), ct);
        return Results.Ok(new { profile });
    }

    private static bool TryValidate(object instance, out Dictionary<string, string[]> errors)
    {
        var ctx = new ValidationContext(instance);
        var results = new List<ValidationResult>();
        var ok = Validator.TryValidateObject(instance, ctx, results, validateAllProperties: true);
        errors = results
            .SelectMany(r => r.MemberNames.DefaultIfEmpty(""), (r, member) => (member, r.ErrorMessage ?? "invalid"))
            .GroupBy(x => x.member)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Item2).ToArray());
        return ok;
    }
}
