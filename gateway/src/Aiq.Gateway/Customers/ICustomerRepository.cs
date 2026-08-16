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

// UC-ACC-001 §5.6: 3-way match — invoice → sales order → delivery note →
// (optional) contract milestone. Read-only view.
public sealed record SalesOrderMatchRow(
    Guid Id,
    string OrderNumber,
    DateOnly OrderDate,
    string Status,
    decimal Subtotal,
    decimal VatAmount,
    decimal TotalAmount,
    int PaymentTermsDays,
    string? InvoicedRef,
    string? DeliveryNoteRef,
    string? SalespersonCode,
    string? Notes);

public sealed record DeliveryNoteRow(
    string DnNumber,
    DateOnly DnDate,
    string WarehouseFrom,
    string? DeliveredBy,
    string? ReceivedBy,
    string Status);

public sealed record ContractMilestoneRow(
    string ContractNumber,
    string ContractTitle,
    decimal ContractValue,
    int MilestoneNo,
    string MilestoneDesc,
    decimal MilestoneAmount,
    string MilestoneStatus);

public sealed record SalesOrderMatch(
    SalesOrderMatchRow SalesOrder,
    DeliveryNoteRow? DeliveryNote,
    ContractMilestoneRow? Contract,
    string MatchQuality);   // exact / partial / not_found

public interface ICustomerRepository
{
    Task<IReadOnlyList<CustomerRecord>> ListAsync(Guid tenantId, string? category, string? search, CancellationToken ct);
    Task<CustomerRecord?> FindByMstAsync(Guid tenantId, string mst, CancellationToken ct);
    Task<CustomerRecord?> FindByIdAsync(Guid tenantId, Guid id, CancellationToken ct);
    Task<CustomerReceivablesReport?> GetReceivablesAsync(Guid tenantId, Guid customerId, CancellationToken ct);
    Task<SalesOrderMatch?> MatchInvoiceToSalesOrderAsync(Guid tenantId, string documentNumber, CancellationToken ct);
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

    public async Task<SalesOrderMatch?> MatchInvoiceToSalesOrderAsync(
        Guid tenantId, string documentNumber, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);

        // 1. Find sales order where invoiced_ref matches. If none, we're done —
        //    invoice was bán không qua đơn hàng (§5.6 A2).
        const string soSql = """
            SELECT id               AS Id,
                   order_number     AS OrderNumber,
                   order_date       AS OrderDate,
                   status           AS Status,
                   subtotal         AS Subtotal,
                   vat_amount       AS VatAmount,
                   total_amount     AS TotalAmount,
                   payment_terms_days AS PaymentTermsDays,
                   invoiced_ref     AS InvoicedRef,
                   delivery_note_ref AS DeliveryNoteRef,
                   salesperson_code AS SalespersonCode,
                   notes            AS Notes
            FROM sim_sales_orders
            WHERE tenant_id = @tenantId
              AND invoiced_ref = @documentNumber
            LIMIT 1
            """;
        var so = await conn.QueryFirstOrDefaultAsync<SalesOrderMatchRow>(
            new CommandDefinition(soSql, new { tenantId, documentNumber }, cancellationToken: ct));
        if (so is null) return null;

        // 2. Delivery note (if any)
        DeliveryNoteRow? dn = null;
        if (!string.IsNullOrWhiteSpace(so.DeliveryNoteRef))
        {
            const string dnSql = """
                SELECT dn_number       AS DnNumber,
                       dn_date         AS DnDate,
                       warehouse_from  AS WarehouseFrom,
                       delivered_by    AS DeliveredBy,
                       received_by     AS ReceivedBy,
                       status          AS Status
                FROM sim_delivery_notes
                WHERE tenant_id = @tenantId AND dn_number = @dnNumber
                LIMIT 1
                """;
            dn = await conn.QueryFirstOrDefaultAsync<DeliveryNoteRow>(
                new CommandDefinition(dnSql, new { tenantId, dnNumber = so.DeliveryNoteRef }, cancellationToken: ct));
        }

        // 3. Contract milestone (if this SO belongs to a contract + milestone
        //    whose invoice_ref matches).
        const string ctrSql = """
            SELECT c.contract_number AS ContractNumber,
                   c.title           AS ContractTitle,
                   c.contract_value  AS ContractValue,
                   m.milestone_no    AS MilestoneNo,
                   m.description     AS MilestoneDesc,
                   m.amount          AS MilestoneAmount,
                   m.status          AS MilestoneStatus
            FROM sim_sales_orders so
            JOIN sim_contracts c ON c.id = so.contract_id
            LEFT JOIN sim_contract_milestones m ON m.contract_id = c.id AND m.invoice_ref = so.invoiced_ref
            WHERE so.id = @soId
            LIMIT 1
            """;
        var contract = await conn.QueryFirstOrDefaultAsync<ContractMilestoneRow>(
            new CommandDefinition(ctrSql, new { soId = so.Id }, cancellationToken: ct));

        var quality = dn is null ? "partial" : "exact";
        return new SalesOrderMatch(so, dn, contract, quality);
    }
}
