using System.Security.Cryptography;
using System.Text;

namespace Aiq.Gateway.Dlp;

/// <summary>
/// Sinh token placeholder deterministic cho PII value.
///
/// Deterministic nghĩa là CÙNG (value, salt, type) → CÙNG token trong 1
/// request. Khác request = khác salt = khác token. Điều này giúp agent
/// nhận ra "2 lần xuất hiện số CMND này là của cùng 1 người" mà vẫn
/// không thấy giá trị thật.
///
/// Format token: &lt;PREFIX_xxxxxxxxxx&gt; (10 hex chars lowercase)
///   - Collision resistant: 16^10 = ~1 trillion combos / salt
///   - Short enough để không spam prompt (16 chars total)
///   - Angle bracket + underscore giúp regex un-mask dễ parse
/// </summary>
public sealed class DeterministicMasker
{
    public string Mask(string value, string requestSalt, PiiType type)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(requestSalt);

        var payload = Encoding.UTF8.GetBytes($"{type}|{requestSalt}|{value}");
        var hash = SHA256.HashData(payload);
        var hex = Convert.ToHexStringLower(hash.AsSpan(0, 5));
        return $"<{type.TokenPrefix()}_{hex}>";
    }

    /// <summary>
    /// Sinh salt ngẫu nhiên 32 chars hex. Dùng khi caller không cung cấp
    /// salt cụ thể (mặc định mỗi Scrub() call có salt riêng).
    /// </summary>
    public static string GenerateSalt()
    {
        var buffer = new byte[16];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexStringLower(buffer);
    }
}
