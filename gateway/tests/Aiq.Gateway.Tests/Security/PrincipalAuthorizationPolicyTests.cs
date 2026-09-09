using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;

namespace Aiq.Gateway.Tests.Security;

public sealed class PrincipalAuthorizationPolicyTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid DepartmentId = Guid.NewGuid();

    [Fact]
    public void ExactPoliciesDoNotInheritAcrossRoles()
    {
        var platform = Principal("super_admin", null, null, null, null);
        var owner = Principal("tenant_owner", TenantId, null, true, null);
        var admin = Principal("admin", TenantId, DepartmentId, true, true);
        var user = Principal("user", TenantId, DepartmentId, true, true);

        Assert.True(PrincipalAuthorizationPolicy.IsPlatformAdmin(platform));
        Assert.False(PrincipalAuthorizationPolicy.IsTenantGovernor(platform));
        Assert.True(PrincipalAuthorizationPolicy.IsTenantGovernor(owner));
        Assert.False(PrincipalAuthorizationPolicy.IsDepartmentAdmin(owner));
        Assert.True(PrincipalAuthorizationPolicy.IsDepartmentAdmin(admin));
        Assert.True(PrincipalAuthorizationPolicy.IsDepartmentMember(admin));
        Assert.True(PrincipalAuthorizationPolicy.IsDepartmentMember(user));
        Assert.False(PrincipalAuthorizationPolicy.IsDepartmentAdmin(user));
    }

    [Fact]
    public void PoliciesFailClosedForInactiveOrIncompletePersistedScope()
    {
        Assert.False(PrincipalAuthorizationPolicy.IsAuthenticated(
            Principal("user", TenantId, DepartmentId, true, true) with { UserActive = false }));
        Assert.False(PrincipalAuthorizationPolicy.IsTenantGovernor(
            Principal("tenant_owner", TenantId, null, false, null)));
        Assert.False(PrincipalAuthorizationPolicy.IsDepartmentAdmin(
            Principal("admin", TenantId, DepartmentId, true, false)));
        Assert.False(PrincipalAuthorizationPolicy.IsDepartmentMember(
            Principal("user", TenantId, null, true, null)));
    }

    [Fact]
    public void ScopePredicatesRequireBothTenantAndDepartmentMatch()
    {
        var principal = Principal("admin", TenantId, DepartmentId, true, true);
        Assert.True(PrincipalAuthorizationPolicy.IsSameTenant(principal, TenantId));
        Assert.True(PrincipalAuthorizationPolicy.IsSameDepartment(principal, TenantId, DepartmentId));
        Assert.False(PrincipalAuthorizationPolicy.IsSameDepartment(principal, Guid.NewGuid(), DepartmentId));
        Assert.False(PrincipalAuthorizationPolicy.IsSameDepartment(principal, TenantId, Guid.NewGuid()));
    }

    private static PrincipalContext Principal(
        string role, Guid? tenantId, Guid? departmentId, bool? tenantActive, bool? departmentActive) =>
        new(Guid.NewGuid(), tenantId, departmentId, role, true, tenantActive, departmentActive);
}
