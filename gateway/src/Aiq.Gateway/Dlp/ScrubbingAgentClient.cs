using Aiq.Gateway.Books;
using Aiq.Gateway.Configuration;
using Aiq.Gateway.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Aiq.Gateway.Dlp;

/// <summary>
/// Sprint 7 Phase B — decorator wrap IAgentClient. Chỉ intercept
/// RunAgentTaskAsync (phương thức gửi text prompt ra LLM cloud); OCR
/// methods gửi file bytes, không cần scrub input.
///
/// Flow:
///   1. Scrub(task) → maskedTask + vault
///   2. Log scrub event (count + types, không log value)
///   3. Call wrapped client với maskedTask
///   4. Unmask response → trả về PII thật cho user
///   5. Log unmask event
///
/// Vault scope: 1 call RunAgentTaskAsync = 1 vault, dispose sau khi
/// unmask xong.
/// </summary>
public sealed class ScrubbingAgentClient : IAgentClient
{
    private readonly IAgentClient _inner;
    private readonly IPiiScrubber _scrubber;
    private readonly IDlpEventRepository _events;
    private readonly IHttpContextAccessor _httpContext;
    private readonly DlpOptions _options;
    private readonly ILogger<ScrubbingAgentClient> _logger;

    public ScrubbingAgentClient(
        IAgentClient inner,
        IPiiScrubber scrubber,
        IDlpEventRepository events,
        IHttpContextAccessor httpContext,
        IOptions<DlpOptions> options,
        ILogger<ScrubbingAgentClient> logger)
    {
        _inner = inner;
        _scrubber = scrubber;
        _events = events;
        _httpContext = httpContext;
        _options = options.Value;
        _logger = logger;
    }

    // OCR methods: passthrough — gửi file bytes, không có text input để scrub.
    public Task<AgentOcrResponse> RunOcrPipelineAsync(Stream pdfStream, string filename, CancellationToken ct)
        => _inner.RunOcrPipelineAsync(pdfStream, filename, ct);

    public Task<AgentOcrResponse> RunOcrImageAsync(Stream imageStream, string filename, string contentType, CancellationToken ct)
        => _inner.RunOcrImageAsync(imageStream, filename, contentType, ct);

    public Task<AgentDocumentExtractResponse> ExtractDocumentTextAsync(Stream fileStream, string filename, string contentType, CancellationToken ct)
        => _inner.ExtractDocumentTextAsync(fileStream, filename, contentType, ct);

    public async Task<string> RunAgentTaskAsync(
        string task,
        string userToken,
        string capability,
        int maxTurns,
        CancellationToken ct)
    {
        // Feature flag OFF → passthrough. Không instrument để minimize risk.
        if (!_options.Enabled)
        {
            return await _inner.RunAgentTaskAsync(task, userToken, capability, maxTurns, ct);
        }

        // 1. Scrub input task
        var scrub = _scrubber.Scrub(task);
        using var vault = scrub.Vault;

        var tenantId = ExtractTenantId();
        var userId = ExtractUserId();
        var requestId = _httpContext.HttpContext?.TraceIdentifier;

        if (scrub.DetectedCount > 0)
        {
            await LogSafelyAsync(new DlpEvent(
                TenantId: tenantId,
                UserId: userId,
                RequestId: requestId,
                Direction: "scrub",
                PiiCount: scrub.DetectedCount,
                PiiTypes: ExtractTypes(scrub.MaskedText),
                TextLengthChars: task.Length,
                Capability: capability), ct);

            _logger.LogInformation(
                "DLP scrub · capability={Capability} count={Count} textLen={Len}",
                capability, scrub.DetectedCount, task.Length);
        }

        // 2. Call wrapped client với masked task
        var maskedResponse = await _inner.RunAgentTaskAsync(scrub.MaskedText, userToken, capability, maxTurns, ct);

        // 3. Unmask response — PII thật được restore
        var finalResponse = _scrubber.Unmask(maskedResponse ?? string.Empty, vault);

        // 4. Log unmask event (nếu có PII trong response thì count tăng theo
        //    số token found; đơn giản dùng DetectedCount của scrub vì vault
        //    đúng là từ scrub).
        if (scrub.DetectedCount > 0 && finalResponse.Length != maskedResponse?.Length)
        {
            await LogSafelyAsync(new DlpEvent(
                TenantId: tenantId,
                UserId: userId,
                RequestId: requestId,
                Direction: "unmask",
                PiiCount: scrub.DetectedCount,
                PiiTypes: ExtractTypes(scrub.MaskedText),
                TextLengthChars: finalResponse.Length,
                Capability: capability), ct);
        }

        return finalResponse;
    }

    /// <summary>
    /// Trích PII types unique từ masked text (regex token prefix). Return
    /// distinct list để lưu vào Postgres TEXT[].
    /// </summary>
    private static string[] ExtractTypes(string maskedText)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(maskedText, @"<([A-Z]+)_[a-f0-9]{10}>");
        return matches
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToArray();
    }

    private Guid ExtractTenantId()
    {
        var ctx = _httpContext.HttpContext;
        if (ctx is not null && TenantContext.TryGet(ctx, out var tid)) return tid;
        return Guid.Empty;
    }

    private Guid? ExtractUserId()
    {
        var ctx = _httpContext.HttpContext;
        if (ctx is null) return null;
        var sub = ctx.User.FindFirst("sub")?.Value
               ?? ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    /// <summary>
    /// Log DLP event — fire-and-forget semantics: lỗi insert không được
    /// phá flow chính (user vẫn phải nhận response). Chỉ log warning.
    /// </summary>
    private async Task LogSafelyAsync(DlpEvent evt, CancellationToken ct)
    {
        try
        {
            await _events.LogAsync(evt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DLP event log failed · direction={Direction}", evt.Direction);
        }
    }
}
