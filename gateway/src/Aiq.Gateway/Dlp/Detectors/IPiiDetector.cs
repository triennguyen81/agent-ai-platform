namespace Aiq.Gateway.Dlp.Detectors;

/// <summary>
/// Phát hiện PII trong text. Mỗi detector thường handle 1 loại PII; một
/// detector có thể return nhiều loại nếu cùng pattern (ví dụ
/// VietnameseIdDetector return cả Cmnd và Cccd).
///
/// Implementation phải:
///   - Thread-safe (singleton DI)
///   - Deterministic (cùng input → cùng matches)
///   - Không throw exception khi text có ký tự lạ
/// </summary>
public interface IPiiDetector
{
    /// <summary>
    /// Scan text, return mọi match tìm thấy. Order theo Start ascending.
    /// Return empty enumerable nếu không có match (không null).
    /// </summary>
    IEnumerable<PiiMatch> Detect(string text);
}
