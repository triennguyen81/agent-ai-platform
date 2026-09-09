using Aiq.Gateway.Data;
using Aiq.Gateway.Governance;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Governance;

public sealed class DynamicAgentRepositoryIntegrationTests : IAsyncLifetime
{
    private readonly string? _baseConnectionString = ResolveTestConnectionString();
    private readonly string _schema = $"assignment_test_{Guid.NewGuid():N}";
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
        if (string.IsNullOrWhiteSpace(_baseConnectionString)) return;

        await using var conn = new NpgsqlConnection(_baseConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"CREATE SCHEMA {_schema}");

        var builder = new NpgsqlConnectionStringBuilder(_baseConnectionString)
        {
            SearchPath = _schema,
        };
        _scopedConnectionString = builder.ConnectionString;

        await using var scoped = new NpgsqlConnection(_scopedConnectionString);
        await scoped.OpenAsync();
        await scoped.ExecuteAsync("""
            CREATE TABLE tenants (
                id UUID PRIMARY KEY,
                status TEXT NOT NULL,
                max_agents INTEGER NULL
            );
            CREATE TABLE users (id UUID PRIMARY KEY);
            CREATE TABLE agents (
                id UUID PRIMARY KEY,
                scope TEXT NOT NULL
            );
            CREATE TABLE agent_tenant_assignments (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                agent_id UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
                tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
                status TEXT NOT NULL CHECK (status IN ('active', 'revoked')),
                assigned_by UUID NULL,
                assigned_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                revoked_at TIMESTAMPTZ NULL,
                CHECK ((status = 'active' AND revoked_at IS NULL)
                    OR (status = 'revoked' AND revoked_at IS NOT NULL))
            );
            CREATE UNIQUE INDEX uq_agent_tenant_assignments_active
                ON agent_tenant_assignments(agent_id, tenant_id)
                WHERE status = 'active';
            CREATE TABLE agent_assignments (
                tenant_id UUID NOT NULL,
                agent_id UUID NOT NULL,
                user_id UUID NOT NULL,
                status TEXT NOT NULL CHECK (status IN ('active', 'revoked')),
                revoked_at TIMESTAMPTZ NULL,
                PRIMARY KEY (tenant_id, agent_id, user_id)
            );
            -- D3: RevokeTenantAsync now also cascades here.
            CREATE TABLE agent_department_assignments (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                tenant_id UUID NOT NULL,
                department_id UUID NOT NULL,
                agent_id UUID NOT NULL,
                status TEXT NOT NULL CHECK (status IN ('active', 'revoked')),
                revoked_at TIMESTAMPTZ NULL
            );
            -- D5: RevokeTenantAsync now also cascades here.
            CREATE TABLE department_capability_bindings (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                tenant_id UUID NOT NULL,
                department_id UUID NOT NULL,
                capability_id UUID NOT NULL,
                agent_id UUID NOT NULL,
                status TEXT NOT NULL CHECK (status IN ('active', 'revoked')),
                revoked_at TIMESTAMPTZ NULL
            );
            """);
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(_baseConnectionString)) return;

        await using var conn = new NpgsqlConnection(_baseConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP SCHEMA IF EXISTS {_schema} CASCADE");
    }

    [Fact]
    public async Task TenantAssignmentPutIsIdempotentQuotaAwareAndHistoryPreserving()
    {
        if (_scopedConnectionString is null) return;

        var tenantId = Guid.NewGuid();
        var inactiveTenantId = Guid.NewGuid();
        var availableTenantId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var secondAgentId = Guid.NewGuid();
        var nonSystemAgentId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(_scopedConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("""
                INSERT INTO tenants(id,status,max_agents) VALUES
                    (@tenantId,'active',1), (@inactiveTenantId,'inactive',1),
                    (@availableTenantId,'active',1);
                INSERT INTO users(id) VALUES (@userId);
                INSERT INTO agents(id,scope) VALUES
                    (@agentId,'system'), (@secondAgentId,'system'), (@nonSystemAgentId,'tenant');
                """, new { tenantId, inactiveTenantId, availableTenantId, agentId, secondAgentId, nonSystemAgentId, userId });
        }

        var repository = new DynamicAgentRepository(
            new NpgsqlConnectionFactory(_scopedConnectionString));

        // First assignment succeeds and creates exactly one active row.
        Assert.True(await repository.AssignTenantAsync(agentId, tenantId, actorId, default));
        Assert.Equal(1, await CountAsync("active"));

        // Repeated PUT succeeds, creates no duplicate, and succeeds at quota.
        Assert.True(await repository.AssignTenantAsync(agentId, tenantId, actorId, default));
        Assert.Equal(1, await CountAsync("active"));

        // A genuinely new assignment still fails when the quota is full.
        var quotaError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.AssignTenantAsync(secondAgentId, tenantId, actorId, default));
        Assert.Equal("agent_quota_exceeded", quotaError.Message);

        // Inactive tenants and non-system agents retain the guarded failure.
        Assert.False(await repository.AssignTenantAsync(secondAgentId, inactiveTenantId, actorId, default));
        Assert.False(await repository.AssignTenantAsync(nonSystemAgentId, availableTenantId, actorId, default));

        await using (var conn = new NpgsqlConnection(_scopedConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("""
                INSERT INTO agent_assignments(tenant_id,agent_id,user_id,status)
                VALUES (@tenantId,@agentId,@userId,'active')
                """, new { tenantId, agentId, userId });
        }

        // Revocation preserves tenant history and cascades to user assignments.
        Assert.True(await repository.RevokeTenantAsync(agentId, tenantId, default));
        Assert.Equal(0, await CountAsync("active"));
        Assert.Equal(1, await CountAsync("revoked"));
        Assert.Equal("revoked", await UserAssignmentStatusAsync());

        // Reassignment inserts a fresh active row while retaining revoked history.
        Assert.True(await repository.AssignTenantAsync(agentId, tenantId, actorId, default));
        Assert.Equal(1, await CountAsync("active"));
        Assert.Equal(1, await CountAsync("revoked"));

        async Task<int> CountAsync(string status)
        {
            await using var conn = new NpgsqlConnection(_scopedConnectionString);
            return await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*)::int FROM agent_tenant_assignments WHERE agent_id=@agentId AND tenant_id=@tenantId AND status=@status",
                new { agentId, tenantId, status });
        }

        async Task<string> UserAssignmentStatusAsync()
        {
            await using var conn = new NpgsqlConnection(_scopedConnectionString);
            return (await conn.ExecuteScalarAsync<string>(
                "SELECT status FROM agent_assignments WHERE tenant_id=@tenantId AND agent_id=@agentId AND user_id=@userId",
                new { tenantId, agentId, userId }))!;
        }
    }
}
