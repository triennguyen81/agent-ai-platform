using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Contracts;

public interface IContractRepository
{
    Task<ContractWithClauses> CreateAsync(
        Guid tenantId,
        Guid departmentId,
        Guid userId,
        CreateContractRequest request,
        CancellationToken ct);

    Task<ContractWithClauses?> FindAsync(Guid tenantId, Guid departmentId, Guid id, CancellationToken ct);
    Task<ContractAggregate?> FindAggregateAsync(Guid tenantId, Guid departmentId, Guid id, CancellationToken ct);
    Task<ContractProcessResult?> ProcessAsync(Guid tenantId, Guid departmentId, Guid actorId, Guid id, ProcessContractRequest request, CancellationToken ct);
    Task<string?> ReviewAsync(Guid tenantId, Guid departmentId, Guid actorId, Guid id, bool approve, string? comment, CancellationToken ct);
    Task<dynamic?> PrepareHandoffAsync(Guid tenantId, Guid departmentId, Guid actorId, Guid id, CancellationToken ct);
    Task SetHitlActionAsync(Guid tenantId,Guid departmentId,Guid id,Guid? actionId,CancellationToken ct);

    Task<IReadOnlyList<ContractRecord>> ListAsync(
        Guid tenantId,
        Guid departmentId,
        string? statusFilter,
        string? search,
        int limit,
        int offset,
        CancellationToken ct);

    Task<ContractRecord?> UpdateAsync(
        Guid tenantId,
        Guid departmentId,
        Guid id,
        UpdateContractRequest request,
        CancellationToken ct);

    Task<bool> DeleteAsync(Guid tenantId, Guid departmentId, Guid id, CancellationToken ct);
}

