using Aiq.Gateway.Books;
using Aiq.Gateway.Configuration;
using Aiq.Gateway.Storage;
using Aiq.Gateway.Vouchers;
using Hangfire;
using Microsoft.Extensions.Options;

namespace Aiq.Gateway.Jobs;

// UC-ACC-001 P1: OCR runs OUT of the request path.
// Web/Cloudflare see a fast response; heavy PaddleOCR + Claude cold-starts
// happen in the background. Detail page polls status until ocr_complete.
public sealed class VoucherOcrJobRunner
{
    private readonly IAgentClient _agent;
    private readonly IVoucherRepository _vouchers;
    private readonly IObjectStorage _storage;
    private readonly S3Options _s3;
    private readonly ILogger<VoucherOcrJobRunner> _logger;

    public VoucherOcrJobRunner(
        IAgentClient agent,
        IVoucherRepository vouchers,
        IObjectStorage storage,
        IOptions<S3Options> s3Options,
        ILogger<VoucherOcrJobRunner> logger)
    {
        _agent = agent;
        _vouchers = vouchers;
        _storage = storage;
        _s3 = s3Options.Value;
        _logger = logger;
    }

    [JobDisplayName("Voucher OCR · {0}")]
    [AutomaticRetry(Attempts = 2, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task RunAsync(
        Guid voucherId,
        string s3Key,
        string originalFilename,
        string contentType,
        bool isImage,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Voucher OCR job start · id={VoucherId} key={S3Key} isImage={IsImage}",
            voucherId, s3Key, isImage);

        // Mark as processing so the UI shows the correct state during OCR.
        await _vouchers.UpdateOcrResultAsync(
            voucherId,
            status: "processing",
            rawText: null,
            ocrConfidence: null,
            errorMessage: null,
            ct);

        try
        {
            // Pull the file back from MinIO into memory. Vouchers are ≤ 25 MB
            // (enforced at upload time) so this is bounded.
            byte[] bytes;
            await using (var s3Stream = await _storage.GetAsync(_s3.VoucherBucket, s3Key, ct))
            {
                await using var buffer = new MemoryStream();
                await s3Stream.CopyToAsync(buffer, ct);
                bytes = buffer.ToArray();
            }

            await using var ocrStream = new MemoryStream(bytes, writable: false);
            var ocr = isImage
                ? await _agent.RunOcrImageAsync(ocrStream, originalFilename, contentType, ct)
                : await _agent.RunOcrPipelineAsync(ocrStream, originalFilename, ct);

            var joined = ocr.JoinedText ?? string.Empty;
            var confidence = (decimal)ocr.AverageConfidence;

            await _vouchers.UpdateOcrResultAsync(
                voucherId,
                status: "ocr_complete",
                rawText: joined,
                ocrConfidence: confidence,
                errorMessage: null,
                ct);

            _logger.LogInformation(
                "Voucher OCR job done · id={VoucherId} textLen={Len} confidence={Conf:F2}",
                voucherId, joined.Length, confidence);
        }
        catch (AgentRequestException ex)
        {
            // Match the sync path's degradation: file stays in S3, stub row
            // remains, user can paste text manually via VoucherExtractForm.
            var msg = ex.UpstreamStatus == 503
                ? "OCR pipeline chưa khả dụng. Paste raw text tay để extract."
                : $"OCR runtime lỗi ({ex.UpstreamStatus}). Paste raw text tay để extract.";
            _logger.LogWarning(ex,
                "Voucher OCR job soft-failed · id={VoucherId} upstream={Upstream}",
                voucherId, ex.UpstreamStatus);
            await _vouchers.UpdateOcrResultAsync(
                voucherId,
                status: "ocr_unavailable",
                rawText: null,
                ocrConfidence: null,
                errorMessage: msg,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Voucher OCR job hard-failed · id={VoucherId}", voucherId);
            await _vouchers.UpdateOcrResultAsync(
                voucherId,
                status: "ocr_unavailable",
                rawText: null,
                ocrConfidence: null,
                errorMessage: $"OCR job crash: {ex.Message}",
                ct);
            throw;   // let Hangfire retry policy handle
        }
    }
}

public interface IVoucherOcrJobScheduler
{
    string Enqueue(Guid voucherId, string s3Key, string originalFilename, string contentType, bool isImage);
}

public sealed class VoucherOcrJobScheduler : IVoucherOcrJobScheduler
{
    private readonly IBackgroundJobClient _hangfire;
    private readonly ILogger<VoucherOcrJobScheduler> _logger;

    public VoucherOcrJobScheduler(IBackgroundJobClient hangfire, ILogger<VoucherOcrJobScheduler> logger)
    {
        _hangfire = hangfire;
        _logger = logger;
    }

    public string Enqueue(Guid voucherId, string s3Key, string originalFilename, string contentType, bool isImage)
    {
        // File is already in S3 — no staging step needed (unlike books flow).
        var jobId = _hangfire.Enqueue<VoucherOcrJobRunner>(
            runner => runner.RunAsync(voucherId, s3Key, originalFilename, contentType, isImage, CancellationToken.None));
        _logger.LogInformation(
            "Enqueued voucher OCR job {JobId} · voucher={VoucherId} s3Key={S3Key}",
            jobId, voucherId, s3Key);
        return jobId;
    }
}
