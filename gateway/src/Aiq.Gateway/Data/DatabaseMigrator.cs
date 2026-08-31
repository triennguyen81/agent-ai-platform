using Dapper;
using System.Security.Cryptography;

namespace Aiq.Gateway.Data;

public sealed class DatabaseMigrator
{
    private readonly NpgsqlConnectionFactory _factory;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<DatabaseMigrator> _logger;

    public DatabaseMigrator(
        NpgsqlConnectionFactory factory,
        IHostEnvironment environment,
        ILogger<DatabaseMigrator> logger)
    {
        _factory = factory;
        _environment = environment;
        _logger = logger;
    }

    public async Task ApplyAsync(CancellationToken ct)
    {
        var directory = Path.Combine(_environment.ContentRootPath, "Data", "Migrations");
        if (!Directory.Exists(directory))
        {
            throw new InvalidOperationException($"Migration directory not found: {directory}");
        }

        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version       TEXT PRIMARY KEY,
                checksum      TEXT,
                applied_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            ALTER TABLE schema_migrations ADD COLUMN IF NOT EXISTS checksum TEXT;
            """, cancellationToken: ct));

        foreach (var path in Directory.GetFiles(directory, "*.sql").Order(StringComparer.Ordinal))
        {
            var version = Path.GetFileName(path);
            var sql = await File.ReadAllTextAsync(path, ct);
            var checksum = ComputeChecksum(sql);
            var appliedChecksum = await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                "SELECT checksum FROM schema_migrations WHERE version = @version",
                new { version }, cancellationToken: ct));
            var applied = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM schema_migrations WHERE version = @version)",
                new { version }, cancellationToken: ct));
            if (applied)
            {
                if (string.IsNullOrWhiteSpace(appliedChecksum))
                {
                    // Establish the checksum baseline for migrations that pre-date
                    // checksum enforcement. This records the currently packaged
                    // content; it cannot attest to their historical contents.
                    await conn.ExecuteAsync(new CommandDefinition("""
                        UPDATE schema_migrations
                        SET checksum = @checksum
                        WHERE version = @version AND checksum IS NULL
                        """, new { version, checksum }, cancellationToken: ct));
                    _logger.LogInformation(
                        "Established checksum baseline for migration {Migration}", version);
                    continue;
                }

                if (!string.Equals(appliedChecksum, checksum, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Applied migration checksum mismatch: {version}. " +
                        "Applied migration files are immutable.");
                }
                continue;
            }

            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                await conn.ExecuteAsync(new CommandDefinition(sql, transaction: tx, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO schema_migrations(version, checksum) VALUES (@version, @checksum)",
                    new { version, checksum }, transaction: tx, cancellationToken: ct));
                await tx.CommitAsync(ct);
                _logger.LogInformation("Applied database migration {Migration}", version);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }
    }

    public static string ComputeChecksum(string sql)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(sql);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
