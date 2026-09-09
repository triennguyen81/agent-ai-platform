using Aiq.Gateway.Data;
using Aiq.Gateway.Governance;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// ACL V2 final phase (V2-G) — legacy shared-agent quarantine inventory and
/// explicit, one-department-at-a-time cloning. Same fixture pattern as
/// <see cref="DepartmentCapabilityOwnershipIntegrationTests"/> (hand-rolled
/// baseline + real migrations 017-019, 021, 022, 024, 025), extended with a
/// legacy `scope='tenant'` agent shared across two departments (Accounting
/// and Sales, both in Tenant A) to exercise migration 024's own
/// `quarantined_shared` classification plus the new clone mechanism — see
/// ACL_V2_FINAL_MIGRATION_PRODUCTION_HARDENING_REPORT.md Part A.
/// </summary>
public sealed class LegacyAgentMigrationIntegrationTests : IAsyncLifetime
{
    static LegacyAgentMigrationIntegrationTests() =>
        Dapper.SqlMapper.AddTypeHandler(new StringArrayTypeHandler());

    private readonly string? _baseConnectionString = ResolveTestConnectionString();
    private readonly string _schema = $"legacy_migration_test_{Guid.NewGuid():N}";
    private string? _scopedConnectionString;
    private DynamicAgentRepository _agentRepo = null!;

    private Guid _tenantA;
    private Guid _accounting, _sales;
    private Guid _ownerA;
    private Guid _sharedAgentId, _sharedVersionId;
    private Guid _singleDeptAgentId;
    private Guid _noDeptAgentId;

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
        await SeedAsync(scoped);

        // 008 installs trg_create_initial_agent_version — CloneSharedAgentToDepartmentAsync
        // relies on it firing for the clone row exactly like it does in
        // production; applied before any agent is seeded below so its own
        // backfill INSERT (which only touches pre-existing agents) is a no-op here.
        await scoped.ExecuteAsync(await ReadMigrationAsync("008_agent_versions.sql"));
        // 011 installs trg_validate_agent_version_pointers (a DEFERRED
        // constraint trigger) -- CloneSharedAgentToDepartmentAsync issues
        // SET CONSTRAINTS against its exact name, which errors if the
        // constraint trigger doesn't exist in this schema at all.
        await scoped.ExecuteAsync(await ReadMigrationAsync("011_agent_consistency_audit.sql"));
        await scoped.ExecuteAsync(await ReadMigrationAsync("017_tenant_owner_role.sql"));
        await scoped.ExecuteAsync(await ReadMigrationAsync("018_tenant_owner_enforcement.sql"));
        await scoped.ExecuteAsync(await ReadMigrationAsync("019_departments.sql"));
        await scoped.ExecuteAsync(await ReadMigrationAsync("021_agent_department_assignments.sql"));
        await scoped.ExecuteAsync(await ReadMigrationAsync("022_business_capabilities.sql"));
        await scoped.ExecuteAsync(await ReadMigrationAsync("024_acl_v2_scope_foundations.sql"));
        await scoped.ExecuteAsync(await ReadMigrationAsync("025_department_capability_binding_v2c_agents.sql"));

        await AssignDepartmentsAsync(scoped);
        await SeedLegacyAgentsAsync(scoped);

        _agentRepo = new DynamicAgentRepository(new NpgsqlConnectionFactory(_scopedConnectionString));
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
            CREATE UNIQUE INDEX uq_agents_tenant_slug ON agents(owning_tenant_id, slug) WHERE scope = 'tenant';
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
                published_at TIMESTAMPTZ,
                UNIQUE (agent_id, version),
                UNIQUE (id, agent_id)
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

    private async Task SeedAsync(NpgsqlConnection conn)
    {
        _tenantA = Guid.NewGuid();
        _ownerA = Guid.NewGuid();
        var tenantA = _tenantA; var ownerA = _ownerA;
        await conn.ExecuteAsync("""
            INSERT INTO tenants(id,code,name,status) VALUES (@tenantA,'tenant-a','Tenant A','active');
            INSERT INTO users(id,email,password_hash,tenant_id,is_active) VALUES (@ownerA,'owner-a@legacytest','x',@tenantA,true);
            INSERT INTO user_roles(user_id,role_id) SELECT @ownerA,id FROM roles WHERE name='tenant_owner';
            """, new { tenantA, ownerA });
    }

