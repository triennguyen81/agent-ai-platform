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
    /// Generic /agent/run passthrough — task text + skill list, returns
    /// the final_answer string. Used by background extraction jobs.
    /// </summary>
    Task<string> RunAgentTaskAsync(
        string task,
        string userToken,
        string[] skills,
        int maxTurns,
        CancellationToken ct);
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

    public async Task<AgentOcrResponse> RunOcrPipelineAsync(
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

    public async Task<AgentOcrResponse> RunOcrImageAsync(
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

    public async Task<string> RunAgentTaskAsync(
        string task,
        string userToken,
        string[] skills,
        int maxTurns,
        CancellationToken ct)
    {
        var payload = new
        {
            task,
            user_token = userToken,
            skills,
            max_turns = maxTurns,
        };
        using var response = await _http.PostAsJsonAsync("/agent/run", payload, ct);
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
}

public sealed record AgentRunResult(
    [property: JsonPropertyName("final_answer")] string? FinalAnswer);

public sealed class AgentRequestException : Exception
{
    public AgentRequestException(string message, int upstreamStatus) : base(message)
    {
        UpstreamStatus = upstreamStatus;
    }

    public int UpstreamStatus { get; }
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
    [property: JsonPropertyName("joined_text")] string JoinedText);

public sealed record AgentPageReport(
    [property: JsonPropertyName("page_number")] int PageNumber,
    [property: JsonPropertyName("raw_text")] string RawText,
    [property: JsonPropertyName("corrected_text")] string CorrectedText,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("similarity")] double? Similarity,
    [property: JsonPropertyName("notes")] string? Notes);
