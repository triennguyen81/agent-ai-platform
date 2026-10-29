using Aiq.Gateway.Security;
using Aiq.Gateway.Vouchers;
using Aiq.Gateway.Vouchers.DocumentProcessing;
using Aiq.Gateway.Vouchers.Lifecycle;
using Aiq.Gateway.Vouchers.Validation;

namespace Aiq.Gateway.Endpoints;

public sealed record VoucherTransitionRequest(string? Reason = null, bool? Post = null);

/// <summary>
/// Chứng từ flow v2 PR3 transitions (docs/flows/chung-tu-proposed.md §2.2, §2.3, §5):
/// submit / approve / return / reject + the event history. Only for tenants with
/// the voucher_flow_v2 flag. Who may act is decided by <see cref="VoucherStateMachine"/>:
/// a same-department ADMIN does every step, including on their own voucher; a USER
/// only submits their own; TENANT_OWNER / SUPER_ADMIN have no department scope (403);
/// another department sees 404; the Agent never transitions (§6.3).
/// Post, reverse and approve {post:true} come in PR5.
/// </summary>
public static class VoucherLifecycleEndpoints
{
    public const string FlowDisabled = "voucher_flow_v2_disabled";

    public static RouteGroupBuilder MapVoucherLifecycle(this RouteGroupBuilder group)
    {
        group.MapGet("/{id:guid}/events", EventsAsync);
        group.MapPost("/{id:guid}/submit", SubmitAsync);
        group.MapPost("/{id:guid}/approve", ApproveAsync).RequireAuthorization("department_admin");
        group.MapPost("/{id:guid}/return", ReturnAsync).RequireAuthorization("department_admin");
        group.MapPost("/{id:guid}/reject", RejectAsync).RequireAuthorization("department_admin");
        return group;
    }

