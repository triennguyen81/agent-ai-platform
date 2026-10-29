using Microsoft.AspNetCore.Http;

namespace Aiq.Gateway.Vouchers.Lifecycle;

/// <summary>
/// v2 edit lock (docs/flows/chung-tu-proposed.md §2.2, §5): fields, extraction,
/// journal suggestions and type change only in an editable lifecycle state.
/// Complements <see cref="VoucherPostingGuard"/> (legacy 'verified' / cash
/// posted), which still covers tenants without the v2 flag.
/// </summary>
public static class VoucherLifecycleGuard
{
    public const string ErrorCode = "voucher_locked";
    public const string Message =
        "Chứng từ đang chờ duyệt, đã duyệt hoặc đã đảo — không sửa được. ADMIN phải trả lại chứng từ (cần sửa) trước.";

    /// <summary>Appended to UPDATE/DELETE … FROM vouchers (unaliased) so a concurrent submit still wins.</summary>
    public const string NotLockedSql =
        "vouchers.lifecycle_state NOT IN ('pending_approval', 'approved', 'reversed') AND vouchers.deleted_at IS NULL";

    public static bool IsLocked(VoucherRecord v) =>
        v.DeletedAt is not null
        || v.LifecycleState is LifecycleStates.PendingApproval or LifecycleStates.Approved or LifecycleStates.Reversed;

    public static IResult Conflict() => Results.Conflict(new { error = ErrorCode, message = Message });
}

/// <summary>Thrown by a repository write that found the voucher locked inside its own transaction.</summary>
public sealed class VoucherLockedException(Guid voucherId)
    : InvalidOperationException($"voucher {voucherId} is locked by its lifecycle state")
{
    public Guid VoucherId { get; } = voucherId;
}
