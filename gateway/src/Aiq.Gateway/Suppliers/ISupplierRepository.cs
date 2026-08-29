using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Suppliers;

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
    decimal CurrentAp);

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
    string? ReferenceNote);

// Kế toán trưởng cần để chấm rủi ro NCC + lên kế hoạch chi.
public sealed record SupplierPayablesReport(
    SupplierRecord Supplier,
    decimal CurrentBalance331,
    IReadOnlyList<ApLedgerRow> OpenInvoices,
    ApAgingBuckets Aging,
    decimal OverdueAmount,
    DateOnly? LastPaymentDate,
    int PaymentTermDefault);

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

public interface ISupplierRepository
{
    Task<IReadOnlyList<SupplierRecord>> ListAsync(Guid tenantId, string? country, string? search, CancellationToken ct);
    Task<SupplierRecord?> FindByMstAsync(Guid tenantId, string mst, CancellationToken ct);
    Task<SupplierRecord?> FindByIdAsync(Guid tenantId, Guid id, CancellationToken ct);
    Task<SupplierPayablesReport?> GetPayablesAsync(Guid tenantId, Guid supplierId, CancellationToken ct);
    Task<PurchaseOrderMatch?> MatchInvoiceToPurchaseOrderAsync(Guid tenantId, string documentNumber, CancellationToken ct);

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
    // credit row in sim_ap_ledger (increases AP) and updates
    // sim_suppliers.current_ap += invoiceTotal. Idempotent by voucherId.
    // Semantics: Có 331 tăng phải trả NCC.
    Task<BookInvoiceResult> BookInvoiceAsync(
        Guid tenantId,
        Guid supplierId,
        Guid voucherId,
        string invoiceDocumentNumber,
        DateOnly invoiceDate,
        DateOnly? dueDate,
        decimal invoiceTotal,
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
            current_ap            AS CurrentAp
        """;

    public async Task<IReadOnlyList<SupplierRecord>> ListAsync(
        Guid tenantId, string? country, string? search, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var sql = $"""
            SELECT {SupplierCols}
            FROM sim_suppliers
            WHERE tenant_id = @tenantId
              AND (@country IS NULL OR country = @country)
              AND (@search IS NULL OR name ILIKE '%' || @search || '%' OR mst LIKE @search || '%' OR supplier_code ILIKE @search || '%')
            ORDER BY current_ap DESC, name
            LIMIT 200
            """;
        var rows = await conn.QueryAsync<SupplierRecord>(sql, new { tenantId, country, search });
        return rows.AsList();
    }

    public async Task<SupplierRecord?> FindByMstAsync(Guid tenantId, string mst, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var sql = $"SELECT {SupplierCols} FROM sim_suppliers WHERE tenant_id = @tenantId AND mst = @mst LIMIT 1";
        return await conn.QueryFirstOrDefaultAsync<SupplierRecord>(sql, new { tenantId, mst });
    }

    public async Task<SupplierRecord?> FindByIdAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var sql = $"SELECT {SupplierCols} FROM sim_suppliers WHERE tenant_id = @tenantId AND id = @id LIMIT 1";
        return await conn.QueryFirstOrDefaultAsync<SupplierRecord>(sql, new { tenantId, id });
    }

    public async Task<SupplierPayablesReport?> GetPayablesAsync(
        Guid tenantId, Guid supplierId, CancellationToken ct)
    {
        var supplier = await FindByIdAsync(tenantId, supplierId, ct);
        if (supplier is null) return null;

        await using var conn = await _factory.OpenAsync(ct);

        // Open payables: unpaid supplier invoices (debit = 0). Ordered by due
        // date so agent thấy dòng cấp bách nhất trước.
        var ledgerSql = """
            SELECT id             AS Id,
                   document_date  AS DocumentDate,
                   document_number AS DocumentNumber,
                   document_type  AS DocumentType,
                   debit          AS Debit,
                   credit         AS Credit,
                   balance_after  AS BalanceAfter,
                   due_date       AS DueDate,
                   CASE WHEN CURRENT_DATE > due_date AND debit = 0
                        THEN (CURRENT_DATE - due_date)
                        ELSE 0
                   END AS DaysOverdue,
                   reference_note AS ReferenceNote
            FROM sim_ap_ledger
            WHERE supplier_id = @supplierId
              AND (document_type = 'invoice' AND debit = 0)
            ORDER BY due_date NULLS LAST, document_date
            """;
        var rowsEnum = await conn.QueryAsync<ApLedgerRow>(
            new CommandDefinition(ledgerSql, new { supplierId }, cancellationToken: ct));
        var rows = new List<ApLedgerRow>(rowsEnum);

        decimal bucketCurrent = 0, b30 = 0, b60 = 0, b90 = 0, over90 = 0;
        decimal overdue = 0;
        foreach (var row in rows)
        {
            if (row.DaysOverdue == 0) bucketCurrent += row.Credit;
            else if (row.DaysOverdue <= 30) { b30 += row.Credit; overdue += row.Credit; }
            else if (row.DaysOverdue <= 60) { b60 += row.Credit; overdue += row.Credit; }
            else if (row.DaysOverdue <= 90) { b90 += row.Credit; overdue += row.Credit; }
            else { over90 += row.Credit; overdue += row.Credit; }
        }

        var lastPaymentSql = """
            SELECT MAX(document_date) FROM sim_ap_ledger
            WHERE supplier_id = @supplierId AND debit > 0
            """;
        var lastPayment = await conn.QueryFirstOrDefaultAsync<DateOnly?>(lastPaymentSql, new { supplierId });

        return new SupplierPayablesReport(
            supplier,
            CurrentBalance331: supplier.CurrentAp,
            OpenInvoices: rows,
            Aging: new ApAgingBuckets(bucketCurrent, b30, b60, b90, over90),
            OverdueAmount: overdue,
            LastPaymentDate: lastPayment,
            PaymentTermDefault: supplier.PaymentTermsDays);
    }

    public async Task<PurchaseOrderMatch?> MatchInvoiceToPurchaseOrderAsync(
        Guid tenantId, string documentNumber, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);

        // Find PO where matched_invoice_number matches. If none → invoice_vat_in
        // không có PO tương ứng (mua vặt, không qua đơn đặt hàng).
        const string poSql = """
            SELECT id                    AS Id,
                   po_number             AS PoNumber,
                   po_date               AS PoDate,
                   supplier_code         AS SupplierCode,
                   status                AS Status,
                   currency              AS Currency,
                   COALESCE(exchange_rate, 1) AS ExchangeRate,
                   COALESCE(subtotal_vnd, 0)  AS SubtotalVnd,
                   total_amount          AS TotalAmount,
                   incoterm              AS Incoterm,
                   payment_terms         AS PaymentTerms,
                   COALESCE(payment_terms_days, 0) AS PaymentTermsDays,
                   COALESCE(delivery_note_ref, goods_received_note) AS DeliveryNoteRef,
                   matched_invoice_number AS MatchedInvoiceNumber,
                   notes                 AS Notes
            FROM sim_purchase_orders
            WHERE tenant_id = @tenantId
              AND matched_invoice_number = @documentNumber
            LIMIT 1
            """;
        var po = await conn.QueryFirstOrDefaultAsync<PurchaseOrderMatchRow>(
            new CommandDefinition(poSql, new { tenantId, documentNumber }, cancellationToken: ct));
        if (po is null) return null;

        // GRN — hiện tại sim_purchase_orders.goods_received_note giữ text
        // (chưa có sim_goods_received_notes table riêng — Sprint 4). Render lại
        // như 1 row từ chính cột đó.
        GoodsReceivedNoteRow? grn = null;
        if (!string.IsNullOrWhiteSpace(po.DeliveryNoteRef))
        {
            grn = new GoodsReceivedNoteRow(
                GrnNumber: po.DeliveryNoteRef,
                GrnDate: po.PoDate,             // best-effort trong Sprint 1; sẽ replace bằng bảng riêng ở Sprint 4
                WarehouseTo: null,
                DeliveredBy: null,
                ReceivedBy: null,
                Status: "received");
        }

        var quality = grn is null ? "partial" : "exact";
        return new PurchaseOrderMatch(po, grn, quality);
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

        // Rolling balance snapshot cho balance_after per row.
        var runningBalance = prevBalance;

        foreach (var app in applications)
        {
            if (app.AppliedAmount <= 0m) continue;
            runningBalance -= app.AppliedAmount;
            const string insertSql = """
                INSERT INTO sim_ap_ledger
                    (tenant_id, supplier_id, voucher_id, document_date, document_number, document_type,
                     debit, credit, balance_after, due_date, reference_note)
                VALUES
                    (@tenantId, @supplierId, @voucherId, @paymentDate, @paymentDocumentNumber, 'payment',
                     @debit, 0, @balanceAfter, NULL, @refNote)
                """;
            await conn.ExecuteAsync(new CommandDefinition(
                insertSql,
                new
                {
                    tenantId,
                    supplierId,
                    voucherId,
                    paymentDate,
                    paymentDocumentNumber,
                    debit = app.AppliedAmount,
                    balanceAfter = runningBalance,
                    refNote = $"voucher:{voucherId:N}|apply:{app.DocumentNumber}",
                },
                transaction: tx,
                cancellationToken: ct));
            totalApplied += app.AppliedAmount;
            rowsInserted++;
        }

        // Discount received (chiết khấu thanh toán được hưởng): giảm phải trả
        // NCC, book Nợ 331 / Có 5153. Trong ledger 331: 1 debit row.
        if (discountAmount > 0m)
        {
            runningBalance -= discountAmount;
            const string discountSql = """
                INSERT INTO sim_ap_ledger
                    (tenant_id, supplier_id, voucher_id, document_date, document_number, document_type,
                     debit, credit, balance_after, due_date, reference_note)
                VALUES
                    (@tenantId, @supplierId, @voucherId, @paymentDate, @discountDoc, 'discount',
                     @debit, 0, @balanceAfter, NULL, @refNote)
                """;
            await conn.ExecuteAsync(new CommandDefinition(
                discountSql,
                new
                {
                    tenantId,
                    supplierId,
                    voucherId,
                    paymentDate,
                    discountDoc = $"CK-{paymentDocumentNumber}",
                    debit = discountAmount,
                    balanceAfter = runningBalance,
                    refNote = $"voucher:{voucherId:N}|discount:{discountReason ?? "auto"}",
                },
                transaction: tx,
                cancellationToken: ct));
            rowsInserted++;
        }

        // Update denormalized current_ap snapshot on supplier row.
        var newBalance = prevBalance - totalApplied - discountAmount;
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE sim_suppliers SET current_ap = @newBalance WHERE tenant_id = @tenantId AND id = @supplierId",
            new { newBalance, tenantId, supplierId },
            transaction: tx,
            cancellationToken: ct));

        await tx.CommitAsync(ct);

        return new ExpenseApplyResult(
            PreviousBalance: prevBalance,
            NewBalance: newBalance,
            TotalApplied: totalApplied,
            DiscountAmount: discountAmount,
            LedgerRowsInserted: rowsInserted);
    }

    public async Task<BookInvoiceResult> BookInvoiceAsync(
        Guid tenantId,
        Guid supplierId,
        Guid voucherId,
        string invoiceDocumentNumber,
        DateOnly invoiceDate,
        DateOnly? dueDate,
        decimal invoiceTotal,
        CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Idempotency guard: nếu voucher_id đã có row → coi như đã book.
        const string dupSql = "SELECT COUNT(*) FROM sim_ap_ledger WHERE voucher_id = @voucherId";
        var existing = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(dupSql, new { voucherId }, transaction: tx, cancellationToken: ct));
        if (existing > 0)
        {
            await tx.RollbackAsync(ct);
            var supplier = await FindByIdAsync(tenantId, supplierId, ct)
                ?? throw new InvalidOperationException("Supplier not found");
            return new BookInvoiceResult(
                PreviousBalance: supplier.CurrentAp,
                NewBalance: supplier.CurrentAp,
                InvoiceTotal: 0m,
                LedgerRowsInserted: 0);
        }

        var prevBalance = await conn.ExecuteScalarAsync<decimal>(
            new CommandDefinition(
                "SELECT current_ap FROM sim_suppliers WHERE tenant_id = @tenantId AND id = @supplierId FOR UPDATE",
                new { tenantId, supplierId },
                transaction: tx,
                cancellationToken: ct));

        var newBalance = prevBalance + invoiceTotal;

        const string insertSql = """
            INSERT INTO sim_ap_ledger
                (tenant_id, supplier_id, voucher_id, document_date, document_number, document_type,
                 debit, credit, balance_after, due_date, reference_note)
            VALUES
                (@tenantId, @supplierId, @voucherId, @invoiceDate, @invoiceDocumentNumber, 'invoice',
                 0, @credit, @balanceAfter, @dueDate, @refNote)
            """;
        await conn.ExecuteAsync(new CommandDefinition(
            insertSql,
            new
            {
                tenantId,
                supplierId,
                voucherId,
                invoiceDate,
                invoiceDocumentNumber,
                credit = invoiceTotal,
                balanceAfter = newBalance,
                dueDate = (object?)dueDate ?? DBNull.Value,
                refNote = $"voucher:{voucherId:N}|book_invoice",
            },
            transaction: tx,
            cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE sim_suppliers SET current_ap = @newBalance WHERE tenant_id = @tenantId AND id = @supplierId",
            new { newBalance, tenantId, supplierId },
            transaction: tx,
            cancellationToken: ct));

        await tx.CommitAsync(ct);

        return new BookInvoiceResult(
            PreviousBalance: prevBalance,
            NewBalance: newBalance,
            InvoiceTotal: invoiceTotal,
            LedgerRowsInserted: 1);
    }
}
