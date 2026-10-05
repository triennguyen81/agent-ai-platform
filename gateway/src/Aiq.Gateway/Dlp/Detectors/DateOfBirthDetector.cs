using System.Text.RegularExpressions;

namespace Aiq.Gateway.Dlp.Detectors;

/// <summary>
/// Ngày sinh: format dd/mm/yyyy hoặc dd-mm-yyyy. Cần context "ngày sinh"
/// / "sinh" / "DoB" vì ngày tháng là ubiquitous (document date, due date,
/// payment date — không phải DoB).
/// </summary>
public sealed partial class DateOfBirthDetector : IPiiDetector
{
    private static readonly string[] Keywords = ["ngày sinh", "ngay sinh", "dob", "d.o.b", "sinh ngày", "sinh:"];

    [GeneratedRegex(
        @"\b(0?[1-9]|[12][0-9]|3[01])[/\-](0?[1-9]|1[0-2])[/\-](19|20)\d{2}\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled)]
    private static partial Regex DobRegex();

    public IEnumerable<PiiMatch> Detect(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        foreach (Match m in DobRegex().Matches(text))
        {
            if (ContextHelper.HasKeyword(text, m.Index, Keywords, radius: 40))
            {
                yield return new PiiMatch(m.Index, m.Length, PiiType.DateOfBirth, m.Value);
            }
        }
    }
}
