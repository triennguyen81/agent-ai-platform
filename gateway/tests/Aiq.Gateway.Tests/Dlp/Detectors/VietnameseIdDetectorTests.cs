using Aiq.Gateway.Dlp;
using Aiq.Gateway.Dlp.Detectors;

namespace Aiq.Gateway.Tests.Dlp.Detectors;

public sealed class VietnameseIdDetectorTests
{
    private readonly VietnameseIdDetector _detector = new();

    [Fact]
    public void Cmnd_WithKeywordContext_IsDetected()
    {
        var text = "Nhân viên có CMND 024123456 làm việc tại HCM";
        var matches = _detector.Detect(text).ToArray();

        Assert.Single(matches);
        Assert.Equal(PiiType.Cmnd, matches[0].Type);
        Assert.Equal("024123456", matches[0].OriginalValue);
    }

    [Fact]
    public void Cccd_WithKeywordContext_IsDetected()
    {
        var text = "Số CCCD: 024198765432 cấp tại Hà Nội";
        var matches = _detector.Detect(text).ToArray();

        Assert.Single(matches);
        Assert.Equal(PiiType.Cccd, matches[0].Type);
        Assert.Equal("024198765432", matches[0].OriginalValue);
    }

    [Fact]
    public void NineDigitWithoutContext_IsNotDetected()
    {
        // 9 chữ số random không có context "CMND" → không match (tránh false positive)
        var text = "Mã đơn hàng 123456789 đã được tạo ngày hôm qua";
        var matches = _detector.Detect(text).ToArray();

        Assert.Empty(matches);
    }

    [Fact]
    public void BothCmndAndCccd_InSameText_BothDetected()
    {
        var text = "CMND cũ: 024123456, CCCD mới: 024198765432";
        var matches = _detector.Detect(text).ToArray();

        Assert.Equal(2, matches.Length);
        Assert.Contains(matches, m => m.Type == PiiType.Cmnd);
        Assert.Contains(matches, m => m.Type == PiiType.Cccd);
    }

    [Fact]
    public void EmptyText_ReturnsEmpty()
    {
        Assert.Empty(_detector.Detect(""));
        Assert.Empty(_detector.Detect(null!));
    }
}
