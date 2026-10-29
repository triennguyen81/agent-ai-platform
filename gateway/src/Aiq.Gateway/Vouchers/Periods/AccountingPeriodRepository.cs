using System.Text.Json;
using Aiq.Gateway.Data;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Vouchers.Periods;

public static class AccountingPeriodErrors
{
    /// <summary>SQLSTATE raised by accounting_period_assert_open (migration 071).</summary>
    public const string SqlState = "AQ001";
    public const string PeriodLocked = "period_locked";
    public const string AlreadyClosed = "period_already_closed";
    public const string NotClosed = "period_not_closed";
    public const string PreviousOpen = "period_previous_open";
    public const string HasUnposted = "period_has_unposted";
    public const string InFuture = "period_in_future";
    public const string ReasonRequired = "reason_required";
    public const string NextPeriodClosed = "next_period_closed";

    /// <summary>The period of a <c>period_locked:yyyy-MM</c> database error; null for any other error.</summary>
    public static string? LockedPeriodOf(Exception ex) =>
        ex is PostgresException { SqlState: SqlState } pg && pg.MessageText.StartsWith(PeriodLocked + ":", StringComparison.Ordinal)
            ? pg.MessageText[(PeriodLocked.Length + 1)..]
            : null;
}

public sealed record AccountingPeriodRecord(
    string Period, string Status, Guid? ClosedBy, DateTime? ClosedAt, Guid? ReopenedBy, DateTime? ReopenedAt, string? ReopenReason);

public sealed record AccountingPeriodEventRecord(
    Guid Id, string Period, string EventType, string ActorType, Guid? ActorId, string? Reason, string Details, string? RequestId, DateTime CreatedAt);

/// <summary>Who closes / reopens: actor type for the history, the user, the request.</summary>
public sealed record PeriodActor(string Type, Guid UserId, string? RequestId);

public sealed record PeriodWarning(string Code, string Period);

/// <summary>A close / reopen as applied (Error null) or refused (Error set, with what refused it).</summary>
public sealed record PeriodChangeResult(
    AccountingPeriodRecord? Period, string? Error = null, string? Previous = null,
    IReadOnlyList<Guid>? RefusingVoucherIds = null, IReadOnlyList<PeriodWarning>? PeriodWarnings = null)
{
    public IReadOnlyList<Guid> VoucherIds => RefusingVoucherIds ?? [];
    public IReadOnlyList<PeriodWarning> Warnings => PeriodWarnings ?? [];
    public static PeriodChangeResult Refused(string error, string? previous = null, IReadOnlyList<Guid>? voucherIds = null) =>
        new(null, error, previous, voucherIds);
}

public interface IAccountingPeriodRepository
{
    /// <summary>The twelve months of <paramref name="year"/>; a month without a row is open (QĐ 13).</summary>
    Task<IReadOnlyList<AccountingPeriodRecord>> ListAsync(Guid tenantId, Guid departmentId, int year, CancellationToken ct);

    Task<IReadOnlySet<AccountingPeriod>> ClosedAsync(Guid tenantId, Guid departmentId, CancellationToken ct);

    /// <summary>
    /// P4, P5, P10, P13: closes the month unless it is already closed, in the
    /// future of <paramref name="current"/>, preceded by an open month that has
    /// vouchers, or still holding vouchers in pending_approval / approved.
    /// The period row is held FOR UPDATE for the whole check, so no v2 write of
    /// that month (which holds it FOR SHARE) can interleave.
    /// </summary>
    Task<PeriodChangeResult> CloseAsync(Guid tenantId, Guid departmentId, AccountingPeriod period, PeriodActor actor,
        AccountingPeriod current, CancellationToken ct);

    /// <summary>QĐ 12, P6: reopening needs a reason; warns when the next month is closed.</summary>
    Task<PeriodChangeResult> ReopenAsync(Guid tenantId, Guid departmentId, AccountingPeriod period, PeriodActor actor,
        string? reason, CancellationToken ct);

    Task<IReadOnlyList<AccountingPeriodEventRecord>> ListEventsAsync(Guid tenantId, Guid departmentId, AccountingPeriod period, CancellationToken ct);
}

public sealed class AccountingPeriodRepository(NpgsqlConnectionFactory factory) : IAccountingPeriodRepository
{
    private const string Columns = """
        to_char(make_date(year, month, 1), 'YYYY-MM') AS Period, status AS Status, closed_by AS ClosedBy, closed_at AS ClosedAt,
        reopened_by AS ReopenedBy, reopened_at AS ReopenedAt, reopen_reason AS ReopenReason
        """;

