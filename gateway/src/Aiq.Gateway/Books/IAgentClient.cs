using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Aiq.Gateway.Books;

public interface IAgentClient
{
    Task<AgentOcrResponse> RunOcrPipelineAsync(
        Stream pdfStream,
        string filename,
        CancellationToken ct);

    Task<AgentOcrResponse> RunOcrImageAsync(
        Stream imageStream,
        string filename,
        string contentType,
        CancellationToken ct);

    /// <summary>
    /// POST /documents/extract — the Contract Upload text-extraction path.
    /// PDF (embedded text layer, falling back to the OCR pipeline for
    /// scanned pages), DOCX, or a single image. No new OCR engine — this
    /// reuses the exact same OcrPipeline as RunOcrPipelineAsync/
    /// RunOcrImageAsync for anything that still needs OCR.
    /// </summary>
    Task<AgentDocumentExtractResponse> ExtractDocumentTextAsync(
        Stream fileStream,
        string filename,
        string contentType,
        CancellationToken ct);

    /// <summary>
    /// D6: /agent/run passthrough routed through the capability resolver —
    /// returns the final_answer string. `capability` must be a stable
    /// catalog code (e.g. "voucher.processing"); the caller never picks or
    /// knows an agent id, and the resolved agent's own tool/skill grants
    /// govern execution, not any skills list this method used to accept.
    /// Used by background extraction jobs, so the uploading user's own
    /// entitlement chain (tenant + department [+ direct assignment for a
    /// plain USER]) is what authorizes the job, exactly as if they had
    /// called it interactively.
    /// </summary>
    Task<string> RunAgentTaskAsync(
        string task,
        string userToken,
        string capability,
        int maxTurns,
        CancellationToken ct);

    /// <summary>
    /// Same /agent/run call as RunAgentTaskAsync, but optionally pinned to a
    /// Gateway-resolved agent (sent as preferred_agent_id, which the Agent
    /// re-authorizes through /me/capabilities) and returning what actually
    /// executed (agent/version/provider/model) for provenance.
    /// </summary>
    async Task<Aiq.Gateway.Vouchers.DocumentProcessing.AgentTaskResult> RunAgentTaskWithProvenanceAsync(
        string task, string userToken, string capability, Guid? preferredAgentId, int maxTurns, CancellationToken ct)
        => new(await RunAgentTaskAsync(task, userToken, capability, maxTurns, ct), null, null, null, null, null);

    /// <summary>
    /// POST /agent/document-extract — tool-less, single-purpose extraction
    /// run under the agent's own manifest (published, or the current draft
    /// when <paramref name="test"/> is true). The Agent rejects with HTTP 409
    /// if the manifest it resolves is not <paramref name="expectedVersionId"/>,
    /// so the configuration the Gateway built the prompt from is exactly the
    /// version that executes.
    /// </summary>
    /// <remarks>
    /// pinVersion: ask for exactly expectedVersionId (published or superseded)
    /// instead of the agent's current published version — re-extracting a
    /// document with the version that originally processed it.
    /// </remarks>
    Task<Aiq.Gateway.Vouchers.DocumentProcessing.AgentTaskResult> RunDocumentExtractionAsync(
        Guid agentId, Guid expectedVersionId, bool test, string task, string userToken, CancellationToken ct, bool pinVersion = false)
        => throw new NotSupportedException("document extraction is not supported by this agent client");
}

public sealed class AgentClient : IAgentClient
{
    private readonly HttpClient _http;
    private readonly ILogger<AgentClient> _logger;

    public AgentClient(HttpClient http, ILogger<AgentClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Client-side throttle for the voucher / expense-claim OCR jobs: one
    /// OCR request in flight from this Gateway process. The Agent enforces
    /// its own process-wide limits for every OCR route (AG-02:
    /// aiq_agent.ocr.concurrency — PDFium serialised, bounded queue,
    /// 429 when full), so this gate is not what keeps the Agent alive; it
    /// keeps batch uploads from filling the Agent's queue and starving
    /// interactive /documents/extract and /knowledge/extract calls.
    /// </summary>
    private static readonly SemaphoreSlim OcrGate = new(1, 1);

    public async Task<AgentOcrResponse> RunOcrPipelineAsync(Stream pdfStream, string filename, CancellationToken ct)
    {
        await OcrGate.WaitAsync(ct);
        try { return await RunOcrPipelineCoreAsync(pdfStream, filename, ct); }
        finally { OcrGate.Release(); }
    }

    public async Task<AgentOcrResponse> RunOcrImageAsync(Stream imageStream, string filename, string contentType, CancellationToken ct)
    {
        await OcrGate.WaitAsync(ct);
        try { return await RunOcrImageCoreAsync(imageStream, filename, contentType, ct); }
        finally { OcrGate.Release(); }
    }

    private async Task<AgentOcrResponse> RunOcrPipelineCoreAsync(
        Stream pdfStream,
        string filename,
        CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(pdfStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", filename);

        using var response = await _http.PostAsync("/ocr/pdf", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "Agent OCR pipeline rejected request: status={Status} body={Body}",
                (int)response.StatusCode, body);
            throw new AgentRequestException(
                $"Agent returned HTTP {(int)response.StatusCode}: {body}",
                (int)response.StatusCode);
        }

        var payload = await response.Content.ReadFromJsonAsync<AgentOcrResponse>(cancellationToken: ct)
            ?? throw new AgentRequestException("Agent returned an empty body.", 502);

        return payload;
    }

    private async Task<AgentOcrResponse> RunOcrImageCoreAsync(
        Stream imageStream,
        string filename,
        string contentType,
        CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(imageStream);
        var mediaType = string.IsNullOrWhiteSpace(contentType) ? "image/jpeg" : contentType;
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        content.Add(fileContent, "file", filename);

        using var response = await _http.PostAsync("/ocr/image", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "Agent OCR image rejected: status={Status} body={Body}",
                (int)response.StatusCode, body);
            throw new AgentRequestException(
                $"Agent returned HTTP {(int)response.StatusCode}: {body}",
                (int)response.StatusCode);
        }

        var payload = await response.Content.ReadFromJsonAsync<AgentOcrResponse>(cancellationToken: ct)
            ?? throw new AgentRequestException("Agent returned an empty body.", 502);

        return payload;
    }

