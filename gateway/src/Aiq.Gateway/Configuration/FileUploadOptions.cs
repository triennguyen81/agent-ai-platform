using System.ComponentModel.DataAnnotations;

namespace Aiq.Gateway.Configuration;

/// <summary>
/// WAF rules for file uploads. Configured via the <c>FileUpload</c> section.
/// </summary>
public sealed class FileUploadOptions
{
    public const string SectionName = "FileUpload";

    /// <summary>Hard cap in bytes. Requests larger than this are rejected before buffering.</summary>
    [Range(1, 1L * 1024 * 1024 * 1024)] // 1 GiB upper bound
    public long MaxBytes { get; init; } = 50L * 1024 * 1024; // 50 MiB

    /// <summary>Allowed MIME content types. Empty disables the check.</summary>
    public string[] AllowedContentTypes { get; init; } = ["application/pdf"];

    /// <summary>
    /// Required magic-byte prefix per allowed content type (lowercase MIME → hex string).
    /// Example: <c>application/pdf</c> → <c>25-50-44-46-2D</c> ("%PDF-").
    /// </summary>
    public Dictionary<string, string> MagicBytes { get; init; } = new()
    {
        ["application/pdf"] = "25-50-44-46-2D",
    };

    /// <summary>Maximum allowed filename length (defensive against path-traversal payloads).</summary>
    [Range(1, 1024)]
    public int MaxFilenameLength { get; init; } = 255;
}
