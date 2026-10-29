using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Migration 070 (chứng từ flow v2 PR3) on the real schema: status ↔ lifecycle
/// sync, the single state_change event writer, append-only history, the legacy
/// backfill (journalized, QĐ 24) and up → down → up. Own throwaway database.
/// </summary>
public sealed class VoucherLifecycleMigrationTests(TaxVerificationDatabase fx) : IClassFixture<TaxVerificationDatabase>
{
    private const string Version = "070_voucher_lifecycle.sql";

    private sealed record Row(string Status, string Lifecycle, int Version, DateTime? PostedAt, DateTime UpdatedAt);

    // ------------------------------------------------------------ triggers

    [Fact]
    public async Task Legacy_status_writes_move_the_lifecycle_and_are_logged_as_system()
    {
        if (fx.ConnectionString is null) return;
        await using var db = await OpenAsync();
        var id = await InsertAsync(db, "processing");
        Assert.Equal(("processing", "draft", 1), Short(await RowAsync(db, id)));

        await db.ExecuteAsync("UPDATE vouchers SET status='extracted' WHERE id=@id", new { id });
        Assert.Equal(("extracted", "extracted", 2), Short(await RowAsync(db, id)));

        await db.ExecuteAsync("UPDATE vouchers SET status='verified' WHERE id=@id", new { id });
        var posted = await RowAsync(db, id);
        Assert.Equal(("verified", "posted", 3), Short(posted));
        Assert.NotNull(posted.PostedAt);

        // Legacy unapply: back to extracted, but posted_at (ever posted) stays.
        await db.ExecuteAsync("UPDATE vouchers SET status='extracted' WHERE id=@id", new { id });
        var unapplied = await RowAsync(db, id);
        Assert.Equal(("extracted", "extracted", 4), Short(unapplied));
        Assert.Equal(posted.PostedAt, unapplied.PostedAt);

        var events = (await db.QueryAsync<(string Type, string Actor, string After)>(
            "SELECT event_type, actor_type, after->>'lifecycle_state' FROM voucher_events WHERE voucher_id=@id ORDER BY created_at, id",
            new { id })).ToList();
        Assert.Equal(["draft", "extracted", "posted", "extracted"], events.Select(e => e.After));
        Assert.All(events, e => Assert.Equal(("state_change", "system"), (e.Type, e.Actor)));
    }

    [Fact]
    public async Task A_v2_write_mirrors_status_and_records_actor_and_reason()
    {
        if (fx.ConnectionString is null) return;
        await using var db = await OpenAsync();
        var id = await InsertAsync(db, "extracted");

        await using (var tx = await db.BeginTransactionAsync())
        {
            await db.ExecuteAsync("""
                SELECT set_config('aiq.actor_type', 'user', true), set_config('aiq.actor_id', @actor, true),
                       set_config('aiq.reason', 'thiếu chữ ký', true);
                UPDATE vouchers SET lifecycle_state = 'needs_fix' WHERE id = @id;
                """, new { id, actor = fx.Admin.ToString() }, tx);
            await tx.CommitAsync();
        }

        Assert.Equal(("extracted", "needs_fix", 2), Short(await RowAsync(db, id)));
        var e = await db.QuerySingleAsync<(string Actor, Guid? ActorId, string? Reason, string Before)>("""
            SELECT actor_type, actor_id, reason, before->>'lifecycle_state' FROM voucher_events
             WHERE voucher_id=@id AND before IS NOT NULL
            """, new { id });
        Assert.Equal(("user", (Guid?)fx.Admin, "thiếu chữ ký", "extracted"), e);

        // Settings are transaction-local: the next write is 'system' again.
        await db.ExecuteAsync("UPDATE vouchers SET lifecycle_state = 'rejected' WHERE id=@id", new { id });
        Assert.Equal("rejected", (await RowAsync(db, id)).Status);
        Assert.Equal("system", await db.ExecuteScalarAsync<string>(
            "SELECT actor_type FROM voucher_events WHERE voucher_id=@id ORDER BY created_at DESC, id DESC LIMIT 1", new { id }));
    }

