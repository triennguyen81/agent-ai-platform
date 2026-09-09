using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

public sealed class SimulatorSchemaMigrationIntegrationTests : IAsyncLifetime
{
    private readonly string? _baseConnectionString = ResolveTestConnectionString();
    private readonly string _schema = $"simulator_migration_test_{Guid.NewGuid():N}";
    private string? _scopedConnectionString;

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
        if (_baseConnectionString is null) return;
        await using var conn = new NpgsqlConnection(_baseConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"CREATE SCHEMA {_schema}");
        _scopedConnectionString = new NpgsqlConnectionStringBuilder(_baseConnectionString)
        {
            SearchPath = _schema,
        }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (_baseConnectionString is null) return;
        await using var conn = new NpgsqlConnection(_baseConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP SCHEMA IF EXISTS {_schema} CASCADE");
    }

    [Fact]
    public async Task MigrationCreatesGatewayBusinessSchemaAndPreservesExistingSimulatorRows()
    {
        if (_scopedConnectionString is null) return;
        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();

        var tenantId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var purchaseOrderId = Guid.NewGuid();
        await conn.ExecuteAsync("""
            CREATE TABLE tenants (id UUID PRIMARY KEY);
            INSERT INTO tenants(id) VALUES (@tenantId);
            CREATE TABLE sim_suppliers (
                id UUID PRIMARY KEY,
                tenant_id UUID NOT NULL REFERENCES tenants(id),
                supplier_code TEXT NOT NULL,
                name TEXT NOT NULL,
                mst TEXT,
                country TEXT,
                currency TEXT,
                payment_terms TEXT,
                current_ap NUMERIC(18,2),
                extras JSONB,
                created_at TIMESTAMPTZ,
                UNIQUE(tenant_id,supplier_code)
            );
            CREATE TABLE sim_purchase_orders (
                id UUID PRIMARY KEY,
                tenant_id UUID NOT NULL REFERENCES tenants(id),
                po_number TEXT NOT NULL,
                po_date DATE NOT NULL,
                supplier_code TEXT NOT NULL,
                order_type TEXT,
                status TEXT,
                expected_delivery DATE,
                currency TEXT,
                exchange_rate NUMERIC(18,4),
                subtotal_foreign NUMERIC(18,2),
                subtotal_vnd NUMERIC(18,2) NOT NULL,
                import_duty_pct NUMERIC(5,2),
                vat_import_pct NUMERIC(5,2),
                incoterm TEXT,
                payment_terms TEXT,
                goods_received_note TEXT,
                matched_invoice_number TEXT,
                notes TEXT,
                created_at TIMESTAMPTZ,
                UNIQUE(tenant_id,po_number)
            );
            INSERT INTO sim_suppliers(
                id,tenant_id,supplier_code,name,payment_terms,current_ap,extras,created_at)
            VALUES (@supplierId,@tenantId,'SUP-1','Existing Supplier','Net 30',100,'{}',NOW());
            INSERT INTO sim_purchase_orders(
                id,tenant_id,po_number,po_date,supplier_code,subtotal_vnd,created_at)
            VALUES (@purchaseOrderId,@tenantId,'PO-1',CURRENT_DATE,'SUP-1',100,NOW());
            """, new { tenantId, supplierId, purchaseOrderId });

        await conn.ExecuteAsync(await ReadMigrationAsync("020_simulator_business_schema.sql"));

        var requiredTables = new[]
        {
            "sim_customers", "sim_suppliers", "sim_employees", "sim_expense_claims",
            "sim_ar_ledger", "sim_products", "sim_sales_orders", "sim_sales_order_lines",
            "sim_delivery_notes", "sim_contracts", "sim_contract_milestones",
            "sim_bank_accounts", "sim_bank_transactions", "sim_purchase_orders",
            "sim_ap_ledger", "sim_goods_received_notes",
        };
        foreach (var table in requiredTables)
        {
            Assert.True(await conn.ExecuteScalarAsync<bool>(
                "SELECT to_regclass(@table) IS NOT NULL", new { table }));
        }

        Assert.Equal("Existing Supplier", await conn.ExecuteScalarAsync<string>(
            "SELECT name FROM sim_suppliers WHERE id=@supplierId", new { supplierId }));
        Assert.Equal("PO-1", await conn.ExecuteScalarAsync<string>(
            "SELECT po_number FROM sim_purchase_orders WHERE id=@purchaseOrderId", new { purchaseOrderId }));
        Assert.True(await conn.ExecuteScalarAsync<bool>("""
            SELECT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema=current_schema()
                  AND table_name='sim_suppliers'
                  AND column_name='payment_terms_days'
            )
            """));
        Assert.True(await conn.ExecuteScalarAsync<bool>("""
            SELECT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema=current_schema()
                  AND table_name='sim_purchase_orders'
                  AND column_name='total_amount'
            )
            """));

        await conn.ExecuteAsync(await ReadMigrationAsync("020_simulator_business_schema.sql"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM sim_suppliers WHERE id=@supplierId", new { supplierId }));
    }

    private static async Task<string> ReadMigrationAsync(string name)
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "Data", "Migrations", name);
        if (File.Exists(outputPath)) return await File.ReadAllTextAsync(outputPath);
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../src/Aiq.Gateway/Data/Migrations", name));
        return await File.ReadAllTextAsync(sourcePath);
    }
}