    public async Task<IReadOnlyList<AccountingPeriodRecord>> ListAsync(Guid tenantId, Guid departmentId, int year, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = (await conn.QueryAsync<AccountingPeriodRecord>(new CommandDefinition($"""
            SELECT {Columns} FROM accounting_periods
             WHERE tenant_id = @tenantId AND department_id = @departmentId AND year = @year
            """, new { tenantId, departmentId, year }, cancellationToken: ct))).ToDictionary(r => r.Period);
        return Enumerable.Range(1, 12)
            .Select(m => new AccountingPeriod(year, m).ToString())
            .Select(p => rows.TryGetValue(p, out var r) ? r : new AccountingPeriodRecord(p, "open", null, null, null, null, null))
            .ToList();
    }

    public async Task<IReadOnlySet<AccountingPeriod>> ClosedAsync(Guid tenantId, Guid departmentId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<(int Year, int Month)>(new CommandDefinition("""
            SELECT year, month FROM accounting_periods
             WHERE tenant_id = @tenantId AND department_id = @departmentId AND status = 'closed'
            """, new { tenantId, departmentId }, cancellationToken: ct));
        return rows.Select(r => new AccountingPeriod(r.Year, r.Month)).ToHashSet();
    }

    public async Task<PeriodChangeResult> CloseAsync(Guid tenantId, Guid departmentId, AccountingPeriod period, PeriodActor actor,
        AccountingPeriod current, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var result = await CloseAsync(conn, tx, tenantId, departmentId, period, actor, current, ct);
        if (result.Error is null) await tx.CommitAsync(ct);
        else await tx.RollbackAsync(ct);
        return result;
    }

