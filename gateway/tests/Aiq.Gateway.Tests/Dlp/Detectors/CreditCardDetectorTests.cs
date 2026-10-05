using Aiq.Gateway.Dlp.Detectors;

namespace Aiq.Gateway.Tests.Dlp.Detectors;

public sealed class CreditCardDetectorTests
{
    private readonly CreditCardDetector _detector = new();

    [Fact]
    public void ValidVisaCard_LuhnPass_IsDetected()
    {
        // Visa test card: 4111 1111 1111 1111
        var text = "Thanh toán bằng card 4111111111111111";
        var matches = _detector.Detect(text).ToArray();

        Assert.Single(matches);
        Assert.Equal("4111111111111111", matches[0].OriginalValue);
    }

    [Fact]
    public void ValidCard_WithSpaces_IsDetected()
    {
        // Mastercard test: 5555 5555 5555 4444
        var text = "Card: 5555 5555 5555 4444 expires 12/26";
        var matches = _detector.Detect(text).ToArray();

        Assert.Single(matches);
    }

    [Fact]
    public void InvalidLuhn_IsNotDetected()
    {
        // 16 chữ số nhưng không pass Luhn
        var text = "Fake card 1234567812345678 should be ignored";
        var matches = _detector.Detect(text).ToArray();

        Assert.Empty(matches);
    }

    [Theory]
    [InlineData("4111111111111111", true)]  // Visa
    [InlineData("5555555555554444", true)]  // Mastercard
    [InlineData("378282246310005", true)]   // Amex
    [InlineData("1234567890123456", false)] // Invalid
    public void LuhnValidator_WorksCorrectly(string digits, bool expected)
    {
        Assert.Equal(expected, CreditCardDetector.IsLuhnValid(digits));
    }
}