    private async Task AssignDepartmentsAsync(NpgsqlConnection conn)
    {
        var tenantA = _tenantA;
        _accounting = await conn.ExecuteScalarAsync<Guid>(
            "UPDATE departments SET code='accounting', name='Accounting' WHERE tenant_id=@tenantA AND is_default RETURNING id", new { tenantA });
        _sales = await conn.ExecuteScalarAsync<Guid>(
            "INSERT INTO departments(tenant_id,code,name,status) VALUES (@tenantA,'sales','Sales','active') RETURNING id", new { tenantA });
    }

    /// <summary>Seeds three legacy scope='tenant' agents, each with a published v1, then applies migration 024 so its classification runs against real, pre-existing data — mirroring a genuine upgrade rather than hand-setting v2_scope.</summary>
    private async Task SeedLegacyAgentsAsync(NpgsqlConnection conn)
    {
        var tenantA = _tenantA; var accounting = _accounting; var sales = _sales; var ownerA = _ownerA;

        _sharedAgentId = await conn.ExecuteScalarAsync<Guid>("""
            INSERT INTO agents(tenant_id, owning_tenant_id, scope, slug, name, created_by)
            VALUES (@tenantA, @tenantA, 'tenant', 'shared-legacy-bot', 'Shared Legacy Bot', @ownerA) RETURNING id
            """, new { tenantA, ownerA });
        // trg_create_initial_agent_version (migration 008) already created a
        // draft v1 for this agent the instant it was inserted above -- update
        // that row in place (mirroring CloneSharedAgentToDepartmentAsync's own
        // fix for the identical situation) rather than inserting a second v1.
        _sharedVersionId = await conn.ExecuteScalarAsync<Guid>(
            "SELECT current_draft_version_id FROM agents WHERE id=@id", new { id = _sharedAgentId });
        await conn.ExecuteAsync("""
            UPDATE agent_versions SET status='published', provider='ollama', model='qwen3:latest',
                system_prompt='You help everyone.', published_by=@ownerA, published_at=NOW()
            WHERE id=@v
            """, new { v = _sharedVersionId, ownerA });
        await conn.ExecuteAsync("UPDATE agents SET published_version_id=@v, current_draft_version_id=NULL WHERE id=@id", new { v = _sharedVersionId, id = _sharedAgentId });
        await conn.ExecuteAsync("""
            INSERT INTO agent_tenant_assignments(agent_id, tenant_id, status, assigned_by) VALUES (@id, @tenantA, 'active', @ownerA);
            INSERT INTO agent_department_assignments(agent_id, tenant_id, department_id, status, assigned_by) VALUES
                (@id, @tenantA, @accounting, 'active', @ownerA),
                (@id, @tenantA, @sales, 'active', @ownerA);
            """, new { id = _sharedAgentId, tenantA, accounting, sales, ownerA });

        _singleDeptAgentId = await conn.ExecuteScalarAsync<Guid>("""
            INSERT INTO agents(tenant_id, owning_tenant_id, scope, slug, name, created_by)
            VALUES (@tenantA, @tenantA, 'tenant', 'single-dept-bot', 'Single Dept Bot', @ownerA) RETURNING id
            """, new { tenantA, ownerA });
        await conn.ExecuteAsync("""
            INSERT INTO agent_tenant_assignments(agent_id, tenant_id, status, assigned_by) VALUES (@id, @tenantA, 'active', @ownerA);
            INSERT INTO agent_department_assignments(agent_id, tenant_id, department_id, status, assigned_by) VALUES (@id, @tenantA, @accounting, 'active', @ownerA);
            """, new { id = _singleDeptAgentId, tenantA, accounting, ownerA });

        _noDeptAgentId = await conn.ExecuteScalarAsync<Guid>("""
            INSERT INTO agents(tenant_id, owning_tenant_id, scope, slug, name, created_by)
            VALUES (@tenantA, @tenantA, 'tenant', 'no-dept-bot', 'No Dept Bot', @ownerA) RETURNING id
            """, new { tenantA, ownerA });
        await conn.ExecuteAsync("INSERT INTO agent_tenant_assignments(agent_id, tenant_id, status, assigned_by) VALUES (@id, @tenantA, 'active', @ownerA)",
            new { id = _noDeptAgentId, tenantA, ownerA });

        // Migration 024 runs BEFORE this seeding in InitializeAsync's real
        // sequence -- re-run its classification block against this freshly
        // seeded data to prove the classification itself (not just the
        // schema) behaves correctly on realistic pre-existing rows, exactly
        // like a populated-database upgrade would encounter.
        await conn.ExecuteAsync("""
            UPDATE agents SET v2_scope=NULL, v2_migration_status='pending', v2_migration_note=NULL, department_id=NULL
            WHERE owning_tenant_id=@tenantA;
            """, new { tenantA });
        await conn.ExecuteAsync("""
            UPDATE agents SET v2_scope='platform_template', department_id=NULL, v2_migration_status='resolved',
                v2_migration_note='reclassify: platform template'
            WHERE scope='system' AND owning_tenant_id IS NULL AND v2_scope IS NULL;

            WITH ownership AS (
                SELECT a.id AS agent_id, (array_agg(DISTINCT ada.department_id))[1] AS department_id
                FROM agents a JOIN agent_department_assignments ada ON ada.agent_id=a.id AND ada.tenant_id=a.owning_tenant_id AND ada.status='active'
                WHERE a.scope='tenant' GROUP BY a.id HAVING count(DISTINCT ada.department_id)=1
            )
            UPDATE agents a SET v2_scope='department', department_id=ownership.department_id,
                v2_migration_status='resolved', v2_migration_note='reclassify: exactly one active department'
            FROM ownership WHERE a.id=ownership.agent_id AND a.v2_scope IS NULL;

            WITH shared AS (
                SELECT a.id FROM agents a JOIN agent_department_assignments ada ON ada.agent_id=a.id AND ada.tenant_id=a.owning_tenant_id AND ada.status='active'
                WHERE a.scope='tenant' GROUP BY a.id HAVING count(DISTINCT ada.department_id)>1
            )
            UPDATE agents a SET v2_migration_status='quarantined_shared', v2_migration_note='reclassify: multiple active departments'
            FROM shared WHERE a.id=shared.id AND a.v2_scope IS NULL;

            UPDATE agents SET v2_migration_status='quarantined_no_department', v2_migration_note='reclassify: no active department'
            WHERE scope='tenant' AND v2_scope IS NULL AND v2_migration_status='pending';
            """);
    }

