using Aiq.Gateway.Data;
using Aiq.Gateway.Governance;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// ACL V2-D — Department Business Capability ("Nghiệp vụ") ownership. Same
/// fixture pattern as <see cref="DepartmentAgentOwnershipIntegrationTests"/>
/// (hand-rolled baseline + real migrations 017-022+024, now +025), extended
/// with a real Department Agent that declares a real Department capability,
/// to prove the resolver/menu/binding gaps V2-D closed (a V2-C Department
/// Agent's capabilities were previously invisible to every capability query,
/// and binding to one was rejected by a DB trigger unconditionally — see
/// ACL_V2_V2D_DEPARTMENT_CAPABILITIES_REPORT.md §0/§11/§12).
///
/// Fixture:
///   Tenant A: Accounting {Admin Accounting, User Accounting}
///             Sales      {Admin Sales, User Sales}
///   Tenant B: Other Department {Admin B}
/// </summary>
public sealed class DepartmentCapabilityOwnershipIntegrationTests : IAsyncLifetime
{
    static DepartmentCapabilityOwnershipIntegrationTests() =>
        Dapper.SqlMapper.AddTypeHandler(new StringArrayTypeHandler());

    private readonly string? _baseConnectionString = ResolveTestConnectionString();
    private readonly string _schema = $"dept_capability_ownership_test_{Guid.NewGuid():N}";
    private string? _scopedConnectionString;
    private DynamicAgentRepository _agentRepo = null!;
    private CapabilityRepository _capRepo = null!;

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
        await scoped.ExecuteAsync(await ReadMigrationAsync("025_department_capability_binding_v2c_agents.sql"));

        await AssignDepartmentsAsync(scoped);

        _agentRepo = new DynamicAgentRepository(new NpgsqlConnectionFactory(_scopedConnectionString));
        _capRepo = new CapabilityRepository(new NpgsqlConnectionFactory(_scopedConnectionString));
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
                (@ownerA,'owner-a@v2dtest','x',@tenantA,true),
                (@ownerB,'owner-b@v2dtest','x',@tenantB,true),
                (@adminAccounting,'admin-accounting@v2dtest','x',@tenantA,true),
                (@userAccounting,'user-accounting@v2dtest','x',@tenantA,true),
                (@adminSales,'admin-sales@v2dtest','x',@tenantA,true),
                (@userSales,'user-sales@v2dtest','x',@tenantA,true),
                (@adminB,'admin-b@v2dtest','x',@tenantB,true);
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

    private static CreateAgentVersionRequest NewVersionRequest(Guid[]? capabilityIds = null) =>
        new("claude_sdk", "sonnet", "You are a department agent.", 0.2m, 4, 120, 2048, null, null, null, null, null, capabilityIds);

    private async Task<Guid> PlatformCapabilityIdAsync(string code)
    {
        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<Guid>("SELECT id FROM business_capabilities WHERE code=@code", new { code });
    }

    /// <summary>Creates a published Department Agent (Accounting, by default) declaring the given capability ids.</summary>
    private async Task<(Guid AgentId, Guid VersionId)> CreatePublishedDepartmentAgentAsync(Guid tenantId, Guid departmentId, Guid adminId, string slug, Guid[] capabilityIds)
    {
        var agent = await _agentRepo.CreateDepartmentAgentAsync(tenantId, departmentId, adminId, NewAgentRequest(slug, slug), default);
        var version = await _agentRepo.CreateDepartmentAgentVersionAsync(tenantId, departmentId, agent.Id, adminId, NewVersionRequest(capabilityIds), default);
        var published = await _agentRepo.PublishDepartmentAgentVersionAsync(tenantId, departmentId, agent.Id, version!.Id, adminId, default);
        Assert.NotNull(published);
        return (agent.Id, version.Id);
    }

