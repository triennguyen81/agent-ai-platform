using System.Text.RegularExpressions;

namespace Aiq.Gateway.Dlp.Detectors;

/// <summary>
/// Email address — simplified RFC 5322.
/// Phủ 99% case thực tế; không cố cover edge case như quoted local-part
/// vì rất hiếm trong hoá đơn / chứng từ.
/// </summary>
public sealed partial class EmailDetector : IPiiDetector
{
    [GeneratedRegex(
        @"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9][a-zA-Z0-9\-]*(\.[a-zA-Z0-9][a-zA-Z0-9\-]*)*\.[a-zA-Z]{2,}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled)]
    private static partial Regex EmailRegex();

    public IEnumerable<PiiMatch> Detect(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        foreach (Match m in EmailRegex().Matches(text))
        {
            yield return new PiiMatch(m.Index, m.Length, PiiType.Email, m.Value);
        }
    }
}
