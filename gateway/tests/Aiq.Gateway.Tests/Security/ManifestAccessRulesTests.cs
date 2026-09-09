using Aiq.Gateway.Governance;

namespace Aiq.Gateway.Tests.Security;

public sealed class ManifestAccessRulesTests
{
    [Fact]
    public void SuperAdminAlwaysGetsTestMode() =>
        Assert.Equal(ManifestAccessMode.SuperAdminTest,
            ManifestAccessRules.Resolve(isSuperAdmin: true, isAdmin: false, isUser: false));

    [Fact]
    public void PlainUserGetsEndUserMode() =>
        Assert.Equal(ManifestAccessMode.EndUser,
            ManifestAccessRules.Resolve(isSuperAdmin: false, isAdmin: false, isUser: true));

    [Fact]
    public void DepartmentAdminGetsAdminDepartmentExecutionMode() =>
        // D3: this is the actual policy change from D2 — a department ADMIN
        // now gets a real execution mode instead of Forbidden.
        Assert.Equal(ManifestAccessMode.AdminDepartmentExecution,
            ManifestAccessRules.Resolve(isSuperAdmin: false, isAdmin: true, isUser: false));

    [Fact]
    public void TenantOwnerIsForbidden() =>
        // Unchanged from pre-D3: "TENANT_OWNER does NOT automatically gain
        // production execution" — a tenant_owner satisfies none of
        // super_admin/admin/user (roles are mutually exclusive), so it falls
        // through exactly like it did before D3.
        Assert.Equal(ManifestAccessMode.Forbidden,
            ManifestAccessRules.Resolve(isSuperAdmin: false, isAdmin: false, isUser: false));

    [Fact]
    public void SuperAdminTakesPriorityOverAdminAndEndUserBranches() =>
        // Roles are mutually exclusive in practice (LoginRoleRules), but if a
        // token ever carried multiple claims, testing must win — never
        // silently fall back to a tenant-scoped execution path for a super_admin.
        Assert.Equal(ManifestAccessMode.SuperAdminTest,
            ManifestAccessRules.Resolve(isSuperAdmin: true, isAdmin: true, isUser: true));

    [Fact]
    public void AdminTakesPriorityOverEndUserBranch() =>
        // Same defense-in-depth reasoning: an admin claim must never
        // downgrade to the (stricter, direct-assignment-requiring) USER path.
        Assert.Equal(ManifestAccessMode.AdminDepartmentExecution,
            ManifestAccessRules.Resolve(isSuperAdmin: false, isAdmin: true, isUser: true));
}