    internal static async Task<IResult> EventsAsync(Guid id, IVoucherRepository repo, IVoucherLifecycleRepository lifecycle,
        HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var tenantId, out var departmentId)) return Results.Forbid();
        if (await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct) is null) return Results.NotFound();
        return Results.Ok(await lifecycle.ListEventsAsync(tenantId, id, ct));
    }

    internal static Task<IResult> SubmitAsync(Guid id, VoucherTransitionRequest? request, IVoucherRepository repo,
        IVoucherLifecycleRepository lifecycle, ITenantFeatureFlags flags, VoucherValidationService validation,
        IDocumentReviewRepository reviews, HttpContext ctx, CancellationToken ct) =>
        TransitionAsync(LifecycleAction.Submit, id, request, repo, lifecycle, flags, validation, reviews, ctx, ct);

    internal static Task<IResult> ApproveAsync(Guid id, VoucherTransitionRequest? request, IVoucherRepository repo,
        IVoucherLifecycleRepository lifecycle, ITenantFeatureFlags flags, VoucherValidationService validation,
        IDocumentReviewRepository reviews, HttpContext ctx, CancellationToken ct) =>
        TransitionAsync(LifecycleAction.Approve, id, request, repo, lifecycle, flags, validation, reviews, ctx, ct);

    internal static Task<IResult> ReturnAsync(Guid id, VoucherTransitionRequest? request, IVoucherRepository repo,
        IVoucherLifecycleRepository lifecycle, ITenantFeatureFlags flags, VoucherValidationService validation,
        IDocumentReviewRepository reviews, HttpContext ctx, CancellationToken ct) =>
        TransitionAsync(LifecycleAction.Return, id, request, repo, lifecycle, flags, validation, reviews, ctx, ct);

    internal static Task<IResult> RejectAsync(Guid id, VoucherTransitionRequest? request, IVoucherRepository repo,
        IVoucherLifecycleRepository lifecycle, ITenantFeatureFlags flags, VoucherValidationService validation,
        IDocumentReviewRepository reviews, HttpContext ctx, CancellationToken ct) =>
        TransitionAsync(LifecycleAction.Reject, id, request, repo, lifecycle, flags, validation, reviews, ctx, ct);

    private static async Task<IResult> TransitionAsync(LifecycleAction action, Guid id, VoucherTransitionRequest? request,
        IVoucherRepository repo, IVoucherLifecycleRepository lifecycle, ITenantFeatureFlags flags,
        VoucherValidationService validation, IDocumentReviewRepository reviews, HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        if (!await flags.IsEnabledAsync(tenantId, TenantFeatureFlags.VoucherFlowV2, ct))
            return Results.Conflict(new { error = FlowDisabled, message = "Tenant chưa bật quy trình chứng từ v2." });
        if (request?.Post == true)
            return Results.BadRequest(new { error = "post_not_available", message = "Duyệt & ghi sổ chưa có ở bước này — duyệt trước, ghi sổ sau." });

        var detail = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
        if (detail is null) return Results.NotFound();

        // Secondary layer (QĐ 23): the header survives only from the agent's network.
        var isAgent = string.Equals(ctx.Request.Headers["X-AIQ-Actor"].ToString(), "agent", StringComparison.OrdinalIgnoreCase);
        var actor = new LifecycleActor(
            isAgent ? LifecycleActorKind.Agent : principal.IsInRole("admin") ? LifecycleActorKind.Admin : LifecycleActorKind.User,
            IsCreator: detail.Voucher.UserId == principal.UserId);

        // Refuse an actor who may never do this before anything is written.
        if (!VoucherStateMachine.MayAct(action, actor))
            return Refused(VoucherStateMachine.Decide(detail.Voucher.LifecycleState, action, actor, request?.Reason));

        if (action == LifecycleAction.Submit)
        {
            // Submitting stores the validator result for exactly the submitted content;
            // with the flag on this also moves the voucher to validated / needs_fix.
            var report = await validation.RecordAsync(tenantId, departmentId, id, ct);
            if (report is null) return Results.NotFound();
            if (report.HasErrors) return ValidationFailed(report);
            detail = await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct);
            if (detail is null) return Results.NotFound();
        }

        var decision = VoucherStateMachine.Decide(detail.Voucher.LifecycleState, action, actor, request?.Reason);
        if (!decision.Allowed) return Refused(decision);

        if (action == LifecycleAction.Approve && await ApproveGateAsync(tenantId, departmentId, id, detail, validation, reviews, ct) is { } refused)
            return refused;

        var moved = await lifecycle.TransitionAsync(tenantId, departmentId, id,
            new LifecycleChange(detail.Voucher.LifecycleState, decision.Target!, isAgent ? "agent" : "user", principal.UserId,
                request?.Reason, ctx.TraceIdentifier), ct);
        if (!moved)
            return Results.Conflict(new { error = LifecycleErrors.InvalidTransition, message = "Chứng từ vừa đổi trạng thái — tải lại rồi thử lại." });

        return Results.Ok(await repo.FindInDepartmentAsync(tenantId, departmentId, id, ct));
    }

    /// <summary>§2.3 → approved, the parts that exist before PR5 (the L2 gate and the posting checks come there).</summary>
    private static async Task<IResult?> ApproveGateAsync(Guid tenantId, Guid departmentId, Guid id, VoucherDetail detail,
        VoucherValidationService validation, IDocumentReviewRepository reviews, CancellationToken ct)
    {
        // Locked since submit, but rates / tenant settings may have changed meanwhile.
        var report = await validation.ComputeAsync(tenantId, departmentId, id, ct);
        if (report is null) return Results.NotFound();
        if (report.HasErrors) return ValidationFailed(report);
        if (VoucherTypeClassification.FromExtractedFields(detail.Voucher.ExtractedFieldsJson) is { BlocksPosting: true })
            return Results.Conflict(new { error = "voucher_type_unconfirmed", message = "Loại chứng từ chưa được xác nhận — ADMIN cần xác nhận loại đúng trước khi duyệt." });
        if (await reviews.GetBlockingReasonsAsync(tenantId, id, ct) is { Count: > 0 } reasons)
            return Results.Conflict(new { error = DocumentReviewRequired.Code, reasons, message = DocumentReviewRequired.Message });
        if (detail.Voucher.VoucherType == VoucherTypes.InvoiceIn && detail.Voucher.TaxVerificationStatus == TaxVerificationStatuses.Invalid)
            return Results.Conflict(new { error = "tax_verification_invalid", message = "Hoá đơn đầu vào bị xác minh thuế là không hợp lệ — không thể duyệt." });
        return null;
    }

    private static IResult ValidationFailed(VoucherValidationReport report) => Results.Conflict(new
    {
        error = "validation_failed",
        message = "Chứng từ còn lỗi kiểm tra — sửa rồi gửi lại.",
        issues = report.Issues.Where(i => i.Severity == ValidationSeverity.Error),
    });

    private static IResult Refused(LifecycleDecision d) => d.ErrorCode switch
    {
        LifecycleErrors.ForbiddenActor => Results.Json(new { error = d.ErrorCode, message = d.Message }, statusCode: StatusCodes.Status403Forbidden),
        LifecycleErrors.ReasonRequired => Results.BadRequest(new { error = d.ErrorCode, message = d.Message }),
        _ => Results.Conflict(new { error = d.ErrorCode, message = d.Message }),
    };
}
