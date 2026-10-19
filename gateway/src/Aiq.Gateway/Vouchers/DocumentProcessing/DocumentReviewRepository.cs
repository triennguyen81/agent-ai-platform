using Aiq.Gateway.Data;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Vouchers.DocumentProcessing;

/// <summary>
/// vouchers.document_review_status — the explicit gate state every posting
/// path checks (apply-invoice, apply-expense, cash settlement).
/// </summary>
public static class DocumentReviewStatuses
{
    public const string AwaitingReview = "awaiting_review";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string CreationFailed = "review_task_creation_failed";

    /// <summary>Anything but "no review" or "approved" blocks authoritative operations.</summary>
    public static bool Blocks(string? status) => status is AwaitingReview or Rejected or CreationFailed;
}

public sealed record DocumentReviewTaskRecord(
    Guid Id, Guid TenantId, Guid DepartmentId, Guid VoucherId, Guid? AgentId, Guid? AgentVersionId,
    Guid? WorkflowId, Guid? WorkflowVersionId, string ReviewStep, string DocumentType, string[] Reasons,
    string Status, Guid? CreatedBy, DateTime CreatedAt, Guid? DecidedBy, DateTime? DecidedAt, string? DecisionNote);

public sealed record DocumentReviewOpenRequest(
    Guid TenantId, Guid DepartmentId, Guid VoucherId, Guid? AgentId, Guid? AgentVersionId,
    Guid WorkflowId, Guid? WorkflowVersionId, string DocumentType, IReadOnlyList<string> Reasons, Guid? ActorUserId);

public sealed record DocumentReviewDecisionRequest(string? Decision, string? Note);

public interface IDocumentReviewRepository
{
    /// <summary>
    /// Persists (or returns the existing) OPEN review task for this document +
    /// workflow version + step and moves the document to awaiting_review in
    /// the SAME transaction. Throws on any failure — callers must fail closed.
    /// </summary>
    Task<(DocumentReviewTaskRecord Task, bool Created)> OpenAsync(DocumentReviewOpenRequest request, CancellationToken ct);

    /// <summary>Records that the gate could not be persisted; the document stays blocked.</summary>
    Task MarkCreationFailedAsync(Guid tenantId, Guid voucherId, CancellationToken ct);

    Task<string?> GetGateStatusAsync(Guid tenantId, Guid voucherId, CancellationToken ct);

    /// <summary>Canonical document-level blocking reasons (DocumentAccountingGate); empty = may mutate.</summary>
    Task<IReadOnlyList<string>> GetBlockingReasonsAsync(Guid tenantId, Guid voucherId, CancellationToken ct);
    Task<IReadOnlyList<DocumentReviewTaskRecord>?> ListForVoucherAsync(Guid tenantId, Guid departmentId, Guid voucherId, CancellationToken ct);

    /// <summary>
    /// ADMIN decision on an OPEN task of a document in the caller's own
    /// tenant+department. Null = not found / not open / other scope.
    /// </summary>
    Task<DocumentReviewTaskRecord?> DecideAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid taskId, Guid actorId,
        string decision, string? note, CancellationToken ct);
}

public sealed class DocumentReviewRepository(NpgsqlConnectionFactory factory) : IDocumentReviewRepository
{
    private const string Columns = """
        id AS Id, tenant_id AS TenantId, department_id AS DepartmentId, voucher_id AS VoucherId,
        agent_id AS AgentId, agent_version_id AS AgentVersionId, workflow_id AS WorkflowId,
        workflow_version_id AS WorkflowVersionId, review_step AS ReviewStep, document_type AS DocumentType,
        reasons AS Reasons, status AS Status, created_by AS CreatedBy, created_at AS CreatedAt,
        decided_by AS DecidedBy, decided_at AS DecidedAt, decision_note AS DecisionNote
        """;

    public const string ReviewStep = "require_review";

