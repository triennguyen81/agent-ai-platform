using Aiq.Gateway.Data;
using Aiq.Gateway.Governance;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// ACL V2-C — Department Agent ownership. Drives the actual
/// <see cref="DynamicAgentRepository"/> methods added for V2-C directly
/// against real PostgreSQL (migrations 017-022 + the additive 024), the
/// same "hand-rolled baseline + real later migrations" pattern
/// <see cref="CapabilityResolutionIntegrationTests"/> established, extended
/// with the extra agents columns 024's composite FK and the V2-C repository
/// code itself require (owning_tenant_id, department_id, v2_scope, etc. —
/// see migration 007/024).
///
/// Fixture:
///   Tenant A: Accounting {Admin Accounting, User Accounting}
///             Sales      {Admin Sales, User Sales}
///   Tenant B: Other Department {Admin B}
/// </summary>
public sealed class DepartmentAgentOwnershipIntegrationTests : IAsyncLifetime
{
    static DepartmentAgentOwnershipIntegrationTests() =>
        Dapper.SqlMapper.AddTypeHandler(new StringArrayTypeHandler());

    private readonly string? _baseConnectionString = ResolveTestConnectionString();
    private readonly string _schema = $"dept_agent_ownership_test_{Guid.NewGuid():N}";
    private string? _scopedConnectionString;
    private DynamicAgentRepository _repo = null!;

    private Guid _tenantA, _tenantB;
    private Guid _accounting, _sales, _otherDept;
    private Guid _adminAccounting, _userAccounting, _adminSales, _userSales, _adminB;

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
        await scoped.ExecuteAsync(await ReadMigrationAsync("024_acl_v2_scope_foundations.sql"));

        await AssignDepartmentsAsync(scoped);

        _repo = new DynamicAgentRepository(new NpgsqlConnectionFactory(_scopedConnectionString));
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
            CREATE EXTENSION IF NOT EXISTS pgcrypto;
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

