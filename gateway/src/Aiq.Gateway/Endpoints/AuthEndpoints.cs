using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Auth;
using Aiq.Gateway.Data;
using Aiq.Gateway.Governance;

namespace Aiq.Gateway.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").RequireRateLimiting("auth");

        group.MapPost("/register", RegisterAsync);
        group.MapPost("/login",    LoginAsync);
        group.MapPost("/external-link", ExternalLinkAsync);

        return app;
    }

    /// <summary>
    /// Exchange a verified external-provider identity (e.g. Clerk) for a
    /// Gateway JWT. Auth is shared-secret in the X-Gateway-Secret header —
    /// only the web tier knows it, so the browser can never call this
    /// endpoint directly. If the user does not exist we provision one with
    /// the provider's email + a random unusable password hash.
    /// </summary>
    private static async Task<IResult> ExternalLinkAsync(
        ExternalLinkRequest request,
        IUserRepository users,
        ITenantRepository tenants,
        IJwtTokenService jwt,
        Microsoft.Extensions.Configuration.IConfiguration config,
        HttpContext ctx,
        CancellationToken ct)
    {
        var expected = config["ExternalAuth:SharedSecret"];
        if (string.IsNullOrWhiteSpace(expected))
        {
            return Results.Problem(
                title: "External auth not configured",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var supplied = ctx.Request.Headers["X-Gateway-Secret"].ToString();
        if (!CryptographicEquals(expected, supplied))
        {
            return Results.Unauthorized();
        }

        if (!TryValidate(request, out var errors))
        {
            return Results.ValidationProblem(errors);
        }

        var provider = request.Provider.ToLowerInvariant();
        var existing = await users.FindByExternalAsync(provider, request.ExternalId, ct);
        var user = existing
            ?? await users.FindByEmailAsync(request.Email, ct)
            ?? await users.CreateExternalAsync(
                request.Email,
                request.DisplayName,
                provider,
                request.ExternalId,
                ct);

        if (existing is null)
        {
            var requestedRole = request.AdditionalRoles?
                .Select(r => r.ToLowerInvariant())
                .FirstOrDefault(LoginRoleRules.IsAllowed) ?? "user";
            var tenantId = requestedRole == "super_admin"
                ? null
                : await ResolveDefaultTenantAsync(tenants, user.Id, ct)
                    ?? (await tenants.FindByCodeAsync("default", ct))?.Id;
            await users.SetTenantAsync(user.Id, tenantId, ct);
            if (tenantId is { } tid)
            {
                await tenants.UpsertMembershipAsync(
                    tid, user.Id, requestedRole == "admin" ? "tenant_admin" : "member", ct);
            }
            await users.AssignRoleAsync(user.Id, requestedRole, ct);
            user = (await users.FindByIdAsync(user.Id, ct))!;
        }

        var roles = await users.GetRolesAsync(user.Id, ct);
        var defaultTenantId = await ResolveDefaultTenantAsync(tenants, user.Id, ct);
        var token = jwt.Issue(user, roles, defaultTenantId);

        return Results.Ok(new AuthResponse(
            token.Token, token.ExpiresAt, user.Id, user.Email, roles));
    }

    private static bool CryptographicEquals(string a, string b)
    {
        var aBytes = System.Text.Encoding.UTF8.GetBytes(a);
        var bBytes = System.Text.Encoding.UTF8.GetBytes(b);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        IUserRepository users,
        ITenantRepository tenants,
        IPasswordHasher hasher,
        IJwtTokenService jwt,
        CancellationToken ct)
    {
        if (!TryValidate(request, out var errors))
        {
            return Results.ValidationProblem(errors);
        }

        var existing = await users.FindByEmailAsync(request.Email, ct);
        if (existing is not null)
        {
            return Results.Conflict(new { error = "Email already registered." });
        }

        var hash = hasher.Hash(request.Password);
        var user = await users.CreateAsync(request.Email, hash, request.DisplayName, ct);

        // Auto-add to default tenant as member so first login has a tenant context
        var defaultTenant = await tenants.FindByCodeAsync("default", ct);
        if (defaultTenant is not null)
        {
            await users.SetTenantAsync(user.Id, defaultTenant.Id, ct);
            await tenants.UpsertMembershipAsync(defaultTenant.Id, user.Id, "member", ct);
        }
        else
        {
            return Results.Problem("Default tenant is not configured.", statusCode: 503);
        }
        await users.AssignRoleAsync(user.Id, "user", ct);
        user = (await users.FindByIdAsync(user.Id, ct))!;

        var roles = await users.GetRolesAsync(user.Id, ct);
        var token = jwt.Issue(user, roles, user.TenantId);

        return Results.Created($"/users/{user.Id}", new AuthResponse(
            token.Token, token.ExpiresAt, user.Id, user.Email, roles));
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        IUserRepository users,
        ITenantRepository tenants,
        IPasswordHasher hasher,
        IJwtTokenService jwt,
        CancellationToken ct)
    {
        if (!TryValidate(request, out var errors))
        {
            return Results.ValidationProblem(errors);
        }

        var user = await users.FindByEmailAsync(request.Email, ct);
        if (user is null || !user.IsActive || !hasher.Verify(request.Password, user.PasswordHash))
        {
            return Results.Unauthorized();
        }

        var roles = await users.GetRolesAsync(user.Id, ct);
        var token = jwt.Issue(user, roles, user.TenantId);

        return Results.Ok(new AuthResponse(
            token.Token, token.ExpiresAt, user.Id, user.Email, roles));
    }

    /// <summary>
    /// Pick the user's first-joined tenant as the default active tenant for
    /// the JWT claim. Returns null if the user has no memberships yet (will be
    /// backfilled by the migration but new users without an invite will lack one).
    /// </summary>
    private static async Task<Guid?> ResolveDefaultTenantAsync(
        ITenantRepository tenants, Guid userId, CancellationToken ct)
    {
        var memberships = await tenants.ListMembershipsForUserAsync(userId, ct);
        return memberships.Count > 0 ? memberships[0].TenantId : (Guid?)null;
    }

    private static bool TryValidate(
        object instance,
        out Dictionary<string, string[]> errors)
    {
        var ctx = new ValidationContext(instance);
        var results = new List<ValidationResult>();
        var ok = Validator.TryValidateObject(instance, ctx, results, validateAllProperties: true);

        errors = results
            .SelectMany(r => r.MemberNames.Select(m => (Member: m, r.ErrorMessage)))
            .GroupBy(t => t.Member)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.ErrorMessage ?? "Invalid").ToArray());

        return ok;
    }
}
