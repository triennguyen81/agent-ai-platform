using Aiq.Gateway.Data;
using Aiq.Gateway.Governance;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// D3: covers the "agent listing used by Chat / My Agents" gap found while
/// auditing D3 — <c>GET /me/agents</c> (<see cref="IAgentRepository.ListForPrincipalAsync"/>
/// and the new <see cref="IAgentRepository.ListForDepartmentManagerAsync"/>)
/// previously only checked tenant entitlement + direct agent_assignments,
/// completely ignoring agent_department_assignments. That meant an ADMIN
/// with a real, active department entitlement but (correctly, per D3 policy)
/// zero direct agent_assignments rows saw an EMPTY "my agents" list — the
/// exact opposite of the approved execution model — and a USER whose
/// department entitlement had been revoked still saw a stale directly-
/// assigned agent as "visible" even though GetManifestAsync would already
/// deny executing it. This file proves the fix: listing now mirrors
/// IDynamicAgentRepository's manifest-resolution chain exactly.
/// </summary>
public sealed class AgentPrincipalVisibilityIntegrationTests : IAsyncLifetime
{
    private readonly string? _baseConnectionString = ResolveTestConnectionString();
    private readonly string _schema = $"agent_principal_visibility_test_{Guid.NewGuid():N}";
    private string? _scopedConnectionString;

    private Guid _tenantA, _tenantB;
    private Guid _accounting, _sales;
    private Guid _adminAccounting, _userAccounting, _userSales;
    private Guid _agentId;
    private Guid _deptAgentId;

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
        await scoped.ExecuteAsync("""
            CREATE TABLE tenants (id UUID PRIMARY KEY, status TEXT NOT NULL DEFAULT 'active');
            CREATE TABLE departments (id UUID PRIMARY KEY, tenant_id UUID NOT NULL, status TEXT NOT NULL DEFAULT 'active');
            CREATE TABLE users (id UUID PRIMARY KEY, tenant_id UUID NOT NULL, department_id UUID);
            -- Real G1/D3 "agents" shape (see IAgentRepository.AgentRepository.SelectColumns
            -- and migration 007_dynamic_agent_identity.sql: it is the SAME physical table
            -- IDynamicAgentRepository queries, unified across scope='tenant'/'system').
            -- v2_scope/owning_tenant_id/department_id (migration 024) carry a V2-C
            -- Department Agent's structural ownership directly on the agent row.
            CREATE TABLE agents (
                id UUID PRIMARY KEY, code TEXT NOT NULL, name TEXT NOT NULL, type TEXT NOT NULL DEFAULT 'domain',
                description TEXT, lifecycle_state TEXT NOT NULL DEFAULT 'production',
                action_tier TEXT NOT NULL DEFAULT 'suggest', disabled_at TIMESTAMPTZ,
                published_version_id UUID,
                v2_scope TEXT, owning_tenant_id UUID, department_id UUID
            );
            CREATE TABLE agent_versions (id UUID PRIMARY KEY, agent_id UUID NOT NULL, version INT NOT NULL);
            CREATE TABLE agent_tenant_assignments (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), agent_id UUID NOT NULL, tenant_id UUID NOT NULL,
                status TEXT NOT NULL CHECK (status IN ('active','revoked'))
            );
            CREATE TABLE agent_department_assignments (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), agent_id UUID NOT NULL, tenant_id UUID NOT NULL,
                department_id UUID NOT NULL, status TEXT NOT NULL CHECK (status IN ('active','revoked'))
            );
            CREATE TABLE agent_assignments (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id UUID NOT NULL, agent_id UUID NOT NULL,
                user_id UUID NOT NULL, status TEXT NOT NULL DEFAULT 'active'
            );
            """);

        _tenantA = Guid.NewGuid(); _tenantB = Guid.NewGuid();
        _accounting = Guid.NewGuid(); _sales = Guid.NewGuid();
        _adminAccounting = Guid.NewGuid(); _userAccounting = Guid.NewGuid(); _userSales = Guid.NewGuid();
        _agentId = Guid.NewGuid();
        _deptAgentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var tenantA = _tenantA; var tenantB = _tenantB;
        var accounting = _accounting; var sales = _sales;
        var adminAccounting = _adminAccounting; var userAccounting = _userAccounting; var userSales = _userSales;
        var agentId = _agentId;
        var deptAgentId = _deptAgentId;

