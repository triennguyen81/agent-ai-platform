using Aiq.Gateway.Data;
using Aiq.Gateway.Governance;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Phase D4 business capability integration coverage — the version-scoped
/// capability catalog, the canonical ADMIN/USER resolver, and TENANT_OWNER
/// department capability-to-agent binding. Fixture mirrors
/// <see cref="AgentDepartmentEntitlementIntegrationTests"/> exactly (same
/// tenants/departments/users), extended with two accounting agents in
/// Accounting so ambiguity/binding behavior has something real to bind.
///
/// Fixture:
///   Tenant A: Owner A
///     Accounting {Admin Accounting, User Accounting,
///                 Accounting Agent (accounting.journal, accounting.invoice),
///                 Accounting Agent 2 (accounting.journal) — deliberately
///                 overlapping, for ambiguity tests}
///     Sales      {Admin Sales, Sales Agent (no accounting capability)}
///   Tenant B: Owner B, Other Department {Other Agent}
/// </summary>
public sealed class CapabilityResolutionIntegrationTests : IAsyncLifetime
{
    static CapabilityResolutionIntegrationTests() =>
        Dapper.SqlMapper.AddTypeHandler(new StringArrayTypeHandler());

    private readonly string? _baseConnectionString = ResolveTestConnectionString();
    private readonly string _schema = $"capability_resolution_test_{Guid.NewGuid():N}";
    private string? _scopedConnectionString;

    private Guid _tenantA, _tenantB;
    private Guid _accounting, _sales, _otherDept;
    private Guid _ownerA;
    private Guid _adminAccounting, _userAccounting, _adminSales;
    private Guid _accountingAgentId, _accountingVersionId;
    private Guid _accountingAgent2Id, _accountingVersion2Id;
    private Guid _salesAgentId, _salesVersionId;
    private Guid _journalCapabilityId, _invoiceCapabilityId;

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
        // ACL V2-D: CapabilityRepository's queries now reference
        // business_capabilities.scope and agents.v2_scope/owning_tenant_id/
        // department_id unconditionally (the Department-Agent-ownership OR
        // branch) — these columns must exist even though every agent in
        // this D4-era fixture stays scope='system'/v2_scope=NULL.
        await scoped.ExecuteAsync(await ReadMigrationAsync("024_acl_v2_scope_foundations.sql"));

        await AssignDepartmentsAsync(scoped);
        await SeedAgentsAsync(scoped);
        // Capabilities must be attached while the version is still 'draft'
        // (trg_protect_published_version_capabilities fails closed
        // otherwise) — seed capabilities first, then flip to 'published',
        // exactly mirroring the real draft -> assign capabilities -> publish
        // sequence the Agent Builder endpoints enforce.
        await SeedCapabilitiesAsync(scoped);
        await scoped.ExecuteAsync("UPDATE agent_versions SET status='published'");
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
                id UUID PRIMARY KEY,
                scope TEXT NOT NULL DEFAULT 'system',
                -- ACL V2-A/V2-D: owning_tenant_id is required by migration
                -- 024's composite FK even though nothing in this D4-era
                -- fixture ever sets it (every agent here stays scope=
                -- 'system'/v2_scope=NULL, exactly like production D4 data).
                tenant_id UUID,
                owning_tenant_id UUID,
                created_by UUID,
                slug TEXT NOT NULL,
                name TEXT NOT NULL,
                description TEXT,
                icon_metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
                action_tier TEXT NOT NULL DEFAULT 'suggest',
                lifecycle_state TEXT NOT NULL DEFAULT 'production',
                disabled_at TIMESTAMPTZ,
                published_version_id UUID,
                current_draft_version_id UUID,
                updated_by UUID
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
            -- Never populated in this fixture (no test here exercises
            -- tools/skills/delegates) but HydrateVersionsAsync always
            -- queries all three unconditionally after every create/update.
            CREATE TABLE agent_version_tool_grants (agent_version_id UUID NOT NULL, tool_id UUID NOT NULL);
            CREATE TABLE agent_version_skill_assignments (agent_version_id UUID NOT NULL, skill_name TEXT NOT NULL);
            CREATE TABLE agent_version_delegates (agent_version_id UUID NOT NULL, specialist_agent_id UUID NOT NULL, added_by UUID);
            -- ValidateVersionAsync/PublishVersionAsync join these unconditionally too.
            CREATE TABLE tools (id UUID PRIMARY KEY, is_active BOOLEAN NOT NULL DEFAULT true, min_action_tier TEXT NOT NULL DEFAULT 'suggest');
            CREATE TABLE workflows (id UUID PRIMARY KEY, name TEXT NOT NULL, is_active BOOLEAN NOT NULL DEFAULT true, definition JSONB NOT NULL DEFAULT '{}'::jsonb);
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

