using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aiq.Gateway.Books;
using Aiq.Gateway.Data;
using Aiq.Gateway.Employees;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Jobs;
using Aiq.Gateway.Payments;
using Aiq.Gateway.Storage;
using Aiq.Gateway.Vouchers;
using Aiq.Gateway.Vouchers.DocumentProcessing;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Multi-file Expense Claim intake on the real schema: ONE claim → ONE item per
/// file → per-item OCR/extraction (real ExpenseClaimItemJobRunner, scripted
/// Agent, in-memory object store) → the EXISTING engine (ProposeAsync), review
/// stages, authoritative recognition and payment — unchanged.
/// Roles in most tests: claimant = USER, stage 1 = Admin, stage 2 = Admin2, payer = Admin3 —
/// any same-department ADMIN may play every role (see Same_admin_* below).
/// </summary>
public sealed class ExpenseClaimMultiFileIntegrationTests(TaxVerificationDatabase fx) : IClassFixture<TaxVerificationDatabase>
{
    static ExpenseClaimMultiFileIntegrationTests()
    {
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyTypeHandler());
        SqlMapper.AddTypeHandler(new StringArrayTypeHandler());
    }

    private readonly ExpenseClaimTestData d = new(fx);
    private bool Skip => fx.ConnectionString is null;
    private readonly MemoryStorage storage = new();
    private readonly ScriptedAgent agent = new();

    private ExpenseClaimIntakeRepository Intake() => new(d.Factory(), d.Claims(), NullLogger<ExpenseClaimIntakeRepository>.Instance);
    private DocumentProcessingRepository Docs() => new(d.Factory(), new CapabilityRepository(d.Factory()));
    private ExpenseClaimItemJobRunner Runner(IDocumentProcessingRepository? docs = null) =>
        new(agent, storage, Options.Create(new S3Options { ServiceUrl = "x", AccessKey = "x", SecretKey = "x" }), Intake(),
            docs ?? Docs(), NullLogger<ExpenseClaimItemJobRunner>.Instance);

    // ------------------------------------------------------------------ bills

    /// <summary>One scripted bill: the "file" carries a tag; OCR returns it; the Agent answers per tag.</summary>
    private static string Bill(string category, decimal gross, string? date = "2026-09-18", decimal? vat = null, string? mst = null,
        string? number = null, decimal confidence = 0.95m, string? extra = null) =>
        "```json\n" + JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["category"] = category, ["merchant_name"] = $"LIVE-TEST {category}", ["merchant_tax_code"] = mst, ["document_number"] = number,
            ["document_date"] = date, ["gross_amount"] = gross, ["vat_amount"] = vat, ["currency"] = "VND",
            ["description"] = $"Chi phí {category}", ["confidence"] = confidence,
        }).TrimEnd('}') + (extra is null ? "" : "," + extra) + "}\n```";

    private async Task<(Guid Claim, Guid Employee)> ClaimAsync(decimal advance = 2_000_000m, DateOnly? tripStart = null, DateOnly? tripEnd = null,
        string? requestId = null, Guid? creator = null)
    {
        await d.MapAsync("cong_tac", "642");
        await d.MapAsync("khach_san", "642");
        await d.MapAsync("an_uong", "641");
        var employee = await d.Scalar<Guid>("""
            INSERT INTO sim_employees(tenant_id, employee_code, full_name, outstanding_advance, ytd_expense)
            VALUES (@t, @code, 'LIVE-TEST Nhân viên', @advance, 1000000) RETURNING id
            """, new { t = fx.Tenant, code = "NV-MF-" + Guid.NewGuid().ToString("N")[..8], advance });
        var c = await Intake().CreateClaimAsync(fx.Tenant, fx.Dept, creator ?? fx.User,
            new CreateExpenseClaimCommand(employee, "Nha Trang → TP.HCM", tripStart, tripEnd, null, requestId), default);
        return (c!.Value.ClaimId, employee);
    }

    private async Task<Guid> UploadAsync(Guid claim, string tag, string? answer, string? requestId = null, int? index = null, string? body = null,
        Guid? actor = null)
    {
        if (answer is not null) agent.Answers[tag] = answer;
        var bytes = Encoding.UTF8.GetBytes(body ?? $"%PDF-1.4 BILL:{tag}");
        var key = TenantObjectKey.Build(fx.Tenant, "expense-claims", claim.ToString("N"), $"{Guid.NewGuid():N}.pdf");
        await storage.PutAsync("voucher-uploads", key, new MemoryStream(bytes), "application/pdf", default);
        var (item, _) = await Intake().AddItemAsync(fx.Tenant, fx.Dept, claim, actor ?? fx.User,
            new NewExpenseClaimItem(requestId, index, key, $"{tag}.pdf", "application/pdf", bytes.Length,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()), default);
        return item.Id;
    }

    private async Task ProcessAsync(Guid item, int attempt = 1, IDocumentProcessingRepository? docs = null) =>
        await Runner(docs).RunAsync(item, attempt, fx.User, "token", default);

    private async Task<(Guid Claim, Guid Employee, Guid[] Items)> TripAsync(decimal advance = 2_000_000m)
    {
        var (claim, employee) = await ClaimAsync(advance);
        var items = new[]
        {
            await UploadAsync(claim, $"bus{Guid.NewGuid():N}", Bill("cong_tac", 600_000m)),
            await UploadAsync(claim, $"hotel{Guid.NewGuid():N}", Bill("khach_san", 2_200_000m)),
            await UploadAsync(claim, $"meal{Guid.NewGuid():N}", Bill("an_uong", 900_000m)),
        };
        foreach (var i in items) await ProcessAsync(i);
        return (claim, employee, items);
    }

    private async Task<ExpenseClaimItemRecord> ItemAsync(Guid claim, Guid item) =>
        (await Intake().GetAsync(fx.Tenant, fx.Dept, claim, default))!.Items.Single(i => i.Id == item);

    private async Task<string?> GateAsync(Guid claim)
    {
        await using var conn = new NpgsqlConnection(d.Cs);
        await conn.OpenAsync();
        return await ExpenseClaimItemGate.ErrorAsync(conn, null, claim, default);
    }

    // ================================================================ BATCH

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public async Task One_batch_creates_exactly_one_claim_with_one_item_per_file(int files)
    {
        if (Skip) return;
        var before = await d.Scalar<int>("SELECT COUNT(*) FROM vouchers WHERE voucher_type='expense_claim'");
        var (claim, _) = await ClaimAsync(requestId: $"req-{Guid.NewGuid():N}");
        var ids = new List<Guid>();
        for (var i = 0; i < files; i++) ids.Add(await UploadAsync(claim, $"b{i}-{Guid.NewGuid():N}", null, "batch-1", i));
        Assert.Equal(before + 1, await d.Scalar<int>("SELECT COUNT(*) FROM vouchers WHERE voucher_type='expense_claim'"));
        var view = (await Intake().GetAsync(fx.Tenant, fx.Dept, claim, default))!;
        Assert.Equal(files, view.Items.Count);
        Assert.Equal(Enumerable.Range(1, files), view.Items.Select(i => i.SequenceNo));
        Assert.All(view.Items, i => Assert.Equal(ExpenseClaimItemStatuses.Processing, i.Status));
        // Every item keeps ITS OWN original file.
        foreach (var i in view.Items)
        {
            var f = (await Intake().GetItemFileAsync(fx.Tenant, fx.Dept, claim, i.Id, default))!.Value;
            Assert.Equal($"%PDF-1.4 BILL:{i.OriginalFilename[..^4]}", Encoding.UTF8.GetString(storage.Objects[f.ObjectKey]));
        }
        Assert.Null(await d.ScalarOrNull<Guid?>("SELECT id FROM expense_claim_results WHERE voucher_id=@claim", new { claim }));
    }

    [Fact]
    public async Task Repeated_submit_with_the_same_request_identity_creates_nothing_new()
    {
        if (Skip) return;
        var requestId = $"req-{Guid.NewGuid():N}";
        var (claim, employee) = await ClaimAsync(requestId: requestId);
        var again = await Intake().CreateClaimAsync(fx.Tenant, fx.Dept, fx.User, new CreateExpenseClaimCommand(employee, "x", null, null, null, requestId), default);
        Assert.Equal((claim, false), again!.Value);
        var first = await UploadAsync(claim, $"r{Guid.NewGuid():N}", null, requestId, 0);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Intake().AddItemAsync(fx.Tenant, fx.Dept, claim, fx.User,
            new NewExpenseClaimItem(requestId, 0, "k", "x.pdf", "application/pdf", 3, "abc"), default)));
        Assert.All(results, r => Assert.Equal((first, false), (r.Item.Id, r.Created)));
        Assert.Equal(1, await d.Scalar<int>("SELECT COUNT(*) FROM expense_claim_items WHERE claim_voucher_id=@claim", new { claim }));
        // Another tenant's / department's view of the claim: not found.
        Assert.Null(await Intake().GetAsync(fx.Tenant, fx.OtherDept, claim, default));
        Assert.Null(await Intake().GetAsync(Guid.NewGuid(), fx.Dept, claim, default));
        Assert.Null(await Intake().CreateClaimAsync(fx.Tenant, fx.Dept, fx.User, new CreateExpenseClaimCommand(Guid.NewGuid(), null, null, null, null, null), default));
    }

    // =========================================================== PROCESSING

    [Fact]
    public async Task Items_process_independently_into_the_existing_engine_with_the_authoritative_advance()
    {
        if (Skip) return;
        var (claim, employee, items) = await TripAsync();
        var view = (await Intake().GetAsync(fx.Tenant, fx.Dept, claim, default))!;
        Assert.All(view.Items, i => Assert.Equal(ExpenseClaimItemStatuses.Ready, i.Status));
        Assert.Equal((3_700_000m, 2_000_000m, 1_700_000m), (view.Totals.ReadyTotal, view.Totals.OutstandingAdvance, view.Totals.ExpectedPayable!.Value));
        var r = view.Result!;
        Assert.Equal((3_700_000m, 3_700_000m, 2_000_000m, 1_700_000m, "APPROVED", "proposed"),
            (r.RequestedAmount, r.ApprovedAmount, r.AdvanceAmount, r.PayableAmount, r.Outcome, r.ReviewStatus));
        Assert.Equal("extracted", await d.Scalar<string>("SELECT status FROM vouchers WHERE id=@claim", new { claim }));
        Assert.Equal(3, await d.Scalar<int>("SELECT jsonb_array_length(extracted_fields->'items') FROM vouchers WHERE id=@claim", new { claim }));
        // Upload never touched the employee's advance / YTD.
        Assert.Equal((2_000_000m, 1_000_000m), await Employee(employee));
        // Each item has its own run in the canonical processing audit (legacy config here: no Agent configured).
        Assert.Equal(3, await d.Scalar<int>("SELECT COUNT(*) FROM document_processing_runs WHERE voucher_id=@claim AND expense_claim_item_id IS NOT NULL AND document_type='expense_claim_item'", new { claim }));
        Assert.All(view.Items, i => Assert.Equal("legacy", i.ConfigSource));
        Assert.Equal(3, agent.ExtractionCalls);   // one call per bill, never one giant prompt
    }

    [Fact]
    public async Task One_failed_item_never_fails_its_siblings_and_retry_reprocesses_only_that_item()
    {
        if (Skip) return;
        var (claim, _) = await ClaimAsync();
        var good = await UploadAsync(claim, $"ok{Guid.NewGuid():N}", Bill("cong_tac", 600_000m));
        var badTag = $"bad{Guid.NewGuid():N}";
        agent.Failures[badTag] = new AgentRequestException("You've hit your session limit · resets 3am", 502);
        var bad = await UploadAsync(claim, badTag, null);
        await ProcessAsync(good);
        await ProcessAsync(bad);
        var failed = await ItemAsync(claim, bad);
        Assert.Equal((ExpenseClaimItemStatuses.ExtractionFailed, "provider_session_limit", "agent_execution"), (failed.Status, failed.ErrorCode, failed.ErrorStage));
        Assert.Equal(ExpenseClaimItemStatuses.Ready, (await ItemAsync(claim, good)).Status);
        Assert.Equal(ExpenseClaimItemGate.Unresolved, await GateAsync(claim));
        Assert.Equal(0, await d.Scalar<int>("SELECT COUNT(*) FROM expense_claim_items WHERE id=@bad AND extracted_data::text ILIKE '%session limit%'", new { bad }));

        agent.Failures.Clear();
        agent.Answers[badTag] = Bill("an_uong", 900_000m);
        var callsBefore = agent.ExtractionCalls;
        var attempt = await Intake().BeginRetryAsync(fx.Tenant, fx.Dept, claim, bad, fx.Admin, default);
        Assert.Equal(2, attempt);
        await ProcessAsync(bad, attempt!.Value);
        Assert.Equal(callsBefore + 1, agent.ExtractionCalls);   // only the failed item re-ran
        Assert.Equal(ExpenseClaimItemStatuses.Ready, (await ItemAsync(claim, bad)).Status);
        Assert.Null(await GateAsync(claim));
        Assert.Equal(1_500_000m, (await d.Claims().GetByVoucherIdAsync(fx.Tenant, fx.Dept, claim, default))!.ApprovedAmount);
        Assert.Equal("reextract", await d.Scalar<string>(
            "SELECT trigger_kind FROM document_processing_runs WHERE expense_claim_item_id=@bad ORDER BY created_at DESC LIMIT 1", new { bad }));
    }

    [Fact]
    public async Task Concurrent_retries_start_one_attempt_and_a_stale_job_changes_nothing()
    {
        if (Skip) return;
        var (claim, _) = await ClaimAsync();
        var tag = $"c{Guid.NewGuid():N}";
        agent.Failures[tag] = new AgentRequestException("down", 503);
        var item = await UploadAsync(claim, tag, null);
        await ProcessAsync(item);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            try { return (await Intake().BeginRetryAsync(fx.Tenant, fx.Dept, claim, item, fx.Admin, default))?.ToString() ?? "null"; }
            catch (ExpenseClaimItemException ex) { return ex.Code; }
        }));
        Assert.Equal(1, outcomes.Count(o => o == "2"));
        Assert.Equal(4, outcomes.Count(o => o == "expense_claim_item_not_retryable"));
        agent.Failures.Clear();
        agent.Answers[tag] = Bill("cong_tac", 100_000m);
        var calls = agent.ExtractionCalls;
        await ProcessAsync(item, attempt: 1);          // superseded attempt: skipped
        Assert.Equal(calls, agent.ExtractionCalls);
        await ProcessAsync(item, attempt: 2);
        await ProcessAsync(item, attempt: 2);          // duplicate delivery of the same attempt: no second run
        Assert.Equal(calls + 1, agent.ExtractionCalls);
        Assert.Equal(ExpenseClaimItemStatuses.Ready, (await ItemAsync(claim, item)).Status);
    }

    [Fact]
    public async Task Manifest_items_record_the_published_version_and_retry_stays_pinned()
    {
        if (Skip) return;
        var docs = new ManifestDocs(Docs());
        var (claim, _) = await ClaimAsync();
        var tag = $"m{Guid.NewGuid():N}";
        agent.Answers[tag] = Bill("cong_tac", 600_000m, confidence: 0.5m);
        var item = await UploadAsync(claim, tag, null);
        await ProcessAsync(item, docs: docs);
        var processed = await ItemAsync(claim, item);
        Assert.Equal(("manifest", docs.AgentId, docs.VersionId, 7), (processed.ConfigSource, processed.AgentId!.Value, processed.AgentVersionId!.Value, processed.AgentVersion!.Value));
        Assert.Equal((docs.AgentId, docs.VersionId, true), agent.LastPinned!.Value);
        Assert.Contains("HUONG DAN CAU HINH", agent.LastPrompt);   // published expense_claim instructions reach the prompt
        Assert.Contains(ExpenseClaimItemReviewReasons.LowConfidence, processed.ReviewReasons);

        // Retry re-runs EXACTLY the recorded version; an unavailable version fails explicitly (never legacy).
        docs.PinnedDenial = DocumentProvenanceErrors.VersionUnavailable;
        var attempt = await Intake().BeginRetryAsync(fx.Tenant, fx.Dept, claim, item, fx.Admin, default);
        await ProcessAsync(item, attempt!.Value, docs);
        var failed = await ItemAsync(claim, item);
        Assert.Equal((ExpenseClaimItemStatuses.ExtractionFailed, DocumentProvenanceErrors.VersionUnavailable), (failed.Status, failed.ErrorCode));
        Assert.Equal(600_000m, failed.ExtractedGrossAmount);   // previous extraction kept
        Assert.Equal(1, docs.PinnedCalls);
        Assert.Equal(1, docs.UploaderCalls);
    }

    // ============================================================ REVIEW

    [Fact]
    public async Task Review_rules_flag_items_and_admin_confirmation_or_exclusion_resolves_them()
    {
        if (Skip) return;
        var (claim, _) = await ClaimAsync(tripStart: new DateOnly(2026, 9, 15), tripEnd: new DateOnly(2026, 9, 20));
        var ok = await UploadAsync(claim, $"ok{Guid.NewGuid():N}", Bill("cong_tac", 600_000m));
        var lowConf = await UploadAsync(claim, $"lc{Guid.NewGuid():N}", Bill("an_uong", 350_000m, confidence: 0.4m));
        var outside = await UploadAsync(claim, $"out{Guid.NewGuid():N}", Bill("cong_tac", 1_200_000m, date: "2026-09-10"));
        var unknown = await UploadAsync(claim, $"unk{Guid.NewGuid():N}", Bill("other", 50_000m));
        var noAmount = await UploadAsync(claim, $"na{Guid.NewGuid():N}", Bill("an_uong", 0m, date: null));
        foreach (var i in new[] { ok, lowConf, outside, unknown, noAmount }) await ProcessAsync(i);

        Assert.Equal(ExpenseClaimItemStatuses.Ready, (await ItemAsync(claim, ok)).Status);
        Assert.Equal([ExpenseClaimItemReviewReasons.LowConfidence], (await ItemAsync(claim, lowConf)).ReviewReasons);
        Assert.Equal([ExpenseClaimItemReviewReasons.OutsideTripDates], (await ItemAsync(claim, outside)).ReviewReasons);
        Assert.Equal([ExpenseClaimItemReviewReasons.CategoryUnknown], (await ItemAsync(claim, unknown)).ReviewReasons);
        Assert.Contains(ExpenseClaimItemReviewReasons.AmountMissing, (await ItemAsync(claim, noAmount)).ReviewReasons);
        Assert.Contains(ExpenseClaimItemReviewReasons.DateMissing, (await ItemAsync(claim, noAmount)).ReviewReasons);
        Assert.Equal(ExpenseClaimItemGate.Unresolved, await GateAsync(claim));
        // Only ready items are in the authoritative total.
        Assert.Equal(600_000m, (await d.Claims().GetByVoucherIdAsync(fx.Tenant, fx.Dept, claim, default))!.ApprovedAmount);
        var preview = (await Intake().GetAsync(fx.Tenant, fx.Dept, claim, default))!.Totals;
        Assert.Null(preview.ExpectedPayable);   // never a "final" payable while items are pending

        // ADMIN confirms (with correction) — audited, original extraction preserved.
        var confirmed = (await Intake().ConfirmItemAsync(fx.Tenant, fx.Dept, claim, lowConf, fx.Admin,
            new ConfirmExpenseClaimItemRequest("an_uong", "Quán LIVE-TEST", null, "BL-1", new DateOnly(2026, 9, 16), 380_000m, null, "VND", "Ăn trưa", "sửa số tiền theo bill"), default))!;
        Assert.Equal((ExpenseClaimItemStatuses.Ready, 380_000m, 350_000m, fx.Admin), (confirmed.Status, confirmed.ConfirmedGrossAmount!.Value, confirmed.ExtractedGrossAmount!.Value, confirmed.ConfirmedBy!.Value));
        Assert.Contains("350000", await d.Scalar<string>("SELECT details::text FROM expense_claim_item_events WHERE item_id=@lowConf AND event='confirmed'", new { lowConf }));
        await Intake().ConfirmItemAsync(fx.Tenant, fx.Dept, claim, outside, fx.Admin,
            new ConfirmExpenseClaimItemRequest("cong_tac", null, null, null, new DateOnly(2026, 9, 10), 1_200_000m, null, null, "Vé mua trước chuyến đi", null), default);
        // Confirmation cannot pick an unconfigured category / foreign currency / bad VAT.
        async Task<string> Refused(ConfirmExpenseClaimItemRequest r) => (await Assert.ThrowsAsync<ExpenseClaimItemException>(() =>
            Intake().ConfirmItemAsync(fx.Tenant, fx.Dept, claim, unknown, fx.Admin, r, default))).Code;
        Assert.Equal("expense_category_not_configured", await Refused(new("other", null, null, null, new DateOnly(2026, 9, 16), 50_000m, null, null, null, null)));
        Assert.Equal("foreign_currency_not_supported", await Refused(new("an_uong", null, null, null, new DateOnly(2026, 9, 16), 50_000m, null, "USD", null, null)));
        Assert.Equal("vat_invalid", await Refused(new("an_uong", null, null, null, new DateOnly(2026, 9, 16), 50_000m, 60_000m, null, null, null)));

        // Exclusion: reason required, file + audit kept, amount not counted.
        Assert.Equal("reason_required", (await Assert.ThrowsAsync<ExpenseClaimItemException>(() =>
            Intake().ExcludeItemAsync(fx.Tenant, fx.Dept, claim, unknown, fx.Admin, " ", default))).Code);
        var excluded = (await Intake().ExcludeItemAsync(fx.Tenant, fx.Dept, claim, unknown, fx.Admin, "Không thuộc chuyến công tác", default))!;
        Assert.Equal((ExpenseClaimItemStatuses.Excluded, "Không thuộc chuyến công tác", fx.Admin), (excluded.Status, excluded.ExcludedReason, excluded.ExcludedBy!.Value));
        await Intake().ExcludeItemAsync(fx.Tenant, fx.Dept, claim, noAmount, fx.Admin, "Bill không đọc được", default);
        Assert.NotNull(await Intake().GetItemFileAsync(fx.Tenant, fx.Dept, claim, unknown, default));
        await Intake().RecomputeAsync(fx.Tenant, fx.Dept, claim, default);
        Assert.Null(await GateAsync(claim));
        Assert.Equal(2_180_000m, (await d.Claims().GetByVoucherIdAsync(fx.Tenant, fx.Dept, claim, default))!.ApprovedAmount); // 600k + 380k + 1.2M
    }

    [Fact]
    public async Task Duplicate_file_or_invoice_is_flagged_for_review_never_silently_counted_twice()
    {
        if (Skip) return;
        var (first, _) = await ClaimAsync();
        var tag = $"dup{Guid.NewGuid():N}";
        var mst = "0301" + Random.Shared.Next(100000, 999999);
        await ProcessAsync(await UploadAsync(first, tag, Bill("khach_san", 2_200_000m, mst: mst, number: "0001234")));
        var (second, _) = await ClaimAsync();
        var sameFile = await UploadAsync(second, tag, null);                                       // identical bytes
        var sameInvoice = await UploadAsync(second, $"inv{Guid.NewGuid():N}", Bill("khach_san", 2_200_000m, mst: mst, number: "0001234"));
        await ProcessAsync(sameFile);
        await ProcessAsync(sameInvoice);
        Assert.Contains(ExpenseClaimItemReviewReasons.DuplicateFile, (await ItemAsync(second, sameFile)).ReviewReasons);
        Assert.Contains(ExpenseClaimItemReviewReasons.DuplicateInvoice, (await ItemAsync(second, sameInvoice)).ReviewReasons);
        Assert.Equal(ExpenseClaimItemGate.Unresolved, await GateAsync(second));
    }

    // ===================================================== RECOGNITION / PAYMENT

    [Fact]
    public async Task Full_flow_recognition_consumes_the_item_derived_totals_and_payment_is_unchanged()
    {
        if (Skip) return;
        var (claim, employee, _) = await TripAsync();
        var claims = d.Claims();
        // Department review is the existing stage (the claimant USER never reviews).
        Assert.Equal("dept_reviewed", (await claims.DepartmentReviewAsync(fx.Tenant, fx.Dept, claim, fx.Admin, default))!.ReviewStatus);
        var recognized = (await claims.AccountingReviewAsync(fx.Tenant, fx.Dept, claim, fx.Admin2, default))!;
        Assert.Equal(("recognized", 3_700_000m, 2_000_000m, 1_700_000m), (recognized.RecognitionStatus, recognized.ApprovedAmount, recognized.AdvanceAmount, recognized.PayableAmount));
        await using (var conn = new NpgsqlConnection(d.Cs))
        {
            await conn.OpenAsync();
            var lines = (await conn.QueryAsync<(string Debit, string Credit, decimal Amount)>(
                "SELECT debit_account, credit_account, amount FROM voucher_journal_suggestions WHERE expense_claim_id=@id AND source='posting'",
                new { id = recognized.Id })).ToList();
            Assert.Equal(2_000_000m, lines.Where(l => l.Credit == "141").Sum(l => l.Amount));   // advance cleared
            Assert.Equal(1_700_000m, lines.Where(l => l.Credit == "334").Sum(l => l.Amount));   // employee payable
            Assert.Equal(3_700_000m, lines.Sum(l => l.Amount));
            Assert.All(lines, l => Assert.Contains(l.Debit, new[] { "642", "641" }));          // trusted policy accounts only
            Assert.Equal(900_000m, lines.Where(l => l.Debit == "641").Sum(l => l.Amount));     // an_uong → 641
        }
        Assert.Equal((0m, 4_700_000m), await Employee(employee));             // advance cleared + YTD, exactly once

        var bank = await d.Scalar<Guid>("""
            INSERT INTO sim_bank_accounts(tenant_id, account_number, bank_name, account_holder, gl_account_code, current_balance)
            VALUES (@t, @n, 'Vietcombank', 'Sáu Trực', '1121', 50000000) RETURNING id
            """, new { t = fx.Tenant, n = "88" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999) });
        var paid = (await d.Payments().PayExpenseClaimAsync(fx.Tenant, fx.Dept, claim, fx.Admin3, new ExpenseClaimPayRequest("bank", bank), default))!;
        Assert.Equal(("paid", "334", "1121", 1_700_000m), (paid.Outcome, paid.Context.Payment!.Journal[0].DebitAccount, paid.Context.Payment.Journal[0].CreditAccount, paid.Context.Payment.Journal[0].Amount));
        Assert.Equal((0m, 4_700_000m), await Employee(employee));             // payment never touches the employee again

        // Recognized → frozen.
        async Task<string> Locked(Func<Task> act) => (await Assert.ThrowsAsync<ExpenseClaimItemException>(act)).Code;
        var item = (await Intake().GetAsync(fx.Tenant, fx.Dept, claim, default))!.Items[0];
        Assert.Equal("expense_claim_locked", await Locked(() => UploadAsync(claim, $"late{Guid.NewGuid():N}", null)));
        Assert.Equal("expense_claim_locked", await Locked(() => Intake().ConfirmItemAsync(fx.Tenant, fx.Dept, claim, item.Id, fx.Admin,
            new ConfirmExpenseClaimItemRequest("cong_tac", null, null, null, new DateOnly(2026, 9, 18), 9_000_000m, null, null, null, null), default)));
        Assert.Equal("expense_claim_locked", await Locked(() => Intake().ExcludeItemAsync(fx.Tenant, fx.Dept, claim, item.Id, fx.Admin, "x", default)));
        Assert.Equal("expense_claim_locked", await Locked(() => Intake().BeginRetryAsync(fx.Tenant, fx.Dept, claim, item.Id, fx.Admin, default)));
        await Intake().RecomputeAsync(fx.Tenant, fx.Dept, claim, default);
        Assert.Equal(3_700_000m, (await claims.GetByVoucherIdAsync(fx.Tenant, fx.Dept, claim, default))!.ApprovedAmount);
        Assert.True((await Intake().GetAsync(fx.Tenant, fx.Dept, claim, default))!.Locked);
    }

    [Fact]
    public async Task Exact_advance_is_recognized_against_141_only_with_no_cash_due()
    {
        if (Skip) return;
        var (claim, employee) = await ClaimAsync(advance: 2_000_000m);
        await ProcessAsync(await UploadAsync(claim, $"ex{Guid.NewGuid():N}", Bill("cong_tac", 2_000_000m)));
        await d.Claims().DepartmentReviewAsync(fx.Tenant, fx.Dept, claim, fx.Admin, default);
        var r = (await d.Claims().AccountingReviewAsync(fx.Tenant, fx.Dept, claim, fx.Admin2, default))!;
        Assert.Equal((0m, "not_due"), (r.PayableAmount, r.PaymentStatus));
        Assert.Equal("no_payment_due", (await Assert.ThrowsAsync<CashSettlementException>(() =>
            d.Payments().PayExpenseClaimAsync(fx.Tenant, fx.Dept, claim, fx.Admin3, new ExpenseClaimPayRequest("cash", null), default))).Code);
        Assert.Equal((0m, 3_000_000m), await Employee(employee));
    }

    [Fact]
    public async Task Recognition_is_blocked_while_items_are_unresolved_and_pay_before_recognition_is_blocked()
    {
        if (Skip) return;
        var (claim, _) = await ClaimAsync();
        await ProcessAsync(await UploadAsync(claim, $"a{Guid.NewGuid():N}", Bill("cong_tac", 600_000m)));
        await d.Claims().DepartmentReviewAsync(fx.Tenant, fx.Dept, claim, fx.Admin, default);
        await UploadAsync(claim, $"late{Guid.NewGuid():N}", null);   // still processing
        var ex = await Assert.ThrowsAsync<ExpenseRecognitionException>(() => d.Claims().AccountingReviewAsync(fx.Tenant, fx.Dept, claim, fx.Admin2, default));
        Assert.Equal(ExpenseClaimItemGate.Unresolved, ex.Code);
        Assert.Contains((await d.Payments().GetExpenseClaimPaymentAsync(fx.Tenant, fx.Dept, claim, fx.Admin3, default))!.Blockers,
            b => b.Code == "expense_not_recognized");
    }

    [Fact]
    public async Task Same_admin_completes_the_whole_multi_file_claim_lifecycle()
    {
        if (Skip) return;
        // ADMIN Department Authority Invariant: ONE department ADMIN creates the
        // claim, uploads the bills, resolves items, approves every stage and pays.
        var admin = fx.Admin;
        var (claim, employee) = await ClaimAsync(advance: 2_000_000m, creator: admin);
        var mst = "0304" + Random.Shared.Next(100000, 999999);
        var bus = await UploadAsync(claim, $"sbus{Guid.NewGuid():N}", Bill("cong_tac", 600_000m), actor: admin);
        var hotel = await UploadAsync(claim, $"shot{Guid.NewGuid():N}", Bill("khach_san", 2_200_000m, vat: 200_000m, mst: mst, number: "0009999"), actor: admin);
        var meal = await UploadAsync(claim, $"smeal{Guid.NewGuid():N}", Bill("an_uong", 900_000m, confidence: 0.4m), actor: admin);
        foreach (var i in new[] { bus, hotel, meal }) await ProcessAsync(i);
        Assert.Equal(ExpenseClaimItemStatuses.NeedsReview, (await ItemAsync(claim, meal)).Status);

        // The creator ADMIN resolves its own item.
        await Intake().ConfirmItemAsync(fx.Tenant, fx.Dept, claim, meal, admin,
            new ConfirmExpenseClaimItemRequest("an_uong", "Nhà hàng", null, "BL-9", new DateOnly(2026, 9, 18), 900_000m, null, "VND", "Ăn tối", null), default);
        await Intake().RecomputeAsync(fx.Tenant, fx.Dept, claim, default);
        Assert.Null(await GateAsync(claim));

        var claims = d.Claims();
        Assert.Equal("dept_reviewed", (await claims.DepartmentReviewAsync(fx.Tenant, fx.Dept, claim, admin, default))!.ReviewStatus);
        await d.Tax().ConfirmAsync(fx.Tenant, fx.Dept, claim, admin, new TaxConfirmRequest(TaxVerificationSources.TctPortal, "Đã tra cứu cổng hoá đơn", true,
            [new TaxAttestedInvoice(mst, null, "0009999", new DateOnly(2026, 9, 18), 2_200_000m, "L")]), default);
        var r = (await claims.AccountingReviewAsync(fx.Tenant, fx.Dept, claim, admin, default))!;
        Assert.Equal(("recognized", 3_700_000m, 2_000_000m, 1_700_000m, admin, admin),
            (r.RecognitionStatus, r.ApprovedAmount, r.AdvanceAmount, r.PayableAmount, r.DeptReviewedBy!.Value, r.RecognizedBy!.Value));
        var paid = (await d.Payments().PayExpenseClaimAsync(fx.Tenant, fx.Dept, claim, admin, new ExpenseClaimPayRequest("cash", null), default))!;
        Assert.Equal(("paid", 1_700_000m), (paid.Outcome, paid.Context.Payment!.Amount));
        Assert.Equal((0m, 4_700_000m), await Employee(employee));
        Assert.Equal(admin, await d.Scalar<Guid>("SELECT user_id FROM vouchers WHERE id=@claim", new { claim }));
    }

    // ======================================================== TAX / NO-LLM AUTHORITY

    [Fact]
    public async Task Extracted_tax_or_account_claims_have_no_authority()
    {
        if (Skip) return;
        var (claim, _) = await ClaimAsync();
        var mst = "0302" + Random.Shared.Next(100000, 999999);
        var item = await UploadAsync(claim, $"inj{Guid.NewGuid():N}", Bill("khach_san", 2_200_000m, vat: 200_000m, mst: mst, number: "0007777",
            extra: "\"gl_account\":\"811\",\"tax_verified\":true,\"payable_amount\":1,\"tax_verification_status\":\"verified\""));
        await ProcessAsync(item);
        Assert.Equal(ExpenseClaimItemStatuses.Ready, (await ItemAsync(claim, item)).Status);
        var result = (await d.Claims().GetByVoucherIdAsync(fx.Tenant, fx.Dept, claim, default))!;
        Assert.True(result.RequiresTaxVerification);            // a real invoice (tax code + number) → tax gate applies
        Assert.Null(await d.ScalarOrNull<string?>("SELECT tax_verification_status FROM vouchers WHERE id=@claim", new { claim }));
        await d.Claims().DepartmentReviewAsync(fx.Tenant, fx.Dept, claim, fx.Admin, default);
        Assert.Equal("tax_verification_required", (await Assert.ThrowsAsync<ExpenseRecognitionException>(() =>
            d.Claims().AccountingReviewAsync(fx.Tenant, fx.Dept, claim, fx.Admin2, default))).Code);

        // The existing ADMIN attestation of exactly the item invoice unlocks recognition — accounts from policy only.
        await d.Tax().ConfirmAsync(fx.Tenant, fx.Dept, claim, fx.Admin, new TaxConfirmRequest(
            TaxVerificationSources.TctPortal, "Đã tra cứu cổng hoá đơn", true,
            [new TaxAttestedInvoice(mst, null, "0007777", new DateOnly(2026, 9, 18), 2_200_000m, "L")]), default);
        var r = (await d.Claims().AccountingReviewAsync(fx.Tenant, fx.Dept, claim, fx.Admin2, default))!;
        Assert.Equal("recognized", r.RecognitionStatus);
        var accounts = await d.Scalar<string>("SELECT string_agg(DISTINCT debit_account, ',' ORDER BY debit_account) FROM voucher_journal_suggestions WHERE expense_claim_id=@id AND source='posting'", new { id = r.Id });
        Assert.DoesNotContain("811", accounts);
        Assert.Equal("1331,642", accounts);
    }

    [Fact]
    public async Task Changing_items_after_tax_attestation_makes_it_stale()
    {
        if (Skip) return;
        var (claim, _) = await ClaimAsync();
        var mst = "0303" + Random.Shared.Next(100000, 999999);
        var item = await UploadAsync(claim, $"tx{Guid.NewGuid():N}", Bill("khach_san", 1_000_000m, mst: mst, number: "0008888"));
        await ProcessAsync(item);
        await d.Tax().ConfirmAsync(fx.Tenant, fx.Dept, claim, fx.Admin, new TaxConfirmRequest(TaxVerificationSources.TctPortal, "Đã tra cứu cổng hoá đơn", true,
            [new TaxAttestedInvoice(mst, null, "0008888", new DateOnly(2026, 9, 18), 1_000_000m, "L")]), default);
        await Intake().ConfirmItemAsync(fx.Tenant, fx.Dept, claim, item, fx.Admin,
            new ConfirmExpenseClaimItemRequest("khach_san", null, mst, "0008888", new DateOnly(2026, 9, 18), 1_500_000m, null, null, null, null), default);
        await Intake().RecomputeAsync(fx.Tenant, fx.Dept, claim, default);
        await d.Claims().DepartmentReviewAsync(fx.Tenant, fx.Dept, claim, fx.Admin, default);
        Assert.Equal("tax_verification_stale", (await Assert.ThrowsAsync<ExpenseRecognitionException>(() =>
            d.Claims().AccountingReviewAsync(fx.Tenant, fx.Dept, claim, fx.Admin2, default))).Code);
    }

    [Fact]
    public async Task Legacy_single_document_claims_are_unaffected_by_the_item_gate()
    {
        if (Skip) return;
        var legacy = await d.CreateAsync(stage: "dept_reviewed");
        Assert.Null(await GateAsync(legacy.Voucher));
        await using var conn = new NpgsqlConnection(d.Cs);
        await conn.OpenAsync();
        Assert.False(await ExpenseClaimItemGate.IsMultiFileAsync(conn, legacy.Voucher, default));
        Assert.Equal("recognized", (await d.RecognizeAsync(legacy))!.RecognitionStatus);
    }

    private async Task<(decimal Advance, decimal Ytd)> Employee(Guid id)
    {
        await using var conn = new NpgsqlConnection(d.Cs);
        await conn.OpenAsync();
        return await conn.QuerySingleAsync<(decimal, decimal)>("SELECT outstanding_advance, ytd_expense FROM sim_employees WHERE id=@id", new { id });
    }

    // ================================================================ fakes

    private sealed class MemoryStorage : IObjectStorage
    {
        public ConcurrentDictionary<string, byte[]> Objects { get; } = new();
        public async Task<PutObjectResult> PutAsync(string bucket, string key, Stream content, string? contentType, CancellationToken ct)
        {
            await using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            Objects[key] = ms.ToArray();
            return new(bucket, key, ms.Length, "", null);
        }
        public Task<Stream> GetAsync(string bucket, string key, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream(Objects[key]));
        public Task<string> GetSignedUrlAsync(string bucket, string key, TimeSpan? expiry, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string bucket, string key, CancellationToken ct) => throw new NotSupportedException("evidence is never deleted");
        public Task<bool> IsHealthyAsync(CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class ScriptedAgent : IAgentClient
    {
        public ConcurrentDictionary<string, string> Answers { get; } = new();
        public ConcurrentDictionary<string, Exception> Failures { get; } = new();
        private int _calls;
        public int ExtractionCalls => _calls;
        public (Guid Agent, Guid Version, bool Pinned)? LastPinned { get; private set; }
        public string LastPrompt { get; private set; } = "";

        public Task<AgentOcrResponse> RunOcrPipelineAsync(Stream s, string f, CancellationToken ct) => Ocr(s);
        public Task<AgentOcrResponse> RunOcrImageAsync(Stream s, string f, string c, CancellationToken ct) => Ocr(s);
        private static async Task<AgentOcrResponse> Ocr(Stream s)
        {
            using var r = new StreamReader(s);
            return new AgentOcrResponse(1, 0.97, new(), [], await r.ReadToEndAsync());
        }
        private AgentTaskResult Answer(string prompt, Guid? agentId, Guid? versionId, int? version)
        {
            Interlocked.Increment(ref _calls);
            LastPrompt = prompt;
            var tag = Regex.Match(prompt, @"BILL:(\w+)").Groups[1].Value;
            if (Failures.TryGetValue(tag, out var ex)) throw ex;
            return new(Answers.TryGetValue(tag, out var a) ? a : "không đọc được", agentId, versionId, version, "test", "test");
        }
        public Task<AgentTaskResult> RunAgentTaskWithProvenanceAsync(string task, string token, string capability, Guid? preferred, int maxTurns, CancellationToken ct) =>
            Task.FromResult(Answer(task, null, null, null));
        public Task<AgentTaskResult> RunDocumentExtractionAsync(Guid agentId, Guid expectedVersionId, bool test, string task, string userToken, CancellationToken ct, bool pinVersion = false)
        {
            LastPinned = (agentId, expectedVersionId, pinVersion);
            return Task.FromResult(Answer(task, agentId, expectedVersionId, 7));
        }
        public Task<string> RunAgentTaskAsync(string task, string token, string capability, int maxTurns, CancellationToken ct) => throw new NotSupportedException();
        public Task<AgentDocumentExtractResponse> ExtractDocumentTextAsync(Stream s, string f, string c, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>A published Agent version whose configuration enables expense_claim; everything else is the real repository.</summary>
    private sealed class ManifestDocs(IDocumentProcessingRepository inner) : IDocumentProcessingRepository
    {
        public Guid AgentId { get; } = Guid.NewGuid();
        public Guid VersionId { get; } = Guid.NewGuid();
        public string? PinnedDenial { get; set; }
        public int PinnedCalls { get; private set; }
        public int UploaderCalls { get; private set; }
        private ManifestDocumentProcessing Config => new(VersionId, false,
        [
            new ManifestDocumentType(VoucherTypes.ExpenseClaim, "Đề nghị hoàn ứng", true, AccountingHandlers.ExpenseClaim, [],
                JsonDocument.Parse("{}").RootElement, "sha", "HUONG DAN CAU HINH", null, []),
        ]);
        public Task<DocumentAgentResolution> ResolveForUploaderAsync(Guid t, Guid d, Guid u, Guid? r, CancellationToken ct)
        {
            UploaderCalls++;
            return Task.FromResult(new DocumentAgentResolution(AgentId, VersionId, 7, "Trợ lý kế toán", Config, null));
        }
        public Task<DocumentAgentResolution> ResolvePinnedAsync(Guid t, Guid d, Guid? a, Guid? av, CancellationToken ct)
        {
            PinnedCalls++;
            return Task.FromResult(PinnedDenial is { } denial
                ? new DocumentAgentResolution(a, av, 7, null, null, denial) { Pinned = true }
                : new DocumentAgentResolution(AgentId, VersionId, 7, "Trợ lý kế toán", Config, null) { Pinned = true });
        }
        public Task<Guid> RecordRunAsync(DocumentProcessingRun run, CancellationToken ct) =>
            inner.RecordRunAsync(run with { AgentId = null, AgentVersionId = null }, ct);   // fake ids are not real agents rows
        public Task<DepartmentDocumentConfig?> GetDepartmentVersionConfigAsync(Guid t, Guid d, Guid a, Guid v, CancellationToken ct) => inner.GetDepartmentVersionConfigAsync(t, d, a, v, ct);
        public Task<DocumentConfigSaveResult> ReplaceDepartmentDraftConfigAsync(Guid t, Guid d, Guid a, Guid v, Guid actor, IReadOnlyList<DocumentTypeConfigInput>? types, CancellationToken ct) => inner.ReplaceDepartmentDraftConfigAsync(t, d, a, v, actor, types, ct);
        public Task<DocumentTestTarget?> GetDepartmentTestTargetAsync(Guid t, Guid d, Guid a, CancellationToken ct) => inner.GetDepartmentTestTargetAsync(t, d, a, ct);
        public Task<Guid?> GetRequestedAgentAsync(Guid v, CancellationToken ct) => inner.GetRequestedAgentAsync(v, ct);
        public Task<DocumentProvenance> GetProvenanceAsync(Guid t, Guid v, CancellationToken ct) => inner.GetProvenanceAsync(t, v, ct);
        public Task SetExtractionFailureAsync(Guid t, Guid v, string c, string s, CancellationToken ct) => inner.SetExtractionFailureAsync(t, v, c, s, ct);
        public Task<ExtractionFailureState?> GetExtractionFailureAsync(Guid t, Guid v, CancellationToken ct) => inner.GetExtractionFailureAsync(t, v, ct);
        public Task SetProcessingBlockerAsync(Guid t, Guid v, string b, bool active, CancellationToken ct) => inner.SetProcessingBlockerAsync(t, v, b, active, ct);
        public Task<bool> IsActiveTenantUserAsync(Guid t, Guid u, CancellationToken ct) => inner.IsActiveTenantUserAsync(t, u, ct);
        public Task SetRequestedAgentAsync(Guid t, Guid v, Guid a, CancellationToken ct) => inner.SetRequestedAgentAsync(t, v, a, ct);
        public Task<string?> GetDepartmentVoucherRawTextAsync(Guid t, Guid d, Guid v, CancellationToken ct) => inner.GetDepartmentVoucherRawTextAsync(t, d, v, ct);
        public Task SetVoucherProvenanceAsync(Guid t, Guid v, Guid? a, Guid? av, string s, CancellationToken ct) => inner.SetVoucherProvenanceAsync(t, v, a, av, s, ct);
    }
}
