namespace Aiq.Gateway.Dlp;

/// <summary>
/// Reverse mapping store cho 1 request: token → original PII value.
///
/// Lifecycle:
///   - Tạo mới mỗi lần gọi IPiiScrubber.Scrub()
///   - Scope per-request, không shared giữa các request khác nhau
///   - TTL mặc định 10 phút để chống leak nếu caller quên dispose
///   - Dispose() clear dict để GC thu hồi memory nhanh
///
/// Thread-safe: không — chỉ 1 thread dùng vault tại 1 thời điểm (per-request).
/// </summary>
public sealed class PiiVault : IDisposable
{
    private readonly Dictionary<string, string> _tokenToOriginal;
    private bool _disposed;

    public DateTimeOffset CreatedAt { get; }
    public string Salt { get; }

    public int Count => _tokenToOriginal.Count;

    public PiiVault(string salt)
    {
        ArgumentNullException.ThrowIfNull(salt);
        _tokenToOriginal = new Dictionary<string, string>(StringComparer.Ordinal);
        Salt = salt;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Lưu mapping token → original. Nếu token đã tồn tại (collision — rất
    /// hiếm với 10-hex), KHÔNG override — giữ value đầu tiên để consistent.
    /// </summary>
    public void Store(string token, string original)
    {
        ThrowIfDisposed();
        _tokenToOriginal.TryAdd(token, original);
    }

    public bool TryGetOriginal(string token, out string? original)
    {
        ThrowIfDisposed();
        return _tokenToOriginal.TryGetValue(token, out original);
    }

    /// <summary>
    /// Vault còn trong window TTL không. Default 10 phút — đủ cho LLM
    /// round-trip chậm nhất mà không giữ memory quá lâu.
    /// </summary>
    public bool IsExpired(TimeSpan ttl)
    {
        return DateTimeOffset.UtcNow - CreatedAt > ttl;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _tokenToOriginal.Clear();
        _disposed = true;
    }
}
