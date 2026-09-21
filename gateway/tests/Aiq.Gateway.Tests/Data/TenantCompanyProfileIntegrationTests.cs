using Aiq.Gateway.Data;
using Aiq.Gateway.Endpoints;
using Aiq.Gateway.Governance;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Company Profile ("Hồ sơ doanh nghiệp" — tenant_company_profiles, migration
/// 049): AIQ's own legal-entity identity per tenant. Covers the repository
/// (1:1 upsert semantics, per-tenant isolation) and the manifest-mapping
/// helper the AI runtime relies on (DynamicAgentEndpoints.
/// ToManifestCompanyProfile). "ADMIN/USER cannot update" is enforced by the
/// endpoint's "tenant_governance" policy (Role=="tenant_owner" exactly) —
/// already proven generically by PrincipalAuthorizationPolicyTests'
/// IsTenantGovernor assertions; not re-derived here via a second HTTP-level
/// harness, since this endpoint requires the identical, already-tested
/// policy every other TENANT_OWNER-only route in this codebase uses.
/// </summary>
public sealed class TenantCompanyProfileIntegrationTests : IAsyncLifetime
{
    // Only Program.cs registers this at real app startup — xUnit never runs
    // it, so aliases (TEXT[]) would fail to materialize via Dapper's
    // constructor-based mapping without it (see ContractProcessingIntegrationTests
    // for the same pattern with the DateOnly handlers).
    static TenantCompanyProfileIntegrationTests()
    {
        Dapper.SqlMapper.AddTypeHandler(new StringArrayTypeHandler());
    }

    private readonly string? _baseConnectionString = ResolveTestConnectionString();
    private readonly string _schema = $"tenant_company_profile_test_{Guid.NewGuid():N}";
    private string? _scopedConnectionString;

    private Guid _tenantA, _tenantB, _ownerA;

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
            CREATE TABLE tenants (id UUID PRIMARY KEY);
            CREATE TABLE users (id UUID PRIMARY KEY);
            """);
        await scoped.ExecuteAsync(await ReadMigrationAsync("049_tenant_company_profiles.sql"));

        _tenantA = Guid.NewGuid(); _tenantB = Guid.NewGuid(); _ownerA = Guid.NewGuid();
        var tenantA = _tenantA; var tenantB = _tenantB; var ownerA = _ownerA;
        await scoped.ExecuteAsync(
            "INSERT INTO tenants(id) VALUES (@tenantA), (@tenantB); INSERT INTO users(id) VALUES (@ownerA);",
            new { tenantA, tenantB, ownerA });
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(_baseConnectionString)) return;
        await using var conn = new NpgsqlConnection(_baseConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP SCHEMA IF EXISTS {_schema} CASCADE");
    }

    private static async Task<string> ReadMigrationAsync(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Aiq.Gateway", "Data", "Migrations", fileName);
        return await File.ReadAllTextAsync(Path.GetFullPath(path));
    }

    private ITenantCompanyProfileRepository Repo() => new TenantCompanyProfileRepository(new NpgsqlConnectionFactory(_scopedConnectionString!));

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNoProfileConfigured()
    {
        if (_scopedConnectionString is null) return;
        var profile = await Repo().GetAsync(_tenantA, default);
        Assert.Null(profile);
    }

    [Fact]
    public async Task TenantOwner_CanCreateThenUpdate_OwnProfile()
    {
        if (_scopedConnectionString is null) return;
        var repo = Repo();
        var created = await repo.UpsertAsync(_tenantA, _ownerA, new UpsertCompanyProfileRequest(
            "Công ty TNHH Việt Đăng", "0301234567", "Việt Đăng", ["AIQ", "Cát Tường AI"],
            "123 Nguyễn Huệ, Q1, TP.HCM", "Nguyễn Văn A", "Giám đốc", "0900000000",
            "contact@vietdang.vn", "https://vietdang.vn"), default);
        Assert.Equal(_tenantA, created.TenantId);
        Assert.Equal("Công ty TNHH Việt Đăng", created.LegalName);
        Assert.Equal(["AIQ", "Cát Tường AI"], created.Aliases);

        // Update — same tenant_id primary key, never a second row.
        var updated = await repo.UpsertAsync(_tenantA, _ownerA, new UpsertCompanyProfileRequest(
            "Công ty TNHH Việt Đăng (đổi tên)", "0301234567", "Việt Đăng", ["AIQ"],
            null, null, null, null, null, null), default);
        Assert.Equal("Công ty TNHH Việt Đăng (đổi tên)", updated.LegalName);
        Assert.Equal(["AIQ"], updated.Aliases);
        Assert.Null(updated.RegisteredAddress);

        await using var verifyConn = new NpgsqlConnection(_scopedConnectionString);
        var count = await verifyConn.QuerySingleAsync<long>(
            "SELECT count(*) FROM tenant_company_profiles WHERE tenant_id=@t", new { t = _tenantA });
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CrossTenant_ProfilesAreFullyIsolated()
    {
        if (_scopedConnectionString is null) return;
        var repo = Repo();
        await repo.UpsertAsync(_tenantA, _ownerA, new UpsertCompanyProfileRequest(
            "Tenant A Co", "1111", null, [], null, null, null, null, null, null), default);

        // Tenant B has never configured a profile — must never see Tenant A's.
        var tenantBProfile = await repo.GetAsync(_tenantB, default);
        Assert.Null(tenantBProfile);

        // Writing Tenant B's own profile must never touch Tenant A's row.
        await repo.UpsertAsync(_tenantB, _ownerA, new UpsertCompanyProfileRequest(
            "Tenant B Co", "2222", null, [], null, null, null, null, null, null), default);
        var tenantAProfile = await repo.GetAsync(_tenantA, default);
        Assert.Equal("Tenant A Co", tenantAProfile!.LegalName);
        Assert.Equal("1111", tenantAProfile.TaxCode);
    }

    [Fact]
    public void ManifestMapping_ReturnsNull_ForUnconfiguredTenant_NeverGuessesIdentity()
    {
        Assert.Null(DynamicAgentEndpoints.ToManifestCompanyProfile(null));
    }

    [Fact]
    public async Task ManifestMapping_CarriesEveryFieldThrough_ForAuthenticatedTenant()
    {
        if (_scopedConnectionString is null) return;
        var repo = Repo();
        await repo.UpsertAsync(_tenantA, _ownerA, new UpsertCompanyProfileRequest(
            "Công ty TNHH Việt Đăng", "0301234567", "Việt Đăng", ["AIQ", "Cát Tường AI"],
            "123 Nguyễn Huệ, Q1, TP.HCM", "Nguyễn Văn A", "Giám đốc", "0900000000",
            "contact@vietdang.vn", "https://vietdang.vn"), default);

        var record = await repo.GetAsync(_tenantA, default);
        var manifestProfile = DynamicAgentEndpoints.ToManifestCompanyProfile(record);

        Assert.NotNull(manifestProfile);
        Assert.Equal("Công ty TNHH Việt Đăng", manifestProfile!.LegalName);
        Assert.Equal("0301234567", manifestProfile.TaxCode);
        Assert.Equal(["AIQ", "Cát Tường AI"], manifestProfile.Aliases);
        Assert.Equal("123 Nguyễn Huệ, Q1, TP.HCM", manifestProfile.RegisteredAddress);
        Assert.Equal("Nguyễn Văn A", manifestProfile.LegalRepresentative);
    }
}
