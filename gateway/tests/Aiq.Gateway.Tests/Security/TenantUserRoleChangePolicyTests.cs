using Aiq.Gateway.Governance;

namespace Aiq.Gateway.Tests.Security;

public sealed class TenantUserRoleChangePolicyTests
{
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid TargetId = Guid.NewGuid();
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid OtherTenantId = Guid.NewGuid();

    [Fact]
    public void UserToAdminIsAllowed()
    {
        var (error, fromRole) = TenantUserRoleChangePolicy.Validate(
            ActorId, TargetId, TenantId, TenantId, new[] { "user" }, "admin");
        Assert.Null(error);
        Assert.Equal("user", fromRole);
    }

    [Fact]
    public void AdminToUserIsAllowed()
    {
        var (error, fromRole) = TenantUserRoleChangePolicy.Validate(
            ActorId, TargetId, TenantId, TenantId, new[] { "admin" }, "user");
        Assert.Null(error);
        Assert.Equal("admin", fromRole);
    }

    [Fact]
    public void CrossTenantTargetIsRejected() =>
        // Rule 10: cross-tenant modification must be impossible.
        Assert.Equal("cross_tenant", TenantUserRoleChangePolicy.Validate(
            ActorId, TargetId, OtherTenantId, TenantId, new[] { "user" }, "admin").Error);

    [Fact]
    public void TenantlessTargetIsRejected() =>
        Assert.Equal("cross_tenant", TenantUserRoleChangePolicy.Validate(
            ActorId, TargetId, null, TenantId, new[] { "user" }, "admin").Error);

    [Fact]
    public void CallerCannotModifySelf() =>
        // Rule 5: target is not caller.
        Assert.Equal("cannot_modify_self", TenantUserRoleChangePolicy.Validate(
            ActorId, ActorId, TenantId, TenantId, new[] { "admin" }, "user").Error);

    [Fact]
    public void TenantOwnerTargetIsRejected() =>
        // Rule 6: target is not tenant_owner. Ownership stays a separate,
        // dedicated workflow (migrations 017/018) — never touched here.
        Assert.Equal("target_is_tenant_owner", TenantUserRoleChangePolicy.Validate(
            ActorId, TargetId, TenantId, TenantId, new[] { "tenant_owner" }, "admin").Error);

    [Fact]
    public void SuperAdminTargetIsRejected() =>
        // Rule 7: target is not super_admin.
        Assert.Equal("target_is_super_admin", TenantUserRoleChangePolicy.Validate(
            ActorId, TargetId, TenantId, TenantId, new[] { "super_admin" }, "admin").Error);

    [Fact]
    public void TargetWithNoModifiableRoleIsRejected() =>
        // Rule 8: current role must be user or admin.
        Assert.Equal("target_has_no_modifiable_role", TenantUserRoleChangePolicy.Validate(
            ActorId, TargetId, TenantId, TenantId, Array.Empty<string>(), "admin").Error);

    [Fact]
    public void RequestedRoleMustBeUserOrAdmin() =>
        // Rule 9: requested role must be user or admin — tenant_owner and
        // super_admin are structurally unreachable as a requested role.
        Assert.Equal("invalid_requested_role", TenantUserRoleChangePolicy.Validate(
            ActorId, TargetId, TenantId, TenantId, new[] { "user" }, "tenant_owner").Error);

    [Fact]
    public void RequestingSuperAdminAsTargetRoleIsRejected() =>
        Assert.Equal("invalid_requested_role", TenantUserRoleChangePolicy.Validate(
            ActorId, TargetId, TenantId, TenantId, new[] { "user" }, "super_admin").Error);

    [Fact]
    public void RequestingSameRoleIsRejectedAsNoChange() =>
        Assert.Equal("no_change", TenantUserRoleChangePolicy.Validate(
            ActorId, TargetId, TenantId, TenantId, new[] { "admin" }, "admin").Error);
}
