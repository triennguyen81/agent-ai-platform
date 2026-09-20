using Aiq.Gateway.Middleware;
using Aiq.Gateway.Reports;

namespace Aiq.Gateway.Endpoints;

/// <summary>
/// P2 audit fix — the PDF's weekly Ban giám đốc report ("dòng tiền thu, tuổi
/// nợ, nhóm khách quá hạn, chi phí theo bộ phận") had no corresponding
/// endpoint anywhere. tenant_governance-gated (the tenant_owner role is the
/// closest existing analog to a company's own leadership — this report
/// spans every Department in the tenant, which no Department-scoped role
/// should see all of at once).
/// </summary>
public static class ReportsEndpoints
{
    public static IEndpointRouteBuilder MapReportsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/tenant/reports/weekly-summary", GetWeeklySummaryAsync)
            .RequireAuthorization("tenant_governance")
            .RequireRateLimiting("default");

        return app;
    }

    private static async Task<IResult> GetWeeklySummaryAsync(
        IWeeklySummaryReportRepository reports,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId))
            return Results.BadRequest(new { error = "tenant_context_missing" });

        var summary = await reports.GetAsync(tenantId, ct);
        return Results.Ok(summary);
    }
}
