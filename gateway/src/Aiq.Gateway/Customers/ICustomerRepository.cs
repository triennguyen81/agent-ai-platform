using System.Text.RegularExpressions;
using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Customers;

// ============================================================================
// UC-ACC-001 §5.4-5.7: Master data + A/R lookups feeding the invoice-to-journal
// agent flow. Reads sim_* tables — later replaced by MCP adapters to real ERP
// without changing this interface.
// ============================================================================

// Deterministic, local-only MST format check (Vietnamese tax code: 10 digits,
// optionally a "-NNN" branch suffix — e.g. 0101234567 or 0101234567-001).
// Never calls an external tax-registry service — real GDT lookup stays
// deferred (no provider configured), same posture as Voucher's
// verify_einvoice_tct tool, which is itself best-effort/fallback-only.
public static class CustomerValidation
{
    private static readonly Regex MstPattern = new(@"^\d{10}(-\d{3})?$", RegexOptions.Compiled);

    public static bool IsValidMstFormat(string? mst) =>
        !string.IsNullOrWhiteSpace(mst) && MstPattern.IsMatch(mst.Trim());
}

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
    decimal CurrentAr,
    string CustomerType,
    string? Representative,
    string Status,
    string? Notes,
    Guid? CreatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record CreateCustomerRequest(
    [property: System.ComponentModel.DataAnnotations.Required] string CustomerCode,
    [property: System.ComponentModel.DataAnnotations.Required] string Name,
    string? Mst,
    string? Address,
    string? Phone,
    string? Email,
    string? Category,
    string CustomerType,
    string? Representative,
    decimal? CreditLimit,
    int? CreditTermsDays,
    string? Notes);

public sealed record UpdateCustomerRequest(
    string? Name,
    string? Mst,
    string? Address,
    string? Phone,
    string? Email,
    string? Category,
    string? CustomerType,
    string? Representative,
    decimal? CreditLimit,
    int? CreditTermsDays,
    string? Notes);

// Non-blocking duplicate signal returned alongside a successful create —
// the caller decides whether to proceed; a hard conflict (same tax code or
// customer code) is instead rejected outright by CreateAsync (409).
public sealed record DuplicateCandidate(Guid Id, string CustomerCode, string Name, string? Mst, decimal Similarity);

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

public sealed record ReceiptApplicationInput(
    string DocumentNumber,
    decimal AppliedAmount);

public sealed record ReceiptApplyResult(
    decimal PreviousBalance,
    decimal NewBalance,
    decimal TotalApplied,
    decimal DiscountAmount,
    int LedgerRowsInserted);

public sealed record InvoiceBookResult(
    decimal PreviousBalance,
    decimal NewBalance,
    decimal InvoiceTotal,
    int LedgerRowsInserted);

public sealed record ReceiptUnapplyResult(
    decimal PreviousBalance,
    decimal NewBalance,
    int LedgerRowsDeleted);

public interface ICustomerRepository
{
    Task<IReadOnlyList<CustomerRecord>> ListAsync(Guid tenantId, Guid departmentId, string? category, string? search, bool includeInactive, CancellationToken ct);
    Task<CustomerRecord?> FindByMstAsync(Guid tenantId, Guid departmentId, string mst, CancellationToken ct);
    Task<CustomerRecord?> FindByIdAsync(Guid tenantId, Guid departmentId, Guid id, CancellationToken ct);
    Task<CustomerReceivablesReport?> GetReceivablesAsync(Guid tenantId, Guid departmentId, Guid customerId, CancellationToken ct);
    Task<SalesOrderMatch?> MatchInvoiceToSalesOrderAsync(Guid tenantId, Guid departmentId, string documentNumber, CancellationToken ct);

    // Hard conflict on (tenant_id, customer_code) or (tenant_id, mst) — the
    // real DB unique constraints are tenant-wide (a tax code identifies one
    // real-world entity regardless of which Department first recorded it),
    // so this check matches that scope, not just the caller's Department.
    // Returns null on a hard conflict instead of throwing; the caller (the
    // endpoint) turns that into 409. `possibleDuplicates` carries same-
    // Department fuzzy name matches (pg_trgm) — informational only, never
    // blocks creation.
    Task<(CustomerRecord? Created, IReadOnlyList<DuplicateCandidate> PossibleDuplicates)> CreateAsync(
        Guid tenantId, Guid departmentId, Guid createdBy, CreateCustomerRequest request, CancellationToken ct);

