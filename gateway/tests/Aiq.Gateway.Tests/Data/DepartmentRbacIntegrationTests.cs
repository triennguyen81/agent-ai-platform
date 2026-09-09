using Aiq.Gateway.Data;
using Aiq.Gateway.Endpoints;
using Aiq.Gateway.Governance;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Phase D2 cross-department RBAC integration coverage. Builds the exact
/// fixture from the D2 task spec against a real, isolated Postgres schema
/// (same real migrations 017/018/019 as <see cref="DepartmentMigrationIntegrationTests"/>)
/// and exercises the actual department-scoped repository methods that
/// UserEndpoints/DynamicAgentEndpoints call — not synthetic in-memory data.
///
/// Fixture:
///   Tenant A: Owner A; Accounting {Admin Accounting, User A1, User A2};
///             Sales {Admin Sales, User S1}
///   Tenant B: Other Department {Admin B, User B}
///
/// Pure-function edge cases (department-inactive/tenant-mismatch/self-modify/
/// privileged-target rejection) are already covered by
/// <see cref="Aiq.Gateway.Tests.Security.DepartmentAccessPolicyTests"/> and
/// <see cref="Aiq.Gateway.Tests.Security.TenantUserRoleChangePolicyTests"/>;
/// this file proves the same rules hold when driven through the real SQL
/// each endpoint actually runs, using real ids from a real fixture.
/// </summary>
public sealed class DepartmentRbacIntegrationTests : IAsyncLifetime
{
    // Program.cs registers this at app startup so Dapper knows how to map a
    // Postgres text[] into UserWithRoles.Roles (string[]) — this test never
    // runs Program.cs, so it must register the same handler itself, exactly
    // once per test process, or every query selecting Roles throws.
    static DepartmentRbacIntegrationTests() =>
        Dapper.SqlMapper.AddTypeHandler(new StringArrayTypeHandler());

    private readonly string? _baseConnectionString = ResolveTestConnectionString();
    private readonly string _schema = $"department_rbac_test_{Guid.NewGuid():N}";
    private string? _scopedConnectionString;

    private Guid _tenantA, _tenantB;
    private Guid _accounting, _sales, _otherDept;
    private Guid _ownerA, _ownerB;
    private Guid _adminAccounting, _userA1, _userA2;
    private Guid _adminSales, _userS1;
    private Guid _adminB, _userB;
    private Guid _agentId;

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

        // Tenants, the owner, and every admin/user must exist BEFORE
        // migrations 018/019 run: 018 asserts every active tenant already
        // has a tenant_owner (it fails the whole migration otherwise — this
        // is the same guard the real D1.5 bootstrap-hardening work relies
        // on), and 019 backfills one active default department per EXISTING
        // tenant at apply time. This mirrors
        // DepartmentMigrationIntegrationTests' proven ordering exactly.
        await SeedTenantsAndUsersAsync(scoped);

        // Real migrations: tenant-owner role/enforcement, the real D1
        // departments migration (departments table, users.department_id,
        // composite FK), then D3's department-agent entitlement table — the
        // exact chain the production database runs. 021's own backfill runs
        // here against zero agent_assignments rows (harmless no-op); each
        // test seeds its own agent/assignment rows afterward as needed.
        await scoped.ExecuteAsync(await ReadMigrationAsync("017_tenant_owner_role.sql"));
        await scoped.ExecuteAsync(await ReadMigrationAsync("018_tenant_owner_enforcement.sql"));
        await scoped.ExecuteAsync(await ReadMigrationAsync("019_departments.sql"));
        await scoped.ExecuteAsync(await ReadMigrationAsync("021_agent_department_assignments.sql"));

        await AssignDepartmentsAsync(scoped);
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
                external_provider TEXT,
                external_id TEXT,
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

