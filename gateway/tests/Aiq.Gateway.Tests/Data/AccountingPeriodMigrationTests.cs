using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Migration 071 (chứng từ flow v2 PR4) on the real schema: a closed period
/// refuses every write to a voucher of that period, in the same transaction
/// (docs/flows/chung-tu-proposed.md §10.1 P1–P3); only for v2 tenants; the
/// period history is append-only; up → down → up. Own throwaway database.
/// </summary>
public sealed class AccountingPeriodMigrationTests(TaxVerificationDatabase fx) : IClassFixture<TaxVerificationDatabase>
{
    private const string Version = "071_accounting_periods.sql";
    private const string PeriodLocked = "AQ001";

    // ------------------------------------------------------------ v2 tenant

    [Fact]
    public async Task A_closed_period_refuses_new_vouchers_up_to_its_last_day_only() => await WithV2Async(async db =>
    {
        await CloseAsync(db, 2031, 10);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(db, new DateOnly(2031, 10, 31)));
        Assert.Equal(PeriodLocked, refused.SqlState);
        Assert.Equal("period_locked:2031-10", refused.MessageText);

        await InsertAsync(db, new DateOnly(2031, 11, 1));                                   // first day of the next month
        await InsertAsync(db, new DateOnly(2031, 10, 31), accountingDate: new DateOnly(2031, 11, 2)); // late invoice, booked later
        await InsertAsync(db, null);                                                        // no date yet: no period
    });

    [Fact]
    public async Task Every_change_to_a_voucher_of_a_closed_period_is_refused() => await WithV2Async(async db =>
    {
        var id = await InsertAsync(db, new DateOnly(2032, 3, 15));
        var moved = await InsertAsync(db, new DateOnly(2032, 4, 2));
        await CloseAsync(db, 2032, 3);

        foreach (var sql in new[]
        {
            "UPDATE vouchers SET total_amount = 2 WHERE id=@id",
            "UPDATE vouchers SET accounting_date = '2032-04-01' WHERE id=@id",       // moving out
            "UPDATE vouchers SET lifecycle_state = 'validated' WHERE id=@id",
            "UPDATE vouchers SET deleted_at = NOW() WHERE id=@id",
            "INSERT INTO voucher_journal_suggestions(voucher_id, entry_no, debit_account, credit_account, amount) VALUES (@id, 1, '1561', '331', 1)",
        })
            Assert.Equal(PeriodLocked, (await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(sql, new { id }))).SqlState);
        // Moving a voucher into the closed period.
        Assert.Equal(PeriodLocked, (await Assert.ThrowsAsync<PostgresException>(() =>
            db.ExecuteAsync("UPDATE vouchers SET accounting_date = '2032-03-31' WHERE id=@moved", new { moved }))).SqlState);

        // Metadata is not an accounting change.
        await db.ExecuteAsync("""
            UPDATE vouchers SET tax_verification_status = 'unavailable', tax_verification_checked_at = NOW(),
                                document_review_status = 'awaiting_review', error_message = 'x'
             WHERE id=@id
            """, new { id });
        Assert.Equal(1m, await db.ExecuteScalarAsync<decimal>("SELECT total_amount FROM vouchers WHERE id=@id", new { id }));
    });

    [Fact]
    public async Task Reversing_a_posted_voucher_of_a_closed_period_is_not_refused() => await WithV2Async(async db =>
    {
        var id = await InsertAsync(db, new DateOnly(2033, 5, 10));
        await db.ExecuteAsync("UPDATE vouchers SET lifecycle_state = 'posted' WHERE id=@id", new { id });
        await CloseAsync(db, 2033, 5);

        await db.ExecuteAsync("""
            INSERT INTO voucher_journal_suggestions(voucher_id, entry_no, debit_account, credit_account, amount, source)
            VALUES (@id, 9, '331', '1561', 1, 'reversal')
            """, new { id });
        await db.ExecuteAsync("""
            UPDATE vouchers SET lifecycle_state = 'reversed', reversed_at = NOW(), reversal_reason = 'sai số tiền' WHERE id=@id
            """, new { id });
        Assert.Equal("reversed", await db.ExecuteScalarAsync<string>("SELECT lifecycle_state FROM vouchers WHERE id=@id", new { id }));
    });

    [Fact]
    public async Task A_period_without_a_row_is_open_and_a_write_materialises_it_open() => await WithV2Async(async db =>
    {
        await InsertAsync(db, new DateOnly(2034, 7, 1));
        Assert.Equal("open", await db.ExecuteScalarAsync<string>(
            "SELECT status FROM accounting_periods WHERE tenant_id=@t AND department_id=@d AND year=2034 AND month=7",
            new { t = fx.Tenant, d = fx.Dept }));
    });

    [Fact]
    public async Task Another_department_has_its_own_periods() => await WithV2Async(async db =>
    {
        await CloseAsync(db, 2035, 2);
        await db.ExecuteAsync("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, status, document_number, document_date, total_amount)
            VALUES (@u, @t, @od, 'invoice_vat_in', 'extracted', @n, '2035-02-10', 1)
            """, new { u = fx.User, t = fx.Tenant, od = fx.OtherDept, n = Guid.NewGuid().ToString("N")[..10] });
    });

    // ------------------------------------------------------- tenant without v2

    [Fact]
    public async Task Without_the_v2_flag_a_closed_period_changes_nothing()
    {
        if (fx.ConnectionString is null) return;
        await using var db = await OpenAsync();
        await CloseAsync(db, 2036, 1);

        var id = await InsertAsync(db, new DateOnly(2036, 1, 20));
        await db.ExecuteAsync("UPDATE vouchers SET total_amount = 5, status = 'verified' WHERE id=@id", new { id });
        await db.ExecuteAsync("""
            INSERT INTO voucher_journal_suggestions(voucher_id, entry_no, debit_account, credit_account, amount) VALUES (@id, 1, '1561', '331', 1)
            """, new { id });
        Assert.Equal(("verified", 5m), await db.QuerySingleAsync<(string, decimal)>("SELECT status, total_amount FROM vouchers WHERE id=@id", new { id }));
        // No period row is materialised for a tenant without v2.
        Assert.Equal(0, await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*)::int FROM accounting_periods WHERE tenant_id=@t AND year=2036 AND month<>1", new { t = fx.Tenant }));
    }

    // ------------------------------------------------------------ history

    [Fact]
    public async Task Period_events_are_append_only()
    {
        if (fx.ConnectionString is null) return;
        await using var db = await OpenAsync();
        var period = await CloseAsync(db, 2037, 4);
        await db.ExecuteAsync("""
            INSERT INTO accounting_period_events(tenant_id, period_id, department_id, year, month, event_type, actor_type, actor_id)
            VALUES (@t, @period, @d, 2037, 4, 'period_closed', 'user', @a)
            """, new { t = fx.Tenant, period, d = fx.Dept, a = fx.Admin });

        foreach (var sql in new[] { "UPDATE accounting_period_events SET reason='x'", "DELETE FROM accounting_period_events", "TRUNCATE accounting_period_events" })
            Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(sql))).SqlState);
        // A period row is never deleted either (only its tenant's cascade may).
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() =>
            db.ExecuteAsync("DELETE FROM accounting_periods WHERE id=@period", new { period }))).SqlState);
    }

    [Fact]
    public async Task A_closed_period_needs_its_closing_time_and_a_reopen_needs_a_reason()
    {
        if (fx.ConnectionString is null) return;
        await using var db = await OpenAsync();
        await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync("""
            INSERT INTO accounting_periods(tenant_id, department_id, year, month, status) VALUES (@t, @d, 2038, 1, 'closed')
            """, new { t = fx.Tenant, d = fx.Dept }));
        await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync("""
            INSERT INTO accounting_periods(tenant_id, department_id, year, month, status, reopened_at, reopen_reason) VALUES (@t, @d, 2038, 2, 'open', NOW(), '  ')
            """, new { t = fx.Tenant, d = fx.Dept }));
        await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync("""
            INSERT INTO accounting_periods(tenant_id, department_id, year, month) VALUES (@t, @d, 2038, 13)
            """, new { t = fx.Tenant, d = fx.Dept }));
    }

    // ------------------------------------------------------------ down / up

    [Fact]
    public async Task Down_then_up_keeps_a_copy_of_the_periods_and_their_history()
    {
        if (fx.ConnectionString is null) return;
        await using var db = await OpenAsync();
        await CloseAsync(db, 2039, 6);

        await db.ExecuteAsync(await File.ReadAllTextAsync(TaxVerificationDatabase.DownScript("071_accounting_periods.down.sql")));

        Assert.False(await db.ExecuteScalarAsync<bool>("SELECT to_regclass('public.accounting_periods') IS NOT NULL"));
        Assert.False(await db.ExecuteScalarAsync<bool>("SELECT to_regclass('public.accounting_period_events') IS NOT NULL"));
        Assert.False(await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM pg_proc WHERE proname = 'accounting_period_assert_open')"));
        Assert.False(await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version=@Version)", new { Version }));
        var backup = await db.ExecuteScalarAsync<string>("SELECT tablename FROM pg_tables WHERE tablename LIKE 'accounting_periods_backup_%'");
        Assert.NotNull(backup);
        Assert.Equal("closed", await db.ExecuteScalarAsync<string>($"SELECT status FROM {backup} WHERE year=2039 AND month=6"));
        Assert.True(await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM pg_tables WHERE tablename LIKE 'accounting_period_events_backup_%')"));
        // Vouchers are untouched and writable without the period triggers.
        await InsertAsync(db, new DateOnly(2039, 6, 3));

        await fx.RunFilesAsync(TaxVerificationDatabase.MigrationsDir(), f => f == Version);
        await db.ExecuteAsync("INSERT INTO schema_migrations(version, checksum) VALUES (@Version, repeat('a', 64)) ON CONFLICT DO NOTHING", new { Version });
        Assert.True(await db.ExecuteScalarAsync<bool>("SELECT to_regclass('public.accounting_periods') IS NOT NULL"));
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)::int FROM pg_trigger WHERE tgname IN ('trg_vouchers_period_lock', 'trg_journal_period_lock') AND NOT tgisinternal
            """));
    }

    // --------------------------------------------------------------- helpers

    private async Task WithV2Async(Func<NpgsqlConnection, Task> body)
    {
        if (fx.ConnectionString is null) return;
        await using var db = await OpenAsync();
        await db.ExecuteAsync("""
            INSERT INTO tenant_feature_flags(tenant_id, flag, enabled) VALUES (@t, 'voucher_flow_v2', true)
            ON CONFLICT (tenant_id, flag) DO UPDATE SET enabled = true
            """, new { t = fx.Tenant });
        try { await body(db); }
        finally { await db.ExecuteAsync("UPDATE tenant_feature_flags SET enabled = false WHERE tenant_id=@t", new { t = fx.Tenant }); }
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var db = new NpgsqlConnection(fx.ConnectionString);
        await db.OpenAsync();
        return db;
    }

    private Task<Guid> CloseAsync(NpgsqlConnection db, int year, int month) => db.ExecuteScalarAsync<Guid>("""
        INSERT INTO accounting_periods(tenant_id, department_id, year, month, status, closed_by, closed_at)
        VALUES (@t, @d, @year, @month, 'closed', @a, NOW())
        ON CONFLICT (tenant_id, department_id, year, month) DO UPDATE SET status = 'closed', closed_by = EXCLUDED.closed_by, closed_at = EXCLUDED.closed_at
        RETURNING id
        """, new { t = fx.Tenant, d = fx.Dept, a = fx.Admin, year, month });

    private Task<Guid> InsertAsync(NpgsqlConnection db, DateOnly? documentDate, DateOnly? accountingDate = null) => db.ExecuteScalarAsync<Guid>("""
        INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, status, document_number, document_date, accounting_date, total_amount)
        VALUES (@u, @t, @d, 'invoice_vat_in', 'extracted', @n, @documentDate, @accountingDate, 1) RETURNING id
        """, new
    {
        u = fx.User, t = fx.Tenant, d = fx.Dept, n = Guid.NewGuid().ToString("N")[..10],
        documentDate = documentDate?.ToDateTime(TimeOnly.MinValue), accountingDate = accountingDate?.ToDateTime(TimeOnly.MinValue),
    });
}
