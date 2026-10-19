using Aiq.Gateway.Customers;
using Aiq.Gateway.Data;
using Aiq.Gateway.Payments;
using Aiq.Gateway.Suppliers;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Chứng từ THU / CHI -> ADMIN duyệt -> tài khoản tiền -> đối trừ AR/AP,
/// against a real Postgres (isolated schema per run). The base tables are the
/// final shape of 018/020/030/031/032/045/049; migration 051 itself is
/// executed from the real file, so the tests also prove it applies (and
/// re-applies) cleanly.
/// </summary>
public sealed partial class CashSettlementIntegrationTests : IAsyncLifetime
{
    static CashSettlementIntegrationTests()
    {
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyTypeHandler());
        SqlMapper.AddTypeHandler(new StringArrayTypeHandler());
    }

    private const string MyTaxCode = "0123456789";

    private readonly string? _baseConnectionString = ResolveTestConnectionString();
    private readonly string _schema = $"cash_settlement_test_{Guid.NewGuid():N}";
    private string? _cs;

    private Guid _tenantId, _departmentId, _adminId, _userId;
    private Guid _otherDepartmentId, _otherTenantId, _otherTenantDepartmentId;

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

    private static async Task<string> ReadMigrationAsync(string name)
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "Data", "Migrations", name);
        if (File.Exists(outputPath)) return await File.ReadAllTextAsync(outputPath);
        return await File.ReadAllTextAsync(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../src/Aiq.Gateway/Data/Migrations", name)));
    }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_baseConnectionString)) return;
        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync($"CREATE SCHEMA {_schema}");
        }
        _cs = new NpgsqlConnectionStringBuilder(_baseConnectionString) { SearchPath = _schema }.ConnectionString;

        await using var db = new NpgsqlConnection(_cs);
        await db.OpenAsync();
        await db.ExecuteAsync("""
            CREATE EXTENSION IF NOT EXISTS pgcrypto;
            CREATE TABLE tenants (id UUID PRIMARY KEY, status TEXT NOT NULL DEFAULT 'active', company_tax_id TEXT);
            CREATE TABLE departments (id UUID PRIMARY KEY, tenant_id UUID NOT NULL REFERENCES tenants(id), status TEXT NOT NULL DEFAULT 'active', UNIQUE (id, tenant_id));
            CREATE TABLE users (id UUID PRIMARY KEY, tenant_id UUID NOT NULL, department_id UUID);
            -- Contract tables: final shape of 023/027/028/042/043 (columns the
            -- settlement -> schedule recompute and the penalty job read/write).
            CREATE TABLE contracts (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id UUID NOT NULL, department_id UUID NOT NULL,
                title TEXT NOT NULL DEFAULT 'HĐ', contract_number TEXT, contract_code TEXT,
                workflow_state TEXT NOT NULL DEFAULT 'APPROVED', penalty_percent NUMERIC(7,4),
                progress_percent NUMERIC(5,2) NOT NULL DEFAULT 0
            );
            CREATE TABLE contract_payment_schedules (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), contract_id UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
                tenant_id UUID NOT NULL, department_id UUID NOT NULL,
                ordinal INT NOT NULL, description TEXT NOT NULL, percentage NUMERIC(7,4) NOT NULL,
                subtotal_amount NUMERIC(18,2) NOT NULL, vat_amount NUMERIC(18,2) NOT NULL, total_amount NUMERIC(18,2) NOT NULL,
                due_date DATE, status TEXT NOT NULL DEFAULT 'pending', created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                trigger_event TEXT, offset_days INT, expected_date DATE,
                penalty_overdue_amount NUMERIC(18,2), penalty_overdue_days INT, penalty_rate NUMERIC(9,4),
                penalty_amount NUMERIC(18,2), penalty_calculated_at TIMESTAMPTZ,
                UNIQUE (contract_id, ordinal)
            );
            CREATE TABLE contract_milestones (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), contract_id UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
                tenant_id UUID NOT NULL, department_id UUID NOT NULL, ordinal INT NOT NULL, name TEXT NOT NULL,
                due_date DATE, reminder_days INT NOT NULL DEFAULT 7, status TEXT NOT NULL DEFAULT 'pending',
                trigger_event TEXT, duration_months INT, expected_date DATE, UNIQUE (contract_id, ordinal)
            );
            CREATE TABLE contract_history (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), contract_id UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
                tenant_id UUID NOT NULL, department_id UUID NOT NULL, actor_id UUID REFERENCES users(id),
                event_type TEXT NOT NULL, from_state TEXT, to_state TEXT, details JSONB NOT NULL DEFAULT '{}'::jsonb,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE tenant_company_profiles (
                tenant_id UUID PRIMARY KEY REFERENCES tenants(id),
                legal_name TEXT, tax_code TEXT, short_name TEXT, aliases TEXT[] NOT NULL DEFAULT '{}',
                registered_address TEXT, legal_representative TEXT, representative_title TEXT,
                phone TEXT, email TEXT, website TEXT,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), updated_by UUID
            );

            CREATE TABLE vouchers (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                user_id UUID NOT NULL REFERENCES users(id), tenant_id UUID NOT NULL, department_id UUID,
                voucher_type TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'extracted',
                document_number TEXT, document_series TEXT, document_date DATE,
                seller_name TEXT, seller_mst TEXT, buyer_name TEXT, buyer_mst TEXT,
                -- 041 + 057: tax verification state (read by the identity-change hook)
                tct_verified BOOLEAN NOT NULL DEFAULT FALSE, tct_verified_at TIMESTAMPTZ,
                tax_verification_status TEXT, tax_verification_reason TEXT, tax_verification_checked_at TIMESTAMPTZ,
                tax_verified_by UUID, tax_verification_fingerprint TEXT, tax_verification_evidence JSONB,
                total_amount NUMERIC(18,2), currency TEXT NOT NULL DEFAULT 'VND', payment_method TEXT,
                extracted_fields JSONB NOT NULL DEFAULT '{}'::jsonb, raw_ocr_text TEXT,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE TABLE voucher_journal_suggestions (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                voucher_id UUID NOT NULL REFERENCES vouchers(id) ON DELETE CASCADE,
                entry_no INT NOT NULL, debit_account TEXT NOT NULL, credit_account TEXT NOT NULL,
                amount NUMERIC(18,2) NOT NULL, description TEXT, confidence NUMERIC(3,2),
                accepted_by UUID REFERENCES users(id), accepted_at TIMESTAMPTZ, rejected_at TIMESTAMPTZ, reject_reason TEXT,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), UNIQUE (voucher_id, entry_no)
            );

            CREATE TABLE sim_customers (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                tenant_id UUID NOT NULL, department_id UUID,
                customer_code TEXT NOT NULL, name TEXT NOT NULL, mst TEXT,
                address TEXT, phone TEXT, email TEXT, category TEXT DEFAULT 'cong_ty',
                credit_limit NUMERIC(18,2) DEFAULT 0, credit_terms_days INT DEFAULT 0,
                current_ar NUMERIC(18,2) DEFAULT 0,
                customer_type TEXT NOT NULL DEFAULT 'company', representative TEXT,
                status TEXT NOT NULL DEFAULT 'active', notes TEXT, created_by UUID,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE (tenant_id, customer_code), UNIQUE (tenant_id, mst)
            );
            CREATE TABLE sim_ar_ledger (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                tenant_id UUID NOT NULL, customer_id UUID NOT NULL REFERENCES sim_customers(id),
                voucher_id UUID, contract_id UUID REFERENCES contracts(id),
                contract_payment_schedule_id UUID REFERENCES contract_payment_schedules(id),
                document_date DATE NOT NULL, document_number TEXT NOT NULL, document_type TEXT NOT NULL,
                debit NUMERIC(18,2) DEFAULT 0, credit NUMERIC(18,2) DEFAULT 0,
                balance_after NUMERIC(18,2), due_date DATE, reference_note TEXT,
                paid_amount NUMERIC(18,2) NOT NULL DEFAULT 0, status TEXT NOT NULL DEFAULT 'open',
                allocated_invoice_id UUID REFERENCES sim_ar_ledger(id),
                created_at TIMESTAMPTZ DEFAULT NOW()
            );
            CREATE UNIQUE INDEX uq_sim_ar_ledger_tenant_voucher ON sim_ar_ledger (tenant_id, voucher_id) WHERE voucher_id IS NOT NULL;

            CREATE TABLE sim_suppliers (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                tenant_id UUID NOT NULL, department_id UUID,
                supplier_code TEXT NOT NULL, name TEXT NOT NULL, mst TEXT,
                country TEXT DEFAULT 'VN', currency TEXT DEFAULT 'VND',
                payment_terms TEXT, payment_terms_days INT DEFAULT 0, current_ap NUMERIC(18,2) DEFAULT 0,
                supplier_type TEXT NOT NULL DEFAULT 'company', representative TEXT,
                phone TEXT, email TEXT, address TEXT,
                status TEXT NOT NULL DEFAULT 'active', notes TEXT, created_by UUID,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE (tenant_id, supplier_code)
            );
            CREATE TABLE sim_ap_ledger (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                tenant_id UUID NOT NULL, supplier_id UUID NOT NULL REFERENCES sim_suppliers(id),
                voucher_id UUID, contract_id UUID REFERENCES contracts(id),
                contract_payment_schedule_id UUID REFERENCES contract_payment_schedules(id),
                document_date DATE NOT NULL, document_number TEXT NOT NULL, document_type TEXT NOT NULL,
                debit NUMERIC(18,2) DEFAULT 0, credit NUMERIC(18,2) DEFAULT 0,
                balance_after NUMERIC(18,2), due_date DATE, reference_note TEXT,
                paid_amount NUMERIC(18,2) NOT NULL DEFAULT 0, status TEXT NOT NULL DEFAULT 'open',
                allocated_invoice_id UUID REFERENCES sim_ap_ledger(id),
                created_at TIMESTAMPTZ DEFAULT NOW()
            );
            CREATE UNIQUE INDEX uq_sim_ap_ledger_tenant_voucher ON sim_ap_ledger (tenant_id, voucher_id) WHERE voucher_id IS NOT NULL;

            CREATE TABLE sim_bank_accounts (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                tenant_id UUID NOT NULL REFERENCES tenants(id), account_number TEXT NOT NULL, bank_name TEXT NOT NULL,
                account_holder TEXT NOT NULL, currency TEXT DEFAULT 'VND', current_balance NUMERIC(18,2) DEFAULT 0,
                gl_account_code TEXT NOT NULL, is_primary BOOLEAN DEFAULT FALSE, created_at TIMESTAMPTZ DEFAULT NOW(),
                UNIQUE (tenant_id, account_number)
            );
            """);
        await db.ExecuteAsync("""
            CREATE TABLE receipt_match_results (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id UUID NOT NULL, department_id UUID NOT NULL,
                voucher_id UUID NOT NULL REFERENCES vouchers(id) ON DELETE CASCADE, outcome TEXT NOT NULL DEFAULT 'PARTIAL_PAYMENT',
                receipt_amount NUMERIC(18,2) NOT NULL DEFAULT 0, review_status TEXT NOT NULL DEFAULT 'proposed',
                reviewed_by UUID, reviewed_at TIMESTAMPTZ, updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), UNIQUE (voucher_id)
            );
            """);
        await db.ExecuteAsync(await ReadMigrationAsync("051_cash_settlement.sql"));
        await db.ExecuteAsync(await ReadMigrationAsync("053_settlement_reference_audit.sql"));
        // 020 shape of the (previously unused) statement-line table, then 054.
        await db.ExecuteAsync("""
            CREATE TABLE sim_bank_transactions (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                account_id UUID NOT NULL REFERENCES sim_bank_accounts(id) ON DELETE CASCADE,
                tx_date DATE NOT NULL, tx_ref TEXT, description TEXT NOT NULL,
                debit NUMERIC(18,2) DEFAULT 0, credit NUMERIC(18,2) DEFAULT 0, balance_after NUMERIC(18,2),
                counterparty_name TEXT, counterparty_account TEXT, matched_document_type TEXT, matched_document_number TEXT,
                suggested_gl_debit TEXT, suggested_gl_credit TEXT, reconciled BOOLEAN DEFAULT FALSE, created_at TIMESTAMPTZ DEFAULT NOW()
            );
            """);
        await db.ExecuteAsync(await ReadMigrationAsync("054_bank_account_master_and_statement_lines.sql"));
        await db.ExecuteAsync(await ReadMigrationAsync("056_cash_settlement_statement_source.sql"));
        // 059: posting lines may link to the expense claim whose recognition produced them.
        await db.ExecuteAsync("ALTER TABLE voucher_journal_suggestions ADD COLUMN IF NOT EXISTS expense_claim_id UUID");
        // 062: the document workflow review gate checked by every posting path.
        await db.ExecuteAsync("ALTER TABLE vouchers ADD COLUMN IF NOT EXISTS document_review_status TEXT");
        // 063: extraction-owned document blockers (legacy L2 failure).
        await db.ExecuteAsync("ALTER TABLE vouchers ADD COLUMN IF NOT EXISTS processing_blockers TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[]");
        // 064: contract payment-discount terms (+ the contract columns/parties they read) and the decision audit.
        await db.ExecuteAsync("""
            ALTER TABLE contracts ADD COLUMN IF NOT EXISTS direction TEXT, ADD COLUMN IF NOT EXISTS currency TEXT DEFAULT 'VND';
            CREATE TABLE IF NOT EXISTS contract_parties (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), contract_id UUID NOT NULL REFERENCES contracts(id) ON DELETE CASCADE,
                tenant_id UUID NOT NULL, department_id UUID NOT NULL, party_role TEXT NOT NULL, name TEXT NOT NULL, tax_code TEXT,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW());
            """);
        await db.ExecuteAsync(await ReadMigrationAsync("064_contract_payment_discount.sql"));

        _tenantId = Guid.NewGuid(); _departmentId = Guid.NewGuid(); _otherDepartmentId = Guid.NewGuid();
        _adminId = Guid.NewGuid(); _userId = Guid.NewGuid();
        _otherTenantId = Guid.NewGuid(); _otherTenantDepartmentId = Guid.NewGuid();
        await db.ExecuteAsync("""
            INSERT INTO tenants(id) VALUES (@t), (@ot);
            INSERT INTO departments(id, tenant_id) VALUES (@d, @t), (@od, @t), (@otd, @ot);
            INSERT INTO users(id, tenant_id, department_id) VALUES (@a, @t, @d), (@u, @t, @d);
            INSERT INTO tenant_company_profiles(tenant_id, legal_name, tax_code, short_name)
                VALUES (@t, 'Công ty TNHH Sáu Trực', @tax, 'Sáu Trực');
            """, new { t = _tenantId, ot = _otherTenantId, d = _departmentId, od = _otherDepartmentId,
                       otd = _otherTenantDepartmentId, a = _adminId, u = _userId, tax = MyTaxCode });
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(_baseConnectionString)) return;
        await using var conn = new NpgsqlConnection(_baseConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP SCHEMA IF EXISTS {_schema} CASCADE");
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private NpgsqlConnectionFactory Factory() => new(_cs!);
    private ICashSettlementRepository Settlement() => new CashSettlementRepository(Factory());
    private ICustomerRepository Customers() => new CustomerRepository(Factory());
    private ISupplierRepository Suppliers() => new SupplierRepository(Factory());
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));

    private async Task<T> Scalar<T>(string sql, object? args = null)
    {
        await using var c = new NpgsqlConnection(_cs);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<T>(sql, args);
    }

    private async Task<Guid> SeedCustomerAsync(string code, string name, string mst, Guid? departmentId = null)
    {
        return await Scalar<Guid>(
            "INSERT INTO sim_customers(tenant_id, department_id, customer_code, name, mst) VALUES (@t,@d,@code,@name,@mst) RETURNING id",
            new { t = _tenantId, d = departmentId ?? _departmentId, code, name, mst });
    }

    private async Task<Guid> SeedSupplierAsync(string code, string name, string mst)
    {
        return await Scalar<Guid>(
            "INSERT INTO sim_suppliers(tenant_id, department_id, supplier_code, name, mst) VALUES (@t,@d,@code,@name,@mst) RETURNING id",
            new { t = _tenantId, d = _departmentId, code, name, mst });
    }

    private async Task<Guid> BookArInvoiceAsync(Guid customerId, string doc, decimal amount, int dueInDays = 30)
    {
        await Customers().BookInvoiceAsync(_tenantId, customerId, Guid.NewGuid(), doc, Today.AddDays(-5), Today.AddDays(dueInDays), amount, null, null, default);
        return await Scalar<Guid>("SELECT id FROM sim_ar_ledger WHERE customer_id=@customerId AND document_number=@doc AND document_type='invoice'", new { customerId, doc });
    }

    private async Task<Guid> BookApInvoiceAsync(Guid supplierId, string doc, decimal amount, int dueInDays = 30)
    {
        await Suppliers().BookInvoiceAsync(_tenantId, supplierId, Guid.NewGuid(), doc, Today.AddDays(-5), Today.AddDays(dueInDays), amount, null, null, default);
        return await Scalar<Guid>("SELECT id FROM sim_ap_ledger WHERE supplier_id=@supplierId AND document_number=@doc AND document_type='invoice'", new { supplierId, doc });
    }

    /// <summary>Receipt: customer (buyer) pays MY_COMPANY (seller). Created by the ADMIN unless stated.</summary>
    private async Task<Guid> ReceiptVoucherAsync(string doc, decimal amount, string customerName, string customerMst,
        string? reference = null, string method = "Chuyển khoản", Guid? createdBy = null, Guid? departmentId = null)
    {
        var fields = System.Text.Json.JsonSerializer.Serialize(new { reason = reference ?? "Thu tiền hàng" });
        return await Scalar<Guid>("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, document_number, document_date,
                seller_name, seller_mst, buyer_name, buyer_mst, total_amount, payment_method, extracted_fields)
            VALUES (@u, @t, @d, 'receipt', @doc, @date, 'Cong Ty TNHH Sáu Trực', @me, @customerName, @customerMst,
                @amount, @method, @fields::jsonb) RETURNING id
            """, new { u = createdBy ?? _adminId, t = _tenantId, d = departmentId ?? _departmentId, doc, date = Today,
                       me = MyTaxCode, customerName, customerMst, amount, method, fields });
    }

    /// <summary>Payment: MY_COMPANY (buyer) pays supplier (seller).</summary>
    private async Task<Guid> PaymentVoucherAsync(string doc, decimal amount, string supplierName, string supplierMst,
        string? reference = null, string method = "CK")
    {
        var fields = System.Text.Json.JsonSerializer.Serialize(new { reason = reference ?? "Chi trả tiền hàng" });
        return await Scalar<Guid>("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, document_number, document_date,
                seller_name, seller_mst, buyer_name, buyer_mst, total_amount, payment_method, extracted_fields)
            VALUES (@u, @t, @d, 'payment', @doc, @date, @supplierName, @supplierMst, 'Cong Ty TNHH Sáu Trực', @me,
                @amount, @method, @fields::jsonb) RETURNING id
            """, new { u = _adminId, t = _tenantId, d = _departmentId, doc, date = Today, me = MyTaxCode,
                       supplierName, supplierMst, amount, method, fields });
    }

    private static PostCashRequest Approve() => new(null, null, false, null);

    private Task<(decimal Paid, string Status)> ArInvoiceAsync(Guid id) => Row<(decimal, string)>("SELECT paid_amount, status FROM sim_ar_ledger WHERE id=@id", new { id });
    private Task<(decimal Paid, string Status)> ApInvoiceAsync(Guid id) => Row<(decimal, string)>("SELECT paid_amount, status FROM sim_ap_ledger WHERE id=@id", new { id });

    private async Task<T> Row<T>(string sql, object args)
    {
        await using var c = new NpgsqlConnection(_cs);
        await c.OpenAsync();
        return await c.QuerySingleAsync<T>(sql, args);
    }

    // ------------------------------------------------------------------
    // A. CUSTOMER RECEIPT
    // ------------------------------------------------------------------

    [Fact]
    public async Task Receipt_SameAdminCreatesAndApprovesOwnReceipt_PartialThenFull_ArReducedExactlyOnce()
    {
        if (_cs is null) return;
        var vietTin = await SeedCustomerAsync("CUS-VT", "Công ty TNHH Việt Tín", "0307890123");
        var other = await BookArInvoiceAsync(vietTin, "DEMO-INV-VT-001", 85_000_000m, dueInDays: 5);
        var inv = await BookArInvoiceAsync(vietTin, "DEMO-ST-VT-001", 39_600_000m);
        var arBefore = (await Customers().FindByIdAsync(_tenantId, _departmentId, vietTin, default))!.CurrentAr;
        Assert.Equal(124_600_000m, arBefore);

        // Receipt 1: 20,000,000 referencing the invoice — created AND approved by the same ADMIN.
        var r1 = await ReceiptVoucherAsync("PT-001", 20_000_000m, "Công ty TNHH Việt Tín", "0307890123", "Thanh toán HĐ DEMO-ST-VT-001 đợt 1");
        var ctx = await Settlement().GetContextAsync(_tenantId, _departmentId, r1, default);
        Assert.Equal(CashDirections.MoneyIn, ctx!.Direction.Direction);
        Assert.Equal(vietTin, ctx.Counterparty!.Id);
        Assert.Equal(MatchCertainty.Certain, ctx.Suggestion!.Certainty);
        Assert.Equal(inv, Assert.Single(ctx.Suggestion.Allocations).InvoiceId);
        Assert.Empty(ctx.Blockers);

        var posted = await Settlement().PostAsync(_tenantId, _departmentId, r1, _adminId, Approve(), default);
        Assert.Equal("posted", posted!.Outcome);
        Assert.Equal(124_600_000m, posted.PreviousBalance);
        Assert.Equal(104_600_000m, posted.NewBalance);
        var line = Assert.Single(posted.Journal);
        Assert.Equal(("1121", "131", 20_000_000m), (line.DebitAccount, line.CreditAccount, line.Amount));

        Assert.Equal((20_000_000m, "partial"), await ArInvoiceAsync(inv));
        Assert.Equal((0m, "open"), await ArInvoiceAsync(other));
        var report = await Customers().GetReceivablesAsync(_tenantId, _departmentId, vietTin, default);
        Assert.Equal(104_600_000m, report!.CurrentBalance131);
        var openInv = report.OpenInvoices.Single(i => i.Id == inv);
        Assert.Equal(19_600_000m, openInv.Outstanding);
        Assert.Equal("PARTIAL", openInv.SettlementStatus);
        Assert.Equal("verified", await Scalar<string>("SELECT status FROM vouchers WHERE id=@r1", new { r1 }));
        Assert.Equal(1, await Scalar<int>(
            "SELECT COUNT(*) FROM voucher_journal_suggestions WHERE voucher_id=@r1 AND source='posting' AND accepted_by=@a AND debit_account='1121' AND credit_account='131'",
            new { r1, a = _adminId }));

        // Double-click / retry: nothing moves again.
        var again = await Settlement().PostAsync(_tenantId, _departmentId, r1, _adminId, Approve(), default);
        Assert.Equal("already_posted", again!.Outcome);
        Assert.Equal((20_000_000m, "partial"), await ArInvoiceAsync(inv));
        Assert.Equal(104_600_000m, (await Customers().FindByIdAsync(_tenantId, _departmentId, vietTin, default))!.CurrentAr);

        // Receipt 2: 19,600,000 with no reference — exact outstanding amount match.
        var r2 = await ReceiptVoucherAsync("PT-002", 19_600_000m, "Công ty TNHH Việt Tín", "0307890123");
        var ctx2 = await Settlement().GetContextAsync(_tenantId, _departmentId, r2, default);
        Assert.Equal("exact_amount", ctx2!.Suggestion!.Method);
        var posted2 = await Settlement().PostAsync(_tenantId, _departmentId, r2, _adminId, Approve(), default);
        Assert.Equal(85_000_000m, posted2!.NewBalance);
        Assert.Equal((39_600_000m, "paid"), await ArInvoiceAsync(inv));
        var afterFull = await Customers().GetReceivablesAsync(_tenantId, _departmentId, vietTin, default);
        Assert.DoesNotContain(afterFull!.OpenInvoices, i => i.Id == inv);
        Assert.Equal(2, await Scalar<int>(
            "SELECT COUNT(*) FROM payment_applications WHERE ar_invoice_id=@inv AND status='active'", new { inv }));
    }

    [Fact]
    public async Task Receipt_DuplicateConcurrentPosting_AppliesMoneyExactlyOnce()
    {
        if (_cs is null) return;
        var c = await SeedCustomerAsync("CUS-DUP", "Công ty Dup", "0300000011");
        var inv = await BookArInvoiceAsync(c, "HD-DUP", 50_000_000m);
        var r = await ReceiptVoucherAsync("PT-DUP", 10_000_000m, "Công ty Dup", "0300000011", "HD-DUP");

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => Settlement().PostAsync(_tenantId, _departmentId, r, _adminId, Approve(), default)));

        Assert.Equal(1, results.Count(x => x!.Outcome == "posted"));
        Assert.Equal(5, results.Count(x => x!.Outcome == "already_posted"));
        Assert.Equal((10_000_000m, "partial"), await ArInvoiceAsync(inv));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM cash_transactions WHERE voucher_id=@r", new { r }));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM sim_ar_ledger WHERE document_type='receipt' AND allocated_invoice_id=@inv", new { inv }));
        Assert.Equal(40_000_000m, (await Customers().FindByIdAsync(_tenantId, _departmentId, c, default))!.CurrentAr);
    }

    [Fact]
    public async Task Receipt_AmbiguousMatch_RequiresAdminSelection_AndWritesNothing_ThenAdminChooses()
    {
        if (_cs is null) return;
        var c = await SeedCustomerAsync("CUS-AMB", "Công ty Amb", "0300000012");
        var a = await BookArInvoiceAsync(c, "HD-A1", 30_000_000m);
        var b = await BookArInvoiceAsync(c, "HD-A2", 40_000_000m);
        var r = await ReceiptVoucherAsync("PT-AMB", 20_000_000m, "Công ty Amb", "0300000012");

        var ex = await Assert.ThrowsAsync<CashSettlementException>(() =>
            Settlement().PostAsync(_tenantId, _departmentId, r, _adminId, Approve(), default));
        Assert.Equal("admin_selection_required", ex.Code);
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM cash_transactions WHERE voucher_id=@r", new { r }));
        Assert.Equal(70_000_000m, (await Customers().FindByIdAsync(_tenantId, _departmentId, c, default))!.CurrentAr);

        var chosen = await Settlement().PostAsync(_tenantId, _departmentId, r, _adminId,
            new PostCashRequest(null, new[] { new AllocationInput(b, 20_000_000m) }, false, null), default);
        Assert.Equal("posted", chosen!.Outcome);
        Assert.Equal("admin_selected", Assert.Single(chosen.Applications).MatchMethod);
        Assert.Equal((0m, "open"), await ArInvoiceAsync(a));
        Assert.Equal((20_000_000m, "partial"), await ArInvoiceAsync(b));
    }

    [Fact]
    public async Task Receipt_UnmatchedReceipt_IsHeldUnapplied_AndNeverReducesArbitraryAr()
    {
        if (_cs is null) return;
        // Customer with NO open invoice pays; an unrelated customer has open AR.
        var payer = await SeedCustomerAsync("CUS-ADV", "Công ty Advance", "0300000013");
        var bystander = await SeedCustomerAsync("CUS-BY", "Công ty Bystander", "0300000014");
        var byInv = await BookArInvoiceAsync(bystander, "HD-BY", 20_000_000m);
        var r = await ReceiptVoucherAsync("PT-ADV", 20_000_000m, "Công ty Advance", "0300000013", "HD-BY");

        var posted = await Settlement().PostAsync(_tenantId, _departmentId, r, _adminId, Approve(), default);
        Assert.Equal("posted", posted!.Outcome);
        Assert.Empty(posted.Applications);
        Assert.Equal(20_000_000m, posted.Posting!.UnappliedAmount);
        Assert.Equal((0m, "open"), await ArInvoiceAsync(byInv));
        Assert.Equal(20_000_000m, (await Customers().FindByIdAsync(_tenantId, _departmentId, bystander, default))!.CurrentAr);
        var report = await Customers().GetReceivablesAsync(_tenantId, _departmentId, payer, default);
        Assert.Equal(0m, report!.CurrentBalance131);
        Assert.Equal(20_000_000m, report.UnappliedAmount);

        // Ambiguous case where ADMIN explicitly chooses to hold the money unapplied.
        var c2 = await SeedCustomerAsync("CUS-HOLD", "Công ty Hold", "0300000015");
        await BookArInvoiceAsync(c2, "HD-H1", 30_000_000m);
        await BookArInvoiceAsync(c2, "HD-H2", 30_000_000m);
        var r2 = await ReceiptVoucherAsync("PT-HOLD", 5_000_000m, "Công ty Hold", "0300000015");
        var held = await Settlement().PostAsync(_tenantId, _departmentId, r2, _adminId, new PostCashRequest(null, null, true, null), default);
        Assert.Equal(5_000_000m, held!.Posting!.UnappliedAmount);
        Assert.Equal(60_000_000m, held.NewBalance);
    }

    [Fact]
    public async Task Receipt_OnePayment_LaterAllocatedToMultipleInvoices()
    {
        if (_cs is null) return;
        var c = await SeedCustomerAsync("CUS-MULTI", "Công ty Multi", "0300000016");
        var a = await BookArInvoiceAsync(c, "HD-M1", 30_000_000m);
        var b = await BookArInvoiceAsync(c, "HD-M2", 50_000_000m);
        var r = await ReceiptVoucherAsync("PT-MULTI", 60_000_000m, "Công ty Multi", "0300000016");
        await Settlement().PostAsync(_tenantId, _departmentId, r, _adminId, new PostCashRequest(null, null, true, null), default);

        var allocs = new[] { new AllocationInput(a, 30_000_000m), new AllocationInput(b, 30_000_000m) };
        var allocated = await Settlement().AllocateAsync(_tenantId, _departmentId, r, _adminId, allocs, default);
        Assert.Equal("allocated", allocated!.Outcome);
        Assert.Equal(0m, allocated.Posting!.UnappliedAmount);
        Assert.Equal(20_000_000m, allocated.NewBalance);
        Assert.Equal((30_000_000m, "paid"), await ArInvoiceAsync(a));
        Assert.Equal((30_000_000m, "partial"), await ArInvoiceAsync(b));

        // Retry of the same allocation is a no-op; anything beyond the posting's money is refused.
        var retry = await Settlement().AllocateAsync(_tenantId, _departmentId, r, _adminId, allocs, default);
        Assert.Equal("already_allocated", retry!.Outcome);
        Assert.Equal((30_000_000m, "partial"), await ArInvoiceAsync(b));
        var r2 = await ReceiptVoucherAsync("PT-MULTI-2", 5_000_000m, "Công ty Multi", "0300000016");
        await Settlement().PostAsync(_tenantId, _departmentId, r2, _adminId, new PostCashRequest(null, null, true, null), default);
        var over = await Assert.ThrowsAsync<CashSettlementException>(() => Settlement().AllocateAsync(
            _tenantId, _departmentId, r2, _adminId, new[] { new AllocationInput(b, 6_000_000m) }, default));
        Assert.Equal("allocation_exceeds_available", over.Code);
    }

    [Fact]
    public async Task Receipt_Unapply_RestoresAr_KeepsHistory_ThenReverseVoidsPosting()
    {
        if (_cs is null) return;
        var c = await SeedCustomerAsync("CUS-UN", "Công ty Unapply", "0300000017");
        var inv = await BookArInvoiceAsync(c, "HD-UN", 39_600_000m);
        var r = await ReceiptVoucherAsync("PT-UN", 20_000_000m, "Công ty Unapply", "0300000017", "HD-UN");
        await Settlement().PostAsync(_tenantId, _departmentId, r, _adminId, Approve(), default);
        Assert.Equal((20_000_000m, "partial"), await ArInvoiceAsync(inv));

        var un = await Settlement().UnapplyAsync(_tenantId, _departmentId, r, _adminId, null, "sai hoá đơn", default);
        Assert.Equal("unapplied", un!.Outcome);
        Assert.Equal(39_600_000m, un.NewBalance);
        Assert.Equal((0m, "open"), await ArInvoiceAsync(inv));
        Assert.Equal(20_000_000m, un.Posting!.UnappliedAmount);
        Assert.Equal("reversed", Assert.Single(un.Applications).Status);
        // History preserved: the apply row AND a reversing row, nothing deleted.
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM sim_ar_ledger WHERE allocated_invoice_id=@inv AND document_type='receipt'", new { inv }));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM sim_ar_ledger WHERE allocated_invoice_id=@inv AND document_type='receipt_reversal' AND debit=20000000", new { inv }));
        var report = await Customers().GetReceivablesAsync(_tenantId, _departmentId, c, default);
        Assert.Equal("UNPAID", report!.OpenInvoices.Single().SettlementStatus);

        // Unapply again: nothing active -> no-op.
        Assert.Equal("nothing_to_unapply", (await Settlement().UnapplyAsync(_tenantId, _departmentId, r, _adminId, null, null, default))!.Outcome);

        var reversed = await Settlement().ReverseAsync(_tenantId, _departmentId, r, _adminId, "nhập nhầm", default);
        Assert.Equal("reversed", reversed!.Outcome);
        Assert.Equal("reversed", reversed.Posting!.Status);
        Assert.Equal(("131", "1121"), (reversed.Journal[0].DebitAccount, reversed.Journal[0].CreditAccount));
        Assert.Equal("extracted", await Scalar<string>("SELECT status FROM vouchers WHERE id=@r", new { r }));
        Assert.Equal(2, await Scalar<int>("SELECT COUNT(*) FROM voucher_journal_suggestions WHERE voucher_id=@r AND source IN ('posting','reversal')", new { r }));

        // After a void the voucher may be posted again (new posting row, old kept).
        var repost = await Settlement().PostAsync(_tenantId, _departmentId, r, _adminId, Approve(), default);
        Assert.Equal("posted", repost!.Outcome);
        Assert.Equal(2, await Scalar<int>("SELECT COUNT(*) FROM cash_transactions WHERE voucher_id=@r", new { r }));
        Assert.Equal((20_000_000m, "partial"), await ArInvoiceAsync(inv));
    }

    [Fact]
    public async Task Receipt_ExplicitAllocationToAnotherCustomersInvoice_IsRefused()
    {
        if (_cs is null) return;
        var c = await SeedCustomerAsync("CUS-X1", "Công ty X1", "0300000018");
        var foreign = await SeedCustomerAsync("CUS-X2", "Công ty X2", "0300000019");
        var ownInv = await BookArInvoiceAsync(c, "HD-X1", 10_000_000m);
        var foreignInv = await BookArInvoiceAsync(foreign, "HD-X2", 10_000_000m);
        var r = await ReceiptVoucherAsync("PT-X", 5_000_000m, "Công ty X1", "0300000018");

        var ex = await Assert.ThrowsAsync<CashSettlementException>(() => Settlement().PostAsync(_tenantId, _departmentId, r, _adminId,
            new PostCashRequest(null, new[] { new AllocationInput(foreignInv, 5_000_000m) }, false, null), default));
        Assert.Equal("invoice_not_open_for_counterparty", ex.Code);
        var tooMuch = await Assert.ThrowsAsync<CashSettlementException>(() => Settlement().PostAsync(_tenantId, _departmentId, r, _adminId,
            new PostCashRequest(null, new[] { new AllocationInput(ownInv, 11_000_000m) }, false, null), default));
        Assert.Equal("allocation_exceeds_outstanding", tooMuch.Code);
        Assert.Equal((0m, "open"), await ArInvoiceAsync(foreignInv));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM cash_transactions WHERE voucher_id=@r", new { r }));
    }

    [Fact]
    public async Task Receipt_WhereMyCompanyIsThePayer_IsBlocked_TitleAloneNeverReducesAr()
    {
        if (_cs is null) return;
        var c = await SeedCustomerAsync("CUS-DIR", "Công ty Dir", "0300000020");
        var inv = await BookArInvoiceAsync(c, "HD-DIR", 10_000_000m);
        // "Phiếu thu" but the extracted payer is MY_COMPANY and the payee is the customer.
        var r = await Scalar<Guid>("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, document_number, document_date,
                seller_name, seller_mst, buyer_name, buyer_mst, total_amount, payment_method)
            VALUES (@u, @t, @d, 'receipt', 'PT-DIR', @date, 'Công ty Dir', '0300000020', 'Sáu Trực', @me, 10000000, 'CK') RETURNING id
            """, new { u = _adminId, t = _tenantId, d = _departmentId, date = Today, me = MyTaxCode });

        var ctx = await Settlement().GetContextAsync(_tenantId, _departmentId, r, default);
        Assert.Equal(CashDirections.NeedsReview, ctx!.Direction.Direction);
        var ex = await Assert.ThrowsAsync<CashSettlementException>(() => Settlement().PostAsync(_tenantId, _departmentId, r, _adminId, Approve(), default));
        Assert.Equal("direction_needs_review", ex.Code);
        Assert.Equal((0m, "open"), await ArInvoiceAsync(inv));
    }

    [Fact]
    public async Task Receipt_UnknownPaymentMethod_BlocksUntilAdminChooses()
    {
        if (_cs is null) return;
        var c = await SeedCustomerAsync("CUS-PM", "Công ty PM", "0300000021");
        await BookArInvoiceAsync(c, "HD-PM", 10_000_000m);
        var r = await ReceiptVoucherAsync("PT-PM", 1_000_000m, "Công ty PM", "0300000021", "HD-PM", method: "");
        await Assert.ThrowsAsync<CashSettlementException>(() => Settlement().PostAsync(_tenantId, _departmentId, r, _adminId, Approve(), default));
        var posted = await Settlement().PostAsync(_tenantId, _departmentId, r, _adminId, new PostCashRequest(null, null, false, "TM"), default);
        Assert.Equal(("cash", "1111"), (posted!.Posting!.PaymentMethod, posted.Posting.CashAccount));
    }

    [Fact]
    public async Task Receipt_BankAccountBalance_UpdatedOnPost_RestoredOnReverse()
    {
        if (_cs is null) return;
        await Scalar<int>("INSERT INTO sim_bank_accounts(tenant_id, account_number, bank_name, account_holder, gl_account_code, is_primary, current_balance) VALUES (@t,'0011000123456','VCB','Sáu Trực','1121',true,100000000) RETURNING 1", new { t = _tenantId });
        var c = await SeedCustomerAsync("CUS-BANK", "Công ty Bank", "0300000022");
        await BookArInvoiceAsync(c, "HD-BANK", 10_000_000m);
        var r = await ReceiptVoucherAsync("PT-BANK", 10_000_000m, "Công ty Bank", "0300000022", "HD-BANK");
        await Settlement().PostAsync(_tenantId, _departmentId, r, _adminId, Approve(), default);
        Assert.Equal(110_000_000m, await Scalar<decimal>("SELECT current_balance FROM sim_bank_accounts WHERE tenant_id=@t", new { t = _tenantId }));
        await Settlement().ReverseAsync(_tenantId, _departmentId, r, _adminId, null, default);
        Assert.Equal(100_000_000m, await Scalar<decimal>("SELECT current_balance FROM sim_bank_accounts WHERE tenant_id=@t", new { t = _tenantId }));
    }

    // ------------------------------------------------------------------
    // B. SUPPLIER PAYMENT
    // ------------------------------------------------------------------

    [Fact]
    public async Task Payment_PartialThenFull_ApReducedExactlyOnce_ReportsReflectImmediately()
    {
        if (_cs is null) return;
        var daiPhat = await SeedSupplierAsync("SUP-DP", "Công ty TNHH Cung ứng Đại Phát", "0200000001");
        var inv = await BookApInvoiceAsync(daiPhat, "DEMO-PINV-DP-003", 55_000_000m);
        var summaryBefore = await Suppliers().GetApSummaryAsync(_tenantId, _departmentId, default);

        var p1 = await PaymentVoucherAsync("PC-001", 20_000_000m, "Công ty TNHH Cung ứng Đại Phát", "0200000001");
        var ctx = await Settlement().GetContextAsync(_tenantId, _departmentId, p1, default);
        Assert.Equal(CashDirections.MoneyOut, ctx!.Direction.Direction);
        Assert.Equal("company_profile_tax_code", ctx.Direction.Source);
        Assert.Equal("outstanding_compatible", ctx.Suggestion!.Method);

        var posted = await Settlement().PostAsync(_tenantId, _departmentId, p1, _adminId, Approve(), default);
        Assert.Equal(55_000_000m, posted!.PreviousBalance);
        Assert.Equal(35_000_000m, posted.NewBalance);
        var line = Assert.Single(posted.Journal);
        Assert.Equal(("331", "1121", 20_000_000m), (line.DebitAccount, line.CreditAccount, line.Amount));
        Assert.Equal((20_000_000m, "partial"), await ApInvoiceAsync(inv));

        var payables = await Suppliers().GetPayablesAsync(_tenantId, _departmentId, daiPhat, default);
        Assert.Equal(35_000_000m, payables!.CurrentBalance331);
        Assert.Equal("PARTIAL", payables.OpenInvoices.Single().SettlementStatus);
        var summary = await Suppliers().GetApSummaryAsync(_tenantId, _departmentId, default);
        Assert.Equal(summaryBefore.TotalPayable - 20_000_000m, summary.TotalPayable);
        Assert.Equal(summaryBefore.Current - 20_000_000m, summary.Current);

        Assert.Equal("already_posted", (await Settlement().PostAsync(_tenantId, _departmentId, p1, _adminId, Approve(), default))!.Outcome);
        Assert.Equal((20_000_000m, "partial"), await ApInvoiceAsync(inv));

        var p2 = await PaymentVoucherAsync("PC-002", 35_000_000m, "Công ty TNHH Cung ứng Đại Phát", "0200000001", "TT HD DEMO-PINV-DP-003");
        var full = await Settlement().PostAsync(_tenantId, _departmentId, p2, _adminId, Approve(), default);
        Assert.Equal(0m, full!.NewBalance);
        Assert.Equal((55_000_000m, "paid"), await ApInvoiceAsync(inv));
        Assert.Equal(summaryBefore.TotalPayable - 55_000_000m, (await Suppliers().GetApSummaryAsync(_tenantId, _departmentId, default)).TotalPayable);
    }

    [Fact]
    public async Task Payment_Unmatched_IsHeldUnapplied_AndNeverReducesArbitraryAp()
    {
        if (_cs is null) return;
        var s = await SeedSupplierAsync("SUP-NOINV", "Công ty No Invoice", "0200000009");
        var bystander = await SeedSupplierAsync("SUP-BY", "Công ty Bystander NCC", "0200000010");
        var byInv = await BookApInvoiceAsync(bystander, "PINV-BY", 20_000_000m);
        var p = await PaymentVoucherAsync("PC-NOINV", 20_000_000m, "Công ty No Invoice", "0200000009", "PINV-BY");

        var posted = await Settlement().PostAsync(_tenantId, _departmentId, p, _adminId, Approve(), default);
        Assert.Empty(posted!.Applications);
        Assert.Equal(20_000_000m, posted.Posting!.UnappliedAmount);
        Assert.Equal((0m, "open"), await ApInvoiceAsync(byInv));
        var payables = await Suppliers().GetPayablesAsync(_tenantId, _departmentId, s, default);
        Assert.Equal(20_000_000m, payables!.UnappliedAmount);
    }

    [Fact]
    public async Task Payment_Unapply_RestoresAp()
    {
        if (_cs is null) return;
        var s = await SeedSupplierAsync("SUP-UN", "Công ty Unapply NCC", "0200000011");
        var inv = await BookApInvoiceAsync(s, "PINV-UN", 55_000_000m);
        var p = await PaymentVoucherAsync("PC-UN", 20_000_000m, "Công ty Unapply NCC", "0200000011");
        await Settlement().PostAsync(_tenantId, _departmentId, p, _adminId, Approve(), default);
        var un = await Settlement().UnapplyAsync(_tenantId, _departmentId, p, _adminId, null, null, default);
        Assert.Equal(55_000_000m, un!.NewBalance);
        Assert.Equal((0m, "open"), await ApInvoiceAsync(inv));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM sim_ap_ledger WHERE allocated_invoice_id=@inv AND document_type='payment_reversal' AND credit=20000000", new { inv }));
    }

    // ------------------------------------------------------------------
    // Scope
    // ------------------------------------------------------------------

    [Fact]
    public async Task CrossDepartment_And_CrossTenant_AreNotFound_AndChangeNothing()
    {
        if (_cs is null) return;
        var c = await SeedCustomerAsync("CUS-SCOPE", "Công ty Scope", "0300000030");
        var inv = await BookArInvoiceAsync(c, "HD-SCOPE", 10_000_000m);
        var r = await ReceiptVoucherAsync("PT-SCOPE", 5_000_000m, "Công ty Scope", "0300000030", "HD-SCOPE");
        var repo = Settlement();

        Assert.Null(await repo.GetContextAsync(_tenantId, _otherDepartmentId, r, default));
        Assert.Null(await repo.PostAsync(_tenantId, _otherDepartmentId, r, _adminId, Approve(), default));
        Assert.Null(await repo.GetContextAsync(_otherTenantId, _otherTenantDepartmentId, r, default));
        Assert.Null(await repo.PostAsync(_otherTenantId, _otherTenantDepartmentId, r, _adminId, Approve(), default));
        Assert.Null(await repo.ReverseAsync(_otherTenantId, _otherTenantDepartmentId, r, _adminId, null, default));
        Assert.Equal((0m, "open"), await ArInvoiceAsync(inv));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM cash_transactions WHERE voucher_id=@r", new { r }));

        // A counterparty from another department cannot be forced in either.
        var foreignCustomer = await SeedCustomerAsync("CUS-OTHER-DEPT", "Công ty Khác", "0300000031", _otherDepartmentId);
        var ex = await Assert.ThrowsAsync<CashSettlementException>(() => repo.PostAsync(_tenantId, _departmentId, r, _adminId,
            new PostCashRequest(foreignCustomer, null, true, null), default));
        Assert.Equal("counterparty_not_in_scope", ex.Code);
    }

    [Fact]
    public async Task VoucherCreatedByUser_IsPostedByDepartmentAdmin_NoSecondApproverNeeded()
    {
        if (_cs is null) return;
        var c = await SeedCustomerAsync("CUS-USR", "Công ty User", "0300000032");
        var inv = await BookArInvoiceAsync(c, "HD-USR", 10_000_000m);
        var r = await ReceiptVoucherAsync("PT-USR", 10_000_000m, "Công ty User", "0300000032", "HD-USR", createdBy: _userId);
        var posted = await Settlement().PostAsync(_tenantId, _departmentId, r, _adminId, Approve(), default);
        Assert.Equal(_adminId, posted!.Posting!.PostedBy);
        Assert.Equal((10_000_000m, "paid"), await ArInvoiceAsync(inv));
    }

    // ------------------------------------------------------------------
    // UI-consistency regressions (PT-ST-VT-2026-001)
    // ------------------------------------------------------------------

    /// <summary>The real extractor output: payer under "payer", our company only in the OCR letterhead.</summary>
    private async Task<Guid> RealShapeReceiptAsync(string doc, decimal amount, string reference)
    {
        var fields = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["payer"] = "CÔNG TY TNHH VIỆT TÍN", ["payer_mst"] = "0307890123", ["amount"] = amount,
            ["content"] = $"Thu một phần công nợ theo hóa đơn {reference}", ["related_invoice_number"] = reference,
            ["recipient_name"] = "CÔNG TY TNHH SÂU TRÚC",
        });
        var ocr = $"CÔNG TY TNHH SÂU TRÚC\nMST: {MyTaxCode} - Địa chỉ: Lạc An\nPHIẾU THU CÔNG NỢ\nĐơn vị nộp tiền CÔNG TY TNHH VIỆT TÍN\nMã số thuế 0307890123\nSố hóa đơn {reference}";
        return await Scalar<Guid>("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, document_number, document_date,
                buyer_mst, total_amount, payment_method, extracted_fields, raw_ocr_text)
            VALUES (@u, @t, @d, 'receipt', @doc, @date, '0307890123', @amount, 'CK', @fields::jsonb, @ocr) RETURNING id
            """, new { u = _adminId, t = _tenantId, d = _departmentId, doc, date = Today, amount, fields, ocr });
    }

    [Fact]
    public async Task RealReceipt_CompanyFoundByOcrTaxCode_PartiesUseCompanyProfile()
    {
        if (_cs is null) return;
        await SeedCustomerAsync("CUS-VT2", "Công ty TNHH Việt Tín", "0307890123");
        var r = await RealShapeReceiptAsync("PT-REAL-1", 20_000_000m, "HD-REAL-1");
        var ctx = await Settlement().GetContextAsync(_tenantId, _departmentId, r, default);
        Assert.Equal(CashDirections.MoneyIn, ctx!.Direction.Direction);
        Assert.Equal("company_profile_tax_code_document", ctx.Direction.Source);
        Assert.Equal("Công ty TNHH Việt Tín", ctx.Parties.Payer.Name);
        Assert.Equal(("Công ty TNHH Sáu Trực", MyTaxCode), (ctx.Parties.Payee.Name, ctx.Parties.Payee.TaxCode));
        Assert.Equal(MyTaxCode, ctx.Parties.CompanyTaxCode);
    }

    [Fact]
    public async Task ReferenceMismatch_AdminSelectedOtherInvoice_IsFlaggedAndAudited()
    {
        if (_cs is null) return;
        var c = await SeedCustomerAsync("CUS-VT3", "Công ty TNHH Việt Tín", "0307890123");
        var referenced = await BookArInvoiceAsync(c, "DEMO-ST-VT-001", 39_600_000m);
        var other = await BookArInvoiceAsync(c, "DEMO-INV-VT-001", 85_000_000m, dueInDays: -20);
        // The referenced invoice is already fully paid.
        var payoff = await ReceiptVoucherAsync("PT-PAYOFF", 39_600_000m, "Công ty TNHH Việt Tín", "0307890123", "DEMO-ST-VT-001");
        await Settlement().PostAsync(_tenantId, _departmentId, payoff, _adminId, Approve(), default);
        Assert.Equal((39_600_000m, "paid"), await ArInvoiceAsync(referenced));

        var r = await RealShapeReceiptAsync("PT-ST-VT-2026-001", 20_000_000m, "DEMO-ST-VT-001");
        var before = await Settlement().GetContextAsync(_tenantId, _departmentId, r, default);
        Assert.Equal("not_applied", before!.Reference.Status);
        var refInv = Assert.Single(before.Reference.ReferencedInvoices);
        Assert.Equal(("DEMO-ST-VT-001", "PAID"), (refInv.DocumentNumber, refInv.SettlementStatus));
        // A PAID referenced invoice is never suggested as a target.
        Assert.DoesNotContain(before.Suggestion!.Allocations, a => a.InvoiceId == referenced);

        var posted = await Settlement().PostAsync(_tenantId, _departmentId, r, _adminId,
            new PostCashRequest(null, new[] { new AllocationInput(other, 20_000_000m) }, false, null), default);
        Assert.Equal("admin_selected", Assert.Single(posted!.Applications).MatchMethod);
        Assert.Equal("DEMO-ST-VT-001", posted.Posting!.DocumentReference);

        var after = await Settlement().GetContextAsync(_tenantId, _departmentId, r, default);
        Assert.Equal("mismatch", after!.Reference.Status);
        Assert.Equal(new[] { "DEMO-INV-VT-001" }, after.Reference.AppliedInvoices);
        Assert.Equal(new[] { "DEMO-ST-VT-001" }, after.Reference.DocumentReferences);
        Assert.Equal((20_000_000m, "partial"), await ArInvoiceAsync(other));
    }

    [Fact]
    public async Task ReferenceMatched_WhenAppliedToTheReferencedInvoice()
    {
        if (_cs is null) return;
        var c = await SeedCustomerAsync("CUS-VT4", "Công ty TNHH Việt Tín", "0307890123");
        await BookArInvoiceAsync(c, "HD-MATCH-1", 30_000_000m);
        await BookArInvoiceAsync(c, "HD-OTHER-1", 30_000_000m);
        var r = await RealShapeReceiptAsync("PT-MATCH", 10_000_000m, "HD-MATCH-1");
        var posted = await Settlement().PostAsync(_tenantId, _departmentId, r, _adminId, Approve(), default);
        Assert.Equal("invoice_reference", Assert.Single(posted!.Applications).MatchMethod);
        Assert.Equal("matched", (await Settlement().GetContextAsync(_tenantId, _departmentId, r, default))!.Reference.Status);
    }

    [Fact]
    public async Task Posting_ClosesPendingLuong02Match_ReverseReopensIt()
    {
        if (_cs is null) return;
        var c = await SeedCustomerAsync("CUS-L02", "Công ty L02", "0300000050");
        await BookArInvoiceAsync(c, "HD-L02", 50_000_000m);
        var r = await ReceiptVoucherAsync("PT-L02", 20_000_000m, "Công ty L02", "0300000050", "HD-L02");
        await Scalar<int>("INSERT INTO receipt_match_results(tenant_id, department_id, voucher_id) VALUES (@t,@d,@r) RETURNING 1",
            new { t = _tenantId, d = _departmentId, r });

        await Settlement().PostAsync(_tenantId, _departmentId, r, _adminId, Approve(), default);
        Assert.Equal("confirmed", await Scalar<string>("SELECT review_status FROM receipt_match_results WHERE voucher_id=@r", new { r }));
        Assert.Equal(_adminId, await Scalar<Guid>("SELECT reviewed_by FROM receipt_match_results WHERE voucher_id=@r", new { r }));

        // Unapply keeps the posting -> still not pending.
        await Settlement().UnapplyAsync(_tenantId, _departmentId, r, _adminId, null, null, default);
        Assert.Equal("confirmed", await Scalar<string>("SELECT review_status FROM receipt_match_results WHERE voucher_id=@r", new { r }));

        // Void -> awaiting approval again.
        await Settlement().ReverseAsync(_tenantId, _departmentId, r, _adminId, null, default);
        Assert.Equal("proposed", await Scalar<string>("SELECT review_status FROM receipt_match_results WHERE voucher_id=@r", new { r }));
    }

    [Fact]
    public async Task Balances_AfterPostUnapplyReapply_AreLiveAndNeverDoubleCounted()
    {
        if (_cs is null) return;
        var c = await SeedCustomerAsync("CUS-BAL", "Công ty Bal", "0300000051");
        var inv = await BookArInvoiceAsync(c, "HD-BAL", 166_600_000m);
        var r = await ReceiptVoucherAsync("PT-BAL", 20_000_000m, "Công ty Bal", "0300000051", "HD-BAL");
        await Settlement().PostAsync(_tenantId, _departmentId, r, _adminId, Approve(), default);
        async Task<(decimal Report, decimal Context)> Balances()
        {
            var report = await Customers().GetReceivablesAsync(_tenantId, _departmentId, c, default);
            var ctx = await Settlement().GetContextAsync(_tenantId, _departmentId, r, default);
            return (report!.CurrentBalance131, ctx!.Counterparty!.CurrentBalance);
        }
        Assert.Equal((146_600_000m, 146_600_000m), await Balances());
        await Settlement().UnapplyAsync(_tenantId, _departmentId, r, _adminId, null, null, default);
        Assert.Equal((166_600_000m, 166_600_000m), await Balances());
        await Settlement().AllocateAsync(_tenantId, _departmentId, r, _adminId, new[] { new AllocationInput(inv, 20_000_000m) }, default);
        await Settlement().AllocateAsync(_tenantId, _departmentId, r, _adminId, new[] { new AllocationInput(inv, 20_000_000m) }, default);
        Assert.Equal((146_600_000m, 146_600_000m), await Balances());
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM voucher_journal_suggestions WHERE voucher_id=@r AND source='posting'", new { r }));
    }

    [Fact]
    public async Task ApBalance_AfterPaymentAndUnapply_IsLive()
    {
        if (_cs is null) return;
        var s = await SeedSupplierAsync("SUP-BAL", "Công ty NCC Bal", "0200000050");
        await BookApInvoiceAsync(s, "PINV-BAL", 72_500_000m);
        var p = await PaymentVoucherAsync("PC-BAL", 5_500_000m, "Công ty NCC Bal", "0200000050", "PINV-BAL");
        await Settlement().PostAsync(_tenantId, _departmentId, p, _adminId, Approve(), default);
        Assert.Equal(67_000_000m, (await Suppliers().GetPayablesAsync(_tenantId, _departmentId, s, default))!.CurrentBalance331);
        Assert.Equal(67_000_000m, (await Settlement().GetContextAsync(_tenantId, _departmentId, p, default))!.Counterparty!.CurrentBalance);
        await Settlement().UnapplyAsync(_tenantId, _departmentId, p, _adminId, null, null, default);
        Assert.Equal(72_500_000m, (await Suppliers().GetPayablesAsync(_tenantId, _departmentId, s, default))!.CurrentBalance331);
    }

    // ------------------------------------------------------------------
    // Voucher type auto-detect / override gate
    // ------------------------------------------------------------------

    [Fact]
    public async Task WrongUploadType_StoredAsDetectedPayment_SettlementRenders_ButWaitsForConfirmation()
    {
        if (_cs is null) return;
        var s = await SeedSupplierAsync("SUP-KL-T", "Công ty CP Vật tư Kim Long", "0200000002");
        var inv = await BookApInvoiceAsync(s, "TEST-PINV-2001", 16_500_000m);
        var ocr = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vouchers", "Fixtures", "04_Phieu_chi.txt"));
        var company = new CompanyIdentity(MyTaxCode, new[] { "Công ty TNHH Sáu Trực" });
        // Uploaded as "Hóa đơn bán", extractor + title say payment.
        var classification = Aiq.Gateway.Vouchers.VoucherTypeClassifier.Combine(
            Aiq.Gateway.Vouchers.VoucherTypeRequest.Parse("invoice_vat_out"),
            Aiq.Gateway.Vouchers.VoucherTypeClassifier.Detect(ocr, company), "payment", null, null, MyTaxCode);
        Assert.Equal("payment", classification.VoucherType);
        var fields = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["payee_name"] = "CÔNG TY CP VẬT TƯ KIM LONG", ["payee_mst"] = "0200000002", ["amount"] = 5_500_000m,
            ["reference_document"] = "TEST-PINV-2001",
            ["voucher_type_classification"] = classification,
        });
        var v = await Scalar<Guid>("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, document_number, document_date,
                total_amount, payment_method, extracted_fields, raw_ocr_text)
            VALUES (@u, @t, @d, @type, 'PC-TEST-4001', @date, 5500000, 'CK', @fields::jsonb, @ocr) RETURNING id
            """, new { u = _adminId, t = _tenantId, d = _departmentId, type = classification.VoucherType, date = Today, fields, ocr });

        var ctx = await Settlement().GetContextAsync(_tenantId, _departmentId, v, default);
        Assert.NotNull(ctx);                                       // panel renders (not 404)
        Assert.Contains("voucher_type_unconfirmed", ctx!.Blockers);
        var ex = await Assert.ThrowsAsync<CashSettlementException>(() => Settlement().PostAsync(_tenantId, _departmentId, v, _adminId, Approve(), default));
        Assert.Equal("voucher_type_unconfirmed", ex.Code);
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM cash_transactions WHERE voucher_id=@v", new { v }));

        // ADMIN confirms "Dùng loại AIQ nhận diện".
        var vouchers = new Aiq.Gateway.Vouchers.VoucherRepository(Factory());
        var confirmed = classification with { ConfirmedBy = _adminId, ConfirmedAt = DateTime.UtcNow, RequiresConfirmation = false, Status = "ok" };
        Assert.True(await vouchers.SetVoucherTypeClassificationAsync(_tenantId, _departmentId, v, "payment",
            System.Text.Json.JsonSerializer.Serialize(confirmed, Aiq.Gateway.Vouchers.VoucherTypeClassification.Json), default));
        var posted = await Settlement().PostAsync(_tenantId, _departmentId, v, _adminId, Approve(), default);
        Assert.Equal(("331", "1121", 5_500_000m), (posted!.Journal[0].DebitAccount, posted.Journal[0].CreditAccount, posted.Journal[0].Amount));
        Assert.Equal((5_500_000m, "partial"), await ApInvoiceAsync(inv));

        // Once posted the type can no longer be rewritten; cross-department never.
        Assert.False(await vouchers.SetVoucherTypeClassificationAsync(_tenantId, _departmentId, v, "receipt", "{}", default));
        Assert.Equal("payment", await Scalar<string>("SELECT voucher_type FROM vouchers WHERE id=@v", new { v }));
    }

    [Fact]
    public async Task AdminCorrection_IsScopedToOwnDepartmentAndTenant()
    {
        if (_cs is null) return;
        var r = await ReceiptVoucherAsync("PT-SCOPE-T", 1_000_000m, "X", "0300000060");
        var vouchers = new Aiq.Gateway.Vouchers.VoucherRepository(Factory());
        Assert.False(await vouchers.SetVoucherTypeClassificationAsync(_tenantId, _otherDepartmentId, r, "payment", "{}", default));
        Assert.False(await vouchers.SetVoucherTypeClassificationAsync(_otherTenantId, _otherTenantDepartmentId, r, "payment", "{}", default));
        Assert.Equal("receipt", await Scalar<string>("SELECT voucher_type FROM vouchers WHERE id=@r", new { r }));
    }

    // ------------------------------------------------------------------
    // Legacy backfill
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("awaiting_review")]
    [InlineData("rejected")]
    [InlineData("review_task_creation_failed")]
    public async Task DocumentReviewGate_BlocksSettlementPosting_UntilApproved(string reviewStatus)
    {
        if (_cs is null) return;
        var s = await SeedSupplierAsync($"SUP-RV-{reviewStatus[..4]}", "Công ty CP Vật tư Kim Long", "0200000002");
        await BookApInvoiceAsync(s, $"TEST-PINV-RV-{reviewStatus[..4]}", 16_500_000m);
        var fields = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["payee_name"] = "CÔNG TY CP VẬT TƯ KIM LONG", ["payee_mst"] = "0200000002", ["amount"] = 5_500_000m,
        });
        var v = await Scalar<Guid>("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, document_number, document_date,
                total_amount, payment_method, extracted_fields, document_review_status)
            VALUES (@u, @t, @d, 'payment', @doc, @date, 5500000, 'CK', @fields::jsonb, @reviewStatus) RETURNING id
            """, new { u = _adminId, t = _tenantId, d = _departmentId, doc = $"PC-RV-{Guid.NewGuid():N}"[..14], date = Today, fields, reviewStatus });

        var ctx = await Settlement().GetContextAsync(_tenantId, _departmentId, v, default);
        Assert.Contains("document_review_required:" + ExpectedGateReason(reviewStatus), ctx!.Blockers);
        var ex = await Assert.ThrowsAsync<CashSettlementException>(() => Settlement().PostAsync(_tenantId, _departmentId, v, _adminId, Approve(), default));
        Assert.Equal("document_review_required", ex.Code);
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM cash_transactions WHERE voucher_id=@v", new { v }));

        await Scalar<int>("UPDATE vouchers SET document_review_status='approved' WHERE id=@v RETURNING 1", new { v });
        ctx = await Settlement().GetContextAsync(_tenantId, _departmentId, v, default);
        Assert.DoesNotContain(ctx!.Blockers, b => b.StartsWith("document_review_required", StringComparison.Ordinal));
    }

    private static string ExpectedGateReason(string reviewStatus) =>
        reviewStatus == "review_task_creation_failed" ? reviewStatus : "document_review:" + reviewStatus;

    [Fact]
    public async Task Migration051_BackfillsLegacyAppliedReceipt_WhichCanThenBeUnapplied()
    {
        if (_cs is null) return;
        var c = await SeedCustomerAsync("CUS-LEG", "Công ty Legacy", "0300000040");
        var inv = await BookArInvoiceAsync(c, "HD-LEG", 60_000_000m);
        var r = await ReceiptVoucherAsync("PT-LEG", 30_000_000m, "Công ty Legacy", "0300000040");
        // Old path (ApplyReceiptAsync) — no cash_transactions row.
        await Customers().ApplyReceiptAsync(_tenantId, c, r, "PT-LEG", Today,
            new[] { new ReceiptApplicationInput("HD-LEG", 30_000_000m) }, 0m, null, default);
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM cash_transactions WHERE voucher_id=@r", new { r }));

        await using (var db = new NpgsqlConnection(_cs))
        {
            await db.OpenAsync();
            await db.ExecuteAsync(await ReadMigrationAsync("051_cash_settlement.sql")); // re-runnable
            await db.ExecuteAsync(await ReadMigrationAsync("051_cash_settlement.sql")); // and idempotent
        }
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM cash_transactions WHERE voucher_id=@r", new { r }));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM payment_applications WHERE ar_invoice_id=@inv AND match_method='legacy'", new { inv }));

        var ctx = await Settlement().GetContextAsync(_tenantId, _departmentId, r, default);
        Assert.NotNull(ctx!.Posting);
        Assert.Equal("already_posted", (await Settlement().PostAsync(_tenantId, _departmentId, r, _adminId, Approve(), default))!.Outcome);

        var un = await Settlement().UnapplyAsync(_tenantId, _departmentId, r, _adminId, null, null, default);
        Assert.Equal(60_000_000m, un!.NewBalance);
        Assert.Equal((0m, "open"), await ArInvoiceAsync(inv));
    }
}
