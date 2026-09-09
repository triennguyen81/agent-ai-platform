using System.Text.Json;
using Aiq.Gateway.Data;
using Aiq.Gateway.Governance;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// ACL V2-E — Department Workflow / Skill / Knowledge ownership. Same
/// fixture pattern as <see cref="DepartmentCapabilityOwnershipIntegrationTests"/>
/// (hand-rolled baseline + real migrations 017-019, 021, 022, 024, 025, and
/// the new 026), proving: immutable Workflow draft/publish versioning
/// (migration 026), Department Skill content lock-once-published, and
/// Department Knowledge (notes scope='department') isolation +
/// Department-Agent attachment — see ACL_V2_V2E_WORKFLOW_SKILL_KNOWLEDGE_REPORT.md.
///
/// Fixture:
///   Tenant A: Accounting {Admin Accounting}, Sales {Admin Sales}
///   Tenant B: Other Department {Admin B}
/// </summary>
public sealed class DepartmentWorkflowSkillKnowledgeIntegrationTests : IAsyncLifetime
{
    static DepartmentWorkflowSkillKnowledgeIntegrationTests() =>
        Dapper.SqlMapper.AddTypeHandler(new StringArrayTypeHandler());

    private readonly string? _baseConnectionString = ResolveTestConnectionString();
    private readonly string _schema = $"dept_v2e_test_{Guid.NewGuid():N}";
    private string? _scopedConnectionString;
    private DynamicAgentRepository _agentRepo = null!;
    private WorkflowRepository _workflowRepo = null!;
    private SkillRepository _skillRepo = null!;
    private DepartmentKnowledgeRepository _knowledgeRepo = null!;

    private Guid _tenantA, _tenantB;
    private Guid _accounting, _sales, _otherDept;
    private Guid _adminAccounting, _adminSales, _adminB;

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
        await scoped.ExecuteAsync(await ReadMigrationAsync("026_department_workflow_versions_and_knowledge_archive.sql"));

        await AssignDepartmentsAsync(scoped);

        var factory = new NpgsqlConnectionFactory(_scopedConnectionString);
        _agentRepo = new DynamicAgentRepository(factory);
        _workflowRepo = new WorkflowRepository(factory);
        _skillRepo = new SkillRepository(factory);
        _knowledgeRepo = new DepartmentKnowledgeRepository(factory);
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
            CREATE TABLE agent_version_skill_assignments (
                agent_version_id UUID NOT NULL, skill_name TEXT NOT NULL, assigned_by UUID,
                CHECK (skill_name ~ '^[a-z0-9]+(?:-[a-z0-9]+)*$' AND length(skill_name) BETWEEN 2 AND 120)
            );
            CREATE TABLE agent_version_delegates (agent_version_id UUID NOT NULL, specialist_agent_id UUID NOT NULL, added_by UUID);
            CREATE TABLE tools (
                id UUID PRIMARY KEY, code TEXT NOT NULL, name TEXT NOT NULL, description TEXT, category TEXT,
                risk_tier TEXT NOT NULL DEFAULT 'low', is_active BOOLEAN NOT NULL DEFAULT true, min_action_tier TEXT NOT NULL DEFAULT 'suggest');
            CREATE TABLE workflows (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), name TEXT NOT NULL, description TEXT,
                is_active BOOLEAN NOT NULL DEFAULT true, definition JSONB NOT NULL DEFAULT '{"steps":[],"requiresApproval":false}'::jsonb,
                created_by UUID, created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
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
        _adminAccounting = Guid.NewGuid(); _adminSales = Guid.NewGuid(); _adminB = Guid.NewGuid();
        var ownerA = Guid.NewGuid(); var ownerB = Guid.NewGuid();

        var tenantA = _tenantA; var tenantB = _tenantB;
        var adminAccounting = _adminAccounting; var adminSales = _adminSales; var adminB = _adminB;

