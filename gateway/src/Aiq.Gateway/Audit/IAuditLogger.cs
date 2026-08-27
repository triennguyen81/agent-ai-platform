using System.Net;
using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Audit;

public interface IAuditLogger
{
    Task WriteAsync(AuditEntry entry, CancellationToken ct);
}

public sealed record AuditEntry(
    Guid? ActorId,
    string Action,
    string ResourceType,
    string? ResourceId,
    object? Payload,
    IPAddress? IpAddress,
    string? UserAgent);

public sealed class PostgresAuditLogger : IAuditLogger
{
    private readonly NpgsqlConnectionFactory _factory;
    private readonly ILogger<PostgresAuditLogger> _logger;

    public PostgresAuditLogger(NpgsqlConnectionFactory factory, ILogger<PostgresAuditLogger> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task WriteAsync(AuditEntry entry, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO audit_log
                (actor_id, action, resource_type, resource_id, payload, ip_address, user_agent)
            VALUES
                (@actorId, @action, @resourceType, @resourceId, @payload::jsonb, @ip::inet, @userAgent);
            """;

        try
        {
            await using var conn = await _factory.OpenAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                sql,
                new
                {
                    actorId = entry.ActorId,
                    action = entry.Action,
                    resourceType = entry.ResourceType,
                    resourceId = entry.ResourceId,
                    payload = entry.Payload is null
                        ? null
                        : System.Text.Json.JsonSerializer.Serialize(entry.Payload),
                    ip = entry.IpAddress?.ToString(),
                    userAgent = entry.UserAgent,
                },
                cancellationToken: ct));
        }
        catch (Exception ex)
        {
            // Audit failures must NEVER fault the request path. Log loudly instead.
            _logger.LogError(ex,
                "Audit write failed for action {Action} on {ResourceType}/{ResourceId}",
                entry.Action, entry.ResourceType, entry.ResourceId);
        }
    }
}