    Task<CustomerRecord?> UpdateAsync(
        Guid tenantId, Guid departmentId, Guid id, UpdateCustomerRequest request, CancellationToken ct);

    // status: "active" | "inactive". Never deletes the row — Voucher AR
    // ledger rows and Contract counterparty proposals reference customers
    // by id, so a hard delete would orphan business history.
    Task<CustomerRecord?> SetStatusAsync(
        Guid tenantId, Guid departmentId, Guid id, string status, Guid actorId, CancellationToken ct);

    // Apply a receipt (phiếu thu) to a customer's open invoices in a
    // single transaction:
    //   1. Insert a credit row in sim_ar_ledger per application (reduces AR).
    //   2. Insert a discount row if provided (Nợ 5211 / Có 131).
    //   3. Update sim_customers.current_ar (subtract totalApplied + discount).
    // Idempotent by voucherId — subsequent calls with same voucherId are no-ops.
    Task<ReceiptApplyResult> ApplyReceiptAsync(
        Guid tenantId,
        Guid customerId,
        Guid voucherId,
        string receiptDocumentNumber,
        DateOnly receiptDate,
        IReadOnlyList<ReceiptApplicationInput> applications,
        decimal discountAmount,
        string? discountReason,
        CancellationToken ct);

    // Book a sales invoice (invoice_vat_out) into sim_ar_ledger — inserts a
    // single debit row (increases AR: Nợ 131 tăng) + updates
    // sim_customers.current_ar += invoiceTotal. Idempotent by voucherId.
    Task<InvoiceBookResult> BookInvoiceAsync(
        Guid tenantId,
        Guid customerId,
        Guid voucherId,
        string invoiceDocumentNumber,
        DateOnly invoiceDate,
        DateOnly? dueDate,
        decimal invoiceTotal,
        CancellationToken ct);