        await conn.ExecuteAsync("""
            INSERT INTO tenants(id,code,name,status) VALUES
                (@tenantA,'tenant-a','Tenant A','active'),
                (@tenantB,'tenant-b','Tenant B','active');
            INSERT INTO users(id,email,password_hash,tenant_id,is_active) VALUES
                (@ownerA,'owner-a@v2etest','x',@tenantA,true),
                (@ownerB,'owner-b@v2etest','x',@tenantB,true),
                (@adminAccounting,'admin-accounting@v2etest','x',@tenantA,true),
                (@adminSales,'admin-sales@v2etest','x',@tenantA,true),
                (@adminB,'admin-b@v2etest','x',@tenantB,true);
            INSERT INTO user_roles(user_id,role_id)
            SELECT @ownerA,id FROM roles WHERE name='tenant_owner'
            UNION ALL SELECT @ownerB,id FROM roles WHERE name='tenant_owner'
            UNION ALL SELECT @adminAccounting,id FROM roles WHERE name='admin'
            UNION ALL SELECT @adminSales,id FROM roles WHERE name='admin'
            UNION ALL SELECT @adminB,id FROM roles WHERE name='admin';
            """, new { tenantA, tenantB, ownerA, ownerB, adminAccounting, adminSales, adminB });
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
        var adminAccounting = _adminAccounting; var adminSales = _adminSales; var adminB = _adminB;
        await conn.ExecuteAsync("""
            UPDATE users SET department_id=@accounting WHERE id=@adminAccounting;
            UPDATE users SET department_id=@sales WHERE id=@adminSales;
            UPDATE users SET department_id=@otherDept WHERE id=@adminB;
            """, new { accounting, sales, otherDept, adminAccounting, adminSales, adminB });
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

    private static CreateAgentVersionRequest NewVersionRequest(Guid? workflowId = null, string[]? skills = null) =>
        new("claude_sdk", "sonnet", "You are a department agent.", 0.2m, 4, 120, 2048, null, null, skills, workflowId, null, null);

    private static JsonElement Def(params string[] promptTexts)
    {
        var steps = string.Join(",", promptTexts.Select(p => $$"""{"type":"prompt","prompt":"{{p}}"}"""));
        return JsonDocument.Parse($$"""{"steps":[{{steps}}],"requiresApproval":false}""").RootElement;
    }

    // ---------------------------------------------------------------------
    // Workflow: immutable draft/publish versioning + ownership
    // ---------------------------------------------------------------------

    [Fact]
    public async Task DepartmentWorkflow_CreatePublishNewDraft_PreservesPublishedDefinitionImmutably()
    {
        if (_scopedConnectionString is null) return;
        var workflow = await _workflowRepo.CreateDepartmentWorkflowAsync(_tenantA, _accounting, _adminAccounting,
            new CreateDepartmentWorkflowRequest("Invoice Review", null, Def("step one")), default);
        Assert.Equal("department", workflow.Scope);
        Assert.NotNull(workflow.CurrentDraftVersionId);
        Assert.Null(workflow.PublishedVersionId);

        var published = await _workflowRepo.PublishDepartmentWorkflowVersionAsync(
            _tenantA, _accounting, workflow.Id, workflow.CurrentDraftVersionId!.Value, _adminAccounting, default);
        Assert.NotNull(published);
        Assert.Equal("published", published!.Status);

        // Mutating that same version row is rejected at the DB level (trigger).
        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();
        await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(
            "UPDATE workflow_versions SET definition='{\"steps\":[],\"requiresApproval\":false}'::jsonb WHERE id=@id",
            new { id = published.Id }));

        // A new draft (v2) can be created and edited without touching v1's content.
        var draft2 = await _workflowRepo.CreateDepartmentWorkflowDraftVersionAsync(
            _tenantA, _accounting, workflow.Id, _adminAccounting, Def("step two"), default);
        Assert.NotNull(draft2);
        Assert.Equal(2, draft2!.Version);
        Assert.Equal("draft", draft2.Status);

