namespace Aiq.Gateway.Configuration;

public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    /// <summary>Service name reported to the OTLP collector. Defaults to "aiq-gateway".</summary>
    public string ServiceName { get; init; } = "aiq-gateway";

    /// <summary>OTLP/gRPC endpoint. When empty/null, tracing is registered but no exporter is wired.</summary>
    public string? OtlpEndpoint { get; init; }

    /// <summary>Deployment environment tag (resource attribute).</summary>
    public string Environment { get; init; } = "production";
}
