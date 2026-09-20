using Aiq.Gateway.Contracts;
using Aiq.Gateway.Data;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Contract Processing hardening: focused xUnit coverage for two concrete
/// defects found and fixed while chasing the real Purchase Contract bug —
/// neither had any xUnit-level regression coverage before this file existed
/// (only the real-HTTP E2E script did):
///
/// 1. JSON casing regression — FindAggregateAsync's dynamic (non-record)
///    queries used UNQUOTED SQL aliases ("due_date AS dueDate"). Postgres
///    folds an unquoted identifier to lowercase, so the actual column name
///    Dapper's `dynamic` path saw — and therefore the JSON key
///    System.Text.Json serialized — was "duedate", not "dueDate". Every
///    camelCase-expecting client (the Web UI included) silently read
///    `undefined`. Fixed by double-quoting every alias; this test proves it
///    stays fixed by serializing the real repository output exactly as the
///    HTTP response would and asserting on the actual JSON keys.
///
/// 2. Conditional-date invariant — ProcessAsync must never store an
///    estimated date as a confirmed due_date. This is enforced by forcing
///    due_date=NULL server-side whenever TriggerEvent is present, even if
///    the caller also sent a DueDate (either by mistake, or because an
///    upstream agent-side repair didn't run) — Gateway is the authority of
///    last resort, not just the agent's own normalization.
/// </summary>
public sealed class ContractProcessingIntegrationTests : IAsyncLifetime
{
    // Only Program.cs registers these at real app startup — xUnit never
    // runs it, so DateOnly? parameters (SignedDate, DueDate, ExpectedDate...)
    // would fail to bind at all without registering them here too.
    static ContractProcessingIntegrationTests()
    {
        Dapper.SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        Dapper.SqlMapper.AddTypeHandler(new NullableDateOnlyTypeHandler());
        Dapper.SqlMapper.AddTypeHandler(new StringArrayTypeHandler());
    }

    private readonly string? _baseConnectionString = ResolveTestConnectionString();
    private readonly string _schema = $"contract_processing_test_{Guid.NewGuid():N}";
    private string? _scopedConnectionString;

    private Guid _tenantId, _departmentId, _userId;

    private static string? ResolveTestConnectionString()
    {
        var explicitConnection = Environment.GetEnvironmentVariable("AIQ_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(explicitConnection)) return explicitConnection;
        var password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD");
        if (string.IsNullOrWhiteSpace(password)) return null;
        return new NpgsqlConnectionStringBuilder
        {
            Host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "postgres",
            Port = 5432,
            Database = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "aiq",
            Username = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "aiq",
            Password = password,
        }.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_baseConnectionString)) return;

        await using var conn = new NpgsqlConnection(_baseConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"CREATE SCHEMA {_schema}");
        _scopedConnectionString = new NpgsqlConnectionStringBuilder(_baseConnectionString)
        {
            SearchPath = _schema,
        }.ConnectionString;

