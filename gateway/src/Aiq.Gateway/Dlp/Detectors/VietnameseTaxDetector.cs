using System.Text.RegularExpressions;

namespace Aiq.Gateway.Dlp.Detectors;

/// <summary>
/// Mã số thuế (MST) VN: 10 chữ số (doanh nghiệp) hoặc 13 chữ số (có mã
/// chi nhánh, format "0101243171-003"). Cần context "MST" / "mã số thuế"
/// để tránh nhầm với SĐT 10-digit.
/// </summary>
public sealed partial class VietnameseTaxDetector : IPiiDetector
{
    private static readonly string[] Keywords = ["mst", "mã số thuế", "ma so thue", "tax code", "tax id"];

    [GeneratedRegex(@"\b\d{10}(-\d{3})?\b", RegexOptions.CultureInvariant | RegexOptions.Compiled)]
    private static partial Regex MstRegex();

    public IEnumerable<PiiMatch> Detect(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        foreach (Match m in MstRegex().Matches(text))
        {
            // Phải có context để confirm — tránh match SĐT 10-digit hoặc
            // số serial 10-digit random.
            if (ContextHelper.HasKeyword(text, m.Index, Keywords, radius: 40))
            {
                yield return new PiiMatch(m.Index, m.Length, PiiType.Mst, m.Value);
            }
        }
    }
}
