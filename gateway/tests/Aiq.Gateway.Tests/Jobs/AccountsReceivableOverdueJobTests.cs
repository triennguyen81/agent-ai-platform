using Aiq.Gateway.Contracts;
using Aiq.Gateway.Data;
using Aiq.Gateway.Jobs;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aiq.Gateway.Tests.Jobs;

/// <summary>
/// R07/R08 — AccountsReceivableOverdueJob is plain, DI-testable C# (Hangfire
/// only supplies the cron trigger), so it's exercised directly here rather
/// than via the real-HTTP E2E scripts (which never fire scheduled jobs).
/// </summary>
public sealed class AccountsReceivableOverdueJobTests : IAsyncLifetime
{
    static AccountsReceivableOverdueJobTests()
    {
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyTypeHandler());
    }

    private readonly string? _baseConnectionString = ResolveTestConnectionString();
    private readonly string _schema = $"ar_overdue_job_test_{Guid.NewGuid():N}";
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
            CREATE TABLE tenants (id UUID PRIMARY KEY);
            CREATE TABLE departments (id UUID PRIMARY KEY, tenant_id UUID NOT NULL);
            CREATE TABLE roles (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), name TEXT NOT NULL UNIQUE);
            INSERT INTO roles(name) VALUES ('admin'), ('user');
            CREATE TABLE users (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                tenant_id UUID NOT NULL, department_id UUID NOT NULL, email TEXT NOT NULL
            );
            CREATE TABLE user_roles (user_id UUID NOT NULL, role_id UUID NOT NULL, PRIMARY KEY(user_id, role_id));
            CREATE TABLE sim_customers (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                tenant_id UUID NOT NULL, department_id UUID NOT NULL, name TEXT NOT NULL
            );
            CREATE TABLE sim_ar_ledger (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                tenant_id UUID NOT NULL, customer_id UUID NOT NULL,
                document_date DATE NOT NULL, document_number TEXT NOT NULL, document_type TEXT NOT NULL,
                debit NUMERIC(18,2) DEFAULT 0, credit NUMERIC(18,2) DEFAULT 0, due_date DATE,
                paid_amount NUMERIC(18,2) NOT NULL DEFAULT 0, status TEXT NOT NULL DEFAULT 'open'
            );
            CREATE TABLE notifications (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                tenant_id UUID NOT NULL, user_id UUID NOT NULL, kind TEXT NOT NULL,
                ref_type TEXT, ref_id UUID, title TEXT NOT NULL, body TEXT,
                read_at TIMESTAMPTZ, created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE UNIQUE INDEX uq_notifications_dedup ON notifications(tenant_id, user_id, kind, ref_id) WHERE ref_id IS NOT NULL;
            """);
    }

    public async Task DisposeAsync()
    {
        if (_baseConnectionString is null) return;
        await using var conn = new NpgsqlConnection(_baseConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP SCHEMA IF EXISTS {_schema} CASCADE");
    }

    [Fact]
    public async Task ScanNotifiesDepartmentAdminsOnlyOnceAndEscalatesAsDaysPass()
    {
        if (_scopedConnectionString is null) return;

        var factory = new NpgsqlConnectionFactory(_scopedConnectionString);
        var notifications = new NotificationRepository(factory);
        var options = Options.Create(new ArOverdueOptions { ReminderDays = 1, NoticeDays = 5, PenaltyDays = 10, LegalDays = 20 });
        var job = new AccountsReceivableOverdueJob(factory, notifications, options, NullLogger<AccountsReceivableOverdueJob>.Instance);

        await using var conn = await factory.OpenAsync();
        var tenantId = Guid.NewGuid();
        var deptId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var regularUserId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        await conn.ExecuteAsync("""
            INSERT INTO tenants(id) VALUES (@tenantId);
            INSERT INTO departments(id,tenant_id) VALUES (@deptId,@tenantId);
            INSERT INTO users(id,tenant_id,department_id,email) VALUES
                (@adminId,@tenantId,@deptId,'admin@t.local'),
                (@regularUserId,@tenantId,@deptId,'user@t.local');
            INSERT INTO user_roles(user_id,role_id) SELECT @adminId, id FROM roles WHERE name='admin';
            INSERT INTO user_roles(user_id,role_id) SELECT @regularUserId, id FROM roles WHERE name='user';
            INSERT INTO sim_customers(id,tenant_id,department_id,name) VALUES (@customerId,@tenantId,@deptId,'Cty ABC');
            -- 3 days overdue -> reminder1 stage at these test thresholds.
            INSERT INTO sim_ar_ledger(tenant_id,customer_id,document_date,document_number,document_type,debit,credit,due_date)
            VALUES (@tenantId,@customerId,CURRENT_DATE-10,'INV-001','invoice',5000000,0,CURRENT_DATE-3);
            """, new { tenantId, deptId, adminId, regularUserId, customerId });

        await job.RunAsync(CancellationToken.None);

        var afterFirstRun = (await conn.QueryAsync<(Guid UserId, string Kind)>(
            "SELECT user_id AS UserId, kind AS Kind FROM notifications")).AsList();
        Assert.Single(afterFirstRun);
        Assert.Equal(adminId, afterFirstRun[0].UserId); // only the admin, never the plain user
        Assert.Equal("ar_overdue_reminder1", afterFirstRun[0].Kind);

        // Re-run same day — idempotent, no duplicate.
        await job.RunAsync(CancellationToken.None);
        var afterSecondRun = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM notifications");
        Assert.Equal(1, afterSecondRun);

        // Advance the invoice past the "notice" threshold (5 days) by
        // moving due_date further back, then re-run — must escalate to a
        // NEW kind (not blocked by the reminder1 dedup key).
        await conn.ExecuteAsync("UPDATE sim_ar_ledger SET due_date = CURRENT_DATE - 7 WHERE document_number='INV-001'");
        await job.RunAsync(CancellationToken.None);
        var kinds = (await conn.QueryAsync<string>("SELECT kind FROM notifications ORDER BY kind")).AsList();
        Assert.Equal(2, kinds.Count);
        Assert.Contains("ar_overdue_notice", kinds);
        Assert.Contains("ar_overdue_reminder1", kinds);

        // Settled by a receipt (paid_amount/status move; credit never does):
        // however far past due, a PAID invoice is never escalated again.
        await conn.ExecuteAsync("UPDATE sim_ar_ledger SET paid_amount = debit, status = 'paid', due_date = CURRENT_DATE - 60 WHERE document_number='INV-001'");
        await job.RunAsync(CancellationToken.None);
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM notifications"));
    }
}