        await using var scoped = new NpgsqlConnection(_scopedConnectionString);
        await scoped.OpenAsync();
        // Final shape only (07_contracts.sql + migrations 027/028 merged) —
        // matches the established pattern of other fixtures in this
        // project: hand-roll just the columns the repository under test
        // actually reads/writes, not the full ALTER history.
        await scoped.ExecuteAsync("""
            CREATE EXTENSION IF NOT EXISTS pgcrypto;
            CREATE TABLE tenants (id UUID PRIMARY KEY, status TEXT NOT NULL DEFAULT 'active', company_tax_id TEXT);
            CREATE TABLE departments (id UUID PRIMARY KEY, tenant_id UUID NOT NULL, status TEXT NOT NULL DEFAULT 'active');
            CREATE TABLE users (id UUID PRIMARY KEY, tenant_id UUID NOT NULL, department_id UUID);

            CREATE TABLE contracts (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                tenant_id UUID, user_id UUID NOT NULL, department_id UUID, title TEXT NOT NULL,
                party_a TEXT, party_b TEXT, signed_date DATE, effective_date DATE, end_date DATE,
                total_value NUMERIC(18,2), currency TEXT NOT NULL DEFAULT 'VND',
                source_pdf_path TEXT, raw_text TEXT, status TEXT NOT NULL DEFAULT 'active',
                tags TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[],
                document_type TEXT NOT NULL DEFAULT 'contract', workflow_state TEXT NOT NULL DEFAULT 'RECEIVED',
                contract_number TEXT, subtotal NUMERIC(18,2), vat_rate NUMERIC(7,4), vat_amount NUMERIC(18,2),
                retention_percent NUMERIC(7,4), penalty_percent NUMERIC(7,4), progress_percent NUMERIC(5,2) NOT NULL DEFAULT 0,
                direction TEXT,
                contract_code TEXT, warranty_months INT, hitl_action_id UUID,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE TABLE contract_clauses (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), contract_id UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
                tenant_id UUID, ordinal INT NOT NULL, heading TEXT, body TEXT NOT NULL,
                has_deadline BOOLEAN NOT NULL DEFAULT FALSE, has_penalty BOOLEAN NOT NULL DEFAULT FALSE,
                clause_type TEXT, is_open BOOLEAN NOT NULL DEFAULT FALSE, trigger_event TEXT,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE TABLE contract_parties (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), contract_id UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
                tenant_id UUID NOT NULL, department_id UUID NOT NULL,
                party_role TEXT NOT NULL, name TEXT NOT NULL, tax_code TEXT, address TEXT, representative TEXT,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE TABLE contract_payment_schedules (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), contract_id UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
                tenant_id UUID NOT NULL, department_id UUID NOT NULL,
                ordinal INT NOT NULL, description TEXT NOT NULL, percentage NUMERIC(7,4) NOT NULL,
                subtotal_amount NUMERIC(18,2) NOT NULL, vat_amount NUMERIC(18,2) NOT NULL, total_amount NUMERIC(18,2) NOT NULL,
                due_date DATE, status TEXT NOT NULL DEFAULT 'pending', created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                trigger_event TEXT, offset_days INT, expected_date DATE,
                penalty_overdue_amount NUMERIC(18,2), penalty_overdue_days INT,
                penalty_rate NUMERIC(9,4), penalty_amount NUMERIC(18,2), penalty_calculated_at TIMESTAMPTZ
            );
            CREATE TABLE contract_milestones (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), contract_id UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
                tenant_id UUID NOT NULL, department_id UUID NOT NULL,
                ordinal INT NOT NULL, name TEXT NOT NULL, due_date DATE, reminder_days INT NOT NULL DEFAULT 7,
                status TEXT NOT NULL DEFAULT 'pending', created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                trigger_event TEXT, duration_months INT, expected_date DATE
            );
            CREATE TABLE contract_alerts (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), contract_id UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
                tenant_id UUID NOT NULL, department_id UUID NOT NULL,
                code TEXT NOT NULL, severity TEXT NOT NULL, message TEXT NOT NULL, field_name TEXT, resolved_at TIMESTAMPTZ,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE TABLE contract_counterparty_proposals (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), contract_id UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
                tenant_id UUID NOT NULL, department_id UUID NOT NULL,
                party_id UUID, proposed_action TEXT NOT NULL, rationale TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'proposed', created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE TABLE contract_handoff_packages (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), contract_id UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
                tenant_id UUID NOT NULL, department_id UUID NOT NULL,
                payload JSONB NOT NULL, status TEXT NOT NULL DEFAULT 'prepared', approved_by UUID,
                exported_at TIMESTAMPTZ, created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE TABLE contract_history (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), contract_id UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
                tenant_id UUID NOT NULL, department_id UUID NOT NULL,
                actor_id UUID, event_type TEXT NOT NULL, from_state TEXT, to_state TEXT, details JSONB NOT NULL DEFAULT '{}'::jsonb,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE TABLE contract_documents (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),contract_id UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
                tenant_id UUID NOT NULL,department_id UUID NOT NULL,uploaded_by UUID NOT NULL,document_type TEXT NOT NULL,
                referenced_code TEXT,original_filename TEXT NOT NULL,content_type TEXT NOT NULL,storage_key TEXT NOT NULL,
                sha256 TEXT NOT NULL,size_bytes BIGINT NOT NULL,created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),UNIQUE(contract_id,sha256)
            );
            CREATE TABLE contract_checklist_items (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),tenant_id UUID NOT NULL,department_id UUID,contract_id UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
                clause_id UUID,title TEXT NOT NULL,description TEXT,due_date DATE,remind_days_before INT NOT NULL DEFAULT 7,
                assignee_email TEXT,status TEXT NOT NULL DEFAULT 'open',completed_at TIMESTAMPTZ,completed_by UUID,
                source TEXT NOT NULL DEFAULT 'manual',created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            -- Minimal Customer/Supplier master shape — ProcessAsync's CT.NEW
            -- check looks these up by (tenant_id,department_id,mst) to avoid
            -- proposing "new counterparty" for an already-known customer.
            CREATE TABLE sim_customers (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id UUID NOT NULL, department_id UUID NOT NULL, mst TEXT
            );
            CREATE TABLE sim_suppliers (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id UUID NOT NULL, department_id UUID NOT NULL, mst TEXT
            );
            """);

