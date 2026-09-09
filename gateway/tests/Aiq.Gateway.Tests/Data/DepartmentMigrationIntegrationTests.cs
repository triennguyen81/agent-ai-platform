using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

public sealed class DepartmentMigrationIntegrationTests : IAsyncLifetime
{
    private readonly string? _baseConnectionString = ResolveTestConnectionString();
    private readonly string _schema = $"department_migration_test_{Guid.NewGuid():N}";
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
        _scopedConnectionString = new NpgsqlConnectionStringBuilder(_baseConnectionString)
        {
            SearchPath = _schema,
        }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(_baseConnectionString)) return;

        await using var conn = new NpgsqlConnection(_baseConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP SCHEMA IF EXISTS {_schema} CASCADE");
    }

    [Fact]
    public async Task MigrationBackfillsDepartmentsAndPreservesExistingSecurityData()
    {
        if (_scopedConnectionString is null) return;

        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();
        await CreatePreD1SchemaAsync(conn);

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var superAdmin = Guid.NewGuid();
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        var adminA = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var adminB = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        await conn.ExecuteAsync("""
            INSERT INTO tenants(id,code,name,status) VALUES
                (@tenantA,'tenant-a','Tenant A','active'),
                (@tenantB,'tenant-b','Tenant B','active');
            INSERT INTO users(id,email,password_hash,tenant_id,is_active) VALUES
                (@superAdmin,'super@test','x',NULL,true),
                (@ownerA,'owner-a@test','x',@tenantA,true),
                (@ownerB,'owner-b@test','x',@tenantB,true),
                (@adminA,'admin-a@test','x',@tenantA,true),
                (@userA,'user-a@test','x',@tenantA,true),
                (@adminB,'admin-b@test','x',@tenantB,true);
            INSERT INTO user_roles(user_id,role_id)
            SELECT @superAdmin,id FROM roles WHERE name='super_admin'
            UNION ALL SELECT @ownerA,id FROM roles WHERE name='tenant_owner'
            UNION ALL SELECT @ownerB,id FROM roles WHERE name='tenant_owner'
            UNION ALL SELECT @adminA,id FROM roles WHERE name='admin'
            UNION ALL SELECT @userA,id FROM roles WHERE name='user'
            UNION ALL SELECT @adminB,id FROM roles WHERE name='admin';
            INSERT INTO agents(id) VALUES (@agentId);
            INSERT INTO agent_tenant_assignments(agent_id,tenant_id,status)
            VALUES (@agentId,@tenantA,'active');
            INSERT INTO agent_assignments(tenant_id,agent_id,user_id,status)
            VALUES (@tenantA,@agentId,@userA,'active');
            """, new { tenantA, tenantB, superAdmin, ownerA, ownerB, adminA, userA, adminB, agentId });

        // Establish the repository's real pre-D1 owner implementation, then D1.
        await conn.ExecuteAsync(await ReadMigrationAsync("017_tenant_owner_role.sql"));
        await conn.ExecuteAsync(await ReadMigrationAsync("018_tenant_owner_enforcement.sql"));
        var migration = await ReadMigrationAsync("019_departments.sql");
        await conn.ExecuteAsync(migration);

        Assert.True(await conn.ExecuteScalarAsync<bool>(
            "SELECT to_regclass('departments') IS NOT NULL"));
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM departments WHERE is_default AND status='active'"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("""
            SELECT count(*)::int FROM (
                SELECT t.id
                FROM tenants t
                LEFT JOIN departments d ON d.tenant_id=t.id AND d.is_default
                GROUP BY t.id
                HAVING count(d.id) <> 1
            ) anomalies
            """));

        Assert.Null(await DepartmentIdAsync(conn, superAdmin));
        Assert.Null(await DepartmentIdAsync(conn, ownerA));
        Assert.Null(await DepartmentIdAsync(conn, ownerB));
        Assert.NotNull(await DepartmentIdAsync(conn, adminA));
        Assert.NotNull(await DepartmentIdAsync(conn, userA));
        Assert.NotNull(await DepartmentIdAsync(conn, adminB));

        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM agent_tenant_assignments WHERE agent_id=@agentId AND tenant_id=@tenantA AND status='active'",
            new { agentId, tenantA }));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM agent_assignments WHERE agent_id=@agentId AND tenant_id=@tenantA AND user_id=@userA AND status='active'",
            new { agentId, tenantA, userA }));

        // The SQL itself is idempotent as well as DatabaseMigrator applying it once.
        await conn.ExecuteAsync(migration);
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM departments WHERE is_default"));

        var departmentA = (await DepartmentIdAsync(conn, adminA))!.Value;
        var departmentB = (await DepartmentIdAsync(conn, adminB))!.Value;
        await AssertSqlStateAsync(conn,
            "INSERT INTO departments(tenant_id,code,name) VALUES (@tenantA,'default','Other')",
            PostgresErrorCodes.UniqueViolation, new { tenantA });
        await conn.ExecuteAsync(
            "INSERT INTO departments(tenant_id,code,name) VALUES (@tenantB,'finance','Finance')",
            new { tenantB });
        await conn.ExecuteAsync(
            "INSERT INTO departments(tenant_id,code,name) VALUES (@tenantA,'finance','Finance')",
            new { tenantA });
        await AssertSqlStateAsync(conn,
            "INSERT INTO departments(tenant_id,code,name) VALUES (@missingTenant,'x','X')",
            PostgresErrorCodes.ForeignKeyViolation, new { missingTenant = Guid.NewGuid() });
        await AssertSqlStateAsync(conn,
            "UPDATE users SET department_id=@missingDepartment WHERE id=@userA",
            PostgresErrorCodes.ForeignKeyViolation, new { missingDepartment = Guid.NewGuid(), userA });
        await AssertSqlStateAsync(conn,
            "UPDATE users SET department_id=@departmentB WHERE id=@userA",
            PostgresErrorCodes.ForeignKeyViolation, new { departmentB, userA });

        // The original deferred exact-one-owner protection still executes.
        await AssertDeferredSqlStateAsync(conn,
            "UPDATE users SET is_active=false WHERE id=@ownerA",
            PostgresErrorCodes.CheckViolation, new { ownerA });
        Assert.True(await conn.ExecuteScalarAsync<bool>(
            "SELECT is_active FROM users WHERE id=@ownerA", new { ownerA }));

        Assert.NotEqual(departmentA, departmentB);
    }

    private static async Task CreatePreD1SchemaAsync(NpgsqlConnection conn)
    {
        await conn.ExecuteAsync("""
            CREATE TABLE tenants (
                id UUID PRIMARY KEY,
                code TEXT UNIQUE NOT NULL,
                name TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'active',
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE TABLE users (
                id UUID PRIMARY KEY,
                email TEXT UNIQUE NOT NULL,
                password_hash TEXT NOT NULL,
                display_name TEXT,
                is_active BOOLEAN NOT NULL DEFAULT true,
                tenant_id UUID REFERENCES tenants(id) ON DELETE RESTRICT,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE TABLE roles (id SERIAL PRIMARY KEY, name TEXT UNIQUE NOT NULL, description TEXT);
            INSERT INTO roles(name) VALUES ('super_admin'),('tenant_owner'),('admin'),('user');
            CREATE TABLE user_roles (
                user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                role_id INT NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
                PRIMARY KEY(user_id,role_id)
            );
            CREATE UNIQUE INDEX uq_user_single_login_role ON user_roles(user_id);
            CREATE TABLE agents (id UUID PRIMARY KEY);
            CREATE TABLE agent_tenant_assignments (
                agent_id UUID NOT NULL REFERENCES agents(id),
                tenant_id UUID NOT NULL REFERENCES tenants(id),
                status TEXT NOT NULL
            );
            CREATE TABLE agent_assignments (
                tenant_id UUID NOT NULL REFERENCES tenants(id),
                agent_id UUID NOT NULL REFERENCES agents(id),
                user_id UUID NOT NULL REFERENCES users(id),
                status TEXT NOT NULL
            );
            """);
    }

    private static async Task<Guid?> DepartmentIdAsync(NpgsqlConnection conn, Guid userId) =>
        await conn.ExecuteScalarAsync<Guid?>(
            "SELECT department_id FROM users WHERE id=@userId", new { userId });

    private static async Task AssertSqlStateAsync(
        NpgsqlConnection conn, string sql, string sqlState, object parameters)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(
            () => conn.ExecuteAsync(sql, parameters));
        Assert.Equal(sqlState, error.SqlState);
    }

    private static async Task AssertDeferredSqlStateAsync(
        NpgsqlConnection conn, string sql, string sqlState, object parameters)
    {
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync(sql, parameters, tx);
        var error = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
        Assert.Equal(sqlState, error.SqlState);
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
