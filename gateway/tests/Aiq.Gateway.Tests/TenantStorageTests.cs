using Aiq.Gateway.Storage;

namespace Aiq.Gateway.Tests;

public sealed class TenantStorageTests
{
    [Fact]
    public void ObjectKeysAreTenantScopedAndCollisionSafe()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var key = TenantObjectKey.Build(tenantA, "vouchers", "user", "file.pdf");

        Assert.True(TenantObjectKey.BelongsTo(tenantA, key));
        Assert.False(TenantObjectKey.BelongsTo(tenantB, key));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/path")]
    [InlineData("")]
    public void ObjectKeysRejectUnsafeSegments(string segment)
    {
        Assert.Throws<ArgumentException>(() => TenantObjectKey.Build(Guid.NewGuid(), segment));
    }
}
