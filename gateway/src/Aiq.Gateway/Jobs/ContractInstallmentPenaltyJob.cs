using Dapper;
using Hangfire;

namespace Aiq.Gateway.Jobs;

/// <summary>
/// P2 audit fix (ACCAGENT_PROCESS_AUDIT_REPORT.md — late-payment penalty
/// never computed despite contracts.penalty_percent already being
/// captured). Daily 07:00 VN scan of contract_payment_schedules.
///
/// Deliberately scoped to the one case where every fact the business
/// process document requires is genuinely unambiguous:
///   - a real due_date that has actually passed;
///   - status='pending' ONLY — never 'partial'. A partially-paid
///     installment's remaining unpaid balance is not tracked anywhere in
///     this schema (contract_payment_schedules has no paid-amount column,
///     only a status enum), so "overdue principal" for a partial
///     installment is not knowable without guessing — exactly the
///     "ambiguous contract terms" case the audit says must stay a review
///     item, not a guessed calculation;
///   - the contract's own penalty_percent actually set (non-null, > 0).
///   - the contract is APPROVED/HANDED_OFF: a draft / under-review /
///     rejected contract's terms are not authoritative, so no penalty is
///     computed for it (and a stale one is cleared).
///
/// Recomputed as a pure function of (total_amount, penalty_percent,
/// days_overdue) every run — never an accumulator — so a retry or a
/// skipped day never double-charges, and an installment that stops being
/// overdue (paid, or its due_date/contract terms changed) has its penalty
/// fields cleared rather than left stale.
/// </summary>
public sealed class ContractInstallmentPenaltyJob
{
    public const string RecurringJobId = "contract-installment-penalty-scan";

    private readonly Aiq.Gateway.Data.NpgsqlConnectionFactory _factory;
    private readonly ILogger<ContractInstallmentPenaltyJob> _logger;

    public ContractInstallmentPenaltyJob(Aiq.Gateway.Data.NpgsqlConnectionFactory factory, ILogger<ContractInstallmentPenaltyJob> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    [JobDisplayName("Contract installment penalty scan")]
    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task RunAsync(CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var cleared = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE contract_payment_schedules s SET
                penalty_overdue_amount = NULL, penalty_overdue_days = NULL,
                penalty_rate = NULL, penalty_amount = NULL, penalty_calculated_at = NULL
            WHERE s.penalty_calculated_at IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM contracts c
                  WHERE c.id = s.contract_id
                    AND c.workflow_state IN ('APPROVED', 'HANDED_OFF')
                    AND s.status = 'pending'
                    AND s.due_date IS NOT NULL AND s.due_date < CURRENT_DATE
                    AND c.penalty_percent IS NOT NULL AND c.penalty_percent > 0
              )
            """, cancellationToken: ct));

        var recomputedIds = (await conn.QueryAsync<Guid>(new CommandDefinition("""
            UPDATE contract_payment_schedules s SET
                penalty_overdue_amount = s.total_amount,
                penalty_overdue_days = (CURRENT_DATE - s.due_date),
                penalty_rate = c.penalty_percent,
                penalty_amount = ROUND(s.total_amount * (c.penalty_percent / 100.0) * (CURRENT_DATE - s.due_date), 2),
                penalty_calculated_at = NOW()
            FROM contracts c
            WHERE c.id = s.contract_id
              AND c.workflow_state IN ('APPROVED', 'HANDED_OFF')
              AND s.status = 'pending'
              AND s.due_date IS NOT NULL AND s.due_date < CURRENT_DATE
              AND c.penalty_percent IS NOT NULL AND c.penalty_percent > 0
              AND s.total_amount IS NOT NULL
            RETURNING s.id;
            """, cancellationToken: ct))).AsList();

        _logger.LogInformation(
            "Contract installment penalty scan: recomputed {Recomputed}, cleared {Cleared}",
            recomputedIds.Count, cleared);
    }
}
