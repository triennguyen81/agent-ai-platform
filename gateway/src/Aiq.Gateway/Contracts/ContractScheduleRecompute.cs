using Dapper;
using Npgsql;

namespace Aiq.Gateway.Contracts;

/// <summary>What one recompute changed on a contract payment schedule (đợt thanh toán).</summary>
public sealed record ContractScheduleChange(
    Guid ContractId, Guid ScheduleId, int Ordinal, string FromStatus, string ToStatus,
    decimal PaidAmount, decimal ScheduleTotal, decimal ProgressBefore, decimal ProgressAfter);

/// <summary>Read model: a schedule reached through an invoice that a settlement touched.</summary>
public sealed record ContractScheduleImpact(
    Guid ContractId, string? ContractNumber, string? ContractTitle, decimal ContractProgressPercent,
    Guid ScheduleId, int Ordinal, string Description, DateOnly? DueDate,
    decimal ScheduleTotal, decimal PaidAmount, string Status,
    IReadOnlyList<ContractScheduleInvoice> Invoices);

public sealed record ContractScheduleInvoice(
    Guid InvoiceId, string DocumentNumber, decimal OriginalAmount, decimal PaidAmount, string InvoiceStatus, decimal AppliedByThisDocument);

/// <summary>
/// The one place contract payment schedule state is derived from accounting.
///
/// Authoritative chain: payment_applications -> AR invoice (sim_ar_ledger
/// open item: debit / paid_amount) -> its explicit contract_payment_schedule_id
/// -> schedule status -> contract progress. A schedule is only ever reached
/// through an invoice explicitly linked to it — never through the customer,
/// its tax code, or "the earliest open schedule".
///
/// Pure recompute, never an increment: running it twice with no accounting
/// change leaves the DB identical. Runs inside the caller's transaction, so
/// a failure here rolls back the accounting mutation that triggered it.
/// </summary>
public static class ContractScheduleRecompute
{
    public const string Pending = "pending";
    public const string Partial = "partial";
    public const string Paid = "paid";

    /// <summary>
    /// paid = Σ min(paid_amount, debit) over every booked invoice linked to the
    /// schedule. Nothing paid -> pending; paid reaches the schedule total ->
    /// paid; otherwise partial. One invoice being PAID never makes the schedule
    /// PAID by itself when other linked invoices / the rest of the total are open.
    /// </summary>
    public static string DeriveStatus(decimal paidAmount, decimal scheduleTotal) =>
        paidAmount <= 0m ? Pending
        : paidAmount >= scheduleTotal ? Paid
        : Partial;

    private sealed record ScheduleRow(Guid Id, Guid ContractId, Guid TenantId, Guid DepartmentId, int Ordinal,
        decimal TotalAmount, string Status, decimal PaidAmount);