    /// <summary>The close inside the caller's transaction (tests hold it open to race a write).</summary>
    internal async Task<PeriodChangeResult> CloseAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid departmentId,
        AccountingPeriod period, PeriodActor actor, AccountingPeriod current, CancellationToken ct)
    {
        if (period > current) return PeriodChangeResult.Refused(AccountingPeriodErrors.InFuture);

        var (periodId, status) = await LockAsync(conn, tx, tenantId, departmentId, period, ct);
        if (status == "closed") return PeriodChangeResult.Refused(AccountingPeriodErrors.AlreadyClosed);

        var previous = period.Previous;
        var previousClosed = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM accounting_periods
                            WHERE tenant_id = @tenantId AND department_id = @departmentId
                              AND year = @Year AND month = @Month AND status = 'closed')
            """, new { tenantId, departmentId, previous.Year, previous.Month }, tx, cancellationToken: ct));
        if (!previousClosed && (await VouchersOfAsync(conn, tx, tenantId, departmentId, previous, onlyUnposted: false, limit: 1, ct)).Count > 0)
            return PeriodChangeResult.Refused(AccountingPeriodErrors.PreviousOpen, previous: previous.ToString());

        var unposted = await VouchersOfAsync(conn, tx, tenantId, departmentId, period, onlyUnposted: true, limit: 200, ct);
        if (unposted.Count > 0) return PeriodChangeResult.Refused(AccountingPeriodErrors.HasUnposted, voucherIds: unposted);

        var record = await conn.QuerySingleAsync<AccountingPeriodRecord>(new CommandDefinition($"""
            UPDATE accounting_periods SET status = 'closed', closed_by = @userId, closed_at = clock_timestamp(), updated_at = NOW()
             WHERE id = @periodId
            RETURNING {Columns}
            """, new { periodId, userId = actor.UserId }, tx, cancellationToken: ct));
        await LogAsync(conn, tx, tenantId, departmentId, periodId, period, "period_closed", actor, null, new { }, ct);
        return new PeriodChangeResult(record);
    }

    public async Task<PeriodChangeResult> ReopenAsync(Guid tenantId, Guid departmentId, AccountingPeriod period, PeriodActor actor,
        string? reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason)) return PeriodChangeResult.Refused(AccountingPeriodErrors.ReasonRequired);
        reason = reason.Trim();

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (periodId, status) = await LockAsync(conn, tx, tenantId, departmentId, period, ct);
        if (status != "closed")
        {
            await tx.RollbackAsync(ct);
            return PeriodChangeResult.Refused(AccountingPeriodErrors.NotClosed);
        }

        var record = await conn.QuerySingleAsync<AccountingPeriodRecord>(new CommandDefinition($"""
            UPDATE accounting_periods SET status = 'open', reopened_by = @userId, reopened_at = clock_timestamp(),
                                          reopen_reason = @reason, updated_at = NOW()
             WHERE id = @periodId
            RETURNING {Columns}
            """, new { periodId, userId = actor.UserId, reason }, tx, cancellationToken: ct));
        var next = period.Next;
        var warnings = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM accounting_periods
                            WHERE tenant_id = @tenantId AND department_id = @departmentId
                              AND year = @Year AND month = @Month AND status = 'closed')
            """, new { tenantId, departmentId, next.Year, next.Month }, tx, cancellationToken: ct))
            ? new[] { new PeriodWarning(AccountingPeriodErrors.NextPeriodClosed, next.ToString()) }
            : [];
        await LogAsync(conn, tx, tenantId, departmentId, periodId, period, "period_reopened", actor, reason, new { warnings }, ct);
        await tx.CommitAsync(ct);
        return new PeriodChangeResult(record, PeriodWarnings: warnings);
    }

    public async Task<IReadOnlyList<AccountingPeriodEventRecord>> ListEventsAsync(Guid tenantId, Guid departmentId, AccountingPeriod period,
        CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<AccountingPeriodEventRecord>(new CommandDefinition("""
            SELECT id AS Id, to_char(make_date(year, month, 1), 'YYYY-MM') AS Period, event_type AS EventType, actor_type AS ActorType,
                   actor_id AS ActorId, reason AS Reason, details::text AS Details, request_id AS RequestId, created_at AS CreatedAt
              FROM accounting_period_events
             WHERE tenant_id = @tenantId AND department_id = @departmentId AND year = @Year AND month = @Month
             ORDER BY created_at, id
            """, new { tenantId, departmentId, period.Year, period.Month }, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>The period row, created open if missing (QĐ 13), held FOR UPDATE until the transaction ends.</summary>
    private static async Task<(Guid Id, string Status)> LockAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        Guid tenantId, Guid departmentId, AccountingPeriod period, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO accounting_periods (tenant_id, department_id, year, month) VALUES (@tenantId, @departmentId, @Year, @Month)
            ON CONFLICT (tenant_id, department_id, year, month) DO NOTHING
            """, new { tenantId, departmentId, period.Year, period.Month }, tx, cancellationToken: ct));
        return await conn.QuerySingleAsync<(Guid, string)>(new CommandDefinition("""
            SELECT id, status FROM accounting_periods
             WHERE tenant_id = @tenantId AND department_id = @departmentId AND year = @Year AND month = @Month
               FOR UPDATE
            """, new { tenantId, departmentId, period.Year, period.Month }, tx, cancellationToken: ct));
    }

    /// <summary>Live vouchers of the department whose accounting date (P1) falls in the month.</summary>
    private static async Task<IReadOnlyList<Guid>> VouchersOfAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId,
        Guid departmentId, AccountingPeriod period, bool onlyUnposted, int limit, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<Guid>(new CommandDefinition("""
            SELECT id FROM vouchers
             WHERE tenant_id = @tenantId AND department_id = @departmentId AND deleted_at IS NULL
               AND COALESCE(accounting_date, document_date) BETWEEN @first AND @last
               AND (NOT @onlyUnposted OR lifecycle_state IN ('pending_approval', 'approved'))
             ORDER BY created_at, id
             LIMIT @limit
            """, new { tenantId, departmentId, first = period.FirstDay, last = period.LastDay, onlyUnposted, limit },
            tx, cancellationToken: ct));
        return rows.AsList();
    }

    private static Task LogAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid departmentId, Guid periodId,
        AccountingPeriod period, string eventType, PeriodActor actor, string? reason, object details, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO accounting_period_events
                (tenant_id, period_id, department_id, year, month, event_type, actor_type, actor_id, reason, details, request_id)
            VALUES (@tenantId, @periodId, @departmentId, @Year, @Month, @eventType, @actorType, @actorId, @reason,
                    CAST(@details AS jsonb), NULLIF(@requestId, ''))
            """, new
        {
            tenantId, periodId, departmentId, period.Year, period.Month, eventType, actorType = actor.Type, actorId = actor.UserId,
            reason, details = JsonSerializer.Serialize(details, Json), requestId = actor.RequestId ?? "",
        }, tx, cancellationToken: ct));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
}
