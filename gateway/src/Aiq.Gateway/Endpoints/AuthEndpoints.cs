using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Auth;
using Aiq.Gateway.Data;

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
            // 3-tier model default: new external users start as plain `user`
            // (can chat). `admin` / `super_admin` granted only via AdditionalRoles
            // (bootstrap path) or later by a super_admin through /users.
            await users.AssignRoleAsync(user.Id, "user", ct);
        }

        // Caller (web tier, authenticated via shared secret) may request extra
        // roles for the user — used for bootstrapping INITIAL_ADMIN_EMAIL as
        // super_admin so the founder account regains full access after roles
        // refactor.
        if (request.AdditionalRoles is { Length: > 0 })
        {
            var allowed = new HashSet<string>(
                new[] { "super_admin", "admin", "user", "reviewer", "uploader", "viewer" },
                StringComparer.OrdinalIgnoreCase);
            foreach (var role in request.AdditionalRoles)
            {
                if (allowed.Contains(role))
                {
                    await users.AssignRoleAsync(user.Id, role.ToLowerInvariant(), ct);
                }
            }
        }

        var roles = await users.GetRolesAsync(user.Id, ct);
        var token = jwt.Issue(user, roles);

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
        await users.AssignRoleAsync(user.Id, "user", ct);

        var roles = await users.GetRolesAsync(user.Id, ct);
        var token = jwt.Issue(user, roles);

        return Results.Created($"/users/{user.Id}", new AuthResponse(
            token.Token, token.ExpiresAt, user.Id, user.Email, roles));
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        IUserRepository users,
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
        var token = jwt.Issue(user, roles);

        return Results.Ok(new AuthResponse(
            token.Token, token.ExpiresAt, user.Id, user.Email, roles));
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
