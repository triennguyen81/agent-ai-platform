using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Customers;

// ============================================================================
// UC-ACC-001 §5.4-5.7: Master data + A/R lookups feeding the invoice-to-journal
// agent flow. Reads sim_* tables — later replaced by MCP adapters to real ERP
// without changing this interface.
// ============================================================================

public sealed record CustomerRecord(
    Guid Id,
    string CustomerCode,
    string Name,
    string? Mst,
    string? Address,
    string? Phone,
    string? Email,
    string Category,
    decimal CreditLimit,
    int CreditTermsDays,
    decimal CurrentAr);

public sealed record ArLedgerRow(
    Guid Id,
    DateOnly DocumentDate,
    string DocumentNumber,
    string DocumentType,
    decimal Debit,
    decimal Credit,
    decimal? BalanceAfter,
    DateOnly? DueDate,
    int DaysOverdue,
    string? ReferenceNote);

// UC-ACC-001 §9.2: fields the agent must see to score credit risk.
public sealed record CustomerReceivablesReport(
    CustomerRecord Customer,
    decimal CurrentBalance131,
    decimal CreditLimit,
    decimal CreditUsedPercent,
    IReadOnlyList<ArLedgerRow> OpenInvoices,
    ArAgingBuckets Aging,
    decimal OverdueAmount,
    DateOnly? LastPaymentDate,
    int PaymentTermDefault);

public sealed record ArAgingBuckets(
    decimal Current,      // not yet due
    decimal Bucket_0_30,
    decimal Bucket_31_60,
    decimal Bucket_61_90,
    decimal Over90);

public interface ICustomerRepository
{
    Task<IReadOnlyList<CustomerRecord>> ListAsync(Guid tenantId, string? category, string? search, CancellationToken ct);
    Task<CustomerRecord?> FindByMstAsync(Guid tenantId, string mst, CancellationToken ct);
    Task<CustomerRecord?> FindByIdAsync(Guid tenantId, Guid id, CancellationToken ct);
    Task<CustomerReceivablesReport?> GetReceivablesAsync(Guid tenantId, Guid customerId, CancellationToken ct);
}

public sealed class CustomerRepository : ICustomerRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public CustomerRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    private const string CustomerCols = """
            id                  AS Id,
            customer_code       AS CustomerCode,
            name                AS Name,
            mst                 AS Mst,
            address             AS Address,
            phone               AS Phone,
            email               AS Email,
            category            AS Category,
            credit_limit        AS CreditLimit,
            credit_terms_days   AS CreditTermsDays,
            current_ar          AS CurrentAr
        """;

    public async Task<IReadOnlyList<CustomerRecord>> ListAsync(
        Guid tenantId, string? category, string? search, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var sql = $"""
            SELECT {CustomerCols}
            FROM sim_customers
            WHERE tenant_id = @tenantId
              AND (@category IS NULL OR category = @category)
              AND (@search IS NULL OR name ILIKE '%' || @search || '%' OR mst LIKE @search || '%' OR customer_code ILIKE @search || '%')
            ORDER BY current_ar DESC, name
            LIMIT 200
            """;
        var rows = await conn.QueryAsync<CustomerRecord>(sql, new { tenantId, category, search });
        return rows.AsList();
    }

    public async Task<CustomerRecord?> FindByMstAsync(Guid tenantId, string mst, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var sql = $"SELECT {CustomerCols} FROM sim_customers WHERE tenant_id = @tenantId AND mst = @mst LIMIT 1";
        return await conn.QueryFirstOrDefaultAsync<CustomerRecord>(sql, new { tenantId, mst });
    }

    public async Task<CustomerRecord?> FindByIdAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var sql = $"SELECT {CustomerCols} FROM sim_customers WHERE tenant_id = @tenantId AND id = @id LIMIT 1";
        return await conn.QueryFirstOrDefaultAsync<CustomerRecord>(sql, new { tenantId, id });
    }

    public async Task<CustomerReceivablesReport?> GetReceivablesAsync(
        Guid tenantId, Guid customerId, CancellationToken ct)
    {
        var customer = await FindByIdAsync(tenantId, customerId, ct);
        if (customer is null) return null;

        await using var conn = await _factory.OpenAsync(ct);

        // Only open invoices (unpaid, credit = 0). Ordered by due date so agent
        // sees the most urgent lines first.
        var ledgerSql = """
            SELECT id             AS Id,
                   document_date  AS DocumentDate,
                   document_number AS DocumentNumber,
                   document_type  AS DocumentType,
                   debit          AS Debit,
                   credit         AS Credit,
                   balance_after  AS BalanceAfter,
                   due_date       AS DueDate,
                   CASE WHEN CURRENT_DATE > due_date AND credit = 0
                        THEN (CURRENT_DATE - due_date)
                        ELSE 0
                   END AS DaysOverdue,
                   reference_note AS ReferenceNote
            FROM sim_ar_ledger
            WHERE customer_id = @customerId
              AND (document_type = 'invoice' AND credit = 0)
            ORDER BY due_date NULLS LAST, document_date
            """;
        var rowsEnum = await conn.QueryAsync<ArLedgerRow>(
            new CommandDefinition(ledgerSql, new { customerId }, cancellationToken: ct));
        var rows = new List<ArLedgerRow>(rowsEnum);

        decimal bucketCurrent = 0, b30 = 0, b60 = 0, b90 = 0, over90 = 0;
        decimal overdue = 0;
        foreach (var row in rows)
        {
            if (row.DaysOverdue == 0) bucketCurrent += row.Debit;
            else if (row.DaysOverdue <= 30) { b30 += row.Debit; overdue += row.Debit; }
            else if (row.DaysOverdue <= 60) { b60 += row.Debit; overdue += row.Debit; }
            else if (row.DaysOverdue <= 90) { b90 += row.Debit; overdue += row.Debit; }
            else { over90 += row.Debit; overdue += row.Debit; }
        }

        var lastPaymentSql = """
            SELECT MAX(document_date) FROM sim_ar_ledger
            WHERE customer_id = @customerId AND credit > 0
            """;
        var lastPayment = await conn.QueryFirstOrDefaultAsync<DateOnly?>(lastPaymentSql, new { customerId });

        var creditUsed = customer.CreditLimit > 0
            ? Math.Round((customer.CurrentAr / customer.CreditLimit) * 100m, 1)
            : 0m;

        return new CustomerReceivablesReport(
            customer,
            CurrentBalance131: customer.CurrentAr,
            CreditLimit: customer.CreditLimit,
            CreditUsedPercent: creditUsed,
            OpenInvoices: rows,
            Aging: new ArAgingBuckets(bucketCurrent, b30, b60, b90, over90),
            OverdueAmount: overdue,
            LastPaymentDate: lastPayment,
            PaymentTermDefault: customer.CreditTermsDays);
    }
}