    [Fact]
    public async Task Editing_fields_neither_moves_the_lifecycle_nor_logs_an_event()
    {
        if (fx.ConnectionString is null) return;
        await using var db = await OpenAsync();
        var id = await InsertAsync(db, "extracted");
        var before = await db.ExecuteScalarAsync<int>("SELECT COUNT(*)::int FROM voucher_events WHERE voucher_id=@id", new { id });

        await db.ExecuteAsync("UPDATE vouchers SET total_amount = 2 WHERE id=@id", new { id });

        Assert.Equal(("extracted", "extracted", 1), Short(await RowAsync(db, id)));
        Assert.Equal(before, await db.ExecuteScalarAsync<int>("SELECT COUNT(*)::int FROM voucher_events WHERE voucher_id=@id", new { id }));
    }

    [Fact]
    public async Task Voucher_events_are_append_only()
    {
        if (fx.ConnectionString is null) return;
        await using var db = await OpenAsync();
        var id = await InsertAsync(db, "processing");

        var update = await Assert.ThrowsAsync<PostgresException>(() =>
            db.ExecuteAsync("UPDATE voucher_events SET reason='x' WHERE voucher_id=@id", new { id }));
        var delete = await Assert.ThrowsAsync<PostgresException>(() =>
            db.ExecuteAsync("DELETE FROM voucher_events WHERE voucher_id=@id", new { id }));
        var truncate = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync("TRUNCATE voucher_events"));
        Assert.Equal("23514", update.SqlState);
        Assert.Equal("23514", delete.SqlState);
        Assert.Equal("23514", truncate.SqlState);
        Assert.Contains("append-only", update.MessageText);
        Assert.True(await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM voucher_events WHERE voucher_id=@id)", new { id }));
    }

    /// <summary>The one way history goes: its tenant is deleted (FK cascade).</summary>
    [Fact]
    public async Task Deleting_a_tenant_still_cascades_its_history()
    {
        if (fx.ConnectionString is null) return;
        await using var db = await OpenAsync();
        var tenant = Guid.NewGuid();
        await db.ExecuteAsync("INSERT INTO tenants(id, code, name, status) VALUES (@tenant, @code, 'Tạm', 'active')",
            new { tenant, code = "tmp-" + tenant.ToString("N")[..8] });
        await db.ExecuteAsync("""
            INSERT INTO voucher_events(tenant_id, voucher_id, event_type, actor_type, reason)
            VALUES (@tenant, gen_random_uuid(), 'deleted', 'system', 'x')
            """, new { tenant });

        await db.ExecuteAsync("DELETE FROM tenants WHERE id=@tenant", new { tenant });

        Assert.False(await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM voucher_events WHERE tenant_id=@tenant)", new { tenant }));
    }

    [Fact]
    public async Task Unknown_lifecycle_state_is_rejected()
    {
        if (fx.ConnectionString is null) return;
        await using var db = await OpenAsync();
        var id = await InsertAsync(db, "extracted");
        await Assert.ThrowsAsync<PostgresException>(() =>
            db.ExecuteAsync("UPDATE vouchers SET lifecycle_state='journalized' WHERE id=@id", new { id }));
    }

    // ------------------------------------------------- backfill + round trip

    [Fact]
    public async Task Down_then_up_backfills_legacy_rows_and_converts_journalized()
    {
        if (fx.ConnectionString is null) return;
        await using var db = await OpenAsync();
        await db.ExecuteAsync("INSERT INTO tenant_feature_flags(tenant_id, flag, enabled) VALUES (@t, 'voucher_flow_v2', true)", new { t = fx.Tenant });
        await db.ExecuteAsync("INSERT INTO schema_migrations(version, checksum) VALUES (@Version, repeat('a', 64)) ON CONFLICT DO NOTHING", new { Version });

        // ---- down ---------------------------------------------------------------
        await db.ExecuteAsync(await File.ReadAllTextAsync(TaxVerificationDatabase.DownScript("070_voucher_lifecycle.down.sql")));

        Assert.False(await db.ExecuteScalarAsync<bool>("SELECT to_regclass('public.voucher_events') IS NOT NULL"));
        Assert.False(await db.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_name='vouchers' AND column_name='lifecycle_state')"));
        Assert.False(await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version=@Version)", new { Version }));
        var flagsBackup = await db.ExecuteScalarAsync<string>("SELECT tablename FROM pg_tables WHERE tablename LIKE 'tenant_feature_flags_backup_%'");
        Assert.NotNull(flagsBackup);
        Assert.True(await db.ExecuteScalarAsync<bool>($"SELECT enabled FROM {flagsBackup} WHERE tenant_id=@t", new { t = fx.Tenant }));
        Assert.True(await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM pg_tables WHERE tablename LIKE 'voucher_events_backup_%')"));

        // Legacy rows as the pre-070 code left them.
        var journalizedPosted = await LegacyAsync(db, "journalized");
        await db.ExecuteAsync("""
            INSERT INTO voucher_journal_suggestions(voucher_id, entry_no, debit_account, credit_account, amount, source)
            VALUES (@journalizedPosted, 1, '1561', '331', 1, 'posting')
            """, new { journalizedPosted });
        var journalizedBare = await LegacyAsync(db, "journalized");
        var verified = await LegacyAsync(db, "verified");
        var ocr = await LegacyAsync(db, "ocr_complete");
        var odd = await LegacyAsync(db, "some_future_status");
        // Every kind of posting evidence the backfill accepts (QĐ 24).
        var customer = await db.ExecuteScalarAsync<Guid>(
            "INSERT INTO sim_customers(tenant_id, customer_code, name) VALUES (@t, @c, 'KH') RETURNING id", new { t = fx.Tenant, c = Guid.NewGuid().ToString("N")[..8] });
        var supplier = await db.ExecuteScalarAsync<Guid>(
            "INSERT INTO sim_suppliers(tenant_id, supplier_code, name) VALUES (@t, @c, 'NCC') RETURNING id", new { t = fx.Tenant, c = Guid.NewGuid().ToString("N")[..8] });
        var journalizedAr = await LegacyAsync(db, "journalized");
        await db.ExecuteAsync("""
            INSERT INTO sim_ar_ledger(tenant_id, customer_id, voucher_id, document_date, document_number, document_type, debit)
            VALUES (@t, @customer, @journalizedAr, '2026-09-01', 'AR-1', 'invoice', 1)
            """, new { t = fx.Tenant, customer, journalizedAr });
        var journalizedAp = await LegacyAsync(db, "journalized");
        await db.ExecuteAsync("""
            INSERT INTO sim_ap_ledger(tenant_id, supplier_id, voucher_id, document_date, document_number, document_type, credit)
            VALUES (@t, @supplier, @journalizedAp, '2026-09-01', 'AP-1', 'invoice', 1)
            """, new { t = fx.Tenant, supplier, journalizedAp });
        var journalizedCash = await LegacyAsync(db, "journalized");
        await CashAsync(db, journalizedCash, supplier, "posted");
        // A payment posted then cancelled through the legacy path: back at extracted, posted once (QĐ 3).
        var legacyReversed = await LegacyAsync(db, "extracted");
        var reversedTx = await CashAsync(db, legacyReversed, supplier, "reversed");
        var updatedAt = await db.ExecuteScalarAsync<DateTime>("SELECT updated_at FROM vouchers WHERE id=@verified", new { verified });

        // ---- up again -----------------------------------------------------------
        await fx.RunFilesAsync(TaxVerificationDatabase.MigrationsDir(), f => f == Version);

        var posted = await RowAsync(db, journalizedPosted);
        Assert.Equal(("verified", "posted"), (posted.Status, posted.Lifecycle));
        Assert.NotNull(posted.PostedAt);
        var bare = await RowAsync(db, journalizedBare);
        Assert.Equal(("extracted", "needs_fix"), (bare.Status, bare.Lifecycle));
        Assert.Null(bare.PostedAt);
        var v = await RowAsync(db, verified);
        Assert.Equal(("verified", "posted", 1), Short(v));
        Assert.Equal(updatedAt, v.UpdatedAt);            // backfill keeps updated_at
        Assert.Equal(updatedAt, v.PostedAt);
        Assert.Equal("draft", (await RowAsync(db, ocr)).Lifecycle);
        Assert.Equal("draft", (await RowAsync(db, odd)).Lifecycle);
        foreach (var evidence in new[] { journalizedAr, journalizedAp, journalizedCash })
        {
            var e = await RowAsync(db, evidence);
            Assert.Equal(("verified", "posted"), (e.Status, e.Lifecycle));
            Assert.NotNull(e.PostedAt);
        }
        var reversed = await RowAsync(db, legacyReversed);
        Assert.Equal(("extracted", "extracted"), (reversed.Status, reversed.Lifecycle));
        Assert.NotNull(reversed.PostedAt);
        Assert.Equal(reversedTx.ToString(), await db.ExecuteScalarAsync<string>("""
            SELECT after->'reversed_cash_transactions'->>0 FROM voucher_events
             WHERE voucher_id=@legacyReversed AND event_type='legacy_backfill'
            """, new { legacyReversed }));
        Assert.Equal(5, await db.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)::int FROM voucher_events WHERE event_type='legacy_backfill' AND before->>'status'='journalized'
               AND voucher_id IN (@journalizedPosted, @journalizedBare, @journalizedAr, @journalizedAp, @journalizedCash)
            """, new { journalizedPosted, journalizedBare, journalizedAr, journalizedAp, journalizedCash }));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*)::int FROM vouchers WHERE status='journalized'"));
        // The backfill itself logs no state_change (only the legacy_backfill rows).
        Assert.False(await db.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM voucher_events WHERE voucher_id=@verified)", new { verified }));
        // The pre-existing fixture voucher survived the round trip.
        Assert.Equal("extracted", (await RowAsync(db, fx.LegacyVoucher)).Lifecycle);

        // ---- down restores journalized, up again converts it again ---------------
        await db.ExecuteAsync("INSERT INTO schema_migrations(version, checksum) VALUES (@Version, repeat('a', 64)) ON CONFLICT DO NOTHING", new { Version });
        await db.ExecuteAsync(await File.ReadAllTextAsync(TaxVerificationDatabase.DownScript("070_voucher_lifecycle.down.sql")));
        Assert.Equal("journalized", await db.ExecuteScalarAsync<string>("SELECT status FROM vouchers WHERE id=@journalizedBare", new { journalizedBare }));
        await fx.RunFilesAsync(TaxVerificationDatabase.MigrationsDir(), f => f == Version);
        Assert.Equal("needs_fix", (await RowAsync(db, journalizedBare)).Lifecycle);
    }

    // --------------------------------------------------------------- helpers

    private static (string, string, int) Short(Row r) => (r.Status, r.Lifecycle, r.Version);

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var db = new NpgsqlConnection(fx.ConnectionString);
        await db.OpenAsync();
        return db;
    }

    private Task<Guid> InsertAsync(NpgsqlConnection db, string status) => db.ExecuteScalarAsync<Guid>("""
        INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, status, document_number, total_amount)
        VALUES (@u, @t, @d, 'invoice_vat_in', @status, @n, 1) RETURNING id
        """, new { u = fx.User, t = fx.Tenant, d = fx.Dept, status, n = Guid.NewGuid().ToString("N")[..10] });

    // Before 070 the lifecycle columns do not exist.
    private Task<Guid> LegacyAsync(NpgsqlConnection db, string status) => InsertAsync(db, status);

    private Task<Guid> CashAsync(NpgsqlConnection db, Guid voucherId, Guid supplierId, string status) => db.ExecuteScalarAsync<Guid>("""
        INSERT INTO cash_transactions(tenant_id, department_id, voucher_id, direction, counterparty_type, supplier_id, amount,
                                      transaction_date, payment_method, cash_account, counter_account, document_number, status)
        VALUES (@t, @d, @voucherId, 'money_out', 'supplier', @supplierId, 1, '2026-09-01', 'cash', '1111', '331', 'PC-1', @status)
        RETURNING id
        """, new { t = fx.Tenant, d = fx.Dept, voucherId, supplierId, status });

    private static Task<Row> RowAsync(NpgsqlConnection db, Guid id) => db.QuerySingleAsync<Row>("""
        SELECT status AS Status, lifecycle_state AS Lifecycle, lifecycle_version AS Version, posted_at AS PostedAt, updated_at AS UpdatedAt
          FROM vouchers WHERE id=@id
        """, new { id });
}