    /// <summary>
    /// Recomputes every schedule linked to <paramref name="arInvoiceIds"/> and the
    /// progress of their contracts. Invoices without a schedule link change nothing.
    /// </summary>
    public static async Task<IReadOnlyList<ContractScheduleChange>> RecomputeForInvoicesAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, IReadOnlyCollection<Guid> arInvoiceIds,
        Guid? actorId, string cause, Guid? cashTransactionId, CancellationToken ct)
    {
        if (arInvoiceIds.Count == 0) return Array.Empty<ContractScheduleChange>();
        var ids = arInvoiceIds.Distinct().ToArray();

        var scheduleIds = (await conn.QueryAsync<Guid>(new CommandDefinition("""
            SELECT DISTINCT contract_payment_schedule_id FROM sim_ar_ledger
            WHERE id = ANY(@ids) AND tenant_id=@tenantId AND document_type='invoice'
              AND contract_payment_schedule_id IS NOT NULL
            """, new { ids, tenantId }, tx, cancellationToken: ct))).ToArray();
        // No linked invoice -> no contract is touched (an active contract of the
        // same customer is irrelevant).
        if (scheduleIds.Length == 0) return Array.Empty<ContractScheduleChange>();

        // Lock order: contracts, then schedules (both by id). Taking the contract
        // lock first serialises two settlements on different schedules of the same
        // contract, so the progress recompute below never misses the other's change.
        var contractIds = (await conn.QueryAsync<Guid>(new CommandDefinition("""
            SELECT c.id FROM contracts c
            WHERE c.tenant_id=@tenantId
              AND c.id IN (SELECT contract_id FROM contract_payment_schedules WHERE id = ANY(@scheduleIds))
            ORDER BY c.id FOR UPDATE OF c
            """, new { tenantId, scheduleIds }, tx, cancellationToken: ct))).ToArray();
        await conn.ExecuteAsync(new CommandDefinition("""
            SELECT id FROM contract_payment_schedules WHERE id = ANY(@scheduleIds) AND tenant_id=@tenantId ORDER BY id FOR UPDATE
            """, new { scheduleIds, tenantId }, tx, cancellationToken: ct));

        var schedules = (await conn.QueryAsync<ScheduleRow>(new CommandDefinition("""
            SELECT s.id AS Id, s.contract_id AS ContractId, s.tenant_id AS TenantId, s.department_id AS DepartmentId,
                   s.ordinal AS Ordinal, s.total_amount AS TotalAmount, s.status AS Status,
                   COALESCE((SELECT SUM(LEAST(GREATEST(l.paid_amount, 0), COALESCE(l.debit, 0)))
                             FROM sim_ar_ledger l
                             WHERE l.contract_payment_schedule_id = s.id AND l.tenant_id = s.tenant_id
                               AND l.document_type = 'invoice'), 0) AS PaidAmount
            FROM contract_payment_schedules s
            WHERE s.id = ANY(@scheduleIds) AND s.tenant_id=@tenantId
            ORDER BY s.contract_id, s.ordinal
            """, new { scheduleIds, tenantId }, tx, cancellationToken: ct))).ToList();

        var progressBefore = (await conn.QueryAsync<(Guid Id, decimal Progress)>(new CommandDefinition(
            "SELECT id, progress_percent FROM contracts WHERE id = ANY(@contractIds)",
            new { contractIds }, tx, cancellationToken: ct))).ToDictionary(r => r.Id, r => r.Progress);

        var changed = new List<(ScheduleRow Row, string To)>();
        foreach (var s in schedules)
        {
            var to = DeriveStatus(s.PaidAmount, s.TotalAmount);
            if (to == s.Status) continue;
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE contract_payment_schedules SET status=@to WHERE id=@id",
                new { to, id = s.Id }, tx, cancellationToken: ct));
            changed.Add((s, to));

            // Existing product rule (paying đợt N activates mốc N+1): only a
            // still-pending, trigger-based milestone without a fixed date, so it
            // is idempotent. A later un-pay does NOT revoke an activated milestone
            // (no confirmed business rule for that yet).
            if (to == Paid)
            {
                await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE contract_milestones SET
                        status = 'active',
                        expected_date = CASE
                            WHEN duration_months IS NOT NULL
                            THEN (CURRENT_DATE + (duration_months::text || ' months')::interval)::date
                            ELSE expected_date
                        END
                    WHERE contract_id=@contractId AND tenant_id=@tenantId AND department_id=@departmentId
                      AND ordinal=@nextOrdinal AND status='pending' AND trigger_event IS NOT NULL AND due_date IS NULL
                    """, new { contractId = s.ContractId, tenantId, departmentId = s.DepartmentId, nextOrdinal = s.Ordinal + 1 },
                    tx, cancellationToken: ct));
            }
        }

        // Existing progress semantics (migration 042): Σ percentage of PAID
        // schedules — a partial schedule contributes nothing. Full recompute.
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE contracts c SET progress_percent = p.progress
            FROM (
                SELECT c2.id, COALESCE((SELECT SUM(s.percentage) FROM contract_payment_schedules s
                                        WHERE s.contract_id = c2.id AND s.tenant_id = c2.tenant_id
                                          AND s.department_id = c2.department_id AND s.status = 'paid'), 0) AS progress
                FROM contracts c2 WHERE c2.id = ANY(@contractIds)
            ) p
            WHERE c.id = p.id AND c.progress_percent IS DISTINCT FROM p.progress
            """, new { contractIds }, tx, cancellationToken: ct));

        var progressAfter = (await conn.QueryAsync<(Guid Id, decimal Progress)>(new CommandDefinition(
            "SELECT id, progress_percent FROM contracts WHERE id = ANY(@contractIds)",
            new { contractIds }, tx, cancellationToken: ct))).ToDictionary(r => r.Id, r => r.Progress);

        var result = new List<ContractScheduleChange>();
        foreach (var (s, to) in changed)
        {
            var change = new ContractScheduleChange(s.ContractId, s.Id, s.Ordinal, s.Status, to, s.PaidAmount, s.TotalAmount,
                progressBefore.GetValueOrDefault(s.ContractId), progressAfter.GetValueOrDefault(s.ContractId));
            result.Add(change);
            // Existing audit trail: attributable to the settlement that caused it.
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO contract_history (contract_id, tenant_id, department_id, actor_id, event_type, from_state, to_state, details)
                VALUES (@contractId, @tenantId, @departmentId, @actorId, 'payment_schedule_status', @from, @to, @details::jsonb)
                """, new
            {
                contractId = s.ContractId, tenantId, departmentId = s.DepartmentId, actorId,
                from = s.Status, to,
                details = System.Text.Json.JsonSerializer.Serialize(new
                {
                    schedule_id = s.Id, ordinal = s.Ordinal, paid_amount = s.PaidAmount, schedule_total = s.TotalAmount,
                    cause, cash_transaction_id = cashTransactionId, invoice_ids = ids,
                    progress_before = change.ProgressBefore, progress_after = change.ProgressAfter,
                }),
            }, tx, cancellationToken: ct));
        }
        return result;
    }

    private sealed record ImpactRow(Guid ContractId, string? ContractNumber, string? ContractTitle, decimal Progress,
        Guid ScheduleId, int Ordinal, string Description, DateOnly? DueDate, decimal ScheduleTotal, string Status,
        Guid InvoiceId, string DocumentNumber, decimal Original, decimal InvoicePaid, string InvoiceStatus, decimal SchedulePaid);

    /// <summary>
    /// Read-only view for the settlement panels: the schedules of the invoices a
    /// document touched, reached only through each invoice's own schedule link.
    /// </summary>
    public static async Task<IReadOnlyList<ContractScheduleImpact>> LoadImpactsAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, Guid departmentId,
        IReadOnlyDictionary<Guid, decimal> appliedByInvoice, CancellationToken ct)
    {
        if (appliedByInvoice.Count == 0) return Array.Empty<ContractScheduleImpact>();
        var ids = appliedByInvoice.Keys.ToArray();
        var rows = (await conn.QueryAsync<ImpactRow>(new CommandDefinition("""
            SELECT c.id AS ContractId, COALESCE(c.contract_number, c.contract_code) AS ContractNumber, c.title AS ContractTitle,
                   c.progress_percent AS Progress,
                   s.id AS ScheduleId, s.ordinal AS Ordinal, s.description AS Description, s.due_date AS DueDate,
                   s.total_amount AS ScheduleTotal, s.status AS Status,
                   l.id AS InvoiceId, l.document_number AS DocumentNumber, l.debit AS Original, l.paid_amount AS InvoicePaid,
                   l.status AS InvoiceStatus,
                   COALESCE((SELECT SUM(LEAST(GREATEST(x.paid_amount, 0), COALESCE(x.debit, 0))) FROM sim_ar_ledger x
                             WHERE x.contract_payment_schedule_id = s.id AND x.tenant_id = s.tenant_id
                               AND x.document_type = 'invoice'), 0) AS SchedulePaid
            FROM sim_ar_ledger l
            JOIN contract_payment_schedules s ON s.id = l.contract_payment_schedule_id AND s.tenant_id = l.tenant_id
            JOIN contracts c ON c.id = s.contract_id AND c.tenant_id = s.tenant_id
            WHERE l.id = ANY(@ids) AND l.tenant_id=@tenantId AND l.document_type='invoice'
              AND s.department_id=@departmentId AND c.department_id=@departmentId
            ORDER BY c.id, s.ordinal, l.document_number
            """, new { ids, tenantId, departmentId }, tx, cancellationToken: ct))).ToList();

        return rows.GroupBy(r => r.ScheduleId).Select(g =>
        {
            var f = g.First();
            return new ContractScheduleImpact(f.ContractId, f.ContractNumber, f.ContractTitle, f.Progress,
                f.ScheduleId, f.Ordinal, f.Description, f.DueDate, f.ScheduleTotal, f.SchedulePaid, f.Status,
                g.Select(r => new ContractScheduleInvoice(r.InvoiceId, r.DocumentNumber, r.Original, r.InvoicePaid, r.InvoiceStatus,
                    appliedByInvoice.GetValueOrDefault(r.InvoiceId))).ToList());
        }).ToList();
    }
}
