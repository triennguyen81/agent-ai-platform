using System.Text.Json;
using Aiq.Gateway.Data;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Employees;

public sealed record ExpenseClaimEmployeeOption(Guid Id, string EmployeeCode, string FullName, string? Position, decimal OutstandingAdvance);

public sealed record CreateExpenseClaimCommand(
    Guid EmployeeId, string? Purpose, DateOnly? TripStart, DateOnly? TripEnd, string? Reference, string? RequestId);

public sealed record NewExpenseClaimItem(
    string? RequestId, int? ClientIndex, string ObjectKey, string OriginalFilename, string ContentType, long SizeBytes, string Sha256);

public sealed record ExpenseClaimItemRecord(
    Guid Id, Guid ClaimVoucherId, int SequenceNo, string OriginalFilename, string ContentType, long SizeBytes, string Sha256,
    string Status, string[] ReviewReasons, int Attempt, decimal? OcrConfidence, decimal? ExtractionConfidence,
    string? ExtractedCategory, string? ExtractedMerchantName, string? ExtractedMerchantTaxCode, string? ExtractedDocumentNumber,
    DateOnly? ExtractedDocumentDate, decimal? ExtractedGrossAmount, decimal? ExtractedVatAmount, string? ExtractedCurrency,
    string? ExtractedDescription,
    string? ConfirmedCategory, string? ConfirmedMerchantName, string? ConfirmedMerchantTaxCode, string? ConfirmedDocumentNumber,
    DateOnly? ConfirmedDocumentDate, decimal? ConfirmedGrossAmount, decimal? ConfirmedVatAmount, string? ConfirmedCurrency,
    string? ConfirmedDescription, Guid? ConfirmedBy, DateTime? ConfirmedAt,
    string? ExcludedReason, Guid? ExcludedBy, DateTime? ExcludedAt,
    Guid? AgentId, Guid? AgentVersionId, int? AgentVersion, string? ConfigSource, string? Provider, string? Model,
    string? ErrorCode, string? ErrorStage, DateTime? ProcessedAt, Guid CreatedBy, DateTime CreatedAt)
{
    public ExpenseClaimItemValues Values => ConfirmedBy is not null
        ? new(ConfirmedCategory, ConfirmedMerchantName, ConfirmedMerchantTaxCode, ConfirmedDocumentNumber, ConfirmedDocumentDate,
            ConfirmedGrossAmount, ConfirmedVatAmount, ConfirmedCurrency, ConfirmedDescription)
        : new(ExtractedCategory, ExtractedMerchantName, ExtractedMerchantTaxCode, ExtractedDocumentNumber, ExtractedDocumentDate,
            ExtractedGrossAmount, ExtractedVatAmount, ExtractedCurrency, ExtractedDescription);
}

public sealed record ExpenseClaimIntakeRecord(
    Guid ClaimVoucherId, Guid EmployeeId, string EmployeeCode, string EmployeeName, decimal OutstandingAdvance,
    string? Purpose, DateOnly? TripStart, DateOnly? TripEnd, Guid CreatedBy, DateTime CreatedAt, string? DocumentNumber);

/// <summary>Item-derived PREVIEW only. Authoritative numbers are expense_claim_results (the existing engine).</summary>
public sealed record ExpenseClaimItemTotals(
    int ItemCount, int ReadyCount, int UnresolvedCount, int ExcludedCount,
    decimal ReadyTotal, decimal UnresolvedKnownTotal, decimal OutstandingAdvance, decimal? ExpectedPayable);

public sealed record ExpenseClaimIntakeView(
    ExpenseClaimIntakeRecord Intake, IReadOnlyList<ExpenseClaimItemRecord> Items, ExpenseClaimItemTotals Totals,
    ExpenseClaimResult? Result, bool Locked, IReadOnlyList<string> ConfiguredCategories);

public sealed record ConfirmExpenseClaimItemRequest(
    string? Category, string? MerchantName, string? MerchantTaxCode, string? DocumentNumber, DateOnly? DocumentDate,
    decimal? GrossAmount, decimal? VatAmount, string? Currency, string? Description, string? Note);

public sealed class ExpenseClaimItemException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>What the item job needs (one snapshot, read before any provider call).</summary>
public sealed record ExpenseClaimItemProcessingSnapshot(
    ExpenseClaimItemRecord Item, Guid TenantId, Guid DepartmentId, string ObjectKey, string? RawOcrText,
    DateOnly? TripStart, DateOnly? TripEnd);

public sealed record ExpenseClaimItemExtraction(
    ExpenseClaimItemValues Values, decimal? Confidence, string ExtractedJson, string[] ReviewReasons,
    Guid? AgentId, Guid? AgentVersionId, int? AgentVersion, string ConfigSource, string? Provider, string? Model);

