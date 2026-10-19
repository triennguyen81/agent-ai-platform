using System.Text.RegularExpressions;
using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Suppliers;

// Deterministic, local-only MST format check — same rule as
// Aiq.Gateway.Customers.CustomerValidation (Vietnamese tax code: 10 digits,
// optionally a "-NNN" branch suffix). Never calls an external tax-registry
// service; kept as its own copy (not a shared helper) since Customers/
// Suppliers have always been independent modules in this codebase and a
// cross-module dependency isn't worth it for one regex.
public static class SupplierValidation
{
    private static readonly Regex MstPattern = new(@"^\d{10}(-\d{3})?$", RegexOptions.Compiled);

    public static bool IsValidMstFormat(string? mst) =>
        !string.IsNullOrWhiteSpace(mst) && MstPattern.IsMatch(mst.Trim());
}

// ============================================================================
// UC3 (Expense/AP): mirror của Customers/ICustomerRepository. Reads sim_*
// tables — sim_suppliers, sim_ap_ledger, sim_purchase_orders. Later replaced
// by MCP adapters to real ERP without changing this interface.
//
// Kế toán semantics (TT200):
//   Có 331 (credit) → tăng phải trả NCC (nhận HĐ NCC)
//   Nợ 331 (debit)  → giảm phải trả NCC (thanh toán qua TM/CK)
//
// Open payables filter: document_type='invoice' AND debit = 0.
// ============================================================================

public sealed record SupplierRecord(
    Guid Id,
    string SupplierCode,
    string Name,
    string? Mst,
    string Country,
    string Currency,
    string? PaymentTerms,
    int PaymentTermsDays,
    decimal CurrentAp,
    string SupplierType,
    string? Representative,
    string? Phone,
    string? Email,
    string? Address,
    string Status,
    string? Notes,
    Guid? CreatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record CreateSupplierRequest(
    [property: System.ComponentModel.DataAnnotations.Required] string SupplierCode,
    [property: System.ComponentModel.DataAnnotations.Required] string Name,
    string? Mst,
    string? Country,
    string? Currency,
    string? PaymentTerms,
    int? PaymentTermsDays,
    string SupplierType,
    string? Representative,
    string? Phone,
    string? Email,
    string? Address,
    string? Notes);

public sealed record UpdateSupplierRequest(
    string? Name,
    string? Mst,
    string? Country,
    string? Currency,
    string? PaymentTerms,
    int? PaymentTermsDays,
    string? SupplierType,
    string? Representative,
    string? Phone,
    string? Email,
    string? Address,
    string? Notes);

public sealed record DuplicateSupplierCandidate(Guid Id, string SupplierCode, string Name, string? Mst, decimal Similarity);

public sealed record ApLedgerRow(
    Guid Id,
    DateOnly DocumentDate,
    string DocumentNumber,
    string DocumentType,
    decimal Debit,
    decimal Credit,
    decimal? BalanceAfter,
    DateOnly? DueDate,
    int DaysOverdue,
    string? ReferenceNote,
    decimal PaidAmount,
    string Status,
    Guid? ContractId,
    Guid? ContractPaymentScheduleId)
{
    // Settlement view (UNPAID/PARTIAL/PAID/OVERDUE) — independent of the
    // accounting POSTED state; derived from the open item itself.
    public decimal Outstanding => Credit - PaidAmount;
    public string SettlementStatus => Aiq.Gateway.Payments.CashSettlementRules.SettlementStatus(
        Credit, PaidAmount, DueDate, DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)));
}

// Kế toán trưởng cần để chấm rủi ro NCC + lên kế hoạch chi.
public sealed record SupplierPayablesReport(
    SupplierRecord Supplier,
    decimal CurrentBalance331,
    IReadOnlyList<ApLedgerRow> OpenInvoices,
    ApAgingBuckets Aging,
    decimal OverdueAmount,
    DateOnly? LastPaymentDate,
    int PaymentTermDefault,
    // Posted receipts/payments not yet allocated to any invoice (customer
    // advance / supplier prepayment) — never netted into the balance above.
    decimal UnappliedAmount);

public sealed record ApAgingBuckets(
    decimal Current,       // trong hạn
    decimal Bucket_0_30,
    decimal Bucket_31_60,
    decimal Bucket_61_90,
    decimal Over90);

// 3-way match phía AP: invoice_vat_in → PO → GRN → (optional) Purchase Contract.
public sealed record PurchaseOrderMatchRow(
    Guid Id,
    string PoNumber,
    DateOnly PoDate,
    string SupplierCode,
    string Status,
    string Currency,
    decimal ExchangeRate,
    decimal SubtotalVnd,
    decimal? TotalAmount,
    string? Incoterm,
    string? PaymentTerms,
    int PaymentTermsDays,
    string? DeliveryNoteRef,
    string? MatchedInvoiceNumber,
    string? Notes);

public sealed record GoodsReceivedNoteRow(
    string GrnNumber,
    DateOnly GrnDate,
    string? WarehouseTo,
    string? DeliveredBy,
    string? ReceivedBy,
    string Status);

public sealed record PurchaseOrderMatch(
    PurchaseOrderMatchRow PurchaseOrder,
    GoodsReceivedNoteRow? GoodsReceivedNote,
    string MatchQuality);   // exact / partial / not_found

