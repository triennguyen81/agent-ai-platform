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
            sameDepartment: true,
            tenantActive: true,
            userActive: true,
            agentPublished: true,
            agentEnabled: true,
            activeAssignment: false));
    }

    [Fact]
    public void DisabledTenantUserOrAgentIsRejected()
    {
        Assert.False(ExecutionAuthorizationRules.CanExecute(false, false, true, true, false, true, true, true, true));
        Assert.False(ExecutionAuthorizationRules.CanExecute(false, false, true, true, true, false, true, true, true));
        Assert.False(ExecutionAuthorizationRules.CanExecute(false, false, true, true, true, true, true, false, true));
    }

    [Fact]
    public void CrossTenantAgentIsRejectedEvenWhenAssigned()
    {
        Assert.False(ExecutionAuthorizationRules.CanExecute(false, false, false, true, true, true, true, true, true));
    }

    [Fact]
    public void AdminCannotAccessAnotherTenantAgent()
    {
        Assert.False(ExecutionAuthorizationRules.CanExecute(false, true, false, true, true, true, true, true, true));
    }

    [Fact]
    public void UserCannotReadAnotherTenantAgentKnowledge()
    {
        // Knowledge context uses the same execution authorization rule as chat/run.
        Assert.False(ExecutionAuthorizationRules.CanExecute(false, false, false, true, true, true, true, true, true));
    }

    // ACL V2 review: a Department Agent's tenant_id equals its owning
    // tenant, so "same tenant" alone used to be enough for an ADMIN of a
    // DIFFERENT department in that same tenant to pass this check (real
    // gap found reviewing GET /runtime/agents/{id}/authorization and
    // AgentKnowledgeEndpoints.ContextAsync, both driven by this same rule).
    // sameDepartment=false now blocks it even when isTenantAdmin=true.

    [Fact]
    public void AdminCannotAccessAnotherDepartmentsAgentInSameTenant()
    {
        Assert.False(ExecutionAuthorizationRules.CanExecute(
            isSuperAdmin: false, isTenantAdmin: true, sameTenant: true, sameDepartment: false,
            tenantActive: true, userActive: true, agentPublished: true, agentEnabled: true, activeAssignment: false));
    }

    [Fact]
    public void UserCannotReadAnotherDepartmentsAgentKnowledgeEvenWithStaleAssignment()
    {
        // Even a (stale/unexpected) active assignment row must not bypass
        // the department check — same defense-in-depth posture as
        // CrossTenantAgentIsRejectedEvenWhenAssigned.
        Assert.False(ExecutionAuthorizationRules.CanExecute(
            isSuperAdmin: false, isTenantAdmin: false, sameTenant: true, sameDepartment: false,
            tenantActive: true, userActive: true, agentPublished: true, agentEnabled: true, activeAssignment: true));
    }

    [Fact]
    public void SuperAdminBypassesDepartmentCheckLikeTenantCheck()
    {
        Assert.True(ExecutionAuthorizationRules.CanExecute(
            isSuperAdmin: true, isTenantAdmin: false, sameTenant: false, sameDepartment: false,
            tenantActive: true, userActive: true, agentPublished: true, agentEnabled: true, activeAssignment: false));
    }

    [Fact]
    public void SameDepartmentAdminIsAllowed()
    {
        Assert.True(ExecutionAuthorizationRules.CanExecute(
            isSuperAdmin: false, isTenantAdmin: true, sameTenant: true, sameDepartment: true,
            tenantActive: true, userActive: true, agentPublished: true, agentEnabled: true, activeAssignment: false));
    }
}