public interface IExpenseClaimIntakeRepository
{
    Task<IReadOnlyList<ExpenseClaimEmployeeOption>> ListEmployeesAsync(Guid tenantId, CancellationToken ct);
    /// <summary>Creates the claim shell (claim voucher + intake). Idempotent by (creator, requestId). Null = employee not in tenant.</summary>
    Task<(Guid ClaimId, bool Created)?> CreateClaimAsync(Guid tenantId, Guid departmentId, Guid actorId, CreateExpenseClaimCommand command, CancellationToken ct);
    Task<ExpenseClaimItemRecord?> FindItemByRequestAsync(Guid claimId, string requestId, int clientIndex, CancellationToken ct);
    /// <summary>Adds ONE item for ONE uploaded file. Throws ExpenseClaimItemException("expense_claim_locked") after recognition.</summary>
    Task<(ExpenseClaimItemRecord Item, bool Created)> AddItemAsync(Guid tenantId, Guid departmentId, Guid claimId, Guid actorId, NewExpenseClaimItem item, CancellationToken ct);
    Task<ExpenseClaimIntakeView?> GetAsync(Guid tenantId, Guid departmentId, Guid claimId, CancellationToken ct);
    Task<(string ObjectKey, string FileName, string ContentType)?> GetItemFileAsync(Guid tenantId, Guid departmentId, Guid claimId, Guid itemId, CancellationToken ct);
    Task<ExpenseClaimItemRecord?> ConfirmItemAsync(Guid tenantId, Guid departmentId, Guid claimId, Guid itemId, Guid actorId, ConfirmExpenseClaimItemRequest request, CancellationToken ct);
    Task<ExpenseClaimItemRecord?> ExcludeItemAsync(Guid tenantId, Guid departmentId, Guid claimId, Guid itemId, Guid actorId, string? reason, CancellationToken ct);
    /// <summary>extraction_failed / needs_review → processing, attempt+1 (exactly one concurrent caller wins). Returns the new attempt.</summary>
    Task<int?> BeginRetryAsync(Guid tenantId, Guid departmentId, Guid claimId, Guid itemId, Guid actorId, CancellationToken ct);
    Task<ExpenseClaimItemProcessingSnapshot?> LoadForProcessingAsync(Guid itemId, int attempt, CancellationToken ct);
    Task SaveOcrAsync(Guid itemId, int attempt, string rawText, decimal confidence, CancellationToken ct);
    Task<IReadOnlyList<string>> ConfiguredCategoriesAsync(Guid tenantId, Guid departmentId, CancellationToken ct);
    Task<ExpenseClaimItemDuplicateEvidence> DuplicateEvidenceAsync(Guid tenantId, Guid claimId, Guid itemId, string sha256, ExpenseClaimItemValues values, CancellationToken ct);
    /// <summary>Writes the item result if (attempt, status=processing) still match. False = superseded/stale.</summary>
    Task<bool> CompleteAsync(Guid itemId, int attempt, Guid? actorId, ExpenseClaimItemExtraction result, CancellationToken ct);
    Task<bool> FailAsync(Guid itemId, int attempt, Guid? actorId, string errorCode, string errorStage,
        Guid? agentId, Guid? agentVersionId, int? agentVersion, string? configSource, string? provider, string? model, CancellationToken ct);
    /// <summary>
    /// Re-derives the claim from its items (serialised per claim) and feeds the
    /// EXISTING engine (ExpenseClaimRepository.ProposeAsync) once no item is
    /// still processing. Never touches a recognized claim.
    /// </summary>
    Task RecomputeAsync(Guid tenantId, Guid departmentId, Guid claimId, CancellationToken ct);
}

public static class ExpenseClaimItemGate
{
    public const string Unresolved = "expense_claim_items_unresolved";
    public const string Empty = "expense_claim_items_empty";

