using Aiq.Gateway.Data;
using Aiq.Gateway.Jobs;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Aiq.Gateway.Tests.Jobs;

/// <summary>
/// P2 audit fix — ContractInstallmentPenaltyJob is plain, DI-testable C#
/// (Hangfire only supplies the cron trigger), so it's exercised directly
/// here rather than via the real-HTTP E2E scripts (which never fire
/// scheduled jobs).
/// </summary>
public sealed class ContractInstallmentPenaltyJobTests : IAsyncLifetime
{
    private readonly string? _baseConnectionString = ResolveTestConnectionString();
    private readonly string _schema = $"penalty_job_test_{Guid.NewGuid():N}";
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

        await using var scoped = new NpgsqlConnection(_scopedConnectionString);
        await scoped.OpenAsync();
        await scoped.ExecuteAsync("""
            CREATE EXTENSION IF NOT EXISTS pgcrypto;
            CREATE TABLE contracts (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                penalty_percent NUMERIC(9,4)
            );
            CREATE TABLE contract_payment_schedules (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                contract_id UUID NOT NULL REFERENCES contracts(id),
                ordinal INT NOT NULL,
                total_amount NUMERIC(18,2),
                due_date DATE,
                status TEXT NOT NULL DEFAULT 'pending',
                penalty_overdue_amount NUMERIC(18,2), penalty_overdue_days INT,
                penalty_rate NUMERIC(9,4), penalty_amount NUMERIC(18,2), penalty_calculated_at TIMESTAMPTZ
            );
            """);
    }

    public async Task DisposeAsync()
    {
        if (_baseConnectionString is null) return;
        await using var conn = new NpgsqlConnection(_baseConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP SCHEMA IF EXISTS {_schema} CASCADE");
    }

    private sealed record PenaltyRow(decimal? PenaltyOverdueAmount, int? PenaltyOverdueDays, decimal? PenaltyRate, decimal? PenaltyAmount, DateTime? PenaltyCalculatedAt);

    [Fact]
    public async Task NotOverdue_NeverGetsAPenalty()
    {
        if (_scopedConnectionString is null) return;
        var factory = new NpgsqlConnectionFactory(_scopedConnectionString);
        var job = new ContractInstallmentPenaltyJob(factory, NullLogger<ContractInstallmentPenaltyJob>.Instance);
        await using var conn = await factory.OpenAsync();

        var contractId = Guid.NewGuid();
        var installmentId = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT INTO contracts(id, penalty_percent) VALUES (@contractId, 0.05); " +
            "INSERT INTO contract_payment_schedules(id, contract_id, ordinal, total_amount, due_date, status) " +
            "VALUES (@installmentId, @contractId, 1, 2000000, CURRENT_DATE + 5, 'pending')",
            new { contractId, installmentId });

        await job.RunAsync(CancellationToken.None);

        var row = await conn.QuerySingleAsync<PenaltyRow>(
            "SELECT penalty_overdue_amount AS PenaltyOverdueAmount, penalty_overdue_days AS PenaltyOverdueDays, penalty_rate AS PenaltyRate, penalty_amount AS PenaltyAmount, penalty_calculated_at AS PenaltyCalculatedAt FROM contract_payment_schedules WHERE id=@installmentId",
            new { installmentId });
        Assert.Null(row.PenaltyAmount);
    }

    [Fact]
    public async Task Overdue_ComputesAmountDaysRateAndCalculationDate()
    {
        if (_scopedConnectionString is null) return;
        var factory = new NpgsqlConnectionFactory(_scopedConnectionString);
        var job = new ContractInstallmentPenaltyJob(factory, NullLogger<ContractInstallmentPenaltyJob>.Instance);
        await using var conn = await factory.OpenAsync();

        var contractId = Guid.NewGuid();
        var installmentId = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT INTO contracts(id, penalty_percent) VALUES (@contractId, 0.05); " +
            "INSERT INTO contract_payment_schedules(id, contract_id, ordinal, total_amount, due_date, status) " +
            "VALUES (@installmentId, @contractId, 1, 2000000, CURRENT_DATE - 10, 'pending')",
            new { contractId, installmentId });

        await job.RunAsync(CancellationToken.None);

        var row = await conn.QuerySingleAsync<PenaltyRow>(
            "SELECT penalty_overdue_amount AS PenaltyOverdueAmount, penalty_overdue_days AS PenaltyOverdueDays, penalty_rate AS PenaltyRate, penalty_amount AS PenaltyAmount, penalty_calculated_at AS PenaltyCalculatedAt FROM contract_payment_schedules WHERE id=@installmentId",
            new { installmentId });
        Assert.Equal(2_000_000m, row.PenaltyOverdueAmount);
        Assert.Equal(10, row.PenaltyOverdueDays);
        Assert.Equal(0.05m, row.PenaltyRate);
        // 2,000,000 * (0.05/100) * 10 days = 10,000
        Assert.Equal(10_000m, row.PenaltyAmount);
        Assert.NotNull(row.PenaltyCalculatedAt);
    }

    [Fact]
    public async Task PartialPayment_NeverGetsAGuessedPenalty()
    {
        if (_scopedConnectionString is null) return;
        var factory = new NpgsqlConnectionFactory(_scopedConnectionString);
        var job = new ContractInstallmentPenaltyJob(factory, NullLogger<ContractInstallmentPenaltyJob>.Instance);
        await using var conn = await factory.OpenAsync();

        var contractId = Guid.NewGuid();
        var installmentId = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT INTO contracts(id, penalty_percent) VALUES (@contractId, 0.05); " +
            "INSERT INTO contract_payment_schedules(id, contract_id, ordinal, total_amount, due_date, status) " +
            "VALUES (@installmentId, @contractId, 1, 2000000, CURRENT_DATE - 10, 'partial')",
            new { contractId, installmentId });

        await job.RunAsync(CancellationToken.None);

        var amount = await conn.QuerySingleAsync<decimal?>(
            "SELECT penalty_amount FROM contract_payment_schedules WHERE id=@installmentId", new { installmentId });
        Assert.Null(amount); // remaining unpaid balance isn't tracked — never guessed
    }

    [Fact]
    public async Task AmbiguousTerms_NoPenaltyPercentConfigured_NeverGuesses()
    {
        if (_scopedConnectionString is null) return;
        var factory = new NpgsqlConnectionFactory(_scopedConnectionString);
        var job = new ContractInstallmentPenaltyJob(factory, NullLogger<ContractInstallmentPenaltyJob>.Instance);
        await using var conn = await factory.OpenAsync();

        var contractId = Guid.NewGuid();
        var installmentId = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT INTO contracts(id, penalty_percent) VALUES (@contractId, NULL); " +
            "INSERT INTO contract_payment_schedules(id, contract_id, ordinal, total_amount, due_date, status) " +
            "VALUES (@installmentId, @contractId, 1, 2000000, CURRENT_DATE - 10, 'pending')",
            new { contractId, installmentId });

        await job.RunAsync(CancellationToken.None);

        var amount = await conn.QuerySingleAsync<decimal?>(
            "SELECT penalty_amount FROM contract_payment_schedules WHERE id=@installmentId", new { installmentId });
        Assert.Null(amount);
    }

    [Fact]
    public async Task Retry_IsIdempotent_AndClearsStaleValueOncePaid()
    {
        if (_scopedConnectionString is null) return;
        var factory = new NpgsqlConnectionFactory(_scopedConnectionString);
        var job = new ContractInstallmentPenaltyJob(factory, NullLogger<ContractInstallmentPenaltyJob>.Instance);
        await using var conn = await factory.OpenAsync();

        var contractId = Guid.NewGuid();
        var installmentId = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT INTO contracts(id, penalty_percent) VALUES (@contractId, 0.05); " +
            "INSERT INTO contract_payment_schedules(id, contract_id, ordinal, total_amount, due_date, status) " +
            "VALUES (@installmentId, @contractId, 1, 2000000, CURRENT_DATE - 10, 'pending')",
            new { contractId, installmentId });

        await job.RunAsync(CancellationToken.None);
        var first = await conn.QuerySingleAsync<decimal?>("SELECT penalty_amount FROM contract_payment_schedules WHERE id=@installmentId", new { installmentId });
        await job.RunAsync(CancellationToken.None);
        var second = await conn.QuerySingleAsync<decimal?>("SELECT penalty_amount FROM contract_payment_schedules WHERE id=@installmentId", new { installmentId });
        Assert.Equal(first, second); // pure recompute, never doubles

        await conn.ExecuteAsync("UPDATE contract_payment_schedules SET status='paid' WHERE id=@installmentId", new { installmentId });
        await job.RunAsync(CancellationToken.None);
        var afterPaid = await conn.QuerySingleAsync<decimal?>("SELECT penalty_amount FROM contract_payment_schedules WHERE id=@installmentId", new { installmentId });
        Assert.Null(afterPaid); // cleared, not left stale
    }
}
