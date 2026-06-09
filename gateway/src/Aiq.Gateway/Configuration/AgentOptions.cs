using System.ComponentModel.DataAnnotations;

namespace Aiq.Gateway.Configuration;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    [Required, Url]
    public string BaseUrl { get; init; } = "http://agent:8000";

    [Range(1, 1800)]
    public int TimeoutSeconds { get; init; } = 300;
}
