using System.Text.RegularExpressions;

namespace Aiq.Gateway.Dlp.Detectors;

/// <summary>
/// Số tài khoản ngân hàng VN. Length rất đa dạng (6-19 chữ số tùy bank)
/// nên BẮT BUỘC có context keyword gần đó để tránh match số bất kỳ.
/// </summary>
public sealed partial class BankAccountDetector : IPiiDetector
{
    private static readonly string[] Keywords =
    [
        "stk", "s.tk",
        "số tài khoản", "so tai khoan",
        "tài khoản", "tai khoan",
        "tknh", "tk ngân hàng",
        "account no", "account number", "a/c",
    ];

    [GeneratedRegex(@"\b\d{6,19}\b", RegexOptions.CultureInvariant | RegexOptions.Compiled)]
    private static partial Regex DigitsRegex();

    public IEnumerable<PiiMatch> Detect(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        foreach (Match m in DigitsRegex().Matches(text))
        {
            // Skip quá ngắn — reduce noise
            if (m.Length < 6) continue;
            if (ContextHelper.HasKeyword(text, m.Index, Keywords, radius: 40))
            {
                yield return new PiiMatch(m.Index, m.Length, PiiType.BankAccount, m.Value);
            }
        }
    }
}
