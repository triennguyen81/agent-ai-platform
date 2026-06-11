using Aiq.Gateway.Data;

namespace Aiq.Gateway.Endpoints;

public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app
            .MapGroup("/users")
            .RequireAuthorization("admin")
            .RequireRateLimiting("default");

        group.MapGet("/", ListAsync);

        return app;
    }

    private static async Task<IResult> ListAsync(
        IUserRepository repo,
        CancellationToken ct)
    {
        var rows = await repo.ListWithRolesAsync(ct);
        return Results.Ok(new { items = rows, total = rows.Count });
    }
}
