using Aiq.Gateway.Payments;
using Aiq.Gateway.Vouchers.DocumentProcessing;
using Aiq.Gateway.Security;

namespace Aiq.Gateway.Endpoints;

/// <summary>
/// Chứng từ THU / CHI -> ADMIN duyệt -> tài khoản tiền -> đối trừ AR/AP.
///
/// ACL (final rule): the owning Department's ADMIN has full operational
/// authority — upload, review, match, approve/post, allocate, unapply,
/// including on a voucher they created themselves. No second ADMIN,
/// TENANT_OWNER or SUPER_ADMIN is required. USER may read the settlement
/// view (same scope as reading the voucher) but every mutation requires
/// "department_admin". Scope is always (tenant, department) from the
/// authenticated principal — a voucher in another department/tenant is 404.
/// </summary>
public static class CashSettlementEndpoints
{
    public static RouteGroupBuilder MapCashSettlement(this RouteGroupBuilder group)
    {
        group.MapGet("/{id:guid}/settlement", GetAsync);
        group.MapPost("/{id:guid}/settlement/post", PostAsync).RequireAuthorization("department_admin");
        group.MapPost("/{id:guid}/settlement/allocate", AllocateAsync).RequireAuthorization("department_admin");
        group.MapPost("/{id:guid}/settlement/discount/confirm", ConfirmDiscountAsync).RequireAuthorization("department_admin");
        group.MapPost("/{id:guid}/settlement/unapply", UnapplyAsync).RequireAuthorization("department_admin");
        group.MapPost("/{id:guid}/settlement/reverse", ReverseAsync).RequireAuthorization("department_admin");
        return group;
    }

    public sealed record PostSettlementBody(Guid? CounterpartyId, List<AllocationInput>? Allocations, bool? LeaveUnapplied, string? PaymentMethod);
    public sealed record AllocateBody(List<AllocationInput>? Allocations);
    public sealed record UnapplyBody(Guid? ApplicationId, string? Reason);
    public sealed record ReverseBody(string? Reason);

    private static async Task<IResult> GetAsync(Guid id, ICashSettlementRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        var context = await repo.GetContextAsync(tenantId, departmentId, id, ct);
        if (context is null) return Results.NotFound();
        return Results.Ok(new { context, canApprove = principal.IsInRole("admin") });
    }

    private static Task<IResult> PostAsync(Guid id, PostSettlementBody? body, ICashSettlementRepository repo, HttpContext ctx, CancellationToken ct) =>
        Mutate(ctx, (tenantId, departmentId, actorId) => repo.PostAsync(tenantId, departmentId, id, actorId,
            new PostCashRequest(body?.CounterpartyId, body?.Allocations, body?.LeaveUnapplied ?? false, body?.PaymentMethod), ct));

    private static Task<IResult> AllocateAsync(Guid id, AllocateBody? body, ICashSettlementRepository repo, HttpContext ctx, CancellationToken ct) =>
        Mutate(ctx, (tenantId, departmentId, actorId) => repo.AllocateAsync(tenantId, departmentId, id, actorId,
            (IReadOnlyList<AllocationInput>?)body?.Allocations ?? Array.Empty<AllocationInput>(), ct));

    private static Task<IResult> ConfirmDiscountAsync(Guid id, ConfirmPaymentDiscountRequest body, ICashSettlementRepository repo, HttpContext ctx, CancellationToken ct) =>
        Mutate(ctx, (tenantId, departmentId, actorId) => repo.ConfirmPaymentDiscountAsync(tenantId, departmentId, id, actorId, body, ct));

    private static Task<IResult> UnapplyAsync(Guid id, UnapplyBody? body, ICashSettlementRepository repo, HttpContext ctx, CancellationToken ct) =>
        Mutate(ctx, (tenantId, departmentId, actorId) => repo.UnapplyAsync(tenantId, departmentId, id, actorId, body?.ApplicationId, body?.Reason, ct));

    private static Task<IResult> ReverseAsync(Guid id, ReverseBody? body, ICashSettlementRepository repo, HttpContext ctx, CancellationToken ct) =>
        Mutate(ctx, (tenantId, departmentId, actorId) => repo.ReverseAsync(tenantId, departmentId, id, actorId, body?.Reason, ct));

    private static async Task<IResult> Mutate(
        HttpContext ctx, Func<Guid, Guid, Guid, Task<SettlementMutationResult?>> action)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        try
        {
            var result = await action(tenantId, departmentId, principal.UserId);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (CashSettlementException ex)
        {
            return ToProblem(ex, ctx, tenantId, departmentId, principal.UserId);
        }
    }

    public static IResult ToProblem(CashSettlementException ex) =>
        Results.Conflict(new { error = ex.Code, message = ex.Message, details = ex.Details });

    /// <summary>As <see cref="ToProblem(CashSettlementException)"/>, plus the audit line for a document-gate refusal.</summary>
    public static IResult ToProblem(CashSettlementException ex, HttpContext ctx, Guid tenantId, Guid departmentId, Guid actorId)
    {
        if (ex.Details is DocumentGateDetails gate)
            DocumentAccountingGate.LogBlocked(VoucherEndpoints.DocumentGateLogger(ctx), tenantId, departmentId, actorId,
                ctx.Request.RouteValues["id"]?.ToString(), $"{ctx.Request.Method} {ctx.Request.Path}", gate.Reasons);
        return ToProblem(ex);
    }
}