    private static async Task<string> ReadMigrationAsync(string name)
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "Data", "Migrations", name);
        if (File.Exists(outputPath)) return await File.ReadAllTextAsync(outputPath);
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../src/Aiq.Gateway/Data/Migrations", name));
        return await File.ReadAllTextAsync(sourcePath);
    }

    [Fact]
    public async Task Migration024Classification_CorrectlyClassifiesAllThreeCases()
    {
        if (_scopedConnectionString is null) return;
        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();

        var shared = await conn.QuerySingleAsync<(string? V2Scope, string Status)>(
            "SELECT v2_scope AS V2Scope, v2_migration_status AS Status FROM agents WHERE id=@id", new { id = _sharedAgentId });
        Assert.Null(shared.V2Scope);
        Assert.Equal("quarantined_shared", shared.Status);

        var single = await conn.QuerySingleAsync<(string? V2Scope, Guid? DepartmentId, string Status)>(
            "SELECT v2_scope AS V2Scope, department_id AS DepartmentId, v2_migration_status AS Status FROM agents WHERE id=@id", new { id = _singleDeptAgentId });
        Assert.Equal("department", single.V2Scope);
        Assert.Equal(_accounting, single.DepartmentId);
        Assert.Equal("resolved", single.Status);

        var none = await conn.QuerySingleAsync<(string? V2Scope, string Status)>(
            "SELECT v2_scope AS V2Scope, v2_migration_status AS Status FROM agents WHERE id=@id", new { id = _noDeptAgentId });
        Assert.Null(none.V2Scope);
        Assert.Equal("quarantined_no_department", none.Status);
    }

    [Fact]
    public async Task SingleDepartmentLegacyAgent_IsAlreadyLiveAsARealDepartmentAgent()
    {
        // This is the deterministic "exactly-one-department -> migrate to
        // Department Agent ownership" case -- migration 024's own backfill
        // already performs it structurally; V2-C's DepartmentOwnershipWhere
        // (v2_scope='department' AND owning_tenant_id=@t AND department_id=@d)
        // then treats it as live, no separate V2-G step required. Verified
        // here end-to-end via the real repository method ADMIN's own Agent
        // Builder actually calls.
        if (_scopedConnectionString is null) return;
        var agents = await _agentRepo.ListDepartmentAgentsAsync(_tenantA, _accounting, default);
        Assert.Contains(agents, a => a.Id == _singleDeptAgentId);
    }

    [Fact]
    public async Task QuarantineInventory_ListsSharedAndNoDepartmentAgents_NeverTheResolvedOne()
    {
        if (_scopedConnectionString is null) return;
        var quarantined = await _agentRepo.ListQuarantinedAgentsAsync(default);
        Assert.Contains(quarantined, a => a.Id == _sharedAgentId && a.V2MigrationStatus == "quarantined_shared" && a.ActiveDepartmentCount == 2);
        Assert.Contains(quarantined, a => a.Id == _noDeptAgentId && a.V2MigrationStatus == "quarantined_no_department");
        Assert.DoesNotContain(quarantined, a => a.Id == _singleDeptAgentId);
    }

    [Fact]
    public async Task CloneSharedAgent_CreatesDepartmentOwnedCloneWithLineageAndCopiedConfig()
    {
        if (_scopedConnectionString is null) return;
        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();
        var toolId = await conn.ExecuteScalarAsync<Guid>(
            "INSERT INTO tools(id, code, name) VALUES (gen_random_uuid(), 'search', 'Search') RETURNING id");
        await conn.ExecuteAsync("INSERT INTO agent_version_tool_grants(agent_version_id, tool_id) VALUES (@v, @t)",
            new { v = _sharedVersionId, t = toolId });
        await conn.ExecuteAsync("INSERT INTO agent_version_skill_assignments(agent_version_id, skill_name) VALUES (@v, 'triage')",
            new { v = _sharedVersionId });

        var clone = await _agentRepo.CloneSharedAgentToDepartmentAsync(_ownerA, _sharedAgentId, _tenantA, _accounting, default);
        Assert.NotNull(clone);

        var cloneRow = await conn.QuerySingleAsync<(string V2Scope, Guid? SourceAgentId, Guid? DepartmentId, string LifecycleState)>(
            "SELECT v2_scope AS V2Scope, source_agent_id AS SourceAgentId, department_id AS DepartmentId, lifecycle_state AS LifecycleState FROM agents WHERE id=@id",
            new { id = clone!.ClonedAgentId });
        Assert.Equal("department", cloneRow.V2Scope);
        Assert.Equal(_sharedAgentId, cloneRow.SourceAgentId);
        Assert.Equal(_accounting, cloneRow.DepartmentId);
        Assert.Equal("production", cloneRow.LifecycleState);

        var toolCount = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM agent_version_tool_grants WHERE agent_version_id=@v", new { v = clone.ClonedVersionId });
        Assert.Equal(1, toolCount);
        var skillCount = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM agent_version_skill_assignments WHERE agent_version_id=@v AND skill_name='triage'", new { v = clone.ClonedVersionId });
        Assert.Equal(1, skillCount);

        // Source is completely untouched -- not deleted, still shared/quarantined.
        var sourceStillExists = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM agents WHERE id=@id AND v2_migration_status='quarantined_shared')", new { id = _sharedAgentId });
        Assert.True(sourceStillExists);
        var sourceToolGrantsIntact = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM agent_version_tool_grants WHERE agent_version_id=@v", new { v = _sharedVersionId });
        Assert.Equal(1, sourceToolGrantsIntact);
    }

    [Fact]
    public async Task CloneSharedAgent_RebindsThisDepartmentsUserAssignmentsAndBindings_NotOtherDepartments()
    {
        if (_scopedConnectionString is null) return;
        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();

        var acctUser = Guid.NewGuid(); var salesUser = Guid.NewGuid();
        await conn.ExecuteAsync("""
            INSERT INTO users(id, email, password_hash, tenant_id, department_id, is_active) VALUES
                (@acctUser, 'acct-user@legacytest', 'x', @tenantA, @accounting, true),
                (@salesUser, 'sales-user@legacytest', 'x', @tenantA, @sales, true);
            INSERT INTO agent_assignments(tenant_id, agent_id, user_id, status) VALUES
                (@tenantA, @sharedAgentId, @acctUser, 'active'),
                (@tenantA, @sharedAgentId, @salesUser, 'active');
            """, new { acctUser, salesUser, tenantA = _tenantA, accounting = _accounting, sales = _sales, sharedAgentId = _sharedAgentId });

        var capability = await conn.QuerySingleAsync<Guid>("""
            INSERT INTO business_capabilities(code, name, scope, is_active) VALUES ('shared.capability', 'Shared', 'platform', true) RETURNING id
            """);
        // protect_published_agent_capability() blocks inserting a capability
        // declaration once the owning version is published, and migration
        // 008's own protect_immutable_agent_version() now also blocks
        // flipping a published version back to draft (it never allows
        // OLD.status <> 'draft'). This is TEST-ONLY seed data representing
        // "a capability that was already declared before the historical
        // publish" -- briefly disabling both triggers for this one insert
        // is the standard, safe way to seed that pre-existing state without
        // fighting immutability guards that exist specifically to stop
        // ADMIN from doing this through the application.
        await conn.ExecuteAsync("ALTER TABLE agent_versions DISABLE TRIGGER trg_protect_immutable_agent_version");
        await conn.ExecuteAsync("ALTER TABLE agent_version_capabilities DISABLE TRIGGER trg_protect_published_version_capabilities");
        await conn.ExecuteAsync("""
            INSERT INTO agent_version_capabilities(agent_version_id, capability_id) VALUES (@v, @c)
            """, new { v = _sharedVersionId, c = capability });
        await conn.ExecuteAsync("ALTER TABLE agent_version_capabilities ENABLE TRIGGER trg_protect_published_version_capabilities");
        await conn.ExecuteAsync("ALTER TABLE agent_versions ENABLE TRIGGER trg_protect_immutable_agent_version");
        await conn.ExecuteAsync("""
            INSERT INTO department_capability_bindings(tenant_id, department_id, capability_id, agent_id, status) VALUES
                (@tenantA, @accounting, @capability, @sharedAgentId, 'active')
            """, new { tenantA = _tenantA, accounting = _accounting, capability, sharedAgentId = _sharedAgentId });

        var clone = await _agentRepo.CloneSharedAgentToDepartmentAsync(_ownerA, _sharedAgentId, _tenantA, _accounting, default);
        Assert.NotNull(clone);

        var acctAssignmentAgent = await conn.ExecuteScalarAsync<Guid>(
            "SELECT agent_id FROM agent_assignments WHERE user_id=@acctUser", new { acctUser });
        Assert.Equal(clone!.ClonedAgentId, acctAssignmentAgent);

        var salesAssignmentAgent = await conn.ExecuteScalarAsync<Guid>(
            "SELECT agent_id FROM agent_assignments WHERE user_id=@salesUser", new { salesUser });
        Assert.Equal(_sharedAgentId, salesAssignmentAgent);

        var bindingAgent = await conn.ExecuteScalarAsync<Guid>(
            "SELECT agent_id FROM department_capability_bindings WHERE tenant_id=@tenantA AND department_id=@accounting AND capability_id=@capability",
            new { tenantA = _tenantA, accounting = _accounting, capability });
        Assert.Equal(clone.ClonedAgentId, bindingAgent);
    }

    [Fact]
    public async Task CloneSharedAgent_IsIdempotent_SecondCallForSameDepartmentReturnsNull()
    {
        if (_scopedConnectionString is null) return;
        var first = await _agentRepo.CloneSharedAgentToDepartmentAsync(_ownerA, _sharedAgentId, _tenantA, _accounting, default);
        Assert.NotNull(first);
        var second = await _agentRepo.CloneSharedAgentToDepartmentAsync(_ownerA, _sharedAgentId, _tenantA, _accounting, default);
        Assert.Null(second);
    }

    [Fact]
    public async Task CloneSharedAgent_DeniedForADepartmentWithNoActiveEntitlement_NeverGuessesOwnership()
    {
        if (_scopedConnectionString is null) return;
        var tenantB = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("INSERT INTO tenants(id, code, name, status) VALUES (@tenantB, 'tenant-b', 'Tenant B', 'active')", new { tenantB });
        var otherDept = await conn.ExecuteScalarAsync<Guid>(
            "UPDATE departments SET code='other', name='Other' WHERE tenant_id=@tenantB AND is_default RETURNING id", new { tenantB });

        // Cross-tenant: Tenant B's department never had an entitlement to Tenant A's shared agent.
        var crossTenant = await _agentRepo.CloneSharedAgentToDepartmentAsync(_ownerA, _sharedAgentId, tenantB, otherDept, default);
        Assert.Null(crossTenant);

        // Same tenant, but the no-department agent was never shared with any
        // department at all -- cloning it anywhere would be guessing.
        var noDeptClone = await _agentRepo.CloneSharedAgentToDepartmentAsync(_ownerA, _noDeptAgentId, _tenantA, _accounting, default);
        Assert.Null(noDeptClone);
    }
}
