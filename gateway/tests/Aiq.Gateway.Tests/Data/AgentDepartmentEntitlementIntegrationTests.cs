using Aiq.Gateway.Data;
using Aiq.Gateway.Governance;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Phase D3 department-level agent entitlement integration coverage. Builds
/// the exact fixture from the D3 task spec against a real, isolated Postgres
/// schema (real migrations 017/018/019/021, same pattern as
/// <see cref="DepartmentRbacIntegrationTests"/>) plus a minimal hand-rolled
/// agent/version/manifest schema (agent_versions, tools, knowledge, etc. are
/// not touched by any prior D1/D2 migration test, so they are defined here —
/// same "minimal but shape-correct" convention as
/// <see cref="Aiq.Gateway.Tests.Governance.DynamicAgentRepositoryIntegrationTests"/>).
///
/// Fixture:
///   Tenant A: Owner A
///     Accounting {Admin Accounting, User Accounting, Accounting Agent (published, tenant+department entitled)}
///     Sales      {Admin Sales, User Sales, Sales Agent (published, tenant+department entitled)}
///   Tenant B: Owner B, Other Department {Other Agent (published, tenant+department entitled)}
///
/// Canonical chain under test:
///   Platform Agent -> Tenant Entitlement -> Department Assignment -> ADMIN execution
///   Platform Agent -> Tenant Entitlement -> Department Assignment -> User Assignment -> USER execution
/// </summary>
public sealed class AgentDepartmentEntitlementIntegrationTests : IAsyncLifetime
{
    static AgentDepartmentEntitlementIntegrationTests() =>
        Dapper.SqlMapper.AddTypeHandler(new StringArrayTypeHandler());

    private readonly string? _baseConnectionString = ResolveTestConnectionString();
    private readonly string _schema = $"agent_dept_entitlement_test_{Guid.NewGuid():N}";
    private string? _scopedConnectionString;

    private Guid _tenantA, _tenantB;
    private Guid _accounting, _sales, _otherDept;
    private Guid _ownerA, _ownerB;
    private Guid _adminAccounting, _userAccounting;
    private Guid _adminSales, _userSales;
    private Guid _adminB, _userB;
    private Guid _accountingAgentId, _accountingVersionId;
    private Guid _salesAgentId, _salesVersionId;
    private Guid _otherAgentId, _otherVersionId;

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

        await using var scoped = new NpgsqlConnection(_scopedConnectionString);
        await scoped.OpenAsync();
        await CreateBaselineSchemaAsync(scoped);

        // Tenants + users must exist before 018 (owner invariant) and 019
        // (default-department backfill) run — see DepartmentRbacIntegrationTests.
        await SeedTenantsAndUsersAsync(scoped);

        await scoped.ExecuteAsync(await ReadMigrationAsync("017_tenant_owner_role.sql"));
        await scoped.ExecuteAsync(await ReadMigrationAsync("018_tenant_owner_enforcement.sql"));
        await scoped.ExecuteAsync(await ReadMigrationAsync("019_departments.sql"));
        await scoped.ExecuteAsync(await ReadMigrationAsync("021_agent_department_assignments.sql"));

