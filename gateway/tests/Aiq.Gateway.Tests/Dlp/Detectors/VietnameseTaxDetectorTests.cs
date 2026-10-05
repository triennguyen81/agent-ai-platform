using Aiq.Gateway.Dlp;
using Aiq.Gateway.Dlp.Detectors;

namespace Aiq.Gateway.Tests.Dlp.Detectors;

public sealed class VietnameseTaxDetectorTests
{
    private readonly VietnameseTaxDetector _detector = new();

    [Fact]
    public void Mst10Digit_WithKeyword_IsDetected()
    {
        var text = "Công ty Việt Đăng có MST 0101243171 hoạt động từ 2015";
        var matches = _detector.Detect(text).ToArray();

        Assert.Single(matches);
        Assert.Equal(PiiType.Mst, matches[0].Type);
        Assert.Equal("0101243171", matches[0].OriginalValue);
    }

    [Fact]
    public void Mst13Digit_WithBranchCode_IsDetected()
    {
        var text = "Chi nhánh MST 0101243171-003 tại HCM";
        var matches = _detector.Detect(text).ToArray();

        Assert.Single(matches);
        Assert.Equal("0101243171-003", matches[0].OriginalValue);
    }

    [Fact]
    public void TenDigitNumber_WithoutContext_IsNotDetected()
    {
        // 10-digit number không có "MST" → không match (có thể là SĐT)
        var text = "Hoá đơn số 1234567890 đã xuất";
        var matches = _detector.Detect(text).ToArray();

        Assert.Empty(matches);
    }
}
