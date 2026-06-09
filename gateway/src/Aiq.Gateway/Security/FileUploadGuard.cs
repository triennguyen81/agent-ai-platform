using System.Security.Cryptography;
using Aiq.Gateway.Configuration;
using Microsoft.Extensions.Options;

namespace Aiq.Gateway.Security;

public interface IFileUploadGuard
{
    /// <summary>Validate metadata before reading the body — cheap.</summary>
    FileUploadValidationResult ValidateMetadata(string? filename, string? contentType, long? contentLength);

    /// <summary>Validate body bytes — confirms declared content-type via magic bytes.</summary>
    FileUploadValidationResult ValidateBody(string contentType, ReadOnlySpan<byte> header);

    /// <summary>SHA-256 hex digest of a stream. Resets stream position to 0 when done.</summary>
    Task<string> ComputeSha256Async(Stream stream, CancellationToken ct);
}

public sealed record FileUploadValidationResult(bool Ok, string? Code = null, string? Reason = null)
{
    public static FileUploadValidationResult Accepted { get; } = new(true);
    public static FileUploadValidationResult Rejected(string code, string reason) => new(false, code, reason);
}

public sealed class FileUploadGuard : IFileUploadGuard
{
    private readonly FileUploadOptions _options;
    private readonly Dictionary<string, byte[]> _magicByContentType;

    public FileUploadGuard(IOptions<FileUploadOptions> options)
    {
        _options = options.Value;
        _magicByContentType = _options.MagicBytes.ToDictionary(
            kv => kv.Key.ToLowerInvariant(),
            kv => ParseHex(kv.Value));
    }

    public FileUploadValidationResult ValidateMetadata(string? filename, string? contentType, long? contentLength)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            return FileUploadValidationResult.Rejected("missing_filename", "Filename is required.");
        }

        if (filename.Length > _options.MaxFilenameLength)
        {
            return FileUploadValidationResult.Rejected(
                "filename_too_long",
                $"Filename exceeds {_options.MaxFilenameLength} characters.");
        }

        if (ContainsPathTraversal(filename))
        {
            return FileUploadValidationResult.Rejected(
                "unsafe_filename",
                "Filename contains path separators or traversal sequences.");
        }

        if (string.IsNullOrWhiteSpace(contentType))
        {
            return FileUploadValidationResult.Rejected("missing_content_type", "Content-Type is required.");
        }

        var normalized = NormalizeContentType(contentType);
        if (_options.AllowedContentTypes.Length > 0
            && !_options.AllowedContentTypes.Any(allowed =>
                string.Equals(allowed, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return FileUploadValidationResult.Rejected(
                "unsupported_content_type",
                $"Content-Type '{normalized}' not in the allow-list.");
        }

        if (contentLength is null)
        {
            return FileUploadValidationResult.Rejected(
                "missing_content_length",
                "Content-Length header is required for upload validation.");
        }

        if (contentLength.Value <= 0)
        {
            return FileUploadValidationResult.Rejected("empty_body", "Upload body is empty.");
        }

        if (contentLength.Value > _options.MaxBytes)
        {
            return FileUploadValidationResult.Rejected(
                "payload_too_large",
                $"Upload size {contentLength.Value} exceeds limit {_options.MaxBytes}.");
        }

        return FileUploadValidationResult.Accepted;
    }

    public FileUploadValidationResult ValidateBody(string contentType, ReadOnlySpan<byte> header)
    {
        var normalized = NormalizeContentType(contentType);
        if (normalized is null || !_magicByContentType.TryGetValue(normalized, out var magic))
        {
            // No magic-byte rule configured for this content type — skip check.
            return FileUploadValidationResult.Accepted;
        }

        if (header.Length < magic.Length)
        {
            return FileUploadValidationResult.Rejected(
                "truncated_body",
                "Upload body is smaller than the expected magic-byte prefix.");
        }

        if (!header[..magic.Length].SequenceEqual(magic))
        {
            return FileUploadValidationResult.Rejected(
                "magic_byte_mismatch",
                "Declared content type does not match the file body.");
        }

        return FileUploadValidationResult.Accepted;
    }

    public async Task<string> ComputeSha256Async(Stream stream, CancellationToken ct)
    {
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);

        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? NormalizeContentType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var semicolon = raw.IndexOf(';');
        var value = semicolon >= 0 ? raw[..semicolon] : raw;
        return value.Trim().ToLowerInvariant();
    }

    private static bool ContainsPathTraversal(string filename)
    {
        return filename.Contains('/', StringComparison.Ordinal)
            || filename.Contains('\\', StringComparison.Ordinal)
            || filename.Contains("..", StringComparison.Ordinal)
            || filename.Contains('\0', StringComparison.Ordinal);
    }

    private static byte[] ParseHex(string spec)
    {
        var compact = spec.Replace("-", string.Empty).Replace(" ", string.Empty);
        if (compact.Length % 2 != 0)
        {
            throw new InvalidOperationException($"Invalid magic-byte hex string: '{spec}'");
        }
        return Convert.FromHexString(compact);
    }
}
