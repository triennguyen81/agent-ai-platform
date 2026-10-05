using System.Text.RegularExpressions;

namespace Aiq.Gateway.Dlp.Detectors;

/// <summary>
/// Credit/debit card: 13-19 chữ số (có thể cách bằng space hoặc dấu
/// gạch), VERIFY bằng Luhn algorithm để loại trừ số random dài.
///
/// Không yêu cầu context keyword — Luhn check đủ specific.
/// </summary>
public sealed partial class CreditCardDetector : IPiiDetector
{
    // 4 groups of 4 digit (16 total) is most common; cover 13-19 range
    // Separator: space hoặc gạch nối, tùy ý mỗi 4 digit.
    [GeneratedRegex(
        @"(?<![0-9])(\d{4}[\s\-]?\d{4}[\s\-]?\d{4}[\s\-]?\d{1,7})(?![0-9])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled)]
    private static partial Regex CardRegex();

    public IEnumerable<PiiMatch> Detect(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        foreach (Match m in CardRegex().Matches(text))
        {
            var digitsOnly = StripNonDigits(m.Value);
            if (digitsOnly.Length is < 13 or > 19) continue;
            if (!IsLuhnValid(digitsOnly)) continue;

            yield return new PiiMatch(m.Index, m.Length, PiiType.CreditCard, m.Value);
        }
    }

    private static string StripNonDigits(string s)
    {
        return new string(s.Where(char.IsDigit).ToArray());
    }

    /// <summary>
    /// Luhn mod-10 check. Standard algorithm — dùng để verify card number
    /// không bị gõ sai và cũng loại trừ dãy số random.
    /// </summary>
    public static bool IsLuhnValid(string digits)
    {
        if (string.IsNullOrEmpty(digits)) return false;

        var sum = 0;
        var alternate = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            if (!char.IsDigit(digits[i])) return false;
            var n = digits[i] - '0';
            if (alternate)
            {
                n *= 2;
                if (n > 9) n -= 9;
            }
            sum += n;
            alternate = !alternate;
        }
        return sum % 10 == 0;
    }
}
