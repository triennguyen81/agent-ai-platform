using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Vouchers.Lifecycle;

/// <summary>One lifecycle move as applied: who, why, and the request it came from.</summary>
public sealed record LifecycleChange(string From, string To, string ActorType, Guid? ActorId, string? Reason, string? RequestId);

public sealed record VoucherEventRecord(
    Guid Id, string EventType, string ActorType, Guid? ActorId, string? Before, string? After, string? Reason, DateTime CreatedAt);

public interface IVoucherLifecycleRepository
{
    /// <summary>
    /// Moves the voucher from <c>change.From</c> to <c>change.To</c> in one
    /// UPDATE guarded by the expected state; false when the voucher is not in
    /// this tenant + department, is deleted, or has already moved.
    /// </summary>
    Task<bool> TransitionAsync(Guid tenantId, Guid departmentId, Guid voucherId, LifecycleChange change, CancellationToken ct);

    Task<IReadOnlyList<VoucherEventRecord>> ListEventsAsync(Guid tenantId, Guid voucherId, CancellationToken ct);

    /// <summary>
    /// Soft delete (QĐ 3): sets deleted_at and logs a 'deleted' event in one
    /// transaction. False when the voucher is not deletable (locked, posted now
    /// or ever, already deleted) or not in this tenant + department.
    /// </summary>
    Task<bool> SoftDeleteAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid actorId, string? requestId, CancellationToken ct);
}

public sealed class VoucherLifecycleRepository(NpgsqlConnectionFactory factory) : IVoucherLifecycleRepository
{
    public async Task<bool> TransitionAsync(Guid tenantId, Guid departmentId, Guid voucherId, LifecycleChange change, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // Read by trg_vouchers_lifecycle_event, which writes the state_change
        // event in this same transaction (migration 070).
        await SetActorAsync(conn, tx, change.ActorType, change.ActorId, change.Reason, change.RequestId, ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE vouchers SET
                lifecycle_state = @to,
                submitted_by = CASE WHEN @to = 'pending_approval' THEN @actorId
                                    WHEN @to = 'needs_fix' THEN NULL ELSE submitted_by END,
                submitted_at = CASE WHEN @to = 'pending_approval' THEN NOW()
                                    WHEN @to = 'needs_fix' THEN NULL ELSE submitted_at END,
                approved_by  = CASE WHEN @to = 'approved' THEN @actorId
                                    WHEN @to = 'needs_fix' THEN NULL ELSE approved_by END,
                approved_at  = CASE WHEN @to = 'approved' THEN NOW()
                                    WHEN @to = 'needs_fix' THEN NULL ELSE approved_at END
             WHERE id = @voucherId AND tenant_id = @tenantId AND department_id = @departmentId
               AND lifecycle_state = @from AND deleted_at IS NULL
            """, new { voucherId, tenantId, departmentId, from = change.From, to = change.To, actorId = change.ActorId },
            tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return rows == 1;
    }

    public async Task<IReadOnlyList<VoucherEventRecord>> ListEventsAsync(Guid tenantId, Guid voucherId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<VoucherEventRecord>(new CommandDefinition("""
            SELECT id AS Id, event_type AS EventType, actor_type AS ActorType, actor_id AS ActorId,
                   before::text AS Before, after::text AS After, reason AS Reason, created_at AS CreatedAt
              FROM voucher_events WHERE tenant_id = @tenantId AND voucher_id = @voucherId
             ORDER BY created_at, id
            """, new { tenantId, voucherId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<bool> SoftDeleteAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid actorId, string? requestId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var state = await conn.ExecuteScalarAsync<string?>(new CommandDefinition($"""
            UPDATE vouchers SET deleted_at = NOW(), deleted_by = @actorId
             WHERE id = @voucherId AND tenant_id = @tenantId AND department_id = @departmentId
               AND deleted_at IS NULL AND posted_at IS NULL
               AND lifecycle_state IN ('draft', 'extracted', 'needs_fix', 'validated', 'rejected')
               AND {VoucherPostingGuard.NotPostedSql}
            RETURNING lifecycle_state
            """, new { voucherId, tenantId, departmentId, actorId }, tx, cancellationToken: ct));
        if (state is null)
        {
            await tx.RollbackAsync(ct);
            return false;
        }
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO voucher_events (tenant_id, voucher_id, event_type, actor_type, actor_id, before, after, request_id)
            VALUES (@tenantId, @voucherId, 'deleted', 'user', @actorId,
                    jsonb_build_object('lifecycle_state', @state), jsonb_build_object('deleted', true), NULLIF(@requestId, ''))
            """, new { tenantId, voucherId, actorId, state, requestId = requestId ?? "" }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return true;
    }

    internal static Task SetActorAsync(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx,
        string actorType, Guid? actorId, string? reason, string? requestId, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition("""
            SELECT set_config('aiq.actor_type', @actorType, true), set_config('aiq.actor_id', @actorId, true),
                   set_config('aiq.reason', @reason, true), set_config('aiq.request_id', @requestId, true)
            """, new { actorType, actorId = actorId?.ToString() ?? "", reason = reason?.Trim() ?? "", requestId = requestId ?? "" },
            tx, cancellationToken: ct));
}
