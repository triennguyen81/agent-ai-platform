using System.Text.RegularExpressions;

namespace Aiq.Gateway.Dlp.Detectors;

/// <summary>
/// Số điện thoại VN. Mobile prefix: 03/05/07/08/09 (+ 8 digit sau).
/// Landline (hiếm gặp trong hoá đơn): 02x. Hỗ trợ +84 prefix.
///
/// Match không cần context vì format VN phone đủ specific
/// (phải bắt đầu bằng 0 hoặc +84 kèm prefix hợp lệ).
/// </summary>
public sealed partial class VietnamesePhoneDetector : IPiiDetector
{
    // Mobile: 0[3|5|7|8|9] + 8 digit = 10 digit total; hoặc +843/+845/... + 8 digit
    // Landline: 02 + 1 digit area + 7 digit number = 10 digit total
    // Note: không dùng \b trước vì + là non-word char, dùng (?<![0-9]) để assert no digit trước
    [GeneratedRegex(
        @"(?<![0-9])(\+84|0)(3\d|5\d|7\d|8\d|9\d|2[0-9])\d{7}(?![0-9])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled)]
    private static partial Regex PhoneRegex();

    public IEnumerable<PiiMatch> Detect(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        foreach (Match m in PhoneRegex().Matches(text))
        {
            yield return new PiiMatch(m.Index, m.Length, PiiType.Phone, m.Value);
        }
    }
}
