using System.Reflection;
using System.Text;
using Aiq.Gateway.Books;
using Aiq.Gateway.Data;
using Aiq.Gateway.Jobs;
using Aiq.Gateway.Storage;
using Aiq.Gateway.Vouchers;
using Dapper;
using Hangfire;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// AG-02: the voucher OCR job (and every Hangfire retry of it after an Agent
/// crash / capacity refusal) writes only while the voucher is still waiting
/// for OCR, and a capacity refusal from the Agent is retried rather than
/// turned into a permanent "OCR unavailable". Real schema, throwaway database.
/// </summary>
public sealed class VoucherOcrJobIdempotencyIntegrationTests(TaxVerificationDatabase fx) : IClassFixture<TaxVerificationDatabase>
{
    private const string Key = "t/vouchers/ag02.pdf";

    [Fact]
    public async Task First_attempt_writes_the_ocr_result()
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync("processing");
        var agent = new FakeAgent("HOA DON AG02");

        await Runner(agent).RunAsync(id, Key, "a.pdf", "application/pdf", false, CancellationToken.None);

        var row = await RowAsync(id);
        Assert.Equal(("ocr_complete", "HOA DON AG02"), (row.Status, row.Text));
        Assert.Equal(1, agent.Calls);
    }

    [Fact]
    public async Task Retry_after_an_agent_failure_completes_the_voucher()
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync("processing");
        var crashing = new FakeAgent("x") { Failure = new HttpRequestException("connection reset (agent restarted)") };

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Runner(crashing).RunAsync(id, Key, "a.pdf", "application/pdf", false, CancellationToken.None));
        Assert.Equal("ocr_unavailable", (await RowAsync(id)).Status);

        await Runner(new FakeAgent("HOA DON RETRY")).RunAsync(id, Key, "a.pdf", "application/pdf", false, CancellationToken.None);
        Assert.Equal(("ocr_complete", "HOA DON RETRY"), ((await RowAsync(id)).Status, (await RowAsync(id)).Text));
    }

    [Theory]
    [InlineData("ocr_complete")]
    [InlineData("extracted")]
    [InlineData("approved")]
    public async Task Retry_never_touches_a_voucher_that_moved_on(string status)
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync(status, rawText: "TEXT THE USER CONFIRMED");
        var before = await RowAsync(id);
        var agent = new FakeAgent("NEW OCR TEXT");

        await Assert.ThrowsAsync<VoucherOcrSupersededException>(() =>
            Runner(agent).RunAsync(id, Key, "a.pdf", "application/pdf", false, CancellationToken.None));

        Assert.Equal(0, agent.Calls);
        Assert.Equal(before, await RowAsync(id));
    }

    [Fact]
    public async Task Result_is_discarded_when_the_voucher_moves_on_during_ocr()
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync("processing");
        // The user pastes text and extraction finishes while the OCR call is in flight.
        var agent = new FakeAgent("LATE OCR TEXT")
        {
            During = () => ExecAsync("UPDATE vouchers SET status='extracted', raw_ocr_text='PASTED' WHERE id=@id", new { id }),
        };

        await Assert.ThrowsAsync<VoucherOcrSupersededException>(() =>
            Runner(agent).RunAsync(id, Key, "a.pdf", "application/pdf", false, CancellationToken.None));

        Assert.Equal(("extracted", "PASTED"), ((await RowAsync(id)).Status, (await RowAsync(id)).Text));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(504)]
    public async Task Agent_capacity_or_timeout_is_retried_not_final(int upstream)
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync("processing");
        var agent = new FakeAgent("x") { Failure = new AgentRequestException("busy", upstream) };

        var thrown = await Assert.ThrowsAsync<AgentRequestException>(() =>
            Runner(agent).RunAsync(id, Key, "a.pdf", "application/pdf", false, CancellationToken.None));

        Assert.Equal(upstream, thrown.UpstreamStatus);
        var row = await RowAsync(id);
        Assert.Equal("ocr_unavailable", row.Status);
        Assert.Contains("thử lại", row.Error);
    }

    [Fact]
    public async Task Agent_unavailable_still_degrades_to_manual_text_without_retry()
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync("processing");
        var agent = new FakeAgent("x") { Failure = new AgentRequestException("no OCR", 503) };

        await Runner(agent).RunAsync(id, Key, "a.pdf", "application/pdf", false, CancellationToken.None);

        Assert.Equal("ocr_unavailable", (await RowAsync(id)).Status);
    }

    [Fact]
    public void Superseded_jobs_are_not_retried_by_hangfire()
    {
        var retry = typeof(VoucherOcrJobRunner).GetMethod(nameof(VoucherOcrJobRunner.RunAsync))!
            .GetCustomAttribute<AutomaticRetryAttribute>()!;
        Assert.Contains(typeof(VoucherOcrSupersededException), retry.ExceptOn);
        Assert.Equal(2, retry.Attempts);
    }

    // ---------------------------------------------------------------- helpers

    private VoucherOcrJobRunner Runner(IAgentClient agent) => new(
        agent,
        new VoucherRepository(new NpgsqlConnectionFactory(fx.ConnectionString!)),
        new BytesStorage(),
        Options.Create(new S3Options { ServiceUrl = "http://unused", AccessKey = "x", SecretKey = "x" }),
        NullLogger<VoucherOcrJobRunner>.Instance);

    private async Task<Guid> VoucherAsync(string status, string? rawText = null)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<Guid>("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, status, raw_ocr_text, total_amount)
            VALUES (@u, @t, @d, 'invoice_vat_in', @status, @rawText, 1000000) RETURNING id
            """, new { u = fx.User, t = fx.Tenant, d = fx.Dept, status, rawText });
    }

    private async Task<(string Status, string? Text, string? Error, decimal? Total)> RowAsync(Guid id)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        return await c.QuerySingleAsync<(string, string?, string?, decimal?)>(
            "SELECT status, raw_ocr_text, error_message, total_amount FROM vouchers WHERE id=@id", new { id });
    }

    private async Task ExecAsync(string sql, object args)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync(sql, args);
    }

    private sealed class BytesStorage : IObjectStorage
    {
        public Task<PutObjectResult> PutAsync(string bucket, string key, Stream content, string? contentType, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> GetAsync(string bucket, string key, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.4 synthetic")));
        public Task<string> GetSignedUrlAsync(string bucket, string key, TimeSpan? expiry, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string bucket, string key, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> IsHealthyAsync(CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class FakeAgent(string text) : IAgentClient
    {
        public int Calls { get; private set; }
        public Exception? Failure { get; init; }
        public Func<Task>? During { get; init; }

        public Task<AgentOcrResponse> RunOcrPipelineAsync(Stream s, string f, CancellationToken ct) => OcrAsync();
        public Task<AgentOcrResponse> RunOcrImageAsync(Stream s, string f, string c, CancellationToken ct) => OcrAsync();

        private async Task<AgentOcrResponse> OcrAsync()
        {
            Calls++;
            if (During is not null) await During();
            if (Failure is not null) throw Failure;
            return new AgentOcrResponse(1, 0.9, new(), [], text);
        }

        public Task<string> RunAgentTaskAsync(string task, string token, string capability, int maxTurns, CancellationToken ct) => throw new NotSupportedException();
        public Task<AgentDocumentExtractResponse> ExtractDocumentTextAsync(Stream s, string f, string c, CancellationToken ct) => throw new NotSupportedException();
    }
}