            -- ACL V2-A (migration 024) ALTERs these tables; they don't exist
            -- in this D4-era fixture otherwise. Minimal stubs only — nothing
            -- in CapabilityRepository queries them.
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

            -- migration 022 attaches a trigger to agent_version_capabilities
            -- using this function, defined in migration 010 for the real
            -- agent_version_tool_grants/agent_version_skill_assignments
            -- tables this minimal fixture never creates. Pre-declare it
            -- here (byte-identical to 010) so 022 can run standalone.
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
        _ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        _adminAccounting = Guid.NewGuid(); _userAccounting = Guid.NewGuid();
        _adminSales = Guid.NewGuid();

        var tenantA = _tenantA; var tenantB = _tenantB; var ownerA = _ownerA;
        var adminAccounting = _adminAccounting; var userAccounting = _userAccounting; var adminSales = _adminSales;

        await conn.ExecuteAsync("""
            INSERT INTO tenants(id,code,name,status) VALUES
                (@tenantA,'tenant-a','Tenant A','active'),
                (@tenantB,'tenant-b','Tenant B','active');
            INSERT INTO users(id,email,password_hash,tenant_id,is_active) VALUES
                (@ownerA,'owner-a@d4test','x',@tenantA,true),
                (@ownerB,'owner-b@d4test','x',@tenantB,true),
                (@adminAccounting,'admin-accounting@d4test','x',@tenantA,true),
                (@userAccounting,'user-accounting@d4test','x',@tenantA,true),
                (@adminSales,'admin-sales@d4test','x',@tenantA,true);
            INSERT INTO user_roles(user_id,role_id)
            SELECT @ownerA,id FROM roles WHERE name='tenant_owner'
            UNION ALL SELECT @ownerB,id FROM roles WHERE name='tenant_owner'
            UNION ALL SELECT @adminAccounting,id FROM roles WHERE name='admin'
            UNION ALL SELECT @userAccounting,id FROM roles WHERE name='user'
            UNION ALL SELECT @adminSales,id FROM roles WHERE name='admin';
            """, new { tenantA, tenantB, ownerA, ownerB, adminAccounting, userAccounting, adminSales });
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
        var adminAccounting = _adminAccounting; var userAccounting = _userAccounting; var adminSales = _adminSales;
        await conn.ExecuteAsync("""
            UPDATE users SET department_id=@accounting WHERE id IN (@adminAccounting,@userAccounting);
            UPDATE users SET department_id=@sales WHERE id=@adminSales;
            """, new { accounting, sales, adminAccounting, userAccounting, adminSales });
    }

    private async Task SeedAgentsAsync(NpgsqlConnection conn)
    {
        _accountingAgentId = Guid.NewGuid(); _accountingVersionId = Guid.NewGuid();
        _accountingAgent2Id = Guid.NewGuid(); _accountingVersion2Id = Guid.NewGuid();
        _salesAgentId = Guid.NewGuid(); _salesVersionId = Guid.NewGuid();

        await SeedOneAgentAsync(conn, _accountingAgentId, _accountingVersionId, "accounting-agent", "Accounting Agent", _tenantA);
        await SeedOneAgentAsync(conn, _accountingAgent2Id, _accountingVersion2Id, "accounting-agent-2", "Accounting Agent 2", _tenantA);
        await SeedOneAgentAsync(conn, _salesAgentId, _salesVersionId, "sales-agent", "Sales Agent", _tenantA);
    }

    private static async Task SeedOneAgentAsync(NpgsqlConnection conn, Guid agentId, Guid versionId, string slug, string name, Guid tenantId)
    {
        // Version starts 'draft' so SeedCapabilitiesAsync can attach
        // capabilities to it — InitializeAsync flips every version to
        // 'published' afterward, once, in one shot.
        await conn.ExecuteAsync("""
            INSERT INTO agents(id,scope,slug,name,action_tier,lifecycle_state,published_version_id)
            VALUES (@agentId,'system',@slug,@name,'suggest','production',@versionId);
            INSERT INTO agent_versions(id,agent_id,version,status,provider,model,system_prompt)
            VALUES (@versionId,@agentId,1,'draft','claude_sdk','sonnet','You are a helpful assistant.');
            INSERT INTO agent_tenant_assignments(agent_id,tenant_id,status) VALUES (@agentId,@tenantId,'active');
            """, new { agentId, versionId, slug, name, tenantId });
    }

    private async Task SeedCapabilitiesAsync(NpgsqlConnection conn)
    {
        _journalCapabilityId = await conn.ExecuteScalarAsync<Guid>(
            "SELECT id FROM business_capabilities WHERE code='accounting.journal'");
        _invoiceCapabilityId = await conn.ExecuteScalarAsync<Guid>(
            "SELECT id FROM business_capabilities WHERE code='accounting.invoice'");

        var journal = _journalCapabilityId; var invoice = _invoiceCapabilityId;
        var v1 = _accountingVersionId; var v2 = _accountingVersion2Id;
        await conn.ExecuteAsync("""
            INSERT INTO agent_version_capabilities(agent_version_id,capability_id) VALUES
                (@v1,@journal), (@v1,@invoice), (@v2,@journal);
            """, new { v1, v2, journal, invoice });
    }

    private static async Task<string> ReadMigrationAsync(string name)
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "Data", "Migrations", name);
        if (File.Exists(outputPath)) return await File.ReadAllTextAsync(outputPath);
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../src/Aiq.Gateway/Data/Migrations", name));
        return await File.ReadAllTextAsync(sourcePath);
    }

    private ICapabilityRepository Repo() => new CapabilityRepository(new NpgsqlConnectionFactory(_scopedConnectionString!));
    private IDynamicAgentRepository AgentsRepo() => new DynamicAgentRepository(new NpgsqlConnectionFactory(_scopedConnectionString!));

    private async Task AssignAgentToDepartmentAsync(Guid agentId, Guid departmentId)
    {
        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "INSERT INTO agent_department_assignments(tenant_id,department_id,agent_id,status) VALUES (@t,@d,@a,'active')",
            new { t = _tenantA, d = departmentId, a = agentId });
    }

    private async Task AssignUserToAgentAsync(Guid agentId, Guid userId)
    {
        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "INSERT INTO agent_assignments(tenant_id,agent_id,user_id,status) VALUES (@t,@a,@u,'active')",
            new { t = _tenantA, a = agentId, u = userId });
    }

    // ==== Agent Builder: version-scoped capability declaration =============

    [Fact]
    public async Task DraftCapabilityAssignment_SucceedsAndPersists()
    {
        if (_scopedConnectionString is null) return;
        var repo = AgentsRepo();
        var actorId = _ownerA;
        var version = await repo.CreateVersionAsync(_salesAgentId, actorId,
            new CreateAgentVersionRequest("claude_sdk", "sonnet", "prompt", 0.2m, 8, 120, 2048, null, null, null, null, null, [_journalCapabilityId]), default);
        Assert.NotNull(version);
        Assert.Contains(_journalCapabilityId, version!.CapabilityIds);
    }

    [Fact]
    public async Task DraftCapabilityAssignment_DuplicateIdsCollapseToOne()
    {
        if (_scopedConnectionString is null) return;
        var repo = AgentsRepo();
        var actorId = _ownerA;
        var version = await repo.CreateVersionAsync(_salesAgentId, actorId,
            new CreateAgentVersionRequest("claude_sdk", "sonnet", "prompt", 0.2m, 8, 120, 2048, null, null, null, null, null, [_journalCapabilityId, _journalCapabilityId]), default);
        Assert.NotNull(version);
        Assert.Single(version!.CapabilityIds);
    }

    [Fact]
    public async Task DraftCapabilityAssignment_InactiveCapabilityRejected()
    {
        if (_scopedConnectionString is null) return;
        await using (var conn = new NpgsqlConnection(_scopedConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("UPDATE business_capabilities SET is_active=false WHERE id=@id", new { id = _journalCapabilityId });
        }
        var repo = AgentsRepo();
        var actorId = _ownerA;
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            repo.CreateVersionAsync(_salesAgentId, actorId,
                new CreateAgentVersionRequest("claude_sdk", "sonnet", "prompt", 0.2m, 8, 120, 2048, null, null, null, null, null, [_journalCapabilityId]), default));
    }

    [Fact]
    public async Task PublishFreezesCapabilitySet_FurtherEditsRejected()
    {
        if (_scopedConnectionString is null) return;
        var repo = AgentsRepo();
        var actorId = _ownerA;
        var version = await repo.CreateVersionAsync(_salesAgentId, actorId,
            new CreateAgentVersionRequest("claude_sdk", "sonnet", "prompt", 0.2m, 8, 120, 2048, null, null, null, null, null, [_journalCapabilityId]), default);
        var published = await repo.PublishVersionAsync(_salesAgentId, version!.Id, actorId, default);
        Assert.NotNull(published);

        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            conn.ExecuteAsync("DELETE FROM agent_version_capabilities WHERE agent_version_id=@id", new { id = version.Id }));
        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public async Task NewDraftFromPublished_CanCopyCapabilityMappings()
    {
        if (_scopedConnectionString is null) return;
        // §3: "creating a new draft from a published version should copy
        // capabilities consistently with existing tools/skills behavior" —
        // there is no server-side clone endpoint (matches ToolIds/Skills:
        // the Web client re-submits the prior version's selections as the
        // new draft's request body). Prove that path end-to-end.
        var repo = AgentsRepo();
        var actorId = _ownerA;
        var v1 = await repo.CreateVersionAsync(_salesAgentId, actorId,
            new CreateAgentVersionRequest("claude_sdk", "sonnet", "prompt", 0.2m, 8, 120, 2048, null, null, null, null, null, [_journalCapabilityId]), default);
        await repo.PublishVersionAsync(_salesAgentId, v1!.Id, actorId, default);

        var v2 = await repo.CreateVersionAsync(_salesAgentId, actorId,
            new CreateAgentVersionRequest("claude_sdk", "sonnet", "prompt v2", 0.2m, 8, 120, 2048, null, null, null, null, null, v1.CapabilityIds), default);
        Assert.NotNull(v2);
        Assert.Equal(v1.CapabilityIds, v2!.CapabilityIds);
    }

    // ==== ADMIN visibility/resolution ========================================

    [Fact]
    public async Task AdminAccounting_SeesJournalCapability()
    {
        if (_scopedConnectionString is null) return;
        await AssignAgentToDepartmentAsync(_accountingAgentId, _accounting);
        var items = await Repo().ListForDepartmentManagerAsync(_tenantA, _accounting, default);
        Assert.Contains(items, c => c.Code == "accounting.journal");
    }

    [Fact]
    public async Task AdminAccounting_ResolvesAccountingAgent_SoleEligible()
    {
        if (_scopedConnectionString is null) return;
        await AssignAgentToDepartmentAsync(_accountingAgentId, _accounting);
        var result = await Repo().ResolveAgentAsync(_tenantA, _accounting, null, isAdmin: true, "accounting.invoice", default);
        Assert.Equal(_accountingAgentId, result.AgentId);
        Assert.Null(result.DenialReason);
    }

    [Fact]
    public async Task AdminAccounting_ExecutesWithoutDirectAdminAssignment()
    {
        if (_scopedConnectionString is null) return;
        await AssignAgentToDepartmentAsync(_accountingAgentId, _accounting);
        // No agent_assignments row for _adminAccounting anywhere.
        var result = await Repo().ResolveAgentAsync(_tenantA, _accounting, null, isAdmin: true, "accounting.journal", default);
        Assert.NotNull(result.AgentId);
    }

    [Fact]
    public async Task AdminAccounting_CannotResolveSalesOnlyCapability()
    {
        if (_scopedConnectionString is null) return;
        // Sales Agent supports no accounting capability at all in this fixture.
        var result = await Repo().ResolveAgentAsync(_tenantA, _accounting, null, isAdmin: true, "supplier.management", default);
        Assert.Null(result.AgentId);
        Assert.Equal("capability_not_available", result.DenialReason);
    }

    [Fact]
    public async Task AdminSales_CannotResolveAccountingJournal()
    {
        if (_scopedConnectionString is null) return;
        await AssignAgentToDepartmentAsync(_accountingAgentId, _accounting);
        var result = await Repo().ResolveAgentAsync(_tenantA, _sales, null, isAdmin: true, "accounting.journal", default);
        Assert.Null(result.AgentId);
        Assert.Equal("capability_not_available", result.DenialReason);
    }

    // ==== USER resolution =====================================================

    [Fact]
    public async Task UserAccounting_DirectAssignment_SeesAndResolvesJournal()
    {
        if (_scopedConnectionString is null) return;
        await AssignAgentToDepartmentAsync(_accountingAgentId, _accounting);
        await AssignUserToAgentAsync(_accountingAgentId, _userAccounting);

        var visible = await Repo().ListForPrincipalAsync(_tenantA, _userAccounting, default);
        Assert.Contains(visible, c => c.Code == "accounting.journal");

        var result = await Repo().ResolveAgentAsync(_tenantA, _accounting, _userAccounting, isAdmin: false, "accounting.journal", default);
        Assert.Equal(_accountingAgentId, result.AgentId);
    }

    [Fact]
    public async Task UserAccounting_NoDirectAssignment_CapabilityAbsentAndDenied()
    {
        if (_scopedConnectionString is null) return;
        await AssignAgentToDepartmentAsync(_accountingAgentId, _accounting);

        var visible = await Repo().ListForPrincipalAsync(_tenantA, _userAccounting, default);
        Assert.Empty(visible);

        var result = await Repo().ResolveAgentAsync(_tenantA, _accounting, _userAccounting, isAdmin: false, "accounting.journal", default);
        Assert.Null(result.AgentId);
        Assert.Equal("capability_not_available", result.DenialReason);
    }

    [Fact]
    public async Task UserAccounting_DepartmentRevoke_Denied()
    {
        if (_scopedConnectionString is null) return;
        await AssignAgentToDepartmentAsync(_accountingAgentId, _accounting);
        await AssignUserToAgentAsync(_accountingAgentId, _userAccounting);
        await using (var conn = new NpgsqlConnection(_scopedConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "UPDATE agent_department_assignments SET status='revoked', revoked_at=NOW() WHERE agent_id=@a AND department_id=@d",
                new { a = _accountingAgentId, d = _accounting });
        }
        var result = await Repo().ResolveAgentAsync(_tenantA, _accounting, _userAccounting, isAdmin: false, "accounting.journal", default);
        Assert.Null(result.AgentId);
    }

    [Fact]
    public async Task UserAccounting_TenantRevoke_Denied()
    {
        if (_scopedConnectionString is null) return;
        await AssignAgentToDepartmentAsync(_accountingAgentId, _accounting);
        await AssignUserToAgentAsync(_accountingAgentId, _userAccounting);
        await using (var conn = new NpgsqlConnection(_scopedConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "UPDATE agent_tenant_assignments SET status='revoked', revoked_at=NOW() WHERE agent_id=@a AND tenant_id=@t",
                new { a = _accountingAgentId, t = _tenantA });
        }
        var result = await Repo().ResolveAgentAsync(_tenantA, _accounting, _userAccounting, isAdmin: false, "accounting.journal", default);
        Assert.Null(result.AgentId);
    }

    // ==== Ambiguity ==========================================================

    [Fact]
    public async Task Ambiguity_TwoEligibleAgents_NoBinding_ReturnsAmbiguous()
    {
        if (_scopedConnectionString is null) return;
        await AssignAgentToDepartmentAsync(_accountingAgentId, _accounting);
        await AssignAgentToDepartmentAsync(_accountingAgent2Id, _accounting);
        // Both Accounting Agent and Accounting Agent 2 declare accounting.journal.
        var result = await Repo().ResolveAgentAsync(_tenantA, _accounting, null, isAdmin: true, "accounting.journal", default);
        Assert.Null(result.AgentId);
        Assert.Equal("capability_ambiguous", result.DenialReason);
    }

    [Fact]
    public async Task Ambiguity_ValidBinding_ResolvesDeterministically()
    {
        if (_scopedConnectionString is null) return;
        await AssignAgentToDepartmentAsync(_accountingAgentId, _accounting);
        await AssignAgentToDepartmentAsync(_accountingAgent2Id, _accounting);
        Assert.True(await Repo().SetBindingAsync(_tenantA, _accounting, _journalCapabilityId, _accountingAgent2Id, _ownerA, default));

        var result = await Repo().ResolveAgentAsync(_tenantA, _accounting, null, isAdmin: true, "accounting.journal", default);
        Assert.Equal(_accountingAgent2Id, result.AgentId);
    }

    [Fact]
    public async Task Binding_BoundAgentNotEligibleForCaller_DeniesRatherThanFallingBack()
    {
        if (_scopedConnectionString is null) return;
        // Bind to Accounting Agent 2, but the USER only has a direct
        // assignment to Accounting Agent (#1) — the binding is authoritative,
        // so this must deny, never silently fall back to agent #1.
        await AssignAgentToDepartmentAsync(_accountingAgentId, _accounting);
        await AssignAgentToDepartmentAsync(_accountingAgent2Id, _accounting);
        await AssignUserToAgentAsync(_accountingAgentId, _userAccounting);
        Assert.True(await Repo().SetBindingAsync(_tenantA, _accounting, _journalCapabilityId, _accountingAgent2Id, _ownerA, default));

        var result = await Repo().ResolveAgentAsync(_tenantA, _accounting, _userAccounting, isAdmin: false, "accounting.journal", default);
        Assert.Null(result.AgentId);
        Assert.Equal("capability_not_available", result.DenialReason);
    }

    [Fact]
    public async Task Binding_ReplacePointsToNewAgent()
    {
        if (_scopedConnectionString is null) return;
        await AssignAgentToDepartmentAsync(_accountingAgentId, _accounting);
        await AssignAgentToDepartmentAsync(_accountingAgent2Id, _accounting);
        Assert.True(await Repo().SetBindingAsync(_tenantA, _accounting, _journalCapabilityId, _accountingAgentId, _ownerA, default));
        Assert.True(await Repo().SetBindingAsync(_tenantA, _accounting, _journalCapabilityId, _accountingAgent2Id, _ownerA, default));

        var items = await Repo().ListDepartmentCapabilitiesAsync(_tenantA, _accounting, default);
        var journal = Assert.Single(items, i => i.Code == "accounting.journal");
        Assert.Equal(_accountingAgent2Id, journal.BoundAgentId);
    }

    [Fact]
    public async Task Binding_RevokeClearsIt()
    {
        if (_scopedConnectionString is null) return;
        await AssignAgentToDepartmentAsync(_accountingAgentId, _accounting);
        Assert.True(await Repo().SetBindingAsync(_tenantA, _accounting, _journalCapabilityId, _accountingAgentId, _ownerA, default));
        Assert.True(await Repo().RevokeBindingAsync(_tenantA, _accounting, _journalCapabilityId, default));

        var items = await Repo().ListDepartmentCapabilitiesAsync(_tenantA, _accounting, default);
        var journal = Assert.Single(items, i => i.Code == "accounting.journal");
        Assert.Null(journal.BoundAgentId);
    }

    [Fact]
    public async Task Binding_WrongDepartmentDenied()
    {
        if (_scopedConnectionString is null) return;
        // Accounting Agent is never assigned to Sales — binding must fail
        // (the DB trigger enforces this even if the endpoint layer didn't).
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            Repo().SetBindingAsync(_tenantA, _sales, _journalCapabilityId, _accountingAgentId, _ownerA, default));
        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public async Task Binding_NonDepartmentAgentDenied()
    {
        if (_scopedConnectionString is null) return;
        // Sales Agent was never assigned to Accounting either.
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            Repo().SetBindingAsync(_tenantA, _accounting, _journalCapabilityId, _salesAgentId, _ownerA, default));
        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public async Task Binding_AgentNotSupportingCapabilityDenied()
    {
        if (_scopedConnectionString is null) return;
        // Sales Agent gets assigned to Accounting department for this test,
        // but its published version never declared accounting.journal.
        await AssignAgentToDepartmentAsync(_salesAgentId, _accounting);
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            Repo().SetBindingAsync(_tenantA, _accounting, _journalCapabilityId, _salesAgentId, _ownerA, default));
        Assert.Equal("23514", ex.SqlState);
    }
}
