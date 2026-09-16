using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aiq.Gateway.Audit;

/// <summary>
/// Records one audit_log row per authenticated state-changing request.
/// Runs AFTER the rest of the pipeline so we capture the final status code,
/// and never blocks the response — the row is written fire-and-forget.
///
/// P2 audit fix (ACCAGENT_PROCESS_AUDIT_REPORT.md — control #8 "Lưu vết mọi
/// hiệu chỉnh" only half-satisfied: who/when was logged, never what) — the
/// request body is now captured too, for state-changing JSON requests only
/// and up to a small size cap, with known-sensitive field names redacted
/// before it ever reaches the audit_log table.
/// </summary>
public sealed class AuditMiddleware
{
    private static readonly HashSet<string> AuditedMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "POST", "PUT", "PATCH", "DELETE",
    };

    private static readonly string[] SkippedPathPrefixes =
    {
        "/healthz/",
        "/jobs/dashboard",
        "/openapi",
    };

    private static readonly HashSet<string> RedactedFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "newPassword", "currentPassword", "secret", "token", "apiKey", "signingKey",
    };

    private const int MaxBodyCaptureBytes = 8 * 1024;

    private readonly RequestDelegate _next;
    private readonly ILogger<AuditMiddleware> _logger;

    public AuditMiddleware(RequestDelegate next, ILogger<AuditMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IAuditLogger audit)
    {
        JsonNode? requestBody = null;
        if (ShouldAudit(context)
            && (context.Request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) ?? false)
            && context.Request.ContentLength is > 0 and <= MaxBodyCaptureBytes)
        {
            requestBody = await TryReadAndResetBodyAsync(context);
        }

        await _next(context);

        if (!ShouldAudit(context))
        {
            return;
        }

        var actorId = TryGetUserId(context);
        var entry = new AuditEntry(
            ActorId: actorId,
            Action: $"{context.Request.Method} {context.Request.Path}",
            ResourceType: ResourceTypeFromPath(context.Request.Path),
            ResourceId: context.GetRouteValue("id")?.ToString(),
            Payload: new
            {
                statusCode = context.Response.StatusCode,
                query = context.Request.QueryString.HasValue
                    ? context.Request.QueryString.Value
                    : null,
                requestBody,
            },
            IpAddress: context.Connection.RemoteIpAddress,
            UserAgent: context.Request.Headers.UserAgent.ToString());

        _ = WriteAuditFireAndForget(audit, entry, context.RequestAborted);
    }

    private async Task<JsonNode?> TryReadAndResetBodyAsync(HttpContext context)
    {
        try
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            var raw = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;

            if (string.IsNullOrWhiteSpace(raw)) return null;
            var node = JsonNode.Parse(raw);
            if (node is not null) Redact(node);
            return node;
        }
        catch (Exception ex)
        {
            // Malformed/oversized body must never break the actual request.
            _logger.LogDebug(ex, "Audit: could not capture request body");
            return null;
        }
    }

    private static void Redact(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(kv => kv.Key).ToList())
            {
                if (RedactedFieldNames.Contains(key))
                {
                    obj[key] = "***redacted***";
                }
                else if (obj[key] is JsonNode child)
                {
                    Redact(child);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is not null) Redact(item);
            }
        }
    }

    private async Task WriteAuditFireAndForget(
        IAuditLogger audit,
        AuditEntry entry,
        CancellationToken requestCt)
    {
        // Detach from the request CT so cancellation of the response does not
        // cancel the audit insert. Cap with a short independent timeout instead.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await audit.WriteAsync(entry, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background audit write threw despite IAuditLogger swallow");
        }
    }

    private static bool ShouldAudit(HttpContext context)
    {
        if (!AuditedMethods.Contains(context.Request.Method))
        {
            return false;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        foreach (var prefix in SkippedPathPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static Guid? TryGetUserId(HttpContext context)
    {
        var raw = context.User.FindFirst("sub")?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private static string ResourceTypeFromPath(PathString path)
    {
        var raw = path.Value ?? string.Empty;
        var segments = raw.Trim('/').Split('/', 2);
        return segments.Length > 0 && segments[0].Length > 0 ? segments[0] : "root";
    }
}
