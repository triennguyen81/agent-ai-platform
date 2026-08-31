using Aiq.Gateway.Governance;

namespace Aiq.Gateway.Tests.Security;

public sealed class ManifestAccessRulesTests
{
    [Fact]
    public void SuperAdminAlwaysGetsTestMode() =>
        Assert.Equal(ManifestAccessMode.SuperAdminTest, ManifestAccessRules.Resolve(isSuperAdmin: true, isUser: false));

    [Fact]
    public void PlainUserGetsEndUserMode() =>
        Assert.Equal(ManifestAccessMode.EndUser, ManifestAccessRules.Resolve(isSuperAdmin: false, isUser: true));

    [Fact]
    public void TenantAdminIsForbidden() =>
        // ADMIN/USER must not access Agent Builder's test path, and a tenant
        // admin has no "user" role claim either — so real chat is blocked too.
        Assert.Equal(ManifestAccessMode.Forbidden, ManifestAccessRules.Resolve(isSuperAdmin: false, isUser: false));

    [Fact]
    public void SuperAdminTakesPriorityOverEndUserBranch() =>
        // Roles are mutually exclusive in practice (LoginRoleRules), but if a
        // token ever carried both claims, testing must win — never silently
        // fall back to the tenant-scoped end-user path for a super_admin.
        Assert.Equal(ManifestAccessMode.SuperAdminTest, ManifestAccessRules.Resolve(isSuperAdmin: true, isUser: true));
}