            -- Superset of CapabilityResolutionIntegrationTests's minimal
            -- agents table: adds owning_tenant_id/per-agent tenant_id/
            -- created_by/code/type/config/owner_user_id/disabled_by/
            -- disabled_reason — the real columns migration 024's composite
            -- FK and DynamicAgentRepository's Department Agent methods
            -- (copied structurally from the existing System Agent ones)
            -- both require.
            CREATE TABLE agents (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                tenant_id UUID REFERENCES tenants(id),
                owning_tenant_id UUID REFERENCES tenants(id),
                code TEXT,
                scope TEXT NOT NULL DEFAULT 'system',
                slug TEXT NOT NULL,
                name TEXT NOT NULL,
                description TEXT,
                icon_metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
                type TEXT NOT NULL DEFAULT 'domain',
                action_tier TEXT NOT NULL DEFAULT 'suggest',
                lifecycle_state TEXT NOT NULL DEFAULT 'draft',
                config JSONB NOT NULL DEFAULT '{}'::jsonb,
                owner_user_id UUID,
                created_by UUID,
                updated_by UUID,
                disabled_at TIMESTAMPTZ,
                disabled_by UUID,
                disabled_reason TEXT,
                published_version_id UUID,
                current_draft_version_id UUID,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE TABLE agent_versions (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
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
                workflow_id UUID,
                created_by UUID,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                published_by UUID,
                published_at TIMESTAMPTZ
            );
            CREATE TABLE agent_version_tool_grants (agent_version_id UUID NOT NULL, tool_id UUID NOT NULL, granted_by UUID);
            CREATE TABLE agent_version_skill_assignments (agent_version_id UUID NOT NULL, skill_name TEXT NOT NULL, assigned_by UUID);
            CREATE TABLE agent_version_delegates (agent_version_id UUID NOT NULL, specialist_agent_id UUID NOT NULL, added_by UUID);
            CREATE TABLE tools (
                id UUID PRIMARY KEY, code TEXT NOT NULL, name TEXT NOT NULL, description TEXT, category TEXT,
                risk_tier TEXT NOT NULL DEFAULT 'low', is_active BOOLEAN NOT NULL DEFAULT true, min_action_tier TEXT NOT NULL DEFAULT 'suggest');
            CREATE TABLE workflows (id UUID PRIMARY KEY, name TEXT NOT NULL, is_active BOOLEAN NOT NULL DEFAULT true, definition JSONB NOT NULL DEFAULT '{}'::jsonb);
            CREATE TABLE agent_tenant_assignments (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                agent_id UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
                tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
                status TEXT NOT NULL CHECK (status IN ('active', 'revoked')),
                assigned_by UUID NULL, assigned_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), revoked_at TIMESTAMPTZ NULL
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
            CREATE TABLE notes (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), user_id UUID REFERENCES users(id),
                tenant_id UUID REFERENCES tenants(id), title TEXT NOT NULL, content TEXT NOT NULL,
                tags TEXT[] NOT NULL DEFAULT '{}',
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE TABLE agent_knowledge (
                agent_id UUID NOT NULL REFERENCES agents(id), note_id UUID NOT NULL REFERENCES notes(id),
                attached_by UUID, attached_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), PRIMARY KEY(agent_id,note_id)
            );
            -- Migration 024 adds a nullable department_id to each of these;
            -- never queried by DynamicAgentRepository itself, so a minimal
            -- stub (matching AclV2ScopeFoundationMigrationIntegrationTests's
            -- pre-V2A fixture) is enough for 024 to apply cleanly.
            CREATE TABLE hitl_gates (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id UUID NOT NULL REFERENCES tenants(id),
                agent_id UUID NOT NULL REFERENCES agents(id), name TEXT NOT NULL, is_active BOOLEAN NOT NULL DEFAULT TRUE);
            CREATE TABLE hitl_pending_actions (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id UUID NOT NULL REFERENCES tenants(id),
                gate_id UUID NOT NULL REFERENCES hitl_gates(id), agent_id UUID NOT NULL REFERENCES agents(id),
                tool_code TEXT NOT NULL, payload JSONB NOT NULL DEFAULT '{}'::jsonb,
                status TEXT NOT NULL DEFAULT 'pending', created_at TIMESTAMPTZ NOT NULL DEFAULT NOW());
            CREATE TABLE review_boards (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id UUID NOT NULL REFERENCES tenants(id), name TEXT NOT NULL);
            CREATE TABLE agent_review_requests (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id UUID NOT NULL REFERENCES tenants(id),
                agent_id UUID NOT NULL REFERENCES agents(id), board_id UUID NOT NULL REFERENCES review_boards(id),
                requested_by UUID, status TEXT NOT NULL DEFAULT 'pending');
            CREATE TABLE audit_log (
                id BIGSERIAL PRIMARY KEY, actor_id UUID, action TEXT NOT NULL,
                resource_type TEXT NOT NULL, tenant_id UUID REFERENCES tenants(id),
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW());

