using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Reports;

/// <summary>
/// P2 audit fix (ACCAGENT_PROCESS_AUDIT_REPORT.md — "Ban giám đốc weekly
/// cash-flow/aging report" missing). Tenant-wide (not Department-scoped —
/// Ban giám đốc oversees the whole company, matching the PDF's own framing),
/// read-only, computed on demand from data that already exists: no new
/// tracking, no fabricated figures.
/// </summary>
public sealed record DepartmentExpenseTotal(Guid DepartmentId, string DepartmentName, decimal TotalApproved);

public sealed record WeeklySummary(
    decimal CashCollectedThisWeek,
    decimal TotalOpenReceivables,
    decimal OverdueReceivables,
    int OverdueCustomerCount,
    IReadOnlyList<DepartmentExpenseTotal> ExpenseByDepartment,
    DateOnly PeriodStart,
    DateOnly PeriodEnd);

public interface IWeeklySummaryReportRepository
{
    Task<WeeklySummary> GetAsync(Guid tenantId, CancellationToken ct);
}

public sealed class WeeklySummaryReportRepository : IWeeklySummaryReportRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public WeeklySummaryReportRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<WeeklySummary> GetAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);

        // "Dòng tiền thu" — money actually POSTED as received in the last 7
        // days (cash_transactions, migration 051), whichever invoice(s) it was
        // allocated to, including any still-unallocated portion — that cash
        // genuinely arrived. A voided (reversed) posting no longer counts.
        var cashCollected = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition("""
            SELECT COALESCE(SUM(amount), 0)
            FROM cash_transactions
            WHERE tenant_id=@tenantId AND direction='money_in' AND status='posted'
              AND posted_at >= NOW() - INTERVAL '7 days'
            """, new { tenantId }, cancellationToken: ct));

        // "Tuổi nợ" / "nhóm khách quá hạn" — open/partial invoices by their
        // REMAINING amount (settlement moves paid_amount, never credit), and how
        // many distinct customers have at least one past its due_date.
        var receivables = await conn.QuerySingleAsync<(decimal Total, decimal Overdue, int OverdueCustomers)>(new CommandDefinition("""
            SELECT
                COALESCE(SUM(debit - paid_amount), 0) AS Total,
                COALESCE(SUM(CASE WHEN due_date < CURRENT_DATE THEN debit - paid_amount ELSE 0 END), 0) AS Overdue,
                COUNT(DISTINCT CASE WHEN due_date < CURRENT_DATE THEN customer_id END) AS OverdueCustomers
            FROM sim_ar_ledger
            WHERE tenant_id=@tenantId AND document_type='invoice' AND status IN ('open','partial')
            """, new { tenantId }, cancellationToken: ct));

        // "Chi phí theo bộ phận" — claims that actually completed the full
        // 3-stage chain in the last 7 days, by the Department that owns them.
        var byDepartment = (await conn.QueryAsync<DepartmentExpenseTotal>(new CommandDefinition("""
            SELECT d.id AS DepartmentId, d.name AS DepartmentName, COALESCE(SUM(e.approved_amount), 0) AS TotalApproved
            FROM departments d
            LEFT JOIN expense_claim_results e
                ON e.department_id = d.id AND e.review_status = 'confirmed' AND e.updated_at >= NOW() - INTERVAL '7 days'
            WHERE d.tenant_id = @tenantId
            GROUP BY d.id, d.name
            ORDER BY d.name
            """, new { tenantId }, cancellationToken: ct))).AsList();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return new WeeklySummary(
            cashCollected, receivables.Total, receivables.Overdue, receivables.OverdueCustomers,
            byDepartment, today.AddDays(-7), today);
    }
}
