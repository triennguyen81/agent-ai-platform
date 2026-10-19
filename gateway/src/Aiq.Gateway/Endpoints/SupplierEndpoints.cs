using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;
using Aiq.Gateway.Suppliers;
using Aiq.Gateway.Security;

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
            .RequireAuthorization("department_member")
            .RequireRateLimiting("default")
            .RequireBusinessCapability("supplier.management", "expense.processing");

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapGet("/by-mst/{mst}", GetByMstAsync);
        group.MapGet("/ap-summary", GetApSummaryAsync);
        group.MapGet("/{id:guid}", GetByIdAsync);
        // Bank Account Master: read = department member; add/edit/(de)activate = ADMIN only.
        group.MapBankAccounts(Aiq.Gateway.Banking.BankOwnerTypes.Supplier);
        group.MapPatch("/{id:guid}", UpdateAsync);
        group.MapGet("/{id:guid}/payables", GetPayablesAsync);

        // Lifecycle (soft) — never a hard delete, Voucher AP ledger rows and
        // Contract counterparty proposals reference suppliers by id.
        // ADMIN-only, same posture as Customer/Voucher's destructive actions.
        group.MapPost("/{id:guid}/deactivate", DeactivateAsync).RequireAuthorization("department_admin");
        group.MapPost("/{id:guid}/reactivate", ReactivateAsync).RequireAuthorization("department_admin");

        // 3-way match phía AP: invoice_vat_in → PO → GRN. Không đặt dưới
        // /suppliers vì URL semantics (invoice not owned by 1 supplier resource),
        // nhưng share ISupplierRepository nên gộp vào cùng module.
        app.MapGet("/purchase-orders/by-invoice/{documentNumber}", GetPurchaseOrderMatchAsync)
            .RequireAuthorization("department_member")
            .RequireBusinessCapability("supplier.management", "expense.processing", "voucher.processing")
            .RequireRateLimiting("default");

        return app;
    }

    private static async Task<IResult> GetPurchaseOrderMatchAsync(
        string documentNumber,
        ISupplierRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if(!BusinessModuleAuthorization.TryScope(ctx,out _,out var tenantId,out var departmentId))return Results.Forbid();
        var match=await repo.MatchInvoiceToPurchaseOrderAsync(tenantId,departmentId,documentNumber,ct);
        return match is null ? Results.NotFound() : Results.Ok(match);
    }

    private static async Task<IResult> ListAsync(
        ISupplierRepository repo,
        HttpContext ctx,
        CancellationToken ct,
        string? country = null,
        string? q = null,
        bool includeInactive = false)
    {
        if(!BusinessModuleAuthorization.TryScope(ctx,out _,out var tenantId,out var departmentId))return Results.Forbid();
        var items=await repo.ListAsync(tenantId,departmentId,country,q,includeInactive,ct);
        return Results.Ok(new { items, total = items.Count });
    }

    // Mutations require "supplier.management" specifically — narrower than
    // the group's read-level "supplier.management" OR "expense.processing"
    // policy, since an expense-processing-only USER shouldn't be able to
    // create/edit the Supplier master itself, only view it.
    private static async Task<bool> CanManageAsync(PrincipalContext principal, ICapabilityRepository capabilities, CancellationToken ct)
    {
        if (principal.IsInRole("admin")) return true;
        return (await capabilities.ResolveAgentAsync(principal.TenantId!.Value, principal.DepartmentId!.Value, principal.UserId, false, "supplier.management", null, ct)).AgentId is not null;
    }

    private static async Task<IResult> CreateAsync(
        CreateSupplierRequest request,
        ISupplierRepository repo,
        ICapabilityRepository capabilities,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        var normalized = request with { SupplierType = string.IsNullOrWhiteSpace(request.SupplierType) ? "company" : request.SupplierType };
        if (normalized.SupplierType is not ("company" or "individual"))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["supplierType"] = ["phải là 'company' hoặc 'individual'"] });
        if (!string.IsNullOrWhiteSpace(normalized.Mst) && !SupplierValidation.IsValidMstFormat(normalized.Mst))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["mst"] = ["Mã số thuế không đúng định dạng (10 số, có thể kèm -NNN chi nhánh)"] });
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        if (!await CanManageAsync(principal, capabilities, ct)) return Results.Forbid();

        var (created, duplicates) = await repo.CreateAsync(tenantId, departmentId, principal.UserId, normalized, ct);
        if (created is null)
            return Results.Conflict(new { error = "duplicate_supplier_code_or_mst" });

        return Results.Created($"/suppliers/{created.Id}", new { supplier = created, possibleDuplicates = duplicates });
    }

    // Editable while active; USER only on their own record, ADMIN on any
    // record in the Department — same creator-scoped pattern as
    // CustomerEndpoints.UpdateAsync.
    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateSupplierRequest request,
        ISupplierRepository repo,
        ICapabilityRepository capabilities,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.Mst) && !SupplierValidation.IsValidMstFormat(request.Mst))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["mst"] = ["Mã số thuế không đúng định dạng (10 số, có thể kèm -NNN chi nhánh)"] });
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        if (!await CanManageAsync(principal, capabilities, ct)) return Results.Forbid();
        var existing = await repo.FindByIdAsync(tenantId, departmentId, id, ct);
        if (existing is null) return Results.NotFound();
        if (!principal.IsInRole("admin") && existing.CreatedBy != principal.UserId) return Results.Forbid();
        if (existing.Status != "active") return Results.Conflict(new { error = "supplier_inactive" });

        var updated = await repo.UpdateAsync(tenantId, departmentId, id, request, ct);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    private static async Task<IResult> DeactivateAsync(Guid id, ISupplierRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        var updated = await repo.SetStatusAsync(tenantId, departmentId, id, "inactive", principal.UserId, ct);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    private static async Task<IResult> ReactivateAsync(Guid id, ISupplierRepository repo, HttpContext ctx, CancellationToken ct)
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
        ISupplierRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if(!BusinessModuleAuthorization.TryScope(ctx,out _,out var tenantId,out var departmentId))return Results.Forbid();
        var supplier=await repo.FindByMstAsync(tenantId,departmentId,mst,ct);
        return supplier is null ? Results.NotFound() : Results.Ok(supplier);
    }

    // AP dashboard aggregate (/expenses page) — real aging/overdue/due-soon
    // totals derived from sim_ap_ledger, never fabricated.
    private static async Task<IResult> GetApSummaryAsync(
        ISupplierRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var tenantId, out var departmentId)) return Results.Forbid();
        var summary = await repo.GetApSummaryAsync(tenantId, departmentId, ct);
        return Results.Ok(summary);
    }

    private static async Task<IResult> GetByIdAsync(
        Guid id,
        ISupplierRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if(!BusinessModuleAuthorization.TryScope(ctx,out _,out var tenantId,out var departmentId))return Results.Forbid();
        var supplier=await repo.FindByIdAsync(tenantId,departmentId,id,ct);
        return supplier is null ? Results.NotFound() : Results.Ok(supplier);
    }

    private static async Task<IResult> GetPayablesAsync(
        Guid id,
        ISupplierRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if(!BusinessModuleAuthorization.TryScope(ctx,out _,out var tenantId,out var departmentId))return Results.Forbid();
        var report=await repo.GetPayablesAsync(tenantId,departmentId,id,ct);
        return report is null ? Results.NotFound() : Results.Ok(report);
    }
}
