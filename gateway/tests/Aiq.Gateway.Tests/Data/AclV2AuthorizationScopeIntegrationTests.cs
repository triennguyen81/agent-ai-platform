using Aiq.Gateway.Data;
using Aiq.Gateway.Governance;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

public sealed class AclV2AuthorizationScopeIntegrationTests : IAsyncLifetime
{
    private readonly string? _baseConnectionString = ResolveTestConnectionString();
    private readonly string _schema = $"acl_v2b_scope_test_{Guid.NewGuid():N}";
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        if (_baseConnectionString is null) return;
        await using var admin = new NpgsqlConnection(_baseConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE SCHEMA {_schema}");
        _connectionString = new NpgsqlConnectionStringBuilder(_baseConnectionString) { SearchPath = _schema }.ConnectionString;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("""
            CREATE TABLE agents (
              id UUID PRIMARY KEY, owning_tenant_id UUID, department_id UUID,
              v2_scope TEXT, v2_migration_status TEXT, tenant_id UUID);
            CREATE TABLE agent_tenant_assignments (
              agent_id UUID, tenant_id UUID, status TEXT);
            CREATE TABLE agent_department_assignments (
              agent_id UUID, tenant_id UUID, department_id UUID, status TEXT);
            CREATE TABLE review_boards (
              id UUID PRIMARY KEY, tenant_id UUID, department_id UUID);
            CREATE TABLE agent_review_requests (
              id UUID PRIMARY KEY, tenant_id UUID, department_id UUID);
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
    public async Task DepartmentVisibilityRejectsCrossDepartmentAgentIds()
    {
        if (_connectionString is null) return;
        var tenant = Guid.NewGuid();
        var accounting = Guid.NewGuid();
        var sales = Guid.NewGuid();
        var accountingAgent = Guid.NewGuid();
        var salesAgent = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("""
            INSERT INTO agents VALUES
              (@accountingAgent,@tenant,@accounting,'department','resolved',@tenant),
              (@salesAgent,@tenant,@sales,'department','resolved',@tenant);
            """, new { tenant, accounting, sales, accountingAgent, salesAgent });

        var repo = new HitlRepository(new NpgsqlConnectionFactory(_connectionString));
        Assert.True(await repo.IsAgentVisibleToDepartmentAsync(tenant, accounting, accountingAgent, default));
        Assert.False(await repo.IsAgentVisibleToDepartmentAsync(tenant, accounting, salesAgent, default));
    }

    [Fact]
    public async Task ReviewIdentifiersAreBoundToTenantAndDepartment()
    {
        if (_connectionString is null) return;
        var tenant = Guid.NewGuid();
        var accounting = Guid.NewGuid();
        var sales = Guid.NewGuid();
        var board = Guid.NewGuid();
        var request = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("""
            INSERT INTO review_boards VALUES (@board,@tenant,@sales);
            INSERT INTO agent_review_requests VALUES (@request,@tenant,@sales);
            """, new { board, request, tenant, sales });

        var repo = new ReviewBoardRepository(new NpgsqlConnectionFactory(_connectionString));
        Assert.True(await repo.IsBoardInDepartmentAsync(board, tenant, sales, default));
        Assert.False(await repo.IsBoardInDepartmentAsync(board, tenant, accounting, default));
        Assert.True(await repo.IsRequestInDepartmentAsync(request, tenant, sales, default));
        Assert.False(await repo.IsRequestInDepartmentAsync(request, tenant, accounting, default));
    }

    private static string? ResolveTestConnectionString()
    {
        var explicitConnection = Environment.GetEnvironmentVariable("AIQ_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(explicitConnection)) return explicitConnection;
        var password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD");
        if (string.IsNullOrWhiteSpace(password)) return null;
        return new NpgsqlConnectionStringBuilder
        {
            Host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "postgres",
            Port = int.TryParse(Environment.GetEnvironmentVariable("POSTGRES_PORT"), out var port) ? port : 5432,
            Database = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "aiq",
            Username = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "aiq",
            Password = password,
        }.ConnectionString;
    }
}
