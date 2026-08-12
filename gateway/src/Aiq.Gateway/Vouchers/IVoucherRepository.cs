using System.Text.Json;
using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Vouchers;

public interface IVoucherRepository
{
    Task<VoucherDetail> CreateAsync(
        Guid? tenantId,
        Guid userId,
        CreateVoucherRequest request,
        CancellationToken ct);

    Task<Guid> CreateStubAsync(
        Guid? tenantId,
        Guid userId,
        string voucherType,
        string sourceFilePath,
        string sourceFileName,
        string sourceMimeType,
        CancellationToken ct);

    Task<VoucherDetail?> FindAsync(
        Guid? tenantId,
        Guid userId,
        Guid id,
        CancellationToken ct);

    Task<IReadOnlyList<VoucherRecord>> ListAsync(
        Guid? tenantId,
        Guid userId,
        string? voucherType,
        string? statusFilter,
        string? search,
        int limit,
        int offset,
        CancellationToken ct);

    Task<VoucherRecord?> UpdateAsync(
        Guid? tenantId,
        Guid userId,
        Guid id,
        UpdateVoucherRequest request,
        CancellationToken ct);

    Task ReplaceExtractedAsync(
        Guid id,
        CreateVoucherRequest request,
        CancellationToken ct);

    Task<IReadOnlyList<VoucherJournalRecord>> SaveJournalSuggestionsAsync(
        Guid voucherId,
        SaveJournalSuggestionsRequest request,
        CancellationToken ct);

    Task<VoucherJournalRecord?> AcceptJournalAsync(
        Guid voucherId,
        Guid entryId,
        Guid userId,
        AcceptJournalRequest request,
        CancellationToken ct);

    Task<bool> DeleteAsync(Guid? tenantId, Guid userId, Guid id, CancellationToken ct);

