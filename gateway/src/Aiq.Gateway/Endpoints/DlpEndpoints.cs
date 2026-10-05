using Aiq.Gateway.Dlp;
using Aiq.Gateway.Middleware;

namespace Aiq.Gateway.Endpoints;

/// <summary>
/// Sprint 7 Phase B — endpoint summary cho admin dashboard DLP.
/// Policy "tenant_governance" (chỉ tenant_owner + super_admin) vì data
/// về PII flow có thể nhạy cảm.
/// </summary>
public static class DlpEndpoints
{
    public static IEndpointRouteBuilder MapDlpEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/dlp/summary", GetSummaryAsync)
            .RequireAuthorization("tenant_governance")
            .RequireRateLimiting("default");

        return app;
    }

    private static async Task<IResult> GetSummaryAsync(
        IDlpEventRepository repo,
        HttpContext ctx,
        CancellationToken ct,
        int days = 7)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId))
            return Results.BadRequest(new { error = "tenant_context_missing" });

        var clampedDays = Math.Clamp(days, 1, 90);
        var summary = await repo.GetSummaryAsync(tenantId, clampedDays, ct);
        return Results.Ok(new
        {
            summary.TotalEvents,
            summary.TotalPiiDetected,
            summary.TypeCounts,
            summary.DailyBuckets,
            windowDays = clampedDays,
        });
    }
}
