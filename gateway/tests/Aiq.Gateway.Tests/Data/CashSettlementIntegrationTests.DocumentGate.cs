using Aiq.Gateway.Payments;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Bank-statement line settlement honours the ONE document accounting gate
/// (document review status + extraction-owned processing blockers): lines stay
/// extractable / matchable / viewable, but post / allocate / reconcile are
/// refused with document_review_required and leave zero accounting effect.
/// </summary>
public sealed partial class CashSettlementIntegrationTests
{
    private async Task<string> GateSnapshotAsync() =>
        string.Join("|", await AccountingSnapshotAsync(),
            await Scalar<string>("SELECT COALESCE(string_agg(current_balance::text, ',' ORDER BY id), '') FROM sim_bank_accounts"),
            await Scalar<long>("SELECT COUNT(*) FROM sim_bank_transactions WHERE reconciled_cash_transaction_id IS NOT NULL"));

    private Task<int> BlockStatementAsync(Guid v, string? reviewStatus, string[]? blockers = null) =>
        Scalar<int>("UPDATE vouchers SET document_review_status=@reviewStatus, processing_blockers=@blockers WHERE id=@v RETURNING 1",
            new { v, reviewStatus, blockers = blockers ?? Array.Empty<string>() });

    public static TheoryData<string?, string?, string> GateStates => new()
    {
        { "awaiting_review", null, "document_review:awaiting_review" },
        { "rejected", null, "document_review:rejected" },
        { "review_task_creation_failed", null, "review_task_creation_failed" },
        { null, "legacy_l2_failure", "legacy_l2_failure" },
        { "approved", "legacy_l2_failure", "legacy_l2_failure" },
    };

