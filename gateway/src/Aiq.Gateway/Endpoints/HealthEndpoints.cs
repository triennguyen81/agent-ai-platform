using Aiq.Gateway.Data;

namespace Aiq.Gateway.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz/live", () => Results.Ok(new { status = "live" }))
            .AllowAnonymous();

        app.MapGet("/healthz/ready", async (
            NpgsqlConnectionFactory factory,
            CancellationToken ct) =>
        {
            try
            {
                await using var conn = await factory.OpenAsync(ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1;";
                await cmd.ExecuteScalarAsync(ct);
                return Results.Ok(new { status = "ready", postgres = "ok" });
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new { status = "degraded", postgres = "error", message = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).AllowAnonymous();

        return app;
    }
}
