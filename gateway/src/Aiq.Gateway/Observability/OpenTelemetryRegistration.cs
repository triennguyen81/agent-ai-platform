using Aiq.Gateway.Configuration;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Aiq.Gateway.Observability;

public static class OpenTelemetryRegistration
{
    /// <summary>
    /// Wires OpenTelemetry tracing. ASP.NET Core requests, outbound HttpClient calls,
    /// and Npgsql commands are auto-instrumented. The OTLP exporter is only attached
    /// when ObservabilityOptions.OtlpEndpoint is set so dev runs do not require a
    /// collector.
    /// </summary>
    public static IServiceCollection AddAiqOpenTelemetry(
        this IServiceCollection services,
        ObservabilityOptions options)
    {
        services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName: options.ServiceName, serviceVersion: "0.1.0")
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment"] = options.Environment,
                }))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(o =>
                    {
                        o.RecordException = true;
                        o.Filter = httpContext =>
                            !httpContext.Request.Path.StartsWithSegments("/healthz")
                            && !httpContext.Request.Path.StartsWithSegments("/metrics")
                            && !httpContext.Request.Path.StartsWithSegments("/jobs/dashboard");
                    })
                    .AddHttpClientInstrumentation(o =>
                    {
                        o.RecordException = true;
                    })
                    // Npgsql.OpenTelemetry registers an ActivitySource named "Npgsql"
                    // that we opt into by source name.
                    .AddSource("Npgsql");

                if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint))
                {
                    tracing.AddOtlpExporter(otlp =>
                    {
                        otlp.Endpoint = new Uri(options.OtlpEndpoint);
                    });
                }
            });

        return services;
    }
}
