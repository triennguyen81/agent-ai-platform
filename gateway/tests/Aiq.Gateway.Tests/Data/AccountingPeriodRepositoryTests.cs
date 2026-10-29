using Aiq.Gateway.Data;
using Aiq.Gateway.Vouchers;
using Aiq.Gateway.Vouchers.Lifecycle;
using Aiq.Gateway.Vouchers.Periods;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Closing / reopening a period (docs/flows/chung-tu-proposed.md §10.1 P4–P6,
/// P10, P13) against the real schema, including a write racing a close: the
/// close holds the period row FOR UPDATE, every v2 voucher write holds it FOR
/// SHARE, so one waits for the other and nothing slips between them.
/// </summary>
public sealed class AccountingPeriodRepositoryTests(TaxVerificationDatabase fx) : IClassFixture<TaxVerificationDatabase>
{
    private const string PeriodLocked = "AQ001";
    // Far from "now" so the P13 future check never interferes; each test its own year.
    private static readonly AccountingPeriod Current = new(2099, 12);

    static AccountingPeriodRepositoryTests()
    {
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyTypeHandler());
    }

    private NpgsqlConnectionFactory Factory => new(fx.ConnectionString!);
    private AccountingPeriodRepository Periods => new(Factory);
    private PeriodActor Admin => new("user", fx.Admin, "req-1");

    // ------------------------------------------------------------ close rules

    [Fact]
    public async Task A_month_closes_only_after_the_previous_one_unless_that_one_is_empty() => await WithV2Async(async () =>
    {
        await VoucherAsync(new DateOnly(2001, 1, 15));
        await VoucherAsync(new DateOnly(2001, 2, 15));

        var refused = await Periods.CloseAsync(fx.Tenant, fx.Dept, new(2001, 2), Admin, Current, CancellationToken.None);
        Assert.Equal(AccountingPeriodErrors.PreviousOpen, refused.Error);
        Assert.Equal("2001-01", refused.Previous);

        Assert.Null((await Periods.CloseAsync(fx.Tenant, fx.Dept, new(2001, 1), Admin, Current, CancellationToken.None)).Error);
        var closed = await Periods.CloseAsync(fx.Tenant, fx.Dept, new(2001, 2), Admin, Current, CancellationToken.None);
        Assert.Null(closed.Error);
        Assert.Equal(("2001-02", "closed", fx.Admin), (closed.Period!.Period, closed.Period.Status, closed.Period.ClosedBy));

        // 2001-03 is empty, so 2001-04 may close while it is open.
        await VoucherAsync(new DateOnly(2001, 4, 2));
        Assert.Null((await Periods.CloseAsync(fx.Tenant, fx.Dept, new(2001, 4), Admin, Current, CancellationToken.None)).Error);
    });

    [Fact]
    public async Task Vouchers_awaiting_approval_or_posting_keep_the_period_open() => await WithV2Async(async () =>
    {
        var pending = await VoucherAsync(new DateOnly(2002, 6, 3), "pending_approval");
        var approved = await VoucherAsync(new DateOnly(2002, 6, 4), "approved");
        await VoucherAsync(new DateOnly(2002, 6, 5), "validated");
        await VoucherAsync(new DateOnly(2002, 6, 6), "posted");
        var elsewhere = await VoucherAsync(new DateOnly(2002, 6, 7), "pending_approval", fx.OtherDept);

        var refused = await Periods.CloseAsync(fx.Tenant, fx.Dept, new(2002, 6), Admin, Current, CancellationToken.None);

        Assert.Equal(AccountingPeriodErrors.HasUnposted, refused.Error);
        Assert.Equal(new[] { pending, approved }.Order(), refused.VoucherIds.Order());
        Assert.DoesNotContain(elsewhere, refused.VoucherIds);
        Assert.Equal("open", (await GetAsync(new(2002, 6))).Status);
    });

    [Fact]
    public async Task Closing_twice_reopening_an_open_period_and_closing_the_future_are_refused() => await WithV2Async(async () =>
    {
        Assert.Null((await Periods.CloseAsync(fx.Tenant, fx.Dept, new(2003, 1), Admin, Current, CancellationToken.None)).Error);
        Assert.Equal(AccountingPeriodErrors.AlreadyClosed,
            (await Periods.CloseAsync(fx.Tenant, fx.Dept, new(2003, 1), Admin, Current, CancellationToken.None)).Error);
        Assert.Equal(AccountingPeriodErrors.NotClosed,
            (await Periods.ReopenAsync(fx.Tenant, fx.Dept, new(2003, 2), Admin, "x", CancellationToken.None)).Error);
        Assert.Equal(AccountingPeriodErrors.InFuture,
            (await Periods.CloseAsync(fx.Tenant, fx.Dept, new(2003, 4), Admin, new(2003, 3), CancellationToken.None)).Error);
        // The current month itself may close.
        Assert.Null((await Periods.CloseAsync(fx.Tenant, fx.Dept, new(2003, 3), Admin, new(2003, 3), CancellationToken.None)).Error);
    });

    [Fact]
    public async Task Reopening_needs_a_reason_warns_when_the_next_month_is_closed_and_is_logged() => await WithV2Async(async () =>
    {
        await Periods.CloseAsync(fx.Tenant, fx.Dept, new(2004, 5), Admin, Current, CancellationToken.None);
        await Periods.CloseAsync(fx.Tenant, fx.Dept, new(2004, 6), Admin, Current, CancellationToken.None);

        Assert.Equal(AccountingPeriodErrors.ReasonRequired,
            (await Periods.ReopenAsync(fx.Tenant, fx.Dept, new(2004, 5), Admin, "   ", CancellationToken.None)).Error);
        Assert.Equal("closed", (await GetAsync(new(2004, 5))).Status);

        var reopened = await Periods.ReopenAsync(fx.Tenant, fx.Dept, new(2004, 5), Admin, "Bổ sung HĐ nhận muộn", CancellationToken.None);
        Assert.Null(reopened.Error);
        Assert.Equal(("open", "Bổ sung HĐ nhận muộn", fx.Admin), (reopened.Period!.Status, reopened.Period.ReopenReason, reopened.Period.ReopenedBy));
        Assert.Equal([new PeriodWarning(AccountingPeriodErrors.NextPeriodClosed, "2004-06")], reopened.Warnings);

        var events = await Periods.ListEventsAsync(fx.Tenant, fx.Dept, new(2004, 5), CancellationToken.None);
        Assert.Equal(["period_closed", "period_reopened"], events.Select(e => e.EventType));
        Assert.All(events, e => Assert.Equal((fx.Admin, "user", "req-1"), (e.ActorId!.Value, e.ActorType, e.RequestId!)));
        Assert.Equal("Bổ sung HĐ nhận muộn", events[1].Reason);
        Assert.Contains("next_period_closed", events[1].Details);
    });

    [Fact]
    public async Task The_year_lists_every_month_and_a_month_without_a_row_is_open() => await WithV2Async(async () =>
    {
        await Periods.CloseAsync(fx.Tenant, fx.Dept, new(2005, 3), Admin, Current, CancellationToken.None);

        var year = await Periods.ListAsync(fx.Tenant, fx.Dept, 2005, CancellationToken.None);

        Assert.Equal(12, year.Count);
        Assert.Equal(Enumerable.Range(1, 12).Select(m => $"2005-{m:D2}"), year.Select(p => p.Period));
        Assert.Equal("closed", year[2].Status);
        Assert.All(year.Where(p => p.Period != "2005-03"), p => Assert.Equal("open", p.Status));
        Assert.Contains(new AccountingPeriod(2005, 3), await Periods.ClosedAsync(fx.Tenant, fx.Dept, CancellationToken.None));
        // Another department sees its own months only.
        Assert.All(await Periods.ListAsync(fx.Tenant, fx.OtherDept, 2005, CancellationToken.None), p => Assert.Equal("open", p.Status));
    });

    // ------------------------------------------------------------ concurrency

    [Fact]
    public async Task A_posting_in_flight_holds_the_close_until_it_commits() => await WithV2Async(async () =>
    {
        var id = await VoucherAsync(new DateOnly(2006, 8, 20), "approved");
        await using var poster = await OpenAsync();
        await using var tx = await poster.BeginTransactionAsync();
        await poster.ExecuteAsync("UPDATE vouchers SET lifecycle_state = 'posted' WHERE id=@id", new { id }, tx);

        var close = Periods.CloseAsync(fx.Tenant, fx.Dept, new(2006, 8), Admin, Current, CancellationToken.None);
        await AssertBlockedAsync(close);

        await tx.CommitAsync();
        var result = await close;
        Assert.Null(result.Error);                         // the voucher is posted now, nothing unposted is left
        Assert.Equal("posted", await LifecycleAsync(id));
    });

    [Fact]
    public async Task A_posting_rolled_back_while_the_close_waits_keeps_the_period_open() => await WithV2Async(async () =>
    {
        var id = await VoucherAsync(new DateOnly(2007, 8, 20), "approved");
        await using var poster = await OpenAsync();
        await using var tx = await poster.BeginTransactionAsync();
        await poster.ExecuteAsync("UPDATE vouchers SET lifecycle_state = 'posted' WHERE id=@id", new { id }, tx);

        var close = Periods.CloseAsync(fx.Tenant, fx.Dept, new(2007, 8), Admin, Current, CancellationToken.None);
        await AssertBlockedAsync(close);

        await tx.RollbackAsync();
        var result = await close;
        Assert.Equal(AccountingPeriodErrors.HasUnposted, result.Error);
        Assert.Equal([id], result.VoucherIds);
    });

    [Fact]
    public async Task A_close_in_flight_makes_a_racing_submit_and_edit_fail_with_period_locked() => await WithV2Async(async () =>
    {
        var id = await VoucherAsync(new DateOnly(2008, 8, 20), "validated");
        await using var closer = await OpenAsync();
        await using var tx = await closer.BeginTransactionAsync();
        Assert.Null((await Periods.CloseAsync(closer, tx, fx.Tenant, fx.Dept, new(2008, 8), Admin, Current, CancellationToken.None)).Error);

        var lifecycle = new VoucherLifecycleRepository(Factory);
        var submit = lifecycle.TransitionAsync(fx.Tenant, fx.Dept, id,
            new LifecycleChange("validated", "pending_approval", "user", fx.User, null, null), CancellationToken.None);
        var edit = new VoucherRepository(Factory).UpdateAsync(fx.Tenant, fx.User, id, new UpdateVoucherRequest(
            null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, 2m, null, null, null, null), CancellationToken.None);
        await AssertBlockedAsync(submit);
        await AssertBlockedAsync(edit);

        await tx.CommitAsync();
        Assert.Equal(PeriodLocked, (await Assert.ThrowsAsync<PostgresException>(() => submit)).SqlState);
        Assert.Equal(PeriodLocked, (await Assert.ThrowsAsync<PostgresException>(() => edit)).SqlState);
        Assert.Equal("validated", await LifecycleAsync(id));
    });

    [Fact]
    public async Task Racing_closes_and_postings_never_leave_an_approved_voucher_in_a_closed_period() => await WithV2Async(async () =>
    {
        var lifecycle = new VoucherLifecycleRepository(Factory);
        // One year per round so the previous month (P4) is always empty.
        for (var year = 2010; year < 2022; year++)
        {
            var period = new AccountingPeriod(year, 6);
            var id = await VoucherAsync(period.FirstDay, "approved");
            var post = Task.Run(() => lifecycle.TransitionAsync(fx.Tenant, fx.Dept, id,
                new LifecycleChange("approved", "posted", "user", fx.Admin, null, null), CancellationToken.None));
            var close = Task.Run(() => Periods.CloseAsync(fx.Tenant, fx.Dept, period, Admin, Current, CancellationToken.None));
            await Task.WhenAll(post, close);

            var closed = (await GetAsync(period)).Status == "closed";
            var state = await LifecycleAsync(id);
            // Either the posting won (then the close saw nothing unposted) or the close
            // refused because the voucher was still approved — never both "closed" and "approved".
            Assert.False(closed && state == "approved", $"{period}: closed with an approved voucher");
            Assert.True(post.Result, $"{period}: the posting must not be lost");
            Assert.Equal(closed ? null : AccountingPeriodErrors.HasUnposted, close.Result.Error);
        }
    });

    // --------------------------------------------------------------- helpers

    private static async Task AssertBlockedAsync(Task task)
    {
        await Task.WhenAny(task, Task.Delay(700));
        Assert.False(task.IsCompleted, "expected the operation to wait for the period row lock");
    }

    private async Task WithV2Async(Func<Task> body)
    {
        if (fx.ConnectionString is null) return;
        await ExecAsync("""
            INSERT INTO tenant_feature_flags(tenant_id, flag, enabled) VALUES (@t, 'voucher_flow_v2', true)
            ON CONFLICT (tenant_id, flag) DO UPDATE SET enabled = true
            """, new { t = fx.Tenant });
        try { await body(); }
        finally { await ExecAsync("UPDATE tenant_feature_flags SET enabled = false WHERE tenant_id=@t", new { t = fx.Tenant }); }
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var db = new NpgsqlConnection(fx.ConnectionString);
        await db.OpenAsync();
        return db;
    }

    private async Task ExecAsync(string sql, object args)
    {
        await using var c = await OpenAsync();
        await c.ExecuteAsync(sql, args);
    }

    private async Task<Guid> VoucherAsync(DateOnly date, string lifecycle = "extracted", Guid? department = null)
    {
        await using var c = await OpenAsync();
        var id = await c.ExecuteScalarAsync<Guid>("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, status, document_number, document_date, total_amount)
            VALUES (@u, @t, @d, 'invoice_vat_in', 'extracted', @n, @date, 1) RETURNING id
            """, new { u = fx.User, t = fx.Tenant, d = department ?? fx.Dept, n = Guid.NewGuid().ToString("N")[..10], date = date.ToDateTime(TimeOnly.MinValue) });
        if (lifecycle != "extracted")
            await c.ExecuteAsync("UPDATE vouchers SET lifecycle_state=@lifecycle WHERE id=@id", new { id, lifecycle });
        return id;
    }

    private async Task<string> LifecycleAsync(Guid id)
    {
        await using var c = await OpenAsync();
        return await c.ExecuteScalarAsync<string>("SELECT lifecycle_state FROM vouchers WHERE id=@id", new { id }) ?? "";
    }

    private async Task<AccountingPeriodRecord> GetAsync(AccountingPeriod period) =>
        (await Periods.ListAsync(fx.Tenant, fx.Dept, period.Year, CancellationToken.None)).Single(p => p.Period == period.ToString());
}
