using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Approvals;

// Sprint 5 HITL — Human-In-The-Loop approval queue. Voucher extract
// phát hiện risk cao (requires_l2_approval flag) → route qua queue thay
// vì auto-apply. Kế toán trưởng (admin+) duyệt qua /admin/approval-queue.

public sealed record ApprovalQueueItem(
    Guid Id,
    Guid TenantId,
    Guid VoucherId,
    string VoucherType,
    string? DocumentNumber,
    decimal? TotalAmount,
    string Currency,
    string[] TriggerReasons,
    string ApprovalLevel,
    string Status,
    Guid? SubmittedBy,
    Guid? ReviewedBy,
    DateTime? ReviewedAt,
    string? ReviewNote,
    DateTime CreatedAt);

public sealed record ApprovalQueueSummary(
    Guid Id,
    Guid VoucherId,
    string VoucherType,
    string? DocumentNumber,
    decimal? TotalAmount,
    string Currency,
    string[] TriggerReasons,
    string Status,
    DateTime CreatedAt,
    // Enriched fields joined from vouchers table
    string? SellerName,
    string? BuyerName,
    DateOnly? DocumentDate,
    string? SubmitterEmail);

public interface IApprovalRepository
{
    // Insert queue item khi voucher extract detect requires_l2_approval.
    // Idempotent — nếu voucher đã có entry (bất kỳ status), return existing.
    Task<ApprovalQueueItem> EnqueueAsync(
        Guid tenantId,
        Guid voucherId,
        string voucherType,
        string? documentNumber,
        decimal? totalAmount,
        string currency,
        IReadOnlyList<string> triggerReasons,
        Guid? submittedBy,
        CancellationToken ct);

    Task<IReadOnlyList<ApprovalQueueSummary>> ListPendingAsync(
        Guid tenantId, int limit, CancellationToken ct);

    Task<ApprovalQueueItem?> FindByIdAsync(
        Guid tenantId, Guid id, CancellationToken ct);

    Task<ApprovalQueueItem?> FindByVoucherIdAsync(
        Guid tenantId, Guid voucherId, CancellationToken ct);

    // Approve/reject. Returns updated item hoặc null nếu not found.
    Task<ApprovalQueueItem?> ReviewAsync(
        Guid tenantId,
        Guid id,
        Guid reviewedBy,
        string decision,     // "approved" | "rejected"
        string? reviewNote,
        CancellationToken ct);
}

public sealed class ApprovalRepository : IApprovalRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public ApprovalRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    private const string Cols = """
            id              AS Id,
            tenant_id       AS TenantId,
            voucher_id      AS VoucherId,
            voucher_type    AS VoucherType,
            document_number AS DocumentNumber,
            total_amount    AS TotalAmount,
            currency        AS Currency,
            trigger_reasons AS TriggerReasons,
            approval_level  AS ApprovalLevel,
            status          AS Status,
            submitted_by    AS SubmittedBy,
            reviewed_by     AS ReviewedBy,
            reviewed_at     AS ReviewedAt,
            review_note     AS ReviewNote,
            created_at      AS CreatedAt
        """;

    public async Task<ApprovalQueueItem> EnqueueAsync(
        Guid tenantId, Guid voucherId, string voucherType,
        string? documentNumber, decimal? totalAmount, string currency,
        IReadOnlyList<string> triggerReasons, Guid? submittedBy,
        CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);

        // Idempotent — UNIQUE(voucher_id) chặn duplicate. Nếu tồn tại, return
        // existing.
        const string upsertSql = $"""
            INSERT INTO sim_approval_queue
                (tenant_id, voucher_id, voucher_type, document_number,
                 total_amount, currency, trigger_reasons, submitted_by)
            VALUES (@tenantId, @voucherId, @voucherType, @documentNumber,
                    @totalAmount, @currency, @triggerReasons, @submittedBy)
            ON CONFLICT (voucher_id) DO UPDATE
                SET trigger_reasons = EXCLUDED.trigger_reasons
            RETURNING {Cols}
            """;
        return await conn.QuerySingleAsync<ApprovalQueueItem>(
            new CommandDefinition(upsertSql,
                new
                {
                    tenantId,
                    voucherId,
                    voucherType,
                    documentNumber,
                    totalAmount,
                    currency,
                    triggerReasons = triggerReasons.ToArray(),
                    submittedBy,
                },
                cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ApprovalQueueSummary>> ListPendingAsync(
        Guid tenantId, int limit, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        const string sql = """
            SELECT q.id                  AS Id,
                   q.voucher_id          AS VoucherId,
                   q.voucher_type        AS VoucherType,
                   q.document_number     AS DocumentNumber,
                   q.total_amount        AS TotalAmount,
                   q.currency            AS Currency,
                   q.trigger_reasons     AS TriggerReasons,
                   q.status              AS Status,
                   q.created_at          AS CreatedAt,
                   v.seller_name         AS SellerName,
                   v.buyer_name          AS BuyerName,
                   v.document_date       AS DocumentDate,
                   u.email               AS SubmitterEmail
            FROM sim_approval_queue q
            JOIN vouchers v ON v.id = q.voucher_id
            LEFT JOIN users u ON u.id = q.submitted_by
            WHERE q.tenant_id = @tenantId
              AND q.status = 'pending'
            ORDER BY q.created_at DESC
            LIMIT @limit
            """;
        var rows = await conn.QueryAsync<ApprovalQueueSummary>(
            new CommandDefinition(sql, new { tenantId, limit }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<ApprovalQueueItem?> FindByIdAsync(
        Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var sql = $"SELECT {Cols} FROM sim_approval_queue WHERE tenant_id = @tenantId AND id = @id LIMIT 1";
        return await conn.QueryFirstOrDefaultAsync<ApprovalQueueItem>(
            new CommandDefinition(sql, new { tenantId, id }, cancellationToken: ct));
    }

    public async Task<ApprovalQueueItem?> FindByVoucherIdAsync(
        Guid tenantId, Guid voucherId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var sql = $"SELECT {Cols} FROM sim_approval_queue WHERE tenant_id = @tenantId AND voucher_id = @voucherId LIMIT 1";
        return await conn.QueryFirstOrDefaultAsync<ApprovalQueueItem>(
            new CommandDefinition(sql, new { tenantId, voucherId }, cancellationToken: ct));
    }

    public async Task<ApprovalQueueItem?> ReviewAsync(
        Guid tenantId, Guid id, Guid reviewedBy,
        string decision, string? reviewNote, CancellationToken ct)
    {
        if (decision != "approved" && decision != "rejected")
        {
            throw new ArgumentException($"invalid decision: {decision}", nameof(decision));
        }

        await using var conn = await _factory.OpenAsync(ct);
        var sql = $"""
            UPDATE sim_approval_queue
               SET status      = @decision,
                   reviewed_by = @reviewedBy,
                   reviewed_at = NOW(),
                   review_note = @reviewNote
             WHERE tenant_id = @tenantId
               AND id = @id
               AND status = 'pending'
            RETURNING {Cols}
            """;
        return await conn.QueryFirstOrDefaultAsync<ApprovalQueueItem>(
            new CommandDefinition(sql,
                new { tenantId, id, reviewedBy, decision, reviewNote },
                cancellationToken: ct));
    }
}