    // Rollback a previously-applied receipt: DELETE rows tagged
    // reference_note LIKE 'voucher:{voucherId}%' from sim_ar_ledger, then
    // recompute sim_customers.current_ar = sum(debit) - sum(credit).
    Task<ReceiptUnapplyResult> UnapplyReceiptAsync(
        Guid tenantId,
        Guid customerId,
        Guid voucherId,
        CancellationToken ct);
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
            current_ar          AS CurrentAr,
            customer_type       AS CustomerType,
            representative      AS Representative,
            status              AS Status,
            notes               AS Notes,
            created_by          AS CreatedBy,
            created_at          AS CreatedAt,
            updated_at          AS UpdatedAt
        """;

    public async Task<IReadOnlyList<CustomerRecord>> ListAsync(
        Guid tenantId, Guid departmentId, string? category, string? search, bool includeInactive, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var sql = $"""
            SELECT {CustomerCols}
            FROM sim_customers
            WHERE tenant_id = @tenantId AND department_id=@departmentId
              AND (@includeInactive OR status = 'active')
              AND (@category IS NULL OR category = @category)
              AND (@search IS NULL OR name ILIKE '%' || @search || '%' OR mst LIKE @search || '%' OR customer_code ILIKE @search || '%')
            ORDER BY current_ar DESC, name
            LIMIT 200
            """;
        var rows = await conn.QueryAsync<CustomerRecord>(sql, new { tenantId, departmentId, category, search, includeInactive });
        return rows.AsList();
    }

    public async Task<CustomerRecord?> FindByMstAsync(Guid tenantId, Guid departmentId, string mst, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var sql = $"SELECT {CustomerCols} FROM sim_customers WHERE tenant_id=@tenantId AND department_id=@departmentId AND mst=@mst LIMIT 1";
        return await conn.QueryFirstOrDefaultAsync<CustomerRecord>(sql, new { tenantId, departmentId, mst });
    }

    public async Task<CustomerRecord?> FindByIdAsync(Guid tenantId, Guid departmentId, Guid id, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var sql = $"SELECT {CustomerCols} FROM sim_customers WHERE tenant_id=@tenantId AND department_id=@departmentId AND id=@id LIMIT 1";
        return await conn.QueryFirstOrDefaultAsync<CustomerRecord>(sql, new { tenantId, departmentId, id });
    }

    private async Task<CustomerRecord?> FindByIdAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<CustomerRecord>($"SELECT {CustomerCols} FROM sim_customers WHERE tenant_id=@tenantId AND id=@id", new { tenantId, id });
    }

    public async Task<(CustomerRecord? Created, IReadOnlyList<DuplicateCandidate> PossibleDuplicates)> CreateAsync(
        Guid tenantId, Guid departmentId, Guid createdBy, CreateCustomerRequest request, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);

        // Hard conflict check — tenant-wide, matching the real DB unique
        // constraints on (tenant_id, customer_code) and (tenant_id, mst).
        // Checked explicitly (not just left to the DB) so the endpoint can
        // return a clear 409 with which field conflicted, instead of a raw
        // constraint-violation exception.
        var codeConflict = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM sim_customers WHERE tenant_id=@tenantId AND customer_code=@CustomerCode)",
            new { tenantId, request.CustomerCode }, cancellationToken: ct));
        if (codeConflict) return (null, Array.Empty<DuplicateCandidate>());

        if (!string.IsNullOrWhiteSpace(request.Mst))
        {
            var mstConflict = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM sim_customers WHERE tenant_id=@tenantId AND mst=@Mst)",
                new { tenantId, request.Mst }, cancellationToken: ct));
            if (mstConflict) return (null, Array.Empty<DuplicateCandidate>());
        }

        // Fuzzy same-Department name duplicates — informational only via
        // pg_trgm similarity(), never blocks creation.
        var duplicates = (await conn.QueryAsync<DuplicateCandidate>(new CommandDefinition("""
            SELECT id AS Id, customer_code AS CustomerCode, name AS Name, mst AS Mst,
                   similarity(name, @Name)::numeric AS Similarity
            FROM sim_customers
            WHERE tenant_id=@tenantId AND department_id=@departmentId AND status='active'
              AND similarity(name, @Name) > 0.4
            ORDER BY similarity(name, @Name) DESC
            LIMIT 5
            """, new { tenantId, departmentId, request.Name }, cancellationToken: ct))).AsList();

        var insertSql = $"""
            INSERT INTO sim_customers (
                tenant_id, department_id, customer_code, name, mst, address, phone, email,
                category, customer_type, representative, credit_limit, credit_terms_days,
                notes, created_by
            ) VALUES (
                @tenantId, @departmentId, @CustomerCode, @Name, @Mst, @Address, @Phone, @Email,
                COALESCE(@Category,'phong_kham'), @CustomerType, @Representative,
                COALESCE(@CreditLimit,0), COALESCE(@CreditTermsDays,0),
                @Notes, @createdBy
            )
            RETURNING {CustomerCols};
            """;
        var created = await conn.QuerySingleAsync<CustomerRecord>(new CommandDefinition(
            insertSql,
            new
            {
                tenantId, departmentId, createdBy,
                request.CustomerCode, request.Name, request.Mst, request.Address, request.Phone, request.Email,
                request.Category, request.CustomerType, request.Representative,
                request.CreditLimit, request.CreditTermsDays, request.Notes,
            },
            cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO customer_history(tenant_id,department_id,customer_id,actor_id,event_type,details) VALUES(@tenantId,@departmentId,@id,@createdBy,'created','{}'::jsonb)",
            new { tenantId, departmentId, id = created.Id, createdBy }, cancellationToken: ct));

        return (created, duplicates);
    }

    public async Task<CustomerRecord?> UpdateAsync(
        Guid tenantId, Guid departmentId, Guid id, UpdateCustomerRequest request, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var sql = $"""
            UPDATE sim_customers SET
                name              = COALESCE(@Name, name),
                mst               = COALESCE(@Mst, mst),
                address           = COALESCE(@Address, address),
                phone             = COALESCE(@Phone, phone),
                email             = COALESCE(@Email, email),
                category          = COALESCE(@Category, category),
                customer_type     = COALESCE(@CustomerType, customer_type),
                representative    = COALESCE(@Representative, representative),
                credit_limit      = COALESCE(@CreditLimit, credit_limit),
                credit_terms_days = COALESCE(@CreditTermsDays, credit_terms_days),
                notes             = COALESCE(@Notes, notes),
                updated_at        = NOW()
            WHERE id = @id AND tenant_id = @tenantId AND department_id = @departmentId AND status = 'active'
            RETURNING {CustomerCols};
            """;
        return await conn.QueryFirstOrDefaultAsync<CustomerRecord>(new CommandDefinition(
            sql,
            new
            {
                id, tenantId, departmentId,
                request.Name, request.Mst, request.Address, request.Phone, request.Email,
                request.Category, request.CustomerType, request.Representative,
                request.CreditLimit, request.CreditTermsDays, request.Notes,
            },
            cancellationToken: ct));
    }

    public async Task<CustomerRecord?> SetStatusAsync(
        Guid tenantId, Guid departmentId, Guid id, string status, Guid actorId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var updated = await conn.QueryFirstOrDefaultAsync<CustomerRecord>(new CommandDefinition(
            $"""
            UPDATE sim_customers SET status=@status, updated_at=NOW()
            WHERE id=@id AND tenant_id=@tenantId AND department_id=@departmentId
            RETURNING {CustomerCols};
            """,
            new { id, tenantId, departmentId, status }, cancellationToken: ct));
        if (updated is null) return null;

        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO customer_history(tenant_id,department_id,customer_id,actor_id,event_type,details) VALUES(@tenantId,@departmentId,@id,@actorId,@eventType,'{}'::jsonb)",
            new { tenantId, departmentId, id, actorId, eventType = status == "active" ? "reactivated" : "deactivated" },
            cancellationToken: ct));
        return updated;
    }

    public async Task<CustomerReceivablesReport?> GetReceivablesAsync(
        Guid tenantId, Guid departmentId, Guid customerId, CancellationToken ct)
    {
        var customer = await FindByIdAsync(tenantId, departmentId, customerId, ct);
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
        Guid tenantId, Guid departmentId, string documentNumber, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);

        // 1. Find sales order where invoiced_ref matches. If none, we're done —
        //    invoice was bán không qua đơn hàng (§5.6 A2).
        const string soSql = """
            SELECT so.id            AS Id,
                   order_number     AS OrderNumber,
                   order_date       AS OrderDate,
                   so.status        AS Status,
                   subtotal         AS Subtotal,
                   vat_amount       AS VatAmount,
                   total_amount     AS TotalAmount,
                   payment_terms_days AS PaymentTermsDays,
                   invoiced_ref     AS InvoicedRef,
                   delivery_note_ref AS DeliveryNoteRef,
                   salesperson_code AS SalespersonCode,
                   notes            AS Notes
            FROM sim_sales_orders so JOIN sim_customers c ON c.id=so.customer_id
            WHERE so.tenant_id = @tenantId AND c.department_id=@departmentId
              AND invoiced_ref = @documentNumber
            LIMIT 1
            """;
        var so = await conn.QueryFirstOrDefaultAsync<SalesOrderMatchRow>(
            new CommandDefinition(soSql, new { tenantId, departmentId, documentNumber }, cancellationToken: ct));
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

    public async Task<ReceiptApplyResult> ApplyReceiptAsync(
        Guid tenantId,
        Guid customerId,
        Guid voucherId,
        string receiptDocumentNumber,
        DateOnly receiptDate,
        IReadOnlyList<ReceiptApplicationInput> applications,
        decimal discountAmount,
        string? discountReason,
        CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Idempotency guard: if a credit row already tags this voucher, we
        // already applied it. Detected via reference_note = "voucher:{id}".
        const string dupSql = "SELECT COUNT(*) FROM sim_ar_ledger WHERE customer_id = @customerId AND reference_note LIKE @tag";
        var existing = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(dupSql,
                new { customerId, tag = $"voucher:{voucherId:N}%" },
                transaction: tx,
                cancellationToken: ct));
        if (existing > 0)
        {
            await tx.RollbackAsync(ct);
            var customer = await FindByIdAsync(tenantId, customerId, ct)
                ?? throw new InvalidOperationException("Customer not found");
            return new ReceiptApplyResult(
                PreviousBalance: customer.CurrentAr,
                NewBalance: customer.CurrentAr,
                TotalApplied: 0m,
                DiscountAmount: 0m,
                LedgerRowsInserted: 0);
        }

        var prevBalance = await conn.ExecuteScalarAsync<decimal>(
            new CommandDefinition(
                "SELECT current_ar FROM sim_customers WHERE tenant_id = @tenantId AND id = @customerId FOR UPDATE",
                new { tenantId, customerId },
                transaction: tx,
                cancellationToken: ct));

        decimal totalApplied = 0m;
        int rowsInserted = 0;

        // Snapshot rolling balance so we can compute balance_after per row.
        var runningBalance = prevBalance;

        foreach (var app in applications)
        {
            if (app.AppliedAmount <= 0m) continue;
            runningBalance -= app.AppliedAmount;
            const string insertSql = """
                INSERT INTO sim_ar_ledger
                    (tenant_id, customer_id, document_date, document_number, document_type,
                     debit, credit, balance_after, due_date, reference_note)
                VALUES
                    (@tenantId, @customerId, @receiptDate, @receiptDocumentNumber, 'receipt',
                     0, @credit, @balanceAfter, NULL, @refNote)
                """;
            await conn.ExecuteAsync(new CommandDefinition(
                insertSql,
                new
                {
                    tenantId,
                    customerId,
                    receiptDate,
                    receiptDocumentNumber,
                    credit = app.AppliedAmount,
                    balanceAfter = runningBalance,
                    refNote = $"voucher:{voucherId:N}|apply:{app.DocumentNumber}",
                },
                transaction: tx,
                cancellationToken: ct));
            totalApplied += app.AppliedAmount;
            rowsInserted++;
        }

        // Discount line reduces A/R further and books to 5211 in the journal
        // (already suggested to the user via ReceiptMatchingPanel).
        if (discountAmount > 0m)
        {
            runningBalance -= discountAmount;
            const string discountSql = """
                INSERT INTO sim_ar_ledger
                    (tenant_id, customer_id, document_date, document_number, document_type,
                     debit, credit, balance_after, due_date, reference_note)
                VALUES
                    (@tenantId, @customerId, @receiptDate, @discountDoc, 'discount',
                     0, @credit, @balanceAfter, NULL, @refNote)
                """;
            await conn.ExecuteAsync(new CommandDefinition(
                discountSql,
                new
                {
                    tenantId,
                    customerId,
                    receiptDate,
                    discountDoc = $"CK-{receiptDocumentNumber}",
                    credit = discountAmount,
                    balanceAfter = runningBalance,
                    refNote = $"voucher:{voucherId:N}|discount:{discountReason ?? "auto"}",
                },
                transaction: tx,
                cancellationToken: ct));
            rowsInserted++;
        }

        // Update the denormalized current_ar snapshot on the customer row.
        var newBalance = prevBalance - totalApplied - discountAmount;
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE sim_customers SET current_ar = @newBalance WHERE tenant_id = @tenantId AND id = @customerId",
            new { newBalance, tenantId, customerId },
            transaction: tx,
            cancellationToken: ct));

        await tx.CommitAsync(ct);

        return new ReceiptApplyResult(
            PreviousBalance: prevBalance,
            NewBalance: newBalance,
            TotalApplied: totalApplied,
            DiscountAmount: discountAmount,
            LedgerRowsInserted: rowsInserted);
    }

    public async Task<InvoiceBookResult> BookInvoiceAsync(
        Guid tenantId,
        Guid customerId,
        Guid voucherId,
        string invoiceDocumentNumber,
        DateOnly invoiceDate,
        DateOnly? dueDate,
        decimal invoiceTotal,
        CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string dupSql = "SELECT COUNT(*) FROM sim_ar_ledger WHERE customer_id = @customerId AND reference_note LIKE @tag";
        var existing = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(dupSql,
                new { customerId, tag = $"voucher:{voucherId:N}%" },
                transaction: tx,
                cancellationToken: ct));
        if (existing > 0)
        {
            await tx.RollbackAsync(ct);
            var customer = await FindByIdAsync(tenantId, customerId, ct)
                ?? throw new InvalidOperationException("Customer not found");
            return new InvoiceBookResult(
                PreviousBalance: customer.CurrentAr,
                NewBalance: customer.CurrentAr,
                InvoiceTotal: 0m,
                LedgerRowsInserted: 0);
        }

        var prevBalance = await conn.ExecuteScalarAsync<decimal>(
            new CommandDefinition(
                "SELECT current_ar FROM sim_customers WHERE tenant_id = @tenantId AND id = @customerId FOR UPDATE",
                new { tenantId, customerId },
                transaction: tx,
                cancellationToken: ct));

        var newBalance = prevBalance + invoiceTotal;

        const string insertSql = """
            INSERT INTO sim_ar_ledger
                (tenant_id, customer_id, document_date, document_number, document_type,
                 debit, credit, balance_after, due_date, reference_note)
            VALUES
                (@tenantId, @customerId, @invoiceDate, @invoiceDocumentNumber, 'invoice',
                 @debit, 0, @balanceAfter, @dueDate, @refNote)
            """;
        await conn.ExecuteAsync(new CommandDefinition(
            insertSql,
            new
            {
                tenantId,
                customerId,
                invoiceDate,
                invoiceDocumentNumber,
                debit = invoiceTotal,
                balanceAfter = newBalance,
                dueDate = (object?)dueDate ?? DBNull.Value,
                refNote = $"voucher:{voucherId:N}|book_invoice",
            },
            transaction: tx,
            cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE sim_customers SET current_ar = @newBalance WHERE tenant_id = @tenantId AND id = @customerId",
            new { newBalance, tenantId, customerId },
            transaction: tx,
            cancellationToken: ct));

        await tx.CommitAsync(ct);

        return new InvoiceBookResult(
            PreviousBalance: prevBalance,
            NewBalance: newBalance,
            InvoiceTotal: invoiceTotal,
            LedgerRowsInserted: 1);
    }

    public async Task<ReceiptUnapplyResult> UnapplyReceiptAsync(
        Guid tenantId,
        Guid customerId,
        Guid voucherId,
        CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var prevBalance = await conn.ExecuteScalarAsync<decimal>(
            new CommandDefinition(
                "SELECT current_ar FROM sim_customers WHERE tenant_id = @tenantId AND id = @customerId FOR UPDATE",
                new { tenantId, customerId },
                transaction: tx,
                cancellationToken: ct));

        // Delete all rows tagged với voucherId. Includes cả credit application
        // rows và optional discount row.
        const string deleteSql = """
            DELETE FROM sim_ar_ledger
             WHERE customer_id = @customerId
               AND reference_note LIKE @tag
            """;
        var deleted = await conn.ExecuteAsync(
            new CommandDefinition(deleteSql,
                new { customerId, tag = $"voucher:{voucherId:N}%" },
                transaction: tx,
                cancellationToken: ct));

        if (deleted == 0)
        {
            await tx.RollbackAsync(ct);
            return new ReceiptUnapplyResult(prevBalance, prevBalance, 0);
        }

        // Recompute current_ar = sum(debit) - sum(credit) from remaining rows.
        const string recomputeSql = """
            SELECT COALESCE(SUM(debit) - SUM(credit), 0) FROM sim_ar_ledger WHERE customer_id = @customerId
            """;
        var newBalance = await conn.ExecuteScalarAsync<decimal>(
            new CommandDefinition(recomputeSql, new { customerId }, transaction: tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE sim_customers SET current_ar = @newBalance WHERE tenant_id = @tenantId AND id = @customerId",
            new { newBalance, tenantId, customerId },
            transaction: tx,
            cancellationToken: ct));

        await tx.CommitAsync(ct);

        return new ReceiptUnapplyResult(prevBalance, newBalance, deleted);
    }
}
