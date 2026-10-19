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
        // ACL V2 review: PATCH /tenant/users/{id}/department (the
        // TENANT_OWNER-only "Chuyển phòng ban" move) is gated by this exact
        // policy — an ADMIN must never pass it (an ADMIN could otherwise
        // move a USER into their own department to gain new agent access,
        // or out of it to dodge oversight — see UserEndpoints.cs's own
        // comment on this route). IsTenantGovernor requires Role==
        // "tenant_owner" exactly, so this was already true by construction;
        // asserting it explicitly closes the gap that no test named this
        // scenario.
        Assert.False(PrincipalAuthorizationPolicy.IsTenantGovernor(admin));
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
    public void ApplyInvoice_OnlyDepartmentAdminPassesTheGateEndpointsRequire()
    {
        // POST /vouchers/{id}/apply-invoice and .../apply-expense (the
        // "Duyệt & ghi nhận" invoice-approval endpoints that create AR/AP)
        // both require this exact "department_admin" policy — ADMIN has
        // full operational authority inside its own department (can
        // approve), USER cannot, and TENANT_OWNER (departmentless — never
        // gets implicit production execution/operational rights inside a
        // department it doesn't belong to) cannot either.
        var admin = Principal("admin", TenantId, DepartmentId, true, true);
        var user = Principal("user", TenantId, DepartmentId, true, true);
        var owner = Principal("tenant_owner", TenantId, null, true, null);
        Assert.True(PrincipalAuthorizationPolicy.IsDepartmentAdmin(admin));
        Assert.False(PrincipalAuthorizationPolicy.IsDepartmentAdmin(user));
        Assert.False(PrincipalAuthorizationPolicy.IsDepartmentAdmin(owner));
    }

    [Fact]
    public void CompanyProfile_OnlyTenantOwnerPassesTheGateEndpointsRequire()
    {
        // GET/PUT /tenant/business-profile ("Hồ sơ doanh nghiệp") requires
        // this exact "tenant_governance" policy — ADMIN and USER must never
        // be able to create/update Company Profile, only read/write it as
        // TENANT_OWNER.
        var owner = Principal("tenant_owner", TenantId, null, true, null);
        var admin = Principal("admin", TenantId, DepartmentId, true, true);
        var user = Principal("user", TenantId, DepartmentId, true, true);
        Assert.True(PrincipalAuthorizationPolicy.IsTenantGovernor(owner));
        Assert.False(PrincipalAuthorizationPolicy.IsTenantGovernor(admin));
        Assert.False(PrincipalAuthorizationPolicy.IsTenantGovernor(user));
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
