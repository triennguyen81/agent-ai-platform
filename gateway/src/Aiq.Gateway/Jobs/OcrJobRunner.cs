using Aiq.Gateway.Books;
using Hangfire;

namespace Aiq.Gateway.Jobs;

/// <summary>
/// Hangfire job worker. The Hangfire server resolves this from DI and calls
/// <see cref="RunAsync"/> on a background thread. The job is idempotent
/// because BookRepository.UpsertPagesAsync uses ON CONFLICT DO UPDATE.
/// </summary>
public sealed class OcrJobRunner
{
    private readonly IAgentClient _agent;
    private readonly IBookRepository _books;
    private readonly ILogger<OcrJobRunner> _logger;

    public OcrJobRunner(
        IAgentClient agent,
        IBookRepository books,
        ILogger<OcrJobRunner> logger)
    {
        _agent = agent;
        _books = books;
        _logger = logger;
    }

    [JobDisplayName("OCR pipeline · book {0}")]
    [AutomaticRetry(Attempts = 2, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task RunAsync(
        Guid tenantId, Guid bookId,
        string stagedFilePath,
        string originalFilename,
        CancellationToken ct)
    {
        if (!File.Exists(stagedFilePath))
        {
            _logger.LogError(
                "OCR job for book {BookId} cannot start — staged file {Path} is missing",
                bookId, stagedFilePath);
            await _books.UpdateStatusAndPageCountAsync(tenantId, bookId, "failed", 0, ct);
            return;
        }

        try
        {
            await _books.UpdateStatusAndPageCountAsync(tenantId, bookId, "processing", 0, ct);

            await using var pdfStream = File.OpenRead(stagedFilePath);
            var ocr = await _agent.RunOcrPipelineAsync(pdfStream, originalFilename, ct);

            var pages = ocr.Pages
                .Select(p => new UpsertPageInput(
                    p.PageNumber,
                    p.RawText,
                    p.CorrectedText,
                    (float)p.Confidence))
                .ToArray();

            await _books.UpsertPagesAsync(tenantId, bookId, pages, ct);
            await _books.UpdateStatusAndPageCountAsync(tenantId, bookId, "processed", ocr.PageCount, ct);

            _logger.LogInformation(
                "OCR job complete for book {BookId} — {PageCount} pages, avg confidence {Confidence:F2}",
                bookId, ocr.PageCount, ocr.AverageConfidence);
        }
        catch (AgentRequestException ex)
        {
            _logger.LogWarning(ex,
                "OCR job for book {BookId} failed (upstream {Status})", bookId, ex.UpstreamStatus);
            await _books.UpdateStatusAndPageCountAsync(tenantId, bookId, "failed", 0, ct);
            throw;
        }
        finally
        {
            TryDelete(stagedFilePath);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete staged upload {Path}", path);
        }
    }
}
