using System.Text.RegularExpressions;

namespace Aiq.Gateway.Vouchers.Validation;

/// <summary>
/// Mã số thuế rules (QĐ 9): 10 digits; 13 digits as 10-3 (branch, dash optional);
/// 12 digits (CCCD used as a personal MST). Comparison ignores dashes and spaces.
/// </summary>
public static partial class MstRules
{
    [GeneratedRegex(@"^(\d{10}|\d{12}|\d{13}|\d{10}-\d{3})$", RegexOptions.CultureInvariant)]
    private static partial Regex Format();

    private static readonly int[] Weights = [31, 29, 23, 19, 17, 13, 7, 5, 3];

    public static bool IsValidFormat(string? mst) => mst is not null && Format().IsMatch(mst.Trim());

    /// <summary>Digits only, for comparison — never written back over the stored value.</summary>
    public static string? Normalize(string? mst)
    {
        if (string.IsNullOrWhiteSpace(mst)) return null;
        var digits = new string(mst.Where(c => c is not ('-' or ' ' or '\t')).ToArray());
        return digits.Length == 0 ? null : digits;
    }

    /// <summary>
    /// Check digit of a 10-digit MST (or the first 10 of a 13-digit one):
    /// d10 = 10 − (Σ dᵢ·wᵢ mod 11), a result of 10 is invalid. Null when there
    /// is no checksum rule (12-digit CCCD). The algorithm is not yet confirmed
    /// by the accountant, so callers treat a mismatch as a warning only.
    /// </summary>
    public static bool? ChecksumOk(string? mst)
    {
        var digits = Normalize(mst);
        if (digits is null || !digits.All(char.IsAsciiDigit) || digits.Length is not (10 or 13)) return null;
        var sum = 0;
        for (var i = 0; i < Weights.Length; i++) sum += (digits[i] - '0') * Weights[i];
        var check = 10 - sum % 11;
        return check != 10 && check == digits[9] - '0';
    }
}
