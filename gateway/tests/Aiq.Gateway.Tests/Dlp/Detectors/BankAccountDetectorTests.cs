using Aiq.Gateway.Dlp;
using Aiq.Gateway.Dlp.Detectors;

namespace Aiq.Gateway.Tests.Dlp.Detectors;

public sealed class BankAccountDetectorTests
{
    private readonly BankAccountDetector _detector = new();

    [Fact]
    public void BankAccount_WithStkKeyword_IsDetected()
    {
        var text = "STK 0123456789012 tại Vietcombank";
        var matches = _detector.Detect(text).ToArray();

        Assert.Single(matches);
        Assert.Equal(PiiType.BankAccount, matches[0].Type);
        Assert.Equal("0123456789012", matches[0].OriginalValue);
    }

    [Fact]
    public void BankAccount_WithTaiKhoanKeyword_IsDetected()
    {
        var text = "Chuyển vào số tài khoản 1234567890 ngân hàng ABC";
        var matches = _detector.Detect(text).ToArray();

        Assert.Single(matches);
        Assert.Equal("1234567890", matches[0].OriginalValue);
    }

    [Fact]
    public void LongDigitsWithoutContext_IsNotDetected()
    {
        // 10-digit random không có context → không match
        var text = "Order 1234567890 shipped yesterday";
        var matches = _detector.Detect(text).ToArray();

        Assert.Empty(matches);
    }
}