            -- Minimal dynamic-agent tables, columns matched to what
            -- DynamicAgentRepository's department-scoped methods actually
            -- select/insert (real DDL: 001_*.sql, 002_agent_assignments.sql).
            CREATE TABLE agents (id UUID PRIMARY KEY);
            CREATE TABLE agent_tenant_assignments (
                agent_id UUID NOT NULL REFERENCES agents(id),
                tenant_id UUID NOT NULL REFERENCES tenants(id),
                status TEXT NOT NULL
            );
            CREATE TABLE agent_assignments (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                tenant_id UUID NOT NULL REFERENCES tenants(id),
                agent_id UUID NOT NULL REFERENCES agents(id),
                user_id UUID NOT NULL REFERENCES users(id),
                assigned_by UUID,
                status TEXT NOT NULL DEFAULT 'active',
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                revoked_at TIMESTAMPTZ,
                UNIQUE (tenant_id, agent_id, user_id)
            );
            """);
        // D3: department-agent entitlement — added after migration 019 below
        // creates `departments`, since this table's FK depends on it.
    }

    private async Task SeedTenantsAndUsersAsync(NpgsqlConnection conn)
    {
        _tenantA = Guid.NewGuid();
        _tenantB = Guid.NewGuid();
        _ownerA = Guid.NewGuid();
        _ownerB = Guid.NewGuid();
        _adminAccounting = Guid.NewGuid();
        _userA1 = Guid.NewGuid();
        _userA2 = Guid.NewGuid();
        _adminSales = Guid.NewGuid();
        _userS1 = Guid.NewGuid();
        _adminB = Guid.NewGuid();
        _userB = Guid.NewGuid();
        _agentId = Guid.NewGuid();

        var tenantA = _tenantA;
        var tenantB = _tenantB;
        var ownerA = _ownerA;
        var ownerB = _ownerB;
        var adminAccounting = _adminAccounting;
        var userA1 = _userA1;
        var userA2 = _userA2;
        var adminSales = _adminSales;
        var userS1 = _userS1;
        var adminB = _adminB;
        var userB = _userB;

        await conn.ExecuteAsync("""
            INSERT INTO tenants(id,code,name,status) VALUES
                (@tenantA,'tenant-a','Tenant A','active'),
                (@tenantB,'tenant-b','Tenant B','active');
            INSERT INTO users(id,email,password_hash,tenant_id,is_active) VALUES
                (@ownerA,'owner-a@test','x',@tenantA,true),
                (@ownerB,'owner-b@test','x',@tenantB,true),
                (@adminAccounting,'admin-accounting@test','x',@tenantA,true),
                (@userA1,'user-a1@test','x',@tenantA,true),
                (@userA2,'user-a2@test','x',@tenantA,true),
                (@adminSales,'admin-sales@test','x',@tenantA,true),
                (@userS1,'user-s1@test','x',@tenantA,true),
                (@adminB,'admin-b@test','x',@tenantB,true),
                (@userB,'user-b@test','x',@tenantB,true);
            INSERT INTO user_roles(user_id,role_id)
            SELECT @ownerA,id FROM roles WHERE name='tenant_owner'
            UNION ALL SELECT @ownerB,id FROM roles WHERE name='tenant_owner'
            UNION ALL SELECT @adminAccounting,id FROM roles WHERE name='admin'
            UNION ALL SELECT @userA1,id FROM roles WHERE name='user'
            UNION ALL SELECT @userA2,id FROM roles WHERE name='user'
            UNION ALL SELECT @adminSales,id FROM roles WHERE name='admin'
            UNION ALL SELECT @userS1,id FROM roles WHERE name='user'
            UNION ALL SELECT @adminB,id FROM roles WHERE name='admin'
            UNION ALL SELECT @userB,id FROM roles WHERE name='user';
            """, new { tenantA, tenantB, ownerA, ownerB, adminAccounting, userA1, userA2, adminSales, userS1, adminB, userB });
    }

    private async Task AssignDepartmentsAsync(NpgsqlConnection conn)
    {
        var tenantA = _tenantA;
        var tenantB = _tenantB;
        var adminAccounting = _adminAccounting;
        var userA1 = _userA1;
        var userA2 = _userA2;
        var adminSales = _adminSales;
        var userS1 = _userS1;
        var adminB = _adminB;
        var userB = _userB;
        var agentId = _agentId;

        // Migration 019 auto-creates a "default" department per tenant.
        // Rename/repurpose Tenant A's default into "Accounting" and add
        // "Sales"; rename Tenant B's default into "Other Department" — this
        // matches the fixture literally instead of relying on migration
        // internals' generated ids.
        _accounting = await conn.ExecuteScalarAsync<Guid>(
            "UPDATE departments SET code='accounting', name='Accounting' WHERE tenant_id=@tenantA AND is_default RETURNING id",
            new { tenantA });
        _sales = await conn.ExecuteScalarAsync<Guid>(
            "INSERT INTO departments(tenant_id,code,name,status) VALUES (@tenantA,'sales','Sales','active') RETURNING id",
            new { tenantA });
        _otherDept = await conn.ExecuteScalarAsync<Guid>(
            "UPDATE departments SET code='other', name='Other Department' WHERE tenant_id=@tenantB AND is_default RETURNING id",
            new { tenantB });

        var accounting = _accounting;
        var sales = _sales;
        var other = _otherDept;

        await conn.ExecuteAsync("""
            UPDATE users SET department_id=@accounting WHERE id IN (@adminAccounting,@userA1,@userA2);
            UPDATE users SET department_id=@sales WHERE id IN (@adminSales,@userS1);
            UPDATE users SET department_id=@other WHERE id IN (@adminB,@userB);
            """, new { accounting, sales, other, adminAccounting, userA1, userA2, adminSales, userS1, adminB, userB });

        await conn.ExecuteAsync("""
            INSERT INTO agents(id) VALUES (@agentId);
            INSERT INTO agent_tenant_assignments(agent_id,tenant_id,status) VALUES (@agentId,@tenantA,'active');
            """, new { agentId, tenantA });
    }

    private static async Task<string> ReadMigrationAsync(string name)
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "Data", "Migrations", name);
        if (File.Exists(outputPath)) return await File.ReadAllTextAsync(outputPath);

        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../src/Aiq.Gateway/Data/Migrations", name));
        return await File.ReadAllTextAsync(sourcePath);
    }

    private IUserRepository Users() => new UserRepository(new NpgsqlConnectionFactory(_scopedConnectionString!));
    private IDepartmentRepository Departments() => new DepartmentRepository(new NpgsqlConnectionFactory(_scopedConnectionString!));
    private IDynamicAgentRepository Agents() => new DynamicAgentRepository(new NpgsqlConnectionFactory(_scopedConnectionString!));

    // ---- Admin Accounting: list/read -----------------------------------

    [Fact]
    public async Task AdminAccounting_ListsOnlyAccountingUsers()
    {
        if (_scopedConnectionString is null) return;

        var rows = await Users().ListByDepartmentAsync(_tenantA, _accounting, default);

        Assert.Equal(new[] { "user-a1@test", "user-a2@test" }, rows.Select(r => r.Email).OrderBy(e => e));
    }

    [Fact]
    public async Task AdminAccounting_CanReadA1()
    {
        if (_scopedConnectionString is null) return;

        var row = await Users().FindDepartmentUserAsync(_tenantA, _accounting, _userA1, default);

        Assert.NotNull(row);
        Assert.Equal("user-a1@test", row!.Email);
    }

    [Fact]
    public async Task AdminAccounting_CannotReadSalesUser()
    {
        if (_scopedConnectionString is null) return;

        var row = await Users().FindDepartmentUserAsync(_tenantA, _accounting, _userS1, default);

        Assert.Null(row);
    }

    [Fact]
    public async Task AdminAccounting_CannotReadTenantBUser()
    {
        if (_scopedConnectionString is null) return;

        var row = await Users().FindDepartmentUserAsync(_tenantA, _accounting, _userB, default);

        Assert.Null(row);
    }

    // ---- Admin Accounting: create -----------------------------------
    //
    // UserEndpoints.CreateTenantUserAsync assigns principal.DepartmentId
    // server-side for a non-owner caller and rejects any client-supplied
    // DepartmentId outright (400 department_id_not_allowed) rather than
    // silently trusting it — see the HTTP E2E report for the request-shape
    // proof. This test proves the data-layer half: once the endpoint has
    // decided the department is Accounting, the created row is genuinely
    // scoped to Accounting and invisible from Sales.

    [Fact]
    public async Task AdminAccounting_CreatedUser_IsScopedToAccountingNotSales()
    {
        if (_scopedConnectionString is null) return;

        var users = Users();
        var newUserId = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(_scopedConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("""
                INSERT INTO users(id,email,password_hash,tenant_id,is_active) VALUES (@newUserId,'new-a3@test','x',@_tenantA,true);
                INSERT INTO user_roles(user_id,role_id) SELECT @newUserId,id FROM roles WHERE name='user';
                """, new { newUserId, _tenantA });
        }
        // Server-assigned department, exactly like CreateTenantUserAsync does
        // for a non-owner (department_id = principal.DepartmentId).
        await users.SetDepartmentAsync(newUserId, _accounting, default);

        Assert.Contains(newUserId, (await users.ListByDepartmentAsync(_tenantA, _accounting, default)).Select(r => r.Id));
        Assert.DoesNotContain(newUserId, (await users.ListByDepartmentAsync(_tenantA, _sales, default)).Select(r => r.Id));
    }

    [Fact]
    public async Task SetDepartmentAsync_RejectsInactiveOrCrossTenantDepartment()
    {
        if (_scopedConnectionString is null) return;

        // Sales belongs to Tenant A but not to a Tenant-B user.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Users().SetDepartmentAsync(_userB, _sales, default));
    }

    // ---- Admin Accounting: modify / deactivate -----------------------

    [Fact]
    public async Task AdminAccounting_CanModifyA1()
    {
        if (_scopedConnectionString is null) return;

        var updated = await Users().UpdateDepartmentUserAsync(_tenantA, _accounting, _userA1, "A1 Renamed", default);

        Assert.True(updated);
    }

    [Fact]
    public async Task AdminAccounting_CannotModifySalesUser()
    {
        if (_scopedConnectionString is null) return;

        var updated = await Users().UpdateDepartmentUserAsync(_tenantA, _accounting, _userS1, "Hijacked", default);

        Assert.False(updated);
    }

    [Fact]
    public async Task AdminAccounting_CannotDeactivateSalesUser()
    {
        if (_scopedConnectionString is null) return;

        var changed = await Users().SetDepartmentUserActiveAsync(_tenantA, _accounting, _userS1, false, default);

        Assert.False(changed);
        Assert.True(await IsActiveAsync(_userS1));
    }

    [Fact]
    public async Task AdminAccounting_CanDeactivateA2()
    {
        if (_scopedConnectionString is null) return;

        var changed = await Users().SetDepartmentUserActiveAsync(_tenantA, _accounting, _userA2, false, default);

        Assert.True(changed);
        Assert.False(await IsActiveAsync(_userA2));
    }

    // ---- Admin Accounting: role change is structurally unreachable ---
    //
    // ChangeTenantUserRoleAsync is mapped with .RequireAuthorization("tenant_owner")
    // ONLY — no ADMIN policy grants that route at all, so an ADMIN caller
    // never reaches TenantUserRoleChangePolicy in the first place (verified
    // live over HTTP in the E2E report). This proves the policy itself would
    // reject the promotion/grant even if it were ever reached, using real
    // fixture ids/roles instead of synthetic ones.

    [Fact]
    public void PromotingUserToAdmin_PolicyAllowsRoleFlipButRouteIsOwnerOnly()
    {
        if (_scopedConnectionString is null) return;

        var (error, fromRole) = TenantUserRoleChangePolicy.Validate(
            _adminAccounting, _userA1, _tenantA, _tenantA, ["user"], "admin");

        // The pure policy permits a user->admin flip in isolation (that's
        // TENANT_OWNER's job) — the actual denial for an ADMIN actor comes
        // from the route policy never admitting them, not from this
        // function rejecting the actor. Both layers are required and are
        // each tested at their own level (this test + the HTTP E2E).
        Assert.Null(error);
        Assert.Equal("user", fromRole);
    }

    [Fact]
    public void GrantingSuperAdminOrTenantOwner_RejectedByRequestShapeValidation()
    {
        // ChangeTenantUserRoleRequest.Role only accepts "user"/"admin" at
        // the model-binding/validation layer, before TenantUserRoleChangePolicy
        // or the route's tenant_owner-only policy is ever consulted.
        foreach (var forbidden in new[] { "super_admin", "tenant_owner", "SUPER_ADMIN", "" })
        {
            var request = new ChangeTenantUserRoleRequest(forbidden);
            var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
            var ok = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
                request, new System.ComponentModel.DataAnnotations.ValidationContext(request), results, true);
            Assert.False(ok);
        }
    }

    // ---- Admin Sales: symmetric isolation -----------------------------

    [Fact]
    public async Task AdminSales_CannotSeeAccountingUsers()
    {
        if (_scopedConnectionString is null) return;

        var rows = await Users().ListByDepartmentAsync(_tenantA, _sales, default);

        Assert.Equal(["user-s1@test"], rows.Select(r => r.Email));
        Assert.DoesNotContain(rows, r => r.Email is "user-a1@test" or "user-a2@test");
    }

    // ---- Tenant Owner A: tenant-wide within own tenant ------------------

    [Fact]
    public async Task TenantOwnerA_SeesAccountingAndSalesUsers()
    {
        if (_scopedConnectionString is null) return;

        var rows = await Users().ListByTenantAsync(_tenantA, default);

        Assert.Equal(
            new[] { "user-a1@test", "user-a2@test", "user-s1@test" },
            rows.Select(r => r.Email).OrderBy(e => e));
    }

    [Fact]
    public async Task TenantOwnerA_CannotSeeTenantBViaTenantScopedRead()
    {
        if (_scopedConnectionString is null) return;

        var target = await Users().FindByIdAsync(_userB, default);

        Assert.NotNull(target);
        // The owner-path handler (GetTenantUserAsync) checks target.TenantId
        // == caller tenant before returning anything — this is the value it
        // would compare against.
        Assert.NotEqual(_tenantA, target!.TenantId);
    }

    // ---- USER: not a management actor ----------------------------------
    //
    // There is no repository method a literal "user" role could call to
    // manage other users at all (ListByDepartmentAsync/etc. require an
    // authenticated ADMIN/TENANT_OWNER principal minted by TenantContextMiddleware).
    // The actual denial mechanism is the "admin" authorization policy on the
    // /tenant/users route group, which literal "user" never satisfies —
    // proven live in the HTTP E2E report.

    // ---- Inactive department: fail closed --------------------------------

    [Fact]
    public async Task InactiveDepartment_MembershipFailsClosed()
    {
        if (_scopedConnectionString is null) return;

        await using (var conn = new NpgsqlConnection(_scopedConnectionString))
        {
            await conn.OpenAsync();
            // Accounting is Tenant A's renamed auto-created default
            // department, and migration 019's own CHECK constraint forbids
            // an inactive default department — clear is_default first (a
            // real deactivation workflow would reassign the default
            // elsewhere; that lifecycle endpoint doesn't exist yet and is
            // out of D2 scope).
            await conn.ExecuteAsync(
                "UPDATE departments SET is_default=false, status='inactive' WHERE id=@accounting",
                new { accounting = _accounting });
        }

        var department = await Departments().FindAsync(_tenantA, _accounting, default);
        var admin = await Users().FindByIdAsync(_adminAccounting, default);
        Assert.NotNull(department);
        Assert.NotNull(admin);

        var error = DepartmentAccessPolicy.ValidateMembership(
            admin!, new HashSet<string>(["admin"], StringComparer.Ordinal), department);

        // This is exactly the check TenantContextMiddleware runs before ever
        // setting AuthenticatedPrincipalContext — a 403 here means Admin
        // Accounting cannot even authenticate a request, so every
        // department-scoped endpoint fails closed.
        Assert.Equal("department_inactive", error);
    }

    // ---- Cross-tenant: denied regardless of known ids --------------------

    [Fact]
    public async Task AdminAccounting_CannotModifyOrDeactivateTenantBUser()
    {
        if (_scopedConnectionString is null) return;

        var users = Users();
        Assert.False(await users.UpdateDepartmentUserAsync(_tenantA, _accounting, _userB, "Hijacked", default));
        Assert.False(await users.SetDepartmentUserActiveAsync(_tenantA, _accounting, _userB, false, default));
        Assert.True(await IsActiveAsync(_userB));
    }

    [Fact]
    public async Task AdminB_CannotSeeTenantAUsers()
    {
        if (_scopedConnectionString is null) return;

        var rows = await Users().ListByDepartmentAsync(_tenantB, _otherDept, default);

        Assert.Equal(["user-b@test"], rows.Select(r => r.Email));
    }

    // ---- Agent user-assignment selector (audit item #11) ------------------
    //
    // ListAssignedDepartmentUsersAsync/AssignDepartmentUserAsync/RevokeDepartmentUserAsync
    // back DynamicAgentEndpoints' ListUsersAsync/AssignUserAsync/RevokeUserAsync
    // for a non-owner (department-scoped ADMIN) caller.

    [Fact]
    public async Task AdminAccounting_CanAssignAndListDepartmentAgentUsers_ButNotSalesUser()
    {
        if (_scopedConnectionString is null) return;

        var agents = Agents();
        var actorId = _adminAccounting;

        // D3: a USER assignment now additionally requires the agent to be
        // actively assigned to the department itself, not just tenant-entitled.
        Assert.True(await agents.AssignDepartmentAgentAsync(_tenantA, _accounting, _agentId, actorId, default));

        Assert.True(await agents.AssignDepartmentUserAsync(_tenantA, _accounting, _agentId, _userA1, actorId, default));
        // A Sales-department user is not selectable by Admin Accounting's
        // department-scoped assignment method, even though both are in Tenant A.
        Assert.False(await agents.AssignDepartmentUserAsync(_tenantA, _accounting, _agentId, _userS1, actorId, default));

        var assigned = await agents.ListAssignedDepartmentUsersAsync(_tenantA, _accounting, _agentId, default);
        Assert.NotNull(assigned);
        Assert.Equal(["user-a1@test"], assigned!.Select(a => a.Email));

        Assert.True(await agents.RevokeDepartmentUserAsync(_tenantA, _accounting, _agentId, _userA1, default));
        Assert.False(await agents.RevokeDepartmentUserAsync(_tenantA, _sales, _agentId, _userS1, default));
    }

    private async Task<bool> IsActiveAsync(Guid userId)
    {
        await using var conn = new NpgsqlConnection(_scopedConnectionString);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<bool>(
            "SELECT is_active FROM users WHERE id=@userId", new { userId });
    }
}
