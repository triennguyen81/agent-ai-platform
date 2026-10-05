using Aiq.Gateway.Dlp;
using Aiq.Gateway.Dlp.Detectors;

namespace Aiq.Gateway.Tests.Dlp.Detectors;

public sealed class EmailDetectorTests
{
    private readonly EmailDetector _detector = new();

    [Theory]
    [InlineData("Liên hệ ketoan@vietdang.vn để biết thêm", "ketoan@vietdang.vn")]
    [InlineData("Email: john.doe@example.com", "john.doe@example.com")]
    [InlineData("Hotmail user1+tag@sub.hotmail.co.uk", "user1+tag@sub.hotmail.co.uk")]
    public void ValidEmail_IsDetected(string text, string expected)
    {
        var matches = _detector.Detect(text).ToArray();
        Assert.Single(matches);
        Assert.Equal(PiiType.Email, matches[0].Type);
        Assert.Equal(expected, matches[0].OriginalValue);
    }

    [Fact]
    public void NoEmail_ReturnsEmpty()
    {
        Assert.Empty(_detector.Detect("Chỉ có text thuần không có email"));
    }
}
