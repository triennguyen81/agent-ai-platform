using Aiq.Gateway.Dlp;
using Aiq.Gateway.Dlp.Detectors;

namespace Aiq.Gateway.Tests.Dlp.Detectors;

public sealed class PassportDetectorTests
{
    private readonly PassportDetector _detector = new();

    [Fact]
    public void Passport_WithContext_IsDetected()
    {
        var text = "Hộ chiếu B1234567 cấp 2020";
        var matches = _detector.Detect(text).ToArray();

        Assert.Single(matches);
        Assert.Equal(PiiType.Passport, matches[0].Type);
        Assert.Equal("B1234567", matches[0].OriginalValue);
    }

    [Fact]
    public void PassportFormat_WithoutContext_IsNotDetected()
    {
        var text = "Mã sản phẩm A9876543 bán chạy";
        var matches = _detector.Detect(text).ToArray();

        Assert.Empty(matches);
    }
}

public sealed class DateOfBirthDetectorTests
{
    private readonly DateOfBirthDetector _detector = new();

    [Fact]
    public void Dob_WithNgaySinhContext_IsDetected()
    {
        var text = "Nguyễn Văn A ngày sinh 15/03/1985";
        var matches = _detector.Detect(text).ToArray();

        Assert.Single(matches);
        Assert.Equal(PiiType.DateOfBirth, matches[0].Type);
        Assert.Equal("15/03/1985", matches[0].OriginalValue);
    }

    [Fact]
    public void OrdinaryDate_WithoutContext_IsNotDetected()
    {
        // Ngày thường (ngày HĐ, ngày thanh toán) không mask
        var text = "Hoá đơn ngày 15/08/2026 đã xuất";
        var matches = _detector.Detect(text).ToArray();

        Assert.Empty(matches);
    }
}
