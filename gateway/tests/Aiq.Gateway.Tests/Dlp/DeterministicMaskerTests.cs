using Aiq.Gateway.Dlp;

namespace Aiq.Gateway.Tests.Dlp;

public sealed class DeterministicMaskerTests
{
    private readonly DeterministicMasker _masker = new();

    [Fact]
    public void SameInputSameSalt_YieldsSameToken()
    {
        var salt = "test-salt-123";
        var t1 = _masker.Mask("024123456", salt, PiiType.Cmnd);
        var t2 = _masker.Mask("024123456", salt, PiiType.Cmnd);

        Assert.Equal(t1, t2);
    }

    [Fact]
    public void SameInputDifferentSalt_YieldsDifferentToken()
    {
        var t1 = _masker.Mask("024123456", "salt-A", PiiType.Cmnd);
        var t2 = _masker.Mask("024123456", "salt-B", PiiType.Cmnd);

        Assert.NotEqual(t1, t2);
    }

    [Fact]
    public void DifferentTypes_YieldDifferentTokens()
    {
        var salt = "common-salt";
        var t1 = _masker.Mask("1234567890", salt, PiiType.Mst);
        var t2 = _masker.Mask("1234567890", salt, PiiType.Phone);

        Assert.NotEqual(t1, t2);
        Assert.StartsWith("<MST_", t1);
        Assert.StartsWith("<PHONE_", t2);
    }

    [Fact]
    public void TokenFormat_IsAngleBracketedWithTypePrefix()
    {
        var token = _masker.Mask("test@example.com", "salt", PiiType.Email);
        Assert.Matches(@"^<EMAIL_[a-f0-9]{10}>$", token);
    }

    [Fact]
    public void GenerateSalt_IsRandomAndHex()
    {
        var s1 = DeterministicMasker.GenerateSalt();
        var s2 = DeterministicMasker.GenerateSalt();

        Assert.NotEqual(s1, s2);
        Assert.Equal(32, s1.Length);  // 16 bytes = 32 hex chars
        Assert.Matches(@"^[a-f0-9]+$", s1);
    }
}
