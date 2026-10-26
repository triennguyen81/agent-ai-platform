using Aiq.Gateway.Contracts;
using Aiq.Gateway.Jobs;
using Aiq.Gateway.Payments;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Contract payment schedule reconciliation from the canonical cash settlement:
/// payment_application -> invoice -> its explicit contract_payment_schedule_id
/// -> schedule status -> contract progress, for every AR path (Phiếu thu,
/// bank statement money_in, allocate, unapply, reallocate, reverse). Never
/// customer/MST -> "some open contract".
/// </summary>
public sealed partial class CashSettlementIntegrationTests
{
    private sealed record SeededContract(Guid Id, Guid[] Schedules);

    private async Task<SeededContract> SeedContractAsync(string number, params (decimal Total, decimal Percentage, int? DueInDays)[] schedules)
    {
        var id = await Scalar<Guid>(
            "INSERT INTO contracts(tenant_id, department_id, title, contract_number, penalty_percent) VALUES (@t,@d,@number,@number,0.05) RETURNING id",
            new { t = _tenantId, d = _departmentId, number });
        var ids = new List<Guid>();
        for (var i = 0; i < schedules.Length; i++)
        {
            var (total, pct, due) = schedules[i];
            ids.Add(await Scalar<Guid>("""
                INSERT INTO contract_payment_schedules(contract_id, tenant_id, department_id, ordinal, description, percentage,
                    subtotal_amount, vat_amount, total_amount, due_date)
                VALUES (@id, @t, @d, @ord, @desc, @pct, @total, 0, @total, @due) RETURNING id
                """, new { id, t = _tenantId, d = _departmentId, ord = i + 1, desc = $"Đợt {i + 1}", pct, total,
                           due = due is { } days ? Today.AddDays(days) : (DateOnly?)null }));
            // Trigger-based milestone N+1 — the existing "đợt N paid -> mốc N+1 active" rule.
            await Scalar<int>("""
                INSERT INTO contract_milestones(contract_id, tenant_id, department_id, ordinal, name, trigger_event, duration_months)
                VALUES (@id, @t, @d, @ord, @name, 'payment', 2) RETURNING 1
                """, new { id, t = _tenantId, d = _departmentId, ord = i + 2, name = $"Mốc {i + 2}" });
        }
        return new SeededContract(id, ids.ToArray());
    }

    private async Task<Guid> BookLinkedInvoiceAsync(Guid customerId, string doc, decimal amount, Guid? contractId, Guid? scheduleId)
    {
        await Customers().BookInvoiceAsync(_tenantId, customerId, Guid.NewGuid(), doc, Today.AddDays(-5), Today.AddDays(30), amount, contractId, scheduleId, default);
        return await Scalar<Guid>("SELECT id FROM sim_ar_ledger WHERE customer_id=@customerId AND document_number=@doc AND document_type='invoice'", new { customerId, doc });
    }

    private Task<int> OverdueDaysAsync(Guid id) => Scalar<int>("SELECT CURRENT_DATE - due_date FROM contract_payment_schedules WHERE id=@id", new { id });
    private Task<string> ScheduleStatusAsync(Guid id) => Scalar<string>("SELECT status FROM contract_payment_schedules WHERE id=@id", new { id });
    private Task<decimal> ProgressAsync(Guid id) => Scalar<decimal>("SELECT progress_percent FROM contracts WHERE id=@id", new { id });
    private Task<string> MilestoneStatusAsync(Guid contractId, int ordinal) =>
        Scalar<string>("SELECT status FROM contract_milestones WHERE contract_id=@contractId AND ordinal=@ordinal", new { contractId, ordinal });
    private Task<int> HistoryCountAsync(Guid contractId) =>
        Scalar<int>("SELECT COUNT(*) FROM contract_history WHERE contract_id=@contractId AND event_type='payment_schedule_status'", new { contractId });

    private static PostCashRequest Allocate(params (Guid Invoice, decimal Amount)[] a) =>
        new(null, a.Select(x => new AllocationInput(x.Invoice, x.Amount)).ToList(), false, null);

