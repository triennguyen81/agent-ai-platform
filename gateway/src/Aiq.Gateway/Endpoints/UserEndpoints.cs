using Aiq.Gateway.Data;

namespace Aiq.Gateway.Endpoints;

public sealed record AssignRoleRequest(string Role);

public static class UserEndpoints
{
    private static readonly HashSet<string> AllowedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        // Active role tiers (UI-surfaced):
        "super_admin", "admin", "user",
        // Legacy roles still accepted for backward compatibility:
        "reviewer", "uploader", "viewer",
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

        return app;
    }

    private static async Task<IResult> ListAsync(
        IUserRepository repo,
        CancellationToken ct)
    {
        var rows = await repo.ListWithRolesAsync(ct);
        return Results.Ok(new { items = rows, total = rows.Count });
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

        await repo.AssignRoleAsync(id, role, ct);
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

        await repo.RemoveRoleAsync(id, normalized, ct);
        var roles = await repo.GetRolesAsync(id, ct);
        return Results.Ok(new { userId = id, roles });
    }
}
