using Aiq.Gateway.Dlp;

namespace Aiq.Gateway.Tests.Dlp;

public sealed class PiiVaultTests
{
    [Fact]
    public void Store_And_Retrieve_RoundTrip()
    {
        using var vault = new PiiVault("salt");
        vault.Store("<CMND_abc123>", "024123456");

        Assert.True(vault.TryGetOriginal("<CMND_abc123>", out var original));
        Assert.Equal("024123456", original);
        Assert.Equal(1, vault.Count);
    }

    [Fact]
    public void Store_SameTokenTwice_KeepsFirstValue()
    {
        using var vault = new PiiVault("salt");
        vault.Store("<CMND_abc123>", "value-A");
        vault.Store("<CMND_abc123>", "value-B");

        Assert.True(vault.TryGetOriginal("<CMND_abc123>", out var original));
        Assert.Equal("value-A", original);
    }

    [Fact]
    public void UnknownToken_ReturnsFalse()
    {
        using var vault = new PiiVault("salt");
        Assert.False(vault.TryGetOriginal("<unknown>", out var original));
        Assert.Null(original);
    }

    [Fact]
    public void IsExpired_FarInFuture_True()
    {
        using var vault = new PiiVault("salt");
        Assert.True(vault.IsExpired(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Dispose_ThenAccess_Throws()
    {
        var vault = new PiiVault("salt");
        vault.Store("<x>", "v");
        vault.Dispose();

        Assert.Throws<ObjectDisposedException>(() => vault.TryGetOriginal("<x>", out _));
    }

    [Fact]
    public void SaltAndCreatedAt_AreExposed()
    {
        using var vault = new PiiVault("my-salt");
        Assert.Equal("my-salt", vault.Salt);
        Assert.True(vault.CreatedAt <= DateTimeOffset.UtcNow);
    }
}