    // UC-ACC-001 E2: dedup by (seller_mst + document_number) within a tenant.
    Task<VoucherRecord?> FindDuplicateAsync(
        Guid? tenantId,
        string sellerMst,
        string documentNumber,
        Guid excludeId,
        CancellationToken ct);
}

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
            source_file_path  AS SourceFilePath,
            source_file_name  AS SourceFileName,
            source_mime_type  AS SourceMimeType,
            raw_ocr_text      AS RawOcrText,
            ocr_confidence    AS OcrConfidence,
            error_message     AS ErrorMessage,
            extracted_fields::text AS ExtractedFieldsJson,
            tags              AS Tags,
            created_at        AS CreatedAt,
            updated_at        AS UpdatedAt
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
            description     AS Description,
            confidence      AS Confidence,
            accepted_by     AS AcceptedBy,
            accepted_at     AS AcceptedAt,
            rejected_at     AS RejectedAt,
            reject_reason   AS RejectReason,
            created_at      AS CreatedAt
        """;

    public async Task<Guid> CreateStubAsync(
        Guid? tenantId,
        Guid userId,
        string voucherType,
        string sourceFilePath,
        string sourceFileName,
        string sourceMimeType,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO vouchers (
                tenant_id, user_id, voucher_type, status,
                source_file_path, source_file_name, source_mime_type
            ) VALUES (
                @tenantId, @userId, @voucherType, 'processing',
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
                    userId,
                    voucherType,
                    sourceFilePath,
                    sourceFileName,
                    sourceMimeType,
                },
                cancellationToken: ct));
    }

    public async Task<VoucherDetail> CreateAsync(
        Guid? tenantId,
        Guid userId,
        CreateVoucherRequest request,
        CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var extractedJson = SerializeExtracted(request.ExtractedFields);

        var insertSql = $"""
            INSERT INTO vouchers (
                tenant_id, user_id, voucher_type, status,
                document_number, document_series, document_date,
                seller_name, seller_mst, seller_address,
                buyer_name, buyer_mst, buyer_address,
                payment_method, subtotal, vat_rate, vat_amount,
                discount_amount, total_amount, currency, amount_in_words,
                source_file_path, source_file_name, source_mime_type,
                raw_ocr_text, ocr_confidence, extracted_fields, tags,
                status
            ) VALUES (
                @tenantId, @userId, @voucherType, 'extracted',
                @documentNumber, @documentSeries, @documentDate,
                @sellerName, @sellerMst, @sellerAddress,
                @buyerName, @buyerMst, @buyerAddress,
                @paymentMethod, @subtotal, @vatRate, @vatAmount,
                @discountAmount, @totalAmount, @currency, @amountInWords,
                @sourceFilePath, @sourceFileName, @sourceMimeType,
                @rawOcrText, @ocrConfidence, @extractedJson::jsonb, @tags,
                'extracted'
            )
            ON CONFLICT (seller_mst, document_number, document_date)
            DO UPDATE SET
                status           = 'extracted',
                document_series  = COALESCE(EXCLUDED.document_series, vouchers.document_series),
                seller_name      = COALESCE(EXCLUDED.seller_name, vouchers.seller_name),
                seller_address   = COALESCE(EXCLUDED.seller_address, vouchers.seller_address),
                buyer_name       = COALESCE(EXCLUDED.buyer_name, vouchers.buyer_name),
                buyer_mst        = COALESCE(EXCLUDED.buyer_mst, vouchers.buyer_mst),
                buyer_address    = COALESCE(EXCLUDED.buyer_address, vouchers.buyer_address),
                payment_method   = COALESCE(EXCLUDED.payment_method, vouchers.payment_method),
                subtotal         = COALESCE(EXCLUDED.subtotal, vouchers.subtotal),
                vat_rate         = COALESCE(EXCLUDED.vat_rate, vouchers.vat_rate),
                vat_amount       = COALESCE(EXCLUDED.vat_amount, vouchers.vat_amount),
                discount_amount  = COALESCE(EXCLUDED.discount_amount, vouchers.discount_amount),
                total_amount     = COALESCE(EXCLUDED.total_amount, vouchers.total_amount),
                currency         = EXCLUDED.currency,
                amount_in_words  = COALESCE(EXCLUDED.amount_in_words, vouchers.amount_in_words),
                raw_ocr_text     = COALESCE(EXCLUDED.raw_ocr_text, vouchers.raw_ocr_text),
                ocr_confidence   = COALESCE(EXCLUDED.ocr_confidence, vouchers.ocr_confidence),
                extracted_fields = EXCLUDED.extracted_fields,
                tags             = EXCLUDED.tags,
                updated_at       = NOW()
            RETURNING {VoucherSelectColumns};
            """;

        var parameters = new
        {
            tenantId,
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

        var voucher = await conn.QuerySingleAsync<VoucherRecord>(
            new CommandDefinition(insertSql, parameters, transaction: tx, cancellationToken: ct));

        // Replace line items (idempotent: delete then insert)
        const string deleteLines = "DELETE FROM voucher_line_items WHERE voucher_id = @voucherId;";
        await conn.ExecuteAsync(new CommandDefinition(
            deleteLines, new { voucherId = voucher.Id }, transaction: tx, cancellationToken: ct));

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

    public async Task ReplaceExtractedAsync(
        Guid id,
        CreateVoucherRequest request,
        CancellationToken ct)
    {
        // Update existing stub row with extracted fields.
        var extractedJson = SerializeExtracted(request.ExtractedFields);

        const string updateSql = """
            UPDATE vouchers SET
                status           = 'extracted',
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
                ocr_confidence   = @ocrConfidence,
                extracted_fields = @extractedJson::jsonb,
                tags             = @tags
            WHERE id = @id;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            updateSql,
            new
            {
                id,
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

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM voucher_line_items WHERE voucher_id = @id;",
            new { id },
            transaction: tx,
            cancellationToken: ct));

        if (request.LineItems is { Count: > 0 })
        {
            const string insertLine = """
                INSERT INTO voucher_line_items (
                    voucher_id, ordinal, item_code, description, unit,
                    quantity, unit_price, subtotal, vat_rate, vat_amount, total_amount
                ) VALUES (
                    @voucherId, @ordinal, @itemCode, @description, @unit,
                    @quantity, @unitPrice, @subtotal, @vatRate, @vatAmount, @totalAmount
                );
                """;

            foreach (var li in request.LineItems)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    insertLine,
                    new
                    {
                        voucherId = id,
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
    }

    public async Task<VoucherDetail?> FindAsync(
        Guid? tenantId,
        Guid userId,
        Guid id,
        CancellationToken ct)
    {
        var voucherSql = $"""
            SELECT {VoucherSelectColumns}
            FROM vouchers
            WHERE id = @id
              AND user_id = @userId
              AND (@tenantId::uuid IS NULL OR tenant_id = @tenantId);
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

    public async Task<IReadOnlyList<VoucherRecord>> ListAsync(
        Guid? tenantId,
        Guid userId,
        string? voucherType,
        string? statusFilter,
        string? search,
        int limit,
        int offset,
        CancellationToken ct)
    {
        var hasType = !string.IsNullOrWhiteSpace(voucherType);
        var hasStatus = !string.IsNullOrWhiteSpace(statusFilter);
        var hasSearch = !string.IsNullOrWhiteSpace(search);

        var sql = $"""
            SELECT {VoucherSelectColumns}
            FROM vouchers
            WHERE user_id = @userId
              AND (@tenantId::uuid IS NULL OR tenant_id = @tenantId)
              {(hasType ? "AND voucher_type = @voucherType" : "")}
              {(hasStatus ? "AND status = @statusFilter" : "")}
              {(hasSearch ? "AND (document_number ILIKE @searchPattern OR seller_name ILIKE @searchPattern OR seller_mst ILIKE @searchPattern)" : "")}
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
                    limit = Math.Clamp(limit, 1, 200),
                    offset = Math.Max(0, offset),
                },
                cancellationToken: ct));
        return rows.ToArray();
    }

    public async Task<VoucherRecord?> UpdateAsync(
        Guid? tenantId,
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
              AND (@tenantId::uuid IS NULL OR tenant_id = @tenantId)
            RETURNING {VoucherSelectColumns};
            """;

        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<VoucherRecord>(
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
                cancellationToken: ct));
    }

    public async Task<IReadOnlyList<VoucherJournalRecord>> SaveJournalSuggestionsAsync(
        Guid voucherId,
        SaveJournalSuggestionsRequest request,
        CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        if (request.ReplaceExisting)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM voucher_journal_suggestions WHERE voucher_id = @voucherId;",
                new { voucherId },
                transaction: tx,
                cancellationToken: ct));
        }

        var inserted = new List<VoucherJournalRecord>();
        var insertSql = $"""
            INSERT INTO voucher_journal_suggestions (
                voucher_id, entry_no, debit_account, credit_account,
                amount, description, confidence
            ) VALUES (
                @voucherId, @entryNo, @debitAccount, @creditAccount,
                @amount, @description, @confidence
            )
            ON CONFLICT (voucher_id, entry_no) DO UPDATE SET
                debit_account  = EXCLUDED.debit_account,
                credit_account = EXCLUDED.credit_account,
                amount         = EXCLUDED.amount,
                description    = EXCLUDED.description,
                confidence     = EXCLUDED.confidence
            RETURNING {JournalSelectColumns};
            """;

        foreach (var e in request.Entries)
        {
            var row = await conn.QuerySingleAsync<VoucherJournalRecord>(
                new CommandDefinition(
                    insertSql,
                    new
                    {
                        voucherId,
                        entryNo = e.EntryNo,
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
        Guid voucherId,
        Guid entryId,
        Guid userId,
        AcceptJournalRequest request,
        CancellationToken ct)
    {
        var sql = request.Accept
            ? $"""
                UPDATE voucher_journal_suggestions SET
                    accepted_by = @userId,
                    accepted_at = NOW(),
                    rejected_at = NULL,
                    reject_reason = NULL
                WHERE id = @entryId AND voucher_id = @voucherId
                RETURNING {JournalSelectColumns};
                """
            : $"""
                UPDATE voucher_journal_suggestions SET
                    rejected_at = NOW(),
                    reject_reason = @rejectReason,
                    accepted_by = NULL,
                    accepted_at = NULL
                WHERE id = @entryId AND voucher_id = @voucherId
                RETURNING {JournalSelectColumns};
                """;

        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<VoucherJournalRecord>(
            new CommandDefinition(
                sql,
                new
                {
                    voucherId,
                    entryId,
                    userId,
                    rejectReason = request.RejectReason,
                },
                cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(Guid? tenantId, Guid userId, Guid id, CancellationToken ct)
    {
        const string sql = """
            DELETE FROM vouchers
            WHERE id = @id
              AND user_id = @userId
              AND (@tenantId::uuid IS NULL OR tenant_id = @tenantId);
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(
            new CommandDefinition(sql, new { id, tenantId, userId }, cancellationToken: ct));
        return rows > 0;
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