            CREATE OR REPLACE FUNCTION protect_published_agent_capability()
            RETURNS TRIGGER AS $$
            DECLARE
                target_version UUID;
                version_status TEXT;
            BEGIN
                target_version := CASE WHEN TG_OP = 'DELETE' THEN OLD.agent_version_id ELSE NEW.agent_version_id END;
                SELECT status INTO version_status FROM agent_versions WHERE id = target_version;
                IF version_status <> 'draft' THEN
                    RAISE EXCEPTION 'published or superseded agent version capabilities are immutable' USING ERRCODE = '23514';
                END IF;
                RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
            END;
            $$ LANGUAGE plpgsql;
            """);
    }

    private async Task SeedTenantsAndUsersAsync(NpgsqlConnection conn)
    {
        _tenantA = Guid.NewGuid(); _tenantB = Guid.NewGuid();
        _adminAccounting = Guid.NewGuid(); _userAccounting = Guid.NewGuid();
        _adminSales = Guid.NewGuid(); _userSales = Guid.NewGuid();
        _adminB = Guid.NewGuid();
        var ownerA = Guid.NewGuid(); var ownerB = Guid.NewGuid();

        var tenantA = _tenantA; var tenantB = _tenantB;
        var adminAccounting = _adminAccounting; var userAccounting = _userAccounting;
        var adminSales = _adminSales; var userSales = _userSales; var adminB = _adminB;

        await conn.ExecuteAsync("""
            INSERT INTO tenants(id,code,name,status) VALUES
                (@tenantA,'tenant-a','Tenant A','active'),
                (@tenantB,'tenant-b','Tenant B','active');
            INSERT INTO users(id,email,password_hash,tenant_id,is_active) VALUES
                (@ownerA,'owner-a@v2ctest','x',@tenantA,true),
                (@ownerB,'owner-b@v2ctest','x',@tenantB,true),
                (@adminAccounting,'admin-accounting@v2ctest','x',@tenantA,true),
                (@userAccounting,'user-accounting@v2ctest','x',@tenantA,true),
                (@adminSales,'admin-sales@v2ctest','x',@tenantA,true),
                (@userSales,'user-sales@v2ctest','x',@tenantA,true),
                (@adminB,'admin-b@v2ctest','x',@tenantB,true);
            INSERT INTO user_roles(user_id,role_id)
            SELECT @ownerA,id FROM roles WHERE name='tenant_owner'
            UNION ALL SELECT @ownerB,id FROM roles WHERE name='tenant_owner'
            UNION ALL SELECT @adminAccounting,id FROM roles WHERE name='admin'
            UNION ALL SELECT @userAccounting,id FROM roles WHERE name='user'
            UNION ALL SELECT @adminSales,id FROM roles WHERE name='admin'
            UNION ALL SELECT @userSales,id FROM roles WHERE name='user'
            UNION ALL SELECT @adminB,id FROM roles WHERE name='admin';
            """, new { tenantA, tenantB, ownerA, ownerB, adminAccounting, userAccounting, adminSales, userSales, adminB });
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

        var accounting = _accounting; var sales = _sales; var otherDept = _otherDept;
        var adminAccounting = _adminAccounting; var userAccounting = _userAccounting;
        var adminSales = _adminSales; var userSales = _userSales; var adminB = _adminB;
        await conn.ExecuteAsync("""
            UPDATE users SET department_id=@accounting WHERE id IN (@adminAccounting,@userAccounting);
            UPDATE users SET department_id=@sales WHERE id IN (@adminSales,@userSales);
            UPDATE users SET department_id=@otherDept WHERE id=@adminB;
            """, new { accounting, sales, otherDept, adminAccounting, userAccounting, adminSales, userSales, adminB });
    }

    private static async Task<string> ReadMigrationAsync(string name)
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "Data", "Migrations", name);
        if (File.Exists(outputPath)) return await File.ReadAllTextAsync(outputPath);
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../src/Aiq.Gateway/Data/Migrations", name));
        return await File.ReadAllTextAsync(sourcePath);
    }

    private static CreateSystemAgentRequest NewAgentRequest(string slug, string name) =>
        new(slug, name, null, null, null, null);

    private static CreateAgentVersionRequest NewVersionRequest() =>
        new("claude_sdk", "sonnet", "You are a department agent.", 0.2m, 4, 120, 2048, null, null, null, null, null, null);

    [Fact]
    public async Task CreateAsync_StampsStructuralOwnershipFromPrincipalNeverFromPayload()
    {
        if (_scopedConnectionString is null) return;
        var agent = await _repo.CreateDepartmentAgentAsync(_tenantA, _accounting, _adminAccounting, NewAgentRequest("accounting-bot", "Accounting Bot"), default);

        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();
        var row = await conn.QuerySingleAsync<(string V2Scope, Guid OwningTenantId, Guid DepartmentId, Guid? CreatedBy, string MigrationStatus)>(
            "SELECT v2_scope, owning_tenant_id, department_id, created_by, v2_migration_status FROM agents WHERE id=@id",
            new { id = agent.Id });

        Assert.Equal("department", row.V2Scope);
        Assert.Equal(_tenantA, row.OwningTenantId);
        Assert.Equal(_accounting, row.DepartmentId);
        Assert.Equal(_adminAccounting, row.CreatedBy);
        Assert.Equal("resolved", row.MigrationStatus);
        Assert.True(await _repo.IsResolvedDepartmentAgentAsync(agent.Id, default));
    }

    [Fact]
    public async Task AdminAccounting_FullOwnLifecycle_CreateDraftValidatePublishAssign()
    {
        if (_scopedConnectionString is null) return;
        var agent = await _repo.CreateDepartmentAgentAsync(_tenantA, _accounting, _adminAccounting, NewAgentRequest("full-cycle", "Full Cycle"), default);

        var found = await _repo.FindDepartmentAgentAsync(_tenantA, _accounting, agent.Id, default);
        Assert.NotNull(found);

        var version = await _repo.CreateDepartmentAgentVersionAsync(_tenantA, _accounting, agent.Id, _adminAccounting, NewVersionRequest(), default);
        Assert.NotNull(version);

        var validation = await _repo.ValidateDepartmentAgentVersionAsync(_tenantA, _accounting, agent.Id, version!.Id, default);
        Assert.True(validation!.Valid);

        var published = await _repo.PublishDepartmentAgentVersionAsync(_tenantA, _accounting, agent.Id, version.Id, _adminAccounting, default);
        Assert.NotNull(published);
        Assert.Equal("published", published!.Status);

        var versions = await _repo.ListDepartmentAgentVersionsAsync(_tenantA, _accounting, agent.Id, default);
        Assert.Single(versions);

        // ADMIN executes its own published Department Agent automatically —
        // no direct agent_assignments row.
        var adminManifest = await _repo.GetDepartmentAgentManifestAsync(_adminAccounting, _tenantA, _accounting, agent.Id, isAdmin: true, default);
        Assert.NotNull(adminManifest.Manifest);

        // USER needs a direct assignment first.
        var beforeAssign = await _repo.GetDepartmentAgentManifestAsync(_userAccounting, _tenantA, _accounting, agent.Id, isAdmin: false, default);
        Assert.Null(beforeAssign.Manifest);
        Assert.Equal("agent_not_assigned", beforeAssign.DenialReason);

        Assert.True(await _repo.AssignUserToDepartmentAgentAsync(_tenantA, _accounting, agent.Id, _userAccounting, _adminAccounting, default));
        var afterAssign = await _repo.GetDepartmentAgentManifestAsync(_userAccounting, _tenantA, _accounting, agent.Id, isAdmin: false, default);
        Assert.NotNull(afterAssign.Manifest);

        var assignedUsers = await _repo.ListDepartmentAgentAssignedUsersAsync(_tenantA, _accounting, agent.Id, default);
        Assert.Contains(assignedUsers!, u => u.UserId == _userAccounting);

        Assert.True(await _repo.RevokeUserFromDepartmentAgentAsync(_tenantA, _accounting, agent.Id, _userAccounting, default));
        var afterRevoke = await _repo.GetDepartmentAgentManifestAsync(_userAccounting, _tenantA, _accounting, agent.Id, isAdmin: false, default);
        Assert.Null(afterRevoke.Manifest);
    }

    [Fact]
    public async Task AdminSales_CannotReadEditTestPublishOrAssignAccountingsAgent()
    {
        if (_scopedConnectionString is null) return;
        var agent = await _repo.CreateDepartmentAgentAsync(_tenantA, _accounting, _adminAccounting, NewAgentRequest("accounting-only", "Accounting Only"), default);
        var version = await _repo.CreateDepartmentAgentVersionAsync(_tenantA, _accounting, agent.Id, _adminAccounting, NewVersionRequest(), default);

        // Known agent/version UUIDs — Admin Sales still gets denied on every operation.
        Assert.Null(await _repo.FindDepartmentAgentAsync(_tenantA, _sales, agent.Id, default));
        Assert.Null(await _repo.UpdateDepartmentAgentAsync(_tenantA, _sales, agent.Id, _adminSales, new(null, null, null, null), default));
        Assert.Null(await _repo.FindDepartmentAgentVersionAsync(_tenantA, _sales, agent.Id, version!.Id, default));
        Assert.Null(await _repo.ValidateDepartmentAgentVersionAsync(_tenantA, _sales, agent.Id, version.Id, default));
        Assert.Null(await _repo.PublishDepartmentAgentVersionAsync(_tenantA, _sales, agent.Id, version.Id, _adminSales, default));
        Assert.Null(await _repo.ListDepartmentAgentAssignedUsersAsync(_tenantA, _sales, agent.Id, default));
        Assert.False(await _repo.AssignUserToDepartmentAgentAsync(_tenantA, _sales, agent.Id, _userSales, _adminSales, default));

        // Sales cannot even test-run it (draft-or-published, ownership gate).
        var testResult = await _repo.GetDepartmentAgentTestManifestAsync(_adminSales, _tenantA, _sales, agent.Id, default);
        Assert.Null(testResult.Manifest);
        Assert.Equal("not_found_or_cross_department", testResult.DenialReason);

        // And cannot assign it to a USER in Sales, even though that user is
        // real and active — the agent just isn't Sales's to hand out.
        Assert.False(await _repo.AssignUserToDepartmentAgentAsync(_tenantA, _sales, agent.Id, _userSales, _adminSales, default));
    }

    [Fact]
    public async Task TenantB_NeverReachesTenantAsAgentRegardlessOfKnownUuid()
    {
        if (_scopedConnectionString is null) return;
        var agent = await _repo.CreateDepartmentAgentAsync(_tenantA, _accounting, _adminAccounting, NewAgentRequest("tenant-a-only", "Tenant A Only"), default);

        Assert.Null(await _repo.FindDepartmentAgentAsync(_tenantB, _otherDept, agent.Id, default));
        Assert.False(await _repo.AssignUserToDepartmentAgentAsync(_tenantB, _otherDept, agent.Id, _adminB, _adminB, default));

        var manifest = await _repo.GetDepartmentAgentManifestAsync(_adminB, _tenantB, _otherDept, agent.Id, isAdmin: true, default);
        Assert.Null(manifest.Manifest);
        Assert.Equal("agent_not_owned_by_department", manifest.DenialReason);
    }

    [Fact]
    public async Task Test_ResolvesCurrentDraftBeforeAnyPublish_OwnerOnly()
    {
        if (_scopedConnectionString is null) return;
        var agent = await _repo.CreateDepartmentAgentAsync(_tenantA, _accounting, _adminAccounting, NewAgentRequest("draft-test", "Draft Test"), default);
        var version = await _repo.CreateDepartmentAgentVersionAsync(_tenantA, _accounting, agent.Id, _adminAccounting, NewVersionRequest(), default);
        Assert.Equal("draft", version!.Status);

        // Never published — a real production manifest must still be denied
        // (lifecycle_state stays 'draft' until PublishDepartmentAgentVersionAsync
        // runs, same denial-ladder order as the pre-existing GetAdminManifestAsync).
        var production = await _repo.GetDepartmentAgentManifestAsync(_adminAccounting, _tenantA, _accounting, agent.Id, isAdmin: true, default);
        Assert.Null(production.Manifest);
        Assert.Equal("agent_not_production", production.DenialReason);

        // ...while the OWNER's test path resolves the draft directly.
        var test = await _repo.GetDepartmentAgentTestManifestAsync(_adminAccounting, _tenantA, _accounting, agent.Id, default);
        Assert.NotNull(test.Manifest);
        Assert.Equal(version.Id, test.Manifest!.PublishedVersionId);
    }

    [Fact]
    public async Task PublishedVersionIsImmutable_NewChangesRequireANewDraft()
    {
        if (_scopedConnectionString is null) return;
        var agent = await _repo.CreateDepartmentAgentAsync(_tenantA, _accounting, _adminAccounting, NewAgentRequest("immutable-check", "Immutable Check"), default);
        var version = await _repo.CreateDepartmentAgentVersionAsync(_tenantA, _accounting, agent.Id, _adminAccounting, NewVersionRequest(), default);
        await _repo.PublishDepartmentAgentVersionAsync(_tenantA, _accounting, agent.Id, version!.Id, _adminAccounting, default);

        // UpdateDepartmentAgentVersionAsync itself has no status filter in
        // its SQL — DepartmentAgentEndpoints.UpdateVersionAsync is what
        // enforces draft-only by checking .Status before calling it (same
        // shape as the existing System Agent endpoint) — verified at the
        // endpoint layer; here we confirm publishing did leave the version
        // rows independent (a second CreateVersionAsync makes a new v2,
        // never mutating v1's content).
        var v2 = await _repo.CreateDepartmentAgentVersionAsync(_tenantA, _accounting, agent.Id, _adminAccounting, NewVersionRequest() with { Model = "opus" }, default);
        Assert.NotNull(v2);
        Assert.Equal(2, v2!.Version);
        var v1Reloaded = await _repo.FindDepartmentAgentVersionAsync(_tenantA, _accounting, agent.Id, version.Id, default);
        Assert.Equal("sonnet", v1Reloaded!.Model);
    }
}
