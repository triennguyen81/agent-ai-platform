using Aiq.Gateway.Security;
using Aiq.Gateway.Vouchers.Lifecycle;
using Aiq.Gateway.Vouchers.Periods;
using Microsoft.AspNetCore.Diagnostics;

namespace Aiq.Gateway.Endpoints;

public sealed record PeriodReopenRequest(string? Reason);

/// <summary>
/// Chứng từ flow v2 PR4 (docs/flows/chung-tu-proposed.md §5, §10.1; QĐ 12, 13):
/// monthly accounting periods of the caller's department. Only for tenants with
/// the voucher_flow_v2 flag. ADMIN Department Authority Invariant: the
/// department ADMIN closes and reopens (route policy department_admin + the
/// check below); USER only reads; TENANT_OWNER / SUPER_ADMIN have no department
/// scope (403); another department only ever sees and changes its own periods;
/// the agent never closes or reopens (§6.3).
/// The lock itself is enforced in the database, inside every write's own
/// transaction (migration 071); this API only changes a period's status.
/// </summary>
public static class AccountingPeriodEndpoints
{
    public static IEndpointRouteBuilder MapAccountingPeriodEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app
            .MapGroup("/accounting-periods")
            .RequireAuthorization("department_member")
            .RequireRateLimiting("default");

