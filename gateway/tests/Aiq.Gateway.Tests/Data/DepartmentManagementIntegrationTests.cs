using Aiq.Gateway.Data;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Phase D5: department CRUD (create/rename/deactivate/activate,
/// deactivation-blocker checks) and the transactional department move
/// (IUserRepository.MoveDepartmentAsync). Fixture mirrors D3/D4's own
/// (Tenant A: Owner A, Accounting {Admin, User}, Sales {Admin, User};
/// Tenant B: Owner B, Other) plus one published, tenant+department-entitled
/// Accounting Agent so the deactivation-blocker and move-cleanup tests have
/// something real to depend on / clean up.
/// </summary>
public sealed class DepartmentManagementIntegrationTests : IAsyncLifetime
{
    private readonly string? _baseConnectionString = ResolveTestConnectionString();
    private readonly string _schema = $"department_mgmt_test_{Guid.NewGuid():N}";
    private string? _scopedConnectionString;

    private Guid _tenantA, _tenantB;
    private Guid _accounting, _sales, _otherDept;
    private Guid _adminAccounting, _userAccounting, _adminSales, _userSales;
    private Guid _accountingAgentId, _accountingVersionId;
    private Guid _journalCapabilityId;

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
        await SeedTenantsAndUsersAsync(scoped);

        await scoped.ExecuteAsync(await ReadMigrationAsync("017_tenant_owner_role.sql"));
        await scoped.ExecuteAsync(await ReadMigrationAsync("018_tenant_owner_enforcement.sql"));
        await scoped.ExecuteAsync(await ReadMigrationAsync("019_departments.sql"));
        await scoped.ExecuteAsync(await ReadMigrationAsync("021_agent_department_assignments.sql"));
        await scoped.ExecuteAsync(await ReadMigrationAsync("022_business_capabilities.sql"));

        await AssignDepartmentsAsync(scoped);
        await SeedAgentAsync(scoped);
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

            CREATE TABLE agents (
                id UUID PRIMARY KEY, scope TEXT NOT NULL DEFAULT 'system', slug TEXT NOT NULL, name TEXT NOT NULL,
                description TEXT, icon_metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
                action_tier TEXT NOT NULL DEFAULT 'suggest', lifecycle_state TEXT NOT NULL DEFAULT 'production',
                disabled_at TIMESTAMPTZ, published_version_id UUID
            );
            CREATE TABLE agent_versions (
                id UUID PRIMARY KEY, agent_id UUID NOT NULL REFERENCES agents(id), version INT NOT NULL,
                status TEXT NOT NULL, provider TEXT NOT NULL, model TEXT NOT NULL, system_prompt TEXT NOT NULL,
                temperature NUMERIC(3,2) NOT NULL DEFAULT 0.2, max_turns INT NOT NULL DEFAULT 8,
                timeout_seconds INT NOT NULL DEFAULT 120, max_tokens INT NOT NULL DEFAULT 2048,
                memory_config JSONB NOT NULL DEFAULT '{}'::jsonb, workflow_id UUID
            );
            CREATE TABLE agent_version_capabilities (agent_version_id UUID NOT NULL, capability_id UUID NOT NULL, assigned_by UUID, assigned_at TIMESTAMPTZ NOT NULL DEFAULT NOW());
            CREATE OR REPLACE FUNCTION protect_published_agent_capability()
            RETURNS TRIGGER AS $$
            DECLARE target_version UUID; version_status TEXT;
            BEGIN
                target_version := CASE WHEN TG_OP = 'DELETE' THEN OLD.agent_version_id ELSE NEW.agent_version_id END;
                SELECT status INTO version_status FROM agent_versions WHERE id = target_version;
                IF version_status <> 'draft' THEN
                    RAISE EXCEPTION 'published or superseded agent version capabilities are immutable' USING ERRCODE = '23514';
                END IF;
                RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
            END;
            $$ LANGUAGE plpgsql;

            CREATE TABLE agent_tenant_assignments (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), agent_id UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
                tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
                status TEXT NOT NULL CHECK (status IN ('active', 'revoked')),
                assigned_by UUID NULL, assigned_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), revoked_at TIMESTAMPTZ NULL,
                CHECK ((status = 'active' AND revoked_at IS NULL) OR (status = 'revoked' AND revoked_at IS NOT NULL))
            );
            CREATE UNIQUE INDEX uq_agent_tenant_assignments_active ON agent_tenant_assignments(agent_id, tenant_id) WHERE status = 'active';
            CREATE TABLE agent_assignments (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id UUID NOT NULL REFERENCES tenants(id),
                agent_id UUID NOT NULL REFERENCES agents(id), user_id UUID NOT NULL REFERENCES users(id),
                assigned_by UUID, status TEXT NOT NULL DEFAULT 'active',
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), revoked_at TIMESTAMPTZ,
                UNIQUE (tenant_id, agent_id, user_id)
            );
            """);
    }

    private async Task SeedTenantsAndUsersAsync(NpgsqlConnection conn)
    {
        _tenantA = Guid.NewGuid(); _tenantB = Guid.NewGuid();
        var ownerA = Guid.NewGuid(); var ownerB = Guid.NewGuid();
        _adminAccounting = Guid.NewGuid(); _userAccounting = Guid.NewGuid();
        _adminSales = Guid.NewGuid(); _userSales = Guid.NewGuid();

        var tenantA = _tenantA; var tenantB = _tenantB;
        var adminAccounting = _adminAccounting; var userAccounting = _userAccounting;
        var adminSales = _adminSales; var userSales = _userSales;

        await conn.ExecuteAsync("""
            INSERT INTO tenants(id,code,name,status) VALUES
                (@tenantA,'tenant-a','Tenant A','active'), (@tenantB,'tenant-b','Tenant B','active');
            INSERT INTO users(id,email,password_hash,tenant_id,is_active) VALUES
                (@ownerA,'owner-a@d5test','x',@tenantA,true), (@ownerB,'owner-b@d5test','x',@tenantB,true),
                (@adminAccounting,'admin-accounting@d5test','x',@tenantA,true),
                (@userAccounting,'user-accounting@d5test','x',@tenantA,true),
                (@adminSales,'admin-sales@d5test','x',@tenantA,true),
                (@userSales,'user-sales@d5test','x',@tenantA,true);
            INSERT INTO user_roles(user_id,role_id)
            SELECT @ownerA,id FROM roles WHERE name='tenant_owner'
            UNION ALL SELECT @ownerB,id FROM roles WHERE name='tenant_owner'
            UNION ALL SELECT @adminAccounting,id FROM roles WHERE name='admin'
            UNION ALL SELECT @userAccounting,id FROM roles WHERE name='user'
            UNION ALL SELECT @adminSales,id FROM roles WHERE name='admin'
            UNION ALL SELECT @userSales,id FROM roles WHERE name='user';
            """, new { tenantA, tenantB, ownerA, ownerB, adminAccounting, userAccounting, adminSales, userSales });
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

        var accounting = _accounting; var sales = _sales;
        var adminAccounting = _adminAccounting; var userAccounting = _userAccounting;
        var adminSales = _adminSales; var userSales = _userSales;
        await conn.ExecuteAsync("""
            UPDATE users SET department_id=@accounting WHERE id IN (@adminAccounting,@userAccounting);
            UPDATE users SET department_id=@sales WHERE id IN (@adminSales,@userSales);
            """, new { accounting, sales, adminAccounting, userAccounting, adminSales, userSales });
    }

    private async Task SeedAgentAsync(NpgsqlConnection conn)
    {
        _accountingAgentId = Guid.NewGuid(); _accountingVersionId = Guid.NewGuid();
        var agentId = _accountingAgentId; var versionId = _accountingVersionId; var tenantA = _tenantA;
        _journalCapabilityId = await conn.ExecuteScalarAsync<Guid>("SELECT id FROM business_capabilities WHERE code='accounting.journal'");
        var journal = _journalCapabilityId;

        await conn.ExecuteAsync("""
            INSERT INTO agents(id,scope,slug,name,action_tier,lifecycle_state,published_version_id)
            VALUES (@agentId,'system','accounting-agent','Accounting Agent','suggest','production',@versionId);
            INSERT INTO agent_versions(id,agent_id,version,status,provider,model,system_prompt)
            VALUES (@versionId,@agentId,1,'draft','claude_sdk','sonnet','You are a helpful assistant.');
            INSERT INTO agent_version_capabilities(agent_version_id,capability_id) VALUES (@versionId,@journal);
            UPDATE agent_versions SET status='published' WHERE id=@versionId;
            INSERT INTO agent_tenant_assignments(agent_id,tenant_id,status) VALUES (@agentId,@tenantA,'active');
            """, new { agentId, versionId, tenantA, journal });
    }

    private static async Task<string> ReadMigrationAsync(string name)
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "Data", "Migrations", name);
        if (File.Exists(outputPath)) return await File.ReadAllTextAsync(outputPath);
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../src/Aiq.Gateway/Data/Migrations", name));
        return await File.ReadAllTextAsync(sourcePath);
    }

    private IDepartmentRepository Departments() => new DepartmentRepository(new NpgsqlConnectionFactory(_scopedConnectionString!));
    private IUserRepository Users() => new UserRepository(new NpgsqlConnectionFactory(_scopedConnectionString!));
    private Aiq.Gateway.Governance.IDynamicAgentRepository DynamicAgents() => new Aiq.Gateway.Governance.DynamicAgentRepository(new NpgsqlConnectionFactory(_scopedConnectionString!));
    private Aiq.Gateway.Governance.ICapabilityRepository Agents() => new Aiq.Gateway.Governance.CapabilityRepository(new NpgsqlConnectionFactory(_scopedConnectionString!));

    private async Task AssignAgentToDepartmentAsync(Guid departmentId)
    {
        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "INSERT INTO agent_department_assignments(tenant_id,department_id,agent_id,status) VALUES (@t,@d,@a,'active')",
            new { t = _tenantA, d = departmentId, a = _accountingAgentId });
    }

    private async Task AssignUserToAgentAsync(Guid userId)
    {
        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "INSERT INTO agent_assignments(tenant_id,agent_id,user_id,status) VALUES (@t,@a,@u,'active')",
            new { t = _tenantA, a = _accountingAgentId, u = userId });
    }

    private async Task<string> AssignmentStatusAsync(Guid userId)
    {
        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();
        return (await conn.ExecuteScalarAsync<string>(
            "SELECT status FROM agent_assignments WHERE user_id=@u AND agent_id=@a",
            new { u = userId, a = _accountingAgentId }))!;
    }

    // ==== Department CRUD ====================================================

    [Fact]
    public async Task CreateDepartment_Succeeds()
    {
        if (_scopedConnectionString is null) return;
        var created = await Departments().CreateAsync(_tenantA, "support", "Support", default);
        Assert.Equal("support", created.Code);
        Assert.False(created.IsDefault);
        Assert.Equal("active", created.Status);
    }

    [Fact]
    public async Task CreateDepartment_DuplicateCodeRejected()
    {
        if (_scopedConnectionString is null) return;
        await Departments().CreateAsync(_tenantA, "support", "Support", default);
        await Assert.ThrowsAsync<PostgresException>(() =>
            Departments().CreateAsync(_tenantA, "support", "Support Team", default));
    }

    [Fact]
    public async Task CreateDepartment_SameCodeDifferentTenant_Allowed()
    {
        if (_scopedConnectionString is null) return;
        await Departments().CreateAsync(_tenantA, "support", "Support", default);
        var created = await Departments().CreateAsync(_tenantB, "support", "Support", default);
        Assert.Equal("support", created.Code);
    }

    [Fact]
    public async Task UpdateDepartment_RenameSucceeds()
    {
        if (_scopedConnectionString is null) return;
        var created = await Departments().CreateAsync(_tenantA, "support", "Support", default);
        var renamed = await Departments().UpdateNameAsync(_tenantA, created.Id, "Customer Support", default);
        Assert.Equal("Customer Support", renamed!.Name);
    }

    [Fact]
    public async Task UpdateDepartment_CrossTenantDenied()
    {
        if (_scopedConnectionString is null) return;
        var created = await Departments().CreateAsync(_tenantA, "support", "Support", default);
        var result = await Departments().UpdateNameAsync(_tenantB, created.Id, "Hijacked", default);
        Assert.Null(result);
    }

    [Fact]
    public async Task ListActive_DoesNotLeakAcrossTenants()
    {
        if (_scopedConnectionString is null) return;
        var tenantAList = await Departments().ListActiveAsync(_tenantA, default);
        Assert.DoesNotContain(tenantAList, d => d.Id == _otherDept);
        Assert.Contains(tenantAList, d => d.Id == _accounting);
    }

    // ==== Deactivation blockers ==============================================

    [Fact]
    public async Task Deactivate_BlockedByActiveMembers()
    {
        if (_scopedConnectionString is null) return;
        var blocker = await Departments().FindDeactivationBlockerAsync(_tenantA, _sales, default);
        Assert.Equal("active_members_remain", blocker);
    }

    [Fact]
    public async Task Deactivate_BlockedByActiveAgentEntitlement()
    {
        if (_scopedConnectionString is null) return;
        var empty = await Departments().CreateAsync(_tenantA, "empty", "Empty", default);
        await AssignAgentToDepartmentAsync(empty.Id);
        var blocker = await Departments().FindDeactivationBlockerAsync(_tenantA, empty.Id, default);
        Assert.Equal("active_agent_entitlements_remain", blocker);
    }

    [Fact]
    public async Task Deactivate_BlockedByActiveAgentEntitlement_EvenWhenCapabilityAlsoBound()
    {
        if (_scopedConnectionString is null) return;
        // Reachable-through-the-real-API scenario: a capability binding can
        // only ever be created while its agent-department entitlement is
        // active (the binding's own DB trigger requires it), so both are
        // active here — FindDeactivationBlockerAsync's fixed check order
        // (members, then agents, then capabilities) reports the agent
        // entitlement first.
        var empty = await Departments().CreateAsync(_tenantA, "empty", "Empty", default);
        await AssignAgentToDepartmentAsync(empty.Id);
        await Agents().SetBindingAsync(_tenantA, empty.Id, _journalCapabilityId, _accountingAgentId, _adminAccounting, default);
        var blocker = await Departments().FindDeactivationBlockerAsync(_tenantA, empty.Id, default);
        Assert.Equal("active_agent_entitlements_remain", blocker);
    }

    [Fact]
    public async Task RevokeDepartmentAgent_CascadesToCapabilityBinding_NoDanglingState()
    {
        if (_scopedConnectionString is null) return;
        // D5 fix: RevokeDepartmentAgentAsync predates D4's capability
        // bindings — without this cascade, revoking the department
        // entitlement here would leave an orphaned active binding pointing
        // at an agent no longer entitled to the department (exactly the
        // "dangling capability binding" §5 requires be prevented).
        var empty = await Departments().CreateAsync(_tenantA, "empty", "Empty", default);
        await AssignAgentToDepartmentAsync(empty.Id);
        Assert.True(await Agents().SetBindingAsync(_tenantA, empty.Id, _journalCapabilityId, _accountingAgentId, _adminAccounting, default));

        Assert.True(await DynamicAgents().RevokeDepartmentAgentAsync(_tenantA, empty.Id, _accountingAgentId, default));

        var blocker = await Departments().FindDeactivationBlockerAsync(_tenantA, empty.Id, default);
        Assert.Null(blocker); // no active agent entitlement AND no active binding left behind
        var bindingStatus = await BindingStatusAsync(empty.Id);
        Assert.Equal("revoked", bindingStatus);
    }

    [Fact]
    public async Task RevokeTenant_CascadesToCapabilityBinding_NoDanglingState()
    {
        if (_scopedConnectionString is null) return;
        await AssignAgentToDepartmentAsync(_accounting);
        Assert.True(await Agents().SetBindingAsync(_tenantA, _accounting, _journalCapabilityId, _accountingAgentId, _adminAccounting, default));

        Assert.True(await DynamicAgents().RevokeTenantAsync(_accountingAgentId, _tenantA, default));

        Assert.Equal("revoked", await BindingStatusAsync(_accounting));
    }

    [Fact]
    public async Task Deactivate_DefenseInDepth_BlockedByOrphanedCapabilityBinding()
    {
        if (_scopedConnectionString is null) return;
        // Hypothetical inconsistent state reachable only by bypassing the
        // repository (e.g. a future direct-SQL write path) — proves
        // FindDeactivationBlockerAsync's capability-binding check is real
        // defense in depth, not dead code, independent of the cascade fix
        // above.
        var empty = await Departments().CreateAsync(_tenantA, "empty", "Empty", default);
        await AssignAgentToDepartmentAsync(empty.Id);
        Assert.True(await Agents().SetBindingAsync(_tenantA, empty.Id, _journalCapabilityId, _accountingAgentId, _adminAccounting, default));
        await using (var conn = new NpgsqlConnection(_scopedConnectionString))
        {
            await conn.OpenAsync();
            // Revoke the entitlement WITHOUT going through RevokeDepartmentAgentAsync.
            await conn.ExecuteAsync(
                "UPDATE agent_department_assignments SET status='revoked', revoked_at=NOW() WHERE tenant_id=@t AND department_id=@d AND agent_id=@a",
                new { t = _tenantA, d = empty.Id, a = _accountingAgentId });
        }
        var blocker = await Departments().FindDeactivationBlockerAsync(_tenantA, empty.Id, default);
        Assert.Equal("active_capability_bindings_remain", blocker);
    }

    private async Task<string> BindingStatusAsync(Guid departmentId)
    {
        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();
        return (await conn.ExecuteScalarAsync<string>(
            "SELECT status FROM department_capability_bindings WHERE tenant_id=@t AND department_id=@d AND capability_id=@c AND agent_id=@a",
            new { t = _tenantA, d = departmentId, c = _journalCapabilityId, a = _accountingAgentId }))!;
    }

    [Fact]
    public async Task Deactivate_CleanDepartmentSucceeds()
    {
        if (_scopedConnectionString is null) return;
        var empty = await Departments().CreateAsync(_tenantA, "empty", "Empty", default);
        var blocker = await Departments().FindDeactivationBlockerAsync(_tenantA, empty.Id, default);
        Assert.Null(blocker);
        var deactivated = await Departments().SetStatusAsync(_tenantA, empty.Id, "inactive", default);
        Assert.Equal("inactive", deactivated!.Status);
    }

    [Fact]
    public async Task Deactivate_DefaultDepartmentAlwaysFails_EvenWhenEmpty()
    {
        if (_scopedConnectionString is null) return;
        // Move both Accounting members out first so the department itself has
        // no member/entitlement blocker — the DB's own CHECK constraint must
        // still refuse to deactivate the default department.
        await Users().MoveDepartmentAsync(_tenantA, _adminAccounting, _sales, default);
        await Users().MoveDepartmentAsync(_tenantA, _userAccounting, _sales, default);
        var blocker = await Departments().FindDeactivationBlockerAsync(_tenantA, _accounting, default);
        Assert.Null(blocker);
        await Assert.ThrowsAsync<PostgresException>(() =>
            Departments().SetStatusAsync(_tenantA, _accounting, "inactive", default));
    }

    [Fact]
    public async Task Activate_RestoresStatus()
    {
        if (_scopedConnectionString is null) return;
        var empty = await Departments().CreateAsync(_tenantA, "empty", "Empty", default);
        await Departments().SetStatusAsync(_tenantA, empty.Id, "inactive", default);
        var reactivated = await Departments().SetStatusAsync(_tenantA, empty.Id, "active", default);
        Assert.Equal("active", reactivated!.Status);
    }

    // ==== Department move =====================================================

    [Fact]
    public async Task MoveUser_ToInactiveDepartment_Fails()
    {
        if (_scopedConnectionString is null) return;
        var inactive = await Departments().CreateAsync(_tenantA, "empty", "Empty", default);
        await Departments().SetStatusAsync(_tenantA, inactive.Id, "inactive", default);
        var ok = await Users().MoveDepartmentAsync(_tenantA, _userAccounting, inactive.Id, default);
        Assert.False(ok);
    }

    [Fact]
    public async Task MoveUser_CrossTenantDestinationDenied()
    {
        if (_scopedConnectionString is null) return;
        var ok = await Users().MoveDepartmentAsync(_tenantA, _userAccounting, _otherDept, default);
        Assert.False(ok);
        var stillAccounting = await Departments().FindAsync(_tenantA, _accounting, default);
        Assert.NotNull(stillAccounting);
    }

    [Fact]
    public async Task MoveUser_WrongCallerTenantDenied()
    {
        if (_scopedConnectionString is null) return;
        // _userAccounting belongs to Tenant A; calling with Tenant B as the
        // caller-supplied tenant must not be able to move a Tenant A user.
        var ok = await Users().MoveDepartmentAsync(_tenantB, _userAccounting, _sales, default);
        Assert.False(ok);
    }

    [Fact]
    public async Task MoveUser_RevokesAssignmentIneffectiveInDestination_KeepsEffectiveOnes()
    {
        if (_scopedConnectionString is null) return;
        // Accounting Agent is entitled to Accounting only (not Sales).
        await AssignAgentToDepartmentAsync(_accounting);
        await AssignUserToAgentAsync(_userAccounting);
        Assert.Equal("active", await AssignmentStatusAsync(_userAccounting));

        var ok = await Users().MoveDepartmentAsync(_tenantA, _userAccounting, _sales, default);
        Assert.True(ok);
        Assert.Equal("revoked", await AssignmentStatusAsync(_userAccounting));

        var moved = await Users().FindByIdAsync(_userAccounting, default);
        Assert.Equal(_sales, moved!.DepartmentId);
    }

    [Fact]
    public async Task MoveUser_AssignmentPreservedWhenAgentAlsoEntitledToDestination()
    {
        if (_scopedConnectionString is null) return;
        // Accounting Agent entitled to BOTH departments this time.
        await AssignAgentToDepartmentAsync(_accounting);
        await AssignAgentToDepartmentAsync(_sales);
        await AssignUserToAgentAsync(_userAccounting);

        await Users().MoveDepartmentAsync(_tenantA, _userAccounting, _sales, default);
        Assert.Equal("active", await AssignmentStatusAsync(_userAccounting));
    }

    [Fact]
    public async Task MoveAdmin_JustChangesDepartment()
    {
        if (_scopedConnectionString is null) return;
        var ok = await Users().MoveDepartmentAsync(_tenantA, _adminAccounting, _sales, default);
        Assert.True(ok);
        var moved = await Users().FindByIdAsync(_adminAccounting, default);
        Assert.Equal(_sales, moved!.DepartmentId);
    }

    [Fact]
    public async Task MoveUser_Transactional_NoPartialStateOnFailure()
    {
        if (_scopedConnectionString is null) return;
        await AssignAgentToDepartmentAsync(_accounting);
        await AssignUserToAgentAsync(_userAccounting);

        // Destination doesn't exist in this tenant at all -> the whole move
        // must no-op, including the (never-reached) assignment cleanup.
        var bogus = Guid.NewGuid();
        var ok = await Users().MoveDepartmentAsync(_tenantA, _userAccounting, bogus, default);
        Assert.False(ok);

        var stillAccounting = await Users().FindByIdAsync(_userAccounting, default);
        Assert.Equal(_accounting, stillAccounting!.DepartmentId);
        Assert.Equal("active", await AssignmentStatusAsync(_userAccounting));
    }
}
