using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Governance;

/// <summary>
/// Durable reporting sink for agent runtime usage (docs/ACCOUNTING_AGENT_AUDIT.md,
/// F-03). The Redis-backed audit store (agent/src/aiq_agent/audit/store.py)
/// stays the realtime/capped log; this table is the queryable, durable
/// source a cost/usage report can be built on. Written exactly once per
/// completed manifest-driven /chat or /agent/run request, from the Python
/// runtime, over HTTP using the same short-lived tool_execution token it
/// already carries for HITL evaluation — never a second, unscoped write
/// path, and tenant_id/department_id/actor_id are always server-derived
/// from that token's authenticated principal, never client-supplied.
/// </summary>
public sealed record RecordAgentUsageRequest(
    Guid? TenantId,
    Guid? DepartmentId,
    Guid? ActorId,
    Guid? AgentId,
    Guid? AgentVersionId,
    string EventType,
    Guid? TraceId,
    string? Provider,
    string? Model,
    long? InputTokens,
    long? OutputTokens,
    decimal? CostAmount,
    int? TotalLatencyMs,
    int? LlmLatencyMs,
    int? ToolLatencyMs,
    int? RagLatencyMs,
    int? LlmCallCount,
    long? CacheCreationInputTokens = null,
    long? CacheReadInputTokens = null,
    int? NumTurns = null,
    Guid? RequestId = null,
    string? CostSource = null,
    IReadOnlyDictionary<string, long>? Metrics = null);

/// <summary>LLM usage reported by the Agent for one OCR-correction pass (numbers only).</summary>
public sealed record LlmUsageReport(
    [property: System.Text.Json.Serialization.JsonPropertyName("llm_call_count")] int LlmCallCount,
    [property: System.Text.Json.Serialization.JsonPropertyName("input_tokens")] long InputTokens,
    [property: System.Text.Json.Serialization.JsonPropertyName("output_tokens")] long OutputTokens,
    [property: System.Text.Json.Serialization.JsonPropertyName("cache_creation_input_tokens")] long CacheCreationInputTokens,
    [property: System.Text.Json.Serialization.JsonPropertyName("cache_read_input_tokens")] long CacheReadInputTokens,
    [property: System.Text.Json.Serialization.JsonPropertyName("cost_usd")] decimal? CostUsd,
    [property: System.Text.Json.Serialization.JsonPropertyName("provider")] string? Provider,
    [property: System.Text.Json.Serialization.JsonPropertyName("model")] string? Model);

public static class AgentUsageEvents
{
    public const string OcrCorrection = "ocr_correction";
    public const string CostSourceClaudeCli = "claude_cli_total_cost_usd";

    /// <summary>Metric keys are short snake_case identifiers; values are non-negative counts.</summary>
    public static bool ValidMetrics(IReadOnlyDictionary<string, long>? metrics) =>
        metrics is null || (metrics.Count <= 40 && metrics.All(kv =>
            kv.Key.Length is > 0 and <= 40 && kv.Key.All(c => c is >= 'a' and <= 'z' or '_' or >= '0' and <= '9') && kv.Value >= 0));

    public static RecordAgentUsageRequest FromOcr(LlmUsageReport usage, Guid? tenantId, Guid? departmentId, Guid? actorId) => new(
        tenantId, departmentId, actorId, AgentId: null, AgentVersionId: null, EventType: OcrCorrection, TraceId: null,
        Provider: usage.Provider, Model: usage.Model, InputTokens: usage.InputTokens, OutputTokens: usage.OutputTokens,
        CostAmount: usage.CostUsd, TotalLatencyMs: null, LlmLatencyMs: null, ToolLatencyMs: null, RagLatencyMs: null,
        LlmCallCount: usage.LlmCallCount, CacheCreationInputTokens: usage.CacheCreationInputTokens,
        CacheReadInputTokens: usage.CacheReadInputTokens,
        CostSource: usage.CostUsd is null ? null : CostSourceClaudeCli);
}

