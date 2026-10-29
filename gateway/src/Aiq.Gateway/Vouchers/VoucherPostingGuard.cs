namespace Aiq.Gateway.Vouchers;

/// <summary>
/// Chứng từ flow v2 PR1 (docs/flows/chung-tu-proposed.md §9): a voucher that
/// carries an accounting effect — status 'verified' (legacy 'journalized'), or
/// a live cash posting — is never rewritten, re-extracted or deleted, by any
/// role. Changing it means reversing the posting first. The SQL predicate is
/// the authority (checked in the same statement as the write); the status
/// check only lets an endpoint refuse before any side effect.
/// </summary>
public static class VoucherPostingGuard
{
    public const string ErrorCode = "voucher_posted";
    public const string Message = "Chứng từ đã ghi sổ — không thể sửa, trích xuất lại hay xoá. Hãy đảo bút toán (huỷ ghi nhận) trước.";

    /// <summary>True while an unaliased <c>vouchers</c> row has no accounting effect.</summary>
    public const string NotPostedSql = """
        vouchers.status NOT IN ('verified', 'journalized')
        AND NOT EXISTS (SELECT 1 FROM cash_transactions c WHERE c.voucher_id = vouchers.id AND c.status = 'posted')
        """;

    public static bool IsPostedStatus(string? status) => status is "verified" or "journalized";

    public static IResult Conflict() => Results.Conflict(new { error = ErrorCode, message = Message });
}
