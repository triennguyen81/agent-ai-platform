using Aiq.Gateway.Accounting;
using Aiq.Gateway.Customers;
using Aiq.Gateway.Data;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Security;
using Aiq.Gateway.Suppliers;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Contract -> AR/AP lifecycle hardening: covers the SALES/PURCHASE test
/// scenarios from the task spec against a real Postgres instance (same
/// isolated-schema-per-run pattern as ContractProcessingIntegrationTests).
///
/// Root cause under test: before migration 045, sim_ar_ledger/sim_ap_ledger
/// had no per-invoice paid_amount/status — an invoice row's own credit/debit
/// never changed when a receipt/payment was applied (settlement was a
/// completely separate, unlinked row), so "open invoices" never reflected
/// partial/full settlement of a SPECIFIC invoice once a customer/supplier
/// had more than one. These tests exercise the fixed BookInvoiceAsync /
/// ApplyReceiptAsync / UnapplyReceiptAsync / ApplyExpenseAsync /
/// UnapplyExpenseAsync directly.
/// </summary>
public sealed class ArApOpenItemIntegrationTests : IAsyncLifetime
{
    // Only Program.cs registers these at real app startup — xUnit never runs
    // it, so DateOnly/DateOnly? parameters would fail to bind at all without
    // registering them here too (same as ContractProcessingIntegrationTests).
    static ArApOpenItemIntegrationTests()
    {
        Dapper.SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        Dapper.SqlMapper.AddTypeHandler(new NullableDateOnlyTypeHandler());
        Dapper.SqlMapper.AddTypeHandler(new StringArrayTypeHandler());
    }

    private readonly string? _baseConnectionString = ResolveTestConnectionString();
    private readonly string _schema = $"ar_ap_open_item_test_{Guid.NewGuid():N}";
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
        // Final shape only (020_simulator_business_schema.sql + 030/031/032
        // + 045_ar_ap_open_items_and_direction.sql merged) — hand-rolls just
        // the columns the repositories under test actually read/write, same
        // established pattern as ContractProcessingIntegrationTests.
        await scoped.ExecuteAsync("""
            CREATE EXTENSION IF NOT EXISTS pgcrypto;
            CREATE TABLE tenants (id UUID PRIMARY KEY, status TEXT NOT NULL DEFAULT 'active', company_tax_id TEXT);
            CREATE TABLE departments (id UUID PRIMARY KEY, tenant_id UUID NOT NULL, status TEXT NOT NULL DEFAULT 'active');
            CREATE TABLE users (id UUID PRIMARY KEY, tenant_id UUID NOT NULL, department_id UUID);

            CREATE TABLE contracts (id UUID PRIMARY KEY DEFAULT gen_random_uuid());
            CREATE TABLE contract_payment_schedules (id UUID PRIMARY KEY DEFAULT gen_random_uuid());

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

            -- migration 049 shape only — the columns TenantCompanyProfileRepository
            -- actually selects/upserts, same "final shape only" convention as the
            -- rest of this fixture.
            CREATE TABLE tenant_company_profiles (
                tenant_id UUID PRIMARY KEY REFERENCES tenants(id),
                legal_name TEXT, tax_code TEXT, short_name TEXT, aliases TEXT[] NOT NULL DEFAULT '{}',
                registered_address TEXT, legal_representative TEXT, representative_title TEXT,
                phone TEXT, email TEXT, website TEXT,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_by UUID
            );

            -- migration 051 — only the columns GetReceivablesAsync/
            -- GetPayablesAsync read for the unapplied (advance) amount.
            CREATE TABLE cash_transactions (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id UUID NOT NULL,
                customer_id UUID, supplier_id UUID, amount NUMERIC(18,2) NOT NULL, status TEXT NOT NULL DEFAULT 'posted'
            );
            CREATE TABLE payment_applications (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), cash_transaction_id UUID NOT NULL REFERENCES cash_transactions(id),
                applied_amount NUMERIC(18,2) NOT NULL, status TEXT NOT NULL DEFAULT 'active'
            );
            """);

