using System.Text.RegularExpressions;

namespace Aiq.Gateway.Dlp.Detectors;

/// <summary>
/// Hộ chiếu VN: format 1 chữ cái + 7 chữ số (ví dụ B1234567, C9876543).
/// Bắt buộc có context "hộ chiếu" / "passport" để tránh false positive
/// với mã sản phẩm / mã đơn hàng.
/// </summary>
public sealed partial class PassportDetector : IPiiDetector
{
    private static readonly string[] Keywords = ["passport", "hộ chiếu", "ho chieu"];

    [GeneratedRegex(@"\b[A-Z]\d{7}\b", RegexOptions.CultureInvariant | RegexOptions.Compiled)]
    private static partial Regex PassportRegex();

    public IEnumerable<PiiMatch> Detect(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        foreach (Match m in PassportRegex().Matches(text))
        {
            if (ContextHelper.HasKeyword(text, m.Index, Keywords, radius: 40))
            {
                yield return new PiiMatch(m.Index, m.Length, PiiType.Passport, m.Value);
            }
        }
    }
}