        var stillV1 = (await _workflowRepo.ListDepartmentWorkflowVersionsAsync(_tenantA, _accounting, workflow.Id, default))
            .Single(v => v.Version == 1);
        Assert.Equal("published", stillV1.Status);
        Assert.Contains("step one", stillV1.DefinitionJson);
    }

    [Fact]
    public async Task DepartmentWorkflow_OnlyOneDraftAtATime()
    {
        if (_scopedConnectionString is null) return;
        var workflow = await _workflowRepo.CreateDepartmentWorkflowAsync(_tenantA, _accounting, _adminAccounting,
            new CreateDepartmentWorkflowRequest("Two Drafts", null, null), default);
        // A second draft-version create while one already exists conflicts.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _workflowRepo.CreateDepartmentWorkflowDraftVersionAsync(_tenantA, _accounting, workflow.Id, _adminAccounting, null, default));
    }

    [Fact]
    public async Task AdminSales_CannotReadEditOrPublishAccountingsWorkflow()
    {
        if (_scopedConnectionString is null) return;
        var workflow = await _workflowRepo.CreateDepartmentWorkflowAsync(_tenantA, _accounting, _adminAccounting,
            new CreateDepartmentWorkflowRequest("Accounting Only", null, null), default);

        Assert.Null(await _workflowRepo.FindOwnDepartmentWorkflowAsync(_tenantA, _sales, workflow.Id, default));
        Assert.Null(await _workflowRepo.UpdateDepartmentWorkflowMetadataAsync(_tenantA, _sales, workflow.Id,
            new UpdateDepartmentWorkflowRequest("Hacked", null), default));
        Assert.Null(await _workflowRepo.PublishDepartmentWorkflowVersionAsync(
            _tenantA, _sales, workflow.Id, workflow.CurrentDraftVersionId!.Value, _adminSales, default));

        // Tenant B likewise denied even with the exact UUID.
        Assert.Null(await _workflowRepo.FindOwnDepartmentWorkflowAsync(_tenantB, _otherDept, workflow.Id, default));
    }

    [Fact]
    public async Task AttachingAnotherDepartmentsUnpublishedWorkflow_IsDenied_PublishedOneIsAllowed()
    {
        if (_scopedConnectionString is null) return;
        var salesWorkflow = await _workflowRepo.CreateDepartmentWorkflowAsync(_tenantA, _sales, _adminSales,
            new CreateDepartmentWorkflowRequest("Sales Only", null, Def("sales step")), default);

        var agent = await _agentRepo.CreateDepartmentAgentAsync(_tenantA, _accounting, _adminAccounting,
            NewAgentRequest("wf-guard", "WF Guard"), default);

        // Cross-department denial, regardless of publish state.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _agentRepo.CreateDepartmentAgentVersionAsync(_tenantA, _accounting, agent.Id, _adminAccounting,
                NewVersionRequest(workflowId: salesWorkflow.Id), default));

        // Even Accounting's OWN workflow is ineligible while still a draft
        // (no immutable published content exists yet to attach).
        var ownDraftWorkflow = await _workflowRepo.CreateDepartmentWorkflowAsync(_tenantA, _accounting, _adminAccounting,
            new CreateDepartmentWorkflowRequest("Own Draft", null, Def("draft step")), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _agentRepo.CreateDepartmentAgentVersionAsync(_tenantA, _accounting, agent.Id, _adminAccounting,
                NewVersionRequest(workflowId: ownDraftWorkflow.Id), default));

        // Publishing it makes it eligible.
        var publishedOwn = await _workflowRepo.PublishDepartmentWorkflowVersionAsync(
            _tenantA, _accounting, ownDraftWorkflow.Id, ownDraftWorkflow.CurrentDraftVersionId!.Value, _adminAccounting, default);
        Assert.NotNull(publishedOwn);
        var version = await _agentRepo.CreateDepartmentAgentVersionAsync(_tenantA, _accounting, agent.Id, _adminAccounting,
            NewVersionRequest(workflowId: ownDraftWorkflow.Id), default);
        Assert.NotNull(version);
        Assert.Equal(ownDraftWorkflow.Id, version!.WorkflowId);
    }

    [Fact]
    public async Task DepartmentWorkflow_PublishingAnEmptyStepListIsDenied()
    {
        if (_scopedConnectionString is null) return;
        // Created with definition:null -> default {"steps":[],"requiresApproval":false}.
        var workflow = await _workflowRepo.CreateDepartmentWorkflowAsync(_tenantA, _accounting, _adminAccounting,
            new CreateDepartmentWorkflowRequest("No Steps Yet", null, null), default);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _workflowRepo.PublishDepartmentWorkflowVersionAsync(
                _tenantA, _accounting, workflow.Id, workflow.CurrentDraftVersionId!.Value, _adminAccounting, default));
        Assert.Equal("workflow_has_no_steps", ex.Message);

        // Still a draft — nothing was published.
        var version = (await _workflowRepo.ListDepartmentWorkflowVersionsAsync(_tenantA, _accounting, workflow.Id, default)).Single();
        Assert.Equal("draft", version.Status);

        // Adding a step makes the exact same draft publishable.
        var withStep = await _workflowRepo.UpdateDepartmentWorkflowDraftVersionAsync(
            _tenantA, _accounting, workflow.Id, workflow.CurrentDraftVersionId!.Value, Def("now has a step"), default);
        Assert.NotNull(withStep);
        var published = await _workflowRepo.PublishDepartmentWorkflowVersionAsync(
            _tenantA, _accounting, workflow.Id, workflow.CurrentDraftVersionId!.Value, _adminAccounting, default);
        Assert.NotNull(published);
        Assert.Equal("published", published!.Status);
    }

    [Fact]
    public async Task AdminAccounting_CannotEditOrPublishAPlatformScopedWorkflow()
    {
        if (_scopedConnectionString is null) return;
        // Platform (Global) workflows have scope='platform' and no tenant/department —
        // simulate one directly, matching how SUPER_ADMIN's /admin/workflows create flow persists it.
        Guid platformWorkflowId;
        Guid platformDraftVersionId;
        await using (var conn = new NpgsqlConnection(_scopedConnectionString))
        {
            await conn.OpenAsync();
            platformWorkflowId = await conn.ExecuteScalarAsync<Guid>("""
                INSERT INTO workflows (scope, tenant_id, department_id, name, is_active)
                VALUES ('platform', NULL, NULL, 'Global Onboarding', true)
                RETURNING id
                """);
            platformDraftVersionId = await conn.ExecuteScalarAsync<Guid>("""
                INSERT INTO workflow_versions (workflow_id, version, status, definition, created_by)
                VALUES (@workflowId, 1, 'draft', '{"steps":[{"type":"prompt","prompt":"global step"}],"requiresApproval":false}'::jsonb, @actorId)
                RETURNING id
                """, new { workflowId = platformWorkflowId, actorId = _adminAccounting });
        }

        // ADMIN's own-department scoped repository calls never match a platform-scope row —
        // no read, no metadata edit, no draft edit, no publish, regardless of tenant/department.
        Assert.Null(await _workflowRepo.FindOwnDepartmentWorkflowAsync(_tenantA, _accounting, platformWorkflowId, default));
        Assert.Null(await _workflowRepo.UpdateDepartmentWorkflowMetadataAsync(_tenantA, _accounting, platformWorkflowId,
            new UpdateDepartmentWorkflowRequest("Hacked Global", null), default));
        Assert.Null(await _workflowRepo.UpdateDepartmentWorkflowDraftVersionAsync(
            _tenantA, _accounting, platformWorkflowId, platformDraftVersionId, Def("hacked"), default));
        Assert.Null(await _workflowRepo.PublishDepartmentWorkflowVersionAsync(
            _tenantA, _accounting, platformWorkflowId, platformDraftVersionId, _adminAccounting, default));

        // The platform workflow itself is untouched.
        await using var verify = new NpgsqlConnection(_scopedConnectionString);
        await verify.OpenAsync();
        var name = await verify.ExecuteScalarAsync<string>(
            "SELECT name FROM workflows WHERE id=@id", new { id = platformWorkflowId });
        Assert.Equal("Global Onboarding", name);
        var status = await verify.ExecuteScalarAsync<string>(
            "SELECT status FROM workflow_versions WHERE id=@id", new { id = platformDraftVersionId });
        Assert.Equal("draft", status);
    }

    // ---------------------------------------------------------------------
    // Skill: declarative content ownership + publish-lock
    // ---------------------------------------------------------------------

    [Fact]
    public async Task DepartmentSkill_CreateEditEnableDisable_OwnDepartmentOnly()
    {
        if (_scopedConnectionString is null) return;
        var skill = await _skillRepo.CreateDepartmentSkillAsync(_tenantA, _accounting, _adminAccounting,
            new CreateDepartmentSkillRequest("invoice-review", "Invoice Review", null, "Review the invoice line items for accuracy."), default);
        Assert.Equal("department", skill.Scope);

        Assert.NotNull(await _skillRepo.UpdateDepartmentSkillMetadataAsync(_tenantA, _accounting, skill.Id,
            new UpdateDepartmentSkillMetadataRequest("Invoice Review v2", null), default));
        Assert.NotNull(await _skillRepo.UpdateDepartmentSkillContentAsync(_tenantA, _accounting, skill.Id,
            new UpdateDepartmentSkillContentRequest("Updated instructions."), default));
        Assert.NotNull(await _skillRepo.SetDepartmentSkillEnabledAsync(_tenantA, _accounting, skill.Id, false, default));

        // Sales cannot touch it at all, even with the known UUID.
        Assert.Null(await _skillRepo.FindOwnDepartmentSkillAsync(_tenantA, _sales, skill.Id, default));
        Assert.Null(await _skillRepo.UpdateDepartmentSkillMetadataAsync(_tenantA, _sales, skill.Id,
            new UpdateDepartmentSkillMetadataRequest("Hacked", null), default));
    }

    [Fact]
    public async Task DepartmentSkill_ContentLockedOnceReferencedByAPublishedAgentVersion()
    {
        if (_scopedConnectionString is null) return;
        var skill = await _skillRepo.CreateDepartmentSkillAsync(_tenantA, _accounting, _adminAccounting,
            new CreateDepartmentSkillRequest("journal-helper", "Journal Helper", null, "Original instructions."), default);

        var agent = await _agentRepo.CreateDepartmentAgentAsync(_tenantA, _accounting, _adminAccounting,
            NewAgentRequest("skill-user", "Skill User"), default);
        var version = await _agentRepo.CreateDepartmentAgentVersionAsync(_tenantA, _accounting, agent.Id, _adminAccounting,
            NewVersionRequest(skills: ["journal-helper"]), default);
        Assert.NotNull(version);
        Assert.Contains("journal-helper", version!.Skills);

        // Still editable while only a DRAFT references it.
        Assert.NotNull(await _skillRepo.UpdateDepartmentSkillContentAsync(_tenantA, _accounting, skill.Id,
            new UpdateDepartmentSkillContentRequest("Edited while draft."), default));

        var published = await _agentRepo.PublishDepartmentAgentVersionAsync(_tenantA, _accounting, agent.Id, version.Id, _adminAccounting, default);
        Assert.NotNull(published);

        // Now locked — editing content throws (endpoint translates to 409).
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _skillRepo.UpdateDepartmentSkillContentAsync(_tenantA, _accounting, skill.Id,
                new UpdateDepartmentSkillContentRequest("Attempted post-publish edit."), default));

        // Metadata (name/description) is NOT locked — only content is.
        Assert.NotNull(await _skillRepo.UpdateDepartmentSkillMetadataAsync(_tenantA, _accounting, skill.Id,
            new UpdateDepartmentSkillMetadataRequest("Renamed After Publish", null), default));
    }

    [Fact]
    public async Task AttachingAnotherDepartmentsSkillCode_IsDenied_UnknownFilesystemNameStillAllowed()
    {
        if (_scopedConnectionString is null) return;
        await _skillRepo.CreateDepartmentSkillAsync(_tenantA, _sales, _adminSales,
            new CreateDepartmentSkillRequest("sales-pitch", "Sales Pitch", null, "Sales-only instructions."), default);

        var agent = await _agentRepo.CreateDepartmentAgentAsync(_tenantA, _accounting, _adminAccounting,
            NewAgentRequest("skill-guard", "Skill Guard"), default);

        // Explicitly another department's OWN skill code -> denied.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _agentRepo.CreateDepartmentAgentVersionAsync(_tenantA, _accounting, agent.Id, _adminAccounting,
                NewVersionRequest(skills: ["sales-pitch"]), default));

        // An arbitrary name unknown to the `skills` catalog at all (e.g. a
        // pre-existing filesystem/platform SKILL.md never migrated into the
        // table) is NOT rejected — preserves 100% pre-V2-E compatibility.
        var version = await _agentRepo.CreateDepartmentAgentVersionAsync(_tenantA, _accounting, agent.Id, _adminAccounting,
            NewVersionRequest(skills: ["some-legacy-filesystem-skill"]), default);
        Assert.NotNull(version);
        Assert.Contains("some-legacy-filesystem-skill", version!.Skills);
    }

    // ---------------------------------------------------------------------
    // Knowledge: department notes isolation + Department Agent attachment
    // ---------------------------------------------------------------------

    [Fact]
    public async Task DepartmentKnowledge_CreateListArchiveDelete_OwnDepartmentOnly()
    {
        if (_scopedConnectionString is null) return;
        var note = await _knowledgeRepo.CreateAsync(_tenantA, _accounting, _adminAccounting,
            new CreateDepartmentKnowledgeRequest("Chart of Accounts", "1000 - Cash\n2000 - AP", ["coa"]), default);

        var list = await _knowledgeRepo.ListOwnAsync(_tenantA, _accounting, default);
        Assert.Contains(list, n => n.Id == note.Id);

        // Sales cannot see, edit, or delete Accounting's Knowledge item.
        Assert.Null(await _knowledgeRepo.FindOwnAsync(_tenantA, _sales, note.Id, default));
        Assert.Null(await _knowledgeRepo.UpdateAsync(_tenantA, _sales, note.Id,
            new UpdateDepartmentKnowledgeRequest("Hacked", null, null), default));
        Assert.False(await _knowledgeRepo.DeleteAsync(_tenantA, _sales, note.Id, default));

        // Archive removes it from the active list without deleting it.
        Assert.NotNull(await _knowledgeRepo.SetArchivedAsync(_tenantA, _accounting, note.Id, true, default));
        Assert.DoesNotContain(await _knowledgeRepo.ListOwnAsync(_tenantA, _accounting, default), n => n.Id == note.Id);

        Assert.True(await _knowledgeRepo.DeleteAsync(_tenantA, _accounting, note.Id, default));
    }

    [Fact]
    public async Task DepartmentKnowledge_DeleteRefusedWhileAttachedToAnAgent()
    {
        if (_scopedConnectionString is null) return;
        var note = await _knowledgeRepo.CreateAsync(_tenantA, _accounting, _adminAccounting,
            new CreateDepartmentKnowledgeRequest("Tax Rules", "VAT 10%", null), default);
        var agent = await _agentRepo.CreateDepartmentAgentAsync(_tenantA, _accounting, _adminAccounting,
            NewAgentRequest("knowledge-user", "Knowledge User"), default);

        Assert.True(await _knowledgeRepo.AttachToDepartmentAgentAsync(_tenantA, _accounting, agent.Id, note.Id, _adminAccounting, default));
        Assert.False(await _knowledgeRepo.DeleteAsync(_tenantA, _accounting, note.Id, default));

        Assert.True(await _knowledgeRepo.DetachFromDepartmentAgentAsync(_tenantA, _accounting, agent.Id, note.Id, default));
        Assert.True(await _knowledgeRepo.DeleteAsync(_tenantA, _accounting, note.Id, default));
    }

    [Fact]
    public async Task AttachingAnotherDepartmentsKnowledge_IsDenied_ManifestOnlyEverSeesAttachedItems()
    {
        if (_scopedConnectionString is null) return;
        var salesNote = await _knowledgeRepo.CreateAsync(_tenantA, _sales, _adminSales,
            new CreateDepartmentKnowledgeRequest("Sales Playbook", "Confidential", null), default);
        var agent = await _agentRepo.CreateDepartmentAgentAsync(_tenantA, _accounting, _adminAccounting,
            NewAgentRequest("knowledge-guard", "Knowledge Guard"), default);

        // Cannot attach Sales's note to Accounting's agent.
        Assert.False(await _knowledgeRepo.AttachToDepartmentAgentAsync(_tenantA, _accounting, agent.Id, salesNote.Id, _adminAccounting, default));
        Assert.Empty(await _knowledgeRepo.ListForDepartmentAgentAsync(_tenantA, _accounting, agent.Id, default));

        // Sales's own note never appears in Accounting's "attachable" list either.
        var attachable = await _knowledgeRepo.ListAttachableForDepartmentAgentAsync(_tenantA, _accounting, agent.Id, default);
        Assert.DoesNotContain(attachable, n => n.Id == salesNote.Id);

        // A cross-tenant agent id (Tenant B) can never attach Tenant A's note either.
        var agentB = await _agentRepo.CreateDepartmentAgentAsync(_tenantB, _otherDept, _adminB, NewAgentRequest("b-agent", "B Agent"), default);
        Assert.False(await _knowledgeRepo.AttachToDepartmentAgentAsync(_tenantB, _otherDept, agentB.Id, salesNote.Id, _adminB, default));
    }

    [Fact]
    public async Task DepartmentKnowledge_NeverAppearsOnAnyPersonalNotesQuery()
    {
        if (_scopedConnectionString is null) return;
        var note = await _knowledgeRepo.CreateAsync(_tenantA, _accounting, _adminAccounting,
            new CreateDepartmentKnowledgeRequest("Department Shared Note", "shared", null), default);

        // Department Knowledge is stored with user_id=NULL specifically so it
        // can never surface on the creator's OWN personal-notes query
        // (INoteRepository.ListAsync always filters `AND user_id=@userId`).
        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();
        var userId = await conn.ExecuteScalarAsync<Guid?>("SELECT user_id FROM notes WHERE id=@id", new { id = note.Id });
        Assert.Null(userId);
    }

    [Fact]
    public async Task UnifiedAdminResourceLists_ReturnGlobalAndOwnDepartment_ExcludeSalesAndTenantB()
    {
        if (_scopedConnectionString is null) return;

        var globalWorkflow = await _workflowRepo.CreateAsync(_adminAccounting, new CreateWorkflowRequest("Global Workflow", null, null), default);
        var ownWorkflow = await _workflowRepo.CreateDepartmentWorkflowAsync(_tenantA, _accounting, _adminAccounting, new CreateDepartmentWorkflowRequest("Accounting Workflow", null, null), default);
        var salesWorkflow = await _workflowRepo.CreateDepartmentWorkflowAsync(_tenantA, _sales, _adminSales, new CreateDepartmentWorkflowRequest("Sales Workflow", null, null), default);
        var bWorkflow = await _workflowRepo.CreateDepartmentWorkflowAsync(_tenantB, _otherDept, _adminB, new CreateDepartmentWorkflowRequest("B Workflow", null, null), default);
        var workflows = await _workflowRepo.ListOwnDepartmentWorkflowsAsync(_tenantA, _accounting, default);
        Assert.Contains(workflows, x => x.Id == globalWorkflow.Id && x.Scope == "platform");
        Assert.Contains(workflows, x => x.Id == ownWorkflow.Id);
        Assert.DoesNotContain(workflows, x => x.Id == salesWorkflow.Id || x.Id == bWorkflow.Id);
        Assert.Null(await _workflowRepo.UpdateDepartmentWorkflowMetadataAsync(_tenantA, _accounting, globalWorkflow.Id, new UpdateDepartmentWorkflowRequest("forbidden", null), default));

        var globalSkill = await _skillRepo.CreatePlatformSkillAsync(_adminAccounting, new CreateDepartmentSkillRequest("global-skill", "Global Skill", null, "global"), default);
        var ownSkill = await _skillRepo.CreateDepartmentSkillAsync(_tenantA, _accounting, _adminAccounting, new CreateDepartmentSkillRequest("accounting-skill", "Accounting Skill", null, "own"), default);
        var salesSkill = await _skillRepo.CreateDepartmentSkillAsync(_tenantA, _sales, _adminSales, new CreateDepartmentSkillRequest("sales-skill", "Sales Skill", null, "sales"), default);
        var bSkill = await _skillRepo.CreateDepartmentSkillAsync(_tenantB, _otherDept, _adminB, new CreateDepartmentSkillRequest("tenantb-skill", "B Skill", null, "b"), default);
        var skills = await _skillRepo.ListOwnDepartmentSkillsAsync(_tenantA, _accounting, default);
        Assert.Contains(skills, x => x.Id == globalSkill.Id && x.Scope == "platform");
        Assert.Contains(skills, x => x.Id == ownSkill.Id);
        Assert.DoesNotContain(skills, x => x.Id == salesSkill.Id || x.Id == bSkill.Id);
        Assert.Null(await _skillRepo.UpdateDepartmentSkillMetadataAsync(_tenantA, _accounting, globalSkill.Id, new UpdateDepartmentSkillMetadataRequest("forbidden", null), default));

        var globalKnowledge = await _knowledgeRepo.CreatePlatformAsync(_adminAccounting, new CreateDepartmentKnowledgeRequest("Global Knowledge", "global", null), default);
        var ownKnowledge = await _knowledgeRepo.CreateAsync(_tenantA, _accounting, _adminAccounting, new CreateDepartmentKnowledgeRequest("Accounting Knowledge", "own", null), default);
        var salesKnowledge = await _knowledgeRepo.CreateAsync(_tenantA, _sales, _adminSales, new CreateDepartmentKnowledgeRequest("Sales Knowledge", "sales", null), default);
        var bKnowledge = await _knowledgeRepo.CreateAsync(_tenantB, _otherDept, _adminB, new CreateDepartmentKnowledgeRequest("B Knowledge", "b", null), default);
        var knowledge = await _knowledgeRepo.ListOwnAsync(_tenantA, _accounting, default);
        Assert.Contains(knowledge, x => x.Id == globalKnowledge.Id && x.Scope == "platform");
        Assert.Contains(knowledge, x => x.Id == ownKnowledge.Id);
        Assert.DoesNotContain(knowledge, x => x.Id == salesKnowledge.Id || x.Id == bKnowledge.Id);
        Assert.Null(await _knowledgeRepo.UpdateAsync(_tenantA, _accounting, globalKnowledge.Id, new UpdateDepartmentKnowledgeRequest("forbidden", null, null), default));

        var agent = await _agentRepo.CreateDepartmentAgentAsync(_tenantA, _accounting, _adminAccounting, NewAgentRequest("global-knowledge-agent", "Global Knowledge Agent"), default);
        var globalVersion = await _agentRepo.CreateDepartmentAgentVersionAsync(_tenantA, _accounting, agent.Id, _adminAccounting,
            NewVersionRequest(workflowId: globalWorkflow.Id, skills: [globalSkill.Code]), default);
        Assert.NotNull(globalVersion);
        Assert.Equal(globalWorkflow.Id, globalVersion!.WorkflowId);
        Assert.Contains(globalSkill.Code, globalVersion.Skills);
        Assert.True(await _knowledgeRepo.AttachToDepartmentAgentAsync(_tenantA, _accounting, agent.Id, globalKnowledge.Id, _adminAccounting, default));
        Assert.False(await _knowledgeRepo.AttachToDepartmentAgentAsync(_tenantA, _accounting, agent.Id, salesKnowledge.Id, _adminAccounting, default));
    }
}
