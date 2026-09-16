using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Customers;
using Aiq.Gateway.Middleware;
using Aiq.Gateway.Security;

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
            .RequireAuthorization("department_member")
            .RequireRateLimiting("default")
            .RequireBusinessCapability("customer.management");

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapGet("/by-mst/{mst}", GetByMstAsync);
        group.MapGet("/{id:guid}", GetByIdAsync);
        group.MapPatch("/{id:guid}", UpdateAsync);
        group.MapGet("/{id:guid}/receivables", GetReceivablesAsync);

        // Lifecycle (soft) — never a hard delete, Voucher AR ledger rows and
        // Contract counterparty proposals reference customers by id.
        // ADMIN-only, same posture as Voucher's DELETE/apply-* endpoints.
        group.MapPost("/{id:guid}/deactivate", DeactivateAsync).RequireAuthorization("department_admin");
        group.MapPost("/{id:guid}/reactivate", ReactivateAsync).RequireAuthorization("department_admin");

        // UC-ACC-001 §5.6: 3-way match — invoice → sales order → delivery note.
        // Not under /customers by URL semantics but shares the CustomerRepository
        // service so no separate module needed.
        app.MapGet("/sales-orders/by-invoice/{documentNumber}", GetSalesOrderMatchAsync)
            .RequireAuthorization("department_member")
            .RequireBusinessCapability("customer.management", "voucher.processing")
            .RequireRateLimiting("default");

        return app;
    }

    private static async Task<IResult> GetSalesOrderMatchAsync(
        string documentNumber,
        ICustomerRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx,out _,out var tenantId,out var departmentId))return Results.Forbid();
        var match = await repo.MatchInvoiceToSalesOrderAsync(tenantId,departmentId,documentNumber,ct);
        return match is null ? Results.NotFound() : Results.Ok(match);
    }

    private static async Task<IResult> ListAsync(
        ICustomerRepository repo,
        HttpContext ctx,
        CancellationToken ct,
        string? category = null,
        string? q = null,
        bool includeInactive = false)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx,out _,out var tenantId,out var departmentId))return Results.Forbid();
        var items = await repo.ListAsync(tenantId,departmentId,category,q,includeInactive,ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> CreateAsync(
        CreateCustomerRequest request,
        ICustomerRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        // customerType defaults to "company" when omitted — most callers
        // (Vouchers' invoice/receipt flow, a quick "+ Thêm khách hàng") never
        // need to think about this distinction; only reject an explicit,
        // invalid value.
        var normalized = request with { CustomerType = string.IsNullOrWhiteSpace(request.CustomerType) ? "company" : request.CustomerType };
        if (normalized.CustomerType is not ("company" or "individual"))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["customerType"] = ["phải là 'company' hoặc 'individual'"] });
        if (!string.IsNullOrWhiteSpace(normalized.Mst) && !CustomerValidation.IsValidMstFormat(normalized.Mst))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["mst"] = ["Mã số thuế không đúng định dạng (10 số, có thể kèm -NNN chi nhánh)"] });
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();

        var (created, duplicates) = await repo.CreateAsync(tenantId, departmentId, principal.UserId, normalized, ct);
        if (created is null)
            return Results.Conflict(new { error = "duplicate_customer_code_or_mst" });

        return Results.Created($"/customers/{created.Id}", new { customer = created, possibleDuplicates = duplicates });
    }

    // Editable while active; USER only on their own record, ADMIN on any
    // record in the Department — same creator-scoped pattern as
    // VoucherEndpoints.UpdateAsync/ContractEndpoints.Update.
    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateCustomerRequest request,
        ICustomerRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.Mst) && !CustomerValidation.IsValidMstFormat(request.Mst))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["mst"] = ["Mã số thuế không đúng định dạng (10 số, có thể kèm -NNN chi nhánh)"] });
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        var existing = await repo.FindByIdAsync(tenantId, departmentId, id, ct);
        if (existing is null) return Results.NotFound();
        if (!principal.IsInRole("admin") && existing.CreatedBy != principal.UserId) return Results.Forbid();
        if (existing.Status != "active") return Results.Conflict(new { error = "customer_inactive" });

        var updated = await repo.UpdateAsync(tenantId, departmentId, id, request, ct);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    private static async Task<IResult> DeactivateAsync(Guid id, ICustomerRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        var updated = await repo.SetStatusAsync(tenantId, departmentId, id, "inactive", principal.UserId, ct);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    private static async Task<IResult> ReactivateAsync(Guid id, ICustomerRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        var updated = await repo.SetStatusAsync(tenantId, departmentId, id, "active", principal.UserId, ct);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    private static bool TryValidate(object value, out Dictionary<string, string[]> errors)
    {
        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(value, new ValidationContext(value), results, validateAllProperties: true);
        errors = results
            .SelectMany(r => r.MemberNames.DefaultIfEmpty("request").Select(m => (m, r.ErrorMessage)))
            .GroupBy(x => x.m)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage ?? "invalid").ToArray());
        return valid;
    }

    private static async Task<IResult> GetByMstAsync(
        string mst,
        ICustomerRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx,out _,out var tenantId,out var departmentId))return Results.Forbid();
        var customer=await repo.FindByMstAsync(tenantId,departmentId,mst,ct);
        return customer is null ? Results.NotFound() : Results.Ok(customer);
    }

    private static async Task<IResult> GetByIdAsync(
        Guid id,
        ICustomerRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx,out _,out var tenantId,out var departmentId))return Results.Forbid();
        var customer=await repo.FindByIdAsync(tenantId,departmentId,id,ct);
        return customer is null ? Results.NotFound() : Results.Ok(customer);
    }

    private static async Task<IResult> GetReceivablesAsync(
        Guid id,
        ICustomerRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx,out _,out var tenantId,out var departmentId))return Results.Forbid();
        var report=await repo.GetReceivablesAsync(tenantId,departmentId,id,ct);
        return report is null ? Results.NotFound() : Results.Ok(report);
    }
}
