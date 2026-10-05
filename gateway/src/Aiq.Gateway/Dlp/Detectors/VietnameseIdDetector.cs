using System.Text.RegularExpressions;

namespace Aiq.Gateway.Dlp.Detectors;

/// <summary>
/// Phát hiện CMND (9 chữ số, giấy cũ) và CCCD (12 chữ số, hiện hành) VN.
///
/// Chiến lược:
///   - CCCD 12-digit CHỈ match khi có keyword context ("CCCD" / "căn cước")
///     vì 12-digit number quá generic → false positive cao
///   - CMND 9-digit TƯƠNG TỰ cần context "CMND" / "CMT" / "chứng minh"
///
/// Không dùng regex pure \d{12}\b vì sẽ match số đơn giá / số lượng.
/// </summary>
public sealed partial class VietnameseIdDetector : IPiiDetector
{
    private static readonly string[] CmndKeywords = ["cmnd", "cmt", "chứng minh", "chung minh"];
    private static readonly string[] CccdKeywords = ["cccd", "ccd", "căn cước", "can cuoc"];

    [GeneratedRegex(@"\b\d{9}\b", RegexOptions.CultureInvariant | RegexOptions.Compiled)]
    private static partial Regex NineDigitRegex();

    [GeneratedRegex(@"\b\d{12}\b", RegexOptions.CultureInvariant | RegexOptions.Compiled)]
    private static partial Regex TwelveDigitRegex();

    public IEnumerable<PiiMatch> Detect(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        foreach (Match m in TwelveDigitRegex().Matches(text))
        {
            if (ContextHelper.HasKeyword(text, m.Index, CccdKeywords))
            {
                yield return new PiiMatch(m.Index, m.Length, PiiType.Cccd, m.Value);
            }
        }

        foreach (Match m in NineDigitRegex().Matches(text))
        {
            if (ContextHelper.HasKeyword(text, m.Index, CmndKeywords))
            {
                yield return new PiiMatch(m.Index, m.Length, PiiType.Cmnd, m.Value);
            }
        }
    }
}
