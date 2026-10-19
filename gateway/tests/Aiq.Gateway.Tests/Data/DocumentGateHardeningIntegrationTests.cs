using Aiq.Gateway.Approvals;
using Aiq.Gateway.Banking;
using Aiq.Gateway.Books;
using Aiq.Gateway.Contracts;
using Aiq.Gateway.Customers;
using Aiq.Gateway.Data;
using Aiq.Gateway.Employees;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Jobs;
using Aiq.Gateway.Receipts;
using Aiq.Gateway.Suppliers;
using Aiq.Gateway.Vouchers;
using Aiq.Gateway.Vouchers.DocumentProcessing;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Document review gate hardening against the full production schema
/// (init + migrations through 063): blocker ownership, the re-extract run
/// actor, and explicit provider/runtime extraction failures — driven through
/// the real VoucherExtractJobRunner with real repositories and a
/// deterministic fake Agent (no provider is ever called).
/// </summary>
public sealed class DocumentGateHardeningIntegrationTests(DocumentProcessingDatabase fx) : IClassFixture<DocumentProcessingDatabase>
{
    static DocumentGateHardeningIntegrationTests() => SqlMapper.AddTypeHandler(new StringArrayTypeHandler());

    private NpgsqlConnectionFactory Factory => new(fx.ConnectionString!);
    private DocumentProcessingRepository Docs => new(Factory, new CapabilityRepository(Factory));
    private DocumentReviewRepository Reviews => new(Factory);
    private VoucherRepository Vouchers => new(Factory);

    private const string RawText = "PHIẾU THU số PT-LIVE-TEST-01. Thu của Công ty TNHH Thử Nghiệm số tiền 1.000.000 đồng. Lý do: LIVE-TEST.";
    private const string GoodAnswer = """
        ```json
        {"voucher_type":"receipt","document_number":"PT-LIVE-TEST-01","document_date":"2026-10-01","total_amount":1000000,"payer_name":"Công ty TNHH Thử Nghiệm"}
        ```
        """;

