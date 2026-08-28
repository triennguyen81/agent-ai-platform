using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Employees;

// ============================================================================
// UC3 (Expense/AP - expense_claim leg): employee master + advance tracking.
// Feed `expense_claim_matching` enrichment cho voucher_type=expense_claim
// và Apply flow (Sprint 2+): outstanding_advance ↓, ytd_expense ↑.
//
// Semantics kế toán VN:
//   TK 141 (tạm ứng) — outstanding_advance
//   TK 6417/6428 (chi phí bán hàng/QL) — ytd_expense accumulator
// ============================================================================

public sealed record EmployeeRecord(
    Guid Id,
    string EmployeeCode,
    string FullName,
    string? Department,
    string? Position,
    string? Email,
    decimal MonthlyExpenseBudget,
    decimal YtdExpense,
    decimal OutstandingAdvance);

public sealed record ExpenseClaimApplyResult(
    decimal PreviousAdvance,
    decimal NewAdvance,
    decimal PreviousYtdExpense,
    decimal NewYtdExpense);

public interface IEmployeeRepository
{
    Task<EmployeeRecord?> FindByCodeAsync(Guid tenantId, string employeeCode, CancellationToken ct);
    Task<EmployeeRecord?> FindByIdAsync(Guid tenantId, Guid id, CancellationToken ct);

    // Áp dụng hoàn ứng: giảm outstanding_advance (tối đa về 0) + tăng ytd_expense.
    // Optional side-effect: đánh dấu sim_expense_claims.status='paid' nếu row
    // đã tồn tại (match by document_number + tenant).
    // Idempotent theo voucherId — chưa có ap_ledger-style guard, dùng flag trên
    // vouchers.status ở caller.
    Task<ExpenseClaimApplyResult> ApplyExpenseClaimAsync(
        Guid tenantId,
        Guid employeeId,
        string? claimDocumentNumber,
        decimal totalClaim,
        decimal advanceTaken,
        CancellationToken ct);
}

public sealed class EmployeeRepository : IEmployeeRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public EmployeeRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    private const string EmployeeCols = """
            id                     AS Id,
            employee_code          AS EmployeeCode,
            full_name              AS FullName,
            department             AS Department,
            position               AS Position,
            email                  AS Email,
            monthly_expense_budget AS MonthlyExpenseBudget,
            ytd_expense            AS YtdExpense,
            outstanding_advance    AS OutstandingAdvance
        """;

    public async Task<EmployeeRecord?> FindByCodeAsync(Guid tenantId, string employeeCode, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var sql = $"SELECT {EmployeeCols} FROM sim_employees WHERE tenant_id = @tenantId AND employee_code = @employeeCode LIMIT 1";
        return await conn.QueryFirstOrDefaultAsync<EmployeeRecord>(sql, new { tenantId, employeeCode });
    }

    public async Task<EmployeeRecord?> FindByIdAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var sql = $"SELECT {EmployeeCols} FROM sim_employees WHERE tenant_id = @tenantId AND id = @id LIMIT 1";
        return await conn.QueryFirstOrDefaultAsync<EmployeeRecord>(sql, new { tenantId, id });
    }

    public async Task<ExpenseClaimApplyResult> ApplyExpenseClaimAsync(
        Guid tenantId,
        Guid employeeId,
        string? claimDocumentNumber,
        decimal totalClaim,
        decimal advanceTaken,
        CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Snapshot với row lock để tránh concurrent apply.
        var snapshot = await conn.QueryFirstOrDefaultAsync<(decimal outstanding_advance, decimal ytd_expense)>(
            new CommandDefinition(
                "SELECT outstanding_advance, ytd_expense FROM sim_employees WHERE tenant_id = @tenantId AND id = @employeeId FOR UPDATE",
                new { tenantId, employeeId },
                transaction: tx,
                cancellationToken: ct));
        var prevAdvance = snapshot.outstanding_advance;
        var prevYtd = snapshot.ytd_expense;

        // Cap advance reduction ở 0 (không đi âm nếu extract sai).
        var actualAdvanceReduction = Math.Min(advanceTaken, prevAdvance);
        var newAdvance = prevAdvance - actualAdvanceReduction;
        var newYtd = prevYtd + totalClaim;

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE sim_employees SET outstanding_advance = @newAdvance, ytd_expense = @newYtd WHERE tenant_id = @tenantId AND id = @employeeId",
            new { newAdvance, newYtd, tenantId, employeeId },
            transaction: tx,
            cancellationToken: ct));

        // Optional: mark claim as paid nếu document_number khớp row tồn tại.
        if (!string.IsNullOrWhiteSpace(claimDocumentNumber))
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE sim_expense_claims
                   SET status = 'paid', paid_at = NOW()
                 WHERE tenant_id = @tenantId
                   AND document_number = @claimDocumentNumber
                   AND status <> 'paid'
                """,
                new { tenantId, claimDocumentNumber },
                transaction: tx,
                cancellationToken: ct));
        }

        await tx.CommitAsync(ct);

        return new ExpenseClaimApplyResult(
            PreviousAdvance: prevAdvance,
            NewAdvance: newAdvance,
            PreviousYtdExpense: prevYtd,
            NewYtdExpense: newYtd);
    }
}
