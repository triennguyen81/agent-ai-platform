using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Aiq.Gateway.Storage;

/// <summary>
/// IObjectStorage implementation backed by the AWS S3 SDK. Talks to any
/// S3-compatible endpoint — set S3Options.ServiceUrl to point at MinIO
/// (http://aiq-minio:9000 on cosmos), AWS S3, Cloudflare R2, etc.
///
/// For MinIO ForcePathStyle must be true (bucket-in-path URL scheme).
/// AWS S3 defaults to virtual-hosted-style which won't work against
/// MinIO's single-DNS setup.
/// </summary>
public sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    private readonly IAmazonS3 _client;
    private readonly S3Options _options;
    private readonly ILogger<S3ObjectStorage> _logger;

    public S3ObjectStorage(IOptions<S3Options> options, ILogger<S3ObjectStorage> logger)
    {
        _options = options.Value;
        _logger = logger;

        var config = new AmazonS3Config
        {
            ServiceURL = _options.ServiceUrl,
            ForcePathStyle = _options.ForcePathStyle,
            // MinIO doesn't need SigV4 chunk streaming — this avoids a
            // handshake that MinIO older versions get wrong.
            UseHttp = _options.ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
        };

        _client = new AmazonS3Client(_options.AccessKey, _options.SecretKey, config);
    }

    public async Task<PutObjectResult> PutAsync(
        string bucket,
        string key,
        Stream content,
        string? contentType,
        CancellationToken ct)
    {
        // We need to know the SHA-256 for audit, but we can't hash and
        // upload in the same pass without buffering the whole file (S3
        // needs Content-Length). Stage to a temp file, hash it, then
        // upload — bounded memory even for large uploads.
        var tempPath = Path.Combine(Path.GetTempPath(), $"s3-put-{Guid.NewGuid():N}");
        try
        {
            long sizeBytes;
            string sha256Hex;

            await using (var tempStream = File.Create(tempPath))
            {
                await content.CopyToAsync(tempStream, ct);
                sizeBytes = tempStream.Length;
            }

            await using (var readForHash = File.OpenRead(tempPath))
            {
                var (_, hex) = await ContentHashing.ComputeSha256Async(readForHash, ct);
                sha256Hex = hex;
            }

            await using var uploadStream = File.OpenRead(tempPath);
            var request = new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                InputStream = uploadStream,
                ContentType = contentType,
            };

            var response = await _client.PutObjectAsync(request, ct);

            _logger.LogInformation(
                "Uploaded {SizeBytes} bytes to s3://{Bucket}/{Key} (sha256={Sha256})",
                sizeBytes, bucket, key, sha256Hex);

            return new PutObjectResult(bucket, key, sizeBytes, sha256Hex, response.ETag);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public Task<string> GetSignedUrlAsync(
        string bucket,
        string key,
        TimeSpan? expiry,
        CancellationToken ct)
    {
        var url = _client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(expiry ?? TimeSpan.FromMinutes(5)),
            Protocol = _options.ServiceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? Protocol.HTTPS
                : Protocol.HTTP,
        });

        // For the browser to actually reach MinIO the URL needs to be
        // accessible from outside the Docker network. On cosmos the
        // internal ServiceUrl (http://aiq-minio:9000) won't work from a
        // browser — the caller must reverse-proxy via NPM/Caddy. See
        // docs/arch/s3-migration-plan.md for the reverse-proxy setup.
        if (!string.IsNullOrEmpty(_options.PublicUrlPrefix))
        {
            url = url.Replace(
                _options.ServiceUrl.TrimEnd('/'),
                _options.PublicUrlPrefix.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);
        }

        return Task.FromResult(url);
    }

    public async Task DeleteAsync(string bucket, string key, CancellationToken ct)
    {
        try
        {
            await _client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucket,
                Key = key,
            }, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Idempotent — missing key is not an error.
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        try
        {
            // Cheapest S3 op that verifies auth + network.
            await _client.ListBucketsAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Object storage health check failed");
            return false;
        }
    }

    public void Dispose() => _client.Dispose();

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clean up staging file {Path}", path);
        }
    }
}

/// <summary>
/// Config binding for the S3-compatible backend. Populate from env vars
/// via Object__ServiceUrl, Object__AccessKey, etc. — see appsettings.json
/// and docker-compose.yml.
/// </summary>
public sealed class S3Options
{
    public const string SectionName = "Object";

    /// <summary>Endpoint URL, e.g. http://aiq-minio:9000 on cosmos.</summary>
    public required string ServiceUrl { get; init; }

    /// <summary>MinIO / IAM access key.</summary>
    public required string AccessKey { get; init; }

    /// <summary>MinIO / IAM secret.</summary>
    public required string SecretKey { get; init; }

    /// <summary>True for MinIO; false for AWS S3 (virtual-hosted).</summary>
    public bool ForcePathStyle { get; init; } = true;

    /// <summary>Bucket for voucher images/PDFs.</summary>
    public string VoucherBucket { get; init; } = "voucher-uploads";

    /// <summary>Bucket for contract PDFs.</summary>
    public string ContractBucket { get; init; } = "contract-uploads";

    /// <summary>
    /// When set, GetSignedUrlAsync rewrites the internal ServiceUrl to
    /// this public prefix so the browser can reach it. Leave empty if
    /// the API is directly reachable.
    /// </summary>
    public string PublicUrlPrefix { get; init; } = "";
}