    public async Task<AgentDocumentExtractResponse> ExtractDocumentTextAsync(
        Stream fileStream,
        string filename,
        string contentType,
        CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(fileStream);
        var mediaType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        content.Add(fileContent, "file", filename);

        using var response = await _http.PostAsync("/documents/extract", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "Agent document extraction rejected request: status={Status} body={Body}",
                (int)response.StatusCode, body);
            throw new AgentRequestException(
                $"Agent returned HTTP {(int)response.StatusCode}: {body}",
                (int)response.StatusCode);
        }

        var payload = await response.Content.ReadFromJsonAsync<AgentDocumentExtractResponse>(cancellationToken: ct)
            ?? throw new AgentRequestException("Agent returned an empty body.", 502);

        return payload;
    }

    public async Task<string> RunAgentTaskAsync(
        string task,
        string userToken,
        string capability,
        int maxTurns,
        CancellationToken ct)
    {
        var payload = new
        {
            task,
            capability,
            max_turns = maxTurns,
        };
        // D6 bug fix: this call previously sent the caller's token as a
        // `user_token` JSON field, which AgentRunRequest has never declared
        // (and its `extra="forbid"` Pydantic config rejects) — and never
        // set an Authorization header at all, so `require_principal`
        // (agent/src/aiq_agent/api/security.py) had nothing to read and
        // this call has always failed authentication. The Agent service
        // authenticates via the Authorization header only, never a body
        // field — same as every other Gateway->Agent call in this class.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/agent/run")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "Agent runTask rejected: status={Status} body={Body}",
                (int)response.StatusCode, body);
            throw new AgentRequestException(
                $"Agent /agent/run HTTP {(int)response.StatusCode}: {body}",
                (int)response.StatusCode);
        }
        var doc = await response.Content.ReadFromJsonAsync<AgentRunResult>(cancellationToken: ct)
            ?? throw new AgentRequestException("Agent returned empty run body.", 502);
        return doc.FinalAnswer ?? string.Empty;
    }

    public async Task<Aiq.Gateway.Vouchers.DocumentProcessing.AgentTaskResult> RunAgentTaskWithProvenanceAsync(
        string task, string userToken, string capability, Guid? preferredAgentId, int maxTurns, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?> { ["task"] = task, ["capability"] = capability, ["max_turns"] = maxTurns };
        if (preferredAgentId is { } preferred) payload["preferred_agent_id"] = preferred.ToString();
        var doc = await PostAsync<AgentRunResult>("/agent/run", payload, userToken, "runTask", ct);
        return doc.ToTaskResult();
    }

    public async Task<Aiq.Gateway.Vouchers.DocumentProcessing.AgentTaskResult> RunDocumentExtractionAsync(
        Guid agentId, Guid expectedVersionId, bool test, string task, string userToken, CancellationToken ct, bool pinVersion = false)
    {
        var payload = new Dictionary<string, object?>
        {
            ["agent_id"] = agentId.ToString(),
            ["expected_version_id"] = expectedVersionId.ToString(),
            ["test"] = test,
            ["task"] = task,
        };
        if (pinVersion) payload["pin_version"] = true;
        var doc = await PostAsync<AgentRunResult>("/agent/document-extract", payload, userToken, "documentExtract", ct);
        return doc.ToTaskResult();
    }

    private async Task<T> PostAsync<T>(string path, object payload, string userToken, string label, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(payload) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Agent {Label} rejected: status={Status} body={Body}", label, (int)response.StatusCode, body);
            throw new AgentRequestException($"Agent {path} HTTP {(int)response.StatusCode}: {body}", (int)response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new AgentRequestException($"Agent returned empty {path} body.", 502);
    }
}

