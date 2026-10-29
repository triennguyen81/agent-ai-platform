using System.Text.Json;
using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Vouchers;

public interface IVoucherRepository
{
    /// <summary>
    /// Inserts a new voucher. Returns null and writes nothing when a voucher
    /// with the same (seller_mst, document_number, document_date) already
    /// exists in the tenant — never an upsert over it (chứng từ flow v2 PR1).
    /// </summary>
    Task<VoucherDetail?> CreateAsync(
        Guid tenantId,
        Guid departmentId,
        Guid userId,
        CreateVoucherRequest request,
        CancellationToken ct);

    Task<Guid> CreateStubAsync(
        Guid tenantId,
        Guid departmentId,
        Guid userId,
        string voucherType,
        string sourceFilePath,
        string sourceFileName,
        string sourceMimeType,
        CancellationToken ct);

    Task<VoucherDetail?> FindAsync(
        Guid tenantId,
        Guid userId,
        Guid id,
        CancellationToken ct);

    Task<VoucherDetail?> FindInDepartmentAsync(Guid tenantId, Guid departmentId, Guid id, CancellationToken ct);
    Task<IReadOnlyList<VoucherRecord>> ListInDepartmentAsync(Guid tenantId, Guid departmentId, string? voucherType, string? statusFilter, string? search, int limit, int offset, CancellationToken ct);

    Task<IReadOnlyList<VoucherRecord>> ListAsync(
        Guid tenantId,
        Guid userId,
        string? voucherType,
        string? statusFilter,
        string? search,
        string? sellerMst,
        DateOnly? documentDateFrom,
        DateOnly? documentDateTo,
        int limit,
        int offset,
        CancellationToken ct);

    Task<VoucherRecord?> UpdateAsync(
        Guid tenantId,
        Guid userId,
        Guid id,
        UpdateVoucherRequest request,
        CancellationToken ct);

    /// <summary>
    /// ADMIN voucher-type decision: sets voucher_type and
    /// extracted_fields.voucher_type_classification in one statement. Refuses
    /// (returns false) once the voucher carries an accounting effect — status
    /// 'verified' or a live cash posting — a posted type is never rewritten.
    /// </summary>
    Task<bool> SetVoucherTypeClassificationAsync(
        Guid tenantId, Guid departmentId, Guid id, string voucherType, string classificationJson, CancellationToken ct,
        bool reprocessing = false);

    /// <summary>
    /// Writes an extraction result over the voucher. Returns false and writes
    /// nothing once it carries an accounting effect (<see cref="VoucherPostingGuard"/>).
    /// </summary>
    Task<bool> ReplaceExtractedAsync(
        Guid tenantId, Guid userId, Guid id,
        CreateVoucherRequest request,
        CancellationToken ct);

    Task<IReadOnlyList<VoucherJournalRecord>> SaveJournalSuggestionsAsync(
        Guid tenantId, Guid departmentId, Guid voucherId,
        SaveJournalSuggestionsRequest request,
        CancellationToken ct);

    Task<VoucherJournalRecord?> AcceptJournalAsync(
        Guid tenantId, Guid departmentId, Guid voucherId,
        Guid entryId,
        Guid userId,
        AcceptJournalRequest request,
        CancellationToken ct);

    /// <summary>False (nothing deleted) when missing or posted (<see cref="VoucherPostingGuard"/>).</summary>
    Task<bool> DeleteAsync(Guid tenantId, Guid userId, Guid id, CancellationToken ct);

    // Tax verification state is owned by ITaxVerificationRepository
    // (Vouchers/TaxVerification.cs): ADMIN attestation with evidence only.

    // R06 — records an explicit Contract -> Invoice/Receipt link. Returns
    // false (no-op) if the voucher, or the contract (and installment, when
    // given), don't all resolve within the SAME tenant+department — this is
    // the tenant-isolation check; a caller can never link across tenants or
    // departments even if they somehow know another tenant's contract id.
    Task<bool> LinkContractAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid contractId, Guid? installmentId, CancellationToken ct);

    // UC-ACC-001 E2: dedup by (seller_mst + document_number) within a tenant.
    // Nullable tenant vì có thể gọi từ Hangfire job (no user context).
    Task<VoucherRecord?> FindDuplicateAsync(
        Guid? tenantId,
        string sellerMst,
        string documentNumber,
        Guid excludeId,
        CancellationToken ct);

    // P1 async OCR: called from Hangfire job (no tenant/user context).
    // Updates just status + rawText + confidence + errorMessage. Idempotent.
    // Returns false and writes nothing on a posted voucher (VoucherPostingGuard).
    Task<bool> UpdateOcrResultAsync(
        Guid id,
        string status,
        string? rawText,
        decimal? ocrConfidence,
        string? errorMessage,
        CancellationToken ct);

    // AG-02: the background OCR job — including a Hangfire retry after an
    // Agent failure — may only write while the voucher is still waiting for
    // OCR ('processing', or 'ocr_unavailable' from an earlier attempt).
    // Returns false and writes nothing once the voucher has moved on
    // (paste-fallback text, extraction, review, posting).
    Task<bool> TryUpdateOcrJobResultAsync(
        Guid id,
        string status,
        string? rawText,
        decimal? ocrConfidence,
        string? errorMessage,
        CancellationToken ct);

    // Sprint 6' A6: aggregate status counts cho báo cáo tổng hợp
    // "đã đối soát / còn treo / quá hạn". overdueThresholdDate = ngày
    // ranh giới quá hạn (voucher chưa Accept trước ngày này = quá hạn).
    Task<VoucherStatusReport> GetStatusReportAsync(
        Guid tenantId,
        DateOnly overdueThresholdDate,
        CancellationToken ct);
}

