using System.Text;
using Aiq.Gateway.Books;
using Aiq.Gateway.Data;
using Aiq.Gateway.Endpoints;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Jobs;
using Aiq.Gateway.Storage;
using Aiq.Gateway.Vouchers;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Token audit Phase 1 against the real schema (migration 066): cache token
/// classes stored separately, real call counts, numeric-only metrics, and
/// OCR-correction usage attributed to the uploading tenant.
/// </summary>
public sealed class AgentUsageTokenDetailIntegrationTests(TaxVerificationDatabase fx) : IClassFixture<TaxVerificationDatabase>
{
    private AgentUsageRepository Repo() => new(new NpgsqlConnectionFactory(fx.ConnectionString!));

    [Fact]
    public async Task Stores_each_token_class_call_count_and_numeric_metrics()
    {
        if (fx.ConnectionString is null) return;
        var requestId = Guid.NewGuid();
        await Repo().RecordAsync(new RecordAgentUsageRequest(
            fx.Tenant, fx.Dept, fx.User, null, null, "chat", null, "claude_sdk", "sonnet",
            InputTokens: 12, OutputTokens: 300, CostAmount: 0.0421m, TotalLatencyMs: 900, LlmLatencyMs: 800,
            ToolLatencyMs: 50, RagLatencyMs: 0, LlmCallCount: 2, CacheCreationInputTokens: 9000,
            CacheReadInputTokens: 21000, NumTurns: 2, RequestId: requestId, CostSource: "claude_cli_total_cost_usd",
            Metrics: new Dictionary<string, long> { ["system_prompt_chars"] = 55000, ["tool_calls"] = 1 }), CancellationToken.None);

        var row = await RowAsync(requestId);
        Assert.Equal((12L, 9000L, 21000L, 300L, 2, 2), (row.In, row.CacheW, row.CacheR, row.Out, row.Calls, row.Turns));
        Assert.Equal("claude_cli_total_cost_usd", row.CostSource);
        Assert.Contains("\"system_prompt_chars\": 55000", row.Metadata);
    }

    [Fact]
    public async Task Ocr_usage_for_a_voucher_is_attributed_to_the_voucher_scope()
    {
        if (fx.ConnectionString is null) return;
        var voucher = await ScalarAsync<Guid>("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, status)
            VALUES (@u, @t, @d, 'invoice_vat_in', 'processing') RETURNING id
            """, new { u = fx.User, t = fx.Tenant, d = fx.Dept });
        var report = new LlmUsageReport(3, 30, 400, 15000, 30000, 0.12m, "claude_cli", "claude-sonnet-4-6");

        await Repo().RecordForVoucherAsync(voucher, AgentUsageEvents.FromOcr(report, null, null, null), CancellationToken.None);

        var row = await QuerySingleAsync<(Guid Tenant, Guid Dept, Guid Actor, Guid? Agent, int Calls, long CacheR)>("""
            SELECT tenant_id, department_id, actor_id, agent_id, llm_call_count, cache_read_input_tokens
            FROM agent_usage_events WHERE event_type='ocr_correction' AND cache_read_input_tokens=30000
            """);
        Assert.Equal((fx.Tenant, fx.Dept, fx.User, (Guid?)null, 3, 30000L), row);
    }

    [Fact]
    public async Task Content_or_non_numeric_metrics_are_refused()
    {
        if (fx.ConnectionString is null) return;
        var bad = new RecordAgentUsageRequest(fx.Tenant, fx.Dept, fx.User, null, null, "chat", null, null, null,
            null, null, null, null, null, null, null, 1, Metrics: new Dictionary<string, long> { ["Nội dung chứng từ"] = 1 });
        await Assert.ThrowsAsync<ArgumentException>(() => Repo().RecordAsync(bad, CancellationToken.None));
        Assert.False(AgentUsageEvents.ValidMetrics(new Dictionary<string, long> { ["chars"] = -1 }));
        Assert.True(AgentUsageEvents.ValidMetrics(new Dictionary<string, long> { ["rag_chars"] = 0 }));
    }

    [Fact]
    public void Agentless_route_accepts_only_known_event_types()
    {
        Assert.Equal(new[] { "legacy_agent_run", "pec_chat", "plain_chat" },
            AgentUsageEndpoints.UnscopedEventTypes.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Ocr_job_records_correction_usage_even_when_text_is_discarded()
    {
        if (fx.ConnectionString is null) return;
        var voucher = await ScalarAsync<Guid>("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, status)
            VALUES (@u, @t, @d, 'invoice_vat_in', 'processing') RETURNING id
            """, new { u = fx.User, t = fx.Tenant, d = fx.Dept });
        var usage = new LlmUsageReport(2, 7, 99, 1234, 4321, 0.05m, "claude_cli", "m");
        var runner = new VoucherOcrJobRunner(new OcrAgent(usage), new VoucherRepository(new NpgsqlConnectionFactory(fx.ConnectionString!)),
            new BytesStorage(), Options.Create(new S3Options { ServiceUrl = "http://unused", AccessKey = "x", SecretKey = "x" }),
            NullLogger<VoucherOcrJobRunner>.Instance, Repo());

        await runner.RunAsync(voucher, "k", "a.pdf", "application/pdf", false, CancellationToken.None);

        var calls = await ScalarAsync<int>(
            "SELECT llm_call_count FROM agent_usage_events WHERE event_type='ocr_correction' AND cache_creation_input_tokens=1234 AND cache_read_input_tokens=4321");
        Assert.Equal(2, calls);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<(long In, long CacheW, long CacheR, long Out, int Calls, int Turns, string CostSource, string Metadata)> RowAsync(Guid requestId) =>
        await QuerySingleAsync<(long, long, long, long, int, int, string, string)>("""
            SELECT input_tokens, cache_creation_input_tokens, cache_read_input_tokens, output_tokens,
                   llm_call_count, num_turns, cost_source, metadata::text
            FROM agent_usage_events WHERE request_id=@requestId
            """, new { requestId });

    private async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<T>(sql, args) ?? throw new InvalidOperationException("no row");
    }

    private async Task<T> QuerySingleAsync<T>(string sql, object? args = null)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        return await c.QuerySingleAsync<T>(sql, args);
    }

    private sealed class BytesStorage : IObjectStorage
    {
        public Task<PutObjectResult> PutAsync(string bucket, string key, Stream content, string? contentType, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> GetAsync(string bucket, string key, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.4 synthetic")));
        public Task<string> GetSignedUrlAsync(string bucket, string key, TimeSpan? expiry, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string bucket, string key, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> IsHealthyAsync(CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class OcrAgent(LlmUsageReport usage) : IAgentClient
    {
        public Task<AgentOcrResponse> RunOcrPipelineAsync(Stream s, string f, CancellationToken ct) =>
            Task.FromResult(new AgentOcrResponse(1, 0.9, new(), [], "HOA DON TONG HOP", usage));
        public Task<AgentOcrResponse> RunOcrImageAsync(Stream s, string f, string c, CancellationToken ct) => RunOcrPipelineAsync(s, f, ct);
        public Task<string> RunAgentTaskAsync(string task, string token, string capability, int maxTurns, CancellationToken ct) => throw new NotSupportedException();
        public Task<AgentDocumentExtractResponse> ExtractDocumentTextAsync(Stream s, string f, string c, CancellationToken ct) => throw new NotSupportedException();
    }
}