public sealed record AgentRunResult(
    [property: JsonPropertyName("final_answer")] string? FinalAnswer,
    [property: JsonPropertyName("agent_id")] Guid? AgentId = null,
    [property: JsonPropertyName("agent_version_id")] Guid? AgentVersionId = null,
    [property: JsonPropertyName("agent_version")] int? AgentVersion = null,
    [property: JsonPropertyName("provider")] string? Provider = null,
    [property: JsonPropertyName("model")] string? Model = null)
{
    public Aiq.Gateway.Vouchers.DocumentProcessing.AgentTaskResult ToTaskResult() =>
        new(FinalAnswer ?? string.Empty, AgentId, AgentVersionId, AgentVersion, Provider, Model);
}

public sealed class AgentRequestException : Exception
{
    public AgentRequestException(string message, int upstreamStatus) : base(message)
    {
        UpstreamStatus = upstreamStatus;
    }

    public int UpstreamStatus { get; }
}

/// <summary>
/// Shared by every endpoint that calls IAgentClient directly from an HTTP
/// handler (ContractEndpoints.ExtractText, AccountingEndpoints.
/// ExtractInvoiceText) — turns an agent call failure into a structured
/// JSON error instead of an unhandled-exception 500.
///
/// Root cause this fixes: both call sites previously caught only
/// AgentRequestException (the agent responding with an HTTP error status).
/// When the agent service itself is unreachable — down, still starting,
/// or a transient network blip — HttpClient throws HttpRequestException
/// instead, which was never caught and surfaced as a raw ASP.NET
/// exception-page 500 (confirmed in gateway logs: "System.Net.Http.
/// HttpRequestException: Resource temporarily unavailable (agent:8000)"
/// from AccountingEndpoints.ExtractInvoiceText). Never creates or mutates
/// any domain state itself — purely a result-mapping helper.
/// </summary>
public static class AgentCallResult
{
    public static async Task<IResult> RunAsync<T>(
        Func<Task<T>> call,
        Func<T, IResult> onSuccess,
        CancellationToken ct)
    {
        try
        {
            return onSuccess(await call());
        }
        catch (AgentRequestException exc)
        {
            var status = exc.UpstreamStatus is >= 400 and < 500 ? exc.UpstreamStatus : StatusCodes.Status502BadGateway;
            return Results.Json(new { error = "extraction_failed", detail = exc.Message }, statusCode: status);
        }
        catch (HttpRequestException)
        {
            // Agent unreachable (connection refused/reset, DNS failure,
            // still starting up). Never leak the raw socket exception to
            // the browser — a clear, retryable, structured error instead.
            return Results.Json(
                new { error = "agent_unavailable", detail = "Dịch vụ xử lý tài liệu tạm thời không khả dụng. Vui lòng thử lại sau ít phút." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The HttpClient's own request timeout fired — distinct from
            // the caller's ct being cancelled (e.g. browser navigated
            // away), which should propagate normally.
            return Results.Json(
                new { error = "agent_timeout", detail = "Dịch vụ xử lý tài liệu phản hồi quá chậm. Vui lòng thử lại." },
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }
}

// Mirrors the agent's PipelineResponse shape (api/ocr_routes.py).
// Python returns snake_case field names — annotate PascalCase params
// so System.Text.Json binds them (default policy is camelCase, which
// left JoinedText / AverageConfidence null / 0 silently).
public sealed record AgentOcrResponse(
    [property: JsonPropertyName("page_count")] int PageCount,
    [property: JsonPropertyName("average_confidence")] double AverageConfidence,
    [property: JsonPropertyName("stage_durations_ms")] Dictionary<string, int> StageDurationsMs,
    [property: JsonPropertyName("pages")] IReadOnlyList<AgentPageReport> Pages,
    [property: JsonPropertyName("joined_text")] string JoinedText,
    // OCR-correction LLM usage (numbers only); null when no LLM call was made
    // or the Agent predates usage reporting.
    [property: JsonPropertyName("llm_usage")] Aiq.Gateway.Governance.LlmUsageReport? LlmUsage = null);

// Mirrors the agent's DocumentExtractResponse shape (api/document_routes.py).
public sealed record AgentDocumentExtractResponse(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("char_count")] int CharCount,
    [property: JsonPropertyName("mime_type")] string MimeType,
    [property: JsonPropertyName("extraction_method")] string ExtractionMethod,
    [property: JsonPropertyName("llm_usage")] Aiq.Gateway.Governance.LlmUsageReport? LlmUsage = null);

public sealed record AgentPageReport(
    [property: JsonPropertyName("page_number")] int PageNumber,
    [property: JsonPropertyName("raw_text")] string RawText,
    [property: JsonPropertyName("corrected_text")] string CorrectedText,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("similarity")] double? Similarity,
    [property: JsonPropertyName("notes")] string? Notes);