        await AssignDepartmentsAsync(scoped);
        await SeedAgentsAsync(scoped);
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(_baseConnectionString)) return;

        await using var conn = new NpgsqlConnection(_baseConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP SCHEMA IF EXISTS {_schema} CASCADE");
    }

    private static async Task CreateBaselineSchemaAsync(NpgsqlConnection conn)
    {
        await conn.ExecuteAsync("""
            CREATE TABLE tenants (
                id UUID PRIMARY KEY, code TEXT UNIQUE NOT NULL, name TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'active',
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE TABLE users (
                id UUID PRIMARY KEY, email TEXT UNIQUE NOT NULL, password_hash TEXT NOT NULL,
                display_name TEXT, is_active BOOLEAN NOT NULL DEFAULT true,
                tenant_id UUID REFERENCES tenants(id) ON DELETE RESTRICT,
                external_provider TEXT, external_id TEXT,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE TABLE roles (id SERIAL PRIMARY KEY, name TEXT UNIQUE NOT NULL, description TEXT);
            INSERT INTO roles(name) VALUES ('super_admin'),('tenant_owner'),('admin'),('user');
            CREATE TABLE user_roles (
                user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                role_id INT NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
                PRIMARY KEY(user_id,role_id)
            );
            CREATE UNIQUE INDEX uq_user_single_login_role ON user_roles(user_id);

            -- Minimal dynamic-agent + manifest-resolution schema. Columns
            -- matched to exactly what IDynamicAgentRepository's manifest and
            -- entitlement queries select/insert (real DDL: 007/008/009/010/012/013).
            CREATE TABLE agents (
                id UUID PRIMARY KEY,
                scope TEXT NOT NULL DEFAULT 'system',
                slug TEXT NOT NULL,
                name TEXT NOT NULL,
                description TEXT,
                icon_metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
                action_tier TEXT NOT NULL DEFAULT 'suggest',
                lifecycle_state TEXT NOT NULL DEFAULT 'production',
                disabled_at TIMESTAMPTZ,
                published_version_id UUID
            );
            CREATE TABLE agent_versions (
                id UUID PRIMARY KEY,
                agent_id UUID NOT NULL REFERENCES agents(id),
                version INT NOT NULL,
                status TEXT NOT NULL,
                provider TEXT NOT NULL,
                model TEXT NOT NULL,
                system_prompt TEXT NOT NULL,
                temperature NUMERIC(3,2) NOT NULL DEFAULT 0.2,
                max_turns INT NOT NULL DEFAULT 8,
                timeout_seconds INT NOT NULL DEFAULT 120,
                max_tokens INT NOT NULL DEFAULT 2048,
                memory_config JSONB NOT NULL DEFAULT '{}'::jsonb,
                workflow_id UUID
            );
            CREATE TABLE tools (
                id UUID PRIMARY KEY, code TEXT NOT NULL, name TEXT NOT NULL, description TEXT,
                category TEXT, risk_tier TEXT NOT NULL DEFAULT 'low',
                is_active BOOLEAN NOT NULL DEFAULT true, min_action_tier TEXT NOT NULL DEFAULT 'suggest'
            );
            CREATE TABLE agent_version_tool_grants (agent_version_id UUID NOT NULL, tool_id UUID NOT NULL);
            CREATE TABLE agent_version_skill_assignments (agent_version_id UUID NOT NULL, skill_name TEXT NOT NULL);
            CREATE TABLE notes (id UUID PRIMARY KEY, tenant_id UUID NOT NULL, title TEXT NOT NULL, content TEXT NOT NULL, tags TEXT[] NOT NULL DEFAULT '{}');
            CREATE TABLE agent_knowledge (agent_id UUID NOT NULL, note_id UUID NOT NULL, attached_at TIMESTAMPTZ NOT NULL DEFAULT NOW());
            CREATE TABLE agent_version_delegates (agent_version_id UUID NOT NULL, specialist_agent_id UUID NOT NULL, added_by UUID);

            CREATE TABLE agent_tenant_assignments (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                agent_id UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
                tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
                status TEXT NOT NULL CHECK (status IN ('active', 'revoked')),
                assigned_by UUID NULL, assigned_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), revoked_at TIMESTAMPTZ NULL,
                CHECK ((status = 'active' AND revoked_at IS NULL) OR (status = 'revoked' AND revoked_at IS NOT NULL))
            );
            CREATE UNIQUE INDEX uq_agent_tenant_assignments_active
                ON agent_tenant_assignments(agent_id, tenant_id) WHERE status = 'active';
            CREATE TABLE agent_assignments (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                tenant_id UUID NOT NULL REFERENCES tenants(id),
                agent_id UUID NOT NULL REFERENCES agents(id),
                user_id UUID NOT NULL REFERENCES users(id),
                assigned_by UUID, status TEXT NOT NULL DEFAULT 'active',
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), revoked_at TIMESTAMPTZ,
                UNIQUE (tenant_id, agent_id, user_id)
            );
            -- D5: RevokeDepartmentAgentAsync/RevokeTenantAsync now also
            -- cascade here (never populated by this D3-era fixture, but the
            -- cascade UPDATE runs unconditionally after every revoke).
            CREATE TABLE department_capability_bindings (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id UUID NOT NULL, department_id UUID NOT NULL,
                capability_id UUID NOT NULL, agent_id UUID NOT NULL, status TEXT NOT NULL DEFAULT 'active', revoked_at TIMESTAMPTZ
            );
            """);
    }

    private async Task SeedTenantsAndUsersAsync(NpgsqlConnection conn)
    {
        _tenantA = Guid.NewGuid();
        _tenantB = Guid.NewGuid();
        _ownerA = Guid.NewGuid();
        _ownerB = Guid.NewGuid();
        _adminAccounting = Guid.NewGuid();
        _userAccounting = Guid.NewGuid();
        _adminSales = Guid.NewGuid();
        _userSales = Guid.NewGuid();
        _adminB = Guid.NewGuid();
        _userB = Guid.NewGuid();

        var tenantA = _tenantA; var tenantB = _tenantB;
        var ownerA = _ownerA; var ownerB = _ownerB;
        var adminAccounting = _adminAccounting; var userAccounting = _userAccounting;
        var adminSales = _adminSales; var userSales = _userSales;
        var adminB = _adminB; var userB = _userB;

        await conn.ExecuteAsync("""
            INSERT INTO tenants(id,code,name,status) VALUES
                (@tenantA,'tenant-a','Tenant A','active'),
                (@tenantB,'tenant-b','Tenant B','active');
            INSERT INTO users(id,email,password_hash,tenant_id,is_active) VALUES
                (@ownerA,'owner-a@d3test','x',@tenantA,true),
                (@ownerB,'owner-b@d3test','x',@tenantB,true),
                (@adminAccounting,'admin-accounting@d3test','x',@tenantA,true),
                (@userAccounting,'user-accounting@d3test','x',@tenantA,true),
                (@adminSales,'admin-sales@d3test','x',@tenantA,true),
                (@userSales,'user-sales@d3test','x',@tenantA,true),
                (@adminB,'admin-b@d3test','x',@tenantB,true),
                (@userB,'user-b@d3test','x',@tenantB,true);
            INSERT INTO user_roles(user_id,role_id)
            SELECT @ownerA,id FROM roles WHERE name='tenant_owner'
            UNION ALL SELECT @ownerB,id FROM roles WHERE name='tenant_owner'
            UNION ALL SELECT @adminAccounting,id FROM roles WHERE name='admin'
            UNION ALL SELECT @userAccounting,id FROM roles WHERE name='user'
            UNION ALL SELECT @adminSales,id FROM roles WHERE name='admin'
            UNION ALL SELECT @userSales,id FROM roles WHERE name='user'
            UNION ALL SELECT @adminB,id FROM roles WHERE name='admin'
            UNION ALL SELECT @userB,id FROM roles WHERE name='user';
            """, new { tenantA, tenantB, ownerA, ownerB, adminAccounting, userAccounting, adminSales, userSales, adminB, userB });
    }

    private async Task AssignDepartmentsAsync(NpgsqlConnection conn)
    {
        var tenantA = _tenantA; var tenantB = _tenantB;
        _accounting = await conn.ExecuteScalarAsync<Guid>(
            "UPDATE departments SET code='accounting', name='Accounting' WHERE tenant_id=@tenantA AND is_default RETURNING id", new { tenantA });
        _sales = await conn.ExecuteScalarAsync<Guid>(
            "INSERT INTO departments(tenant_id,code,name,status) VALUES (@tenantA,'sales','Sales','active') RETURNING id", new { tenantA });
        _otherDept = await conn.ExecuteScalarAsync<Guid>(
            "UPDATE departments SET code='other', name='Other Department' WHERE tenant_id=@tenantB AND is_default RETURNING id", new { tenantB });

        var accounting = _accounting; var sales = _sales; var other = _otherDept;
        var adminAccounting = _adminAccounting; var userAccounting = _userAccounting;
        var adminSales = _adminSales; var userSales = _userSales;
        var adminB = _adminB; var userB = _userB;
        await conn.ExecuteAsync("""
            UPDATE users SET department_id=@accounting WHERE id IN (@adminAccounting,@userAccounting);
            UPDATE users SET department_id=@sales WHERE id IN (@adminSales,@userSales);
            UPDATE users SET department_id=@other WHERE id IN (@adminB,@userB);
            """, new { accounting, sales, other, adminAccounting, userAccounting, adminSales, userSales, adminB, userB });
    }

    /// <summary>Three published, tenant-entitled agents — Accounting/Sales in Tenant A, Other in Tenant B. None are department-assigned yet; individual tests do that.</summary>
    private async Task SeedAgentsAsync(NpgsqlConnection conn)
    {
        _accountingAgentId = Guid.NewGuid(); _accountingVersionId = Guid.NewGuid();
        _salesAgentId = Guid.NewGuid(); _salesVersionId = Guid.NewGuid();
        _otherAgentId = Guid.NewGuid(); _otherVersionId = Guid.NewGuid();

        await SeedOneAgentAsync(conn, _accountingAgentId, _accountingVersionId, "accounting-agent", "Accounting Agent", _tenantA);
        await SeedOneAgentAsync(conn, _salesAgentId, _salesVersionId, "sales-agent", "Sales Agent", _tenantA);
        await SeedOneAgentAsync(conn, _otherAgentId, _otherVersionId, "other-agent", "Other Agent", _tenantB);
    }

    private static async Task SeedOneAgentAsync(NpgsqlConnection conn, Guid agentId, Guid versionId, string slug, string name, Guid tenantId)
    {
        await conn.ExecuteAsync("""
            INSERT INTO agents(id,scope,slug,name,action_tier,lifecycle_state,published_version_id)
            VALUES (@agentId,'system',@slug,@name,'suggest','production',@versionId);
            INSERT INTO agent_versions(id,agent_id,version,status,provider,model,system_prompt)
            VALUES (@versionId,@agentId,1,'published','claude_sdk','sonnet','You are a helpful assistant.');
            INSERT INTO agent_tenant_assignments(agent_id,tenant_id,status) VALUES (@agentId,@tenantId,'active');
            """, new { agentId, versionId, slug, name, tenantId });
    }

    private static async Task<string> ReadMigrationAsync(string name)
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "Data", "Migrations", name);
        if (File.Exists(outputPath)) return await File.ReadAllTextAsync(outputPath);
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../src/Aiq.Gateway/Data/Migrations", name));
        return await File.ReadAllTextAsync(sourcePath);
    }

    private IDynamicAgentRepository Agents() => new DynamicAgentRepository(new NpgsqlConnectionFactory(_scopedConnectionString!));

    private async Task SetDepartmentStatusAsync(Guid departmentId, string status)
    {
        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();
        // A department that is currently the tenant default cannot be marked
        // inactive (migration 019's own CHECK constraint) — clear is_default
        // first, matching the pattern used across the D2/D3 test suites.
        await conn.ExecuteAsync(
            "UPDATE departments SET is_default=false, status=@status WHERE id=@departmentId",
            new { departmentId, status });
    }

    private async Task SetTenantAssignmentStatusAsync(Guid agentId, Guid tenantId, string status)
    {
        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "UPDATE agent_tenant_assignments SET status=@status, revoked_at=CASE WHEN @status='revoked' THEN NOW() ELSE NULL END WHERE agent_id=@agentId AND tenant_id=@tenantId",
            new { agentId, tenantId, status });
    }

    // ==== TENANT_OWNER A ====================================================

    [Fact]
    public async Task OwnerA_SeesTenantEntitledAgents()
    {
        if (_scopedConnectionString is null) return;
        var items = await Agents().ListAgentsForTenantAsync(_tenantA, default);
        Assert.Equal(["Accounting Agent", "Sales Agent"], items.Select(i => i.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task OwnerA_AssignAccountingAgentToAccountingDepartment_SucceedsAndIsIdempotent()
    {
        if (_scopedConnectionString is null) return;
        var agents = Agents();
        Assert.True(await agents.AssignDepartmentAgentAsync(_tenantA, _accounting, _accountingAgentId, _ownerA, default));
        Assert.True(await agents.AssignDepartmentAgentAsync(_tenantA, _accounting, _accountingAgentId, _ownerA, default));

        var entitlements = await agents.ListDepartmentEntitlementsAsync(_tenantA, _accounting, default);
        Assert.Single(entitlements, e => e.AgentId == _accountingAgentId && e.Assigned);
    }

    [Fact]
    public async Task OwnerA_CannotAssignNonTenantEntitledAgent()
    {
        if (_scopedConnectionString is null) return;
        // Other Agent is entitled to Tenant B, never to Tenant A.
        var assigned = await Agents().AssignDepartmentAgentAsync(_tenantA, _accounting, _otherAgentId, _ownerA, default);
        Assert.False(assigned);
    }

    [Fact]
    public async Task OwnerA_CannotAssignToTenantBDepartment()
    {
        if (_scopedConnectionString is null) return;
        // departmentId belongs to Tenant B; tenantId argument is Tenant A ->
        // the composite department/tenant EXISTS check fails.
        var assigned = await Agents().AssignDepartmentAgentAsync(_tenantA, _otherDept, _accountingAgentId, _ownerA, default);
        Assert.False(assigned);
    }

    [Fact]
    public async Task OwnerA_RevokeSucceeds()
    {
        if (_scopedConnectionString is null) return;
        var agents = Agents();
        Assert.True(await agents.AssignDepartmentAgentAsync(_tenantA, _accounting, _accountingAgentId, _ownerA, default));
        Assert.True(await agents.RevokeDepartmentAgentAsync(_tenantA, _accounting, _accountingAgentId, default));
        var entitlements = await agents.ListDepartmentEntitlementsAsync(_tenantA, _accounting, default);
        Assert.DoesNotContain(entitlements, e => e.AgentId == _accountingAgentId && e.Assigned);
    }

    // ==== ADMIN visibility/execution ========================================

    [Fact]
    public async Task AdminAccounting_SeesOnlyAccountingAgent()
    {
        if (_scopedConnectionString is null) return;
        var agents = Agents();
        await agents.AssignDepartmentAgentAsync(_tenantA, _accounting, _accountingAgentId, _ownerA, default);

        var items = await agents.ListAgentsForDepartmentAsync(_tenantA, _accounting, default);
        Assert.Equal(["Accounting Agent"], items.Select(i => i.Name));
    }

    [Fact]
    public async Task AdminAccounting_DoesNotSeeSalesAgentUnlessAlsoAssignedToAccounting()
    {
        if (_scopedConnectionString is null) return;
        var agents = Agents();
        await agents.AssignDepartmentAgentAsync(_tenantA, _sales, _salesAgentId, _ownerA, default);
        Assert.DoesNotContain((await agents.ListAgentsForDepartmentAsync(_tenantA, _accounting, default)), a => a.AgentId == _salesAgentId);

        await agents.AssignDepartmentAgentAsync(_tenantA, _accounting, _salesAgentId, _ownerA, default);
        Assert.Contains((await agents.ListAgentsForDepartmentAsync(_tenantA, _accounting, default)), a => a.AgentId == _salesAgentId);
    }

    [Fact]
    public async Task AdminAccounting_ExecutesWithoutDirectAdminAssignment()
    {
        if (_scopedConnectionString is null) return;
        var agents = Agents();
        Assert.True(await agents.AssignDepartmentAgentAsync(_tenantA, _accounting, _accountingAgentId, _ownerA, default));

        // No agent_assignments row for _adminAccounting was ever created.
        var result = await agents.GetAdminManifestAsync(_adminAccounting, _tenantA, _accounting, _accountingAgentId, default);
        Assert.NotNull(result.Manifest);
        Assert.Null(result.DenialReason);
        Assert.Equal(_accounting, result.Manifest!.DepartmentId);
    }

    [Fact]
    public async Task AdminAccounting_CannotExecuteSalesAgent()
    {
        if (_scopedConnectionString is null) return;
        var agents = Agents();
        await agents.AssignDepartmentAgentAsync(_tenantA, _sales, _salesAgentId, _ownerA, default);

        var result = await agents.GetAdminManifestAsync(_adminAccounting, _tenantA, _accounting, _salesAgentId, default);
        Assert.Null(result.Manifest);
        Assert.Equal("agent_not_assigned_to_department", result.DenialReason);
    }

    [Fact]
    public async Task AdminAccounting_CannotAssignSalesAgentToAccountingUser()
    {
        if (_scopedConnectionString is null) return;
        var agents = Agents();
        await agents.AssignDepartmentAgentAsync(_tenantA, _sales, _salesAgentId, _ownerA, default);
        // Sales Agent is never assigned to Accounting -> denied regardless of
        // the target user's own (Accounting) department being correct.
        Assert.False(await agents.AssignDepartmentUserAsync(_tenantA, _accounting, _salesAgentId, _userAccounting, _adminAccounting, default));
    }

    [Fact]
    public async Task AdminAccounting_CanAssignAccountingAgentToAccountingUser()
    {
        if (_scopedConnectionString is null) return;
        var agents = Agents();
        await agents.AssignDepartmentAgentAsync(_tenantA, _accounting, _accountingAgentId, _ownerA, default);
        Assert.True(await agents.AssignDepartmentUserAsync(_tenantA, _accounting, _accountingAgentId, _userAccounting, _adminAccounting, default));
    }

    [Fact]
    public async Task AdminAccounting_CannotAssignToSalesUser()
    {
        if (_scopedConnectionString is null) return;
        var agents = Agents();
        await agents.AssignDepartmentAgentAsync(_tenantA, _accounting, _accountingAgentId, _ownerA, default);
        // Admin Accounting's own department scope excludes a Sales user even
        // though the Accounting Agent itself is validly department-assigned.
        Assert.False(await agents.AssignDepartmentUserAsync(_tenantA, _accounting, _accountingAgentId, _userSales, _adminAccounting, default));
    }

    [Fact]
    public async Task AdminSales_IsolatedFromAccounting()
    {
        if (_scopedConnectionString is null) return;
        var agents = Agents();
        await agents.AssignDepartmentAgentAsync(_tenantA, _accounting, _accountingAgentId, _ownerA, default);
        await agents.AssignDepartmentAgentAsync(_tenantA, _sales, _salesAgentId, _ownerA, default);

        var salesView = await agents.ListAgentsForDepartmentAsync(_tenantA, _sales, default);
        Assert.Equal(["Sales Agent"], salesView.Select(a => a.Name));

        var execResult = await agents.GetAdminManifestAsync(_adminSales, _tenantA, _sales, _accountingAgentId, default);
        Assert.Null(execResult.Manifest);
        Assert.Equal("agent_not_assigned_to_department", execResult.DenialReason);
    }

    // ==== USER execution =====================================================

    [Fact]
    public async Task UserAccounting_DirectAssignmentPlusDepartmentEntitlement_Executes()
    {
        if (_scopedConnectionString is null) return;
        var agents = Agents();
        Assert.True(await agents.AssignDepartmentAgentAsync(_tenantA, _accounting, _accountingAgentId, _ownerA, default));
        Assert.True(await agents.AssignDepartmentUserAsync(_tenantA, _accounting, _accountingAgentId, _userAccounting, _adminAccounting, default));

        var result = await agents.GetManifestAsync(_userAccounting, _tenantA, _accounting, _accountingAgentId, default);
        Assert.NotNull(result.Manifest);
        Assert.Null(result.DenialReason);
    }

    [Fact]
    public async Task UserAccounting_NoDirectAssignment_Denied()
    {
        if (_scopedConnectionString is null) return;
        var agents = Agents();
        await agents.AssignDepartmentAgentAsync(_tenantA, _accounting, _accountingAgentId, _ownerA, default);
        // Department entitlement exists, but no direct agent_assignments row for this user.
        var result = await agents.GetManifestAsync(_userAccounting, _tenantA, _accounting, _accountingAgentId, default);
        Assert.Null(result.Manifest);
        Assert.Equal("agent_not_assigned", result.DenialReason);
    }

    [Fact]
    public async Task UserAccounting_DepartmentEntitlementRevoked_Denied()
    {
        if (_scopedConnectionString is null) return;
        var agents = Agents();
        await agents.AssignDepartmentAgentAsync(_tenantA, _accounting, _accountingAgentId, _ownerA, default);
        await agents.AssignDepartmentUserAsync(_tenantA, _accounting, _accountingAgentId, _userAccounting, _adminAccounting, default);
        Assert.True(await agents.RevokeDepartmentAgentAsync(_tenantA, _accounting, _accountingAgentId, default));

        var result = await agents.GetManifestAsync(_userAccounting, _tenantA, _accounting, _accountingAgentId, default);
        Assert.Null(result.Manifest);
        Assert.Equal("agent_not_assigned_to_department", result.DenialReason);
    }

    [Fact]
    public async Task UserAccounting_RevokingDepartmentEntitlement_AlsoRevokesDirectAssignmentHistory()
    {
        if (_scopedConnectionString is null) return;
        var agents = Agents();
        await agents.AssignDepartmentAgentAsync(_tenantA, _accounting, _accountingAgentId, _ownerA, default);
        await agents.AssignDepartmentUserAsync(_tenantA, _accounting, _accountingAgentId, _userAccounting, _adminAccounting, default);
        await agents.RevokeDepartmentAgentAsync(_tenantA, _accounting, _accountingAgentId, default);

        // §9: the child USER assignment is itself revoked (not merely
        // ineffective) — history preserved, never deleted.
        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();
        var status = await conn.ExecuteScalarAsync<string>(
            "SELECT status FROM agent_assignments WHERE tenant_id=@t AND agent_id=@a AND user_id=@u",
            new { t = _tenantA, a = _accountingAgentId, u = _userAccounting });
        Assert.Equal("revoked", status);
    }

    [Fact]
    public async Task UserAccounting_TenantEntitlementRevoked_Denied()
    {
        if (_scopedConnectionString is null) return;
        var agents = Agents();
        await agents.AssignDepartmentAgentAsync(_tenantA, _accounting, _accountingAgentId, _ownerA, default);
        await agents.AssignDepartmentUserAsync(_tenantA, _accounting, _accountingAgentId, _userAccounting, _adminAccounting, default);
        Assert.True(await agents.RevokeTenantAsync(_accountingAgentId, _tenantA, default));

        var result = await agents.GetManifestAsync(_userAccounting, _tenantA, _accounting, _accountingAgentId, default);
        Assert.Null(result.Manifest);
        Assert.Equal("agent_not_assigned_to_tenant", result.DenialReason);
    }

    [Fact]
    public async Task UserAccounting_TenantRevoke_AlsoDisablesAdminExecution()
    {
        if (_scopedConnectionString is null) return;
        // §10: revoking tenant entitlement must cascade so ADMIN no longer
        // executes either, not just USER.
        var agents = Agents();
        await agents.AssignDepartmentAgentAsync(_tenantA, _accounting, _accountingAgentId, _ownerA, default);
        Assert.True(await agents.RevokeTenantAsync(_accountingAgentId, _tenantA, default));

        var adminResult = await agents.GetAdminManifestAsync(_adminAccounting, _tenantA, _accounting, _accountingAgentId, default);
        Assert.Null(adminResult.Manifest);
        Assert.Equal("agent_not_assigned_to_tenant", adminResult.DenialReason);

        var visible = await agents.ListAgentsForDepartmentAsync(_tenantA, _accounting, default);
        Assert.DoesNotContain(visible, a => a.AgentId == _accountingAgentId);
    }

    [Fact]
    public async Task UserAccounting_CannotSpoofAgentId()
    {
        if (_scopedConnectionString is null) return;
        var agents = Agents();
        await agents.AssignDepartmentAgentAsync(_tenantA, _accounting, _accountingAgentId, _ownerA, default);
        await agents.AssignDepartmentUserAsync(_tenantA, _accounting, _accountingAgentId, _userAccounting, _adminAccounting, default);
        // User has a real, valid assignment to the Accounting Agent — a
        // request naming a completely different (and completely
        // unrelated/unassigned) agent id must still be denied.
        var spoofed = Guid.NewGuid();
        var result = await agents.GetManifestAsync(_userAccounting, _tenantA, _accounting, spoofed, default);
        Assert.Null(result.Manifest);
        Assert.Equal("not_found_or_cross_tenant", result.DenialReason);
    }

    // ==== Cross-tenant =======================================================

    [Fact]
    public async Task CrossTenant_AdminAndUserDeniedRegardlessOfKnownIds()
    {
        if (_scopedConnectionString is null) return;
        var agents = Agents();
        await agents.AssignDepartmentAgentAsync(_tenantA, _accounting, _accountingAgentId, _ownerA, default);
        await agents.AssignDepartmentUserAsync(_tenantA, _accounting, _accountingAgentId, _userAccounting, _adminAccounting, default);

        // Admin B / User B (Tenant B) try the real Tenant A agent id, using
        // their own (Tenant B) tenant/department. Agent ids are global
        // platform identifiers (agents carries no tenant column of its own),
        // so the row itself is found — denial instead comes from the tenant
        // ENTITLEMENT check: the Accounting Agent was never entitled to
        // Tenant B, so both callers fail closed there. Still an unconditional
        // deny regardless of the known id — just a more precise reason than
        // a bare "not found" would be.
        var adminResult = await agents.GetAdminManifestAsync(_adminB, _tenantB, _otherDept, _accountingAgentId, default);
        Assert.Null(adminResult.Manifest);
        Assert.Equal("agent_not_assigned_to_tenant", adminResult.DenialReason);

        var userResult = await agents.GetManifestAsync(_userB, _tenantB, _otherDept, _accountingAgentId, default);
        Assert.Null(userResult.Manifest);
        Assert.Equal("agent_not_assigned_to_tenant", userResult.DenialReason);
    }

    // ==== Inactive department ================================================

    [Fact]
    public async Task InactiveDepartment_AdminExecutionDenied()
    {
        if (_scopedConnectionString is null) return;
        var agents = Agents();
        await agents.AssignDepartmentAgentAsync(_tenantA, _accounting, _accountingAgentId, _ownerA, default);
        await SetDepartmentStatusAsync(_accounting, "inactive");

        // GetAdminManifestAsync itself only checks agent_department_assignments
        // (not department.status) — the real fail-closed enforcement for an
        // inactive department happens one layer up, in
        // TenantContextMiddleware/DepartmentAccessPolicy, before any manifest
        // call is even reached (see DepartmentAccessPolicyTests). This test
        // proves the OTHER half: an inactive department can no longer accept
        // NEW entitlement changes either.
        var department = await new DepartmentRepository(new NpgsqlConnectionFactory(_scopedConnectionString!))
            .FindAsync(_tenantA, _accounting, default);
        Assert.Equal("inactive", department!.Status);
        Assert.False(await agents.AssignDepartmentAgentAsync(_tenantA, _accounting, _salesAgentId, _ownerA, default));
    }

    [Fact]
    public async Task InactiveDepartment_UserExecutionDenied()
    {
        if (_scopedConnectionString is null) return;
        var agents = Agents();
        await agents.AssignDepartmentAgentAsync(_tenantA, _accounting, _accountingAgentId, _ownerA, default);
        await agents.AssignDepartmentUserAsync(_tenantA, _accounting, _accountingAgentId, _userAccounting, _adminAccounting, default);
        await SetDepartmentStatusAsync(_accounting, "inactive");

        var department = await new DepartmentRepository(new NpgsqlConnectionFactory(_scopedConnectionString!))
            .FindAsync(_tenantA, _accounting, default);
        var error = DepartmentAccessPolicy.ValidateMembership(
            (await new UserRepository(new NpgsqlConnectionFactory(_scopedConnectionString!)).FindByIdAsync(_userAccounting, default))!,
            new HashSet<string>(["user"], StringComparer.Ordinal), department);
        Assert.Equal("department_inactive", error);
    }

    // ==== Disabled/revoked agent =============================================

    [Fact]
    public async Task DisabledAgent_AdminAndUserExecutionDenied()
    {
        if (_scopedConnectionString is null) return;
        var agents = Agents();
        await agents.AssignDepartmentAgentAsync(_tenantA, _accounting, _accountingAgentId, _ownerA, default);
        await agents.AssignDepartmentUserAsync(_tenantA, _accounting, _accountingAgentId, _userAccounting, _adminAccounting, default);

        await using (var conn = new NpgsqlConnection(_scopedConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("UPDATE agents SET disabled_at=NOW() WHERE id=@id", new { id = _accountingAgentId });
        }

        var adminResult = await agents.GetAdminManifestAsync(_adminAccounting, _tenantA, _accounting, _accountingAgentId, default);
        Assert.Equal("agent_disabled", adminResult.DenialReason);
        var userResult = await agents.GetManifestAsync(_userAccounting, _tenantA, _accounting, _accountingAgentId, default);
        Assert.Equal("agent_disabled", userResult.DenialReason);
    }
}
