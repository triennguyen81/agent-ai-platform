using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;

namespace Aiq.Gateway.Endpoints;

public static class AgentUsageEndpoints
{
    public static IEndpointRouteBuilder MapAgentUsageEndpoints(this IEndpointRouteBuilder app)
    {
        // Same trust model as POST /runtime/agents/{agentId}/hitl/evaluate
        // (HitlEndpoints.cs): called by the Python runtime using the
        // short-lived tool_execution token minted alongside the manifest,
        // never a real user session, never exposed to the browser.
        app.MapPost("/runtime/agents/{agentId:guid}/usage", RecordAsync)
            .RequireAuthorization("resource_or_tool").RequireRateLimiting("default");
        // LLM calls that run without an agent manifest (plain / PEC chat, the
        // legacy agentic runtime). Called by the Python runtime with the
        // caller's own token; scope is derived from that token, and only the
        // event types below are accepted on this route.
        app.MapPost("/runtime/usage", RecordUnscopedAsync)
            .RequireAuthorization("resource_or_tool").RequireRateLimiting("default");
        return app;
    }

    public sealed record UsageRequest(
        [property: Required, StringLength(40, MinimumLength = 1)] string EventType,
        Guid? AgentVersionId,
        Guid? TraceId,
        [property: StringLength(40)] string? Provider,
        [property: StringLength(200)] string? Model,
        [property: Range(0, int.MaxValue)] int? InputTokens,
        [property: Range(0, int.MaxValue)] int? OutputTokens,
        [property: Range(typeof(decimal), "0", "79228162514264337593543950335")] decimal? CostAmount,
        [property: Range(0, int.MaxValue)] int? TotalLatencyMs,
        [property: Range(0, int.MaxValue)] int? LlmLatencyMs,
        [property: Range(0, int.MaxValue)] int? ToolLatencyMs,
        [property: Range(0, int.MaxValue)] int? RagLatencyMs,
        [property: Range(0, int.MaxValue)] int? LlmCallCount,
        [property: Range(0, long.MaxValue)] long? CacheCreationInputTokens = null,
        [property: Range(0, long.MaxValue)] long? CacheReadInputTokens = null,
        [property: Range(0, int.MaxValue)] int? NumTurns = null,
        Guid? RequestId = null,
        [property: StringLength(60)] string? CostSource = null,
        Dictionary<string, long>? Metrics = null);

    /// <summary>Event types accepted on the agent-less route.</summary>
    public static readonly IReadOnlySet<string> UnscopedEventTypes =
        new HashSet<string>(StringComparer.Ordinal) { "plain_chat", "pec_chat", "legacy_agent_run" };

    private static async Task<IResult> RecordAsync(
        Guid agentId, UsageRequest request, IAgentUsageRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!AgentUsageEvents.ValidMetrics(request.Metrics))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["metrics"] = ["numeric snake_case metrics only"] });
        // tenant_id / department_id / actor_id are ALWAYS derived server-side
        // from the authenticated principal — never from the request body —
        // exactly like every other resource_or_tool-gated endpoint in this
        // codebase (HitlEndpoints.EvaluateAsync is the direct precedent).
        if (!AuthenticatedPrincipalContext.TryGet(ctx, out var principal) || principal.TenantId is not { } tenantId)
            return Results.BadRequest(new { error = "tenant_context_missing" });

        await repo.RecordAsync(new RecordAgentUsageRequest(
            TenantId: tenantId,
            DepartmentId: principal.DepartmentId,
            ActorId: principal.UserId,
            AgentId: agentId,
            AgentVersionId: request.AgentVersionId,
            EventType: request.EventType,
            TraceId: request.TraceId,
            Provider: request.Provider,
            Model: request.Model,
            InputTokens: request.InputTokens,
            OutputTokens: request.OutputTokens,
            CostAmount: request.CostAmount,
            TotalLatencyMs: request.TotalLatencyMs,
            LlmLatencyMs: request.LlmLatencyMs,
            ToolLatencyMs: request.ToolLatencyMs,
            RagLatencyMs: request.RagLatencyMs,
            LlmCallCount: request.LlmCallCount,
            CacheCreationInputTokens: request.CacheCreationInputTokens,
            CacheReadInputTokens: request.CacheReadInputTokens,
            NumTurns: request.NumTurns,
            RequestId: request.RequestId,
            CostSource: request.CostSource,
            Metrics: request.Metrics),
            ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RecordUnscopedAsync(
        UsageRequest request, IAgentUsageRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!UnscopedEventTypes.Contains(request.EventType) || request.AgentVersionId is not null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["eventType"] = ["not an agent-less usage event"] });
        if (!AgentUsageEvents.ValidMetrics(request.Metrics))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["metrics"] = ["numeric snake_case metrics only"] });
        if (!AuthenticatedPrincipalContext.TryGet(ctx, out var principal))
            return Results.BadRequest(new { error = "principal_missing" });
        // A platform principal without a tenant (super_admin) has no tenant
        // scope to attribute usage to: nothing is recorded.
        if (principal.TenantId is not { } tenantId) return Results.NoContent();

        await repo.RecordAsync(new RecordAgentUsageRequest(
            TenantId: tenantId,
            DepartmentId: principal.DepartmentId,
            ActorId: principal.UserId,
            AgentId: null,
            AgentVersionId: null,
            EventType: request.EventType,
            TraceId: request.TraceId,
            Provider: request.Provider,
            Model: request.Model,
            InputTokens: request.InputTokens,
            OutputTokens: request.OutputTokens,
            CostAmount: request.CostAmount,
            TotalLatencyMs: request.TotalLatencyMs,
            LlmLatencyMs: request.LlmLatencyMs,
            ToolLatencyMs: request.ToolLatencyMs,
            RagLatencyMs: request.RagLatencyMs,
            LlmCallCount: request.LlmCallCount,
            CacheCreationInputTokens: request.CacheCreationInputTokens,
            CacheReadInputTokens: request.CacheReadInputTokens,
            NumTurns: request.NumTurns,
            RequestId: request.RequestId,
            CostSource: request.CostSource,
            Metrics: request.Metrics),
            ct);
        return Results.NoContent();
    }

    private static bool TryValidate(object instance, out Dictionary<string, string[]> errors)
    {
        var validationCtx = new ValidationContext(instance);
        var results = new List<ValidationResult>();
        var ok = Validator.TryValidateObject(instance, validationCtx, results, validateAllProperties: true);
        errors = results
            .SelectMany(r => r.MemberNames.DefaultIfEmpty("request").Select(m => (Member: m, r.ErrorMessage)))
            .GroupBy(t => t.Member)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage ?? "Invalid").ToArray());
        return ok;
    }
}