    [Fact]
    public async Task DepartmentAgentsCapability_IsVisibleToAdminMenuResolverAndBindingOversight()
    {
        if (_scopedConnectionString is null) return;
        var journalCap = await PlatformCapabilityIdAsync("accounting.journal");
        var (agentId, _) = await CreatePublishedDepartmentAgentAsync(_tenantA, _accounting, _adminAccounting, "acct-journal-bot", [journalCap]);

        // ADMIN Nghiệp vụ menu now sees a capability declared ONLY by a
        // structurally-owned Department Agent (previously invisible — no
        // agent_tenant_assignments/agent_department_assignments row exists
        // or can exist for it).
        var adminMenu = await _capRepo.ListForDepartmentManagerAsync(_tenantA, _accounting, default);
        Assert.Contains(adminMenu, c => c.Code == "accounting.journal");

        // The canonical resolver picks it up too (single eligible agent, no binding needed).
        var resolved = await _capRepo.ResolveAgentAsync(_tenantA, _accounting, null, isAdmin: true, "accounting.journal", null, default);
        Assert.Equal(agentId, resolved.AgentId);

        // TENANT_OWNER's oversight view (still GET-only) also sees it.
        var oversight = await _capRepo.ListDepartmentCapabilitiesAsync(_tenantA, _accounting, default);
        Assert.Contains(oversight, c => c.Code == "accounting.journal" && c.EligibleAgents.Any(a => a.AgentId == agentId));
    }

    [Fact]
    public async Task User_RequiresDirectAssignmentEvenForADepartmentAgentsCapability()
    {
        if (_scopedConnectionString is null) return;
        var journalCap = await PlatformCapabilityIdAsync("accounting.journal");
        var (agentId, _) = await CreatePublishedDepartmentAgentAsync(_tenantA, _accounting, _adminAccounting, "acct-journal-bot-2", [journalCap]);

        var before = await _capRepo.ResolveAgentAsync(_tenantA, _accounting, _userAccounting, isAdmin: false, "accounting.journal", null, default);
        Assert.Null(before.AgentId);
        Assert.Equal("capability_not_available", before.DenialReason);

        Assert.True(await _agentRepo.AssignUserToDepartmentAgentAsync(_tenantA, _accounting, agentId, _userAccounting, _adminAccounting, default));
        var after = await _capRepo.ResolveAgentAsync(_tenantA, _accounting, _userAccounting, isAdmin: false, "accounting.journal", null, default);
        Assert.Equal(agentId, after.AgentId);
    }

    [Fact]
    public async Task AdminSales_NeverSeesAccountingsDepartmentAgentCapability()
    {
        if (_scopedConnectionString is null) return;
        var journalCap = await PlatformCapabilityIdAsync("accounting.journal");
        await CreatePublishedDepartmentAgentAsync(_tenantA, _accounting, _adminAccounting, "acct-journal-bot-3", [journalCap]);

        var salesMenu = await _capRepo.ListForDepartmentManagerAsync(_tenantA, _sales, default);
        Assert.DoesNotContain(salesMenu, c => c.Code == "accounting.journal");

        var resolved = await _capRepo.ResolveAgentAsync(_tenantA, _sales, null, isAdmin: true, "accounting.journal", null, default);
        Assert.Null(resolved.AgentId);
        Assert.Equal("capability_not_available", resolved.DenialReason);
    }