    private async Task<SettlementMutationResult> ReceiveAsync(string doc, decimal amount, params (Guid Invoice, decimal Amount)[] allocations)
    {
        var v = await ReceiptVoucherAsync(doc, amount, "Công ty TNHH Việt Tín", "0307890123");
        var r = (await Settlement().PostAsync(_tenantId, _departmentId, v, _adminId, Allocate(allocations), default))!;
        Assert.Equal("posted", r.Outcome);
        return r;
    }

    private async Task<string> ContractStateAsync()
    {
        await using var c = new NpgsqlConnection(_cs);
        await c.OpenAsync();
        var s = await c.QueryAsync<string>("SELECT id || ':' || status || ':' || COALESCE(penalty_amount::text,'-') FROM contract_payment_schedules ORDER BY id");
        var k = await c.QueryAsync<string>("SELECT id || ':' || progress_percent FROM contracts ORDER BY id");
        var m = await c.QueryAsync<string>("SELECT id || ':' || status FROM contract_milestones ORDER BY id");
        var h = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contract_history");
        return string.Join("|", s.Concat(k).Concat(m)) + "|h" + h;
    }

    // CASE 1 + 2 — partial, then full ---------------------------------------

    [Fact]
    public async Task Schedule_Case1And2_PartialThenFull_ProgressCountsOnlyPaidSchedules()
    {
        if (_cs is null) return;
        var vt = await SeedCustomerAsync("CUS-VT", "Công ty TNHH Việt Tín", "0307890123");
        var k = await SeedContractAsync("HDMB-2026-001", (40_000_000m, 40m, null), (60_000_000m, 60m, null));
        var inv = await BookLinkedInvoiceAsync(vt, "INV-HD-001", 40_000_000m, k.Id, k.Schedules[0]);

        await ReceiveAsync("PT-HD-1", 20_000_000m, (inv, 20_000_000m));
        Assert.Equal((20_000_000m, "partial"), await ArInvoiceAsync(inv));
        Assert.Equal("partial", await ScheduleStatusAsync(k.Schedules[0]));
        Assert.Equal("pending", await ScheduleStatusAsync(k.Schedules[1]));
        Assert.Equal(0m, await ProgressAsync(k.Id)); // existing semantics: a partial schedule contributes nothing
        Assert.Equal("pending", await MilestoneStatusAsync(k.Id, 2));

        var second = await ReceiveAsync("PT-HD-2", 20_000_000m, (inv, 20_000_000m));
        Assert.Equal((40_000_000m, "paid"), await ArInvoiceAsync(inv));
        Assert.Equal("paid", await ScheduleStatusAsync(k.Schedules[0]));
        Assert.Equal("pending", await ScheduleStatusAsync(k.Schedules[1]));
        Assert.Equal(40m, await ProgressAsync(k.Id));
        Assert.Equal("active", await MilestoneStatusAsync(k.Id, 2)); // existing rule preserved
        Assert.Equal("pending", await MilestoneStatusAsync(k.Id, 3));

        // Attributable through the settlement: pending->partial, partial->paid.
        Assert.Equal(2, await HistoryCountAsync(k.Id));
        var last = await Scalar<string>("""
            SELECT from_state || '>' || to_state || '|' || (details->>'cash_transaction_id') || '|' || (details->>'cause')
            FROM contract_history WHERE contract_id=@id ORDER BY created_at DESC, to_state LIMIT 1
            """, new { id = k.Id });
        Assert.Equal($"partial>paid|{second.Posting!.Id}|posted", last);
    }

    // CASE 3 — two invoices, one schedule -----------------------------------

    [Fact]
    public async Task Schedule_Case3_TwoInvoicesSameSchedule_OnePaidInvoiceDoesNotPayTheSchedule()
    {
        if (_cs is null) return;
        var vt = await SeedCustomerAsync("CUS-VT", "Công ty TNHH Việt Tín", "0307890123");
        var k = await SeedContractAsync("HDMB-2026-003", (40_000_000m, 40m, null), (60_000_000m, 60m, null));
        var a = await BookLinkedInvoiceAsync(vt, "INV-A", 25_000_000m, k.Id, k.Schedules[0]);
        var b = await BookLinkedInvoiceAsync(vt, "INV-B", 15_000_000m, k.Id, k.Schedules[0]);

        await ReceiveAsync("PT-A", 25_000_000m, (a, 25_000_000m));
        Assert.Equal((25_000_000m, "paid"), await ArInvoiceAsync(a));
        Assert.Equal((0m, "open"), await ArInvoiceAsync(b));
        Assert.Equal("partial", await ScheduleStatusAsync(k.Schedules[0]));
        Assert.Equal(0m, await ProgressAsync(k.Id));

        await ReceiveAsync("PT-B", 15_000_000m, (b, 15_000_000m));
        Assert.Equal("paid", await ScheduleStatusAsync(k.Schedules[0]));
        Assert.Equal(40m, await ProgressAsync(k.Id));
    }

