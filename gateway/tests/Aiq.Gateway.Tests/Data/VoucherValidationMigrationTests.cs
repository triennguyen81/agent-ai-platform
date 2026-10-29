using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Migration 068 (chứng từ flow v2 PR2): up → down → up on the real schema.
/// The down script is run by hand in production; here it must drop exactly
/// what 068 created, keep a backup of human-entered data, and let 068 apply
/// again cleanly. Own throwaway database: this class drops tables.
/// </summary>
public sealed class VoucherValidationMigrationTests(TaxVerificationDatabase fx) : IClassFixture<TaxVerificationDatabase>
{
    private const string Version = "068_voucher_validation.sql";

    [Fact]
    public async Task Up_down_up_round_trips_and_backs_up_entered_data()
    {
        if (fx.ConnectionString is null) return;
        await using var db = new NpgsqlConnection(fx.ConnectionString);
        await db.OpenAsync();

        // ---- up (applied by the fixture) ------------------------------------
        Assert.True(await TablesExistAsync(db));
        Assert.Equal(8m, await db.ExecuteScalarAsync<decimal>(
            "SELECT rate FROM vat_rates WHERE code='8' AND effective_from='2025-07-01' AND effective_to='2026-12-31'"));
        await db.ExecuteAsync("INSERT INTO tenant_voucher_settings(tenant_id, confidence_threshold) VALUES (@t, 0.75)", new { t = fx.Tenant });
        await db.ExecuteAsync("INSERT INTO schema_migrations(version, checksum) VALUES (@Version, repeat('a', 64)) ON CONFLICT DO NOTHING", new { Version });

        // ---- down -------------------------------------------------------------
        await db.ExecuteAsync(await File.ReadAllTextAsync(DownScript()));

        Assert.False(await TablesExistAsync(db));
        Assert.False(await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version=@Version)", new { Version }));
        var backup = await db.ExecuteScalarAsync<string>(
            "SELECT tablename FROM pg_tables WHERE tablename LIKE 'tenant_voucher_settings_backup_%'");
        Assert.NotNull(backup);
        Assert.Equal(0.75m, await db.ExecuteScalarAsync<decimal>($"SELECT confidence_threshold FROM {backup} WHERE tenant_id=@t", new { t = fx.Tenant }));
        Assert.True(await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM pg_tables WHERE tablename LIKE 'vat_rates_backup_%')"));
        // Existing vouchers are untouched by the round trip.
        Assert.True(await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM vouchers WHERE id=@id)", new { id = fx.LegacyVoucher }));

        // ---- up again ---------------------------------------------------------
        await fx.RunFilesAsync(TaxVerificationDatabase.MigrationsDir(), f => f == Version);

        Assert.True(await TablesExistAsync(db));
        Assert.Equal(6, await db.ExecuteScalarAsync<int>("SELECT COUNT(*)::int FROM vat_rates"));
    }

    [Fact]
    public async Task Down_scripts_are_never_picked_up_by_the_migrator()
    {
        // DatabaseMigrator reads only *.sql at the top of Data/Migrations.
        var output = Path.Combine(AppContext.BaseDirectory, "Data", "Migrations");
        if (Directory.Exists(output))
            Assert.DoesNotContain(Directory.GetFiles(output, "*.sql"), f => f.EndsWith(".down.sql", StringComparison.Ordinal));
        Assert.True(File.Exists(DownScript()));
        await Task.CompletedTask;
    }

    private static async Task<bool> TablesExistAsync(NpgsqlConnection db) => await db.ExecuteScalarAsync<bool>("""
        SELECT to_regclass('public.vat_rates') IS NOT NULL
           AND to_regclass('public.tenant_voucher_settings') IS NOT NULL
           AND to_regclass('public.voucher_validations') IS NOT NULL
        """);

    private static string DownScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "gateway", "src", "Aiq.Gateway", "Data", "Migrations"))) dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("repo root not found above " + AppContext.BaseDirectory);
        return Path.Combine(dir.FullName, "gateway", "src", "Aiq.Gateway", "Data", "Migrations", "down", "068_voucher_validation.down.sql");
    }
}
