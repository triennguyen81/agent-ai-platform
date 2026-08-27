using Aiq.Gateway.Governance;

namespace Aiq.Gateway.Tests.Security;

public sealed class ResourceOrToolPolicyTests
{
    [Fact]
    public void AdminAndSuperAdminAlwaysAuthorized() =>
        Assert.True(ResourceOrToolPolicy.IsAuthorized(isSuperAdmin: true, isAdmin: false, isUser: false, hasToolExecutionScope: false));

    [Fact]
    public void PlainUserWithoutScopeIsRejected() =>
        // Regression: a role=user session must never reach Contracts/Vouchers
        // directly, even with no scope claim at all.
        Assert.False(ResourceOrToolPolicy.IsAuthorized(isSuperAdmin: false, isAdmin: false, isUser: true, hasToolExecutionScope: false));

    [Fact]
    public void UserWithToolExecutionScopeIsAuthorized() =>
        Assert.True(ResourceOrToolPolicy.IsAuthorized(isSuperAdmin: false, isAdmin: false, isUser: true, hasToolExecutionScope: true));

    [Fact]
    public void ScopeClaimAloneWithoutUserRoleIsRejected() =>
        // A caller that is somehow neither admin/super_admin nor role=user
        // (shouldn't happen given LoginRoleRules, but defense in depth) must
        // not be authorized purely by carrying the scope claim.
        Assert.False(ResourceOrToolPolicy.IsAuthorized(isSuperAdmin: false, isAdmin: false, isUser: false, hasToolExecutionScope: true));
}