    // CASE 4 — one payment, two schedules -----------------------------------

    [Fact]
    public async Task Schedule_Case4_OnePayment_TwoInvoices_TwoSchedules_BothRecomputed()
    {
        if (_cs is null) return;
        var vt = await SeedCustomerAsync("CUS-VT", "Công ty TNHH Việt Tín", "0307890123");
        var k = await SeedContractAsync("HDMB-2026-004", (20_000_000m, 40m, null), (30_000_000m, 60m, null));
        var a = await BookLinkedInvoiceAsync(vt, "INV-A", 20_000_000m, k.Id, k.Schedules[0]);
        var b = await BookLinkedInvoiceAsync(vt, "INV-B", 30_000_000m, k.Id, k.Schedules[1]);

        var r = await ReceiveAsync("PT-AB", 50_000_000m, (a, 20_000_000m), (b, 30_000_000m));
        Assert.Equal(2, r.Applications.Count);
        Assert.Equal("paid", await ScheduleStatusAsync(k.Schedules[0]));
        Assert.Equal("paid", await ScheduleStatusAsync(k.Schedules[1]));
        Assert.Equal(100m, await ProgressAsync(k.Id));
    }

    // CASE 5 — same customer, two contracts: no MST heuristic ---------------

    [Fact]
    public async Task Schedule_Case5_SameCustomerTwoContracts_OnlyTheAllocatedInvoicesContractChanges()
    {
        if (_cs is null) return;
        var vt = await SeedCustomerAsync("CUS-VT", "Công ty TNHH Việt Tín", "0307890123");
        // Contract A has the EARLIEST open schedule — exactly what the old
        // "customer MST -> earliest open schedule" heuristic would have picked.
        var ka = await SeedContractAsync("HDMB-A", (20_000_000m, 50m, -30), (20_000_000m, 50m, null));
        var kb = await SeedContractAsync("HDMB-B", (20_000_000m, 100m, 60));
        await BookLinkedInvoiceAsync(vt, "INV-KA-1", 20_000_000m, ka.Id, ka.Schedules[0]);
        var ib = await BookLinkedInvoiceAsync(vt, "INV-KB-1", 20_000_000m, kb.Id, kb.Schedules[0]);

        await ReceiveAsync("PT-KB", 20_000_000m, (ib, 20_000_000m));
        Assert.Equal("paid", await ScheduleStatusAsync(kb.Schedules[0]));
        Assert.Equal(100m, await ProgressAsync(kb.Id));

        Assert.Equal("pending", await ScheduleStatusAsync(ka.Schedules[0]));
        Assert.Equal("pending", await ScheduleStatusAsync(ka.Schedules[1]));
        Assert.Equal(0m, await ProgressAsync(ka.Id));
        Assert.Equal("pending", await MilestoneStatusAsync(ka.Id, 2));
        Assert.Equal(0, await HistoryCountAsync(ka.Id));
    }

    // CASE 6 — invoice without contract -------------------------------------

    [Fact]
    public async Task Schedule_Case6_InvoiceWithoutContract_NoContractChange_EvenWithOpenContracts()
    {
        if (_cs is null) return;
        var vt = await SeedCustomerAsync("CUS-VT", "Công ty TNHH Việt Tín", "0307890123");
        var k = await SeedContractAsync("HDMB-OPEN", (40_000_000m, 40m, -10), (60_000_000m, 60m, null));
        await BookLinkedInvoiceAsync(vt, "INV-LINKED", 40_000_000m, k.Id, k.Schedules[0]);
        var plain = await BookLinkedInvoiceAsync(vt, "INV-PLAIN", 40_000_000m, null, null);
        var before = await ContractStateAsync();
        var arBefore = await ArAsync(vt);

        await ReceiveAsync("PT-PLAIN", 40_000_000m, (plain, 40_000_000m));
        Assert.Equal((40_000_000m, "paid"), await ArInvoiceAsync(plain));
        Assert.Equal(arBefore - 40_000_000m, await ArAsync(vt));
        Assert.Equal(before, await ContractStateAsync());
    }

