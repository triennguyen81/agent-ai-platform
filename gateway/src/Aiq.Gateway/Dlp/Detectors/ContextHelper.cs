namespace Aiq.Gateway.Dlp.Detectors;

/// <summary>
/// Helper tra context xung quanh match — nhiều detector cần confirm từ
/// khoá gần đó (ví dụ "CMND", "MST", "STK") để tránh false positive với
/// dãy số thuần tuý.
/// </summary>
internal static class ContextHelper
{
    /// <summary>
    /// Return substring quanh index [max(0, index - radius), min(len, index + radius)]
    /// để tìm kiếm case-insensitive context keyword.
    /// </summary>
    public static string Nearby(string text, int index, int radius = 30)
    {
        var start = Math.Max(0, index - radius);
        var end = Math.Min(text.Length, index + radius);
        return text[start..end];
    }

    /// <summary>
    /// True nếu một trong các keyword xuất hiện trong window quanh index.
    /// Case-insensitive, không phân biệt dấu/không dấu VN (naive — chỉ
    /// so sánh lowercase ASCII; keyword truyền vào đã normalize sẵn).
    /// </summary>
    public static bool HasKeyword(string text, int index, IReadOnlyCollection<string> keywords, int radius = 30)
    {
        var window = Nearby(text, index, radius).ToLowerInvariant();
        foreach (var kw in keywords)
        {
            if (window.Contains(kw)) return true;
        }
        return false;
    }
}