public static class AgentUsageRecording
{
    /// <summary>Records OCR-correction usage; never throws (usage must not fail the business call).</summary>
    public static async Task RecordOcrBestEffortAsync(this IAgentUsageRepository repo, LlmUsageReport? usage,
        Guid tenantId, Guid departmentId, Guid actorId, ILogger logger, CancellationToken ct)
    {
        if (usage is null) return;
        try { await repo.RecordAsync(AgentUsageEvents.FromOcr(usage, tenantId, departmentId, actorId), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "OCR usage not recorded");
        }
    }
}

public interface IAgentUsageRepository
{
    Task RecordAsync(RecordAgentUsageRequest request, CancellationToken ct);

    /// <summary>
    /// Same row, with tenant/department/actor taken from the voucher itself
    /// (background OCR jobs carry only the voucher id). No-op when the voucher
    /// does not exist.
    /// </summary>
    Task RecordForVoucherAsync(Guid voucherId, RecordAgentUsageRequest request, CancellationToken ct);
}

public sealed class AgentUsageRepository : IAgentUsageRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public AgentUsageRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    private const string Columns = """
        tenant_id, department_id, actor_id, agent_id, agent_version_id, event_type,
        request_count, trace_id, provider, model, input_tokens, output_tokens, cost_amount,
        total_latency_ms, llm_latency_ms, tool_latency_ms, rag_latency_ms, llm_call_count,
        cache_creation_input_tokens, cache_read_input_tokens, num_turns, request_id, cost_source, metadata
        """;

    private const string Values = """
        @AgentId, @AgentVersionId, @EventType,
        1, @TraceId, @Provider, @Model, @InputTokens, @OutputTokens, @CostAmount,
        @TotalLatencyMs, @LlmLatencyMs, @ToolLatencyMs, @RagLatencyMs, @LlmCallCount,
        @CacheCreationInputTokens, @CacheReadInputTokens, @NumTurns, @RequestId, @CostSource, CAST(@Metadata AS jsonb)
        """;

    private static object Parameters(RecordAgentUsageRequest r, Guid? voucherId = null) => new
    {
        r.TenantId, r.DepartmentId, r.ActorId, r.AgentId, r.AgentVersionId, r.EventType, r.TraceId, r.Provider, r.Model,
        r.InputTokens, r.OutputTokens, r.CostAmount, r.TotalLatencyMs, r.LlmLatencyMs, r.ToolLatencyMs, r.RagLatencyMs,
        r.LlmCallCount, r.CacheCreationInputTokens, r.CacheReadInputTokens, r.NumTurns, r.RequestId, r.CostSource,
        Metadata = System.Text.Json.JsonSerializer.Serialize(r.Metrics ?? new Dictionary<string, long>()),
        VoucherId = voucherId,
    };

    public async Task RecordAsync(RecordAgentUsageRequest r, CancellationToken ct)
    {
        if (!AgentUsageEvents.ValidMetrics(r.Metrics)) throw new ArgumentException("invalid usage metrics", nameof(r));
        const string sql = $"""
            INSERT INTO agent_usage_events ({Columns})
            VALUES (@TenantId, @DepartmentId, @ActorId, {Values})
            """;
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, Parameters(r), cancellationToken: ct));
    }

    public async Task RecordForVoucherAsync(Guid voucherId, RecordAgentUsageRequest r, CancellationToken ct)
    {
        if (!AgentUsageEvents.ValidMetrics(r.Metrics)) throw new ArgumentException("invalid usage metrics", nameof(r));
        const string sql = $"""
            INSERT INTO agent_usage_events ({Columns})
            SELECT v.tenant_id, v.department_id, v.user_id, {Values}
            FROM vouchers v WHERE v.id = @VoucherId
            """;
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, Parameters(r, voucherId), cancellationToken: ct));
    }
}
