using System.Security.Cryptography;

namespace Aiq.Gateway.Storage;

/// <summary>
/// Abstract object-storage boundary — implementations wrap S3-compatible
/// backends (MinIO for cosmos, AWS S3 or Cloudflare R2 later).
///
/// The gateway uses this instead of writing to a local Docker volume so
/// uploaded voucher/contract files:
///   - Are addressable by short-lived signed URLs (browser fetches directly)
///   - Survive container rebuilds automatically (volume vs bucket)
///   - Can migrate between clouds without app changes (same interface)
///   - Are encrypted at rest by the object-store backend
/// </summary>
public interface IObjectStorage
{
    /// <summary>
    /// Upload a stream to the given bucket + key. Returns the SHA-256 of
    /// the uploaded bytes so callers can store it for audit / dedupe.
    /// The stream is fully consumed and disposed by this method.
    /// </summary>
    Task<PutObjectResult> PutAsync(
        string bucket,
        string key,
        Stream content,
        string? contentType,
        CancellationToken ct);

    /// <summary>
    /// Return a signed URL the caller can share with a browser client.
    /// The URL is valid for the given TTL — defaults to 5 minutes if
    /// the caller passes null.
    /// </summary>
    Task<string> GetSignedUrlAsync(
        string bucket,
        string key,
        TimeSpan? expiry,
        CancellationToken ct);

    /// <summary>
    /// Delete an object. No error if the key is already missing (idempotent).
    /// </summary>
    Task DeleteAsync(string bucket, string key, CancellationToken ct);

    /// <summary>
    /// Cheap health check — return true when the backend responds. Used
    /// by /healthz/ready and by tests that want to gate on connectivity.
    /// </summary>
    Task<bool> IsHealthyAsync(CancellationToken ct);
}

public sealed record PutObjectResult(
    string Bucket,
    string Key,
    long SizeBytes,
    string Sha256Hex,
    string? ETag);

public static class ContentHashing
{
    /// <summary>
    /// Compute SHA-256 while streaming — used by the S3 implementation
    /// so we don't have to buffer the whole file in memory just to hash.
    /// </summary>
    public static async Task<(byte[] Bytes, string Hex)> ComputeSha256Async(
        Stream stream,
        CancellationToken ct)
    {
        using var sha = SHA256.Create();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var bytes = sha.Hash ?? throw new InvalidOperationException("SHA256 hash returned null.");
        return (bytes, Convert.ToHexString(bytes).ToLowerInvariant());
    }
}