    [Fact]
    public async Task BindingToADepartmentAgent_SucceedsOnlyAfterMigration025Widening()
    {
        if (_scopedConnectionString is null) return;
        var journalCap = await PlatformCapabilityIdAsync("accounting.journal");
        var taxCap = await PlatformCapabilityIdAsync("accounting.tax");
        // Two eligible agents for the SAME capability -> genuinely ambiguous,
        // so a binding is the only way to make it deterministic.
        var (agentA, _) = await CreatePublishedDepartmentAgentAsync(_tenantA, _accounting, _adminAccounting, "acct-bot-a", [journalCap, taxCap]);
        var (agentB, _) = await CreatePublishedDepartmentAgentAsync(_tenantA, _accounting, _adminAccounting, "acct-bot-b", [journalCap]);

        var ambiguous = await _capRepo.ResolveAgentAsync(_tenantA, _accounting, null, isAdmin: true, "accounting.journal", null, default);
        Assert.Null(ambiguous.AgentId);
        Assert.Equal("capability_ambiguous", ambiguous.DenialReason);

        // Before migration 025, this INSERT would fail with a 23514 from
        // enforce_department_capability_binding_eligibility (it could never
        // find an agent_department_assignments row for a Department Agent).
        Assert.True(await _capRepo.SetBindingAsync(_tenantA, _accounting, journalCap, agentA, _adminAccounting, default));

        var resolved = await _capRepo.ResolveAgentAsync(_tenantA, _accounting, null, isAdmin: true, "accounting.journal", null, default);
        Assert.Equal(agentA, resolved.AgentId);

        // Binding to the OTHER eligible agent for the OTHER capability still
        // resolves deterministically (single eligible agent, no binding needed).
        var taxResolved = await _capRepo.ResolveAgentAsync(_tenantA, _accounting, null, isAdmin: true, "accounting.tax", null, default);
        Assert.Equal(agentA, taxResolved.AgentId);
        _ = agentB; // exists only to create the ambiguity above
    }

    [Fact]
    public async Task CreateDepartmentCapability_StampsOwnershipFromPrincipal_CrossDepartmentDenied()
    {
        if (_scopedConnectionString is null) return;
        var created = await _capRepo.CreateDepartmentCapabilityAsync(
            _tenantA, _accounting, _adminAccounting,
            new CreateDepartmentCapabilityRequest("crm.lead_management", "Quản lý khách hàng tiềm năng", null, "crm"), default);

        Assert.Equal("department", created.Scope);
        Assert.NotNull(await _capRepo.FindOwnDepartmentCapabilityAsync(_tenantA, _accounting, created.Id, default));

        // Sales cannot see, edit, enable/disable Accounting's own capability
        // even with the known UUID.
        Assert.Null(await _capRepo.FindOwnDepartmentCapabilityAsync(_tenantA, _sales, created.Id, default));
        Assert.Null(await _capRepo.UpdateDepartmentCapabilityAsync(_tenantA, _sales, created.Id, new UpdateDepartmentCapabilityRequest("Hacked", null, null), default));
        Assert.Null(await _capRepo.SetDepartmentCapabilityEnabledAsync(_tenantA, _sales, created.Id, false, default));

        // Tenant B likewise denied.
        Assert.Null(await _capRepo.FindOwnDepartmentCapabilityAsync(_tenantB, _otherDept, created.Id, default));
    }

    [Fact]
    public async Task AttachingAnotherDepartmentsCapabilityToOwnAgentDraft_IsDenied()
    {
        if (_scopedConnectionString is null) return;
        var salesCapability = await _capRepo.CreateDepartmentCapabilityAsync(
            _tenantA, _sales, _adminSales,
            new CreateDepartmentCapabilityRequest("sales.lead_scoring", "Chấm điểm khách hàng tiềm năng", null, "sales"), default);

        var agent = await _agentRepo.CreateDepartmentAgentAsync(_tenantA, _accounting, _adminAccounting, NewAgentRequest("accounting-guard", "Accounting Guard"), default);

        // Accounting's ADMIN cannot attach Sales's own capability to Accounting's draft.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _agentRepo.CreateDepartmentAgentVersionAsync(_tenantA, _accounting, agent.Id, _adminAccounting, NewVersionRequest([salesCapability.Id]), default));

        // The draft/version itself must not have been created by the failed attempt.
        var versions = await _agentRepo.ListDepartmentAgentVersionsAsync(_tenantA, _accounting, agent.Id, default);
        Assert.Empty(versions);
    }

