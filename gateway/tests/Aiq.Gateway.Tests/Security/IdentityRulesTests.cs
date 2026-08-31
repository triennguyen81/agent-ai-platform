using Aiq.Gateway.Data;

namespace Aiq.Gateway.Tests.Security;

public sealed class IdentityRulesTests
{
    [Fact]
    public void OnlyFourLoginRolesAreAccepted()
    {
        Assert.True(LoginRoleRules.IsAllowed("super_admin"));
        Assert.True(LoginRoleRules.IsAllowed("tenant_owner"));
        Assert.True(LoginRoleRules.IsAllowed("admin"));
        Assert.True(LoginRoleRules.IsAllowed("user"));
        Assert.False(LoginRoleRules.IsAllowed("reviewer"));
        Assert.False(LoginRoleRules.IsAllowed("tenant_admin"));
        Assert.False(LoginRoleRules.IsAllowed("owner"));
    }
}
