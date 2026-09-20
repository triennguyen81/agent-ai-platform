using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Audit;
using Aiq.Gateway.Auth;
using Aiq.Gateway.Data;
using Aiq.Gateway.Middleware;

namespace Aiq.Gateway.Endpoints;

public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/me/change-password", ChangePasswordAsync)
            .RequireAuthorization("authenticated")
            .RequireRateLimiting("auth");

        return app;
    }

    /// <summary>
    /// Self-service password change. Reuses the same IPasswordHasher as
    /// login/register — never a different hashing path. Requires the
    /// caller's CURRENT password (the one property that makes this
    /// meaningfully different from an admin-initiated reset, and the
    /// reason it must never be reachable via the reset-password routes
    /// with the caller as their own target — see UserEndpoints.cs).
    /// </summary>
    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        IUserRepository users,
        IPasswordHasher hasher,
        IAuditLogger audit,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!AuthenticatedPrincipalContext.TryGet(ctx, out var principal)) return Results.Unauthorized();

        if (request.NewPassword != request.ConfirmPassword)
            return Results.BadRequest(new { error = "password_confirmation_mismatch" });

        var user = await users.FindByIdAsync(principal.UserId, ct);
        if (user is null) return Results.Unauthorized();

        // Never log/echo either password — only the verification result.
        if (!hasher.Verify(request.CurrentPassword, user.PasswordHash))
            return Results.BadRequest(new { error = "current_password_incorrect" });

        if (request.NewPassword == request.CurrentPassword)
            return Results.BadRequest(new { error = "new_password_must_differ" });

        var hash = hasher.Hash(request.NewPassword);
        await users.SetPasswordHashAsync(user.Id, hash, mustChangePassword: false, ct);

        // Payload is deliberately null — there is nothing safe to record
        // about a password change beyond who did it and when.
        await audit.WriteAsync(new AuditEntry(
            principal.UserId, "PASSWORD_CHANGED", "user", principal.UserId.ToString(),
            null, ctx.Connection.RemoteIpAddress, ctx.Request.Headers.UserAgent.ToString()), ct);

        // No session/token store exists in this architecture (JWTs are
        // stateless, signature-only — see Auth/IJwtTokenService.cs), so any
        // other access token issued to this user before the change remains
        // valid until it naturally expires. Not addressed here per "do not
        // redesign auth" — a real fix would need a server-side revocation
        // list, which is a materially bigger change than this feature.
        return Results.Ok(new { ok = true });
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
