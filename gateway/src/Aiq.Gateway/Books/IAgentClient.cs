using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Aiq.Gateway.Books;

public interface IAgentClient
{
    Task<AgentOcrResponse> RunOcrPipelineAsync(
        Stream pdfStream,
        string filename,
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
}

public sealed class AgentRequestException : Exception
{
    public AgentRequestException(string message, int upstreamStatus) : base(message)
    {
        UpstreamStatus = upstreamStatus;
    }

    public int UpstreamStatus { get; }
}

// Mirrors the agent's PipelineResponse shape (api/ocr_routes.py).
public sealed record AgentOcrResponse(
    int PageCount,
    double AverageConfidence,
    Dictionary<string, int> StageDurationsMs,
    IReadOnlyList<AgentPageReport> Pages,
    string JoinedText);

public sealed record AgentPageReport(
    int PageNumber,
    string RawText,
    string CorrectedText,
    double Confidence,
    double? Similarity,
    string? Notes);