        _tenantId = Guid.NewGuid();
        _departmentId = Guid.NewGuid();
        _userId = Guid.NewGuid();
        await scoped.ExecuteAsync("""
            INSERT INTO tenants(id) VALUES (@tenantId);
            INSERT INTO departments(id,tenant_id) VALUES (@departmentId,@tenantId);
            INSERT INTO users(id,tenant_id,department_id) VALUES (@userId,@tenantId,@departmentId);
            """, new { tenantId = _tenantId, departmentId = _departmentId, userId = _userId });
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(_baseConnectionString)) return;
        await using var conn = new NpgsqlConnection(_baseConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP SCHEMA IF EXISTS {_schema} CASCADE");
    }

    private IContractRepository Repo() => new ContractRepository(new NpgsqlConnectionFactory(_scopedConnectionString!));

    private async Task<Guid> CreateContractAsync(IContractRepository repo)
    {
        var created = await repo.CreateAsync(_tenantId, _departmentId, _userId,
            new CreateContractRequest("Test Contract", "A", "B", null, null, null, null, "VND", null, null, null, null), default);
        return created.Contract.Id;
    }

    [Fact]
    public async Task CreateAsync_PersistsClauseHeadingAndBody_NotEmpty()
    {
        // Real production regression: contract 370f0098-a77a-410a-9318-
        // ddb850bb86f2 had 8 clauses saved with heading=NULL, body='' for
        // every single one, despite raw_text holding the full original
        // wording. The repository layer itself was never the bug (it
        // faithfully persists whatever CreateClauseDto.Body/Heading it is
        // given) — this test proves that end of the pipeline stays correct
        // now that the upstream mapping (contract_tools.py) is fixed to
        // always supply real text.
        if (_scopedConnectionString is null) return;
        var repo = Repo();
        var rawText = "ĐIỀU 1. PHẠM VI DỊCH VỤ\nBên B cung cấp cho Bên A dịch vụ tư vấn, cấu hình và triển khai hệ thống.";
        var created = await repo.CreateAsync(_tenantId, _departmentId, _userId,
            new CreateContractRequest(
                "HỢP ĐỒNG CUNG CẤP DỊCH VỤ", "A", "B", null, null, null, null, "VND", null, rawText, null,
                Clauses: [new CreateClauseDto(1, "Điều 1 — PHẠM VI DỊCH VỤ",
                    "Bên B cung cấp cho Bên A dịch vụ tư vấn, cấu hình và triển khai hệ thống.", false, false)]),
            default);

        Assert.Single(created.Clauses);
        Assert.Equal("Điều 1 — PHẠM VI DỊCH VỤ", created.Clauses[0].Heading);
        Assert.StartsWith("Bên B cung cấp cho Bên A dịch vụ tư vấn, cấu hình và triển khai", created.Clauses[0].Body);
        Assert.NotEqual(0, created.Clauses[0].Body.Length);

        // Read it back exactly as the HTTP GET /contracts/{id} handler does.
        var aggregate = await repo.FindAggregateAsync(_tenantId, _departmentId, created.Contract.Id, default);
        Assert.NotNull(aggregate);
        Assert.Single(aggregate!.Clauses);
        Assert.Equal("Điều 1 — PHẠM VI DỊCH VỤ", aggregate.Clauses[0].Heading);
        Assert.StartsWith("Bên B cung cấp cho Bên A dịch vụ tư vấn", aggregate.Clauses[0].Body);

        // Same JSON-serialization check FindAggregateAsync_SerializesCamelCaseKeys
        // already runs for payments/milestones/parties — clauses is a
        // strongly-typed record list (not `dynamic`), so System.Text.Json
        // already emits real camelCase property names by convention; assert
        // the keys survive serialization (content correctness is already
        // verified above via the deserialized aggregate — default
        // JsonSerializer options escape non-ASCII text as \uXXXX, so
        // asserting the raw Vietnamese substring here would be a false
        // negative on the encoder, not a real bug).
        var json = System.Text.Json.JsonSerializer.Serialize(aggregate, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        });
        Assert.Contains("\"body\":", json);
        Assert.Contains("\"heading\":", json);
        Assert.DoesNotContain("\"body\":\"\"", json);
    }

    [Fact]
    public async Task ProcessAsync_NeverModifiesClauses_ReprocessingDoesNotDuplicateOrClearThem()
    {
        // Clauses are written once at CreateAsync time and read-only from
        // ProcessAsync's own perspective (only used for the MISSING_APPENDIX
        // heuristic) — reprocessing must never duplicate, clear, or alter
        // them, no matter how many times /process runs.
        if (_scopedConnectionString is null) return;
        var repo = Repo();
        var created = await repo.CreateAsync(_tenantId, _departmentId, _userId,
            new CreateContractRequest("Test Contract", "A", "B", null, null, null, null, "VND", null, null, null,
                Clauses:
                [
                    new CreateClauseDto(1, "Điều 1", "Nội dung điều một.", false, false),
                    new CreateClauseDto(2, "Điều 2", "Nội dung điều hai.", false, false),
                ]),
            default);

        var request = new ProcessContractRequest(null, null, 100m, 10m, 110m, null, null,
            Parties: [new("buyer", "A", "0101234567", null, null), new("seller", "B", "0201234567", null, null)],
            PaymentTerms: [new(1, "Một lần", 100m, new DateOnly(2026, 1, 1))], Milestones: null);

        await repo.ProcessAsync(_tenantId, _departmentId, _userId, created.Contract.Id, request, default);
        await repo.ProcessAsync(_tenantId, _departmentId, _userId, created.Contract.Id, request, default);
        await repo.ProcessAsync(_tenantId, _departmentId, _userId, created.Contract.Id, request, default);

        var aggregate = await repo.FindAggregateAsync(_tenantId, _departmentId, created.Contract.Id, default);
        Assert.Equal(2, aggregate!.Clauses.Count);
        Assert.Equal("Nội dung điều một.", aggregate.Clauses[0].Body);
        Assert.Equal("Nội dung điều hai.", aggregate.Clauses[1].Body);
    }

    [Fact]
    public async Task ProcessAsync_TriggerEventPresent_ForcesDueDateNull_EvenIfCallerAlsoSentOne()
    {
        if (_scopedConnectionString is null) return;
        var contractId = await CreateContractAsync(Repo());

        // Invalid combination: caller sends BOTH a confirmed due date AND a
        // trigger_event. Gateway must never let the confirmed date win —
        // trigger_event always forces due_date to NULL, regardless of what
        // arrives on the wire (defense in depth beyond any agent-side repair).
        var request = new ProcessContractRequest(
            null, null, 100m, 10m, 110m, null, null,
            Parties: [new ContractPartyDto("buyer", "A", "0101234567", null, null), new ContractPartyDto("seller", "B", "0201234567", null, null)],
            PaymentTerms: [new PaymentTermDto(1, "Đợt 1", 100m, new DateOnly(2026, 1, 1), TriggerEvent: "Ký biên bản giao nhận", OffsetDays: 10, ExpectedDate: new DateOnly(2026, 2, 1))],
            Milestones: [new MilestoneDto(1, "Bảo hành", new DateOnly(2027, 1, 1), 7, TriggerEvent: "Ký biên bản giao nhận", DurationMonths: 24, ExpectedDate: new DateOnly(2028, 1, 1))]);

        var result = await Repo().ProcessAsync(_tenantId, _departmentId, _userId, contractId, request, default);
        Assert.NotNull(result);

        var aggregate = await Repo().FindAggregateAsync(_tenantId, _departmentId, contractId, default);
        Assert.NotNull(aggregate);
        dynamic payment = aggregate!.Payments[0];
        Assert.Null(payment.dueDate);
        Assert.Equal("Ký biên bản giao nhận", payment.triggerEvent);
        Assert.Equal(new DateTime(2026, 2, 1), payment.expectedDate);

        dynamic milestone = aggregate.Milestones[0];
        Assert.Null(milestone.dueDate);
        Assert.Equal(24, milestone.durationMonths);
    }

    [Fact]
    public async Task FindAggregateAsync_SerializesCamelCaseKeys_NotLowercasedByPostgres()
    {
        if (_scopedConnectionString is null) return;
        var contractId = await CreateContractAsync(Repo());
        var request = new ProcessContractRequest(
            null, null, 100m, 10m, 110m, null, null,
            Parties: [new ContractPartyDto("buyer", "A", "0101", null, null), new ContractPartyDto("seller", "B", "0202", null, null)],
            PaymentTerms: [new PaymentTermDto(1, "Đợt 1", 100m, new DateOnly(2026, 1, 1))],
            Milestones: null);
        await Repo().ProcessAsync(_tenantId, _departmentId, _userId, contractId, request, default);

        var aggregate = await Repo().FindAggregateAsync(_tenantId, _departmentId, contractId, default);
        Assert.NotNull(aggregate);

        // Serialize exactly as the HTTP endpoint does (Results.Ok(aggregate))
        // and assert on the real JSON text — this is what a client actually
        // receives, unlike inspecting the C# dynamic object directly.
        var json = System.Text.Json.JsonSerializer.Serialize(aggregate, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        });
        Assert.Contains("\"dueDate\"", json);
        Assert.Contains("\"subtotalAmount\"", json);
        Assert.Contains("\"totalAmount\"", json);
        Assert.Contains("\"partyRole\"", json);
        Assert.Contains("\"taxCode\"", json);
        Assert.DoesNotContain("\"duedate\"", json);
        Assert.DoesNotContain("\"subtotalamount\"", json);
        Assert.DoesNotContain("\"partyrole\"", json);
    }

    [Fact]
    public async Task ProcessAsync_IsIdempotent_RetryDoesNotDuplicateRows()
    {
        if (_scopedConnectionString is null) return;
        var contractId = await CreateContractAsync(Repo());
        var request = new ProcessContractRequest(
            null, null, 100m, 10m, 110m, null, null,
            Parties: [new ContractPartyDto("buyer", "A", "0101234567", null, null), new ContractPartyDto("seller", "B", "0201234567", null, null)],
            PaymentTerms: [new PaymentTermDto(1, "Đợt 1", 100m, new DateOnly(2026, 1, 1))],
            Milestones: [new MilestoneDto(1, "Bàn giao", new DateOnly(2026, 6, 1))]);

        var first = await Repo().ProcessAsync(_tenantId, _departmentId, _userId, contractId, request, default);
        var second = await Repo().ProcessAsync(_tenantId, _departmentId, _userId, contractId, request, default);
        Assert.Equal(first!.WorkflowState, second!.WorkflowState);
        Assert.Equal(first.PaymentCount, second.PaymentCount);
        Assert.Equal(first.MilestoneCount, second.MilestoneCount);

        var aggregate = await Repo().FindAggregateAsync(_tenantId, _departmentId, contractId, default);
        Assert.Single(aggregate!.Payments);
        Assert.Single(aggregate.Milestones);
        await using var conn=new NpgsqlConnection(_scopedConnectionString);
        Assert.Equal(2,await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM contract_checklist_items WHERE contract_id=@contractId AND source='contract_processing'",new{contractId}));
    }

    [Fact]
    public async Task ProcessAsync_ThreeInstallmentSplit_GeneratesExactDeterministicAmounts()
    {
        // Real production regression: contract b314adbe-d9fb-4789-a70d-
        // 635dbc736194 — pre-VAT 300,000,000, VAT 10% (30,000,000), total
        // 330,000,000, split 30/40/30. Amounts must be computed
        // deterministically in Gateway code, never trusted from the LLM.
        if (_scopedConnectionString is null) return;
        var contractId = await CreateContractAsync(Repo());
        var request = new ProcessContractRequest(
            null, null, 300_000_000m, 10m, 330_000_000m, null, null,
            Parties: [new ContractPartyDto("buyer", "A", "0101234567", null, null), new ContractPartyDto("seller", "B", "0201234567", null, null)],
            PaymentTerms:
            [
                new PaymentTermDto(1, "Đợt 1 — tạm ứng", 30m, new DateOnly(2026, 9, 20)),
                new PaymentTermDto(2, "Đợt 2 — giao hàng", 40m, new DateOnly(2026, 11, 1)),
                new PaymentTermDto(3, "Đợt 3 — nghiệm thu", 30m, new DateOnly(2026, 12, 31)),
            ],
            Milestones: null);

        var result = await Repo().ProcessAsync(_tenantId, _departmentId, _userId, contractId, request, default);
        Assert.NotNull(result);
        Assert.Equal(3, result!.PaymentCount);

        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        var rows = (await conn.QueryAsync<(int Ordinal, decimal Subtotal, decimal Vat, decimal Total)>(
            "SELECT ordinal AS Ordinal, subtotal_amount AS Subtotal, vat_amount AS Vat, total_amount AS Total FROM contract_payment_schedules WHERE contract_id=@contractId ORDER BY ordinal",
            new { contractId })).ToList();

        Assert.Equal(3, rows.Count);
        // 30% of 300M = 90M subtotal + 9M VAT = 99M total
        Assert.Equal(90_000_000m, rows[0].Subtotal);
        Assert.Equal(9_000_000m, rows[0].Vat);
        Assert.Equal(99_000_000m, rows[0].Total);
        // 40% of 300M = 120M subtotal + 12M VAT = 132M total
        Assert.Equal(120_000_000m, rows[1].Subtotal);
        Assert.Equal(12_000_000m, rows[1].Vat);
        Assert.Equal(132_000_000m, rows[1].Total);
        // 30% of 300M = 90M subtotal + 9M VAT = 99M total
        Assert.Equal(90_000_000m, rows[2].Subtotal);
        Assert.Equal(9_000_000m, rows[2].Vat);
        Assert.Equal(99_000_000m, rows[2].Total);
        Assert.Equal(99_000_000m + 132_000_000m + 99_000_000m, rows.Sum(r => r.Total));
    }

    [Fact]
    public async Task ProcessAsync_DuplicateContractNumber_NeverCrashes_PersistsNullAndAlertsInstead()
    {
        // The exact production bug: ProcessAsync detected a duplicate
        // contract_number, added a DUPLICATE_CONTRACT alert, but then
        // unconditionally wrote the colliding value anyway — tripping
        // uq_contract_number_department (partial unique index, migration
        // 027) as an unhandled 23505 that crashed the whole /process call.
        if (_scopedConnectionString is null) return;
        var repo = Repo();
        var existingId = await CreateContractAsync(repo);
        await repo.ProcessAsync(_tenantId, _departmentId, _userId, existingId,
            new ProcessContractRequest(null, "HD-DUP-001", 100m, 10m, 110m, null, null,
                Parties: [new("buyer", "A", "0101234567", null, null), new("seller", "B", "0201234567", null, null)],
                PaymentTerms: [new(1, "Một lần", 100m, new DateOnly(2026, 1, 1))], Milestones: null),
            default);

        var newContractId = await CreateContractAsync(repo);
        var duplicateRequest = new ProcessContractRequest(null, "HD-DUP-001", 100m, 10m, 110m, null, null,
            Parties: [new("buyer", "C", "0301234567", null, null), new("seller", "D", "0401234567", null, null)],
            PaymentTerms: [new(1, "Một lần", 100m, new DateOnly(2026, 1, 1))], Milestones: null);

        // Must not throw — this is the regression itself.
        var result = await repo.ProcessAsync(_tenantId, _departmentId, _userId, newContractId, duplicateRequest, default);

        Assert.NotNull(result);
        Assert.Equal("NEEDS_REVIEW", result!.WorkflowState);

        var persistedNumber = await GetScalarAsync<string?>(
            "SELECT contract_number FROM contracts WHERE id=@newContractId", new { newContractId });
        Assert.Null(persistedNumber);

        var alertCode = await GetScalarAsync<string?>(
            "SELECT code FROM contract_alerts WHERE contract_id=@newContractId AND code='DUPLICATE_CONTRACT'", new { newContractId });
        Assert.Equal("DUPLICATE_CONTRACT", alertCode);

        // The contract must remain reviewable afterward — not corrupted or
        // stuck by the failed attempt.
        var aggregate = await repo.FindAggregateAsync(_tenantId, _departmentId, newContractId, default);
        Assert.NotNull(aggregate);
    }

    [Fact]
    public async Task ProcessAsync_MissingRequiredReferencedDocument_ReturnsControlledWarning_NeverThrows()
    {
        // "Missing Appendix 01" must become a controlled NEEDS_DOCUMENT
        // state + MISSING_REFERENCED_DOCUMENT alert — never an exception.
        if (_scopedConnectionString is null) return;
        var repo = Repo();
        var contractId = await CreateContractAsync(repo);
        var request = new ProcessContractRequest(null, null, 100m, 10m, 110m, null, null,
            Parties: [new("buyer", "A", "0101234567", null, null), new("seller", "B", "0201234567", null, null)],
            PaymentTerms: [new(1, "Một lần", 100m, new DateOnly(2026, 1, 1))], Milestones: null,
            ReferencedDocuments: [new ReferencedDocumentDto("appendix", "01", Required: true)]);

        var result = await repo.ProcessAsync(_tenantId, _departmentId, _userId, contractId, request, default);

        Assert.NotNull(result);
        Assert.Equal("NEEDS_DOCUMENT", result!.WorkflowState);
        var alertCode = await GetScalarAsync<string?>(
            "SELECT code FROM contract_alerts WHERE contract_id=@contractId AND code='MISSING_REFERENCED_DOCUMENT'", new { contractId });
        Assert.Equal("MISSING_REFERENCED_DOCUMENT", alertCode);

        // Contract remains reviewable, not corrupted.
        var aggregate = await repo.FindAggregateAsync(_tenantId, _departmentId, contractId, default);
        Assert.NotNull(aggregate);
    }

    [Fact]
    public async Task ProcessAsync_NeverCreatesArApOrJournalRows()
    {
        // No Contract path may create AR/AP/journal/voucher state — enforced
        // here by the test fixture itself deliberately never creating those
        // tables (vouchers, journal_entries, sim_ar_ledger, sim_ap_ledger):
        // if ProcessAsync's SQL touched any of them, this would fail with
        // "relation does not exist", not silently pass.
        if (_scopedConnectionString is null) return;
        var repo = Repo();
        var contractId = await CreateContractAsync(repo);
        var request = new ProcessContractRequest(null, null, 300_000_000m, 10m, 330_000_000m, null, null,
            Parties: [new("buyer", "A", "0101234567", null, null), new("seller", "B", "0201234567", null, null)],
            PaymentTerms:
            [
                new(1, "Đợt 1", 30m, new DateOnly(2026, 9, 20)),
                new(2, "Đợt 2", 40m, new DateOnly(2026, 11, 1)),
                new(3, "Đợt 3", 30m, new DateOnly(2026, 12, 31)),
            ],
            Milestones: [new(1, "Bàn giao", new DateOnly(2026, 12, 31))]);

        var result = await repo.ProcessAsync(_tenantId, _departmentId, _userId, contractId, request, default);
        Assert.NotNull(result);

        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        var tableExists = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=current_schema() AND table_name IN ('vouchers','journal_entries','sim_ar_ledger','sim_ap_ledger'))");
        Assert.False(tableExists);
    }

    private async Task<T> GetScalarAsync<T>(string sql, object param)
    {
        await using var conn = new NpgsqlConnection(_scopedConnectionString!);
        return await conn.ExecuteScalarAsync<T>(sql, param);
    }

    [Fact]
    public async Task RepositoryScope_DeniesOtherTenantAndDepartment()
    {
        if(_scopedConnectionString is null)return;var repo=Repo();var id=await CreateContractAsync(repo);
        Assert.Null(await repo.FindAsync(_tenantId,Guid.NewGuid(),id,default));
        Assert.Null(await repo.FindAsync(Guid.NewGuid(),_departmentId,id,default));
        Assert.Empty(await repo.ListAsync(_tenantId,Guid.NewGuid(),null,null,50,0,default));
    }

    [Fact]
    public async Task FinalizedContract_CannotBeEditedDeletedOrReprocessed_AndHandoffIsIdempotent()
    {
        if(_scopedConnectionString is null)return;var repo=Repo();var id=await CreateContractAsync(repo);
        var request=new ProcessContractRequest(null,"HD-1",100m,10m,110m,null,null,
            Parties:[new("buyer","A","0101234567",null,null),new("seller","B","0201234567",null,null)],
            PaymentTerms:[new(1,"Một lần",100m,new DateOnly(2026,1,1))],Milestones:null);
        Assert.Equal("WAITING_REVIEW",(await repo.ProcessAsync(_tenantId,_departmentId,_userId,id,request,default))!.WorkflowState);
        Assert.Equal("APPROVED",await repo.ReviewAsync(_tenantId,_departmentId,_userId,id,true,"ok",default));
        dynamic first=await repo.PrepareHandoffAsync(_tenantId,_departmentId,_userId,id,default);
        dynamic retry=await repo.PrepareHandoffAsync(_tenantId,_departmentId,_userId,id,default);
        Assert.Equal((Guid)first.Id,(Guid)retry.Id);
        Assert.Contains("EXPORT_ONLY",(string)first.Payload);
        Assert.Contains("paymentSchedules",(string)first.Payload);
        Assert.Contains("counterpartyProposals",(string)first.Payload);
        Assert.Null(await repo.ProcessAsync(_tenantId,_departmentId,_userId,id,request,default));
        Assert.Null(await repo.UpdateAsync(_tenantId,_departmentId,id,new UpdateContractRequest("changed",null,null,null,null,null,null,null,null,null),default));
        Assert.False(await repo.DeleteAsync(_tenantId,_departmentId,id,default));
    }
}
