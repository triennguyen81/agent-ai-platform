using Aiq.Gateway.Data;
using Aiq.Gateway.Governance;

namespace Aiq.Gateway.Tests.Security;

public sealed class DepartmentAccessPolicyTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly Guid DepartmentA = Guid.NewGuid();

    [Theory]
    [InlineData("admin")]
    [InlineData("user")]
    public void DepartmentMembersRequireAnActiveSameTenantDepartment(string role)
    {
        var user = User(TenantA, DepartmentA);
        var roles = Roles(role);

        Assert.Null(DepartmentAccessPolicy.ValidateMembership(
            user, roles, Department(TenantA, "active")));
        Assert.Equal("department_not_in_tenant", DepartmentAccessPolicy.ValidateMembership(
            user, roles, Department(TenantB, "active")));
        Assert.Equal("department_inactive", DepartmentAccessPolicy.ValidateMembership(
            user, roles, Department(TenantA, "inactive")));
        Assert.Equal("department_not_in_tenant", DepartmentAccessPolicy.ValidateMembership(
            user, roles, null));
        Assert.Equal("department_required", DepartmentAccessPolicy.ValidateMembership(
            User(TenantA, null), roles, null));
        Assert.Equal("tenant_required", DepartmentAccessPolicy.ValidateMembership(
            User(null, DepartmentA), roles, null));
    }

    [Theory]
    [InlineData("tenant_owner")]
    [InlineData("super_admin")]
    public void PlatformAndOwnerRolesRemainDepartmentless(string role)
    {
        Assert.Null(DepartmentAccessPolicy.ValidateMembership(User(
            role == "super_admin" ? null : TenantA, null), Roles(role), null));
        Assert.Equal(role == "super_admin"
                ? "platform_admin_must_be_unscoped"
                : "tenant_owner_must_be_departmentless",
            DepartmentAccessPolicy.ValidateMembership(User(TenantA, DepartmentA), Roles(role), null));
    }

    [Fact]
    public void MembershipRejectsMissingOrMultipleCanonicalLoginRoles()
    {
        Assert.Equal("exactly_one_login_role_required",
            DepartmentAccessPolicy.ValidateMembership(User(TenantA, DepartmentA), Roles(), null));
        Assert.Equal("exactly_one_login_role_required",
            DepartmentAccessPolicy.ValidateMembership(
                User(TenantA, DepartmentA), Roles("admin", "user"), Department(TenantA, "active")));
        Assert.Equal("tenant_required",
            DepartmentAccessPolicy.ValidateMembership(User(null, null), Roles("tenant_owner"), null));
    }

    private static UserRecord User(Guid? tenant, Guid? department) =>
        new(Guid.NewGuid(), "u@test", "x", null, true, DateTime.UtcNow, tenant, department);

    private static DepartmentRecord Department(Guid tenant, string status) =>
        new(DepartmentA, tenant, "accounting", "Accounting", status, false);

    private static IReadOnlySet<string> Roles(params string[] roles) =>
        new HashSet<string>(roles, StringComparer.Ordinal);
}
