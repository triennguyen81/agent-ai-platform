using Aiq.Gateway.Data;
using Dapper;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// The real <see cref="DatabaseMigrator"/> (not the test fixture's copy of its
/// loop) on an empty database: infra/postgres/init, then every Gateway
/// migration in one run, each recorded with its checksum; a second run is a
/// no-op. Own throwaway database, dropped afterwards.
/// </summary>
public sealed class FreshDatabaseMigratorTests
{
    private sealed class Env(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Aiq.Gateway";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public async Task Every_migration_applies_on_an_empty_database_and_a_rerun_is_a_no_op()
    {
        var baseConnection = new TaxVerificationDatabase().BaseConnectionString;
        if (baseConnection is null) return;
        var name = $"aiq_fresh_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(baseConnection))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync($"CREATE DATABASE {name}");
        }
        var connection = new NpgsqlConnectionStringBuilder(baseConnection) { Database = name, Pooling = false }.ConnectionString;
        try
        {
            await using (var db = new NpgsqlConnection(connection))
            {
                await db.OpenAsync();
                foreach (var path in Directory.GetFiles(TaxVerificationDatabase.RepoPath("infra", "postgres", "init"), "*.sql").Order(StringComparer.Ordinal))
                {
                    var sql = string.Join("\n", (await File.ReadAllLinesAsync(path)).Where(l => !l.TrimStart().StartsWith('\\')));
                    await db.ExecuteAsync(new CommandDefinition(sql, commandTimeout: 120));
                }
            }

            var migrations = TaxVerificationDatabase.MigrationsDir();
            var migrator = new DatabaseMigrator(new NpgsqlConnectionFactory(connection),
                new Env(Directory.GetParent(migrations)!.Parent!.FullName), NullLogger<DatabaseMigrator>.Instance);
            await migrator.ApplyAsync(CancellationToken.None);

            var expected = Directory.GetFiles(migrations, "*.sql")
                .ToDictionary(p => Path.GetFileName(p), p =>DatabaseMigrator.ComputeChecksum(File.ReadAllText(p)));
            Assert.Contains("070_voucher_lifecycle.sql", expected.Keys);

            await using var check = new NpgsqlConnection(connection);
            await check.OpenAsync();
            var applied = (await check.QueryAsync<(string Version, string Checksum)>("SELECT version, checksum FROM schema_migrations"))
                .ToDictionary(r => r.Version, r => r.Checksum);
            Assert.Equal(expected.OrderBy(e => e.Key), applied.OrderBy(a => a.Key));
            Assert.True(await check.ExecuteScalarAsync<bool>("""
                SELECT is_nullable = 'NO' FROM information_schema.columns WHERE table_name = 'vouchers' AND column_name = 'lifecycle_state'
                """));
            Assert.Equal(2, await check.ExecuteScalarAsync<int>("""
                SELECT COUNT(*)::int FROM pg_trigger WHERE tgrelid = 'voucher_events'::regclass AND NOT tgisinternal
                """));

            await migrator.ApplyAsync(CancellationToken.None);
            Assert.Equal(expected.Count, await check.ExecuteScalarAsync<int>("SELECT COUNT(*)::int FROM schema_migrations"));
        }
        finally
        {
            await using var admin = new NpgsqlConnection(baseConnection);
            await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
        }
    }
}
