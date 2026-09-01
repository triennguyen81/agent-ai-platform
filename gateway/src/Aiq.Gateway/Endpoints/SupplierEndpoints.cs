using Aiq.Gateway.Middleware;
using Aiq.Gateway.Suppliers;

namespace Aiq.Gateway.Endpoints;

// ============================================================================
// UC3 (Expense/AP): supplier + payables endpoints. Đối xứng CustomerEndpoints
// — được consume bởi voucher upload flow cho invoice_vat_in, payment,
// bank_transfer để enrich seller_mst / beneficiary_mst → supplier +
// current_ap + risk flags.
// ============================================================================

public static class SupplierEndpoints
{
    public static IEndpointRouteBuilder MapSupplierEndpoints(this IEndpointRouteBuilder app)
    {
        // "user" policy — kế toán viên (role=user) sử dụng hàng ngày.
        var group = app
            .MapGroup("/suppliers")
            .RequireAuthorization("user")
            .RequireRateLimiting("default");

        group.MapGet("/", ListAsync);
        group.MapGet("/by-mst/{mst}", GetByMstAsync);
        group.MapGet("/{id:guid}", GetByIdAsync);
        group.MapGet("/{id:guid}/payables", GetPayablesAsync);

        // 3-way match phía AP: invoice_vat_in → PO → GRN. Không đặt dưới
        // /suppliers vì URL semantics (invoice not owned by 1 supplier resource),
        // nhưng share ISupplierRepository nên gộp vào cùng module.
        app.MapGet("/purchase-orders/by-invoice/{documentNumber}", GetPurchaseOrderMatchAsync)
            .RequireAuthorization("user")
            .RequireRateLimiting("default");

        return app;
    }

    private static async Task<IResult> GetPurchaseOrderMatchAsync(
        string documentNumber,
        ISupplierRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId))
        {
            return Results.BadRequest(new { error = "tenant scope required" });
        }
        var match = await repo.MatchInvoiceToPurchaseOrderAsync(tenantId, documentNumber, ct);
        return match is null ? Results.NotFound() : Results.Ok(match);
    }

    private static async Task<IResult> ListAsync(
        ISupplierRepository repo,
        HttpContext ctx,
        CancellationToken ct,
        string? country = null,
        string? q = null)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId))
        {
            return Results.BadRequest(new { error = "tenant scope required" });
        }
        var items = await repo.ListAsync(tenantId, country, q, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> GetByMstAsync(
        string mst,
        ISupplierRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId))
        {
            return Results.BadRequest(new { error = "tenant scope required" });
        }
        var supplier = await repo.FindByMstAsync(tenantId, mst, ct);
        return supplier is null ? Results.NotFound() : Results.Ok(supplier);
    }

    private static async Task<IResult> GetByIdAsync(
        Guid id,
        ISupplierRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId))
        {
            return Results.BadRequest(new { error = "tenant scope required" });
        }
        var supplier = await repo.FindByIdAsync(tenantId, id, ct);
        return supplier is null ? Results.NotFound() : Results.Ok(supplier);
    }

    private static async Task<IResult> GetPayablesAsync(
        Guid id,
        ISupplierRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId))
        {
            return Results.BadRequest(new { error = "tenant scope required" });
        }
        var report = await repo.GetPayablesAsync(tenantId, id, ct);
        return report is null ? Results.NotFound() : Results.Ok(report);
    }
}