        group.MapGet("/", ListAsync);
        group.MapGet("/{period}/events", EventsAsync);
        group.MapPost("/{period}/close", CloseAsync).RequireAuthorization("department_admin");
        group.MapPost("/{period}/reopen", ReopenAsync).RequireAuthorization("department_admin");
        return app;
    }

    internal static async Task<IResult> ListAsync(int? year, IAccountingPeriodRepository periods, ITenantFeatureFlags flags,
        TimeProvider time, HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var tenantId, out var departmentId)) return Results.Forbid();
        if (await DisabledAsync(flags, tenantId, ct) is { } disabled) return disabled;
        var y = year ?? AccountingPeriod.Of(time.GetUtcNow()).Year;
        if (y is < AccountingPeriod.MinYear or > AccountingPeriod.MaxYear)
            return Results.BadRequest(new { error = "invalid_year", message = $"Năm phải trong khoảng {AccountingPeriod.MinYear}–{AccountingPeriod.MaxYear}." });
        return Results.Ok(await periods.ListAsync(tenantId, departmentId, y, ct));
    }

    internal static async Task<IResult> EventsAsync(string period, IAccountingPeriodRepository periods, ITenantFeatureFlags flags,
        HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var tenantId, out var departmentId)) return Results.Forbid();
        if (await DisabledAsync(flags, tenantId, ct) is { } disabled) return disabled;
        if (!AccountingPeriod.TryParse(period, out var p)) return InvalidPeriod();
        return Results.Ok(await periods.ListEventsAsync(tenantId, departmentId, p, ct));
    }

    internal static async Task<IResult> CloseAsync(string period, IAccountingPeriodRepository periods, ITenantFeatureFlags flags,
        TimeProvider time, HttpContext ctx, CancellationToken ct)
    {
        if (await ScopeAsync(ctx, flags, period, ct) is not { } scope) return Results.Forbid();
        if (scope.Refused is { } refused) return refused;
        var result = await periods.CloseAsync(scope.TenantId, scope.DepartmentId, scope.Period, scope.Actor, AccountingPeriod.Of(time.GetUtcNow()), ct);
        return result.Error is null ? Results.Ok(new { period = result.Period, warnings = result.Warnings }) : Refused(result, scope.Period);
    }

    internal static async Task<IResult> ReopenAsync(string period, PeriodReopenRequest? request, IAccountingPeriodRepository periods,
        ITenantFeatureFlags flags, HttpContext ctx, CancellationToken ct)
    {
        if (await ScopeAsync(ctx, flags, period, ct) is not { } scope) return Results.Forbid();
        if (scope.Refused is { } refused) return refused;
        var result = await periods.ReopenAsync(scope.TenantId, scope.DepartmentId, scope.Period, scope.Actor, request?.Reason, ct);
        return result.Error is null ? Results.Ok(new { period = result.Period, warnings = result.Warnings }) : Refused(result, scope.Period);
    }

    /// <summary>
    /// 409 period_locked for the database error a closed period raises (SQLSTATE
    /// AQ001, migration 071); null for any other exception.
    /// </summary>
    public static IResult? PeriodLockedResult(Exception ex) =>
        AccountingPeriodErrors.LockedPeriodOf(ex) is { } period ? PeriodLocked(period) : null;

    /// <summary>
    /// Pre-check for the legacy recognition paths (apply-invoice / apply-expense):
    /// they book the ledger in its own transaction before the voucher row
    /// changes, so the trigger alone would refuse only the second half. PR5 makes
    /// posting one transaction; until then a v2 voucher of a closed period is
    /// refused before anything is booked. Null when the period is open, the
    /// voucher has no date yet, or the tenant has no v2 flag.
    /// </summary>
    public static async Task<IResult?> LockedBeforePostingAsync(Aiq.Gateway.Vouchers.VoucherRecord voucher, Guid tenantId, Guid departmentId,
        ITenantFeatureFlags flags, IAccountingPeriodRepository periods, CancellationToken ct)
    {
        if (AccountingPeriod.EffectiveDate(voucher.AccountingDate, voucher.DocumentDate) is not { } date) return null;
        if (!await flags.IsEnabledAsync(tenantId, TenantFeatureFlags.VoucherFlowV2, ct)) return null;
        var period = AccountingPeriod.Of(date);
        return (await periods.ClosedAsync(tenantId, departmentId, ct)).Contains(period) ? PeriodLocked(period.ToString()) : null;
    }

    private static IResult PeriodLocked(string period) => Results.Conflict(new
    {
        error = AccountingPeriodErrors.PeriodLocked,
        period,
        message = $"Kỳ kế toán {period} đã khoá — chọn ngày hạch toán trong kỳ đang mở, hoặc nhờ ADMIN mở lại kỳ (có lý do).",
    });

    private sealed record Scope(Guid TenantId, Guid DepartmentId, AccountingPeriod Period, PeriodActor Actor, IResult? Refused);

    /// <summary>Close / reopen: department scope, the v2 flag, a valid period, an ADMIN that is not the agent.</summary>
    private static async Task<Scope?> ScopeAsync(HttpContext ctx, ITenantFeatureFlags flags, string period, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return null;
        IResult? refused = null;
        AccountingPeriod.TryParse(period, out var p);
        if (await DisabledAsync(flags, tenantId, ct) is { } disabled) refused = disabled;
        else if (p == default) refused = InvalidPeriod();
        // Secondary layer (QĐ 23): the header survives only from the agent's network.
        else if (string.Equals(ctx.Request.Headers["X-AIQ-Actor"].ToString(), "agent", StringComparison.OrdinalIgnoreCase))
            refused = Forbidden("forbidden_actor", "Agent không được khoá hoặc mở kỳ kế toán.");
        else if (!principal.IsInRole("admin"))
            refused = Forbidden("forbidden_actor", "Chỉ ADMIN của phòng ban mới được khoá hoặc mở kỳ kế toán.");
        return new Scope(tenantId, departmentId, p, new PeriodActor("user", principal.UserId, ctx.TraceIdentifier), refused);
    }

    private static async Task<IResult?> DisabledAsync(ITenantFeatureFlags flags, Guid tenantId, CancellationToken ct) =>
        await flags.IsEnabledAsync(tenantId, TenantFeatureFlags.VoucherFlowV2, ct)
            ? null
            : Results.Conflict(new { error = VoucherLifecycleEndpoints.FlowDisabled, message = "Tenant chưa bật quy trình chứng từ v2." });

    private static IResult InvalidPeriod() =>
        Results.BadRequest(new { error = "invalid_period", message = "Kỳ phải có dạng yyyy-MM (ví dụ 2026-10)." });

    private static IResult Forbidden(string error, string message) =>
        Results.Json(new { error, message }, statusCode: StatusCodes.Status403Forbidden);

    private static IResult Refused(PeriodChangeResult r, AccountingPeriod period) => r.Error switch
    {
        AccountingPeriodErrors.ReasonRequired => Results.BadRequest(new { error = r.Error, message = "Mở lại kỳ phải có lý do." }),
        AccountingPeriodErrors.AlreadyClosed => Results.Conflict(new { error = r.Error, message = $"Kỳ {period} đã khoá rồi." }),
        AccountingPeriodErrors.NotClosed => Results.Conflict(new { error = r.Error, message = $"Kỳ {period} đang mở." }),
        AccountingPeriodErrors.InFuture => Results.Conflict(new { error = r.Error, message = $"Kỳ {period} chưa tới — chỉ khoá được kỳ hiện tại hoặc trước đó." }),
        AccountingPeriodErrors.PreviousOpen => Results.Conflict(new
        {
            error = r.Error, previous = r.Previous,
            message = $"Kỳ {r.Previous} còn mở và có chứng từ — khoá kỳ {r.Previous} trước.",
        }),
        AccountingPeriodErrors.HasUnposted => Results.Conflict(new
        {
            error = r.Error, voucherIds = r.VoucherIds,
            message = $"Kỳ {period} còn {r.VoucherIds.Count} chứng từ chờ duyệt hoặc đã duyệt chưa ghi sổ.",
        }),
        _ => Results.Conflict(new { error = r.Error }),
    };
}

/// <summary>
/// The same 409 period_locked for any route outside /vouchers (whose own filter
/// maps it first), so a closed period never surfaces as a 500.
/// </summary>
public sealed class PeriodLockedExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (AccountingPeriodEndpoints.PeriodLockedResult(exception) is not { } result) return false;
        await result.ExecuteAsync(httpContext);
        return true;
    }
}
