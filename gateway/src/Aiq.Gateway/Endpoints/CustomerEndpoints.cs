using Aiq.Gateway.Customers;
using Aiq.Gateway.Middleware;

namespace Aiq.Gateway.Endpoints;

// ============================================================================
// UC-ACC-001 §5.4: customer + A/R endpoints consumed by the invoice-processing
// agent flow (voucher upload → enrich buyer_mst → look up customer + AR →
// journal suggestion uses customer sub-account).
// ============================================================================

public static class CustomerEndpoints
{
    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        // "user" policy: any authenticated tenant member (kế toán viên included)
        var group = app
            .MapGroup("/customers")
            .RequireAuthorization("user")
            .RequireRateLimiting("default");

        group.MapGet("/", ListAsync);
        group.MapGet("/by-mst/{mst}", GetByMstAsync);
        group.MapGet("/{id:guid}", GetByIdAsync);
        group.MapGet("/{id:guid}/receivables", GetReceivablesAsync);

        // UC-ACC-001 §5.6: 3-way match — invoice → sales order → delivery note.
        // Not under /customers by URL semantics but shares the CustomerRepository
        // service so no separate module needed.
        app.MapGet("/sales-orders/by-invoice/{documentNumber}", GetSalesOrderMatchAsync)
            .RequireAuthorization("user")
            .RequireRateLimiting("default");

        return app;
    }

    private static async Task<IResult> GetSalesOrderMatchAsync(
        string documentNumber,
        ICustomerRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId))
        {
            return Results.BadRequest(new { error = "tenant scope required" });
        }
        var match = await repo.MatchInvoiceToSalesOrderAsync(tenantId, documentNumber, ct);
        return match is null ? Results.NotFound() : Results.Ok(match);
    }

    private static async Task<IResult> ListAsync(
        ICustomerRepository repo,
        HttpContext ctx,
        CancellationToken ct,
        string? category = null,
        string? q = null)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId))
        {
            return Results.BadRequest(new { error = "tenant scope required" });
        }
        var items = await repo.ListAsync(tenantId, category, q, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> GetByMstAsync(
        string mst,
        ICustomerRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId))
        {
            return Results.BadRequest(new { error = "tenant scope required" });
        }
        var customer = await repo.FindByMstAsync(tenantId, mst, ct);
        return customer is null ? Results.NotFound() : Results.Ok(customer);
    }

    private static async Task<IResult> GetByIdAsync(
        Guid id,
        ICustomerRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId))
        {
            return Results.BadRequest(new { error = "tenant scope required" });
        }
        var customer = await repo.FindByIdAsync(tenantId, id, ct);
        return customer is null ? Results.NotFound() : Results.Ok(customer);
    }

    private static async Task<IResult> GetReceivablesAsync(
        Guid id,
        ICustomerRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId))
        {
            return Results.BadRequest(new { error = "tenant scope required" });
        }
        var report = await repo.GetReceivablesAsync(tenantId, id, ct);
        return report is null ? Results.NotFound() : Results.Ok(report);
    }
}