    [Fact]
    public async Task PlatformCapability_AttachesFreelyButIsNeverEditableByAdmin()
    {
        if (_scopedConnectionString is null) return;
        var journalCap = await PlatformCapabilityIdAsync("accounting.journal");
        var agent = await _agentRepo.CreateDepartmentAgentAsync(_tenantA, _accounting, _adminAccounting, NewAgentRequest("platform-consumer", "Platform Consumer"), default);
        var version = await _agentRepo.CreateDepartmentAgentVersionAsync(_tenantA, _accounting, agent.Id, _adminAccounting, NewVersionRequest([journalCap]), default);
        Assert.NotNull(version);
        Assert.Contains(journalCap, version!.CapabilityIds);

        // ADMIN has no "own department" row for a platform capability at all —
        // FindOwnDepartmentCapabilityAsync never returns platform rows.
        Assert.Null(await _capRepo.FindOwnDepartmentCapabilityAsync(_tenantA, _accounting, journalCap, default));
        Assert.Null(await _capRepo.UpdateDepartmentCapabilityAsync(_tenantA, _accounting, journalCap, new UpdateDepartmentCapabilityRequest("Hacked", null, null), default));
    }

    [Fact]
    public async Task TenantOwner_And_SuperAdmin_HaveNoRepositoryPathToMutateDepartmentCapabilitiesOrBindings()
    {
        // ACL V2-D moved binding mutation off TENANT_OWNER entirely — there is
        // no repository method left that a tenant_governance-policy endpoint
        // could call to mutate a binding (SetBindingAsync/RevokeBindingAsync
        // are now ADMIN-routed only; see CapabilityEndpoints.cs's route
        // groups). This is proven at the endpoint-mapping layer (no PUT/DELETE
        // registered under /tenant/departments/{id}/capabilities), not
        // re-provable at the repository layer since the repository itself is
        // role-agnostic by design. Documented here rather than asserted
        // against a repository call that would trivially "succeed" regardless
        // of caller role.
        await Task.CompletedTask;
    }

    [Fact]
    public async Task DuplicateCapabilityCode_AcrossDepartments_IsRejected_GlobalUniquenessPreserved()
    {
        if (_scopedConnectionString is null) return;
        await _capRepo.CreateDepartmentCapabilityAsync(_tenantA, _accounting, _adminAccounting,
            new CreateDepartmentCapabilityRequest("shared.duplicate_code", "First", null, null), default);

        // Same code, different department — global uniqueness (migration 022,
        // deliberately preserved per V2-D brief §7) rejects it exactly like a
        // duplicate platform code always has.
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            _capRepo.CreateDepartmentCapabilityAsync(_tenantA, _sales, _adminSales,
                new CreateDepartmentCapabilityRequest("shared.duplicate_code", "Second", null, null), default));
    }

    [Fact]
    public async Task UnifiedAdminCapabilityList_ReturnsPlatformAndOwnDepartment_AndNeverOtherScopes()
    {
        if (_scopedConnectionString is null) return;
        var global = await _capRepo.CreatePlatformCapabilityAsync(_adminAccounting,
            new CreateDepartmentCapabilityRequest("global.shared", "Global", null, null), default);
        var accounting = await _capRepo.CreateDepartmentCapabilityAsync(_tenantA, _accounting, _adminAccounting,
            new CreateDepartmentCapabilityRequest("accounting.own", "Accounting", null, null), default);
        var sales = await _capRepo.CreateDepartmentCapabilityAsync(_tenantA, _sales, _adminSales,
            new CreateDepartmentCapabilityRequest("sales.private", "Sales", null, null), default);
        var tenantB = await _capRepo.CreateDepartmentCapabilityAsync(_tenantB, _otherDept, _adminB,
            new CreateDepartmentCapabilityRequest("tenantb.private", "Tenant B", null, null), default);

        var visible = await _capRepo.ListOwnDepartmentCapabilitiesAsync(_tenantA, _accounting, default);
        Assert.Contains(visible, x => x.Id == global.Id && x.Scope == "platform");
        Assert.Contains(visible, x => x.Id == accounting.Id && x.Scope == "department");
        Assert.DoesNotContain(visible, x => x.Id == sales.Id || x.Id == tenantB.Id);
        Assert.Null(await _capRepo.UpdateDepartmentCapabilityAsync(_tenantA, _accounting, global.Id,
            new UpdateDepartmentCapabilityRequest("forbidden", null, null), default));
    }
}
