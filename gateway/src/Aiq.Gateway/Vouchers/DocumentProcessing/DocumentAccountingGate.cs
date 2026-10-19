using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aiq.Gateway.Vouchers.DocumentProcessing;

/// <summary>Blocker codes owned by the extraction subsystem (vouchers.processing_blockers).</summary>
public static class ProcessingBlockers
{
    /// <summary>Legacy L2 approval (sim_approval_queue) could not be enqueued. Owned and cleared ONLY by a successful extraction run.</summary>
    public const string LegacyL2Failure = "legacy_l2_failure";
}

/// <summary>The structured refusal every gated accounting mutation returns.</summary>
public static class DocumentReviewRequired
{
    public const string Code = "document_review_required";
    public const string Message = "Chứng từ đang bị chặn bởi bước duyệt/kiểm soát — chưa thể ghi nhận kế toán.";
}

/// <summary>
/// The ONE server-side document-level blocker check for authoritative
/// accounting mutations (invoice apply, expense apply, cash settlement post /
/// allocate, bank-statement line post / allocate / reconcile).
///
/// Blocker ownership — each state has exactly one writer:
///   vouchers.document_review_status  → document review subsystem
///                                      (require_review task open / decided /
///                                      review_task_creation_failed)
///   vouchers.processing_blockers     → extraction job (legacy_l2_failure)
///   voucher_type_classification      → type confirmation (checked separately,
///                                      where it already was)
/// This gate only READS; nothing here clears any state. Corrective actions
/// (unapply / reverse) are not gated so a wrong posting can always be undone.
/// </summary>
public static class DocumentAccountingGate
{
    public static IReadOnlyList<string> Reasons(string? reviewStatus, IEnumerable<string>? processingBlockers)
    {
        var reasons = new List<string>();
        switch (reviewStatus)
        {
            case DocumentReviewStatuses.AwaitingReview: reasons.Add("document_review:awaiting_review"); break;
            case DocumentReviewStatuses.Rejected: reasons.Add("document_review:rejected"); break;
            case DocumentReviewStatuses.CreationFailed: reasons.Add(DocumentReviewStatuses.CreationFailed); break;
        }
        reasons.AddRange((processingBlockers ?? []).Where(b => !string.IsNullOrWhiteSpace(b)).Distinct(StringComparer.Ordinal));
        return reasons;
    }

    /// <summary>Blocking reasons of one voucher in its tenant (empty = may mutate). Null = voucher not found.</summary>
    public static async Task<IReadOnlyList<string>?> LoadReasonsAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, Guid voucherId, CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<(string? ReviewStatus, string[]? Blockers)?>(new CommandDefinition("""
            SELECT document_review_status AS ReviewStatus, processing_blockers AS Blockers
            FROM vouchers WHERE id=@voucherId AND tenant_id=@tenantId
            """, new { voucherId, tenantId }, tx, cancellationToken: ct));
        return row is null ? null : Reasons(row.Value.ReviewStatus, row.Value.Blockers);
    }

    /// <summary>
    /// Structured audit line for a refused mutation (no document content —
    /// ids, operation and reason codes only).
    /// </summary>
    public static void LogBlocked(ILogger logger, Guid tenantId, Guid departmentId, Guid actorId, string? voucherId, string operation,
        IEnumerable<string> reasons) =>
        logger.LogWarning(
            "accounting_mutation_blocked tenant={TenantId} department={DepartmentId} actor_id={ActorId} voucher_id={VoucherId} operation={Operation} review_blocked=true blocking_reasons={Reasons}",
            tenantId, departmentId, actorId, voucherId, operation, string.Join(",", reasons));
}
