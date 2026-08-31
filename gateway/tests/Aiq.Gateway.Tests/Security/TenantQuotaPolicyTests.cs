using Aiq.Gateway.Governance;

namespace Aiq.Gateway.Tests.Security;

public sealed class TenantQuotaPolicyTests
{
    [Fact]
    public void NullLimitIsAlwaysUnlimited() =>
        Assert.True(TenantQuotaPolicy.HasRoomFor(max: null, current: 1_000_000));

    [Fact]
    public void RoomRemainsBelowLimit() =>
        Assert.True(TenantQuotaPolicy.HasRoomFor(max: 5, current: 4));

    [Fact]
    public void AtLimitHasNoRoom() =>
        // current == max means the cap is already fully consumed — the next
        // create must be rejected, not allowed to reach current == max + 1.
        Assert.False(TenantQuotaPolicy.HasRoomFor(max: 5, current: 5));

    [Fact]
    public void OverLimitHasNoRoom() =>
        Assert.False(TenantQuotaPolicy.HasRoomFor(max: 5, current: 6));

    [Fact]
    public void ZeroLimitAllowsNothing() =>
        Assert.False(TenantQuotaPolicy.HasRoomFor(max: 0, current: 0));
}
