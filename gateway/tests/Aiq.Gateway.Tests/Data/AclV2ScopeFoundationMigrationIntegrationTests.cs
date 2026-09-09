using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

public sealed class AclV2ScopeFoundationMigrationIntegrationTests : IAsyncLifetime
{
    private readonly string? _baseConnectionString = ResolveTestConnectionString();
    private readonly string _schema = $"acl_v2a_migration_test_{Guid.NewGuid():N}";
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
    public async Task MigrationClassifiesOnlyUnambiguousAgentsAndPreservesExistingData()
    {
        if (_scopedConnectionString is null) return;
        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();
        await CreatePreV2ASchemaAsync(conn);

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var departmentA1 = Guid.NewGuid();
        var departmentA2 = Guid.NewGuid();
        var departmentB = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var platformAgent = Guid.NewGuid();
        var singleAgent = Guid.NewGuid();
        var sharedAgent = Guid.NewGuid();
        var orphanAgent = Guid.NewGuid();
        var note = Guid.NewGuid();

        await conn.ExecuteAsync("""
            INSERT INTO tenants(id) VALUES (@tenantA),(@tenantB);
            INSERT INTO departments(id,tenant_id) VALUES
              (@departmentA1,@tenantA),(@departmentA2,@tenantA),(@departmentB,@tenantB);
            INSERT INTO users(id,tenant_id) VALUES (@actor,@tenantA);
            INSERT INTO agents(id,scope,tenant_id,owning_tenant_id,created_by) VALUES
              (@platformAgent,'system',NULL,NULL,@actor),
              (@singleAgent,'tenant',@tenantA,@tenantA,@actor),
              (@sharedAgent,'tenant',@tenantA,@tenantA,@actor),
              (@orphanAgent,'tenant',@tenantA,@tenantA,@actor);
            INSERT INTO agent_department_assignments(id,tenant_id,department_id,agent_id,status) VALUES
              (gen_random_uuid(),@tenantA,@departmentA1,@singleAgent,'active'),
              (gen_random_uuid(),@tenantA,@departmentA1,@sharedAgent,'active'),
              (gen_random_uuid(),@tenantA,@departmentA2,@sharedAgent,'active');
            INSERT INTO business_capabilities(id,code,name) VALUES
              (gen_random_uuid(),'accounting.journal','Journal');
            INSERT INTO workflows(id,name,definition,created_by) VALUES
              (gen_random_uuid(),'Existing workflow','{"steps":[]}',@actor);
            INSERT INTO agent_versions(id,agent_id,workflow_id) SELECT gen_random_uuid(),@singleAgent,id FROM workflows;
            INSERT INTO agent_version_skill_assignments(agent_version_id,skill_name)
              SELECT id,'existing-skill' FROM agent_versions;
            INSERT INTO notes(id,user_id,tenant_id,title,content) VALUES
              (@note,@actor,@tenantA,'Existing note','preserve me');
            INSERT INTO agent_knowledge(agent_id,note_id,attached_by) VALUES
              (@singleAgent,@note,@actor);
            INSERT INTO hitl_gates(id,tenant_id,agent_id,name) VALUES
              (gen_random_uuid(),@tenantA,@singleAgent,'Gate');
            INSERT INTO hitl_pending_actions(id,tenant_id,gate_id,agent_id,tool_code,payload)
              SELECT gen_random_uuid(),tenant_id,id,agent_id,'tool','{}' FROM hitl_gates;
            INSERT INTO review_boards(id,tenant_id,name) VALUES
              (gen_random_uuid(),@tenantA,'Board');
            INSERT INTO agent_review_requests(id,tenant_id,agent_id,board_id,requested_by)
              SELECT gen_random_uuid(),@tenantA,@singleAgent,id,@actor FROM review_boards;
            INSERT INTO audit_log(actor_id,action,resource_type,tenant_id)
              VALUES (@actor,'existing','agent',@tenantA);
            """, new
        {
            tenantA, tenantB, departmentA1, departmentA2, departmentB, actor,
            platformAgent, singleAgent, sharedAgent, orphanAgent, note,
        });

        var migration = await ReadMigrationAsync("024_acl_v2_scope_foundations.sql");
        await conn.ExecuteAsync(migration);

        Assert.Equal("platform_template", await ScopeAsync(conn, platformAgent));
        Assert.Equal("department", await ScopeAsync(conn, singleAgent));
        Assert.Equal(departmentA1, await DepartmentAsync(conn, singleAgent));
        Assert.Null(await ScopeAsync(conn, sharedAgent));
        Assert.Null(await DepartmentAsync(conn, sharedAgent));
        Assert.Equal("quarantined_shared", await StatusAsync(conn, sharedAgent));
        Assert.Null(await ScopeAsync(conn, orphanAgent));
        Assert.Equal("quarantined_no_department", await StatusAsync(conn, orphanAgent));

        Assert.Equal("platform", await conn.ExecuteScalarAsync<string>(
            "SELECT scope FROM business_capabilities WHERE code='accounting.journal'"));
        Assert.Equal("platform", await conn.ExecuteScalarAsync<string>(
            "SELECT scope FROM workflows WHERE name='Existing workflow'"));
        Assert.Equal("filesystem", await conn.ExecuteScalarAsync<string>(
            "SELECT source_kind FROM skills WHERE code='existing-skill'"));
        Assert.Equal("preserve me", await conn.ExecuteScalarAsync<string>(
            "SELECT content FROM notes WHERE id=@note", new { note }));
        Assert.Equal("tenant", await conn.ExecuteScalarAsync<string>(
            "SELECT scope FROM notes WHERE id=@note", new { note }));
        Assert.Equal(departmentA1, await conn.ExecuteScalarAsync<Guid?>(
            "SELECT department_id FROM hitl_gates WHERE agent_id=@singleAgent", new { singleAgent }));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM acl_v2_agent_scope_issues WHERE issue_code='agent_multiple_departments'"));

        // SQL remains re-runnable; no duplicate catalogs/workspaces or changed data.
        await conn.ExecuteAsync(migration);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM skills WHERE code='existing-skill'"));
        Assert.Equal(3, await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM department_workspaces"));
        Assert.Equal("preserve me", await conn.ExecuteScalarAsync<string>(
            "SELECT content FROM notes WHERE id=@note", new { note }));
    }

    [Fact]
    public async Task ScopeAndCompositeDepartmentConstraintsRejectInvalidRows()
    {
        if (_scopedConnectionString is null) return;
        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();
        await CreatePreV2ASchemaAsync(conn);
        await conn.ExecuteAsync(await ReadMigrationAsync("024_acl_v2_scope_foundations.sql"));

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var departmentA = Guid.NewGuid();
        await conn.ExecuteAsync("""
            INSERT INTO tenants(id) VALUES (@tenantA),(@tenantB);
            INSERT INTO departments(id,tenant_id) VALUES (@departmentA,@tenantA);
            """, new { tenantA, tenantB, departmentA });

        await AssertSqlStateAsync(conn, """
            INSERT INTO business_capabilities(id,code,name,scope,tenant_id,department_id)
            VALUES (gen_random_uuid(),'bad.platform','Bad','platform',@tenantA,@departmentA)
            """, PostgresErrorCodes.CheckViolation, new { tenantA, departmentA });
        await AssertSqlStateAsync(conn, """
            INSERT INTO business_capabilities(id,code,name,scope,tenant_id,department_id)
            VALUES (gen_random_uuid(),'bad.department','Bad','department',@tenantB,@departmentA)
            """, PostgresErrorCodes.ForeignKeyViolation, new { tenantB, departmentA });
        await AssertSqlStateAsync(conn, """
            INSERT INTO skills(id,code,name,scope,tenant_id,department_id,source_kind,declarative_content)
            VALUES (gen_random_uuid(),'bad-skill','Bad','department',@tenantA,@departmentA,'filesystem','x')
            """, PostgresErrorCodes.CheckViolation, new { tenantA, departmentA });
        await AssertSqlStateAsync(conn, """
            INSERT INTO workflows(id,name,definition,scope,tenant_id,department_id)
            VALUES (gen_random_uuid(),'Bad','{"steps":[]}','department',@tenantB,@departmentA)
            """, PostgresErrorCodes.ForeignKeyViolation, new { tenantB, departmentA });
    }

    private static async Task<string?> ScopeAsync(NpgsqlConnection conn, Guid id) =>
        await conn.ExecuteScalarAsync<string?>("SELECT v2_scope FROM agents WHERE id=@id", new { id });

    private static async Task<Guid?> DepartmentAsync(NpgsqlConnection conn, Guid id) =>
        await conn.ExecuteScalarAsync<Guid?>("SELECT department_id FROM agents WHERE id=@id", new { id });

    private static async Task<string> StatusAsync(NpgsqlConnection conn, Guid id) =>
        await conn.ExecuteScalarAsync<string?>(
            "SELECT v2_migration_status FROM agents WHERE id=@id", new { id })
        ?? throw new InvalidOperationException($"Agent {id} was not found");

    private static async Task AssertSqlStateAsync(
        NpgsqlConnection conn, string sql, string sqlState, object parameters)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => conn.ExecuteAsync(sql, parameters));
        Assert.Equal(sqlState, exception.SqlState);
    }

    private static async Task<string> ReadMigrationAsync(string name)
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "Data", "Migrations", name);
        if (File.Exists(outputPath)) return await File.ReadAllTextAsync(outputPath);
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../src/Aiq.Gateway/Data/Migrations", name));
        return await File.ReadAllTextAsync(sourcePath);
    }

    private static async Task CreatePreV2ASchemaAsync(NpgsqlConnection conn)
    {
        await conn.ExecuteAsync("""
            CREATE EXTENSION IF NOT EXISTS pgcrypto;
            CREATE TABLE tenants (id UUID PRIMARY KEY);
            CREATE TABLE users (id UUID PRIMARY KEY, tenant_id UUID REFERENCES tenants(id));
            CREATE TABLE departments (
              id UUID PRIMARY KEY, tenant_id UUID NOT NULL REFERENCES tenants(id),
              UNIQUE(id,tenant_id));
            CREATE TABLE agents (
              id UUID PRIMARY KEY, scope TEXT NOT NULL,
              tenant_id UUID REFERENCES tenants(id), owning_tenant_id UUID REFERENCES tenants(id),
              created_by UUID REFERENCES users(id));
            CREATE TABLE agent_department_assignments (
              id UUID PRIMARY KEY, tenant_id UUID NOT NULL REFERENCES tenants(id),
              department_id UUID NOT NULL, agent_id UUID NOT NULL REFERENCES agents(id), status TEXT NOT NULL,
              FOREIGN KEY(department_id,tenant_id) REFERENCES departments(id,tenant_id));
            CREATE TABLE business_capabilities (
              id UUID PRIMARY KEY, code TEXT UNIQUE NOT NULL, name TEXT NOT NULL,
              description TEXT, category TEXT, is_active BOOLEAN NOT NULL DEFAULT TRUE,
              created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW());
            CREATE TABLE workflows (
              id UUID PRIMARY KEY, name TEXT NOT NULL, description TEXT,
              definition JSONB NOT NULL, is_active BOOLEAN NOT NULL DEFAULT TRUE,
              created_by UUID REFERENCES users(id), created_at TIMESTAMPTZ DEFAULT NOW(), updated_at TIMESTAMPTZ DEFAULT NOW());
            CREATE TABLE agent_versions (
              id UUID PRIMARY KEY, agent_id UUID NOT NULL REFERENCES agents(id), workflow_id UUID REFERENCES workflows(id));
            CREATE TABLE agent_version_skill_assignments (
              agent_version_id UUID NOT NULL REFERENCES agent_versions(id), skill_name TEXT NOT NULL,
              PRIMARY KEY(agent_version_id,skill_name));
            CREATE TABLE notes (
              id UUID PRIMARY KEY, user_id UUID NOT NULL REFERENCES users(id), tenant_id UUID NOT NULL REFERENCES tenants(id),
              title TEXT NOT NULL, content TEXT NOT NULL, tags TEXT[] NOT NULL DEFAULT '{}',
              created_at TIMESTAMPTZ DEFAULT NOW(), updated_at TIMESTAMPTZ DEFAULT NOW());
            CREATE TABLE agent_knowledge (
              agent_id UUID NOT NULL REFERENCES agents(id), note_id UUID NOT NULL REFERENCES notes(id),
              attached_by UUID REFERENCES users(id), attached_at TIMESTAMPTZ DEFAULT NOW(), PRIMARY KEY(agent_id,note_id));
            CREATE TABLE hitl_gates (
              id UUID PRIMARY KEY, tenant_id UUID NOT NULL REFERENCES tenants(id), agent_id UUID NOT NULL REFERENCES agents(id),
              name TEXT NOT NULL, is_active BOOLEAN NOT NULL DEFAULT TRUE);
            CREATE TABLE hitl_pending_actions (
              id UUID PRIMARY KEY, tenant_id UUID NOT NULL REFERENCES tenants(id), gate_id UUID NOT NULL REFERENCES hitl_gates(id),
              agent_id UUID NOT NULL REFERENCES agents(id), tool_code TEXT NOT NULL, payload JSONB NOT NULL,
              status TEXT NOT NULL DEFAULT 'pending', created_at TIMESTAMPTZ DEFAULT NOW());
            CREATE TABLE review_boards (
              id UUID PRIMARY KEY, tenant_id UUID NOT NULL REFERENCES tenants(id), name TEXT NOT NULL);
            CREATE TABLE agent_review_requests (
              id UUID PRIMARY KEY, tenant_id UUID NOT NULL REFERENCES tenants(id), agent_id UUID NOT NULL REFERENCES agents(id),
              board_id UUID NOT NULL REFERENCES review_boards(id), requested_by UUID REFERENCES users(id),
              status TEXT NOT NULL DEFAULT 'pending');
            CREATE TABLE audit_log (
              id BIGSERIAL PRIMARY KEY, actor_id UUID REFERENCES users(id), action TEXT NOT NULL,
              resource_type TEXT NOT NULL, tenant_id UUID REFERENCES tenants(id), created_at TIMESTAMPTZ DEFAULT NOW());
            """);
    }
}