public sealed record VoucherStatusReport(
    int VerifiedCount,
    int PendingCount,
    int OverdueCount,
    int FailedCount,
    int RejectedCount);

public sealed class VoucherRepository : IVoucherRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public VoucherRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    private const string VoucherSelectColumns = """
            id                AS Id,
            user_id           AS UserId,
            tenant_id         AS TenantId,
            department_id     AS DepartmentId,
            voucher_type      AS VoucherType,
            status            AS Status,
            document_number   AS DocumentNumber,
            document_series   AS DocumentSeries,
            document_date     AS DocumentDate,
            seller_name       AS SellerName,
            seller_mst        AS SellerMst,
            seller_address    AS SellerAddress,
            buyer_name        AS BuyerName,
            buyer_mst         AS BuyerMst,
            buyer_address     AS BuyerAddress,
            subtotal          AS Subtotal,
            vat_rate          AS VatRate,
            vat_amount        AS VatAmount,
            discount_amount   AS DiscountAmount,
            total_amount      AS TotalAmount,
            currency          AS Currency,
            amount_in_words   AS AmountInWords,
            payment_method    AS PaymentMethod,
            tct_verified      AS TctVerified,
            tct_verified_at   AS TctVerifiedAt,
            tax_verification_status     AS TaxVerificationStatus,
            tax_verification_reason     AS TaxVerificationReason,
            tax_verification_checked_at AS TaxVerificationCheckedAt,
            tax_verified_by             AS TaxVerifiedBy,
            tax_verification_fingerprint AS TaxVerificationFingerprint,
            contract_id             AS ContractId,
            contract_installment_id AS ContractInstallmentId,
            source_file_path  AS SourceFilePath,
            source_file_name  AS SourceFileName,
            source_mime_type  AS SourceMimeType,
            raw_ocr_text      AS RawOcrText,
            ocr_confidence    AS OcrConfidence,
            error_message     AS ErrorMessage,
            extracted_fields::text AS ExtractedFieldsJson,
            tags              AS Tags,
            created_at        AS CreatedAt,
            updated_at        AS UpdatedAt,
            lifecycle_state   AS LifecycleState,
            lifecycle_version AS LifecycleVersion,
            submitted_by      AS SubmittedBy,
            submitted_at      AS SubmittedAt,
            approved_by       AS ApprovedBy,
            approved_at       AS ApprovedAt,
            posted_at         AS PostedAt,
            deleted_at        AS DeletedAt,
            supersedes_id     AS SupersedesId
        """;

    private const string LineItemSelectColumns = """
            id             AS Id,
            voucher_id     AS VoucherId,
            ordinal        AS Ordinal,
            item_code      AS ItemCode,
            description    AS Description,
            unit           AS Unit,
            quantity       AS Quantity,
            unit_price     AS UnitPrice,
            subtotal       AS Subtotal,
            vat_rate       AS VatRate,
            vat_amount     AS VatAmount,
            total_amount   AS TotalAmount,
            created_at     AS CreatedAt
        """;

    private const string JournalSelectColumns = """
            id              AS Id,
            voucher_id      AS VoucherId,
            entry_no        AS EntryNo,
            debit_account   AS DebitAccount,
            credit_account  AS CreditAccount,
            amount          AS Amount,
            source          AS Source,
            description     AS Description,
            confidence      AS Confidence,
            accepted_by     AS AcceptedBy,
            accepted_at     AS AcceptedAt,
            rejected_at     AS RejectedAt,
            reject_reason   AS RejectReason,
            created_at      AS CreatedAt
        """;

    public async Task<Guid> CreateStubAsync(
        Guid tenantId,
        Guid departmentId,
        Guid userId,
        string voucherType,
        string sourceFilePath,
        string sourceFileName,
        string sourceMimeType,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO vouchers (
                tenant_id, department_id, user_id, voucher_type, status,
                source_file_path, source_file_name, source_mime_type
            ) VALUES (
                @tenantId, @departmentId, @userId, @voucherType, 'processing',
                @sourceFilePath, @sourceFileName, @sourceMimeType
            )
            RETURNING id;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleAsync<Guid>(
            new CommandDefinition(
                sql,
                new
                {
                    tenantId,
                    departmentId,
                    userId,
                    voucherType,
                    sourceFilePath,
                    sourceFileName,
                    sourceMimeType,
                },
                cancellationToken: ct));
    }

    public async Task<VoucherDetail?> CreateAsync(
        Guid tenantId,
        Guid departmentId,
        Guid userId,
        CreateVoucherRequest request,
        CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var extractedJson = SerializeExtracted(request.ExtractedFields);

        var insertSql = $"""
            INSERT INTO vouchers (
                tenant_id, department_id, user_id, voucher_type, status,
                document_number, document_series, document_date,
                seller_name, seller_mst, seller_address,
                buyer_name, buyer_mst, buyer_address,
                payment_method, subtotal, vat_rate, vat_amount,
                discount_amount, total_amount, currency, amount_in_words,
                source_file_path, source_file_name, source_mime_type,
                raw_ocr_text, ocr_confidence, extracted_fields, tags
            ) VALUES (
                @tenantId, @departmentId, @userId, @voucherType, 'extracted',
                @documentNumber, @documentSeries, @documentDate,
                @sellerName, @sellerMst, @sellerAddress,
                @buyerName, @buyerMst, @buyerAddress,
                @paymentMethod, @subtotal, @vatRate, @vatAmount,
                @discountAmount, @totalAmount, @currency, @amountInWords,
                @sourceFilePath, @sourceFileName, @sourceMimeType,
                @rawOcrText, @ocrConfidence, @extractedJson::jsonb, @tags
            )
            ON CONFLICT (tenant_id, seller_mst, document_number, document_date)
            WHERE tenant_id IS NOT NULL
              AND seller_mst IS NOT NULL
              AND document_number IS NOT NULL
              AND document_date IS NOT NULL
            DO NOTHING
            RETURNING {VoucherSelectColumns};
            """;

        var parameters = new
        {
            tenantId,
            departmentId,
            userId,
            voucherType = request.VoucherType,
            documentNumber = request.DocumentNumber,
            documentSeries = request.DocumentSeries,
            documentDate = request.DocumentDate,
            sellerName = request.SellerName,
            sellerMst = request.SellerMst,
            sellerAddress = request.SellerAddress,
            buyerName = request.BuyerName,
            buyerMst = request.BuyerMst,
            buyerAddress = request.BuyerAddress,
            paymentMethod = request.PaymentMethod,
            subtotal = request.Subtotal,
            vatRate = request.VatRate,
            vatAmount = request.VatAmount,
            discountAmount = request.DiscountAmount,
            totalAmount = request.TotalAmount,
            currency = request.Currency ?? "VND",
            amountInWords = request.AmountInWords,
            sourceFilePath = request.SourceFilePath,
            sourceFileName = request.SourceFileName,
            sourceMimeType = request.SourceMimeType,
            rawOcrText = request.RawOcrText,
            ocrConfidence = request.OcrConfidence,
            extractedJson,
            tags = request.Tags ?? Array.Empty<string>(),
        };

        // An existing voucher with the same identity is never overwritten —
        // it may be posted, or belong to another department (P3).
        var voucher = await conn.QuerySingleOrDefaultAsync<VoucherRecord>(
            new CommandDefinition(insertSql, parameters, transaction: tx, cancellationToken: ct));
        if (voucher is null) { await tx.RollbackAsync(ct); return null; }

        var lineItems = new List<VoucherLineItemRecord>();
        if (request.LineItems is { Count: > 0 })
        {
            var insertLine = $"""
                INSERT INTO voucher_line_items (
                    voucher_id, ordinal, item_code, description, unit,
                    quantity, unit_price, subtotal, vat_rate, vat_amount, total_amount
                ) VALUES (
                    @voucherId, @ordinal, @itemCode, @description, @unit,
                    @quantity, @unitPrice, @subtotal, @vatRate, @vatAmount, @totalAmount
                )
                RETURNING {LineItemSelectColumns};
                """;

            foreach (var li in request.LineItems)
            {
                var inserted = await conn.QuerySingleAsync<VoucherLineItemRecord>(
                    new CommandDefinition(
                        insertLine,
                        new
                        {
                            voucherId = voucher.Id,
                            li.Ordinal,
                            itemCode = li.ItemCode,
                            li.Description,
                            li.Unit,
                            li.Quantity,
                            unitPrice = li.UnitPrice,
                            li.Subtotal,
                            vatRate = li.VatRate,
                            vatAmount = li.VatAmount,
                            totalAmount = li.TotalAmount,
                        },
                        transaction: tx,
                        cancellationToken: ct));
                lineItems.Add(inserted);
            }
        }

        await tx.CommitAsync(ct);
        return new VoucherDetail(voucher, lineItems, Array.Empty<VoucherJournalRecord>());
    }

    public async Task<bool> SetVoucherTypeClassificationAsync(
        Guid tenantId, Guid departmentId, Guid id, string voucherType, string classificationJson, CancellationToken ct,
        bool reprocessing = false)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE vouchers v
               SET voucher_type = @voucherType,
                   status = CASE WHEN @reprocessing THEN 'processing' ELSE v.status END,
                   extracted_fields = jsonb_set(COALESCE(v.extracted_fields, '{}'::jsonb), '{voucher_type_classification}', @classificationJson::jsonb, true)
                                      || jsonb_build_object('voucher_type', @voucherType)
             WHERE v.id = @id AND v.tenant_id = @tenantId AND v.department_id = @departmentId
               AND v.status <> 'verified'
               AND NOT EXISTS (SELECT 1 FROM cash_transactions c WHERE c.voucher_id = v.id AND c.status = 'posted')
               AND v.lifecycle_state NOT IN ('pending_approval', 'approved', 'reversed') AND v.deleted_at IS NULL
            """, new { id, tenantId, departmentId, voucherType, classificationJson, reprocessing }, tx, cancellationToken: ct));
        // A type change changes which invoices the voucher stands for.
        if (rows == 1) await TaxVerificationRepository.InvalidateIfIdentityChangedAsync(conn, tx, tenantId, id, ct);
        await tx.CommitAsync(ct);
        return rows == 1;
    }

    public async Task<bool> ReplaceExtractedAsync(
        Guid tenantId, Guid userId, Guid id,
        CreateVoucherRequest request,
        CancellationToken ct)
    {
        // Update existing stub row with extracted fields.
        var extractedJson = SerializeExtracted(request.ExtractedFields);

        const string updateSql = $"""
            UPDATE vouchers SET
                status           = 'extracted',
                voucher_type     = @voucherType,
                document_number  = @documentNumber,
                document_series  = @documentSeries,
                document_date    = @documentDate,
                seller_name      = @sellerName,
                seller_mst       = @sellerMst,
                seller_address   = @sellerAddress,
                buyer_name       = @buyerName,
                buyer_mst        = @buyerMst,
                buyer_address    = @buyerAddress,
                payment_method   = @paymentMethod,
                subtotal         = @subtotal,
                vat_rate         = @vatRate,
                vat_amount       = @vatAmount,
                discount_amount  = @discountAmount,
                total_amount     = @totalAmount,
                currency         = @currency,
                amount_in_words  = @amountInWords,
                raw_ocr_text     = @rawOcrText,
                ocr_confidence   = COALESCE(@ocrConfidence, ocr_confidence),
                extracted_fields = @extractedJson::jsonb,
                tags             = @tags
            WHERE id = @id AND tenant_id = @tenantId AND user_id = @userId
              AND {VoucherPostingGuard.NotPostedSql}
              AND {Aiq.Gateway.Vouchers.Lifecycle.VoucherLifecycleGuard.NotLockedSql};
            """;

        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var updated = await conn.ExecuteAsync(new CommandDefinition(
            updateSql,
            new
            {
                id, tenantId, userId,
                voucherType = request.VoucherType,
                documentNumber = request.DocumentNumber,
                documentSeries = request.DocumentSeries,
                documentDate = request.DocumentDate,
                sellerName = request.SellerName,
                sellerMst = request.SellerMst,
                sellerAddress = request.SellerAddress,
                buyerName = request.BuyerName,
                buyerMst = request.BuyerMst,
                buyerAddress = request.BuyerAddress,
                paymentMethod = request.PaymentMethod,
                subtotal = request.Subtotal,
                vatRate = request.VatRate,
                vatAmount = request.VatAmount,
                discountAmount = request.DiscountAmount,
                totalAmount = request.TotalAmount,
                currency = request.Currency ?? "VND",
                amountInWords = request.AmountInWords,
                rawOcrText = request.RawOcrText,
                ocrConfidence = request.OcrConfidence,
                extractedJson,
                tags = request.Tags ?? Array.Empty<string>(),
            },
            transaction: tx,
            cancellationToken: ct));
        if (updated == 0) { await tx.RollbackAsync(ct); return false; }
        // Re-extraction may change the invoice identity a tax verification covers.
        await TaxVerificationRepository.InvalidateIfIdentityChangedAsync(conn, tx, tenantId, id, ct);

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM voucher_line_items li WHERE voucher_id = @id AND EXISTS (SELECT 1 FROM vouchers v WHERE v.id=li.voucher_id AND v.tenant_id=@tenantId AND v.user_id=@userId);",
            new { id, tenantId, userId },
            transaction: tx,
            cancellationToken: ct));

        if (request.LineItems is { Count: > 0 })
        {
            const string insertLine = """
                INSERT INTO voucher_line_items (
                    voucher_id, ordinal, item_code, description, unit,
                    quantity, unit_price, subtotal, vat_rate, vat_amount, total_amount
                ) SELECT
                    @voucherId, @ordinal, @itemCode, @description, @unit,
                    @quantity, @unitPrice, @subtotal, @vatRate, @vatAmount, @totalAmount
                WHERE EXISTS (SELECT 1 FROM vouchers WHERE id=@voucherId AND tenant_id=@tenantId AND user_id=@userId);
                """;

            foreach (var li in request.LineItems)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    insertLine,
                    new
                    {
                        voucherId = id, tenantId, userId,
                        li.Ordinal,
                        itemCode = li.ItemCode,
                        li.Description,
                        li.Unit,
                        li.Quantity,
                        unitPrice = li.UnitPrice,
                        li.Subtotal,
                        vatRate = li.VatRate,
                        vatAmount = li.VatAmount,
                        totalAmount = li.TotalAmount,
                    },
                    transaction: tx,
                    cancellationToken: ct));
            }
        }

        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<VoucherDetail?> FindAsync(
        Guid tenantId,
        Guid userId,
        Guid id,
        CancellationToken ct)
    {
        // Voucher visibility is tenant-scoped: every kế toán viên at the
        // tenant needs to see the same set of chứng từ so admin uploads
        // and kế toán actions share one queue. The user_id column is
        // retained for audit ("who uploaded") but no longer gates reads.
        var voucherSql = $"""
            SELECT {VoucherSelectColumns}
            FROM vouchers
            WHERE id = @id
              AND user_id = @userId
              AND tenant_id = @tenantId;
            """;

        var lineSql = $"""
            SELECT {LineItemSelectColumns}
            FROM voucher_line_items
            WHERE voucher_id = @id
            ORDER BY ordinal;
            """;

        var journalSql = $"""
            SELECT {JournalSelectColumns}
            FROM voucher_journal_suggestions
            WHERE voucher_id = @id
            ORDER BY entry_no;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        var voucher = await conn.QuerySingleOrDefaultAsync<VoucherRecord>(
            new CommandDefinition(voucherSql, new { id, userId, tenantId }, cancellationToken: ct));
        if (voucher is null)
        {
            return null;
        }

        var lines = (await conn.QueryAsync<VoucherLineItemRecord>(
            new CommandDefinition(lineSql, new { id }, cancellationToken: ct))).ToArray();
        var journal = (await conn.QueryAsync<VoucherJournalRecord>(
            new CommandDefinition(journalSql, new { id }, cancellationToken: ct))).ToArray();

        return new VoucherDetail(voucher, lines, journal);
    }

    public async Task<VoucherDetail?> FindInDepartmentAsync(Guid tenantId, Guid departmentId, Guid id, CancellationToken ct)
    {
        var voucherSql = $"SELECT {VoucherSelectColumns} FROM vouchers WHERE id=@id AND tenant_id=@tenantId AND department_id=@departmentId";
        await using var conn = await _factory.OpenAsync(ct);
        var voucher = await conn.QuerySingleOrDefaultAsync<VoucherRecord>(new CommandDefinition(voucherSql, new { id, tenantId, departmentId }, cancellationToken: ct));
        if (voucher is null) return null;
        var lines = (await conn.QueryAsync<VoucherLineItemRecord>(new CommandDefinition($"SELECT {LineItemSelectColumns} FROM voucher_line_items WHERE voucher_id=@id ORDER BY ordinal", new { id }, cancellationToken: ct))).ToArray();
        var journal = (await conn.QueryAsync<VoucherJournalRecord>(new CommandDefinition($"SELECT {JournalSelectColumns} FROM voucher_journal_suggestions WHERE voucher_id=@id ORDER BY entry_no", new { id }, cancellationToken: ct))).ToArray();
        return new VoucherDetail(voucher, lines, journal);
    }

    public async Task<IReadOnlyList<VoucherRecord>> ListInDepartmentAsync(Guid tenantId, Guid departmentId, string? voucherType, string? statusFilter, string? search, int limit, int offset, CancellationToken ct)
    {
        var sql = $"""
            SELECT {VoucherSelectColumns} FROM vouchers
            WHERE tenant_id=@tenantId AND department_id=@departmentId
              AND (@voucherType IS NULL OR voucher_type=@voucherType)
              AND (@statusFilter IS NULL OR status=@statusFilter)
              AND (@search IS NULL OR document_number ILIKE '%'||@search||'%' OR seller_name ILIKE '%'||@search||'%' OR seller_mst ILIKE @search||'%')
            ORDER BY created_at DESC LIMIT @limit OFFSET @offset
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<VoucherRecord>(new CommandDefinition(sql, new { tenantId, departmentId, voucherType, statusFilter, search, limit=Math.Clamp(limit,1,200), offset=Math.Max(0,offset) }, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<VoucherRecord>> ListAsync(
        Guid tenantId,
        Guid userId,
        string? voucherType,
        string? statusFilter,
        string? search,
        string? sellerMst,
        DateOnly? documentDateFrom,
        DateOnly? documentDateTo,
        int limit,
        int offset,
        CancellationToken ct)
    {
        var hasType = !string.IsNullOrWhiteSpace(voucherType);
        var hasStatus = !string.IsNullOrWhiteSpace(statusFilter);
        var hasSearch = !string.IsNullOrWhiteSpace(search);
        var hasSellerMst = !string.IsNullOrWhiteSpace(sellerMst);
        var hasDateFrom = documentDateFrom.HasValue;
        var hasDateTo = documentDateTo.HasValue;

        // Tenant-scoped list (same as FindAsync). userId retained on
        // command for audit logging but not filtered on.
        //
        // A2 filters: sellerMst = exact MST match (dùng để export hồ sơ
        // theo NCC), documentDateFrom/To = khoảng thời gian document_date
        // (inclusive both ends) — dùng cho tra cứu theo yêu cầu bank.
        var sql = $"""
            SELECT {VoucherSelectColumns}
            FROM vouchers
            WHERE user_id = @userId
              AND tenant_id = @tenantId
              {(hasType ? "AND voucher_type = @voucherType" : "")}
              {(hasStatus ? "AND status = @statusFilter" : "")}
              {(hasSearch ? "AND (document_number ILIKE @searchPattern OR seller_name ILIKE @searchPattern OR seller_mst ILIKE @searchPattern)" : "")}
              {(hasSellerMst ? "AND seller_mst = @sellerMst" : "")}
              {(hasDateFrom ? "AND document_date >= @documentDateFrom" : "")}
              {(hasDateTo ? "AND document_date <= @documentDateTo" : "")}
            ORDER BY created_at DESC
            LIMIT @limit OFFSET @offset;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<VoucherRecord>(
            new CommandDefinition(
                sql,
                new
                {
                    tenantId,
                    userId,
                    voucherType,
                    statusFilter,
                    searchPattern = hasSearch ? $"%{search}%" : null,
                    sellerMst,
                    documentDateFrom,
                    documentDateTo,
                    limit = Math.Clamp(limit, 1, 200),
                    offset = Math.Max(0, offset),
                },
                cancellationToken: ct));
        return rows.ToArray();
    }

    public async Task<VoucherRecord?> UpdateAsync(
        Guid tenantId,
        Guid userId,
        Guid id,
        UpdateVoucherRequest request,
        CancellationToken ct)
    {
        var sql = $"""
            UPDATE vouchers SET
                status          = COALESCE(@status, status),
                document_number = COALESCE(@documentNumber, document_number),
                document_series = COALESCE(@documentSeries, document_series),
                document_date   = COALESCE(@documentDate, document_date),
                seller_name     = COALESCE(@sellerName, seller_name),
                seller_mst      = COALESCE(@sellerMst, seller_mst),
                seller_address  = COALESCE(@sellerAddress, seller_address),
                buyer_name      = COALESCE(@buyerName, buyer_name),
                buyer_mst       = COALESCE(@buyerMst, buyer_mst),
                buyer_address   = COALESCE(@buyerAddress, buyer_address),
                payment_method  = COALESCE(@paymentMethod, payment_method),
                subtotal        = COALESCE(@subtotal, subtotal),
                vat_rate        = COALESCE(@vatRate, vat_rate),
                vat_amount      = COALESCE(@vatAmount, vat_amount),
                discount_amount = COALESCE(@discountAmount, discount_amount),
                total_amount    = COALESCE(@totalAmount, total_amount),
                currency        = COALESCE(@currency, currency),
                amount_in_words = COALESCE(@amountInWords, amount_in_words),
                error_message   = COALESCE(@errorMessage, error_message),
                tags            = COALESCE(@tags, tags)
            WHERE id = @id
              AND user_id = @userId
              AND tenant_id = @tenantId
              AND {VoucherPostingGuard.NotPostedSql}
              AND {Aiq.Gateway.Vouchers.Lifecycle.VoucherLifecycleGuard.NotLockedSql}
            RETURNING {VoucherSelectColumns};
            """;

        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var updated = await conn.QuerySingleOrDefaultAsync<VoucherRecord>(
            new CommandDefinition(
                sql,
                new
                {
                    id,
                    tenantId,
                    userId,
                    status = request.Status,
                    documentNumber = request.DocumentNumber,
                    documentSeries = request.DocumentSeries,
                    documentDate = request.DocumentDate,
                    sellerName = request.SellerName,
                    sellerMst = request.SellerMst,
                    sellerAddress = request.SellerAddress,
                    buyerName = request.BuyerName,
                    buyerMst = request.BuyerMst,
                    buyerAddress = request.BuyerAddress,
                    paymentMethod = request.PaymentMethod,
                    subtotal = request.Subtotal,
                    vatRate = request.VatRate,
                    vatAmount = request.VatAmount,
                    discountAmount = request.DiscountAmount,
                    totalAmount = request.TotalAmount,
                    currency = request.Currency,
                    amountInWords = request.AmountInWords,
                    errorMessage = request.ErrorMessage,
                    tags = request.Tags,
                },
                tx,
                cancellationToken: ct));
        if (updated is null) { await tx.RollbackAsync(ct); return null; }
        // An edit of MST / số / ngày / tổng tiền invalidates a tax verification.
        await TaxVerificationRepository.InvalidateIfIdentityChangedAsync(conn, tx, tenantId, id, ct);
        await tx.CommitAsync(ct);
        if (updated.TaxVerificationStatus != TaxVerificationStatuses.Verified) return updated;
        return await conn.QuerySingleOrDefaultAsync<VoucherRecord>(new CommandDefinition(
            $"SELECT {VoucherSelectColumns} FROM vouchers WHERE id=@id AND tenant_id=@tenantId", new { id, tenantId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<VoucherJournalRecord>> SaveJournalSuggestionsAsync(
        Guid tenantId, Guid departmentId, Guid voucherId,
        SaveJournalSuggestionsRequest request,
        CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // Suggestions change only while the voucher is editable (§2.2); the row
        // lock keeps a concurrent submit from slipping in between.
        var locked = await conn.ExecuteScalarAsync<bool?>(new CommandDefinition("""
            SELECT lifecycle_state IN ('pending_approval', 'approved', 'reversed') OR deleted_at IS NOT NULL
              FROM vouchers WHERE id=@voucherId AND tenant_id=@tenantId AND department_id=@departmentId FOR UPDATE
            """, new { voucherId, tenantId, departmentId }, tx, cancellationToken: ct));
        if (locked == true) throw new Aiq.Gateway.Vouchers.Lifecycle.VoucherLockedException(voucherId);

        if (request.ReplaceExisting)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                // Only AI/manual SUGGESTIONS are replaced — authoritative posting /
                // reversal lines are never touched (DB trigger enforces it too).
                "DELETE FROM voucher_journal_suggestions j WHERE voucher_id = @voucherId AND j.source = 'suggestion' AND EXISTS (SELECT 1 FROM vouchers v WHERE v.id=j.voucher_id AND v.tenant_id=@tenantId AND v.department_id=@departmentId);",
                new { voucherId, tenantId, departmentId },
                transaction: tx,
                cancellationToken: ct));
        }

        var inserted = new List<VoucherJournalRecord>();
        var insertSql = $"""
            INSERT INTO voucher_journal_suggestions (
                voucher_id, entry_no, debit_account, credit_account,
                amount, description, confidence
            ) SELECT
                @voucherId, @entryNo, @debitAccount, @creditAccount,
                @amount, @description, @confidence
            WHERE EXISTS (SELECT 1 FROM vouchers WHERE id=@voucherId AND tenant_id=@tenantId AND department_id=@departmentId)
            ON CONFLICT (voucher_id, entry_no) DO UPDATE SET
                debit_account  = EXCLUDED.debit_account,
                credit_account = EXCLUDED.credit_account,
                amount         = EXCLUDED.amount,
                description    = EXCLUDED.description,
                confidence     = EXCLUDED.confidence
            WHERE voucher_journal_suggestions.source = 'suggestion'
            RETURNING {JournalSelectColumns};
            """;

        foreach (var e in request.Entries)
        {
            // A suggestion never takes an authoritative line's number: if the
            // requested entry_no belongs to a posting, append after the last one.
            var entryNo = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM voucher_journal_suggestions WHERE voucher_id=@voucherId AND entry_no=@entryNo AND source <> 'suggestion')",
                new { voucherId, entryNo = e.EntryNo }, tx, cancellationToken: ct))
                ? await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COALESCE(MAX(entry_no),0)+1 FROM voucher_journal_suggestions WHERE voucher_id=@voucherId",
                    new { voucherId }, tx, cancellationToken: ct))
                : e.EntryNo;
            var row = await conn.QuerySingleAsync<VoucherJournalRecord>(
                new CommandDefinition(
                    insertSql,
                    new
                    {
                        voucherId, tenantId, departmentId,
                        entryNo,
                        debitAccount = e.DebitAccount,
                        creditAccount = e.CreditAccount,
                        amount = e.Amount,
                        description = e.Description,
                        confidence = e.Confidence,
                    },
                    transaction: tx,
                    cancellationToken: ct));
            inserted.Add(row);
        }

        await tx.CommitAsync(ct);
        return inserted;
    }

    public async Task<VoucherJournalRecord?> AcceptJournalAsync(
        Guid tenantId, Guid departmentId, Guid voucherId,
        Guid entryId,
        Guid userId,
        AcceptJournalRequest request,
        CancellationToken ct)
    {
        // Reachable only via the "department_admin" route — deciding is an
        // ADMIN operational action over the whole Department, never
        // creator-scoped. userId here is audit-only (accepted_by).
        var sql = request.Accept
            ? $"""
                UPDATE voucher_journal_suggestions SET
                    accepted_by = @userId,
                    accepted_at = NOW(),
                    rejected_at = NULL,
                    reject_reason = NULL
                WHERE id = @entryId AND voucher_id = @voucherId AND source = 'suggestion'
                  AND EXISTS (SELECT 1 FROM vouchers v WHERE v.id=voucher_id AND v.tenant_id=@tenantId AND v.department_id=@departmentId
                              AND v.lifecycle_state NOT IN ('pending_approval', 'approved', 'reversed') AND v.deleted_at IS NULL)
                RETURNING {JournalSelectColumns};
                """
            : $"""
                UPDATE voucher_journal_suggestions SET
                    rejected_at = NOW(),
                    reject_reason = @rejectReason,
                    accepted_by = NULL,
                    accepted_at = NULL
                WHERE id = @entryId AND voucher_id = @voucherId AND source = 'suggestion'
                  AND EXISTS (SELECT 1 FROM vouchers v WHERE v.id=voucher_id AND v.tenant_id=@tenantId AND v.department_id=@departmentId
                              AND v.lifecycle_state NOT IN ('pending_approval', 'approved', 'reversed') AND v.deleted_at IS NULL)
                RETURNING {JournalSelectColumns};
                """;

        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<VoucherJournalRecord>(
            new CommandDefinition(
                sql,
                new
                {
                    tenantId, departmentId, voucherId,
                    entryId,
                    userId,
                    rejectReason = request.RejectReason,
                },
                cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(Guid tenantId, Guid userId, Guid id, CancellationToken ct)
    {
        // Delete is department_admin-only at the endpoint layer, but this
        // method still scopes by (tenant_id, user_id) — the caller
        // (VoucherEndpoints.DeleteAsync) is responsible for resolving the
        // voucher's actual creator via FindInDepartmentAsync first and
        // passing that id through, not the deleting admin's own id.
        // A posted voucher is audit history: reverse it, never delete it (P5).
        const string sql = $"""
            DELETE FROM vouchers
            WHERE id = @id
              AND user_id = @userId
              AND tenant_id = @tenantId
              AND {VoucherPostingGuard.NotPostedSql}
              AND {Aiq.Gateway.Vouchers.Lifecycle.VoucherLifecycleGuard.NotLockedSql}
              AND posted_at IS NULL;
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(
            new CommandDefinition(sql, new { id, tenantId, userId }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<bool> LinkContractAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid contractId, Guid? installmentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var contractInScope = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM contracts WHERE id=@contractId AND tenant_id=@tenantId AND department_id=@departmentId)",
            new { contractId, tenantId, departmentId }, cancellationToken: ct));
        if (!contractInScope) return false;

        if (installmentId is { } instId)
        {
            var installmentInScope = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM contract_payment_schedules WHERE id=@instId AND contract_id=@contractId AND tenant_id=@tenantId AND department_id=@departmentId)",
                new { instId, contractId, tenantId, departmentId }, cancellationToken: ct));
            if (!installmentInScope) return false;
        }

        const string sql = """
            UPDATE vouchers SET contract_id = @contractId, contract_installment_id = @installmentId
            WHERE id = @voucherId AND tenant_id = @tenantId AND department_id = @departmentId;
            """;
        var rows = await conn.ExecuteAsync(
            new CommandDefinition(sql, new { voucherId, contractId, installmentId, tenantId, departmentId }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<bool> UpdateOcrResultAsync(
        Guid id,
        string status,
        string? rawText,
        decimal? ocrConfidence,
        string? errorMessage,
        CancellationToken ct)
    {
        const string sql = $"""
            UPDATE vouchers
            SET status         = @status,
                raw_ocr_text   = COALESCE(@rawText, raw_ocr_text),
                ocr_confidence = COALESCE(@ocrConfidence, ocr_confidence),
                error_message  = @errorMessage,
                updated_at     = NOW()
            WHERE id = @id
              AND {VoucherPostingGuard.NotPostedSql}
              AND {Aiq.Gateway.Vouchers.Lifecycle.VoucherLifecycleGuard.NotLockedSql}
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new { id, status, rawText, ocrConfidence, errorMessage },
            cancellationToken: ct)) == 1;
    }

    public async Task<bool> TryUpdateOcrJobResultAsync(
        Guid id,
        string status,
        string? rawText,
        decimal? ocrConfidence,
        string? errorMessage,
        CancellationToken ct)
    {
        const string sql = """
            UPDATE vouchers
            SET status         = @status,
                raw_ocr_text   = COALESCE(@rawText, raw_ocr_text),
                ocr_confidence = COALESCE(@ocrConfidence, ocr_confidence),
                error_message  = @errorMessage,
                updated_at     = NOW()
            WHERE id = @id
              AND status IN ('processing', 'ocr_unavailable')
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new { id, status, rawText, ocrConfidence, errorMessage },
            cancellationToken: ct)) == 1;
    }

    public async Task<VoucherRecord?> FindDuplicateAsync(
        Guid? tenantId,
        string sellerMst,
        string documentNumber,
        Guid excludeId,
        CancellationToken ct)
    {
        var sql = $"""
            SELECT {VoucherSelectColumns}
            FROM vouchers
            WHERE seller_mst = @sellerMst
              AND document_number = @documentNumber
              AND id <> @excludeId
              AND (@tenantId::uuid IS NULL OR tenant_id = @tenantId)
              AND status NOT IN ('rejected')
            LIMIT 1;
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<VoucherRecord>(
            new CommandDefinition(
                sql,
                new { tenantId, sellerMst, documentNumber, excludeId },
                cancellationToken: ct));
    }

    public async Task<VoucherStatusReport> GetStatusReportAsync(
        Guid tenantId,
        DateOnly overdueThresholdDate,
        CancellationToken ct)
    {
        // Sprint 6' A6: aggregate counts theo bucket nghiệp vụ.
        //   verified          - đã Accept, đã ghi ledger
        //   pending           - có OCR/extract nhưng chưa Accept (extracted, ocr_complete)
        //   overdue           - pending AND document_date < overdueThresholdDate
        //   failed            - OCR không đọc được (ocr_unavailable, processing timeout)
        //   rejected          - explicitly rejected (dup, invalid)
        // Bucket overdue subsume của pending — cần trừ ra để không double-count
        // trên UI.
        const string sql = """
            SELECT
              SUM(CASE WHEN status = 'verified' THEN 1 ELSE 0 END)::int              AS VerifiedCount,
              SUM(CASE WHEN status IN ('extracted', 'ocr_complete') THEN 1 ELSE 0 END)::int AS PendingCount,
              SUM(CASE
                WHEN status IN ('extracted', 'ocr_complete')
                 AND document_date IS NOT NULL
                 AND document_date < @overdueThresholdDate
                THEN 1 ELSE 0 END)::int                                                AS OverdueCount,
              SUM(CASE WHEN status IN ('ocr_unavailable', 'processing') THEN 1 ELSE 0 END)::int AS FailedCount,
              SUM(CASE WHEN status = 'rejected' THEN 1 ELSE 0 END)::int              AS RejectedCount
            FROM vouchers
            WHERE tenant_id = @tenantId;
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleAsync<VoucherStatusReport>(
            new CommandDefinition(
                sql,
                new { tenantId, overdueThresholdDate },
                cancellationToken: ct));
        return row;
    }

    private static string SerializeExtracted(object? fields)
    {
        if (fields is null)
        {
            return "{}";
        }
        if (fields is JsonElement el)
        {
            return el.GetRawText();
        }
        return JsonSerializer.Serialize(fields);
    }
}