    [Theory]
    [MemberData(nameof(GateStates))]
    public async Task StatementGate_BlocksPost_ButLineStaysMatchable(string? reviewStatus, string? blocker, string reason)
    {
        if (_cs is null) return;
        await SeedCompanyBankAccountAsync();
        var (vt, inv) = await VietTinWithInvoiceAsync();
        var (v, rows) = await ImportStatementAsync("SK-G1", Csv("10/10/2026,FT7001,TT DEMO-INV-VT-001,123456789,CÔNG TY TNHH VIỆT TÍN,VCB,20000000,"));
        await BlockStatementAsync(v, reviewStatus, blocker is null ? null : [blocker]);

        // Extracted lines are still matched and viewable.
        var ctx = (await Settle().GetStatementLineContextAsync(_tenantId, _departmentId, v, rows[0].Id, default))!;
        Assert.Equal(InvoiceMatchStatuses.AutoMatched, ctx.InvoiceMatch.Status);
        Assert.Equal(inv, Assert.Single(ctx.InvoiceMatch.Suggestion!.Allocations).InvoiceId);
        Assert.Contains("document_review_required:" + reason, ctx.Blockers);
        Assert.Single((await Statements().ListAsync(_tenantId, _departmentId, v, default))!);

        var before = await GateSnapshotAsync();
        foreach (var req in new[] { new PostStatementLineRequest(null, false, false), new PostStatementLineRequest(null, true, true),
                     new PostStatementLineRequest(new[] { new AllocationInput(inv, 20_000_000m) }, false, true) })
        {
            var ex = await Assert.ThrowsAsync<CashSettlementException>(() =>
                Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, req, default));
            Assert.Equal("document_review_required", ex.Code);
        }
        Assert.Equal(before, await GateSnapshotAsync());
        Assert.Equal((0m, "open"), await ArInvoiceAsync(inv));
        Assert.Equal(StatementAccountingStatuses.NotPosted,
            (await Settle().GetStatementLineContextAsync(_tenantId, _departmentId, v, rows[0].Id, default))!.AccountingStatus);
    }

    [Fact]
    public async Task StatementGate_ConcurrentBlockedPosts_ZeroEffect_ThenApprovalReleases()
    {
        if (_cs is null) return;
        var bank = await SeedCompanyBankAccountAsync();
        var (vt, inv) = await VietTinWithInvoiceAsync();
        var (v, rows) = await ImportStatementAsync("SK-G2", Csv("10/10/2026,FT7002,TT DEMO-INV-VT-001,123456789,CÔNG TY TNHH VIỆT TÍN,VCB,20000000,"));
        await BlockStatementAsync(v, "awaiting_review");
        var before = await GateSnapshotAsync();
        var arBefore = await ArAsync(vt);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            try { await Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, new PostStatementLineRequest(null, false, false), default); return "posted"; }
            catch (CashSettlementException ex) { return ex.Code; }
        }));
        Assert.All(attempts, code => Assert.Equal("document_review_required", code));
        Assert.Equal(before, await GateSnapshotAsync());
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM cash_transactions WHERE voucher_id=@v", new { v }));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM payment_applications"));
        Assert.Equal(arBefore, await ArAsync(vt));

        // Review released (approved) -> the SAME line posts exactly once.
        await BlockStatementAsync(v, "approved");
        var posted = (await Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, new PostStatementLineRequest(null, false, false), default))!;
        Assert.Equal("posted", posted.Outcome);
        Assert.Equal((20_000_000m, "partial"), await ArInvoiceAsync(inv));
        Assert.Equal(arBefore - 20_000_000m, await ArAsync(vt));
        Assert.Equal(120_000_000m, await Scalar<decimal>("SELECT current_balance FROM sim_bank_accounts WHERE id=@bank", new { bank }));
    }

    [Theory]
    [InlineData("awaiting_review")]
    [InlineData("rejected")]
    [InlineData("review_task_creation_failed")]
    public async Task StatementGate_BlocksAllocate_ButCorrectiveReverseStillAllowed(string reviewStatus)
    {
        if (_cs is null) return;
        await SeedCompanyBankAccountAsync();
        var (vt, inv) = await VietTinWithInvoiceAsync();
        var (v, rows) = await ImportStatementAsync("SK-G3", Csv("10/10/2026,FT7003,VIET TIN CK,123456789,CÔNG TY TNHH VIỆT TÍN,VCB,20000000,"));
        // Posted unapplied while the document was clean, THEN a review gate lands on it.
        var posted = (await Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, new PostStatementLineRequest(null, true, false), default))!;
        Assert.Equal(20_000_000m, posted.Posting!.UnappliedAmount);
        await BlockStatementAsync(v, reviewStatus);
        var before = await GateSnapshotAsync();

        var ex = await Assert.ThrowsAsync<CashSettlementException>(() => Settle().AllocateStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId,
            new[] { new AllocationInput(inv, 20_000_000m) }, default));
        Assert.Equal("document_review_required", ex.Code);
        Assert.Equal(before, await GateSnapshotAsync());
        Assert.Equal((0m, "open"), await ArInvoiceAsync(inv));

        // Reverse is corrective and deliberately NOT gated.
        Assert.Equal("reversed", (await Settle().ReverseStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, "sai chứng từ", default))!.Outcome);
    }

    [Theory]
    [InlineData("awaiting_review")]
    [InlineData("rejected")]
    [InlineData("review_task_creation_failed")]
    public async Task StatementGate_BlocksReconcile(string reviewStatus)
    {
        if (_cs is null) return;
        await SeedCompanyBankAccountAsync();
        var (vt, inv) = await VietTinWithInvoiceAsync();
        var pt = await ReceiptVoucherAsync("PT-GATE-R", 20_000_000m, "Công ty TNHH Việt Tín", "0307890123", "DEMO-INV-VT-001");
        var receipt = (await Settlement().PostAsync(_tenantId, _departmentId, pt, _adminId, Approve(), default))!;
        var (v, rows) = await ImportStatementAsync("SK-G4", Csv($"{Today:dd/MM/yyyy},FT7004,TT DEMO-INV-VT-001,123456789,CÔNG TY TNHH VIỆT TÍN,VCB,20000000,"));
        await BlockStatementAsync(v, reviewStatus);
        var ctx = (await Settle().GetStatementLineContextAsync(_tenantId, _departmentId, v, rows[0].Id, default))!;
        Assert.Equal(receipt.Posting!.Id, Assert.Single(ctx.Duplicates).CashTransactionId); // still matched for review
        var before = await GateSnapshotAsync();

        var ex = await Assert.ThrowsAsync<CashSettlementException>(() =>
            Settle().ReconcileStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, receipt.Posting.Id, default));
        Assert.Equal("document_review_required", ex.Code);
        Assert.Equal(before, await GateSnapshotAsync());
        Assert.Null(await Scalar<Guid?>("SELECT reconciled_cash_transaction_id FROM sim_bank_transactions WHERE id=@id", new { id = rows[0].Id }));

        await BlockStatementAsync(v, "approved");
        Assert.Equal(StatementAccountingStatuses.Reconciled,
            (await Settle().ReconcileStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, receipt.Posting.Id, default))!.AccountingStatus);
    }

    [Fact]
    public async Task StatementGate_NoReviewConfigured_LegacyBehaviourUnchanged()
    {
        if (_cs is null) return;
        await SeedCompanyBankAccountAsync();
        var (vt, inv) = await VietTinWithInvoiceAsync();
        var (v, rows) = await ImportStatementAsync("SK-G5", Csv("10/10/2026,FT7005,TT DEMO-INV-VT-001,123456789,CÔNG TY TNHH VIỆT TÍN,VCB,20000000,"));
        Assert.Null(await Scalar<string?>("SELECT document_review_status FROM vouchers WHERE id=@v", new { v }));
        Assert.Empty((await Settle().GetStatementLineContextAsync(_tenantId, _departmentId, v, rows[0].Id, default))!.Blockers);
        Assert.Equal("posted", (await Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, new PostStatementLineRequest(null, false, false), default))!.Outcome);
    }
}
