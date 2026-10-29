using Aiq.Gateway.Books;

namespace Aiq.Gateway.Vouchers.DocumentProcessing;

/// <summary>
/// Maps a provider/runtime failure to a SAFE, stable code. Only the code is
/// persisted or shown; upstream exception text and provider messages stay in
/// server logs.
/// </summary>
public static class ExtractionFailures
{
    public const string ProviderUnavailable = "provider_unavailable";
    public const string ProviderRateLimited = "provider_rate_limited";
    public const string ProviderSessionLimit = "provider_session_limit";
    public const string ProviderTimeout = "provider_timeout";
    public const string AgentExecutionFailed = "agent_execution_failed";
    public const string OutputInvalid = "extraction_output_invalid";
    /// <summary>Chứng từ flow v2 PR4: the extracted accounting date falls in a closed period (stage "save").</summary>
    public const string PeriodLocked = "period_locked";

    public static string Classify(Exception ex) => ex switch
    {
        AgentRequestException a when a.UpstreamStatus == 429 => ProviderRateLimited,
        AgentRequestException a when a.UpstreamStatus is 408 or 504 => ProviderTimeout,
        AgentRequestException a when FromText(a.Message) is { } code => code,
        AgentRequestException a when a.UpstreamStatus is 502 or 503 => ProviderUnavailable,
        AgentRequestException => AgentExecutionFailed,
        TimeoutException or TaskCanceledException => ProviderTimeout,
        HttpRequestException => ProviderUnavailable,
        _ => AgentExecutionFailed,
    };

    /// <summary>A 200 answer without JSON: a provider limit message, or plainly unusable output.</summary>
    public static string ClassifyAnswer(string? answer) => FromText(answer) ?? OutputInvalid;

    private static string? FromText(string? text)
    {
        var t = (text ?? string.Empty).ToLowerInvariant();
        if (t.Contains("session limit") || t.Contains("usage limit") || t.Contains("quota")) return ProviderSessionLimit;
        if (t.Contains("rate limit") || t.Contains("rate_limit") || t.Contains("too many requests")) return ProviderRateLimited;
        if (t.Contains("timed out") || t.Contains("timeout")) return ProviderTimeout;
        return null;
    }
}
