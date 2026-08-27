using Aiq.Gateway.Governance;

namespace Aiq.Gateway.Tests.Security;

public sealed class ExecutionAuthorizationTests
{
    [Fact]
    public void UserMustHaveActiveAssignment()
    {
        Assert.False(ExecutionAuthorizationRules.CanExecute(
            isSuperAdmin: false,
            isTenantAdmin: false,
            sameTenant: true,
            tenantActive: true,
            userActive: true,
            agentPublished: true,
            agentEnabled: true,
            activeAssignment: false));
    }

    [Fact]
    public void DisabledTenantUserOrAgentIsRejected()
    {
        Assert.False(ExecutionAuthorizationRules.CanExecute(false, false, true, false, true, true, true, true));
        Assert.False(ExecutionAuthorizationRules.CanExecute(false, false, true, true, false, true, true, true));
        Assert.False(ExecutionAuthorizationRules.CanExecute(false, false, true, true, true, true, false, true));
    }

    [Fact]
    public void CrossTenantAgentIsRejectedEvenWhenAssigned()
    {
        Assert.False(ExecutionAuthorizationRules.CanExecute(false, false, false, true, true, true, true, true));
    }

    [Fact]
    public void AdminCannotAccessAnotherTenantAgent()
    {
        Assert.False(ExecutionAuthorizationRules.CanExecute(false, true, false, true, true, true, true, true));
    }

    [Fact]
    public void UserCannotReadAnotherTenantAgentKnowledge()
    {
        // Knowledge context uses the same execution authorization rule as chat/run.
        Assert.False(ExecutionAuthorizationRules.CanExecute(false, false, false, true, true, true, true, true));
    }
}
