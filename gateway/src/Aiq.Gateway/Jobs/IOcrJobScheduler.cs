using Aiq.Gateway.Configuration;
using Hangfire;
using Microsoft.Extensions.Options;

namespace Aiq.Gateway.Jobs;

public interface IOcrJobScheduler
{
    /// <summary>Stage a PDF on disk and enqueue an OCR job. Returns the Hangfire job id.</summary>
    Task<string> EnqueueAsync(
        Guid tenantId, Guid bookId,
        Stream pdfContent,
        string originalFilename,
        CancellationToken ct);
}

public sealed class OcrJobScheduler : IOcrJobScheduler
{
    private readonly IBackgroundJobClient _hangfire;
    private readonly JobsOptions _options;
    private readonly ILogger<OcrJobScheduler> _logger;

    public OcrJobScheduler(
        IBackgroundJobClient hangfire,
        IOptions<JobsOptions> options,
        ILogger<OcrJobScheduler> logger)
    {
        _hangfire = hangfire;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> EnqueueAsync(
        Guid tenantId, Guid bookId,
        Stream pdfContent,
        string originalFilename,
        CancellationToken ct)
    {
        Directory.CreateDirectory(_options.UploadStagingDirectory);

        // Use a fresh GUID so the on-disk filename never collides and never leaks
        // anything from the original upload's filename.
        var stagedPath = Path.Combine(
            Path.Combine(_options.UploadStagingDirectory, $"tenant-{tenantId:N}"),
            $"book-{bookId:N}-{Guid.NewGuid():N}.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);

        await using (var dest = File.Create(stagedPath))
        {
            await pdfContent.CopyToAsync(dest, ct);
        }

        var jobId = _hangfire.Enqueue<OcrJobRunner>(
            runner => runner.RunAsync(tenantId, bookId, stagedPath, originalFilename, CancellationToken.None));

        _logger.LogInformation(
            "Enqueued OCR job {JobId} for tenant {TenantId}, book {BookId}",
            jobId, tenantId, bookId);

        return jobId;
    }
}
