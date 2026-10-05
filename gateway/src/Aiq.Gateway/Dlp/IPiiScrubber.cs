namespace Aiq.Gateway.Dlp;

/// <summary>
/// Main entry point cho DLP pipeline. Hai thao tác:
///
///   1. Scrub(text) → thay mọi PII bằng token placeholder, return
///      (maskedText, vault). Vault giữ reverse mapping cho un-mask.
///
///   2. Unmask(text, vault) → quét token trong text, replace lại bằng
///      PII gốc dựa trên vault.
///
/// Caller DISPOSE vault sau khi dùng xong (bọc using statement).
/// </summary>
public interface IPiiScrubber
{
    ScrubResult Scrub(string text);
    ScrubResult Scrub(string text, string salt);
    string Unmask(string text, PiiVault vault);
}

public sealed record ScrubResult(string MaskedText, PiiVault Vault, int DetectedCount);