    // CASE 7 — bank statement goes through the same hook ---------------------

    [Fact]
    public async Task Schedule_Case7_BankStatement_SameResultAsReceipt()
    {
        if (_cs is null) return;
        await SeedCompanyBankAccountAsync();
        var (vt, _) = await VietTinWithInvoiceAsync(1_000_000m, "INV-UNRELATED");
        var k = await SeedContractAsync("HDMB-SK", (40_000_000m, 40m, null), (60_000_000m, 60m, null));
        var inv = await BookLinkedInvoiceAsync(vt, "INV-SK-001", 40_000_000m, k.Id, k.Schedules[0]);
        var (v, rows) = await ImportStatementAsync("SK-HD", Csv(
            "10/10/2026,FT7001,TT INV-SK-001 DOT 1,123456789,CÔNG TY TNHH VIỆT TÍN,VCB,15000000,",
            "11/10/2026,FT7002,TT INV-SK-001 CON LAI,123456789,CÔNG TY TNHH VIỆT TÍN,VCB,25000000,"));

        var p1 = (await Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, new PostStatementLineRequest(null, false, false), default))!;
        Assert.Equal(inv, Assert.Single(p1.Applications).InvoiceId);
        Assert.Equal("partial", await ScheduleStatusAsync(k.Schedules[0]));
        Assert.Equal(0m, await ProgressAsync(k.Id));