    private async Task<T> Scalar<T>(string sql, object? args = null)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        return (await conn.ExecuteScalarAsync<T>(sql, args))!;
    }

    private Task<Guid> OcrCompleteVoucherAsync() => Scalar<Guid>("""
        INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, status, raw_ocr_text, ocr_confidence)
        VALUES (@u, @t, @d, 'receipt', 'ocr_complete', @raw, 0.99) RETURNING id
        """, new { u = fx.UserKt, t = fx.TenantA, d = fx.Kt, raw = RawText });

    private sealed class ScriptedAgent : IAgentClient
    {
        public Func<AgentTaskResult> Next { get; set; } = () => new(GoodAnswer, null, null, null, "test", "test");
        public int Calls { get; private set; }
        public Task<AgentTaskResult> RunAgentTaskWithProvenanceAsync(string task, string token, string capability, Guid? preferred, int maxTurns, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Next());
        }
        public Task<string> RunAgentTaskAsync(string task, string token, string capability, int maxTurns, CancellationToken ct) => throw new NotSupportedException();
        public Task<AgentOcrResponse> RunOcrPipelineAsync(Stream s, string f, CancellationToken ct) => throw new NotSupportedException();
        public Task<AgentOcrResponse> RunOcrImageAsync(Stream s, string f, string c, CancellationToken ct) => throw new NotSupportedException();
        public Task<AgentDocumentExtractResponse> ExtractDocumentTextAsync(Stream s, string f, string c, CancellationToken ct) => throw new NotSupportedException();
    }

    private VoucherExtractJobRunner Runner(IAgentClient agent)
    {
        var f = Factory;
        var customers = new CustomerRepository(f);
        var suppliers = new SupplierRepository(f);
        var employees = new EmployeeRepository(f);
        var vouchers = new VoucherRepository(f);
        var notifications = new NotificationRepository(f);
        return new VoucherExtractJobRunner(agent, vouchers, customers, suppliers, employees, new ApprovalRepository(f),
            new ReceiptMatchRepository(f, customers, notifications),
            new ExpenseClaimRepository(f, employees, suppliers, vouchers, notifications),
            Options.Create(new ExpenseRiskOptions()), new TenantCompanyProfileRepository(f),
            new BankStatementRepository(f, new BankAccountRepository(f)), Docs, Reviews,
            NullLogger<VoucherExtractJobRunner>.Instance);
    }

    private sealed record RunRow(Guid? Actor, string Trigger, string? ErrorCode, string? ErrorStage);

    private async Task<List<RunRow>> RunsAsync(Guid voucherId)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        return (await conn.QueryAsync<RunRow>("""
            SELECT actor_user_id AS Actor, trigger_kind AS Trigger, error_code AS ErrorCode, error_stage AS ErrorStage
            FROM document_processing_runs WHERE voucher_id=@voucherId ORDER BY created_at, id
            """, new { voucherId })).AsList();
    }

    private async Task<(string Status, Guid Creator, string? Fields, string? Code, string? Stage, DateTime? FailedAt)> StateAsync(Guid v)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        return await conn.QuerySingleAsync<(string, Guid, string?, string?, string?, DateTime?)>("""
            SELECT status, user_id, extracted_fields::text, extraction_error_code, extraction_error_stage, extraction_failed_at
            FROM vouchers WHERE id=@v
            """, new { v });
    }

    // ======================================================= RE-EXTRACT ACTOR

    [Fact]
    public async Task Initial_actor_is_the_uploader_and_admin_reextract_records_the_admin_while_creator_is_preserved()
    {
        if (fx.ConnectionString is null) return;
        var agent = new ScriptedAgent();
        var v = await OcrCompleteVoucherAsync();

        // Upload chain (legacy signature): uploader = creator = actor.
        await Runner(agent).RunAsync(v, fx.UserKt, fx.TenantA, "receipt", "token", default);
        var initial = await StateAsync(v);
        Assert.Equal(("extracted", fx.UserKt), (initial.Status, initial.Creator));
        Assert.Equal(new RunRow(fx.UserKt, "initial", null, null), Assert.Single(await RunsAsync(v)));

        // ADMIN re-extract: actor comes from the job payload (endpoint principal).
        await Runner(agent).RunForActorAsync(v, fx.UserKt, fx.TenantA, "receipt", "admin-token", fx.AdminKt, default);
        var runs = await RunsAsync(v);
        Assert.Equal(new RunRow(fx.AdminKt, "reextract", null, null), runs[^1]);
        Assert.Equal(fx.UserKt, (await StateAsync(v)).Creator);   // creator/uploader untouched
        Assert.Equal(fx.UserKt, await Scalar<Guid>("SELECT user_id FROM vouchers WHERE id=@v", new { v }));
    }

    [Fact]
    public async Task Cross_tenant_or_unknown_actor_in_the_job_payload_is_refused()
    {
        if (fx.ConnectionString is null) return;
        var agent = new ScriptedAgent();
        var v = await OcrCompleteVoucherAsync();
        foreach (var spoofed in new[] { fx.AdminB, Guid.NewGuid() })
            await Runner(agent).RunForActorAsync(v, fx.UserKt, fx.TenantA, "receipt", "token", spoofed, default);
        Assert.Equal(0, agent.Calls);
        Assert.Empty(await RunsAsync(v));
        Assert.Equal("ocr_complete", (await StateAsync(v)).Status);
        Assert.False(await Docs.IsActiveTenantUserAsync(fx.TenantA, fx.AdminB, default));
        Assert.True(await Docs.IsActiveTenantUserAsync(fx.TenantA, fx.AdminKt, default));
    }

    // ===================================================== PROVIDER FAILURE

    public static TheoryData<string, string, string> InitialFailures => new()
    {
        { "502:You've hit your session limit · resets 3am", ExtractionFailures.ProviderSessionLimit, "agent_execution" },
        { "429:too many", ExtractionFailures.ProviderRateLimited, "agent_execution" },
        { "503:upstream down", ExtractionFailures.ProviderUnavailable, "agent_execution" },
        { "500:boom", ExtractionFailures.AgentExecutionFailed, "agent_execution" },
        { "timeout", ExtractionFailures.ProviderTimeout, "agent_execution" },
        { "200:You've hit your session limit · resets 3am", ExtractionFailures.ProviderSessionLimit, "agent_output" },
        { "200:Xin lỗi, tôi không đọc được chứng từ.", ExtractionFailures.OutputInvalid, "agent_output" },
    };

    private static Func<AgentTaskResult> Failure(string spec) => spec switch
    {
        "timeout" => () => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"),
        _ when spec.StartsWith("200:", StringComparison.Ordinal) => () => new(spec[4..], null, null, null, "test", "test"),
        _ => () => throw new AgentRequestException(spec[(spec.IndexOf(':') + 1)..], int.Parse(spec[..spec.IndexOf(':')])),
    };

    [Theory]
    [MemberData(nameof(InitialFailures))]
    public async Task Initial_provider_failure_is_an_explicit_safe_failed_state_and_retry_clears_it(string spec, string code, string stage)
    {
        if (fx.ConnectionString is null) return;
        var agent = new ScriptedAgent { Next = Failure(spec) };
        var v = await OcrCompleteVoucherAsync();

        await Runner(agent).RunAsync(v, fx.UserKt, fx.TenantA, "receipt", "token", default);   // no throw → no Hangfire auto-retry loop
        var failed = await StateAsync(v);
        Assert.Equal(("extraction_failed", code, stage), (failed.Status, failed.Code, failed.Stage));
        Assert.NotNull(failed.FailedAt);
        Assert.Equal(RawText, await Scalar<string>("SELECT raw_ocr_text FROM vouchers WHERE id=@v", new { v }));   // OCR kept
        Assert.Equal(new RunRow(fx.UserKt, "initial", code, stage), Assert.Single(await RunsAsync(v)));
        Assert.Equal(new ExtractionFailureState(code, stage, failed.FailedAt), await Docs.GetExtractionFailureAsync(fx.TenantA, v, default));
        // No provider text persisted anywhere.
        Assert.Equal(0, await Scalar<int>("""
            SELECT COUNT(*) FROM vouchers WHERE id=@v AND (COALESCE(extracted_fields::text,'') ILIKE '%session limit%' OR COALESCE(error_message,'') ILIKE '%session limit%')
            """, new { v }));

        // Retry (ADMIN) succeeds → error superseded, normal extraction.
        agent.Next = () => new(GoodAnswer, null, null, null, "test", "test");
        await Runner(agent).RunForActorAsync(v, fx.UserKt, fx.TenantA, "receipt", "token", fx.AdminKt, default);
        var ok = await StateAsync(v);
        Assert.Equal(("extracted", (string?)null, (string?)null), (ok.Status, ok.Code, ok.Stage));
        Assert.Null(await Docs.GetExtractionFailureAsync(fx.TenantA, v, default));
        Assert.Equal(new RunRow(fx.AdminKt, "initial", null, null), (await RunsAsync(v))[^1]);
    }

    [Theory]
    [InlineData("502:You've hit your session limit · resets 3am", ExtractionFailures.ProviderSessionLimit)]
    [InlineData("timeout", ExtractionFailures.ProviderTimeout)]
    [InlineData("200:rate limit exceeded", ExtractionFailures.ProviderRateLimited)]
    public async Task Failed_reextract_preserves_the_previous_successful_extraction_and_audits_the_admin(string spec, string code)
    {
        if (fx.ConnectionString is null) return;
        var agent = new ScriptedAgent();
        var v = await OcrCompleteVoucherAsync();
        await Runner(agent).RunAsync(v, fx.UserKt, fx.TenantA, "receipt", "token", default);
        var before = await StateAsync(v);
        Assert.Equal("extracted", before.Status);
        var provenanceBefore = await Scalar<string>("SELECT document_config_source FROM vouchers WHERE id=@v", new { v });

        // ADMIN type correction moves the row to 'processing', then the re-extract fails.
        Assert.True(await Vouchers.SetVoucherTypeClassificationAsync(fx.TenantA, fx.Kt, v, "receipt",
            """{"voucher_type":"receipt","source":"admin_corrected"}""", default, reprocessing: true));
        Assert.Equal("processing", (await StateAsync(v)).Status);
        agent.Next = Failure(spec);
        await Runner(agent).RunForActorAsync(v, fx.UserKt, fx.TenantA, "admin:receipt", "admin-token", fx.AdminKt, default);

        var after = await StateAsync(v);
        Assert.Equal("extracted", after.Status);                                  // previous result stays current
        Assert.Equal(code, after.Code);                                            // failure still visible
        Assert.Equal(fx.UserKt, after.Creator);
        Assert.Contains("PT-LIVE-TEST-01", after.Fields);                          // previous extraction intact
        Assert.Equal(provenanceBefore, await Scalar<string>("SELECT document_config_source FROM vouchers WHERE id=@v", new { v }));
        var runs = await RunsAsync(v);
        Assert.Equal(2, runs.Count);
        Assert.Equal(new RunRow(fx.UserKt, "initial", null, null), runs[0]);      // audit history kept
        Assert.Equal((fx.AdminKt, "reextract", code), (runs[1].Actor, runs[1].Trigger, runs[1].ErrorCode));
    }

    [Fact]
    public async Task Extraction_error_and_blocker_columns_only_accept_known_safe_codes()
    {
        if (fx.ConnectionString is null) return;
        var v = await OcrCompleteVoucherAsync();
        await Assert.ThrowsAsync<PostgresException>(() =>
            Docs.SetExtractionFailureAsync(fx.TenantA, v, "You've hit your session limit", "agent_execution", default));
        await Assert.ThrowsAsync<PostgresException>(() =>
            Docs.SetProcessingBlockerAsync(fx.TenantA, v, "anything_else", true, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Docs.SetExtractionFailureAsync(fx.TenantB, v, ExtractionFailures.ProviderTimeout, "agent_execution", default));   // other tenant: not recorded
        Assert.Null(await Docs.GetExtractionFailureAsync(fx.TenantA, v, default));
    }

    // ===================================================== BLOCKER OWNERSHIP

    [Fact]
    public async Task Type_confirmation_clears_only_its_own_blocker()
    {
        if (fx.ConnectionString is null) return;
        var v = await OcrCompleteVoucherAsync();
        await Scalar<int>("UPDATE vouchers SET status='extracted' WHERE id=@v RETURNING 1", new { v });
        var wf = Guid.NewGuid();
        var (task, _) = await Reviews.OpenAsync(new DocumentReviewOpenRequest(fx.TenantA, fx.Kt, v, null, null, wf, null, "receipt",
            ["workflow_require_review"], fx.UserKt), default);
        await Docs.SetProcessingBlockerAsync(fx.TenantA, v, ProcessingBlockers.LegacyL2Failure, true, default);
        await Docs.SetProcessingBlockerAsync(fx.TenantA, v, ProcessingBlockers.LegacyL2Failure, true, default);   // idempotent
        var expected = new[] { "document_review:awaiting_review", "legacy_l2_failure" };
        Assert.Equal(expected, await Reviews.GetBlockingReasonsAsync(fx.TenantA, v, default));

        // ADMIN confirms the voucher type (confirmed classification, no reprocess).
        Assert.True(await Vouchers.SetVoucherTypeClassificationAsync(fx.TenantA, fx.Kt, v, "receipt",
            """{"voucher_type":"receipt","source":"admin_corrected","requires_confirmation":true,"confirmed_by":"x"}""", default));
        Assert.Equal(expected, await Reviews.GetBlockingReasonsAsync(fx.TenantA, v, default));
        Assert.Equal("awaiting_review", await Reviews.GetGateStatusAsync(fx.TenantA, v, default));
        Assert.Equal("open", await Scalar<string>("SELECT status FROM document_review_tasks WHERE id=@id", new { id = task.Id }));

        // Review approval clears ONLY the review.
        Assert.NotNull(await Reviews.DecideAsync(fx.TenantA, fx.Kt, v, task.Id, fx.AdminKt, "approved", null, default));
        Assert.Equal(["legacy_l2_failure"], await Reviews.GetBlockingReasonsAsync(fx.TenantA, v, default));

        // The extraction owner clears L2.
        await Docs.SetProcessingBlockerAsync(fx.TenantA, v, ProcessingBlockers.LegacyL2Failure, false, default);
        Assert.Empty(await Reviews.GetBlockingReasonsAsync(fx.TenantA, v, default));
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("review_task_creation_failed")]
    public async Task Type_confirmation_never_releases_rejected_or_failed_review(string status)
    {
        if (fx.ConnectionString is null) return;
        var v = await OcrCompleteVoucherAsync();
        await Scalar<int>("UPDATE vouchers SET status='extracted', document_review_status=@status WHERE id=@v RETURNING 1", new { v, status });
        var before = await Reviews.GetBlockingReasonsAsync(fx.TenantA, v, default);
        Assert.NotEmpty(before);
        Assert.True(await Vouchers.SetVoucherTypeClassificationAsync(fx.TenantA, fx.Kt, v, "receipt",
            """{"voucher_type":"receipt","source":"admin_corrected"}""", default));
        Assert.Equal(before, await Reviews.GetBlockingReasonsAsync(fx.TenantA, v, default));
        Assert.Equal(status, await Reviews.GetGateStatusAsync(fx.TenantA, v, default));
    }

    [Fact]
    public async Task Successful_extraction_without_l2_need_never_sets_the_l2_blocker_and_legacy_voucher_is_unblocked()
    {
        if (fx.ConnectionString is null) return;
        var v = await OcrCompleteVoucherAsync();
        await Runner(new ScriptedAgent()).RunAsync(v, fx.UserKt, fx.TenantA, "receipt", "token", default);
        Assert.Empty(await Reviews.GetBlockingReasonsAsync(fx.TenantA, v, default));
        Assert.Null(await Reviews.GetGateStatusAsync(fx.TenantA, v, default));
        Assert.Empty(await Scalar<string[]>("SELECT processing_blockers FROM vouchers WHERE id=@v", new { v }));
    }
}
