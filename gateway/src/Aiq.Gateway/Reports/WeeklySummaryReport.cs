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

        // "Dòng tiền thu" — every receipt confirmed (settled) in the last 7
        // days, whichever invoice(s) it ended up allocated to. Includes the
        // unallocated portion of an overpayment too — that cash genuinely
        // arrived, even if which invoice it belongs to is still pending.
        var cashCollected = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition("""
            SELECT COALESCE(SUM(matched_amount + unallocated_amount), 0)
            FROM receipt_match_results
            WHERE tenant_id=@tenantId AND review_status='confirmed' AND updated_at >= NOW() - INTERVAL '7 days'
            """, new { tenantId }, cancellationToken: ct));

        // "Tuổi nợ" / "nhóm khách quá hạn" — open (unpaid) invoices, and how
        // many distinct customers have at least one past its due_date.
        var receivables = await conn.QuerySingleAsync<(decimal Total, decimal Overdue, int OverdueCustomers)>(new CommandDefinition("""
            SELECT
                COALESCE(SUM(debit - credit), 0) AS Total,
                COALESCE(SUM(CASE WHEN due_date < CURRENT_DATE THEN debit - credit ELSE 0 END), 0) AS Overdue,
                COUNT(DISTINCT CASE WHEN due_date < CURRENT_DATE THEN customer_id END) AS OverdueCustomers
            FROM sim_ar_ledger
            WHERE tenant_id=@tenantId AND document_type='invoice' AND (debit - credit) > 0
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