        await Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[1].Id, _adminId, new PostStatementLineRequest(null, false, false), default);
        Assert.Equal((40_000_000m, "paid"), await ArInvoiceAsync(inv));
        Assert.Equal("paid", await ScheduleStatusAsync(k.Schedules[0]));
        Assert.Equal(40m, await ProgressAsync(k.Id));

        // The statement panel shows the schedule reached through the applied invoice.
        var ctx = (await Settle().GetStatementLineContextAsync(_tenantId, _departmentId, v, rows[1].Id, default))!;
        var impact = Assert.Single(ctx.ContractSchedules);
        Assert.Equal(("HDMB-SK", 1, "paid", 40_000_000m), (impact.ContractNumber, impact.Ordinal, impact.Status, impact.PaidAmount));
        Assert.Equal(("INV-SK-001", 25_000_000m), (impact.Invoices[0].DocumentNumber, impact.Invoices[0].AppliedByThisDocument));
    }

    // CASE 8 — unapply ---------------------------------------------------------

    [Fact]
    public async Task Schedule_Case8_Unapply_PaidBackToPartialThenPending_MilestoneNotRevoked()
    {
        if (_cs is null) return;
        var vt = await SeedCustomerAsync("CUS-VT", "Công ty TNHH Việt Tín", "0307890123");
        var k = await SeedContractAsync("HDMB-UN", (40_000_000m, 40m, null), (60_000_000m, 60m, null));
        var inv = await BookLinkedInvoiceAsync(vt, "INV-UN", 40_000_000m, k.Id, k.Schedules[0]);
        var r1 = await ReceiveAsync("PT-UN-1", 20_000_000m, (inv, 20_000_000m));
        var r2 = await ReceiveAsync("PT-UN-2", 20_000_000m, (inv, 20_000_000m));
        Assert.Equal("paid", await ScheduleStatusAsync(k.Schedules[0]));
        Assert.Equal("active", await MilestoneStatusAsync(k.Id, 2));

        var u2 = (await Settlement().UnapplyAsync(_tenantId, _departmentId, r2.Posting!.VoucherId, _adminId, null, "sai hoá đơn", default))!;
        Assert.Equal("unapplied", u2.Outcome);
        Assert.Equal((20_000_000m, "partial"), await ArInvoiceAsync(inv));
        Assert.Equal("partial", await ScheduleStatusAsync(k.Schedules[0]));
        Assert.Equal(0m, await ProgressAsync(k.Id));
        // Cash stays posted (canonical semantics), only the application is reversed.
        Assert.Equal("posted", await Scalar<string>("SELECT status FROM cash_transactions WHERE id=@id", new { id = r2.Posting.Id }));
        // No confirmed rule to revoke an activated milestone — preserved.
        Assert.Equal("active", await MilestoneStatusAsync(k.Id, 2));

        await Settlement().UnapplyAsync(_tenantId, _departmentId, r1.Posting!.VoucherId, _adminId, null, null, default);
        Assert.Equal("pending", await ScheduleStatusAsync(k.Schedules[0]));
        Assert.Equal((0m, "open"), await ArInvoiceAsync(inv));
    }

    // CASE 9 — reallocate across contracts ----------------------------------

    [Fact]
    public async Task Schedule_Case9_Reallocate_OldScheduleDown_NewScheduleUp()
    {
        if (_cs is null) return;
        var vt = await SeedCustomerAsync("CUS-VT", "Công ty TNHH Việt Tín", "0307890123");
        var ka = await SeedContractAsync("HDMB-RA", (20_000_000m, 100m, null));
        var kb = await SeedContractAsync("HDMB-RB", (20_000_000m, 100m, null));
        var ia = await BookLinkedInvoiceAsync(vt, "INV-RA", 20_000_000m, ka.Id, ka.Schedules[0]);
        var ib = await BookLinkedInvoiceAsync(vt, "INV-RB", 20_000_000m, kb.Id, kb.Schedules[0]);

        var r = await ReceiveAsync("PT-RE", 20_000_000m, (ia, 20_000_000m));
        Assert.Equal(("paid", "pending"), (await ScheduleStatusAsync(ka.Schedules[0]), await ScheduleStatusAsync(kb.Schedules[0])));

        var voucher = r.Posting!.VoucherId;
        await Settlement().UnapplyAsync(_tenantId, _departmentId, voucher, _adminId, r.Applications[0].Id, "phân bổ lại", default);
        Assert.Equal(("pending", 0m), (await ScheduleStatusAsync(ka.Schedules[0]), await ProgressAsync(ka.Id)));

        var re = (await Settlement().AllocateAsync(_tenantId, _departmentId, voucher, _adminId, new[] { new AllocationInput(ib, 20_000_000m) }, default))!;
        Assert.Equal("allocated", re.Outcome);
        Assert.Equal(("pending", 0m), (await ScheduleStatusAsync(ka.Schedules[0]), await ProgressAsync(ka.Id)));
        Assert.Equal(("paid", 100m), (await ScheduleStatusAsync(kb.Schedules[0]), await ProgressAsync(kb.Id)));
        Assert.Equal((0m, "open"), await ArInvoiceAsync(ia));
        Assert.Equal((20_000_000m, "paid"), await ArInvoiceAsync(ib));

        // The receipt panel shows only the schedule of the ACTIVE application with an applied amount.
        var ctx = (await Settlement().GetContextAsync(_tenantId, _departmentId, voucher, default))!;
        var impacts = ctx.ContractSchedules.ToDictionary(i => i.ContractNumber!);
        Assert.Equal(("paid", 20_000_000m), (impacts["HDMB-RB"].Status, impacts["HDMB-RB"].Invoices[0].AppliedByThisDocument));
        Assert.Equal(("pending", 0m), (impacts["HDMB-RA"].Status, impacts["HDMB-RA"].Invoices[0].AppliedByThisDocument));
    }

    // CASE 10 — reverse ---------------------------------------------------------

    [Fact]
    public async Task Schedule_Case10_ReverseStatementPosting_RestoresEverything_SecondReverseIsNoop()
    {
        if (_cs is null) return;
        await SeedCompanyBankAccountAsync();
        var (vt, _) = await VietTinWithInvoiceAsync(1_000_000m, "INV-UNRELATED");
        var k = await SeedContractAsync("HDMB-RV", (40_000_000m, 40m, null), (60_000_000m, 60m, null));
        var inv = await BookLinkedInvoiceAsync(vt, "INV-RV", 40_000_000m, k.Id, k.Schedules[0]);
        var arBefore = await ArAsync(vt);
        var (v, rows) = await ImportStatementAsync("SK-RV", Csv("10/10/2026,FT7101,TT INV-RV,123456789,CÔNG TY TNHH VIỆT TÍN,VCB,40000000,"));

        await Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, new PostStatementLineRequest(null, false, false), default);
        Assert.Equal(("paid", 40m), (await ScheduleStatusAsync(k.Schedules[0]), await ProgressAsync(k.Id)));

        var rev = (await Settle().ReverseStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, "nhập nhầm", default))!;
        Assert.Equal("reversed", rev.Outcome);
        Assert.Equal((0m, "open"), await ArInvoiceAsync(inv));
        Assert.Equal(arBefore, await ArAsync(vt));
        Assert.Equal(("pending", 0m), (await ScheduleStatusAsync(k.Schedules[0]), await ProgressAsync(k.Id)));
        var state = await ContractStateAsync();

        var again = (await Settle().ReverseStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, "lần 2", default))!;
        Assert.Equal("nothing_to_reverse", again.Outcome);
        Assert.Equal(state, await ContractStateAsync());
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM voucher_journal_suggestions WHERE source='reversal'"));
    }

    // CASE 11 — idempotency ----------------------------------------------------

    [Fact]
    public async Task Schedule_Case11_RecomputeTwice_IdenticalState()
    {
        if (_cs is null) return;
        var vt = await SeedCustomerAsync("CUS-VT", "Công ty TNHH Việt Tín", "0307890123");
        var k = await SeedContractAsync("HDMB-ID", (40_000_000m, 40m, null), (60_000_000m, 60m, null));
        var a = await BookLinkedInvoiceAsync(vt, "INV-ID-1", 40_000_000m, k.Id, k.Schedules[0]);
        var b = await BookLinkedInvoiceAsync(vt, "INV-ID-2", 60_000_000m, k.Id, k.Schedules[1]);
        await ReceiveAsync("PT-ID", 70_000_000m, (a, 40_000_000m), (b, 30_000_000m));
        var state = await ContractStateAsync();

        for (var i = 0; i < 2; i++)
        {
            await using var conn = new NpgsqlConnection(_cs);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var changes = await ContractScheduleRecompute.RecomputeForInvoicesAsync(conn, tx, _tenantId, new[] { a, b }, _adminId, "manual", null, default);
            await tx.CommitAsync();
            Assert.Empty(changes);
            Assert.Equal(state, await ContractStateAsync());
        }
        Assert.Equal(("paid", "partial", 40m), (await ScheduleStatusAsync(k.Schedules[0]), await ScheduleStatusAsync(k.Schedules[1]), await ProgressAsync(k.Id)));
    }

    [Theory]
    [InlineData(0, 40_000_000, "pending")]
    [InlineData(1, 40_000_000, "partial")]
    [InlineData(39_999_999.99, 40_000_000, "partial")]
    [InlineData(40_000_000, 40_000_000, "paid")]
    [InlineData(45_000_000, 40_000_000, "paid")]
    public void Schedule_DeriveStatus(decimal paid, decimal total, string expected) =>
        Assert.Equal(expected, ContractScheduleRecompute.DeriveStatus(paid, total));

    // CASE 12 — penalty job consumes the corrected state ----------------------

    [Fact]
    public async Task Schedule_Case12_PenaltyJob_PaidByStatementNotPenalised_EligibleAgainAfterReverse()
    {
        if (_cs is null) return;
        await SeedCompanyBankAccountAsync();
        var (vt, _) = await VietTinWithInvoiceAsync(1_000_000m, "INV-UNRELATED");
        // Both schedules overdue; contract penalty_percent 0.05 %/day.
        var k = await SeedContractAsync("HDMB-PN", (40_000_000m, 40m, -10), (60_000_000m, 60m, -5));
        var inv = await BookLinkedInvoiceAsync(vt, "INV-PN", 40_000_000m, k.Id, k.Schedules[0]);
        var (v, rows) = await ImportStatementAsync("SK-PN", Csv("10/10/2026,FT7201,TT INV-PN,123456789,CÔNG TY TNHH VIỆT TÍN,VCB,40000000,"));
        var job = new ContractInstallmentPenaltyJob(Factory(), NullLogger<ContractInstallmentPenaltyJob>.Instance);

        await job.RunAsync(default);
        Assert.NotNull(await ScalarOrNull<decimal?>("SELECT penalty_amount FROM contract_payment_schedules WHERE id=@id", new { id = k.Schedules[0] }));

        await Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, new PostStatementLineRequest(null, false, false), default);
        Assert.Equal("paid", await ScheduleStatusAsync(k.Schedules[0]));
        await job.RunAsync(default);
        Assert.Null(await ScalarOrNull<decimal?>("SELECT penalty_amount FROM contract_payment_schedules WHERE id=@id", new { id = k.Schedules[0] }));
        // Control: the unpaid overdue schedule is still penalised by the unchanged rules.
        // Days overdue as the job computes them (DB CURRENT_DATE), not the test's UTC+7 "today".
        Assert.Equal(60_000_000m * 0.0005m * await OverdueDaysAsync(k.Schedules[1]), await Scalar<decimal>("SELECT penalty_amount FROM contract_payment_schedules WHERE id=@id", new { id = k.Schedules[1] }));

        await Settle().ReverseStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, null, default);
        Assert.Equal("pending", await ScheduleStatusAsync(k.Schedules[0]));
        await job.RunAsync(default);
        Assert.Equal(40_000_000m * 0.0005m * await OverdueDaysAsync(k.Schedules[0]), await Scalar<decimal>("SELECT penalty_amount FROM contract_payment_schedules WHERE id=@id", new { id = k.Schedules[0] }));
        Assert.Equal((0m, "open"), await ArInvoiceAsync(inv));
    }

    // Isolation / atomicity ---------------------------------------------------

    [Fact]
    public async Task Schedule_RecomputeFailure_RollsBackTheWholeSettlement()
    {
        if (_cs is null) return;
        var vt = await SeedCustomerAsync("CUS-VT", "Công ty TNHH Việt Tín", "0307890123");
        var k = await SeedContractAsync("HDMB-RB", (40_000_000m, 40m, null));
        var inv = await BookLinkedInvoiceAsync(vt, "INV-ATOM", 40_000_000m, k.Id, k.Schedules[0]);
        var arBefore = await ArAsync(vt);
        // Simulate a contract-side failure inside the settlement transaction.
        await Scalar<int>("""
            CREATE FUNCTION fail_schedule() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'schedule write failed'; END $$;
            CREATE TRIGGER trg_fail BEFORE UPDATE ON contract_payment_schedules FOR EACH ROW EXECUTE FUNCTION fail_schedule();
            SELECT 1;
            """);
        var v = await ReceiptVoucherAsync("PT-ATOM", 40_000_000m, "Công ty TNHH Việt Tín", "0307890123");
        await Assert.ThrowsAnyAsync<PostgresException>(() => Settlement().PostAsync(_tenantId, _departmentId, v, _adminId, Allocate((inv, 40_000_000m)), default));

        Assert.Equal((0m, "open"), await ArInvoiceAsync(inv));
        Assert.Equal(arBefore, await ArAsync(vt));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM cash_transactions"));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM payment_applications"));
        Assert.Equal("pending", await ScheduleStatusAsync(k.Schedules[0]));
    }

    [Fact]
    public async Task Schedule_OtherDepartmentContract_NotShownOnPanel_ButStillRecomputedFromItsOwnInvoice()
    {
        if (_cs is null) return;
        var vt = await SeedCustomerAsync("CUS-VT", "Công ty TNHH Việt Tín", "0307890123");
        var k = await SeedContractAsync("HDMB-VIEW", (40_000_000m, 40m, null));
        var inv = await BookLinkedInvoiceAsync(vt, "INV-VIEW", 40_000_000m, k.Id, k.Schedules[0]);
        var r = await ReceiveAsync("PT-VIEW", 40_000_000m, (inv, 40_000_000m));

        var own = (await Settlement().GetContextAsync(_tenantId, _departmentId, r.Posting!.VoucherId, default))!;
        Assert.Equal("HDMB-VIEW", Assert.Single(own.ContractSchedules).ContractNumber);
        // Panel read model is department-scoped: no contract details leak across departments.
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        var other = await ContractScheduleRecompute.LoadImpactsAsync(conn, null, _tenantId, _otherDepartmentId,
            new Dictionary<Guid, decimal> { [inv] = 40_000_000m }, default);
        Assert.Empty(other);
        var foreign = await ContractScheduleRecompute.LoadImpactsAsync(conn, null, _otherTenantId, _departmentId,
            new Dictionary<Guid, decimal> { [inv] = 40_000_000m }, default);
        Assert.Empty(foreign);
    }
}