    public async Task<(DocumentReviewTaskRecord Task, bool Created)> OpenAsync(DocumentReviewOpenRequest r, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var workflowKey = r.WorkflowVersionId ?? r.WorkflowId;
        // ON CONFLICT on the partial unique index: repeated and concurrent
        // runs converge on ONE open task.
        var created = await conn.QuerySingleOrDefaultAsync<DocumentReviewTaskRecord>(new CommandDefinition($"""
            INSERT INTO document_review_tasks(tenant_id, department_id, voucher_id, agent_id, agent_version_id,
                workflow_id, workflow_version_id, workflow_key, review_step, document_type, reasons, created_by)
            SELECT @TenantId, @DepartmentId, @VoucherId, @AgentId, @AgentVersionId, @WorkflowId, @WorkflowVersionId,
                @workflowKey, @step, @DocumentType, @reasons, @ActorUserId
            WHERE EXISTS (SELECT 1 FROM vouchers WHERE id=@VoucherId AND tenant_id=@TenantId AND department_id=@DepartmentId)
            ON CONFLICT (tenant_id, department_id, voucher_id, workflow_key, review_step) WHERE status = 'open' DO NOTHING
            RETURNING {Columns}
            """, new
        {
            r.TenantId, r.DepartmentId, r.VoucherId, r.AgentId, r.AgentVersionId, r.WorkflowId, r.WorkflowVersionId,
            workflowKey, step = ReviewStep, r.DocumentType, reasons = r.Reasons.ToArray(), r.ActorUserId,
        }, tx, cancellationToken: ct));
        var task = created ?? await conn.QuerySingleOrDefaultAsync<DocumentReviewTaskRecord>(new CommandDefinition($"""
            SELECT {Columns} FROM document_review_tasks
            WHERE tenant_id=@TenantId AND department_id=@DepartmentId AND voucher_id=@VoucherId
              AND workflow_key=@workflowKey AND review_step=@step AND status='open'
            """, new { r.TenantId, r.DepartmentId, r.VoucherId, workflowKey, step = ReviewStep }, tx, cancellationToken: ct))
            ?? throw new InvalidOperationException("review_task_creation_failed");
        var gate = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE vouchers SET document_review_status=@status WHERE id=@VoucherId AND tenant_id=@TenantId AND department_id=@DepartmentId",
            new { r.VoucherId, r.TenantId, r.DepartmentId, status = DocumentReviewStatuses.AwaitingReview }, tx, cancellationToken: ct));
        if (gate != 1) throw new InvalidOperationException("review_task_creation_failed");
        await tx.CommitAsync(ct);
        return (task, created is not null);
    }

    public async Task MarkCreationFailedAsync(Guid tenantId, Guid voucherId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE vouchers SET document_review_status=@status WHERE id=@voucherId AND tenant_id=@tenantId",
            new { voucherId, tenantId, status = DocumentReviewStatuses.CreationFailed }, cancellationToken: ct));
        if (n != 1) throw new InvalidOperationException("review_gate_not_recorded");
    }

    public async Task<string?> GetGateStatusAsync(Guid tenantId, Guid voucherId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT document_review_status FROM vouchers WHERE id=@voucherId AND tenant_id=@tenantId",
            new { voucherId, tenantId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<string>> GetBlockingReasonsAsync(Guid tenantId, Guid voucherId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await DocumentAccountingGate.LoadReasonsAsync(conn, null, tenantId, voucherId, ct) ?? [];
    }

    public async Task<IReadOnlyList<DocumentReviewTaskRecord>?> ListForVoucherAsync(Guid tenantId, Guid departmentId, Guid voucherId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var visible = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM vouchers WHERE id=@voucherId AND tenant_id=@tenantId AND department_id=@departmentId)",
            new { voucherId, tenantId, departmentId }, cancellationToken: ct));
        if (!visible) return null;
        return (await conn.QueryAsync<DocumentReviewTaskRecord>(new CommandDefinition($"""
            SELECT {Columns} FROM document_review_tasks
            WHERE tenant_id=@tenantId AND department_id=@departmentId AND voucher_id=@voucherId
            ORDER BY created_at DESC
            """, new { tenantId, departmentId, voucherId }, cancellationToken: ct))).AsList();
    }

    public async Task<DocumentReviewTaskRecord?> DecideAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid taskId, Guid actorId,
        string decision, string? note, CancellationToken ct)
    {
        if (decision is not (DocumentReviewStatuses.Approved or DocumentReviewStatuses.Rejected))
            throw new ArgumentException("decision must be approved or rejected", nameof(decision));
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var decided = await conn.QuerySingleOrDefaultAsync<DocumentReviewTaskRecord>(new CommandDefinition($"""
            UPDATE document_review_tasks SET status=@decision, decided_by=@actorId, decided_at=NOW(), decision_note=@note
            WHERE id=@taskId AND voucher_id=@voucherId AND tenant_id=@tenantId AND department_id=@departmentId AND status='open'
            RETURNING {Columns}
            """, new { taskId, voucherId, tenantId, departmentId, actorId, decision, note }, tx, cancellationToken: ct));
        if (decided is null) { await tx.RollbackAsync(ct); return null; }
        // A rejection blocks the document; an approval releases the gate only
        // when no other review on it is still open.
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE vouchers SET document_review_status = CASE
                WHEN @decision = 'rejected' THEN 'rejected'
                WHEN EXISTS (SELECT 1 FROM document_review_tasks t WHERE t.voucher_id=@voucherId AND t.status='open') THEN 'awaiting_review'
                ELSE 'approved' END
            WHERE id=@voucherId AND tenant_id=@tenantId AND department_id=@departmentId
            """, new { voucherId, tenantId, departmentId, decision }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return decided;
    }
}