// AP dashboard aggregate — same aging logic as SupplierPayablesReport.Aging
// but summed across every supplier in the Department in one query, from the
// real sim_ap_ledger open-invoice rows (document_type='invoice' AND debit=0).
// Nothing here is invented: a Department with no unpaid supplier invoices
// gets all-zero buckets, not a fabricated number.
public sealed record ApAgingSummary(
    int SupplierCount,
    decimal TotalPayable,
    decimal Current,
    decimal Bucket_0_30,
    decimal Bucket_31_60,
    decimal Bucket_61_90,
    decimal Over90,
    decimal OverdueAmount,
    decimal DueWithin7Days,
    decimal DueWithin30Days,
    int LargeBalanceSupplierCount);

public sealed record ExpenseApplicationInput(
    string DocumentNumber,
    decimal AppliedAmount);

public sealed record ExpenseApplyResult(
    decimal PreviousBalance,
    decimal NewBalance,
    decimal TotalApplied,
    decimal DiscountAmount,
    int LedgerRowsInserted);

public sealed record BookInvoiceResult(
    decimal PreviousBalance,
    decimal NewBalance,
    decimal InvoiceTotal,
    int LedgerRowsInserted);

public sealed record ExpenseUnapplyResult(
    decimal PreviousBalance,
    decimal NewBalance,
    int LedgerRowsDeleted);

public interface ISupplierRepository
{
    Task<IReadOnlyList<SupplierRecord>> ListAsync(Guid tenantId, Guid departmentId, string? country, string? search, bool includeInactive, CancellationToken ct);
    Task<SupplierRecord?> FindByMstAsync(Guid tenantId, Guid departmentId, string mst, CancellationToken ct);
    Task<SupplierRecord?> FindByIdAsync(Guid tenantId, Guid departmentId, Guid id, CancellationToken ct);
    Task<SupplierPayablesReport?> GetPayablesAsync(Guid tenantId, Guid departmentId, Guid supplierId, CancellationToken ct);
    Task<PurchaseOrderMatch?> MatchInvoiceToPurchaseOrderAsync(Guid tenantId, Guid departmentId, string documentNumber, CancellationToken ct);

    Task<ApAgingSummary> GetApSummaryAsync(Guid tenantId, Guid departmentId, CancellationToken ct);

    // Hard conflict on (tenant_id, supplier_code) [DB-enforced] or
    // (tenant_id, mst) [app-enforced only — see docs/SUPPLIER_MODULE_TODO.md
    // for why no DB constraint backs it]. Returns null on a hard conflict
    // instead of throwing; the endpoint turns that into 409.
    // `possibleDuplicates` is same-Department fuzzy name matches (pg_trgm),
    // informational only.
    Task<(SupplierRecord? Created, IReadOnlyList<DuplicateSupplierCandidate> PossibleDuplicates)> CreateAsync(
        Guid tenantId, Guid departmentId, Guid createdBy, CreateSupplierRequest request, CancellationToken ct);

    Task<SupplierRecord?> UpdateAsync(
        Guid tenantId, Guid departmentId, Guid id, UpdateSupplierRequest request, CancellationToken ct);

    // status: "active" | "inactive". Never deletes the row — Voucher AP
    // ledger rows and Contract counterparty proposals reference suppliers
    // by id, so a hard delete would orphan business history.
    Task<SupplierRecord?> SetStatusAsync(
        Guid tenantId, Guid departmentId, Guid id, string status, Guid actorId, CancellationToken ct);

    // Apply a payment (phiếu chi / uỷ nhiệm chi) to a supplier's open payables
    // in a single transaction:
    //   1. Insert a debit row in sim_ap_ledger per application (reduces AP).
    //   2. Insert a discount row if provided (Nợ 331 / Có 5153 — chiết khấu
    //      thanh toán được hưởng).
    //   3. Update sim_suppliers.current_ap (subtract totalApplied + discount).
    // Idempotent by voucherId — subsequent calls with same voucherId are no-ops.
    Task<ExpenseApplyResult> ApplyExpenseAsync(
        Guid tenantId,
        Guid supplierId,
        Guid voucherId,
        string paymentDocumentNumber,
        DateOnly paymentDate,
        IReadOnlyList<ExpenseApplicationInput> applications,
        decimal discountAmount,
        string? discountReason,
        CancellationToken ct);

    // Book an incoming supplier invoice (invoice_vat_in) — inserts a single
    // credit row in sim_ap_ledger (increases AP), status='open',
    // paid_amount=0, then recomputes sim_suppliers.current_ap from every
    // open/partial AP row. Idempotent by (tenant_id, voucher_id) — a real
    // unique constraint (migration 045). Semantics: Có 331 tăng phải trả
    // NCC. contractId/contractPaymentScheduleId are optional,
    // application-set links, mirroring Customer's BookInvoiceAsync.
    Task<BookInvoiceResult> BookInvoiceAsync(
        Guid tenantId,
        Guid supplierId,
        Guid voucherId,
        string invoiceDocumentNumber,
        DateOnly invoiceDate,
        DateOnly? dueDate,
        decimal invoiceTotal,
        Guid? contractId,
        Guid? contractPaymentScheduleId,
        CancellationToken ct);

    // Symmetric reversal of ApplyExpenseAsync — mirrors Customer's
    // UnapplyReceiptAsync (previously Supplier had no reversal path at all).
    // Restores each specific invoice's paid_amount/status by exactly what
    // this voucher allocated to it, then recomputes current_ap.
    Task<ExpenseUnapplyResult> UnapplyExpenseAsync(
        Guid tenantId,
        Guid supplierId,
        Guid voucherId,
        CancellationToken ct);
}

