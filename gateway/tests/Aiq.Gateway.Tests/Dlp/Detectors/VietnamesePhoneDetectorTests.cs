using Aiq.Gateway.Dlp;
using Aiq.Gateway.Dlp.Detectors;

namespace Aiq.Gateway.Tests.Dlp.Detectors;

public sealed class VietnamesePhoneDetectorTests
{
    private readonly VietnamesePhoneDetector _detector = new();

    [Theory]
    [InlineData("Liên hệ 0912345678", "0912345678")]
    [InlineData("Hotline 0387654321", "0387654321")]
    [InlineData("Call 0987654321 or email", "0987654321")]
    public void MobileWithZeroPrefix_IsDetected(string text, string expected)
    {
        var matches = _detector.Detect(text).ToArray();
        Assert.Single(matches);
        Assert.Equal(PiiType.Phone, matches[0].Type);
        Assert.Equal(expected, matches[0].OriginalValue);
    }

    [Fact]
    public void MobileWithPlus84Prefix_IsDetected()
    {
        var text = "International: +84912345678";
        var matches = _detector.Detect(text).ToArray();

        Assert.Single(matches);
        Assert.Equal("+84912345678", matches[0].OriginalValue);
    }

    [Fact]
    public void LandlineHCM_IsDetected()
    {
        // VN landline sau 2017 migration: 10 chữ số
        // 028 (HCM) + 7 digit = 10 total
        var text = "Văn phòng HCM: 0283812345";
        var matches = _detector.Detect(text).ToArray();

        Assert.Single(matches);
    }

    [Fact]
    public void NonVietnameseNumber_IsNotDetected()
    {
        // US phone number không match
        var text = "US office: 12125551234";
        var matches = _detector.Detect(text).ToArray();

        Assert.Empty(matches);
    }
}
