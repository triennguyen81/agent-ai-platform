namespace Aiq.Gateway.Dlp;

/// <summary>
/// Một PII match tìm thấy trong text. Immutable record.
/// </summary>
/// <param name="Start">Index trong text input (0-based).</param>
/// <param name="Length">Độ dài substring đã match.</param>
/// <param name="Type">Loại PII.</param>
/// <param name="OriginalValue">Giá trị gốc (dùng để lưu vào vault + un-mask sau).</param>
public sealed record PiiMatch(int Start, int Length, PiiType Type, string OriginalValue)
{
    public int End => Start + Length;

    /// <summary>
    /// Kiểm tra 2 match có overlap vị trí nhau không (dùng để resolve
    /// khi nhiều detector cùng match một đoạn).
    /// </summary>
    public bool Overlaps(PiiMatch other) =>
        Start < other.End && other.Start < End;
}