public sealed class ContractRepository : IContractRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public ContractRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    private const string ContractSelectColumns = """
            id              AS Id,
            user_id         AS UserId,
            department_id   AS DepartmentId,
            title           AS Title,
            party_a         AS PartyA,
            party_b         AS PartyB,
            signed_date     AS SignedDate,
            effective_date  AS EffectiveDate,
            end_date        AS EndDate,
            total_value     AS TotalValue,
            currency        AS Currency,
            source_pdf_path AS SourcePdfPath,
            raw_text        AS RawText,
            status          AS Status,
            workflow_state  AS WorkflowState,
            document_type   AS DocumentType,
            contract_number AS ContractNumber,
            contract_code   AS ContractCode,
            warranty_months AS WarrantyMonths,
            hitl_action_id  AS HitlActionId,
            subtotal        AS Subtotal,
            vat_rate        AS VatRate,
            vat_amount      AS VatAmount,
            retention_percent AS RetentionPercent,
            penalty_percent AS PenaltyPercent,
            progress_percent AS ProgressPercent,
            direction       AS Direction,
            tags            AS Tags,
            created_at      AS CreatedAt,
            updated_at      AS UpdatedAt
        """;

    private const string ClauseSelectColumns = """
            id              AS Id,
            contract_id     AS ContractId,
            ordinal         AS Ordinal,
            heading         AS Heading,
            body            AS Body,
            has_deadline    AS HasDeadline,
            has_penalty     AS HasPenalty,
            clause_type     AS ClauseType,
            is_open         AS IsOpen,
            trigger_event   AS TriggerEvent,
            created_at      AS CreatedAt
        """;

    public async Task<ContractWithClauses> CreateAsync(
        Guid tenantId, Guid departmentId, Guid userId, CreateContractRequest request, CancellationToken ct)
    {
        var insertContract = $"""
            INSERT INTO contracts (
                tenant_id, department_id, user_id, title, party_a, party_b,
                signed_date, effective_date, end_date,
                total_value, currency, source_pdf_path, raw_text, tags
            ) VALUES (
                @tenantId, @departmentId, @userId, @title, @partyA, @partyB,
                @signedDate, @effectiveDate, @endDate,
                @totalValue, @currency, @sourcePdfPath, @rawText, @tags
            )
            RETURNING {ContractSelectColumns};
            """;

        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var contract = await conn.QuerySingleAsync<ContractRecord>(
            new CommandDefinition(
                insertContract,
                new
                {
                    tenantId,
                    departmentId,
                    userId,
                    title = request.Title,
                    partyA = request.PartyA,
                    partyB = request.PartyB,
                    signedDate = request.SignedDate,
                    effectiveDate = request.EffectiveDate,
                    endDate = request.EndDate,
                    totalValue = request.TotalValue,
                    currency = request.Currency ?? "VND",
                    sourcePdfPath = request.SourcePdfPath,
                    rawText = request.RawText,
                    tags = request.Tags ?? Array.Empty<string>(),
                },
                transaction: tx,
                cancellationToken: ct));

        var clauses = new List<ContractClauseRecord>();
        if (request.Clauses is { Count: > 0 })
        {
            const string insertClause = """
                INSERT INTO contract_clauses (
                    tenant_id, contract_id, ordinal, heading, body, has_deadline, has_penalty, clause_type, is_open, trigger_event
                ) VALUES (
                    @tenantId, @contractId, @ordinal, @heading, @body, @hasDeadline, @hasPenalty, @ClauseType, @IsOpen, @TriggerEvent
                )
                RETURNING
                    id              AS Id,
                    contract_id     AS ContractId,
                    ordinal         AS Ordinal,
                    heading         AS Heading,
                    body            AS Body,
                    has_deadline    AS HasDeadline,
                    has_penalty     AS HasPenalty,
                    clause_type     AS ClauseType,
                    is_open         AS IsOpen,
                    trigger_event   AS TriggerEvent,
                    created_at      AS CreatedAt;
                """;

            foreach (var clause in request.Clauses)
            {
                var inserted = await conn.QuerySingleAsync<ContractClauseRecord>(
                    new CommandDefinition(
                        insertClause,
                        new
                        {
                            tenantId,
                            contractId = contract.Id,
                            clause.Ordinal,
                            clause.Heading,
                            clause.Body,
                            clause.HasDeadline,
                            clause.HasPenalty,
                            clause.ClauseType,
                            clause.IsOpen,
                            clause.TriggerEvent,
                        },
                        transaction: tx,
                        cancellationToken: ct));
                clauses.Add(inserted);
            }
        }

        await tx.CommitAsync(ct);
        return new ContractWithClauses(contract, clauses);
    }

    public async Task<ContractWithClauses?> FindAsync(Guid tenantId, Guid departmentId, Guid id, CancellationToken ct)
    {
        var contractSql = $"""
            SELECT {ContractSelectColumns}
            FROM contracts
            WHERE id = @id AND tenant_id = @tenantId AND department_id = @departmentId;
            """;

        var clausesSql = $"""
            SELECT {ClauseSelectColumns}
            FROM contract_clauses
            WHERE contract_id = @id AND tenant_id = @tenantId
            ORDER BY ordinal;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        var contract = await conn.QuerySingleOrDefaultAsync<ContractRecord>(
            new CommandDefinition(contractSql, new { id, tenantId, departmentId }, cancellationToken: ct));
        if (contract is null)
        {
            return null;
        }

        var clauses = await conn.QueryAsync<ContractClauseRecord>(
            new CommandDefinition(clausesSql, new { id, tenantId }, cancellationToken: ct));
        return new ContractWithClauses(contract, clauses.ToArray());
    }

    public async Task<IReadOnlyList<ContractRecord>> ListAsync(
        Guid tenantId,
        Guid departmentId,
        string? statusFilter,
        string? search,
        int limit,
        int offset,
        CancellationToken ct)
    {
        var hasStatus = !string.IsNullOrWhiteSpace(statusFilter);
        var hasSearch = !string.IsNullOrWhiteSpace(search);

        var sql = $"""
            SELECT {ContractSelectColumns}
            FROM contracts
            WHERE tenant_id = @tenantId AND department_id = @departmentId
              {(hasStatus ? "AND status = @statusFilter" : "")}
              {(hasSearch ? "AND title ILIKE @searchPattern" : "")}
            ORDER BY created_at DESC
            LIMIT @limit OFFSET @offset;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<ContractRecord>(
            new CommandDefinition(
                sql,
                new
                {
                    tenantId,
                    departmentId,
                    statusFilter,
                    searchPattern = hasSearch ? $"%{search}%" : null,
                    limit = Math.Clamp(limit, 1, 200),
                    offset = Math.Max(0, offset),
                },
                cancellationToken: ct));
        return rows.ToArray();
    }

    public async Task<ContractRecord?> UpdateAsync(
        Guid tenantId, Guid departmentId, Guid id, UpdateContractRequest request, CancellationToken ct)
    {
        var sql = $"""
            UPDATE contracts SET
                title          = COALESCE(@title, title),
                party_a        = COALESCE(@partyA, party_a),
                party_b        = COALESCE(@partyB, party_b),
                signed_date    = COALESCE(@signedDate, signed_date),
                effective_date = COALESCE(@effectiveDate, effective_date),
                end_date       = COALESCE(@endDate, end_date),
                total_value    = COALESCE(@totalValue, total_value),
                currency       = COALESCE(@currency, currency),
                status         = COALESCE(@status, status),
                tags           = COALESCE(@tags, tags)
            WHERE id = @id AND tenant_id = @tenantId AND department_id = @departmentId
              AND workflow_state NOT IN ('APPROVED','HANDED_OFF','REJECTED')
            RETURNING {ContractSelectColumns};
            """;

        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ContractRecord>(
            new CommandDefinition(
                sql,
                new
                {
                    id,
                    tenantId,
                    departmentId,
                    title = request.Title,
                    partyA = request.PartyA,
                    partyB = request.PartyB,
                    signedDate = request.SignedDate,
                    effectiveDate = request.EffectiveDate,
                    endDate = request.EndDate,
                    totalValue = request.TotalValue,
                    currency = request.Currency,
                    status = request.Status,
                    tags = request.Tags,
                },
                cancellationToken: ct));
    }

    public async Task<ContractAggregate?> FindAggregateAsync(Guid tenantId, Guid departmentId, Guid id, CancellationToken ct)
    {
        var detail = await FindAsync(tenantId, departmentId, id, ct);
        if (detail is null) return null;
        await using var conn = await _factory.OpenAsync(ct);
        var scope = new { tenantId, departmentId, id };
        // Every alias below is double-quoted: Postgres folds an UNQUOTED
        // alias to lowercase, and these queries return `dynamic` rows (no
        // strongly-typed record), so System.Text.Json serializes whatever
        // key case Postgres actually returned — an unquoted "dueDate" alias
        // silently became the JSON key "duedate", which the Web UI (and
        // every camelCase-expecting client) never matched. Quoting preserves
        // the intended camelCase all the way to the HTTP response.
        var parties = (await conn.QueryAsync("SELECT id,party_role AS \"partyRole\",name,tax_code AS \"taxCode\",address,representative FROM contract_parties WHERE tenant_id=@tenantId AND department_id=@departmentId AND contract_id=@id ORDER BY created_at", scope)).AsList();
        var payments = (await conn.QueryAsync("SELECT id,ordinal,description,percentage,subtotal_amount AS \"subtotalAmount\",vat_amount AS \"vatAmount\",total_amount AS \"totalAmount\",due_date AS \"dueDate\",trigger_event AS \"triggerEvent\",offset_days AS \"offsetDays\",expected_date AS \"expectedDate\",status,penalty_overdue_amount AS \"penaltyOverdueAmount\",penalty_overdue_days AS \"penaltyOverdueDays\",penalty_rate AS \"penaltyRate\",penalty_amount AS \"penaltyAmount\",penalty_calculated_at AS \"penaltyCalculatedAt\" FROM contract_payment_schedules WHERE tenant_id=@tenantId AND department_id=@departmentId AND contract_id=@id ORDER BY ordinal", scope)).AsList();
        var milestones = (await conn.QueryAsync("SELECT id,ordinal,name,due_date AS \"dueDate\",reminder_days AS \"reminderDays\",trigger_event AS \"triggerEvent\",duration_months AS \"durationMonths\",expected_date AS \"expectedDate\",status FROM contract_milestones WHERE tenant_id=@tenantId AND department_id=@departmentId AND contract_id=@id ORDER BY ordinal", scope)).AsList();
        var alerts = (await conn.QueryAsync("SELECT id,code,severity,message,field_name AS \"fieldName\",resolved_at AS \"resolvedAt\",created_at AS \"createdAt\" FROM contract_alerts WHERE tenant_id=@tenantId AND department_id=@departmentId AND contract_id=@id ORDER BY created_at", scope)).AsList();
        var proposals = (await conn.QueryAsync("SELECT id,proposed_action AS \"proposedAction\",rationale,status,created_at AS \"createdAt\" FROM contract_counterparty_proposals WHERE tenant_id=@tenantId AND department_id=@departmentId AND contract_id=@id ORDER BY created_at", scope)).AsList();
        var documents = (await conn.QueryAsync("SELECT id,uploaded_by AS \"uploadedBy\",document_type AS \"documentType\",referenced_code AS \"referencedCode\",original_filename AS \"originalFilename\",content_type AS \"contentType\",size_bytes AS \"sizeBytes\",created_at AS \"createdAt\" FROM contract_documents WHERE tenant_id=@tenantId AND department_id=@departmentId AND contract_id=@id ORDER BY created_at", scope)).AsList();
        var handoffs = (await conn.QueryAsync("SELECT id,payload::text AS payload,status,approved_by AS \"approvedBy\",exported_at AS \"exportedAt\",created_at AS \"createdAt\" FROM contract_handoff_packages WHERE tenant_id=@tenantId AND department_id=@departmentId AND contract_id=@id ORDER BY created_at DESC", scope)).AsList();
        var history = (await conn.QueryAsync("SELECT id,actor_id AS \"actorId\",event_type AS \"eventType\",from_state AS \"fromState\",to_state AS \"toState\",details::text AS details,created_at AS \"createdAt\" FROM contract_history WHERE tenant_id=@tenantId AND department_id=@departmentId AND contract_id=@id ORDER BY created_at DESC", scope)).AsList();
        return new(detail.Contract, detail.Clauses, parties, payments, milestones, alerts, proposals, documents, handoffs, history);
    }

    public async Task<ContractProcessResult?> ProcessAsync(Guid tenantId, Guid departmentId, Guid actorId, Guid id, ProcessContractRequest request, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var current = await conn.QuerySingleOrDefaultAsync<(string State, DateOnly? Signed, DateOnly? Effective, DateOnly? End)>(new CommandDefinition(
            "SELECT workflow_state AS State,signed_date AS Signed,effective_date AS Effective,end_date AS End FROM contracts WHERE id=@id AND tenant_id=@tenantId AND department_id=@departmentId AND workflow_state NOT IN ('APPROVED','HANDED_OFF','REJECTED') FOR UPDATE",
            new { id, tenantId, departmentId }, tx, cancellationToken: ct));
        if (current == default) return null;
        var subtotal = request.Subtotal ?? request.TotalValue ?? 0;
        var vatRate = request.VatRate ?? 0;
        var vat = decimal.Round(subtotal * vatRate / 100m, 2, MidpointRounding.AwayFromZero);
        var total = request.TotalValue ?? subtotal + vat;
        var alerts = new List<(string Code, string Severity, string Message, string? Field)>();
        if (subtotal <= 0) alerts.Add(("AMOUNT_REQUIRED", "error", "Giá trị hợp đồng phải lớn hơn 0.", "subtotal"));
        // CT.VAT: spec threshold is a deviation over 1.000đ, not 1đ.
        if (request.TotalValue is not null && Math.Abs(total - (subtotal + vat)) > 1000m) alerts.Add(("VAT_MISMATCH", "error", "Tổng tiền không khớp tiền trước thuế cộng VAT.", "totalValue"));
        if (current.Effective is not null && current.End is not null && current.End < current.Effective) alerts.Add(("DATE_RANGE_INVALID", "error", "Ngày kết thúc trước ngày hiệu lực.", "endDate"));
        var earliest=current.Effective??current.Signed;
        if(earliest is not null&&(request.PaymentTerms??[]).Any(p=>string.IsNullOrWhiteSpace(p.TriggerEvent)&&p.DueDate is not null&&p.DueDate<earliest))alerts.Add(("PAYMENT_DATE_INVALID","error","Ngày thanh toán cố định trước ngày ký/hiệu lực.","paymentTerms"));
        if(earliest is not null&&(request.Milestones??[]).Any(m=>string.IsNullOrWhiteSpace(m.TriggerEvent)&&m.DueDate is not null&&m.DueDate<earliest))alerts.Add(("MILESTONE_DATE_INVALID","error","Ngày mốc cố định trước ngày ký/hiệu lực.","milestones"));
        var paymentPercent = request.PaymentTerms?.Sum(x => x.Percentage) ?? 0;
        if (request.PaymentTerms is { Count: > 0 } && Math.Abs(paymentPercent - 100m) > 0.0001m) alerts.Add(("PAYMENT_PERCENT_INVALID", "error", "Tổng tỷ lệ thanh toán phải bằng 100%.", "paymentTerms"));
        if ((request.PenaltyPercent ?? 0) > (request.StandardPenaltyThreshold ?? 8)) alerts.Add(("PENALTY_DEVIATION", "warning", "Mức phạt vượt ngưỡng hợp đồng chuẩn.", "penaltyPercent"));
        if ((request.RetentionPercent ?? 0) > (request.StandardRetentionThreshold ?? 10)) alerts.Add(("RETENTION_DEVIATION", "warning", "Tỷ lệ giữ lại vượt ngưỡng hợp đồng chuẩn.", "retentionPercent"));
        if (request.Parties is null || request.Parties.Count < 2) alerts.Add(("PARTIES_INCOMPLETE", "error", "Cần đủ thông tin các bên hợp đồng.", "parties"));
        foreach(var party in request.Parties??[])
            if(!string.IsNullOrWhiteSpace(party.TaxCode)&&!System.Text.RegularExpressions.Regex.IsMatch(party.TaxCode,@"^\d{10}(?:\d{3})?$"))
                alerts.Add(("TAX_CODE_INVALID","error",$"Mã số thuế của {party.Name} phải có 10 hoặc 13 chữ số.","parties"));
        var allowedDocumentTypes=new HashSet<string>(StringComparer.Ordinal){"contract","sale_contract","sales_contract","purchase_contract","rent_contract","loan_contract","service_contract","appendix","acceptance","delivery","other"};
        if(!string.IsNullOrWhiteSpace(request.DocumentType)&&!allowedDocumentTypes.Contains(request.DocumentType))
            alerts.Add(("DOCUMENT_TYPE_INVALID","error","Loại tài liệu không thuộc danh mục Contract Processing.","documentType"));
        var duplicate = request.ContractNumber is not null && await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM contracts WHERE tenant_id=@tenantId AND department_id=@departmentId AND contract_number=@number AND id<>@id)",
            new { tenantId, departmentId, number=request.ContractNumber, id }, tx, cancellationToken: ct));
        if (duplicate) alerts.Add(("DUPLICATE_CONTRACT", "error", $"Số hợp đồng '{request.ContractNumber}' đã tồn tại trong phòng ban — hợp đồng này được lưu không kèm số hợp đồng, cần bổ sung/sửa thủ công.", "contractNumber"));
        // Bug fix: `duplicate` was computed above and turned into an alert,
        // but the final UPDATE below used to write request.ContractNumber
        // unconditionally regardless — reproducing the exact collision this
        // check just detected, so uq_contract_number_department (partial
        // unique index, migration 027, excludes NULL) threw an unhandled
        // 23505 and crashed the whole /process call instead of landing the
        // contract in NEEDS_REVIEW the way every other "error" alert here
        // does. Persist NULL instead of the colliding value — the DUPLICATE_
        // CONTRACT alert above (now written to contract_alerts a few lines
        // down) is the record of what was actually extracted.
        var contractNumberToPersist = duplicate ? null : request.ContractNumber;
        // CT.REF: a clause references an appendix ("phụ lục") but no clause
        // carries that appendix's own content (heading mentions it too) —
        // deterministic proxy using only this contract's own clauses, no
        // separate attachment tracking exists yet.
        var clauseRows = (await conn.QueryAsync<(string? Heading, string Body, bool IsOpen, string? TriggerEvent)>(new CommandDefinition(
            "SELECT heading AS Heading, body AS Body,is_open AS IsOpen,trigger_event AS TriggerEvent FROM contract_clauses WHERE contract_id=@id", new { id }, tx, cancellationToken: ct))).AsList();
        var referencesAppendix = clauseRows.Any(c => c.Body.Contains("phụ lục", StringComparison.OrdinalIgnoreCase));
        var hasAppendixClause = clauseRows.Any(c => c.Heading is not null && c.Heading.Contains("phụ lục", StringComparison.OrdinalIgnoreCase));
        var hasAppendixDocument=await conn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM contract_documents WHERE contract_id=@id AND tenant_id=@tenantId AND department_id=@departmentId AND document_type='appendix')",new{id,tenantId,departmentId},tx,cancellationToken:ct));
        if (referencesAppendix && !hasAppendixClause&&!hasAppendixDocument) alerts.Add(("MISSING_APPENDIX", "warning", "Hợp đồng dẫn chiếu phụ lục nhưng chưa nhận được phụ lục.", "documents"));
        foreach(var clause in clauseRows.Where(c=>c.IsOpen||!string.IsNullOrWhiteSpace(c.TriggerEvent)))
            alerts.Add(("OPEN_OR_CONDITIONAL_CLAUSE","warning",$"Điều khoản '{clause.Heading ?? "không tiêu đề"}' còn mở hoặc phụ thuộc điều kiện.","clauses"));
        var missingRequiredDocument=false;
        foreach(var required in request.ReferencedDocuments?.Where(x=>x.Required)??[])
        {
            var exists=await conn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM contract_documents WHERE contract_id=@id AND tenant_id=@tenantId AND department_id=@departmentId AND document_type=@DocumentType AND (@ReferencedCode IS NULL OR referenced_code=@ReferencedCode))",new{id,tenantId,departmentId,required.DocumentType,required.ReferencedCode},tx,cancellationToken:ct));
            if(!exists){missingRequiredDocument=true;alerts.Add(("MISSING_REFERENCED_DOCUMENT","error",$"Thiếu tài liệu bắt buộc: {required.DocumentType}{(required.ReferencedCode is null?"":$" ({required.ReferencedCode})")}","documents"));}
        }

        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM contract_parties WHERE contract_id=@id; DELETE FROM contract_payment_schedules WHERE contract_id=@id; DELETE FROM contract_milestones WHERE contract_id=@id; DELETE FROM contract_alerts WHERE contract_id=@id AND resolved_at IS NULL; DELETE FROM contract_counterparty_proposals WHERE contract_id=@id AND status='proposed';", new { id }, tx, cancellationToken: ct));
        foreach (var p in request.Parties ?? [])
        {
            var partyId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
                "INSERT INTO contract_parties(contract_id,tenant_id,department_id,party_role,name,tax_code,address,representative) VALUES(@id,@tenantId,@departmentId,@PartyRole,@Name,@TaxCode,@Address,@Representative) RETURNING id",
                new { id, tenantId, departmentId, p.PartyRole, p.Name, p.TaxCode, p.Address, p.Representative }, tx, cancellationToken: ct));
            // CT.NEW: this tax code has never appeared on any other contract
            // this department has on file, AND it isn't already a known
            // Customer/Supplier master record — propose opening a
            // counterparty code, informational only (never blocks the
            // workflow). Checking sim_customers/sim_suppliers too (not just
            // contract history) avoids Contract Processing inventing a
            // second, disconnected "this looks new" signal for a
            // counterparty the Customer/Supplier module already knows
            // about (see docs/CUSTOMER_MODULE_TODO.md CUS-10).
            if (!string.IsNullOrWhiteSpace(p.TaxCode))
            {
                var knownElsewhere = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                    """
                    SELECT EXISTS(SELECT 1 FROM contract_parties WHERE tenant_id=@tenantId AND department_id=@departmentId AND tax_code=@TaxCode AND contract_id<>@id)
                    OR EXISTS(SELECT 1 FROM sim_customers WHERE tenant_id=@tenantId AND department_id=@departmentId AND mst=@TaxCode)
                    OR EXISTS(SELECT 1 FROM sim_suppliers WHERE tenant_id=@tenantId AND department_id=@departmentId AND mst=@TaxCode)
                    """,
                    new { tenantId, departmentId, p.TaxCode, id }, tx, cancellationToken: ct));
                if (!knownElsewhere)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO contract_counterparty_proposals(contract_id,tenant_id,department_id,party_id,proposed_action,rationale) VALUES(@id,@tenantId,@departmentId,@partyId,@Action,@Rationale)",
                        new { id, tenantId, departmentId, partyId, Action = $"Mở mã đối tượng công nợ mới cho {p.Name}", Rationale = $"MST {p.TaxCode} chưa xuất hiện trong hồ sơ hợp đồng nào khác của phòng ban." },
                        tx, cancellationToken: ct));
                    alerts.Add(("NEW_COUNTERPARTY", "info", $"Đối tác {p.Name} (MST {p.TaxCode}) chưa có trong danh mục — đề xuất mở mã đối tượng mới.", "parties"));
                }
            }
        }
        // Conditional dates: a TriggerEvent means the date is not yet a
        // confirmed legal deadline — DueDate is always forced NULL here
        // regardless of what the caller sent, so an estimate can never be
        // silently stored as a confirmed due date. ExpectedDate travels
        // alongside as a non-binding planning estimate only.
        foreach (var p in request.PaymentTerms ?? [])
        {
            var baseAmount=decimal.Round(subtotal*p.Percentage/100m,2);
            var lineVat=decimal.Round(baseAmount*vatRate/100m,2);
            var hasTrigger=!string.IsNullOrWhiteSpace(p.TriggerEvent);
            var effectiveDueDate=hasTrigger?null:p.DueDate;
            await conn.ExecuteAsync(new CommandDefinition("INSERT INTO contract_payment_schedules(contract_id,tenant_id,department_id,ordinal,description,percentage,subtotal_amount,vat_amount,total_amount,due_date,trigger_event,offset_days,expected_date) VALUES(@id,@tenantId,@departmentId,@Ordinal,@Description,@Percentage,@baseAmount,@lineVat,@lineTotal,@effectiveDueDate,@TriggerEvent,@OffsetDays,@ExpectedDate)",new{id,tenantId,departmentId,p.Ordinal,p.Description,p.Percentage,baseAmount,lineVat,lineTotal=baseAmount+lineVat,effectiveDueDate,p.TriggerEvent,p.OffsetDays,p.ExpectedDate},tx,cancellationToken:ct));
            if(hasTrigger) alerts.Add(("PAYMENT_AWAITING_TRIGGER","info",$"Đợt {p.Ordinal} ({p.Description}) đang chờ {p.TriggerEvent}.","paymentTerms"));
        }
        foreach (var m in request.Milestones ?? [])
        {
            var hasTrigger=!string.IsNullOrWhiteSpace(m.TriggerEvent);
            var effectiveDueDate=hasTrigger?null:m.DueDate;
            await conn.ExecuteAsync(new CommandDefinition("INSERT INTO contract_milestones(contract_id,tenant_id,department_id,ordinal,name,due_date,reminder_days,trigger_event,duration_months,expected_date) VALUES(@id,@tenantId,@departmentId,@Ordinal,@Name,@effectiveDueDate,@ReminderDays,@TriggerEvent,@DurationMonths,@ExpectedDate)",new{id,tenantId,departmentId,m.Ordinal,m.Name,effectiveDueDate,m.ReminderDays,m.TriggerEvent,m.DurationMonths,m.ExpectedDate},tx,cancellationToken:ct));
            if(hasTrigger) alerts.Add(("MILESTONE_AWAITING_TRIGGER","info",$"{m.Name} chưa xác định ngày — đang chờ {m.TriggerEvent}.","milestones"));
        }
        foreach (var a in alerts) await conn.ExecuteAsync(new CommandDefinition("INSERT INTO contract_alerts(contract_id,tenant_id,department_id,code,severity,message,field_name) VALUES(@id,@tenantId,@departmentId,@Code,@Severity,@Message,@Field)",new{id,tenantId,departmentId,a.Code,a.Severity,a.Message,a.Field},tx,cancellationToken:ct));
        // Generated checklist rows are replaced as one deterministic set;
        // manually maintained rows remain untouched. Conditional expected
        // dates deliberately remain NULL here and never become due_date.
        await conn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM contract_checklist_items WHERE contract_id=@id AND tenant_id=@tenantId AND source='contract_processing';
            INSERT INTO contract_checklist_items(tenant_id,department_id,contract_id,title,description,due_date,remind_days_before,source)
              SELECT @tenantId,@departmentId,@id,'Thanh toán: '||description,
                     CASE WHEN trigger_event IS NULL THEN percentage||'% giá trị hợp đồng' ELSE percentage||'% — chờ '||trigger_event END,
                     due_date,7,'contract_processing'
              FROM contract_payment_schedules WHERE contract_id=@id;
            INSERT INTO contract_checklist_items(tenant_id,department_id,contract_id,title,description,due_date,remind_days_before,source)
              SELECT @tenantId,@departmentId,@id,'Mốc: '||name,
                     CASE WHEN trigger_event IS NULL THEN NULL ELSE 'Chờ '||trigger_event END,
                     due_date,reminder_days,'contract_processing'
              FROM contract_milestones WHERE contract_id=@id;
            INSERT INTO contract_checklist_items(tenant_id,department_id,contract_id,title,description,due_date,remind_days_before,source)
              SELECT @tenantId,@departmentId,@id,'Xử lý cảnh báo '||code,message,NULL,0,'contract_processing'
              FROM contract_alerts WHERE contract_id=@id AND severity IN ('error','warning') AND resolved_at IS NULL;
            """,new{id,tenantId,departmentId},tx,cancellationToken:ct));
        var state = request.Parties is null || request.Parties.Count < 2 || missingRequiredDocument ? "NEEDS_DOCUMENT" : alerts.Any(a=>a.Severity=="error") ? "NEEDS_REVIEW" : "WAITING_REVIEW";

        // Deterministic Sales/Purchase direction — resolved server-side from
        // contract_parties' own tax codes (this contract's seller/buyer, as
        // just recorded above) against the tenant's registered company_tax_id
        // (migration 045). Never inferred from document text/LLM output —
        // same rule and the same root-cause fix as the invoice-side bug in
        // VoucherEndpoints.ApplyInvoiceAsync/ApplyExpenseAsync.
        var companyTaxId = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT company_tax_id FROM tenants WHERE id=@tenantId", new { tenantId }, tx, cancellationToken: ct));
        var sellerTaxCode = request.Parties?.FirstOrDefault(p => p.PartyRole == "seller")?.TaxCode;
        var buyerTaxCode = request.Parties?.FirstOrDefault(p => p.PartyRole == "buyer")?.TaxCode;
        var directionResolution = Aiq.Gateway.Accounting.InvoiceDirectionResolver.Resolve(sellerTaxCode, buyerTaxCode, companyTaxId);
        var direction = directionResolution.Direction switch
        {
            Aiq.Gateway.Accounting.InvoiceDirection.Sales => "sales",
            Aiq.Gateway.Accounting.InvoiceDirection.Purchase => "purchase",
            _ => "needs_review",
        };

        await conn.ExecuteAsync(new CommandDefinition("UPDATE contracts SET document_type=COALESCE(@DocumentType,document_type),contract_number=@ContractNumber,contract_code=COALESCE(contract_code,'HD-'||upper(substr(replace(id::text,'-',''),1,12))),subtotal=@subtotal,vat_rate=@vatRate,vat_amount=@vat,total_value=@total,retention_percent=@RetentionPercent,penalty_percent=@PenaltyPercent,warranty_months=@WarrantyMonths,workflow_state=@state,direction=@direction WHERE id=@id AND tenant_id=@tenantId AND department_id=@departmentId; INSERT INTO contract_history(contract_id,tenant_id,department_id,actor_id,event_type,from_state,to_state,details) SELECT @id,@tenantId,@departmentId,@actorId,'workflow_transition',s.previous,s.next,jsonb_build_object('deterministic',s.deterministic,'alertCount',@alertCount) FROM (VALUES (@oldState,'CLASSIFIED',false),('CLASSIFIED','EXTRACTED',false),('EXTRACTED','VALIDATED',true),('VALIDATED','SCHEDULE_GENERATED',true),('SCHEDULE_GENERATED',@state,true)) s(previous,next,deterministic);",new{id,tenantId,departmentId,actorId,request.DocumentType,ContractNumber=contractNumberToPersist,subtotal,vatRate,vat,total,request.RetentionPercent,request.PenaltyPercent,request.WarrantyMonths,state,direction,oldState=current.State,alertCount=alerts.Count},tx,cancellationToken:ct));
        await tx.CommitAsync(ct);
        return new(state, alerts.Count, request.PaymentTerms?.Count ?? 0, request.Milestones?.Count ?? 0);
    }

    public async Task<string?> ReviewAsync(Guid tenantId, Guid departmentId, Guid actorId, Guid id, bool approve, string? comment, CancellationToken ct)
    {
        var next = approve ? "APPROVED" : "REJECTED";
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition("WITH old AS (SELECT workflow_state FROM contracts WHERE id=@id AND tenant_id=@tenantId AND department_id=@departmentId AND workflow_state IN ('WAITING_REVIEW','NEEDS_REVIEW') FOR UPDATE), upd AS (UPDATE contracts SET workflow_state=@next FROM old WHERE id=@id RETURNING old.workflow_state) INSERT INTO contract_history(contract_id,tenant_id,department_id,actor_id,event_type,from_state,to_state,details) SELECT @id,@tenantId,@departmentId,@actorId,'human_review',workflow_state,@next,jsonb_build_object('comment',@comment) FROM upd RETURNING @next",new{id,tenantId,departmentId,actorId,next,comment},cancellationToken:ct));
    }

    public async Task SetHitlActionAsync(Guid tenantId,Guid departmentId,Guid id,Guid? actionId,CancellationToken ct)
    {await using var conn=await _factory.OpenAsync(ct);await conn.ExecuteAsync(new CommandDefinition("UPDATE contracts SET hitl_action_id=@actionId WHERE id=@id AND tenant_id=@tenantId AND department_id=@departmentId AND workflow_state IN ('WAITING_REVIEW','NEEDS_REVIEW')",new{tenantId,departmentId,id,actionId},cancellationToken:ct));}

    public async Task<dynamic?> PrepareHandoffAsync(Guid tenantId, Guid departmentId, Guid actorId, Guid id, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync(new CommandDefinition("""
            WITH existing AS (
              SELECT h.id,h.payload::text AS payload,h.status,h.created_at
              FROM contract_handoff_packages h JOIN contracts c ON c.id=h.contract_id
              WHERE c.id=@id AND c.tenant_id=@tenantId AND c.department_id=@departmentId AND c.workflow_state='HANDED_OFF'
              ORDER BY h.created_at DESC LIMIT 1
            ), c AS (
              SELECT * FROM contracts WHERE id=@id AND tenant_id=@tenantId AND department_id=@departmentId AND workflow_state='APPROVED' FOR UPDATE
            ), ins AS (
              INSERT INTO contract_handoff_packages(contract_id,tenant_id,department_id,payload,status,approved_by)
              SELECT c.id,c.tenant_id,c.department_id,jsonb_build_object(
                'contractId',c.id,'contractCode',c.contract_code,'contractNumber',c.contract_number,
                'documentType',c.document_type,'subtotal',c.subtotal,'vatRate',c.vat_rate,'vatAmount',c.vat_amount,
                'totalValue',c.total_value,'currency',c.currency,'warrantyMonths',c.warranty_months,
                'paymentSchedules',COALESCE((SELECT jsonb_agg(jsonb_build_object('ordinal',p.ordinal,'description',p.description,'percentage',p.percentage,'subtotalAmount',p.subtotal_amount,'vatAmount',p.vat_amount,'totalAmount',p.total_amount,'dueDate',p.due_date,'triggerEvent',p.trigger_event,'offsetDays',p.offset_days,'expectedDate',p.expected_date) ORDER BY p.ordinal) FROM contract_payment_schedules p WHERE p.contract_id=c.id),'[]'::jsonb),
                'counterpartyProposals',COALESCE((SELECT jsonb_agg(jsonb_build_object('action',cp.proposed_action,'rationale',cp.rationale,'status',cp.status)) FROM contract_counterparty_proposals cp WHERE cp.contract_id=c.id),'[]'::jsonb),
                'accountingBoundary','EXPORT_ONLY'),'prepared',@actorId FROM c
              RETURNING id,payload::text AS payload,status,created_at
            ), moved AS (
              UPDATE contracts SET workflow_state='HANDED_OFF' FROM c WHERE contracts.id=c.id RETURNING contracts.id
            ), history AS (
              INSERT INTO contract_history(contract_id,tenant_id,department_id,actor_id,event_type,from_state,to_state,details)
              SELECT c.id,c.tenant_id,c.department_id,@actorId,'accounting_handoff','APPROVED','HANDED_OFF',jsonb_build_object('accountingBoundary','EXPORT_ONLY') FROM c
            )
            SELECT id AS "Id",payload AS "Payload",status AS "Status",created_at AS "CreatedAt" FROM ins
            UNION ALL SELECT id,payload,status,created_at FROM existing LIMIT 1
            """,new{id,tenantId,departmentId,actorId},cancellationToken:ct));
    }

    public async Task<bool> DeleteAsync(Guid tenantId, Guid departmentId, Guid id, CancellationToken ct)
    {
        const string sql = "DELETE FROM contracts WHERE id = @id AND tenant_id = @tenantId AND department_id = @departmentId AND workflow_state NOT IN ('APPROVED','HANDED_OFF','REJECTED');";
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(
            new CommandDefinition(sql, new { id, tenantId, departmentId }, cancellationToken: ct));
        return rows > 0;
    }
}
