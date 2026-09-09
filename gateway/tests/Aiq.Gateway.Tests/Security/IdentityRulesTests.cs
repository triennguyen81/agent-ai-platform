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

    [Fact]
    public void PrincipalRoleMustBeExactlyOneCanonicalLoginRole()
    {
        Assert.True(LoginRoleRules.TryGetExactRole(["ADMIN"], out var role));
        Assert.Equal("admin", role);
        Assert.False(LoginRoleRules.TryGetExactRole([], out _));
        Assert.False(LoginRoleRules.TryGetExactRole(["admin", "user"], out _));
        Assert.False(LoginRoleRules.TryGetExactRole(["reviewer"], out _));
    }
}
