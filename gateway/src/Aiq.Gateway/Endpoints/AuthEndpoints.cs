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

        return app;
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
        await users.AssignRoleAsync(user.Id, "viewer", ct);

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