    /// <summary>
    /// Null for legacy single-document claims (no intake). For a multi-file
    /// claim: every item must be ready or excluded, and at least one ready.
    /// </summary>
    public static async Task<string?> ErrorAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid claimVoucherId, CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<(bool IsIntake, int Unresolved, int Ready)>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM expense_claim_intakes WHERE claim_voucher_id=@claimVoucherId),
                   (SELECT COUNT(*) FROM expense_claim_items WHERE claim_voucher_id=@claimVoucherId AND status IN ('processing','needs_review','extraction_failed'))::int,
                   (SELECT COUNT(*) FROM expense_claim_items WHERE claim_voucher_id=@claimVoucherId AND status='ready')::int
            """, new { claimVoucherId }, tx, cancellationToken: ct));
        if (!row.IsIntake) return null;
        if (row.Unresolved > 0) return Unresolved;
        return row.Ready == 0 ? Empty : null;
    }

    public static async Task<bool> IsMultiFileAsync(NpgsqlConnection conn, Guid claimVoucherId, CancellationToken ct) =>
        await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM expense_claim_intakes WHERE claim_voucher_id=@claimVoucherId)", new { claimVoucherId }, cancellationToken: ct));
}

public sealed class ExpenseClaimIntakeRepository(
    NpgsqlConnectionFactory factory, IExpenseClaimRepository claims, ILogger<ExpenseClaimIntakeRepository> logger) : IExpenseClaimIntakeRepository
{
    private const string ItemColumns = """
        id AS Id, claim_voucher_id AS ClaimVoucherId, sequence_no AS SequenceNo, original_filename AS OriginalFilename,
        content_type AS ContentType, size_bytes AS SizeBytes, sha256 AS Sha256, status AS Status, review_reasons AS ReviewReasons,
        attempt AS Attempt, ocr_confidence AS OcrConfidence, extraction_confidence AS ExtractionConfidence,
        extracted_category AS ExtractedCategory, extracted_merchant_name AS ExtractedMerchantName,
        extracted_merchant_tax_code AS ExtractedMerchantTaxCode, extracted_document_number AS ExtractedDocumentNumber,
        extracted_document_date AS ExtractedDocumentDate, extracted_gross_amount AS ExtractedGrossAmount,
        extracted_vat_amount AS ExtractedVatAmount, extracted_currency AS ExtractedCurrency, extracted_description AS ExtractedDescription,
        confirmed_category AS ConfirmedCategory, confirmed_merchant_name AS ConfirmedMerchantName,
        confirmed_merchant_tax_code AS ConfirmedMerchantTaxCode, confirmed_document_number AS ConfirmedDocumentNumber,
        confirmed_document_date AS ConfirmedDocumentDate, confirmed_gross_amount AS ConfirmedGrossAmount,
        confirmed_vat_amount AS ConfirmedVatAmount, confirmed_currency AS ConfirmedCurrency, confirmed_description AS ConfirmedDescription,
        confirmed_by AS ConfirmedBy, confirmed_at AS ConfirmedAt, excluded_reason AS ExcludedReason, excluded_by AS ExcludedBy,
        excluded_at AS ExcludedAt, agent_id AS AgentId, agent_version_id AS AgentVersionId, agent_version AS AgentVersion,
        config_source AS ConfigSource, provider AS Provider, model AS Model, error_code AS ErrorCode, error_stage AS ErrorStage,
        processed_at AS ProcessedAt, created_by AS CreatedBy, created_at AS CreatedAt
        """;

    private static DateOnly TodayVn => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));

    public async Task<IReadOnlyList<ExpenseClaimEmployeeOption>> ListEmployeesAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryAsync<ExpenseClaimEmployeeOption>(new CommandDefinition("""
            SELECT id AS Id, employee_code AS EmployeeCode, full_name AS FullName, position AS Position,
                   COALESCE(outstanding_advance,0) AS OutstandingAdvance
            FROM sim_employees WHERE tenant_id=@tenantId ORDER BY employee_code
            """, new { tenantId }, cancellationToken: ct))).AsList();
    }

    public async Task<(Guid ClaimId, bool Created)?> CreateClaimAsync(Guid tenantId, Guid departmentId, Guid actorId,
        CreateExpenseClaimCommand c, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (!string.IsNullOrWhiteSpace(c.RequestId))
        {
            // Serialise same-request submits, then return the claim the first one created.
            await conn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))",
                new { key = $"expense_claim_intake:{tenantId}:{actorId}:{c.RequestId}" }, tx, cancellationToken: ct));
            var existing = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition("""
                SELECT claim_voucher_id FROM expense_claim_intakes
                WHERE tenant_id=@tenantId AND created_by=@actorId AND request_id=@RequestId AND department_id=@departmentId
                """, new { tenantId, actorId, c.RequestId, departmentId }, tx, cancellationToken: ct));
            if (existing is { } id) { await tx.CommitAsync(ct); return (id, false); }
        }
        var employee = await conn.QuerySingleOrDefaultAsync<(Guid Id, string Code, string Name)?>(new CommandDefinition(
            "SELECT id, employee_code, full_name FROM sim_employees WHERE id=@EmployeeId AND tenant_id=@tenantId",
            new { c.EmployeeId, tenantId }, tx, cancellationToken: ct));
        if (employee is not { } e) return null;
        var fields = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["intake"] = "multi_file", ["employee_code"] = e.Code, ["employee_name"] = e.Name, ["purpose"] = Clean(c.Purpose),
            ["trip_from"] = c.TripStart?.ToString("yyyy-MM-dd"), ["trip_to"] = c.TripEnd?.ToString("yyyy-MM-dd"),
            ["currency"] = "VND", ["items"] = Array.Empty<object>(),
        });
        var claimId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, status, document_number, document_date,
                currency, total_amount, extracted_fields, buyer_name)
            VALUES (@actorId, @tenantId, @departmentId, 'expense_claim', 'processing', @number, @today, 'VND', 0, @fields::jsonb, @name)
            RETURNING id
            """, new
        {
            actorId, tenantId, departmentId, today = TodayVn, fields, name = e.Name,
            number = Clean(c.Reference) ?? $"DNHU-{TodayVn:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
        }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO expense_claim_intakes(claim_voucher_id, tenant_id, department_id, employee_id, purpose, trip_start, trip_end, request_id, created_by)
            VALUES (@claimId, @tenantId, @departmentId, @EmployeeId, @purpose, @TripStart, @TripEnd, @RequestId, @actorId)
            """, new { claimId, tenantId, departmentId, c.EmployeeId, purpose = Clean(c.Purpose), c.TripStart, c.TripEnd, RequestId = Clean(c.RequestId), actorId },
            tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return (claimId, true);
    }

    public async Task<ExpenseClaimItemRecord?> FindItemByRequestAsync(Guid claimId, string requestId, int clientIndex, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ExpenseClaimItemRecord>(new CommandDefinition($"""
            SELECT {ItemColumns} FROM expense_claim_items WHERE claim_voucher_id=@claimId AND request_id=@requestId AND client_index=@clientIndex
            """, new { claimId, requestId, clientIndex }, cancellationToken: ct));
    }

    public async Task<(ExpenseClaimItemRecord Item, bool Created)> AddItemAsync(Guid tenantId, Guid departmentId, Guid claimId, Guid actorId,
        NewExpenseClaimItem item, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockClaimAsync(conn, tx, tenantId, departmentId, claimId, ct);
        if (item.RequestId is not null && item.ClientIndex is { } idx)
        {
            var existing = await conn.QuerySingleOrDefaultAsync<ExpenseClaimItemRecord>(new CommandDefinition($"""
                SELECT {ItemColumns} FROM expense_claim_items WHERE claim_voucher_id=@claimId AND request_id=@RequestId AND client_index=@idx
                """, new { claimId, item.RequestId, idx }, tx, cancellationToken: ct));
            if (existing is not null) { await tx.CommitAsync(ct); return (existing, false); }
        }
        var created = await conn.QuerySingleAsync<ExpenseClaimItemRecord>(new CommandDefinition($"""
            INSERT INTO expense_claim_items(tenant_id, department_id, claim_voucher_id, sequence_no, request_id, client_index,
                object_key, original_filename, content_type, size_bytes, sha256, created_by)
            VALUES (@tenantId, @departmentId, @claimId,
                COALESCE((SELECT MAX(sequence_no) FROM expense_claim_items WHERE claim_voucher_id=@claimId), 0) + 1,
                @RequestId, @ClientIndex, @ObjectKey, @OriginalFilename, @ContentType, @SizeBytes, @Sha256, @actorId)
            RETURNING {ItemColumns}
            """, new { tenantId, departmentId, claimId, item.RequestId, item.ClientIndex, item.ObjectKey, item.OriginalFilename,
                       item.ContentType, item.SizeBytes, item.Sha256, actorId }, tx, cancellationToken: ct));
        await EventAsync(conn, tx, tenantId, claimId, created.Id, actorId, "uploaded",
            new { file = item.OriginalFilename, sha256 = item.Sha256, size = item.SizeBytes, sequence = created.SequenceNo }, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE vouchers SET status='processing', updated_at=NOW() WHERE id=@claimId", new { claimId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return (created, true);
    }

    /// <summary>Lock order: claim voucher (FOR UPDATE) — the same row ExpenseRecognition locks first.</summary>
    private static async Task LockClaimAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid departmentId, Guid claimId, CancellationToken ct)
    {
        var state = await conn.QuerySingleOrDefaultAsync<(string Status, string? Recognition, string? Review)?>(new CommandDefinition("""
            SELECT v.status, r.recognition_status, r.review_status
            FROM vouchers v JOIN expense_claim_intakes i ON i.claim_voucher_id = v.id
            LEFT JOIN expense_claim_results r ON r.voucher_id = v.id
            WHERE v.id=@claimId AND v.tenant_id=@tenantId AND v.department_id=@departmentId AND v.voucher_type='expense_claim'
            FOR UPDATE OF v
            """, new { claimId, tenantId, departmentId }, tx, cancellationToken: ct))
            ?? throw new ExpenseClaimItemException("not_found", "Không tìm thấy đề nghị hoàn ứng.");
        if (IsLocked(state.Status, state.Recognition, state.Review))
            throw new ExpenseClaimItemException("expense_claim_locked", "Đề nghị đã được ghi nhận kế toán — không thể thay đổi khoản chi.");
    }

    private static bool IsLocked(string voucherStatus, string? recognition, string? review) =>
        recognition == ExpenseRecognition.Recognized || review == "confirmed" || voucherStatus == "verified";

    public async Task<ExpenseClaimIntakeView?> GetAsync(Guid tenantId, Guid departmentId, Guid claimId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var intake = await conn.QuerySingleOrDefaultAsync<ExpenseClaimIntakeRecord>(new CommandDefinition("""
            SELECT i.claim_voucher_id AS ClaimVoucherId, i.employee_id AS EmployeeId, e.employee_code AS EmployeeCode, e.full_name AS EmployeeName,
                   COALESCE(e.outstanding_advance,0) AS OutstandingAdvance, i.purpose AS Purpose, i.trip_start AS TripStart, i.trip_end AS TripEnd,
                   i.created_by AS CreatedBy, i.created_at AS CreatedAt, v.document_number AS DocumentNumber
            FROM expense_claim_intakes i JOIN vouchers v ON v.id = i.claim_voucher_id JOIN sim_employees e ON e.id = i.employee_id
            WHERE i.claim_voucher_id=@claimId AND i.tenant_id=@tenantId AND i.department_id=@departmentId
            """, new { claimId, tenantId, departmentId }, cancellationToken: ct));
        if (intake is null) return null;
        var items = (await conn.QueryAsync<ExpenseClaimItemRecord>(new CommandDefinition($"""
            SELECT {ItemColumns} FROM expense_claim_items WHERE claim_voucher_id=@claimId ORDER BY sequence_no
            """, new { claimId }, cancellationToken: ct))).AsList();
        var status = await conn.QuerySingleAsync<(string Status, string? Recognition, string? Review)>(new CommandDefinition("""
            SELECT v.status, r.recognition_status, r.review_status FROM vouchers v LEFT JOIN expense_claim_results r ON r.voucher_id=v.id WHERE v.id=@claimId
            """, new { claimId }, cancellationToken: ct));
        var result = await claims.GetByVoucherIdAsync(tenantId, departmentId, claimId, ct);
        var categories = await CategoriesAsync(conn, null, tenantId, departmentId, ct);
        return new(intake, items, Totals(items, intake.OutstandingAdvance), result, IsLocked(status.Status, status.Recognition, status.Review), categories);
    }

    internal static ExpenseClaimItemTotals Totals(IReadOnlyList<ExpenseClaimItemRecord> items, decimal outstandingAdvance)
    {
        var ready = items.Where(i => i.Status == ExpenseClaimItemStatuses.Ready).ToList();
        var unresolved = items.Where(i => ExpenseClaimItemStatuses.Unresolved(i.Status)).ToList();
        var readyTotal = ready.Sum(i => i.Values.GrossAmount ?? 0m);
        // An expected payable is shown only when nothing is pending — never a misleading final number.
        decimal? expected = unresolved.Count == 0 && ready.Count > 0 ? readyTotal - Math.Min(outstandingAdvance, readyTotal) : null;
        return new(items.Count, ready.Count, unresolved.Count, items.Count(i => i.Status == ExpenseClaimItemStatuses.Excluded),
            readyTotal, unresolved.Sum(i => i.Values.GrossAmount ?? 0m), outstandingAdvance, expected);
    }

    public async Task<(string ObjectKey, string FileName, string ContentType)?> GetItemFileAsync(Guid tenantId, Guid departmentId, Guid claimId, Guid itemId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<(string, string, string)?>(new CommandDefinition("""
            SELECT object_key, original_filename, content_type FROM expense_claim_items
            WHERE id=@itemId AND claim_voucher_id=@claimId AND tenant_id=@tenantId AND department_id=@departmentId
            """, new { itemId, claimId, tenantId, departmentId }, cancellationToken: ct));
    }

    public async Task<ExpenseClaimItemRecord?> ConfirmItemAsync(Guid tenantId, Guid departmentId, Guid claimId, Guid itemId, Guid actorId,
        ConfirmExpenseClaimItemRequest r, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockClaimAsync(conn, tx, tenantId, departmentId, claimId, ct);
        var item = await LockItemAsync(conn, tx, claimId, itemId, ct);
        if (item is null) return null;
        if (item.Status is ExpenseClaimItemStatuses.Processing or ExpenseClaimItemStatuses.Excluded)
            throw new ExpenseClaimItemException("expense_claim_item_not_confirmable", "Khoản chi đang xử lý hoặc đã loại — không thể xác nhận.");
        var categories = await CategoriesAsync(conn, tx, tenantId, departmentId, ct);
        var category = ExpenseClaimItemRules.MatchCategory(r.Category, categories)
            ?? throw new ExpenseClaimItemException("expense_category_not_configured", "Khoản mục chưa được cấu hình định mức/tài khoản cho phòng ban.");
        if (r.GrossAmount is not > 0m) throw new ExpenseClaimItemException("amount_required", "Số tiền phải lớn hơn 0.");
        if (r.VatAmount is { } vat && (vat < 0m || vat > r.GrossAmount)) throw new ExpenseClaimItemException("vat_invalid", "Tiền thuế không hợp lệ.");
        if (r.DocumentDate is null) throw new ExpenseClaimItemException("date_required", "Cần ngày chứng từ.");
        var currency = ExpenseClaimItemRules.NormalizeCurrency(r.Currency);
        if (currency != "VND") throw new ExpenseClaimItemException("foreign_currency_not_supported", "Chưa hỗ trợ ngoại tệ (không có nguồn tỷ giá).");
        var updated = await conn.QuerySingleAsync<ExpenseClaimItemRecord>(new CommandDefinition($"""
            UPDATE expense_claim_items SET status='ready', review_reasons=ARRAY[]::TEXT[],
                confirmed_category=@category, confirmed_merchant_name=@MerchantName, confirmed_merchant_tax_code=@MerchantTaxCode,
                confirmed_document_number=@DocumentNumber, confirmed_document_date=@DocumentDate, confirmed_gross_amount=@GrossAmount,
                confirmed_vat_amount=@VatAmount, confirmed_currency=@currency, confirmed_description=@Description,
                confirmed_by=@actorId, confirmed_at=NOW(), updated_at=NOW()
            WHERE id=@itemId
            RETURNING {ItemColumns}
            """, new { itemId, category, MerchantName = Clean(r.MerchantName), MerchantTaxCode = Clean(r.MerchantTaxCode),
                       DocumentNumber = Clean(r.DocumentNumber), r.DocumentDate, r.GrossAmount, r.VatAmount, currency,
                       Description = Clean(r.Description), actorId }, tx, cancellationToken: ct));
        await EventAsync(conn, tx, tenantId, claimId, itemId, actorId, "confirmed", new
        {
            before = item.Values, after = updated.Values, previousStatus = item.Status, resolvedReasons = item.ReviewReasons, note = Clean(r.Note),
        }, ct);
        await tx.CommitAsync(ct);
        return updated;
    }

    public async Task<ExpenseClaimItemRecord?> ExcludeItemAsync(Guid tenantId, Guid departmentId, Guid claimId, Guid itemId, Guid actorId,
        string? reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ExpenseClaimItemException("reason_required", "Cần lý do loại khoản chi.");
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockClaimAsync(conn, tx, tenantId, departmentId, claimId, ct);
        var item = await LockItemAsync(conn, tx, claimId, itemId, ct);
        if (item is null) return null;
        if (item.Status == ExpenseClaimItemStatuses.Excluded) { await tx.CommitAsync(ct); return item; }
        var updated = await conn.QuerySingleAsync<ExpenseClaimItemRecord>(new CommandDefinition($"""
            UPDATE expense_claim_items SET status='excluded', excluded_reason=@reason, excluded_by=@actorId, excluded_at=NOW(), updated_at=NOW()
            WHERE id=@itemId RETURNING {ItemColumns}
            """, new { itemId, reason = reason.Trim(), actorId }, tx, cancellationToken: ct));
        await EventAsync(conn, tx, tenantId, claimId, itemId, actorId, "excluded", new { reason = reason.Trim(), previousStatus = item.Status }, ct);
        await tx.CommitAsync(ct);
        return updated;
    }

    public async Task<int?> BeginRetryAsync(Guid tenantId, Guid departmentId, Guid claimId, Guid itemId, Guid actorId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockClaimAsync(conn, tx, tenantId, departmentId, claimId, ct);
        var item = await LockItemAsync(conn, tx, claimId, itemId, ct);
        if (item is null) return null;
        if (item.Status is not (ExpenseClaimItemStatuses.ExtractionFailed or ExpenseClaimItemStatuses.NeedsReview))
            throw new ExpenseClaimItemException("expense_claim_item_not_retryable", "Chỉ trích xuất lại khoản chi lỗi hoặc cần kiểm tra.");
        var attempt = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            UPDATE expense_claim_items SET status='processing', attempt=attempt+1, error_code=NULL, error_stage=NULL, updated_at=NOW()
            WHERE id=@itemId RETURNING attempt
            """, new { itemId }, tx, cancellationToken: ct));
        await EventAsync(conn, tx, tenantId, claimId, itemId, actorId, "retry_requested", new { attempt, previousStatus = item.Status }, ct);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE vouchers SET status='processing', updated_at=NOW() WHERE id=@claimId", new { claimId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return attempt;
    }

    private static Task<ExpenseClaimItemRecord?> LockItemAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid claimId, Guid itemId, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<ExpenseClaimItemRecord>(new CommandDefinition($"""
            SELECT {ItemColumns} FROM expense_claim_items WHERE id=@itemId AND claim_voucher_id=@claimId FOR UPDATE
            """, new { itemId, claimId }, tx, cancellationToken: ct));

    // ------------------------------------------------------------------ job side

    public async Task<ExpenseClaimItemProcessingSnapshot?> LoadForProcessingAsync(Guid itemId, int attempt, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var item = await conn.QuerySingleOrDefaultAsync<ExpenseClaimItemRecord>(new CommandDefinition($"""
            SELECT {ItemColumns} FROM expense_claim_items WHERE id=@itemId AND attempt=@attempt AND status='processing'
            """, new { itemId, attempt }, cancellationToken: ct));
        if (item is null) return null;
        var ctx = await conn.QuerySingleAsync<(Guid Tenant, Guid Dept, string Key, string? Raw, DateOnly? Start, DateOnly? End)>(new CommandDefinition("""
            SELECT it.tenant_id, it.department_id, it.object_key, it.raw_ocr_text, i.trip_start, i.trip_end
            FROM expense_claim_items it JOIN expense_claim_intakes i ON i.claim_voucher_id = it.claim_voucher_id WHERE it.id=@itemId
            """, new { itemId }, cancellationToken: ct));
        return new(item, ctx.Tenant, ctx.Dept, ctx.Key, ctx.Raw, ctx.Start, ctx.End);
    }

    public async Task SaveOcrAsync(Guid itemId, int attempt, string rawText, decimal confidence, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE expense_claim_items SET raw_ocr_text=@rawText, ocr_confidence=@confidence, updated_at=NOW()
            WHERE id=@itemId AND attempt=@attempt AND status='processing'
            """, new { itemId, attempt, rawText, confidence = Math.Clamp(confidence, 0m, 1m) }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<string>> ConfiguredCategoriesAsync(Guid tenantId, Guid departmentId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await CategoriesAsync(conn, null, tenantId, departmentId, ct);
    }

    /// <summary>Categories the department actually configured (the only ones recognition can map to an account).</summary>
    private static async Task<IReadOnlyList<string>> CategoriesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, Guid departmentId, CancellationToken ct) =>
        (await conn.QueryAsync<string>(new CommandDefinition("""
            SELECT DISTINCT category FROM department_expense_policies
            WHERE tenant_id=@tenantId AND department_id=@departmentId AND active AND category IS NOT NULL
            ORDER BY category
            """, new { tenantId, departmentId }, tx, cancellationToken: ct))).AsList();

    public async Task<ExpenseClaimItemDuplicateEvidence> DuplicateEvidenceAsync(Guid tenantId, Guid claimId, Guid itemId, string sha256,
        ExpenseClaimItemValues v, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        // Same file bytes anywhere in the tenant (other items, not excluded).
        var sameFile = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM expense_claim_items WHERE tenant_id=@tenantId AND sha256=@sha256 AND id<>@itemId AND status<>'excluded')
            """, new { tenantId, sha256, itemId }, cancellationToken: ct));
        // Same seller tax code + document number: another item, or an existing voucher.
        var sameInvoice = !string.IsNullOrWhiteSpace(v.MerchantTaxCode) && !string.IsNullOrWhiteSpace(v.DocumentNumber)
            && await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS (SELECT 1 FROM expense_claim_items WHERE tenant_id=@tenantId AND id<>@itemId AND status<>'excluded'
                                 AND COALESCE(confirmed_merchant_tax_code, extracted_merchant_tax_code)=@mst
                                 AND COALESCE(confirmed_document_number, extracted_document_number)=@no)
                    OR EXISTS (SELECT 1 FROM vouchers WHERE tenant_id=@tenantId AND seller_mst=@mst AND document_number=@no AND id<>@claimId)
                """, new { tenantId, itemId, claimId, mst = v.MerchantTaxCode!.Trim(), no = v.DocumentNumber!.Trim() }, cancellationToken: ct));
        // Same date + amount (+ merchant when known) within this claim.
        var sameAmountDate = v.GrossAmount is > 0m && v.DocumentDate is not null
            && await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS (SELECT 1 FROM expense_claim_items WHERE claim_voucher_id=@claimId AND id<>@itemId AND status<>'excluded'
                                 AND COALESCE(confirmed_gross_amount, extracted_gross_amount)=@gross
                                 AND COALESCE(confirmed_document_date, extracted_document_date)=@date
                                 AND (@merchant::text IS NULL OR lower(COALESCE(confirmed_merchant_name, extracted_merchant_name, ''))=lower(@merchant)))
                """, new { claimId, itemId, gross = v.GrossAmount, date = v.DocumentDate, merchant = Clean(v.MerchantName) }, cancellationToken: ct));
        return new(sameFile, sameInvoice, sameAmountDate);
    }

    public async Task<bool> CompleteAsync(Guid itemId, int attempt, Guid? actorId, ExpenseClaimItemExtraction r, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var claim = await conn.QuerySingleOrDefaultAsync<(Guid ClaimId, Guid TenantId)?>(new CommandDefinition("""
            SELECT claim_voucher_id, tenant_id FROM expense_claim_items WHERE id=@itemId
            """, new { itemId }, tx, cancellationToken: ct));
        if (claim is null) return false;
        // Same lock order as every other claim mutation (claim voucher first).
        await conn.ExecuteAsync(new CommandDefinition("SELECT 1 FROM vouchers WHERE id=@id FOR UPDATE", new { id = claim.Value.ClaimId }, tx, cancellationToken: ct));
        var status = r.ReviewReasons.Length == 0 ? ExpenseClaimItemStatuses.Ready : ExpenseClaimItemStatuses.NeedsReview;
        var v = r.Values;
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE expense_claim_items SET status=@status, review_reasons=@reasons, extracted_data=@json::jsonb,
                extraction_confidence=@conf, extracted_category=@Category, extracted_merchant_name=@MerchantName,
                extracted_merchant_tax_code=@MerchantTaxCode, extracted_document_number=@DocumentNumber,
                extracted_document_date=@DocumentDate, extracted_gross_amount=@GrossAmount, extracted_vat_amount=@VatAmount,
                extracted_currency=@Currency, extracted_description=@Description,
                agent_id=@AgentId, agent_version_id=@AgentVersionId, agent_version=@AgentVersion, config_source=@ConfigSource,
                provider=@Provider, model=@Model, error_code=NULL, error_stage=NULL, processed_at=NOW(), updated_at=NOW()
            WHERE id=@itemId AND attempt=@attempt AND status='processing'
            """, new
        {
            itemId, attempt, status, reasons = r.ReviewReasons, json = r.ExtractedJson, conf = r.Confidence is { } c ? Math.Clamp(c, 0m, 1m) : (decimal?)null,
            v.Category, v.MerchantName, v.MerchantTaxCode, v.DocumentNumber, v.DocumentDate, v.GrossAmount, v.VatAmount, v.Currency, v.Description,
            r.AgentId, r.AgentVersionId, r.AgentVersion, r.ConfigSource, r.Provider, r.Model,
        }, tx, cancellationToken: ct));
        if (n == 0) { await tx.RollbackAsync(ct); return false; }
        await EventAsync(conn, tx, claim.Value.TenantId, claim.Value.ClaimId, itemId, actorId, "extracted", new
        {
            attempt, status, reviewReasons = r.ReviewReasons, values = v, confidence = r.Confidence,
            agentId = r.AgentId, agentVersionId = r.AgentVersionId, agentVersion = r.AgentVersion, configSource = r.ConfigSource,
            provider = r.Provider, model = r.Model,
        }, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> FailAsync(Guid itemId, int attempt, Guid? actorId, string errorCode, string errorStage,
        Guid? agentId, Guid? agentVersionId, int? agentVersion, string? configSource, string? provider, string? model, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var claim = await conn.QuerySingleOrDefaultAsync<(Guid ClaimId, Guid TenantId)?>(new CommandDefinition(
            "SELECT claim_voucher_id, tenant_id FROM expense_claim_items WHERE id=@itemId", new { itemId }, tx, cancellationToken: ct));
        if (claim is null) return false;
        await conn.ExecuteAsync(new CommandDefinition("SELECT 1 FROM vouchers WHERE id=@id FOR UPDATE", new { id = claim.Value.ClaimId }, tx, cancellationToken: ct));
        // A failed retry keeps the previous extracted values (nothing is wiped).
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE expense_claim_items SET status='extraction_failed', error_code=@errorCode, error_stage=@errorStage,
                agent_id=COALESCE(@agentId, agent_id), agent_version_id=COALESCE(@agentVersionId, agent_version_id),
                agent_version=COALESCE(@agentVersion, agent_version), config_source=COALESCE(@configSource, config_source),
                provider=COALESCE(@provider, provider), model=COALESCE(@model, model), processed_at=NOW(), updated_at=NOW()
            WHERE id=@itemId AND attempt=@attempt AND status='processing'
            """, new { itemId, attempt, errorCode, errorStage, agentId, agentVersionId, agentVersion, configSource, provider, model }, tx, cancellationToken: ct));
        if (n == 0) { await tx.RollbackAsync(ct); return false; }
        await EventAsync(conn, tx, claim.Value.TenantId, claim.Value.ClaimId, itemId, actorId, "extraction_failed",
            new { attempt, errorCode, errorStage, agentId, agentVersionId, configSource }, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    // ------------------------------------------------------------------ claim derivation

    public async Task RecomputeAsync(Guid tenantId, Guid departmentId, Guid claimId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        // Serialise derivations of the same claim (concurrent item completions).
        // Session-level lock: held across the voucher update (committed first)
        // AND the engine call, which runs on its own connections.
        var key = $"expense_claim_recompute:{claimId}";
        await conn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_lock(hashtextextended(@key, 0))", new { key }, cancellationToken: ct));
        try
        {
            await RecomputeLockedAsync(conn, tenantId, departmentId, claimId, ct);
        }
        finally
        {
            await conn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_unlock(hashtextextended(@key, 0))", new { key }, cancellationToken: CancellationToken.None));
        }
    }

    private async Task RecomputeLockedAsync(NpgsqlConnection conn, Guid tenantId, Guid departmentId, Guid claimId, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        var intake = await conn.QuerySingleOrDefaultAsync<(string EmployeeCode, string EmployeeName, decimal Outstanding, string? Purpose, DateOnly? Start, DateOnly? End, string Status, string? Recognition, string? Review)?>(new CommandDefinition("""
            SELECT e.employee_code, e.full_name, COALESCE(e.outstanding_advance,0), i.purpose, i.trip_start, i.trip_end, v.status,
                   r.recognition_status, r.review_status
            FROM expense_claim_intakes i JOIN sim_employees e ON e.id=i.employee_id JOIN vouchers v ON v.id=i.claim_voucher_id
            LEFT JOIN expense_claim_results r ON r.voucher_id=v.id
            WHERE i.claim_voucher_id=@claimId AND i.tenant_id=@tenantId AND i.department_id=@departmentId
            """, new { claimId, tenantId, departmentId }, tx, cancellationToken: ct));
        if (intake is not { } c) { await tx.RollbackAsync(ct); return; }
        if (IsLocked(c.Status, c.Recognition, c.Review)) { await tx.RollbackAsync(ct); return; }   // recognized claims never change
        var items = (await conn.QueryAsync<ExpenseClaimItemRecord>(new CommandDefinition($"""
            SELECT {ItemColumns} FROM expense_claim_items WHERE claim_voucher_id=@claimId ORDER BY sequence_no
            """, new { claimId }, tx, cancellationToken: ct))).AsList();
        var ready = items.Where(i => i.Status == ExpenseClaimItemStatuses.Ready).ToList();
        var processing = items.Any(i => i.Status == ExpenseClaimItemStatuses.Processing);
        var engineItems = ready.Select(i => ExpenseClaimItemRules.ToEngineInput(i.SequenceNo, i.Values)).ToList();

        // The claim voucher mirrors the server-derived items in the existing
        // expense_claim shape, so the existing tax-verification fingerprint
        // (TaxVerificationRules.IdentitiesOf) covers exactly these bills and
        // goes stale whenever they change.
        var fields = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["intake"] = "multi_file", ["employee_code"] = c.EmployeeCode, ["employee_name"] = c.EmployeeName, ["purpose"] = c.Purpose,
            ["trip_from"] = c.Start?.ToString("yyyy-MM-dd"), ["trip_to"] = c.End?.ToString("yyyy-MM-dd"), ["currency"] = "VND",
            ["total_claim"] = engineItems.Sum(i => i.Amount),
            ["items"] = engineItems.Select(i => new Dictionary<string, object?>
            {
                ["seq"] = i.Seq, ["category"] = i.Category, ["description"] = i.Description, ["amount"] = i.Amount,
                ["receipt_attached"] = true, ["vendor_mst"] = i.VendorMst, ["invoice_no"] = i.InvoiceNo,
                ["document_date"] = i.DocumentDate?.ToString("yyyy-MM-dd"), ["vat_amount"] = i.VatAmount,
            }).ToList(),
        });
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE vouchers SET extracted_fields=@fields::jsonb, total_amount=@total, status=@status, updated_at=NOW()
            WHERE id=@claimId
            """, new { claimId, fields, total = engineItems.Sum(i => i.Amount), status = processing ? "processing" : "extracted" }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);

        // The EXISTING engine decides approved/capped/duplicates/VAT/advance/
        // payable and the recognition basis. Advance = the employee's
        // authoritative outstanding advance (capped by the engine) — never a
        // browser or document number. Run once nothing is still processing.
        if (!processing)
        {
            await claims.ProposeAsync(tenantId, departmentId, claimId,
                new ExpenseClaimProposalInput(c.EmployeeCode, null, c.Outstanding, "VND", engineItems), ct);
        }
        logger.LogInformation("expense_claim_recomputed claim={ClaimId} items={Items} ready={Ready} proposed={Proposed}",
            claimId, items.Count, ready.Count, !processing);
    }

    private static Task EventAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid claimId, Guid itemId, Guid? actorId,
        string evt, object details, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO expense_claim_item_events(tenant_id, claim_voucher_id, item_id, actor_id, event, details)
            VALUES (@tenantId, @claimId, @itemId, @actorId, @evt, @details::jsonb)
            """, new { tenantId, claimId, itemId, actorId, evt, details = JsonSerializer.Serialize(details) }, tx, cancellationToken: ct));

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