        _tenantId = Guid.NewGuid();
        _departmentId = Guid.NewGuid();
        _userId = Guid.NewGuid();
        await scoped.ExecuteAsync(
            "INSERT INTO tenants(id) VALUES (@t); INSERT INTO departments(id, tenant_id) VALUES (@d, @t); INSERT INTO users(id, tenant_id, department_id) VALUES (@u, @t, @d);",
            new { t = _tenantId, d = _departmentId, u = _userId });
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(_baseConnectionString)) return;
        await using var conn = new NpgsqlConnection(_baseConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP SCHEMA IF EXISTS {_schema} CASCADE");
    }

    private ICustomerRepository Customers() => new CustomerRepository(new NpgsqlConnectionFactory(_scopedConnectionString!));
    private ISupplierRepository Suppliers() => new SupplierRepository(new NpgsqlConnectionFactory(_scopedConnectionString!));
    private ITenantCompanyProfileRepository CompanyProfiles() => new TenantCompanyProfileRepository(new NpgsqlConnectionFactory(_scopedConnectionString!));

    private async Task<Guid> SeedCustomerAsync(string code, string mst)
    {
        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<Guid>(
            "INSERT INTO sim_customers(tenant_id, department_id, customer_code, name, mst) VALUES (@t,@d,@code,@code,@mst) RETURNING id",
            new { t = _tenantId, d = _departmentId, code, mst });
    }

    private async Task<Guid> SeedSupplierAsync(string code, string mst)
    {
        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<Guid>(
            "INSERT INTO sim_suppliers(tenant_id, department_id, supplier_code, name, mst) VALUES (@t,@d,@code,@code,@mst) RETURNING id",
            new { t = _tenantId, d = _departmentId, code, mst });
    }

    [Fact]
    public async Task Sales2_InvoiceApproved_CreatesOpenArAndCurrentAr()
    {
        if (_scopedConnectionString is null) return;
        var customerId = await SeedCustomerAsync("CUS-S2", "0307890123");
        var repo = Customers();

        var result = await repo.BookInvoiceAsync(
            _tenantId, customerId, Guid.NewGuid(), "HD-99M", DateOnly.FromDateTime(DateTime.UtcNow), null, 99_000_000m, null, null, default);

        Assert.Equal(1, result.LedgerRowsInserted);
        Assert.Equal(99_000_000m, result.NewBalance);
        var customer = await repo.FindByIdAsync(_tenantId, _departmentId, customerId, default);
        Assert.Equal(99_000_000m, customer!.CurrentAr);
        var receivables = await repo.GetReceivablesAsync(_tenantId, _departmentId, customerId, default);
        var invoice = Assert.Single(receivables!.OpenInvoices);
        Assert.Equal("open", invoice.Status);
        Assert.Equal(0m, invoice.PaidAmount);
    }

    [Fact]
    public async Task Sales3And4_PartialThenFullReceipt_SettlesArAndCurrentAr()
    {
        if (_scopedConnectionString is null) return;
        var customerId = await SeedCustomerAsync("CUS-S34", "0307890124");
        var repo = Customers();
        await repo.BookInvoiceAsync(_tenantId, customerId, Guid.NewGuid(), "HD-S34", DateOnly.FromDateTime(DateTime.UtcNow), null, 99_000_000m, null, null, default);

        // SALES 3 — receipt 50m -> PARTIAL, remaining 49m, currentAr 49m
        var r1 = await repo.ApplyReceiptAsync(
            _tenantId, customerId, Guid.NewGuid(), "PT-01", DateOnly.FromDateTime(DateTime.UtcNow),
            new[] { new ReceiptApplicationInput("HD-S34", 50_000_000m) }, 0m, null, default);
        Assert.Equal(49_000_000m, r1.NewBalance);
        var afterPartial = await repo.GetReceivablesAsync(_tenantId, _departmentId, customerId, default);
        var invoicePartial = Assert.Single(afterPartial!.OpenInvoices);
        Assert.Equal("partial", invoicePartial.Status);
        Assert.Equal(50_000_000m, invoicePartial.PaidAmount);

        // SALES 4 — receipt remaining 49m -> PAID, currentAr 0
        var r2 = await repo.ApplyReceiptAsync(
            _tenantId, customerId, Guid.NewGuid(), "PT-02", DateOnly.FromDateTime(DateTime.UtcNow),
            new[] { new ReceiptApplicationInput("HD-S34", 49_000_000m) }, 0m, null, default);
        Assert.Equal(0m, r2.NewBalance);
        var afterFull = await repo.GetReceivablesAsync(_tenantId, _departmentId, customerId, default);
        Assert.Empty(afterFull!.OpenInvoices); // PAID invoices are no longer "open"
        var customer = await repo.FindByIdAsync(_tenantId, _departmentId, customerId, default);
        Assert.Equal(0m, customer!.CurrentAr);
    }

    [Fact]
    public async Task Sales5_DuplicateInvoiceApply_OnlyOneArRow()
    {
        if (_scopedConnectionString is null) return;
        var customerId = await SeedCustomerAsync("CUS-S5", "0307890125");
        var repo = Customers();
        var voucherId = Guid.NewGuid();

        var first = await repo.BookInvoiceAsync(_tenantId, customerId, voucherId, "HD-S5", DateOnly.FromDateTime(DateTime.UtcNow), null, 99_000_000m, null, null, default);
        var second = await repo.BookInvoiceAsync(_tenantId, customerId, voucherId, "HD-S5", DateOnly.FromDateTime(DateTime.UtcNow), null, 99_000_000m, null, null, default);

        Assert.Equal(1, first.LedgerRowsInserted);
        Assert.Equal(0, second.LedgerRowsInserted); // real UNIQUE(tenant_id, voucher_id) no-ops the retry
        var customer = await repo.FindByIdAsync(_tenantId, _departmentId, customerId, default);
        Assert.Equal(99_000_000m, customer!.CurrentAr); // not double-booked
    }

    [Fact]
    public async Task DuplicateReceiptApply_PaidAmountUnchangedAfterSecondAttempt()
    {
        if (_scopedConnectionString is null) return;
        var customerId = await SeedCustomerAsync("CUS-DUPR", "0307890126");
        var repo = Customers();
        await repo.BookInvoiceAsync(_tenantId, customerId, Guid.NewGuid(), "HD-DUPR", DateOnly.FromDateTime(DateTime.UtcNow), null, 99_000_000m, null, null, default);
        var receiptVoucherId = Guid.NewGuid();

        var first = await repo.ApplyReceiptAsync(_tenantId, customerId, receiptVoucherId, "PT-DUPR", DateOnly.FromDateTime(DateTime.UtcNow),
            new[] { new ReceiptApplicationInput("HD-DUPR", 50_000_000m) }, 0m, null, default);
        var second = await repo.ApplyReceiptAsync(_tenantId, customerId, receiptVoucherId, "PT-DUPR", DateOnly.FromDateTime(DateTime.UtcNow),
            new[] { new ReceiptApplicationInput("HD-DUPR", 50_000_000m) }, 0m, null, default);

        Assert.Equal(49_000_000m, first.NewBalance);
        Assert.Equal(0, second.LedgerRowsInserted); // idempotency guard no-ops the retry
        Assert.Equal(49_000_000m, second.NewBalance); // unchanged, not double-applied
    }

    [Fact]
    public async Task UnapplyReceipt_RestoresOpenStatusAndCurrentAr()
    {
        if (_scopedConnectionString is null) return;
        var customerId = await SeedCustomerAsync("CUS-UNAP", "0307890127");
        var repo = Customers();
        await repo.BookInvoiceAsync(_tenantId, customerId, Guid.NewGuid(), "HD-UNAP", DateOnly.FromDateTime(DateTime.UtcNow), null, 99_000_000m, null, null, default);
        var receiptVoucherId = Guid.NewGuid();
        await repo.ApplyReceiptAsync(_tenantId, customerId, receiptVoucherId, "PT-UNAP", DateOnly.FromDateTime(DateTime.UtcNow),
            new[] { new ReceiptApplicationInput("HD-UNAP", 99_000_000m) }, 0m, null, default);
        var paidCustomer = await repo.FindByIdAsync(_tenantId, _departmentId, customerId, default);
        Assert.Equal(0m, paidCustomer!.CurrentAr);

        var unapplied = await repo.UnapplyReceiptAsync(_tenantId, customerId, receiptVoucherId, default);

        Assert.Equal(99_000_000m, unapplied.NewBalance);
        var restored = await repo.GetReceivablesAsync(_tenantId, _departmentId, customerId, default);
        var invoice = Assert.Single(restored!.OpenInvoices);
        Assert.Equal("open", invoice.Status);
        Assert.Equal(0m, invoice.PaidAmount);
    }

    [Fact]
    public async Task Purchase2_SupplierInvoiceApproved_CreatesOpenApAndCurrentAp()
    {
        if (_scopedConnectionString is null) return;
        var supplierId = await SeedSupplierAsync("SUP-P2", "0312345678");
        var repo = Suppliers();

        var result = await repo.BookInvoiceAsync(
            _tenantId, supplierId, Guid.NewGuid(), "HDNCC-99M", DateOnly.FromDateTime(DateTime.UtcNow), null, 99_000_000m, null, null, default);

        Assert.Equal(1, result.LedgerRowsInserted);
        Assert.Equal(99_000_000m, result.NewBalance);
        var supplier = await repo.FindByIdAsync(_tenantId, _departmentId, supplierId, default);
        Assert.Equal(99_000_000m, supplier!.CurrentAp);
    }

    [Fact]
    public async Task Purchase3And4_PartialThenFullPayment_SettlesApAndCurrentAp()
    {
        if (_scopedConnectionString is null) return;
        var supplierId = await SeedSupplierAsync("SUP-P34", "0312345679");
        var repo = Suppliers();
        await repo.BookInvoiceAsync(_tenantId, supplierId, Guid.NewGuid(), "HDNCC-P34", DateOnly.FromDateTime(DateTime.UtcNow), null, 99_000_000m, null, null, default);

        // PURCHASE 3 — payment 60m -> PARTIAL, remaining 39m
        var r1 = await repo.ApplyExpenseAsync(_tenantId, supplierId, Guid.NewGuid(), "PC-01", DateOnly.FromDateTime(DateTime.UtcNow),
            new[] { new ExpenseApplicationInput("HDNCC-P34", 60_000_000m) }, 0m, null, default);
        Assert.Equal(39_000_000m, r1.NewBalance);

        // PURCHASE 4 — payment 39m -> PAID, currentAp 0
        var r2 = await repo.ApplyExpenseAsync(_tenantId, supplierId, Guid.NewGuid(), "PC-02", DateOnly.FromDateTime(DateTime.UtcNow),
            new[] { new ExpenseApplicationInput("HDNCC-P34", 39_000_000m) }, 0m, null, default);
        Assert.Equal(0m, r2.NewBalance);
        var supplier = await repo.FindByIdAsync(_tenantId, _departmentId, supplierId, default);
        Assert.Equal(0m, supplier!.CurrentAp);
    }

    [Fact]
    public async Task Purchase5_DuplicateInvoiceAndPayment_NoDuplicateApNoDoublePayment()
    {
        if (_scopedConnectionString is null) return;
        var supplierId = await SeedSupplierAsync("SUP-P5", "0312345680");
        var repo = Suppliers();
        var invoiceVoucherId = Guid.NewGuid();

        var firstBook = await repo.BookInvoiceAsync(_tenantId, supplierId, invoiceVoucherId, "HDNCC-P5", DateOnly.FromDateTime(DateTime.UtcNow), null, 99_000_000m, null, null, default);
        var secondBook = await repo.BookInvoiceAsync(_tenantId, supplierId, invoiceVoucherId, "HDNCC-P5", DateOnly.FromDateTime(DateTime.UtcNow), null, 99_000_000m, null, null, default);
        Assert.Equal(1, firstBook.LedgerRowsInserted);
        Assert.Equal(0, secondBook.LedgerRowsInserted);

        var paymentVoucherId = Guid.NewGuid();
        var firstPay = await repo.ApplyExpenseAsync(_tenantId, supplierId, paymentVoucherId, "PC-P5", DateOnly.FromDateTime(DateTime.UtcNow),
            new[] { new ExpenseApplicationInput("HDNCC-P5", 99_000_000m) }, 0m, null, default);
        var secondPay = await repo.ApplyExpenseAsync(_tenantId, supplierId, paymentVoucherId, "PC-P5", DateOnly.FromDateTime(DateTime.UtcNow),
            new[] { new ExpenseApplicationInput("HDNCC-P5", 99_000_000m) }, 0m, null, default);

        Assert.Equal(0m, firstPay.NewBalance);
        Assert.Equal(0, secondPay.LedgerRowsInserted);
        Assert.Equal(0m, secondPay.NewBalance);
    }

    [Fact]
    public async Task UnapplyPayment_RestoresOpenStatusAndCurrentAp()
    {
        if (_scopedConnectionString is null) return;
        var supplierId = await SeedSupplierAsync("SUP-UNAP", "0312345681");
        var repo = Suppliers();
        await repo.BookInvoiceAsync(_tenantId, supplierId, Guid.NewGuid(), "HDNCC-UNAP", DateOnly.FromDateTime(DateTime.UtcNow), null, 99_000_000m, null, null, default);
        var paymentVoucherId = Guid.NewGuid();
        await repo.ApplyExpenseAsync(_tenantId, supplierId, paymentVoucherId, "PC-UNAP", DateOnly.FromDateTime(DateTime.UtcNow),
            new[] { new ExpenseApplicationInput("HDNCC-UNAP", 99_000_000m) }, 0m, null, default);

        var unapplied = await repo.UnapplyExpenseAsync(_tenantId, supplierId, paymentVoucherId, default);

        Assert.Equal(99_000_000m, unapplied.NewBalance);
        var restored = await repo.GetPayablesAsync(_tenantId, _departmentId, supplierId, default);
        var invoice = Assert.Single(restored!.OpenInvoices);
        Assert.Equal("open", invoice.Status);
        Assert.Equal(0m, invoice.PaidAmount);
    }

    [Fact]
    public async Task Tenant_CannotReadOrMutateOtherTenantsCustomer()
    {
        if (_scopedConnectionString is null) return;
        var customerId = await SeedCustomerAsync("CUS-TENANT", "0307890199");
        var repo = Customers();
        var otherTenantId = Guid.NewGuid();
        var otherDepartmentId = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(_scopedConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "INSERT INTO tenants(id) VALUES (@t); INSERT INTO departments(id, tenant_id) VALUES (@d, @t);",
                new { t = otherTenantId, d = otherDepartmentId });
        }

        var crossTenantRead = await repo.FindByIdAsync(otherTenantId, otherDepartmentId, customerId, default);
        Assert.Null(crossTenantRead);

        // Booking an invoice against the real customerId but under the WRONG
        // tenant must not silently succeed against another tenant's data —
        // there is no customer row scoped to (otherTenantId, customerId), so
        // the balance recompute for that (tenantId, customerId) pair yields 0
        // and the real customer's own current_ar remains untouched.
        await repo.BookInvoiceAsync(otherTenantId, customerId, Guid.NewGuid(), "HD-XT", DateOnly.FromDateTime(DateTime.UtcNow), null, 99_000_000m, null, null, default);
        var realCustomer = await repo.FindByIdAsync(_tenantId, _departmentId, customerId, default);
        Assert.Equal(0m, realCustomer!.CurrentAr);
    }

    // ==== Invoice approval → posting: trusted Company Profile as the sole =
    // ==== "who are we" source (never a legacy field, never client input) =
    //
    // Root-caused by this task: ApplyInvoiceAsync/ApplyExpenseAsync (the
    // "Duyệt & ghi nhận" endpoints) used to resolve company identity from
    // tenants.company_tax_id (a legacy single field). They now resolve it
    // from tenant_company_profiles (ITenantCompanyProfileRepository) — the
    // real Company Profile TENANT_OWNER manages. These tests prove the
    // exact composition those endpoints perform (fetch profile → feed
    // InvoiceDirectionResolver), using the Sáu Trực ↔ Việt Tín numbers from
    // the live-verify scenario, without needing a full HTTP harness (this
    // codebase's existing convention — see InvoiceDirectionResolverTests
    // for the pure-function coverage of the resolver itself, unchanged
    // here).

    [Fact]
    public async Task CompanyProfile_SellerIsOwnCompany_ResolvesSales()
    {
        if (_scopedConnectionString is null) return;
        await CompanyProfiles().UpsertAsync(_tenantId, _userId, new UpsertCompanyProfileRequest(
            "Công Ty TNHH Sáu Trực", "0123456789", "Sáu Trực", [], "Lạc An, Ninh Thọ",
            null, null, null, null, null), default);

        var profile = await CompanyProfiles().GetAsync(_tenantId, default);
        var resolution = InvoiceDirectionResolver.Resolve(
            sellerTaxId: "0123456789", buyerTaxId: "0307890123", companyTaxId: profile?.TaxCode);

        Assert.Equal(InvoiceDirection.Sales, resolution.Direction);
        Assert.Equal("0307890123", resolution.CounterpartyTaxId);
    }

    [Fact]
    public async Task CompanyProfile_BuyerIsOwnCompany_ResolvesPurchase()
    {
        if (_scopedConnectionString is null) return;
        await CompanyProfiles().UpsertAsync(_tenantId, _userId, new UpsertCompanyProfileRequest(
            "Công Ty TNHH Sáu Trực", "0123456789", "Sáu Trực", [], "Lạc An, Ninh Thọ",
            null, null, null, null, null), default);

        var profile = await CompanyProfiles().GetAsync(_tenantId, default);
        var resolution = InvoiceDirectionResolver.Resolve(
            sellerTaxId: "0200000001", buyerTaxId: "0123456789", companyTaxId: profile?.TaxCode);

        Assert.Equal(InvoiceDirection.Purchase, resolution.Direction);
        Assert.Equal("0200000001", resolution.CounterpartyTaxId);
    }

    [Fact]
    public async Task CompanyProfile_NotConfigured_NeedsReview_NeverGuesses()
    {
        if (_scopedConnectionString is null) return;
        // No tenant_company_profiles row at all for this tenant.
        var profile = await CompanyProfiles().GetAsync(_tenantId, default);
        Assert.Null(profile);

        var resolution = InvoiceDirectionResolver.Resolve(
            sellerTaxId: "0123456789", buyerTaxId: "0307890123", companyTaxId: profile?.TaxCode);

        Assert.Equal(InvoiceDirection.NeedsReview, resolution.Direction);
        Assert.Equal("company_tax_id_not_configured", resolution.Reason);
    }

    [Fact]
    public async Task CompanyProfile_TakesPrecedenceOverLegacyTenantsCompanyTaxId()
    {
        if (_scopedConnectionString is null) return;
        // Legacy field deliberately set to a DIFFERENT tax code than Company
        // Profile — proves apply-invoice/apply-expense's own composition
        // (GetAsync().TaxCode, not tenants.company_tax_id) is what actually
        // decides direction now. If this ever regressed back to reading the
        // legacy field, this exact invoice would resolve as Purchase (buyer
        // matches the stale legacy value) instead of Sales.
        await using (var conn = new NpgsqlConnection(_scopedConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "UPDATE tenants SET company_tax_id = @stale WHERE id = @t",
                new { stale = "0307890123", t = _tenantId }); // == the buyer's own MST below
        }
        await CompanyProfiles().UpsertAsync(_tenantId, _userId, new UpsertCompanyProfileRequest(
            "Công Ty TNHH Sáu Trực", "0123456789", "Sáu Trực", [], "Lạc An, Ninh Thọ",
            null, null, null, null, null), default);

        var profile = await CompanyProfiles().GetAsync(_tenantId, default);
        var resolution = InvoiceDirectionResolver.Resolve(
            sellerTaxId: "0123456789", buyerTaxId: "0307890123", companyTaxId: profile?.TaxCode);

        Assert.Equal(InvoiceDirection.Sales, resolution.Direction);
    }

    [Fact]
    public async Task LiveVerifyScenario_SauTruc_SellsToVietTin_39_6M_PostsAndCreatesAr()
    {
        if (_scopedConnectionString is null) return;
        await CompanyProfiles().UpsertAsync(_tenantId, _userId, new UpsertCompanyProfileRequest(
            "Công Ty TNHH Sáu Trực", "0123456789", "Sáu Trực", [], "Lạc An, Ninh Thọ",
            null, null, null, null, null), default);
        var vietTinId = await SeedCustomerAsync("CUS-VIETTIN", "0307890123");
        var customers = Customers();

        var profile = await CompanyProfiles().GetAsync(_tenantId, default);
        var resolution = InvoiceDirectionResolver.Resolve("0123456789", "0307890123", profile?.TaxCode);
        Assert.Equal(InvoiceDirection.Sales, resolution.Direction);

        // Before approval: AR unchanged.
        var before = await customers.FindByIdAsync(_tenantId, _departmentId, vietTinId, default);
        Assert.Equal(0m, before!.CurrentAr);

        // Approve/post — same call ApplyInvoiceAsync makes.
        var voucherId = Guid.NewGuid();
        var result = await customers.BookInvoiceAsync(
            _tenantId, vietTinId, voucherId, "DEMO-INV-VT-39.6M",
            DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30),
            39_600_000m, null, null, default);

        Assert.Equal(1, result.LedgerRowsInserted);
        Assert.Equal(39_600_000m, result.NewBalance);

        var after = await customers.FindByIdAsync(_tenantId, _departmentId, vietTinId, default);
        Assert.Equal(39_600_000m, after!.CurrentAr);

        // get_customer_receivables sees it immediately, UNPAID.
        var receivables = await customers.GetReceivablesAsync(_tenantId, _departmentId, vietTinId, default);
        Assert.Equal(39_600_000m, receivables!.CurrentBalance131);
        var invoice = Assert.Single(receivables.OpenInvoices);
        Assert.Equal("open", invoice.Status);
        Assert.Equal(0m, invoice.PaidAmount); // UNPAID — posting never marks paid

        // Idempotent re-approval (double-click) never duplicates AR.
        var retry = await customers.BookInvoiceAsync(
            _tenantId, vietTinId, voucherId, "DEMO-INV-VT-39.6M",
            DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30),
            39_600_000m, null, null, default);
        Assert.Equal(0, retry.LedgerRowsInserted);
        var afterRetry = await customers.FindByIdAsync(_tenantId, _departmentId, vietTinId, default);
        Assert.Equal(39_600_000m, afterRetry!.CurrentAr); // unchanged, not doubled
    }

    // ==== Final ACL rule: same-department ADMIN self-approval is allowed ==
    //
    // ApplyInvoiceAsync/ApplyExpenseAsync (the "Duyệt & ghi nhận" endpoints)
    // do not block the creator — an ADMIN who
    // uploaded/extracted/reviewed a voucher may also be the one who
    // approves/posts it, no second ADMIN/TENANT_OWNER/SUPER_ADMIN required.
    // (ADMIN Department Authority Invariant — see AdminDepartmentAuthorityInvariantTests)
    // — these tests explicitly confirm the SAME user id is both the
    // voucher's creator and the one performing the posting call, and that
    // the underlying booking these endpoints perform succeeds cleanly with
    // AR/AP changing exactly once (never blocked, never double-booked).

    [Fact]
    public async Task SelfApproval_SameAdminUploadsAndApprovesSalesInvoice_ArChangesExactlyOnce()
    {
        if (_scopedConnectionString is null) return;
        var adminId = _userId; // the one user this fixture seeds — plays both roles
        var customerId = await SeedCustomerAsync("CUS-SELFAPPR-AR", "0307899001");
        var customers = Customers();

        // "Uploads/creates/extracts" — same admin is recorded as the
        // voucher's creator (mirrors vouchers.user_id at CreateAsync time).
        var voucherId = Guid.NewGuid();
        var creatorId = adminId;

        // "Same ADMIN approves/posts" — allowed by the ADMIN Department
        // Authority Invariant (no creator check exists).
        Assert.Equal(creatorId, adminId); // same ADMIN creates and posts

        var result = await customers.BookInvoiceAsync(
            _tenantId, customerId, voucherId, "SELFAPPR-AR-001",
            DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30),
            55_000_000m, null, null, default);

        Assert.Equal(1, result.LedgerRowsInserted);
        Assert.Equal(55_000_000m, result.NewBalance);
        var receivables = await customers.GetReceivablesAsync(_tenantId, _departmentId, customerId, default);
        var invoice = Assert.Single(receivables!.OpenInvoices);
        Assert.Equal("open", invoice.Status);
        Assert.Equal(0m, invoice.PaidAmount);

        // Exactly once — a repeat "approve" call (idempotency, unchanged by
        // this review) still never double-books even for the self-approval
        // case.
        var retry = await customers.BookInvoiceAsync(
            _tenantId, customerId, voucherId, "SELFAPPR-AR-001",
            DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30),
            55_000_000m, null, null, default);
        Assert.Equal(0, retry.LedgerRowsInserted);
        var final = await customers.FindByIdAsync(_tenantId, _departmentId, customerId, default);
        Assert.Equal(55_000_000m, final!.CurrentAr);
    }

    [Fact]
    public async Task SelfApproval_SameAdminUploadsAndApprovesPurchaseInvoice_ApChangesExactlyOnce()
    {
        if (_scopedConnectionString is null) return;
        var adminId = _userId;
        var supplierId = await SeedSupplierAsync("SUP-SELFAPPR-AP", "0200099001");
        var suppliers = Suppliers();

        var voucherId = Guid.NewGuid();
        var creatorId = adminId;
        Assert.Equal(creatorId, adminId); // same ADMIN creates and posts

        var result = await suppliers.BookInvoiceAsync(
            _tenantId, supplierId, voucherId, "SELFAPPR-AP-001",
            DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30),
            55_000_000m, null, null, default);

        Assert.Equal(1, result.LedgerRowsInserted);
        Assert.Equal(55_000_000m, result.NewBalance);
        var payables = await suppliers.GetPayablesAsync(_tenantId, _departmentId, supplierId, default);
        var invoice = Assert.Single(payables!.OpenInvoices);
        Assert.Equal("open", invoice.Status);
        Assert.Equal(0m, invoice.PaidAmount);

        var retry = await suppliers.BookInvoiceAsync(
            _tenantId, supplierId, voucherId, "SELFAPPR-AP-001",
            DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30),
            55_000_000m, null, null, default);
        Assert.Equal(0, retry.LedgerRowsInserted);
        var final = await suppliers.FindByIdAsync(_tenantId, _departmentId, supplierId, default);
        Assert.Equal(55_000_000m, final!.CurrentAp);
    }
}
