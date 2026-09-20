using Aiq.Gateway.Auth;
using Aiq.Gateway.Data;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Password change/reset behaviour against a real Postgres instance — a
/// throwaway schema mirroring just the columns UserRepository's password
/// methods touch (users/roles/user_roles), same isolation pattern as
/// AclV2AuthorizationScopeIntegrationTests. Skips (returns) when no test
/// database is configured, same as that class.
/// </summary>
public sealed class UserRepositoryPasswordIntegrationTests : IAsyncLifetime
{
    private readonly string? _baseConnectionString = ResolveTestConnectionString();
    private readonly string _schema = $"user_pw_test_{Guid.NewGuid():N}";
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
            CREATE EXTENSION IF NOT EXISTS pgcrypto;
            CREATE TABLE tenants (id UUID PRIMARY KEY, code TEXT, status TEXT);
            CREATE TABLE departments (id UUID PRIMARY KEY, tenant_id UUID, status TEXT);
            CREATE TABLE users (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                email TEXT UNIQUE NOT NULL,
                password_hash TEXT NOT NULL,
                display_name TEXT,
                is_active BOOLEAN NOT NULL DEFAULT TRUE,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                tenant_id UUID,
                department_id UUID,
                external_provider TEXT,
                external_id TEXT,
                must_change_password BOOLEAN NOT NULL DEFAULT FALSE
            );
            CREATE TABLE roles (id SERIAL PRIMARY KEY, name TEXT UNIQUE NOT NULL, description TEXT);
            CREATE TABLE user_roles (
                user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                role_id INT NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
                PRIMARY KEY (user_id, role_id)
            );
            INSERT INTO roles (name) VALUES ('super_admin'), ('admin'), ('user'), ('tenant_owner');
            """);
    }

    public async Task DisposeAsync()
    {
        if (_baseConnectionString is null) return;
        await using var conn = new NpgsqlConnection(_baseConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP SCHEMA IF EXISTS {_schema} CASCADE");
    }

    private async Task<Guid> InsertUserAsync(
        NpgsqlConnection conn, string email, string role, Guid? tenantId = null, Guid? departmentId = null)
    {
        var id = await conn.ExecuteScalarAsync<Guid>("""
            INSERT INTO users (email, password_hash, tenant_id, department_id)
            VALUES (@email, @hash, @tenantId, @departmentId)
            RETURNING id;
            """, new { email, hash = new BcryptPasswordHasher().Hash("OldPassword123!"), tenantId, departmentId });
        await conn.ExecuteAsync("""
            INSERT INTO user_roles (user_id, role_id)
            SELECT @id, r.id FROM roles r WHERE r.name = @role;
            """, new { id, role });
        return id;
    }

    [Fact]
    public async Task SetPasswordHashAsync_UpdatesHashAndMustChangeFlag()
    {
        if (_connectionString is null) return;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        var userId = await InsertUserAsync(conn, "u1@test.local", "user");

        var repo = new UserRepository(new NpgsqlConnectionFactory(_connectionString));
        var hasher = new BcryptPasswordHasher();
        var newHash = hasher.Hash("NewPassword456!");

        var ok = await repo.SetPasswordHashAsync(userId, newHash, mustChangePassword: true, default);
        Assert.True(ok);

        var reloaded = await repo.FindByIdAsync(userId, default);
        Assert.NotNull(reloaded);
        Assert.True(reloaded!.MustChangePassword);
        // Old password must no longer verify; new one must.
        Assert.False(hasher.Verify("OldPassword123!", reloaded.PasswordHash));
        Assert.True(hasher.Verify("NewPassword456!", reloaded.PasswordHash));
    }

    [Fact]
    public async Task SetDepartmentUserPasswordAsync_SucceedsForRoleUserInSameDepartment()
    {
        if (_connectionString is null) return;
        var tenantId = Guid.NewGuid();
        var departmentId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        var userId = await InsertUserAsync(conn, "dept-user@test.local", "user", tenantId, departmentId);

        var repo = new UserRepository(new NpgsqlConnectionFactory(_connectionString));
        var hash = new BcryptPasswordHasher().Hash("NewPassword456!");
        var ok = await repo.SetDepartmentUserPasswordAsync(tenantId, departmentId, userId, hash, true, default);

        Assert.True(ok);
    }

    [Fact]
    public async Task SetDepartmentUserPasswordAsync_RejectsWrongDepartment()
    {
        if (_connectionString is null) return;
        var tenantId = Guid.NewGuid();
        var departmentId = Guid.NewGuid();
        var otherDepartmentId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        var userId = await InsertUserAsync(conn, "dept-user2@test.local", "user", tenantId, departmentId);

        var repo = new UserRepository(new NpgsqlConnectionFactory(_connectionString));
        var hash = new BcryptPasswordHasher().Hash("NewPassword456!");
        // Requirement: a department-scoped ADMIN must never reset a user
        // outside their own department, even within the same tenant.
        var ok = await repo.SetDepartmentUserPasswordAsync(tenantId, otherDepartmentId, userId, hash, true, default);

        Assert.False(ok);
    }

    [Fact]
    public async Task SetDepartmentUserPasswordAsync_RejectsWrongTenant()
    {
        if (_connectionString is null) return;
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var departmentId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        var userId = await InsertUserAsync(conn, "dept-user3@test.local", "user", tenantId, departmentId);

        var repo = new UserRepository(new NpgsqlConnectionFactory(_connectionString));
        var hash = new BcryptPasswordHasher().Hash("NewPassword456!");
        // Requirement: never allow cross-tenant reset.
        var ok = await repo.SetDepartmentUserPasswordAsync(otherTenantId, departmentId, userId, hash, true, default);

        Assert.False(ok);
    }

    [Fact]
    public async Task SetDepartmentUserPasswordAsync_RejectsNonUserRole()
    {
        if (_connectionString is null) return;
        var tenantId = Guid.NewGuid();
        var departmentId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        // An admin account sitting in the same tenant/department must still
        // never be reachable through the department-scoped user setter.
        var adminId = await InsertUserAsync(conn, "dept-admin@test.local", "admin", tenantId, departmentId);

        var repo = new UserRepository(new NpgsqlConnectionFactory(_connectionString));
        var hash = new BcryptPasswordHasher().Hash("NewPassword456!");
        var ok = await repo.SetDepartmentUserPasswordAsync(tenantId, departmentId, adminId, hash, true, default);

        Assert.False(ok);
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