        await scoped.ExecuteAsync("""
            INSERT INTO tenants(id) VALUES (@tenantA), (@tenantB);
            INSERT INTO departments(id,tenant_id) VALUES (@accounting,@tenantA), (@sales,@tenantA);
            INSERT INTO users(id,tenant_id,department_id) VALUES
                (@adminAccounting,@tenantA,@accounting), (@userAccounting,@tenantA,@accounting),
                (@userSales,@tenantA,@sales);
            INSERT INTO agents(id,code,name,lifecycle_state,published_version_id)
                VALUES (@agentId,'accounting-agent','Accounting Agent','production',@versionId);
            INSERT INTO agent_versions(id,agent_id,version) VALUES (@versionId,@agentId,1);
            INSERT INTO agent_tenant_assignments(agent_id,tenant_id,status) VALUES (@agentId,@tenantA,'active');
            -- V2-C Department Agent: structural ownership only, deliberately NO
            -- agent_tenant_assignments/agent_department_assignments row — see
            -- IDynamicAgentRepository.CreateDepartmentAgentAsync.
            INSERT INTO agents(id,code,name,lifecycle_state,v2_scope,owning_tenant_id,department_id)
                VALUES (@deptAgentId,'zzz-dept-agent','ZZZ','production','department',@tenantA,@accounting);
            """, new { tenantA, tenantB, accounting, sales, adminAccounting, userAccounting, userSales, agentId, versionId, deptAgentId });
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(_baseConnectionString)) return;
        await using var conn = new NpgsqlConnection(_baseConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP SCHEMA IF EXISTS {_schema} CASCADE");
    }

    private IAgentRepository Repo() => new AgentRepository(new NpgsqlConnectionFactory(_scopedConnectionString!));

    private async Task SetDepartmentAssignmentAsync(string status)
    {
        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();
        var existing = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM agent_department_assignments WHERE agent_id=@a AND department_id=@d",
            new { a = _agentId, d = _accounting });
        if (existing == 0)
        {
            await conn.ExecuteAsync(
                "INSERT INTO agent_department_assignments(agent_id,tenant_id,department_id,status) VALUES (@a,@t,@d,@status)",
                new { a = _agentId, t = _tenantA, d = _accounting, status });
        }
        else
        {
            await conn.ExecuteAsync(
                "UPDATE agent_department_assignments SET status=@status WHERE agent_id=@a AND department_id=@d",
                new { a = _agentId, d = _accounting, status });
        }
    }

    private async Task SetDirectAssignmentAsync(Guid userId, string status, Guid? agentId = null)
    {
        var a = agentId ?? _agentId;
        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();
        var existing = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM agent_assignments WHERE agent_id=@a AND user_id=@u",
            new { a, u = userId });
        if (existing == 0)
        {
            await conn.ExecuteAsync(
                "INSERT INTO agent_assignments(tenant_id,agent_id,user_id,status) VALUES (@t,@a,@u,@status)",
                new { t = _tenantA, a, u = userId, status });
        }
        else
        {
            await conn.ExecuteAsync(
                "UPDATE agent_assignments SET status=@status WHERE agent_id=@a AND user_id=@u",
                new { a, u = userId, status });
        }
    }

    [Fact]
    public async Task AdminAccounting_SeesDepartmentAgent_WithZeroDirectAssignments()
    {
        if (_scopedConnectionString is null) return;
        await SetDepartmentAssignmentAsync("active");

        // No agent_assignments row for _adminAccounting exists anywhere —
        // the D3-approved shape for an ADMIN's own execution grant.
        var items = await Repo().ListForDepartmentManagerAsync(_tenantA, _accounting, default);
        Assert.Single(items, i => i.AgentId == _agentId && i.AccessLevel == "department");
    }

    [Fact]
    public async Task AdminSales_DoesNotSeeAccountingOnlyAgent()
    {
        if (_scopedConnectionString is null) return;
        await SetDepartmentAssignmentAsync("active");

        var items = await Repo().ListForDepartmentManagerAsync(_tenantA, _sales, default);
        Assert.Empty(items);
    }

    // ---------------------------------------------------------------------
    // ACL V2 review: ListForDepartmentManagerAsync (the ADMIN branch of
    // GET /me/agents — sidebar + My Agents) only ever checked chain 1
    // (agent_department_assignments, the legacy scope='system' entitlement
    // table). A Department Agent (v2_scope='department') can structurally
    // never have a row there — see CreateDepartmentAgentAsync — so an
    // ADMIN who creates and publishes their OWN Department Agent never saw
    // it in their own sidebar/My Agents, even though it was fully
    // executable via GetDepartmentAgentManifestAsync(isAdmin: true). This
    // is the exact "production agent but page shows nothing" class of bug
    // reported for the legacy tenant-agent screen, just on a different
    // surface. Fixed by UNIONing in v2_scope='department' agents owned by
    // the caller's own tenant+department, mirroring ListForPrincipalAsync's
    // existing chain-2 branch for USER.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task AdminAccounting_SeesOwnDepartmentOwnedAgent_NoEntitlementRowNeeded()
    {
        if (_scopedConnectionString is null) return;
        // No agent_tenant_assignments/agent_department_assignments row for
        // _deptAgentId exists anywhere — structurally impossible for a
        // Department Agent, and no agent_assignments row either (an ADMIN
        // never needs one — see ListForPrincipalAsync's own comment on the
        // isAdmin execution bypass).
        var items = await Repo().ListForDepartmentManagerAsync(_tenantA, _accounting, default);
        Assert.Single(items, i => i.AgentId == _deptAgentId && i.AccessLevel == "department");
    }

    [Fact]
    public async Task AdminSales_DoesNotSeeAccountingsDepartmentOwnedAgent()
    {
        if (_scopedConnectionString is null) return;
        var items = await Repo().ListForDepartmentManagerAsync(_tenantA, _sales, default);
        Assert.DoesNotContain(items, i => i.AgentId == _deptAgentId);
    }

    [Fact]
    public async Task UserAccounting_DirectAssignmentPlusDepartmentEntitlement_IsVisible()
    {
        if (_scopedConnectionString is null) return;
        await SetDepartmentAssignmentAsync("active");
        await SetDirectAssignmentAsync(_userAccounting, "active");

        var items = await Repo().ListForPrincipalAsync(_tenantA, _userAccounting, default);
        Assert.Single(items, i => i.AgentId == _agentId);
    }

    [Fact]
    public async Task UserAccounting_StaleDirectAssignmentAfterDepartmentRevoke_IsNotVisible()
    {
        if (_scopedConnectionString is null) return;
        await SetDepartmentAssignmentAsync("active");
        await SetDirectAssignmentAsync(_userAccounting, "active");
        // Department entitlement revoked; the direct assignment row is left
        // untouched (simulates a caller that didn't go through the
        // transactional RevokeDepartmentAgentAsync cascade, or a race).
        await SetDepartmentAssignmentAsync("revoked");

        var items = await Repo().ListForPrincipalAsync(_tenantA, _userAccounting, default);
        Assert.Empty(items);
    }

    [Fact]
    public async Task UserAccounting_NoDirectAssignment_IsNotVisibleEvenWithDepartmentEntitlement()
    {
        if (_scopedConnectionString is null) return;
        await SetDepartmentAssignmentAsync("active");

        var items = await Repo().ListForPrincipalAsync(_tenantA, _userAccounting, default);
        Assert.Empty(items);
    }

    // ---------------------------------------------------------------------
    // V2-C Department Agent visibility on /me/agents (bug: a Department
    // Agent can never have an agent_tenant_assignments/
    // agent_department_assignments row, so requiring one made it
    // permanently invisible to its own directly-assigned USERs — see
    // IAgentRepository.ListForPrincipalAsync's structural-ownership OR
    // branch).
    // ---------------------------------------------------------------------

    [Fact]
    public async Task UserAccounting_DepartmentOwnedAgent_WithDirectAssignment_IsVisible()
    {
        if (_scopedConnectionString is null) return;
        // No agent_tenant_assignments/agent_department_assignments row for
        // _deptAgentId exists anywhere — structurally impossible for a
        // Department Agent — only the direct assignment below.
        await SetDirectAssignmentAsync(_userAccounting, "active", _deptAgentId);

        var items = await Repo().ListForPrincipalAsync(_tenantA, _userAccounting, default);
        Assert.Single(items, i => i.AgentId == _deptAgentId);
    }

    [Fact]
    public async Task UserSales_DepartmentOwnedAccountingAgent_IsNotVisible_EvenWithDirectAssignment()
    {
        if (_scopedConnectionString is null) return;
        // _deptAgentId is owned by Accounting (department_id=_accounting).
        // _userSales is in a different department — even a (mistaken)
        // direct assignment row must not make it visible; department
        // isolation is enforced structurally, not just by absence of a row.
        await SetDirectAssignmentAsync(_userSales, "active", _deptAgentId);

        var items = await Repo().ListForPrincipalAsync(_tenantA, _userSales, default);
        Assert.Empty(items);
    }

    [Fact]
    public async Task UserAccounting_DepartmentOwnedAgent_NoDirectAssignment_IsNotVisible()
    {
        if (_scopedConnectionString is null) return;
        // Structural department ownership alone is never sufficient — the
        // active direct agent_assignments row is still required for a USER
        // (unlike ListForDepartmentManagerAsync, which is ADMIN-only).
        var items = await Repo().ListForPrincipalAsync(_tenantA, _userAccounting, default);
        Assert.Empty(items);
    }

    [Fact]
    public async Task UserAccounting_SystemScopeAgent_StillRequiresFullLegacyEntitlementChain()
    {
        if (_scopedConnectionString is null) return;
        // _agentId is the legacy scope='system'-style agent (v2_scope is
        // NULL) — the structural-ownership OR branch must never apply to
        // it, so it still needs the full tenant + department entitlement
        // chain plus the direct assignment, exactly as before this fix.
        await SetDirectAssignmentAsync(_userAccounting, "active");

        // Direct assignment alone, no department entitlement, is not enough.
        var withoutDeptEntitlement = await Repo().ListForPrincipalAsync(_tenantA, _userAccounting, default);
        Assert.Empty(withoutDeptEntitlement);

        // Adding the department entitlement makes it visible (unchanged
        // legacy chain behavior, proven already above by
        // UserAccounting_DirectAssignmentPlusDepartmentEntitlement_IsVisible;
        // repeated here for an explicit side-by-side contrast with the new
        // Department Agent branch).
        await SetDepartmentAssignmentAsync("active");
        var withDeptEntitlement = await Repo().ListForPrincipalAsync(_tenantA, _userAccounting, default);
        Assert.Single(withDeptEntitlement, i => i.AgentId == _agentId);
    }
}