public sealed class SupplierRepository : ISupplierRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public SupplierRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    private const string SupplierCols = """
            id                    AS Id,
            supplier_code         AS SupplierCode,
            name                  AS Name,
            mst                   AS Mst,
            country               AS Country,
            currency              AS Currency,
            payment_terms         AS PaymentTerms,
            COALESCE(payment_terms_days, 0) AS PaymentTermsDays,
            current_ap            AS CurrentAp,
            supplier_type         AS SupplierType,
            representative        AS Representative,
            phone                 AS Phone,
            email                 AS Email,
            address               AS Address,
            status                AS Status,
            notes                 AS Notes,
            created_by            AS CreatedBy,
            created_at            AS CreatedAt,
            updated_at            AS UpdatedAt
        """;

    public async Task<IReadOnlyList<SupplierRecord>> ListAsync(
        Guid tenantId, Guid departmentId, string? country, string? search, bool includeInactive, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var sql = $"""
            SELECT {SupplierCols}
            FROM sim_suppliers
            WHERE tenant_id = @tenantId AND department_id=@departmentId
              AND (@includeInactive OR status = 'active')
              AND (@country IS NULL OR country = @country)
              AND (@search IS NULL OR name ILIKE '%' || @search || '%' OR mst LIKE @search || '%' OR supplier_code ILIKE @search || '%')
            ORDER BY current_ap DESC, name
            LIMIT 200
            """;
        var rows = await conn.QueryAsync<SupplierRecord>(sql, new { tenantId, departmentId, country, search, includeInactive });
        return rows.AsList();
    }

    public async Task<SupplierRecord?> FindByMstAsync(Guid tenantId, Guid departmentId, string mst, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var sql = $"SELECT {SupplierCols} FROM sim_suppliers WHERE tenant_id=@tenantId AND department_id=@departmentId AND mst=@mst LIMIT 1";
        return await conn.QueryFirstOrDefaultAsync<SupplierRecord>(sql, new { tenantId, departmentId, mst });
    }

    public async Task<SupplierRecord?> FindByIdAsync(Guid tenantId, Guid departmentId, Guid id, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var sql = $"SELECT {SupplierCols} FROM sim_suppliers WHERE tenant_id=@tenantId AND department_id=@departmentId AND id=@id LIMIT 1";
        return await conn.QueryFirstOrDefaultAsync<SupplierRecord>(sql, new { tenantId, departmentId, id });
    }

    private async Task<SupplierRecord?> FindByIdAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<SupplierRecord>($"SELECT {SupplierCols} FROM sim_suppliers WHERE tenant_id=@tenantId AND id=@id", new { tenantId, id });
    }

    public async Task<(SupplierRecord? Created, IReadOnlyList<DuplicateSupplierCandidate> PossibleDuplicates)> CreateAsync(
        Guid tenantId, Guid departmentId, Guid createdBy, CreateSupplierRequest request, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var codeConflict = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM sim_suppliers WHERE tenant_id=@tenantId AND supplier_code=@SupplierCode)",
            new { tenantId, request.SupplierCode }, cancellationToken: ct));
        if (codeConflict) return (null, Array.Empty<DuplicateSupplierCandidate>());

        if (!string.IsNullOrWhiteSpace(request.Mst))
        {
            var mstConflict = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM sim_suppliers WHERE tenant_id=@tenantId AND mst=@Mst)",
                new { tenantId, request.Mst }, cancellationToken: ct));
            if (mstConflict) return (null, Array.Empty<DuplicateSupplierCandidate>());
        }

        var duplicates = (await conn.QueryAsync<DuplicateSupplierCandidate>(new CommandDefinition("""
            SELECT id AS Id, supplier_code AS SupplierCode, name AS Name, mst AS Mst,
                   similarity(name, @Name)::numeric AS Similarity
            FROM sim_suppliers
            WHERE tenant_id=@tenantId AND department_id=@departmentId AND status='active'
              AND similarity(name, @Name) > 0.4
            ORDER BY similarity(name, @Name) DESC
            LIMIT 5
            """, new { tenantId, departmentId, request.Name }, cancellationToken: ct))).AsList();

        var insertSql = $"""
            INSERT INTO sim_suppliers (
                tenant_id, department_id, supplier_code, name, mst, country, currency,
                payment_terms, payment_terms_days, supplier_type, representative,
                phone, email, address, notes, created_by
            ) VALUES (
                @tenantId, @departmentId, @SupplierCode, @Name, @Mst,
                COALESCE(@Country,'VN'), COALESCE(@Currency,'VND'),
                @PaymentTerms, COALESCE(@PaymentTermsDays,0), @SupplierType, @Representative,
                @Phone, @Email, @Address, @Notes, @createdBy
            )
            RETURNING {SupplierCols};
            """;
        var created = await conn.QuerySingleAsync<SupplierRecord>(new CommandDefinition(
            insertSql,
            new
            {
                tenantId, departmentId, createdBy,
                request.SupplierCode, request.Name, request.Mst, request.Country, request.Currency,
                request.PaymentTerms, request.PaymentTermsDays, request.SupplierType, request.Representative,
                request.Phone, request.Email, request.Address, request.Notes,
            },
            cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO supplier_history(tenant_id,department_id,supplier_id,actor_id,event_type,details) VALUES(@tenantId,@departmentId,@id,@createdBy,'created','{}'::jsonb)",
            new { tenantId, departmentId, id = created.Id, createdBy }, cancellationToken: ct));

        return (created, duplicates);
    }

    public async Task<SupplierRecord?> UpdateAsync(
        Guid tenantId, Guid departmentId, Guid id, UpdateSupplierRequest request, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var sql = $"""
            UPDATE sim_suppliers SET
                name              = COALESCE(@Name, name),
                mst               = COALESCE(@Mst, mst),
                country           = COALESCE(@Country, country),
                currency          = COALESCE(@Currency, currency),
                payment_terms     = COALESCE(@PaymentTerms, payment_terms),
                payment_terms_days = COALESCE(@PaymentTermsDays, payment_terms_days),
                supplier_type     = COALESCE(@SupplierType, supplier_type),
                representative    = COALESCE(@Representative, representative),
                phone             = COALESCE(@Phone, phone),
                email             = COALESCE(@Email, email),
                address           = COALESCE(@Address, address),
                notes             = COALESCE(@Notes, notes),
                updated_at        = NOW()
            WHERE id = @id AND tenant_id = @tenantId AND department_id = @departmentId AND status = 'active'
            RETURNING {SupplierCols};
            """;
        return await conn.QueryFirstOrDefaultAsync<SupplierRecord>(new CommandDefinition(
            sql,
            new
            {
                id, tenantId, departmentId,
                request.Name, request.Mst, request.Country, request.Currency,
                request.PaymentTerms, request.PaymentTermsDays, request.SupplierType, request.Representative,
                request.Phone, request.Email, request.Address, request.Notes,
            },
            cancellationToken: ct));
    }

    public async Task<SupplierRecord?> SetStatusAsync(
        Guid tenantId, Guid departmentId, Guid id, string status, Guid actorId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var updated = await conn.QueryFirstOrDefaultAsync<SupplierRecord>(new CommandDefinition(
            $"""
            UPDATE sim_suppliers SET status=@status, updated_at=NOW()
            WHERE id=@id AND tenant_id=@tenantId AND department_id=@departmentId
            RETURNING {SupplierCols};
            """,
            new { id, tenantId, departmentId, status }, cancellationToken: ct));
        if (updated is null) return null;

        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO supplier_history(tenant_id,department_id,supplier_id,actor_id,event_type,details) VALUES(@tenantId,@departmentId,@id,@actorId,@eventType,'{}'::jsonb)",
            new { tenantId, departmentId, id, actorId, eventType = status == "active" ? "reactivated" : "deactivated" },
            cancellationToken: ct));
        return updated;
    }

    public async Task<SupplierPayablesReport?> GetPayablesAsync(
        Guid tenantId, Guid departmentId, Guid supplierId, CancellationToken ct)
    {
        var supplier = await FindByIdAsync(tenantId, departmentId, supplierId, ct);
        if (supplier is null) return null;

        await using var conn = await _factory.OpenAsync(ct);

        // Open payables — status-driven (open/partial), same fix as
        // Customer's GetReceivablesAsync (migration 045): the old
        // "debit=0 on this row" heuristic never reflected settlement once a
        // payment was applied via a separate, unlinked ledger row. Ordered
        // by due date so agent thấy dòng cấp bách nhất trước.
        var ledgerSql = """
            SELECT id             AS Id,
                   document_date  AS DocumentDate,
                   document_number AS DocumentNumber,
                   document_type  AS DocumentType,
                   debit          AS Debit,
                   credit         AS Credit,
                   balance_after  AS BalanceAfter,
                   due_date       AS DueDate,
                   CASE WHEN CURRENT_DATE > due_date AND status <> 'paid'
                        THEN (CURRENT_DATE - due_date)
                        ELSE 0
                   END AS DaysOverdue,
                   reference_note AS ReferenceNote,
                   paid_amount    AS PaidAmount,
                   status         AS Status,
                   contract_id    AS ContractId,
                   contract_payment_schedule_id AS ContractPaymentScheduleId
            FROM sim_ap_ledger
            WHERE supplier_id = @supplierId
              AND document_type = 'invoice' AND status IN ('open','partial')
            ORDER BY due_date NULLS LAST, document_date
            """;
        var rowsEnum = await conn.QueryAsync<ApLedgerRow>(
            new CommandDefinition(ledgerSql, new { supplierId }, cancellationToken: ct));
        var rows = new List<ApLedgerRow>(rowsEnum);

        // Aging buckets use the REMAINING balance (credit - paid_amount —
        // credit is the invoice amount for AP rows, debit is a payment).
        decimal bucketCurrent = 0, b30 = 0, b60 = 0, b90 = 0, over90 = 0;
        decimal overdue = 0;
        foreach (var row in rows)
        {
            var remaining = row.Credit - row.PaidAmount;
            if (row.DaysOverdue == 0) bucketCurrent += remaining;
            else if (row.DaysOverdue <= 30) { b30 += remaining; overdue += remaining; }
            else if (row.DaysOverdue <= 60) { b60 += remaining; overdue += remaining; }
            else if (row.DaysOverdue <= 90) { b90 += remaining; overdue += remaining; }
            else { over90 += remaining; overdue += remaining; }
        }

        var lastPaymentSql = """
            SELECT MAX(document_date) FROM sim_ap_ledger
            WHERE supplier_id = @supplierId AND debit > 0
            """;
        var lastPayment = await conn.QueryFirstOrDefaultAsync<DateOnly?>(lastPaymentSql, new { supplierId });

        var unapplied = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition("""
            SELECT COALESCE(SUM(c.amount - COALESCE((SELECT SUM(a.applied_amount) FROM payment_applications a
                                                     WHERE a.cash_transaction_id = c.id AND a.status = 'active'), 0)), 0)
            FROM cash_transactions c
            WHERE c.tenant_id = @tenantId AND c.supplier_id = @id AND c.status = 'posted'
            """, new { tenantId, id = supplierId }, cancellationToken: ct));

        return new SupplierPayablesReport(
            supplier,
            CurrentBalance331: supplier.CurrentAp,
            OpenInvoices: rows,
            Aging: new ApAgingBuckets(bucketCurrent, b30, b60, b90, over90),
            OverdueAmount: overdue,
            LastPaymentDate: lastPayment,
            PaymentTermDefault: supplier.PaymentTermsDays,
            UnappliedAmount: unapplied);
    }

    public async Task<PurchaseOrderMatch?> MatchInvoiceToPurchaseOrderAsync(
        Guid tenantId, Guid departmentId, string documentNumber, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);

        // Find PO where matched_invoice_number matches. If none → invoice_vat_in
        // không có PO tương ứng (mua vặt, không qua đơn đặt hàng).
        const string poSql = """
            SELECT po.id                 AS Id,
                   po_number             AS PoNumber,
                   po_date               AS PoDate,
                   supplier_code         AS SupplierCode,
                   po.status             AS Status,
                   po.currency           AS Currency,
                   COALESCE(exchange_rate, 1) AS ExchangeRate,
                   COALESCE(subtotal_vnd, 0)  AS SubtotalVnd,
                   total_amount          AS TotalAmount,
                   incoterm              AS Incoterm,
                   po.payment_terms      AS PaymentTerms,
                   COALESCE(payment_terms_days, 0) AS PaymentTermsDays,
                   COALESCE(delivery_note_ref, goods_received_note) AS DeliveryNoteRef,
                   matched_invoice_number AS MatchedInvoiceNumber,
                   notes                 AS Notes
            FROM sim_purchase_orders po JOIN sim_suppliers s ON s.tenant_id=po.tenant_id AND s.supplier_code=po.supplier_code
            WHERE po.tenant_id = @tenantId AND s.department_id=@departmentId
              AND matched_invoice_number = @documentNumber
            LIMIT 1
            """;
        var po = await conn.QueryFirstOrDefaultAsync<PurchaseOrderMatchRow>(
            new CommandDefinition(poSql, new { tenantId, departmentId, documentNumber }, cancellationToken: ct));
        if (po is null) return null;

        // GRN — Sprint 4: join real sim_goods_received_notes table (fall back
        // to sim_purchase_orders.goods_received_note text nếu GRN table trống).
        GoodsReceivedNoteRow? grn = null;
        const string grnSql = """
            SELECT grn_number     AS GrnNumber,
                   grn_date       AS GrnDate,
                   warehouse_to   AS WarehouseTo,
                   delivered_by   AS DeliveredBy,
                   received_by    AS ReceivedBy,
                   status         AS Status
            FROM sim_goods_received_notes
            WHERE tenant_id = @tenantId
              AND po_number = @poNumber
            ORDER BY grn_date DESC
            LIMIT 1
            """;
        try
        {
            grn = await conn.QueryFirstOrDefaultAsync<GoodsReceivedNoteRow>(
                new CommandDefinition(grnSql, new { tenantId, poNumber = po.PoNumber }, cancellationToken: ct));
        }
        catch
        {
            // Migration 19b chưa apply — bảng chưa tồn tại. Silent fallback.
        }

        // Fallback: nếu GRN table không có row, dùng text từ sim_purchase_orders.
        if (grn is null && !string.IsNullOrWhiteSpace(po.DeliveryNoteRef))
        {
            grn = new GoodsReceivedNoteRow(
                GrnNumber: po.DeliveryNoteRef,
                GrnDate: po.PoDate,
                WarehouseTo: null,
                DeliveredBy: null,
                ReceivedBy: null,
                Status: "received");
        }

        var quality = grn is null ? "partial" : "exact";
        return new PurchaseOrderMatch(po, grn, quality);
    }

    public async Task<ApAgingSummary> GetApSummaryAsync(Guid tenantId, Guid departmentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var scope = new { tenantId, departmentId };

        // Open/partial supplier invoices only — same filter GetPayablesAsync
        // uses per-supplier, aggregated here across the whole Department in
        // one query. Buckets use the REMAINING amount (credit - paid_amount),
        // so an applied payment reduces aging immediately (previously the
        // full invoice amount was counted, and fully paid invoices too).
        var aging = await conn.QuerySingleAsync<(
            decimal Current, decimal B30, decimal B60, decimal B90, decimal Over90,
            decimal Overdue, decimal Due7, decimal Due30, int SupplierCount)>(
            new CommandDefinition("""
                SELECT
                    COALESCE(SUM(CASE WHEN l.due_date IS NULL OR l.due_date >= CURRENT_DATE THEN l.credit - l.paid_amount ELSE 0 END),0) AS Current,
                    COALESCE(SUM(CASE WHEN l.due_date < CURRENT_DATE AND CURRENT_DATE - l.due_date <= 30 THEN l.credit - l.paid_amount ELSE 0 END),0) AS B30,
                    COALESCE(SUM(CASE WHEN l.due_date < CURRENT_DATE AND CURRENT_DATE - l.due_date BETWEEN 31 AND 60 THEN l.credit - l.paid_amount ELSE 0 END),0) AS B60,
                    COALESCE(SUM(CASE WHEN l.due_date < CURRENT_DATE AND CURRENT_DATE - l.due_date BETWEEN 61 AND 90 THEN l.credit - l.paid_amount ELSE 0 END),0) AS B90,
                    COALESCE(SUM(CASE WHEN l.due_date < CURRENT_DATE AND CURRENT_DATE - l.due_date > 90 THEN l.credit - l.paid_amount ELSE 0 END),0) AS Over90,
                    COALESCE(SUM(CASE WHEN l.due_date < CURRENT_DATE THEN l.credit - l.paid_amount ELSE 0 END),0) AS Overdue,
                    COALESCE(SUM(CASE WHEN l.due_date >= CURRENT_DATE AND l.due_date <= CURRENT_DATE + 7 THEN l.credit - l.paid_amount ELSE 0 END),0) AS Due7,
                    COALESCE(SUM(CASE WHEN l.due_date >= CURRENT_DATE AND l.due_date <= CURRENT_DATE + 30 THEN l.credit - l.paid_amount ELSE 0 END),0) AS Due30,
                    COUNT(DISTINCT l.supplier_id) AS SupplierCount
                FROM sim_ap_ledger l
                JOIN sim_suppliers s ON s.id = l.supplier_id
                WHERE s.tenant_id=@tenantId AND s.department_id=@departmentId
                  AND l.document_type='invoice' AND l.status IN ('open','partial')
                """, scope, cancellationToken: ct));

        // Total payable + large-balance count come from the denormalized
        // sim_suppliers.current_ap snapshot (same source the /suppliers list
        // already shows), not re-derived from the ledger — keeps this number
        // consistent with what the Supplier detail page displays.
        var totals = await conn.QuerySingleAsync<(decimal TotalAp, int LargeCount)>(
            new CommandDefinition("""
                SELECT COALESCE(SUM(current_ap),0) AS TotalAp,
                       COUNT(*) FILTER (WHERE current_ap >= 500000000) AS LargeCount
                FROM sim_suppliers
                WHERE tenant_id=@tenantId AND department_id=@departmentId AND status='active'
                """, scope, cancellationToken: ct));

        return new ApAgingSummary(
            SupplierCount: aging.SupplierCount,
            TotalPayable: totals.TotalAp,
            Current: aging.Current,
            Bucket_0_30: aging.B30,
            Bucket_31_60: aging.B60,
            Bucket_61_90: aging.B90,
            Over90: aging.Over90,
            OverdueAmount: aging.Overdue,
            DueWithin7Days: aging.Due7,
            DueWithin30Days: aging.Due30,
            LargeBalanceSupplierCount: totals.LargeCount);
    }

    public async Task<ExpenseApplyResult> ApplyExpenseAsync(
        Guid tenantId,
        Guid supplierId,
        Guid voucherId,
        string paymentDocumentNumber,
        DateOnly paymentDate,
        IReadOnlyList<ExpenseApplicationInput> applications,
        decimal discountAmount,
        string? discountReason,
        CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Idempotency guard: nếu đã có row tag voucher_id này → coi như đã apply.
        // Dùng cùng pattern như ApplyReceiptAsync (reference_note LIKE 'voucher:{id}%').
        const string dupSql = "SELECT COUNT(*) FROM sim_ap_ledger WHERE supplier_id = @supplierId AND reference_note LIKE @tag";
        var existing = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(dupSql,
                new { supplierId, tag = $"voucher:{voucherId:N}%" },
                transaction: tx,
                cancellationToken: ct));
        if (existing > 0)
        {
            await tx.RollbackAsync(ct);
            var supplier = await FindByIdAsync(tenantId, supplierId, ct)
                ?? throw new InvalidOperationException("Supplier not found");
            return new ExpenseApplyResult(
                PreviousBalance: supplier.CurrentAp,
                NewBalance: supplier.CurrentAp,
                TotalApplied: 0m,
                DiscountAmount: 0m,
                LedgerRowsInserted: 0);
        }

        var prevBalance = await conn.ExecuteScalarAsync<decimal>(
            new CommandDefinition(
                "SELECT current_ap FROM sim_suppliers WHERE tenant_id = @tenantId AND id = @supplierId FOR UPDATE",
                new { tenantId, supplierId },
                transaction: tx,
                cancellationToken: ct));

        decimal totalApplied = 0m;
        int rowsInserted = 0;
        Guid? soleInvoiceId = null;
        var distinctInvoiceIds = 0;

        foreach (var app in applications)
        {
            if (app.AppliedAmount <= 0m) continue;

            // Lock and settle the SPECIFIC open/partial AP invoice row this
            // application targets — same fix as Customer's ApplyReceiptAsync.
            var invoiceRow = await conn.QuerySingleOrDefaultAsync<(Guid Id, decimal Credit, decimal PaidAmount)?>(
                new CommandDefinition("""
                    SELECT id, credit, paid_amount FROM sim_ap_ledger
                    WHERE tenant_id=@tenantId AND supplier_id=@supplierId AND document_number=@docNumber
                      AND document_type='invoice' AND status IN ('open','partial')
                    FOR UPDATE
                    """, new { tenantId, supplierId, docNumber = app.DocumentNumber }, tx, cancellationToken: ct));

            Guid? allocatedInvoiceId = null;
            if (invoiceRow is { } inv)
            {
                var newPaid = Math.Min(inv.PaidAmount + app.AppliedAmount, inv.Credit);
                var newStatus = newPaid >= inv.Credit ? "paid" : "partial";
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE sim_ap_ledger SET paid_amount=@newPaid, status=@newStatus WHERE id=@id",
                    new { newPaid, newStatus, id = inv.Id }, tx, cancellationToken: ct));
                allocatedInvoiceId = inv.Id;
                if (soleInvoiceId != inv.Id) distinctInvoiceIds++;
                soleInvoiceId = inv.Id;
            }

            const string insertSql = """
                INSERT INTO sim_ap_ledger
                    (tenant_id, supplier_id, voucher_id, document_date, document_number, document_type,
                     debit, credit, balance_after, due_date, reference_note, allocated_invoice_id)
                VALUES
                    (@tenantId, @supplierId, NULL, @paymentDate, @paymentDocumentNumber, 'payment',
                     @debit, 0, NULL, NULL, @refNote, @allocatedInvoiceId)
                """;
            await conn.ExecuteAsync(new CommandDefinition(
                insertSql,
                new
                {
                    tenantId,
                    supplierId,
                    paymentDate,
                    paymentDocumentNumber,
                    debit = app.AppliedAmount,
                    refNote = $"voucher:{voucherId:N}|apply:{app.DocumentNumber}",
                    allocatedInvoiceId = (object?)allocatedInvoiceId ?? DBNull.Value,
                },
                transaction: tx,
                cancellationToken: ct));
            totalApplied += app.AppliedAmount;
            rowsInserted++;
        }

        // Discount received (chiết khấu thanh toán được hưởng) — same
        // single-invoice attribution rule as Customer's ApplyReceiptAsync.
        if (discountAmount > 0m)
        {
            Guid? discountAllocatedInvoiceId = null;
            if (soleInvoiceId is { } onlyInvoiceId && distinctInvoiceIds == 1)
            {
                var inv = await conn.QuerySingleOrDefaultAsync<(decimal Credit, decimal PaidAmount)?>(
                    new CommandDefinition("SELECT credit, paid_amount FROM sim_ap_ledger WHERE id=@id FOR UPDATE",
                        new { id = onlyInvoiceId }, tx, cancellationToken: ct));
                if (inv is { } row)
                {
                    var newPaid = Math.Min(row.PaidAmount + discountAmount, row.Credit);
                    var newStatus = newPaid >= row.Credit ? "paid" : "partial";
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE sim_ap_ledger SET paid_amount=@newPaid, status=@newStatus WHERE id=@id",
                        new { newPaid, newStatus, id = onlyInvoiceId }, tx, cancellationToken: ct));
                    discountAllocatedInvoiceId = onlyInvoiceId;
                }
            }

            const string discountSql = """
                INSERT INTO sim_ap_ledger
                    (tenant_id, supplier_id, voucher_id, document_date, document_number, document_type,
                     debit, credit, balance_after, due_date, reference_note, allocated_invoice_id)
                VALUES
                    (@tenantId, @supplierId, NULL, @paymentDate, @discountDoc, 'discount',
                     @debit, 0, NULL, NULL, @refNote, @allocatedInvoiceId)
                """;
            await conn.ExecuteAsync(new CommandDefinition(
                discountSql,
                new
                {
                    tenantId,
                    supplierId,
                    paymentDate,
                    discountDoc = $"CK-{paymentDocumentNumber}",
                    debit = discountAmount,
                    refNote = $"voucher:{voucherId:N}|discount:{discountReason ?? "auto"}",
                    allocatedInvoiceId = (object?)discountAllocatedInvoiceId ?? DBNull.Value,
                },
                transaction: tx,
                cancellationToken: ct));
            rowsInserted++;
        }

        var newBalance = await RecomputeCurrentApAsync(conn, tx, tenantId, supplierId, ct);

        await tx.CommitAsync(ct);

        return new ExpenseApplyResult(
            PreviousBalance: prevBalance,
            NewBalance: newBalance,
            TotalApplied: totalApplied,
            DiscountAmount: discountAmount,
            LedgerRowsInserted: rowsInserted);
    }

    public async Task<ExpenseUnapplyResult> UnapplyExpenseAsync(
        Guid tenantId,
        Guid supplierId,
        Guid voucherId,
        CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var prevBalance = await conn.ExecuteScalarAsync<decimal>(
            new CommandDefinition(
                "SELECT current_ap FROM sim_suppliers WHERE tenant_id = @tenantId AND id = @supplierId FOR UPDATE",
                new { tenantId, supplierId },
                transaction: tx,
                cancellationToken: ct));

        var reversals = (await conn.QueryAsync<(Guid InvoiceId, decimal Amount)>(new CommandDefinition("""
            SELECT allocated_invoice_id AS InvoiceId, debit AS Amount
            FROM sim_ap_ledger
            WHERE supplier_id = @supplierId AND reference_note LIKE @tag AND allocated_invoice_id IS NOT NULL
            """, new { supplierId, tag = $"voucher:{voucherId:N}%" }, tx, cancellationToken: ct))).AsList();

        foreach (var group in reversals.GroupBy(r => r.InvoiceId))
        {
            var totalReverse = group.Sum(r => r.Amount);
            var invoiceRow = await conn.QuerySingleOrDefaultAsync<(decimal Credit, decimal PaidAmount)?>(
                new CommandDefinition("SELECT credit, paid_amount FROM sim_ap_ledger WHERE id=@id FOR UPDATE",
                    new { id = group.Key }, tx, cancellationToken: ct));
            if (invoiceRow is not { } inv) continue;
            var restoredPaid = Math.Max(inv.PaidAmount - totalReverse, 0m);
            var restoredStatus = restoredPaid <= 0m ? "open" : restoredPaid < inv.Credit ? "partial" : "paid";
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE sim_ap_ledger SET paid_amount=@restoredPaid, status=@restoredStatus WHERE id=@id",
                new { restoredPaid, restoredStatus, id = group.Key }, tx, cancellationToken: ct));
        }

        const string deleteSql = """
            DELETE FROM sim_ap_ledger
             WHERE supplier_id = @supplierId
               AND reference_note LIKE @tag
            """;
        var deleted = await conn.ExecuteAsync(
            new CommandDefinition(deleteSql,
                new { supplierId, tag = $"voucher:{voucherId:N}%" },
                transaction: tx,
                cancellationToken: ct));

        if (deleted == 0)
        {
            await tx.RollbackAsync(ct);
            return new ExpenseUnapplyResult(prevBalance, prevBalance, 0);
        }

        var newBalance = await RecomputeCurrentApAsync(conn, tx, tenantId, supplierId, ct);

        await tx.CommitAsync(ct);

        return new ExpenseUnapplyResult(prevBalance, newBalance, deleted);
    }

    public async Task<BookInvoiceResult> BookInvoiceAsync(
        Guid tenantId,
        Guid supplierId,
        Guid voucherId,
        string invoiceDocumentNumber,
        DateOnly invoiceDate,
        DateOnly? dueDate,
        decimal invoiceTotal,
        Guid? contractId,
        Guid? contractPaymentScheduleId,
        CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var prevBalance = await conn.ExecuteScalarAsync<decimal>(
            new CommandDefinition(
                "SELECT current_ap FROM sim_suppliers WHERE tenant_id = @tenantId AND id = @supplierId FOR UPDATE",
                new { tenantId, supplierId },
                transaction: tx,
                cancellationToken: ct));

        // Idempotency: real UNIQUE(tenant_id, voucher_id) constraint
        // (migration 045) — ON CONFLICT DO NOTHING makes a second
        // apply-expense(invoice_vat_in) call for the same voucherId a
        // guaranteed DB-level no-op.
        const string insertSql = """
            INSERT INTO sim_ap_ledger
                (tenant_id, supplier_id, voucher_id, document_date, document_number, document_type,
                 debit, credit, balance_after, due_date, reference_note,
                 paid_amount, status, contract_id, contract_payment_schedule_id)
            VALUES
                (@tenantId, @supplierId, @voucherId, @invoiceDate, @invoiceDocumentNumber, 'invoice',
                 0, @credit, NULL, @dueDate, @refNote,
                 0, 'open', @contractId, @contractPaymentScheduleId)
            ON CONFLICT (tenant_id, voucher_id) WHERE voucher_id IS NOT NULL DO NOTHING
            """;
        var inserted = await conn.ExecuteAsync(new CommandDefinition(
            insertSql,
            new
            {
                tenantId,
                supplierId,
                voucherId,
                invoiceDate,
                invoiceDocumentNumber,
                credit = invoiceTotal,
                dueDate = (object?)dueDate ?? DBNull.Value,
                refNote = $"voucher:{voucherId:N}|book_invoice",
                contractId = (object?)contractId ?? DBNull.Value,
                contractPaymentScheduleId = (object?)contractPaymentScheduleId ?? DBNull.Value,
            },
            transaction: tx,
            cancellationToken: ct));

        if (inserted == 0)
        {
            await tx.RollbackAsync(ct);
            return new BookInvoiceResult(
                PreviousBalance: prevBalance,
                NewBalance: prevBalance,
                InvoiceTotal: 0m,
                LedgerRowsInserted: 0);
        }

        var newBalance = await RecomputeCurrentApAsync(conn, tx, tenantId, supplierId, ct);

        await tx.CommitAsync(ct);

        return new BookInvoiceResult(
            PreviousBalance: prevBalance,
            NewBalance: newBalance,
            InvoiceTotal: invoiceTotal,
            LedgerRowsInserted: 1);
    }

    // Derived, not incremented — mirrors Customer's RecomputeCurrentArAsync.
    // credit is the invoice amount for AP rows (debit is a payment/discount).
    internal static async Task<decimal> RecomputeCurrentApAsync(
        Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, Guid tenantId, Guid supplierId, CancellationToken ct)
    {
        var newBalance = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition("""
            SELECT COALESCE(SUM(credit - paid_amount), 0)
            FROM sim_ap_ledger
            WHERE tenant_id = @tenantId AND supplier_id = @supplierId
              AND document_type = 'invoice' AND status IN ('open','partial')
            """, new { tenantId, supplierId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE sim_suppliers SET current_ap = @newBalance WHERE tenant_id = @tenantId AND id = @supplierId",
            new { newBalance, tenantId, supplierId }, tx, cancellationToken: ct));
        return newBalance;
    }
}
