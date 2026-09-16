using Aiq.Gateway.Approvals;
using Aiq.Gateway.Middleware;

namespace Aiq.Gateway.Endpoints;

// Sprint 5 HITL endpoints — kế toán trưởng (admin+) duyệt voucher risk cao.
// Approve → auto trigger apply-* endpoint tương ứng. Reject → voucher chuyển
// status 'rejected'.
//
// R21 (business remediation, DUPLICATE finding) — this is a LEGACY,
// tenant-wide HITL mechanism (RequireAuthorization("admin") has no
// Department scoping) that predates the Business-Module Department-ACL
// migration. It still has a real, live caller
// (VoucherExtractJobRunner.MaybeEnqueueForApprovalAsync, L2-risk triggers
// on ar_context/ap_context) so it is kept working and NOT removed here.
// It is deprecated for new use: every newer HITL surface (receipt_match_
// results / expense_claim_results review_status) uses the Department-
// scoped pattern instead — do not add new triggers into this queue; extend
// the Department-scoped pattern for any new business-consequence review.

public static class ApprovalEndpoints
{
    public static IEndpointRouteBuilder MapApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app
            .MapGroup("/approval-queue")
            .RequireAuthorization("admin")   // Chỉ admin+ vào được (kế toán trưởng)
            .RequireRateLimiting("default");

        group.MapGet("/", ListPendingAsync);
        group.MapGet("/{id:guid}", GetByIdAsync);
        group.MapPost("/{id:guid}/review", ReviewAsync);

        return app;
    }

    private sealed record ReviewRequest(string Decision, string? Note);

    private static async Task<IResult> ListPendingAsync(
        IApprovalRepository repo,
        HttpContext ctx,
        CancellationToken ct,
        int limit = 50)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId))
        {
            return Results.BadRequest(new { error = "tenant scope required" });
        }
        var items = await repo.ListPendingAsync(tenantId, limit, ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> GetByIdAsync(
        Guid id,
        IApprovalRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId))
        {
            return Results.BadRequest(new { error = "tenant scope required" });
        }
        var item = await repo.FindByIdAsync(tenantId, id, ct);
        return item is null ? Results.NotFound() : Results.Ok(item);
    }

    private static async Task<IResult> ReviewAsync(
        Guid id,
        ReviewRequest request,
        IApprovalRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TenantContext.TryGet(ctx, out var tenantId))
        {
            return Results.BadRequest(new { error = "tenant scope required" });
        }
        if (!TryGetUserId(ctx, out var reviewerId))
        {
            return Results.Unauthorized();
        }
        if (request.Decision is not ("approved" or "rejected"))
        {
            return Results.BadRequest(new { error = "decision phải là 'approved' hoặc 'rejected'" });
        }

        var updated = await repo.ReviewAsync(tenantId, id, reviewerId, request.Decision, request.Note, ct);
        if (updated is null)
        {
            return Results.NotFound(new { error = "not found hoặc đã được duyệt trước đó" });
        }

        // Note: MVP không auto-trigger apply-* endpoint sau approve.
        // Kế toán viên vẫn phải bấm Accept button. Lý do: separation of
        // concerns — approve = kế toán trưởng OK về mặt policy, apply =
        // kế toán viên thực hiện. Sprint 5.5 sẽ auto-apply nếu cần.

        return Results.Ok(new
        {
            id = updated.Id,
            voucherId = updated.VoucherId,
            status = updated.Status,
            reviewedBy = updated.ReviewedBy,
            reviewedAt = updated.ReviewedAt,
            reviewNote = updated.ReviewNote,
            message = updated.Status == "approved"
                ? "Đã duyệt — kế toán viên có thể vào Accept voucher"
                : "Đã từ chối — voucher không được apply",
        });
    }

    private static bool TryGetUserId(HttpContext ctx, out Guid userId)
    {
        var raw = ctx.User.FindFirst("sub")?.Value
            ?? ctx.User.FindFirst("nameidentifier")?.Value;
        return Guid.TryParse(raw, out userId);
    }
}
