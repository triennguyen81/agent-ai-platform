using System.Text.Json;
using Aiq.Gateway.Data;
using Aiq.Gateway.Vouchers;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// One throwaway DATABASE per test class with the full production schema:
/// infra/postgres/init 01..20 (base tables + extensions), then every Gateway
/// migration before 057, a legacy unattested 'verified' row, then 057 itself.
/// A separate database (not a schema) so nothing can ever resolve to the
/// live tables. Dropped afterwards.
/// </summary>
public sealed class TaxVerificationDatabase : IAsyncLifetime
{
    public string? BaseConnectionString { get; } = ResolveTestConnectionString();
    public string? ConnectionString { get; private set; }
    public readonly string DatabaseName = $"aiq_taxtest_{Guid.NewGuid():N}";
    public Guid Tenant, Dept, OtherDept, Admin, Admin2, Admin3, User, LegacyVoucher;

    private static string? ResolveTestConnectionString()
    {
        var explicitConnection = Environment.GetEnvironmentVariable("AIQ_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(explicitConnection)) return explicitConnection;
        var password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD");
        if (string.IsNullOrWhiteSpace(password)) return null;
        return new NpgsqlConnectionStringBuilder
        {
            Host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "postgres",
            Port = 5432,
            Database = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "aiq",
            Username = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "aiq",
            Password = password,
        }.ConnectionString;
    }

    internal static string RepoPath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "infra", "postgres", "init"))) dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("infra/postgres/init not found above " + AppContext.BaseDirectory);
        return Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
    }

    public static string MigrationsDir()
    {
        var output = Path.Combine(AppContext.BaseDirectory, "Data", "Migrations");
        return Directory.Exists(output) ? output : RepoPath("gateway", "src", "Aiq.Gateway", "Data", "Migrations");
    }

    /// <summary>A hand-run down script (never copied to the output, never run by the migrator).</summary>
    public static string DownScript(string file) => RepoPath("gateway", "src", "Aiq.Gateway", "Data", "Migrations", "down", file);

    /// <summary>
    /// Gateway migrations: one transaction per file, as DatabaseMigrator does.
    /// Init scripts: plain execution, as psql/docker-entrypoint does (some
    /// manage their own BEGIN/COMMIT).
    /// </summary>
    public async Task RunFilesAsync(string dir, Func<string, bool> include, bool transactional = true)
    {
        await using var db = new NpgsqlConnection(ConnectionString);
        await db.OpenAsync();
        foreach (var path in Directory.GetFiles(dir, "*.sql").Order(StringComparer.Ordinal))
        {
            if (!include(Path.GetFileName(path))) continue;
            await using var tx = transactional ? await db.BeginTransactionAsync() : null;
            // psql meta-commands (\echo, \d) in the init scripts are not SQL.
            var sql = string.Join("\n", (await File.ReadAllLinesAsync(path)).Where(l => !l.TrimStart().StartsWith('\\')));
            await db.ExecuteAsync(new CommandDefinition(sql, transaction: tx, commandTimeout: 120));
            if (tx is not null) await tx.CommitAsync();
        }
    }

    public static bool Before057(string f) => string.CompareOrdinal(f, "057") < 0;
    public static bool Is057(string f) => f.StartsWith("057_", StringComparison.Ordinal);

    public async Task InitializeAsync()
    {
        if (BaseConnectionString is null) return;
        await using (var admin = new NpgsqlConnection(BaseConnectionString))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync($"CREATE DATABASE {DatabaseName}");
        }
        ConnectionString = new NpgsqlConnectionStringBuilder(BaseConnectionString) { Database = DatabaseName, Pooling = false }.ConnectionString;
        await RunFilesAsync(RepoPath("infra", "postgres", "init"), _ => true, transactional: false);
        await using (var db = new NpgsqlConnection(ConnectionString))
        {
            await db.OpenAsync(); // created by DatabaseMigrator before any file runs
            await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS schema_migrations (version TEXT PRIMARY KEY, checksum TEXT, applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW())");
        }
        await RunFilesAsync(MigrationsDir(), Before057);
        await SeedAsync();
        await RunFilesAsync(MigrationsDir(), Is057);
        await RunFilesAsync(MigrationsDir(), f => string.CompareOrdinal(f, "058") >= 0);
    }

    private async Task SeedAsync()
    {
        Tenant = Guid.NewGuid(); Dept = Guid.NewGuid(); OtherDept = Guid.NewGuid();
        Admin = Guid.NewGuid(); Admin2 = Guid.NewGuid(); Admin3 = Guid.NewGuid(); User = Guid.NewGuid();
        await using var db = new NpgsqlConnection(ConnectionString);
        await db.OpenAsync();
        await db.ExecuteAsync("""
            INSERT INTO tenants(id, code, name) VALUES (@t, @code, 'Sáu Trực test');
            INSERT INTO departments(id, tenant_id, code, name) VALUES (@d, @t, 'KT', 'Kế toán'), (@od, @t, 'KD', 'Kinh doanh');
            INSERT INTO users(id, email, password_hash, tenant_id) VALUES
                (@a, @ae, 'x', @t), (@a2, @a2e, 'x', @t), (@a3, @a3e, 'x', @t), (@u, @ue, 'x', @t);
            """, new { t = Tenant, code = "T" + Tenant.ToString("N")[..8], d = Dept, od = OtherDept,
                       a = Admin, ae = $"admin-{Admin:N}@t.vn", a2 = Admin2, a2e = $"admin2-{Admin2:N}@t.vn", a3 = Admin3, a3e = $"admin3-{Admin3:N}@t.vn", u = User, ue = $"user-{User:N}@t.vn" });
        // A legacy, unattested 'verified' row as the old Agent tool left it.
        LegacyVoucher = await db.ExecuteScalarAsync<Guid>("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, status, document_number, document_date, seller_mst, total_amount,
                                 tax_verification_status, tct_verified)
            VALUES (@u, @t, @d, 'invoice_vat_in', 'extracted', 'LEGACY-1', '2026-09-20', '0312345678', 1000000, 'verified', TRUE) RETURNING id
            """, new { u = User, t = Tenant, d = Dept });
    }

    public async Task DisposeAsync()
    {
        if (BaseConnectionString is null) return;
        await using var admin = new NpgsqlConnection(BaseConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {DatabaseName} WITH (FORCE)");
    }
}

/// <summary>
/// Tax verification gate hardening against the REAL schema (see
/// <see cref="TaxVerificationDatabase"/>): vouchers / expense claim / event
/// tables and the attestation CHECK are exactly production's.
/// </summary>
public sealed class TaxVerificationIntegrationTests(TaxVerificationDatabase fx) : IClassFixture<TaxVerificationDatabase>
{
    static TaxVerificationIntegrationTests()
    {
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyTypeHandler());
    }

    private string? _cs => fx.ConnectionString;
    private Guid _tenant => fx.Tenant;
    private Guid _dept => fx.Dept;
    private Guid _otherDept => fx.OtherDept;
    private Guid _admin => fx.Admin;
    private Guid _admin2 => fx.Admin2;
    private Guid _user => fx.User;
    private Guid _legacyVoucher => fx.LegacyVoucher;
    private Task ApplyMigrationsAsync(Func<string, bool> include) => fx.RunFilesAsync(TaxVerificationDatabase.MigrationsDir(), include);
    private static bool Is057(string f) => TaxVerificationDatabase.Is057(f);

    // ---------------------------------------------------------------- helpers

    private NpgsqlConnectionFactory Factory() => new(_cs!);
    private ITaxVerificationRepository Repo() => new TaxVerificationRepository(Factory());
    private IVoucherRepository Vouchers() => new VoucherRepository(Factory());

    /// <summary>For queries that must yield a value; NULL or no row fails the test loudly.</summary>
    private async Task<T> Scalar<T>(string sql, object? args = null) where T : notnull
    {
        await using var c = new NpgsqlConnection(_cs);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<T>(sql, args)
            ?? throw new InvalidOperationException($"Scalar query returned NULL or no row: {sql}");
    }

    /// <summary>For queries where SQL NULL / no row is a valid, asserted outcome.</summary>
    private async Task<T?> ScalarOrNull<T>(string sql, object? args = null)
    {
        await using var c = new NpgsqlConnection(_cs);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<T>(sql, args);
    }

    private static readonly DateOnly InvDate = new(2026, 9, 20);

    private Task<Guid> InvoiceVoucherAsync(string number, decimal total, Guid? createdBy = null, Guid? dept = null) =>
        Scalar<Guid>("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, status, document_number, document_series, document_date, seller_mst, total_amount)
            VALUES (@u, @t, @d, 'invoice_vat_in', 'extracted', @number, '1C26TAA', @date, '0312345678', @total) RETURNING id
            """, new { u = createdBy ?? _user, t = _tenant, d = dept ?? _dept, number, date = InvDate, total });

    private static object[] ClaimItems(decimal secondAmount = 2_200_000m) =>
    [
        new { category = "cong_tac", description = "Khách sạn", amount = 1_100_000m, vendor_mst = "0301111111", invoice_no = "0000123", document_date = "2026-09-18" },
        new { category = "cong_tac", description = "Vé máy bay", amount = secondAmount, vendor_mst = "0302222222", invoice_no = "456", document_date = "2026-09-17" },
        new { category = "an_uong", description = "Taxi (không hoá đơn)", amount = 150_000m },
    ];

    private async Task<Guid> ClaimVoucherAsync(Guid claimant)
    {
        var id = await Scalar<Guid>("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, status, document_number, document_date, total_amount, extracted_fields)
            VALUES (@u, @t, @d, 'expense_claim', 'extracted', 'DNTT-001', @date, 3450000, @fields::jsonb) RETURNING id
            """, new { u = claimant, t = _tenant, d = _dept, date = InvDate, fields = JsonSerializer.Serialize(new { items = ClaimItems() }) });
        await Scalar<int>("""
            INSERT INTO expense_claim_results(tenant_id, department_id, voucher_id, outcome, requires_tax_verification)
            VALUES (@t, @d, @id, 'APPROVED', TRUE) RETURNING 1
            """, new { t = _tenant, d = _dept, id });
        return id;
    }

    private static TaxConfirmRequest Evidence(params TaxAttestedInvoice[] invoices) =>
        new(TaxVerificationSources.TctPortal, "Đã tra cứu trên hoadondientu.gdt.gov.vn, trạng thái: Hoá đơn hợp lệ", true, invoices);

    private static TaxAttestedInvoice Inv(string mst, string number, DateOnly date, decimal total, string? series = null) =>
        new(mst, series, number, date, total, "LOOKUP-1");

    private readonly string _inv = $"INV-{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    private TaxConfirmRequest InvoiceEvidence(string? number = null, decimal total = 11_000_000m) =>
        Evidence(Inv("0312345678", number ?? _inv, InvDate, total, "1C26TAA"));

    private Task<VoucherRecord?> VoucherAsync(Guid id) => Vouchers().FindInDepartmentAsync(_tenant, _dept, id, default).ContinueWith(t => t.Result?.Voucher);

    private Task<int> EventsAsync(Guid id, string evt) =>
        Scalar<int>("SELECT COUNT(*) FROM voucher_tax_verification_events WHERE voucher_id=@id AND event=@evt", new { id, evt });

    // ------------------------------------------------------------- migration

    [Fact]
    public async Task Migration057_DowngradesUnattestedLegacyVerification_AndIsIdempotent()
    {
        if (_cs is null) return;
        var row = await Scalar<string>("SELECT tax_verification_status || '|' || tct_verified FROM vouchers WHERE id=@id", new { id = _legacyVoucher });
        Assert.Equal("pending_review|false", row);
        Assert.Equal(1, await EventsAsync(_legacyVoucher, "legacy_unattested"));
        Assert.False(TaxVerificationRules.IsVerifiedForCurrentData((await VoucherAsync(_legacyVoucher))!));

        await ApplyMigrationsAsync(Is057); // re-apply: no new event, nothing changes
        Assert.Equal(1, await EventsAsync(_legacyVoucher, "legacy_unattested"));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM pg_constraint WHERE conname='chk_tax_verified_is_attested' AND convalidated"));
    }

    [Fact]
    public async Task Database_RejectsAnUnattestedVerifiedRow()
    {
        if (_cs is null) return;
        var id = await InvoiceVoucherAsync(_inv, 11_000_000m);
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            Scalar<int>("UPDATE vouchers SET tax_verification_status='verified' WHERE id=@id RETURNING 1", new { id }));
        Assert.Equal("chk_tax_verified_is_attested", ex.ConstraintName);
    }

    // ------------------------------------------------------- propose / confirm

    [Fact]
    public async Task AgentProposal_IsOnlyPendingReview_NeverPassesTheGate()
    {
        if (_cs is null) return;
        var id = await InvoiceVoucherAsync(_inv, 11_000_000m);
        var view = (await Repo().ProposeAsync(_tenant, _dept, id, _admin, "agent", "admin", "verified", """{"tct_status":"http_200"}""", null, default))!;
        Assert.Equal((TaxVerificationStatuses.PendingReview, false), (view.Status, view.EffectivelyVerified));
        Assert.Equal(("proposed", "agent"), (view.Events[^1].Event, view.Events[^1].ActorType));
        Assert.False(TaxVerificationRules.IsVerifiedForCurrentData((await VoucherAsync(id))!));
        Assert.False(await Scalar<bool>("SELECT tct_verified FROM vouchers WHERE id=@id", new { id }));
    }

    [Fact]
    public async Task AdminConfirm_WithMatchingEvidence_Verifies_BindsFingerprint_AndAudits()
    {
        if (_cs is null) return;
        var id = await InvoiceVoucherAsync(_inv, 11_000_000m);
        // Leading zeros / spacing / case differences are the same invoice.
        var view = (await Repo().ConfirmAsync(_tenant, _dept, id, _admin,
            Evidence(Inv(" 0312345678 ", " " + _inv.ToLowerInvariant() + " ", InvDate, 11_000_000.00m, "1c26taa")), default))!;
        Assert.True(view.EffectivelyVerified);
        Assert.Equal((TaxVerificationStatuses.Verified, _admin, false), (view.Status, view.VerifiedBy, view.Stale));
        var v = (await VoucherAsync(id))!;
        Assert.True(TaxVerificationRules.IsVerifiedForCurrentData(v));
        Assert.Equal(TaxVerificationRules.Fingerprint(TaxVerificationRules.IdentitiesOf(v)), v.TaxVerificationFingerprint);
        var evidence = await Scalar<string>("SELECT tax_verification_evidence::text FROM vouchers WHERE id=@id", new { id });
        Assert.Contains("\"verification_source\": \"admin_manual\"", evidence);
        Assert.Contains("\"evidence_source\": \"tct_portal\"", evidence);
        Assert.Contains("\"checked_tax_codes\": [\"0312345678\"]", evidence);
        Assert.Contains("LOOKUP-1", evidence);
        Assert.Equal("admin|admin_manual", await Scalar<string>(
            "SELECT actor_role || '|' || source FROM voucher_tax_verification_events WHERE voucher_id=@id AND event='verified'", new { id }));
        Assert.Equal(1, await EventsAsync(id, "verified"));
        Assert.True(await Scalar<bool>("SELECT tct_verified FROM vouchers WHERE id=@id", new { id }));
    }

    [Theory]
    [InlineData("tct_portal", "ngắn", true, "evidence_note_required")]
    [InlineData("tct_portal", "Đã tra cứu trên cổng, hợp lệ", false, "evidence_attestation_required")]
    [InlineData("google", "Đã tra cứu trên cổng, hợp lệ", true, "evidence_source_invalid")]
    public async Task Confirm_RequiresSourceNoteAndExplicitAttestation(string source, string note, bool checkedOnPortal, string code)
    {
        if (_cs is null) return;
        var id = await InvoiceVoucherAsync(_inv, 11_000_000m);
        var ex = await Assert.ThrowsAsync<TaxVerificationException>(() => Repo().ConfirmAsync(_tenant, _dept, id, _admin,
            new TaxConfirmRequest(source, note, checkedOnPortal, new[] { Inv("0312345678", _inv, InvDate, 11_000_000m, "1C26TAA") }), default));
        Assert.Equal(code, ex.Code);
        Assert.Null(await ScalarOrNull<string?>("SELECT tax_verification_status FROM vouchers WHERE id=@id", new { id }));
    }

    [Fact]
    public async Task Confirm_EvidenceThatDoesNotMatchTheVoucher_IsRefused()
    {
        if (_cs is null) return;
        var id = await InvoiceVoucherAsync(_inv, 11_000_000m);
        var wrongAmount = await Assert.ThrowsAsync<TaxVerificationException>(() =>
            Repo().ConfirmAsync(_tenant, _dept, id, _admin, InvoiceEvidence(total: 10_000_000m), default));
        Assert.Equal("evidence_mismatch", wrongAmount.Code);
        Assert.Contains("total_amount", JsonSerializer.Serialize(wrongAmount.Details));

        var otherInvoice = await Assert.ThrowsAsync<TaxVerificationException>(() =>
            Repo().ConfirmAsync(_tenant, _dept, id, _admin, InvoiceEvidence(number: "INV-NOT-THIS"), default));
        var problems = JsonSerializer.Serialize(otherInvoice.Details);
        Assert.Contains("missing", problems);
        Assert.Contains("not_on_voucher", problems);

        var wrongSeries = await Assert.ThrowsAsync<TaxVerificationException>(() => Repo().ConfirmAsync(_tenant, _dept, id, _admin,
            Evidence(Inv("0312345678", _inv, InvDate, 11_000_000m, "1C26TBB")), default));
        Assert.Contains("invoice_series", JsonSerializer.Serialize(wrongSeries.Details));

        Assert.Null(await ScalarOrNull<string?>("SELECT tax_verification_status FROM vouchers WHERE id=@id", new { id }));
        Assert.Equal(0, await EventsAsync(id, "verified"));
    }

    // ------------------------------------------------------------ expense claim

    [Fact]
    public async Task ExpenseClaim_ClaimantAdminMayVerify_ButMustAttestEveryInvoice()
    {
        if (_cs is null) return;
        // ADMIN Department Authority Invariant: the ADMIN who created the claim attests it.
        var id = await ClaimVoucherAsync(claimant: _admin);
        var all = Evidence(Inv("0301111111", "123", new DateOnly(2026, 9, 18), 1_100_000m), Inv("0302222222", "456", new DateOnly(2026, 9, 17), 2_200_000m));

        var partial = await Assert.ThrowsAsync<TaxVerificationException>(() => Repo().ConfirmAsync(_tenant, _dept, id, _admin,
            Evidence(Inv("0301111111", "123", new DateOnly(2026, 9, 18), 1_100_000m)), default));
        Assert.Equal("evidence_mismatch", partial.Code);

        var view = (await Repo().ConfirmAsync(_tenant, _dept, id, _admin, all, default))!;
        Assert.True(view.EffectivelyVerified);
        Assert.True(view.RequiredForPayment);
        Assert.Equal(2, view.Invoices.Count); // the taxi line has no invoice
    }

    [Fact]
    public async Task ExpenseClaim_ItemChangedByReExtraction_InvalidatesVerification()
    {
        if (_cs is null) return;
        var id = await ClaimVoucherAsync(claimant: _user);
        await Repo().ConfirmAsync(_tenant, _dept, id, _admin, Evidence(
            Inv("0301111111", "123", new DateOnly(2026, 9, 18), 1_100_000m), Inv("0302222222", "456", new DateOnly(2026, 9, 17), 2_200_000m)), default);
        Assert.True(TaxVerificationRules.IsVerifiedForCurrentData((await VoucherAsync(id))!));

        await Vouchers().ReplaceExtractedAsync(_tenant, _user, id, new CreateVoucherRequest(
            "expense_claim", "DNTT-001", null, InvDate, null, null, null, null, null, null, null, null, null, null, null, 3_550_000m,
            "VND", null, null, null, null, null, null, new { items = ClaimItems(secondAmount: 2_300_000m) }, null, null), default);

        var v = (await VoucherAsync(id))!;
        Assert.Null(v.TaxVerificationStatus);
        Assert.Null(v.TaxVerifiedBy);
        Assert.False(TaxVerificationRules.IsVerifiedForCurrentData(v));
        Assert.Equal(1, await EventsAsync(id, "invalidated"));
        Assert.Contains("tct_portal", await Scalar<string>(
            "SELECT evidence::text FROM voucher_tax_verification_events WHERE voucher_id=@id AND event='invalidated'", new { id }));
    }

    // ----------------------------------------------------------- invalidation

    [Fact]
    public async Task EditingIdentityField_Invalidates_EditingOtherFields_DoesNot()
    {
        if (_cs is null) return;
        var id = await InvoiceVoucherAsync(_inv, 11_000_000m);
        await Repo().ConfirmAsync(_tenant, _dept, id, _admin, InvoiceEvidence(), default);

        UpdateVoucherRequest Edit(string? buyer = null, decimal? total = null) =>
            new(null, null, null, null, null, null, null, buyer, null, null, null, null, null, null, null, total, null, null, null, null);

        var notIdentity = (await Vouchers().UpdateAsync(_tenant, _user, id, Edit(buyer: "Sáu Trực"), default))!;
        Assert.Equal(TaxVerificationStatuses.Verified, notIdentity.TaxVerificationStatus);
        Assert.Equal(0, await EventsAsync(id, "invalidated"));

        var edited = (await Vouchers().UpdateAsync(_tenant, _user, id, Edit(total: 12_000_000m), default))!;
        Assert.Null(edited.TaxVerificationStatus);
        Assert.False(await Scalar<bool>("SELECT tct_verified FROM vouchers WHERE id=@id", new { id }));
        Assert.Equal(1, await EventsAsync(id, "invalidated"));

        // Re-verifying the corrected data works and needs the NEW amount.
        Assert.Equal("evidence_mismatch", (await Assert.ThrowsAsync<TaxVerificationException>(() =>
            Repo().ConfirmAsync(_tenant, _dept, id, _admin, InvoiceEvidence(), default))).Code);
        Assert.True((await Repo().ConfirmAsync(_tenant, _dept, id, _admin, InvoiceEvidence(total: 12_000_000m), default))!.EffectivelyVerified);
    }

    [Fact]
    public async Task VoucherTypeChange_Invalidates()
    {
        if (_cs is null) return;
        var id = await InvoiceVoucherAsync(_inv, 11_000_000m);
        await Repo().ConfirmAsync(_tenant, _dept, id, _admin, InvoiceEvidence(), default);
        Assert.True(await Vouchers().SetVoucherTypeClassificationAsync(_tenant, _dept, id, "receipt", "{}", default));
        Assert.Null(await ScalarOrNull<string?>("SELECT tax_verification_status FROM vouchers WHERE id=@id", new { id }));
        Assert.Equal(1, await EventsAsync(id, "invalidated"));
    }

    [Fact]
    public void Gate_StaleFingerprint_DoesNotPass()
    {
        VoucherRecord V(string? fingerprint, Guid? by) => new()
        {
            VoucherType = "invoice_vat_in", DocumentNumber = "INV-1", SellerMst = "0312345678", DocumentDate = InvDate, TotalAmount = 11_000_000m,
            TaxVerificationStatus = "verified", TaxVerifiedBy = by, TaxVerificationFingerprint = fingerprint,
        };
        var current = TaxVerificationRules.Fingerprint(TaxVerificationRules.IdentitiesOf(V(null, null)));
        Assert.False(TaxVerificationRules.IsVerifiedForCurrentData(V("v1:stale", Guid.NewGuid())));
        Assert.True(TaxVerificationRules.IsVerifiedForCurrentData(V(current, Guid.NewGuid())));
        Assert.False(TaxVerificationRules.IsVerifiedForCurrentData(V(current, null)));
    }

    // -------------------------------------------------- reject / propose rules

    private static TaxRejectRequest AdminReject(string reason = "Tra cứu cổng TCT: hoá đơn đã bị thay thế", bool unavailable = false) =>
        new(reason, unavailable, TaxVerificationSources.TctPortal, "LOOKUP-NEG");

    private Task<string?> StatusAsync(Guid id) => ScalarOrNull<string?>("SELECT tax_verification_status FROM vouchers WHERE id=@id", new { id });

    [Fact]
    public async Task UserOrAgentReject_IsRefused_NoAuthoritativeMutation()
    {
        if (_cs is null) return;
        var id = await InvoiceVoucherAsync(_inv, 11_000_000m);
        foreach (var (type, role) in new[] { ("user", "user"), ("agent", "user"), ("agent", "admin") })
        {
            var ex = await Assert.ThrowsAsync<TaxVerificationException>(() =>
                Repo().RejectAsync(_tenant, _dept, id, type == "agent" && role == "admin" ? _admin : _user, type, role, AdminReject(), default));
            Assert.Equal("admin_required", ex.Code);
        }
        Assert.Null(await StatusAsync(id));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM voucher_tax_verification_events WHERE voucher_id=@id", new { id }));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("unavailable")]
    public async Task AgentNegativeProposal_StaysPendingReview_RecommendationRecorded(string recommendation)
    {
        if (_cs is null) return;
        var id = await ClaimVoucherAsync(claimant: _user);
        var view = (await Repo().ProposeAsync(_tenant, _dept, id, _user, "agent", "user", recommendation,
            """{"tool":"verify_einvoice_tct","note":"TCT trả 404"}""", "TCT trả 404", default))!;
        Assert.Equal((TaxVerificationStatuses.PendingReview, recommendation), (view.Status, view.ProposedRecommendation));
        Assert.Null(await ScalarOrNull<string?>("SELECT tax_verification_evidence::text FROM vouchers WHERE id=@id", new { id }));
        var evt = await Scalar<string>("SELECT source || '|' || (evidence->>'recommendation') FROM voucher_tax_verification_events WHERE voucher_id=@id", new { id });
        Assert.Equal($"agent_proposal|{recommendation}", evt);
        // Neither opens nor "closes" the gate authoritatively: still the not-yet-decided state.
        Assert.Equal("tax_verification_required", TaxVerificationRules.ExpenseGateError((await VoucherAsync(id))!, true));
    }

    [Fact]
    public async Task UnknownRecommendation_IsRejected()
    {
        if (_cs is null) return;
        var id = await InvoiceVoucherAsync(_inv, 11_000_000m);
        Assert.Equal("recommendation_invalid", (await Assert.ThrowsAsync<TaxVerificationException>(() =>
            Repo().ProposeAsync(_tenant, _dept, id, _user, "agent", "user", "verified_final", null, null, default))).Code);
        Assert.Null(await StatusAsync(id));
    }

    [Fact]
    public async Task AdminReject_WithEvidence_IsAuthoritativeInvalid_AuditedWithEvidence()
    {
        if (_cs is null) return;
        var id = await InvoiceVoucherAsync(_inv, 11_000_000m);
        var view = (await Repo().RejectAsync(_tenant, _dept, id, _admin, "user", "admin", AdminReject(), default))!;
        Assert.Equal((TaxVerificationStatuses.Invalid, false), (view.Status, view.EffectivelyVerified));
        var evidence = await Scalar<string>("SELECT tax_verification_evidence::text FROM vouchers WHERE id=@id", new { id });
        Assert.Contains("\"verification_source\": \"admin_manual\"", evidence);
        Assert.Contains("\"result\": \"invalid\"", evidence);
        Assert.Contains("LOOKUP-NEG", evidence);
        Assert.Equal("invalid|admin|admin_manual", await Scalar<string>(
            "SELECT event || '|' || actor_role || '|' || source FROM voucher_tax_verification_events WHERE voucher_id=@id", new { id }));

        var unavailable = await InvoiceVoucherAsync(_inv + "-U", 11_000_000m);
        Assert.Equal(TaxVerificationStatuses.Unavailable,
            (await Repo().RejectAsync(_tenant, _dept, unavailable, _admin, "user", "admin", AdminReject("Cổng TCT không phản hồi khi tra cứu", true), default))!.Status);
    }

    [Theory]
    [InlineData("", "tct_portal", "evidence_reason_required")]
    [InlineData("ngắn", "tct_portal", "evidence_reason_required")]
    [InlineData("Tra cứu cổng TCT: hoá đơn đã huỷ", null, "evidence_source_invalid")]
    [InlineData("Tra cứu cổng TCT: hoá đơn đã huỷ", "llm_opinion", "evidence_source_invalid")]
    public async Task AdminReject_WithoutEvidence_IsRefused(string reason, string? source, string code)
    {
        if (_cs is null) return;
        var id = await InvoiceVoucherAsync(_inv, 11_000_000m);
        Assert.Equal(code, (await Assert.ThrowsAsync<TaxVerificationException>(() =>
            Repo().RejectAsync(_tenant, _dept, id, _admin, "user", "admin", new TaxRejectRequest(reason, false, source, null), default))).Code);
        Assert.Null(await StatusAsync(id));
    }

    [Fact]
    public async Task Proposals_NeverOverrideAnAdminDecision_VerifiedOrInvalid()
    {
        if (_cs is null) return;
        var verified = await InvoiceVoucherAsync(_inv, 11_000_000m);
        await Repo().ConfirmAsync(_tenant, _dept, verified, _admin, InvoiceEvidence(), default);
        var p1 = (await Repo().ProposeAsync(_tenant, _dept, verified, _user, "agent", "user", "invalid", null, "LLM: không hợp lệ", default))!;
        Assert.Equal((TaxVerificationStatuses.Verified, true), (p1.Status, p1.EffectivelyVerified));
        Assert.Null(TaxVerificationRules.ExpenseGateError((await VoucherAsync(verified))!, true)); // gate stays open

        var invalid = await InvoiceVoucherAsync(_inv + "-I", 11_000_000m);
        await Repo().RejectAsync(_tenant, _dept, invalid, _admin, "user", "admin", AdminReject(), default);
        var p2 = (await Repo().ProposeAsync(_tenant, _dept, invalid, _user, "user", "user", "verified", null, "Tôi nghĩ hợp lệ", default))!;
        Assert.Equal(TaxVerificationStatuses.Invalid, p2.Status);
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM voucher_tax_verification_events WHERE voucher_id=@id AND event='proposed'", new { id = invalid }));
    }

    [Fact]
    public async Task AdminMayOverturnAVerification_WithEvidence_HistoryKept()
    {
        if (_cs is null) return;
        var id = await InvoiceVoucherAsync(_inv, 11_000_000m);
        await Repo().ConfirmAsync(_tenant, _dept, id, _admin, InvoiceEvidence(), default);
        var view = (await Repo().RejectAsync(_tenant, _dept, id, _admin2, "user", "admin", AdminReject(), default))!;
        Assert.Equal((TaxVerificationStatuses.Invalid, (Guid?)null), (view.Status, view.VerifiedBy));
        Assert.Equal(new[] { "verified", "invalid" }, view.Events.Select(e => e.Event).ToArray());
        Assert.Contains("tct_portal", view.Events[0].EvidenceJson); // the original verification evidence is preserved
    }

    [Fact]
    public async Task OtherDepartmentOrTenant_SeesNothing_AndCannotAct()
    {
        if (_cs is null) return;
        var id = await InvoiceVoucherAsync(_inv, 11_000_000m);
        Assert.Null(await Repo().GetAsync(_tenant, _otherDept, id, default));
        Assert.Null(await Repo().ConfirmAsync(_tenant, _otherDept, id, _admin, InvoiceEvidence(), default));
        Assert.Null(await Repo().ConfirmAsync(Guid.NewGuid(), _dept, id, _admin, InvoiceEvidence(), default));
        Assert.Null(await ScalarOrNull<string?>("SELECT tax_verification_status FROM vouchers WHERE id=@id", new { id }));
    }

    [Fact]
    public async Task NotVerifiable_VoucherWithoutInvoice()
    {
        if (_cs is null) return;
        var id = await Scalar<Guid>("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, status, total_amount)
            VALUES (@u, @t, @d, 'receipt', 'extracted', 1000) RETURNING id
            """, new { u = _user, t = _tenant, d = _dept });
        Assert.Equal("not_verifiable", (await Assert.ThrowsAsync<TaxVerificationException>(() =>
            Repo().ConfirmAsync(_tenant, _dept, id, _admin, InvoiceEvidence(), default))).Code);
    }

    // ------------------------------------------------ spec security cases

    [Fact]
    public async Task BrowserSuppliedAuthorityFields_AreIgnored()
    {
        if (_cs is null) return;
        var id = await InvoiceVoucherAsync(_inv, 11_000_000m);
        // Exactly what a tampered browser could send; bound like the endpoint does (web defaults).
        var json = $$"""
            { "tenant_id": "{{Guid.NewGuid()}}", "department_id": "{{Guid.NewGuid()}}", "verified_by": "{{_user}}",
              "is_verified": true, "tax_verification_status": "verified",
              "source": "tct_portal", "note": "Đã tra cứu trên cổng TCT, hợp lệ", "portalChecked": true,
              "invoices": [ { "sellerMst": "0312345678", "invoiceSeries": "1C26TAA", "invoiceNumber": "{{_inv}}",
                              "invoiceDate": "2026-09-20", "totalAmount": 11000000, "lookupCode": "X1" } ] }
            """;
        var request = JsonSerializer.Deserialize<TaxConfirmRequest>(json, JsonSerializerOptions.Web)!;
        var view = (await Repo().ConfirmAsync(_tenant, _dept, id, _admin, request, default))!;
        Assert.Equal(_admin, view.VerifiedBy); // from the authenticated actor, not the body
        Assert.Equal(_tenant, await Scalar<Guid>("SELECT tenant_id FROM vouchers WHERE id=@id", new { id }));
        Assert.Equal(_dept, await Scalar<Guid>("SELECT department_id FROM vouchers WHERE id=@id", new { id }));
        Assert.DoesNotContain(_user.ToString(), await Scalar<string>("SELECT tax_verification_evidence::text FROM vouchers WHERE id=@id", new { id }));
    }

    [Fact]
    public async Task ReplayingTheSameConfirmation_IsIdempotent_FirstEvidenceKept()
    {
        if (_cs is null) return;
        var id = await InvoiceVoucherAsync(_inv, 11_000_000m);
        await Repo().ConfirmAsync(_tenant, _dept, id, _admin, InvoiceEvidence(), default);
        var evidence = await Scalar<string>("SELECT tax_verification_evidence::text FROM vouchers WHERE id=@id", new { id });
        var replay = (await Repo().ConfirmAsync(_tenant, _dept, id, _admin2, InvoiceEvidence(), default))!;
        Assert.Equal(_admin, replay.VerifiedBy);
        Assert.Equal(evidence, await Scalar<string>("SELECT tax_verification_evidence::text FROM vouchers WHERE id=@id", new { id }));
        Assert.Equal(1, await EventsAsync(id, "verified"));
    }

    [Fact]
    public async Task ConcurrentConfirmations_ProduceExactlyOneVerification()
    {
        if (_cs is null) return;
        var id = await InvoiceVoucherAsync(_inv, 11_000_000m);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            Repo().ConfirmAsync(_tenant, _dept, id, i % 2 == 0 ? _admin : _admin2, InvoiceEvidence(), default)));
        Assert.All(results, r => Assert.True(r!.EffectivelyVerified));
        Assert.Equal(1, await EventsAsync(id, "verified"));
        var by = await Scalar<Guid>("SELECT tax_verified_by FROM vouchers WHERE id=@id", new { id });
        Assert.All(results, r => Assert.Equal(by, r!.VerifiedBy));
    }

    [Fact]
    public async Task ExistingVerification_EvidenceAndHistory_SurviveLaterAgentActivity()
    {
        if (_cs is null) return;
        var id = await InvoiceVoucherAsync(_inv, 11_000_000m);
        await Repo().ConfirmAsync(_tenant, _dept, id, _admin, InvoiceEvidence(), default);
        var evidence = await Scalar<string>("SELECT tax_verification_evidence::text FROM vouchers WHERE id=@id", new { id });

        await Repo().ProposeAsync(_tenant, _dept, id, _admin, "agent", "admin", "invalid", """{"result":"Tax code invalid"}""", null, default);
        Assert.Equal("admin_required", (await Assert.ThrowsAsync<TaxVerificationException>(() =>
            Repo().RejectAsync(_tenant, _dept, id, _admin, "agent", "admin", AdminReject(), default))).Code); // agent with an ADMIN's token

        Assert.Equal(evidence, await Scalar<string>("SELECT tax_verification_evidence::text FROM vouchers WHERE id=@id", new { id }));
        Assert.Equal(TaxVerificationStatuses.Verified, await Scalar<string>("SELECT tax_verification_status FROM vouchers WHERE id=@id", new { id }));
        Assert.Equal(1, await EventsAsync(id, "verified"));
        Assert.Equal(1, await EventsAsync(id, "proposed"));
    }

    // ------------------------------------------------------- LLM safety

    [Fact]
    public async Task LlmClaimingVerifiedSuccessfully_WithoutTrustedEvidence_IsNeverVerified()
    {
        if (_cs is null) return;
        var id = await ClaimVoucherAsync(claimant: _user);
        var llm = """{"model_output":"Tax code verified successfully","is_verified":true,"tax_verified":true,"tct_status":"http_200"}""";
        var view = (await Repo().ProposeAsync(_tenant, _dept, id, _admin, "agent", "admin", "verified", llm, "verified", default))!;
        Assert.Equal(TaxVerificationStatuses.PendingReview, view.Status);
        Assert.False(view.EffectivelyVerified);
        Assert.Null(view.VerifiedBy);
        Assert.Equal("agent_proposal", await Scalar<string>("SELECT source FROM voucher_tax_verification_events WHERE voucher_id=@id", new { id }));
        Assert.Equal("tax_verification_required", TaxVerificationRules.ExpenseGateError((await VoucherAsync(id))!, requiresTaxVerification: true));
    }

    // ------------------------------------------- expense claim gate regression

    [Fact]
    public async Task ExpenseGate_ReadsOnlyAuthoritativeState()
    {
        if (_cs is null) return;
        var notChecked = await ClaimVoucherAsync(claimant: _user);
        // A boolean in the extracted JSON is not authority.
        await Scalar<int>("""UPDATE vouchers SET extracted_fields = extracted_fields || '{"tct_verified":true,"tax_verified":true}'::jsonb WHERE id=@id RETURNING 1""", new { id = notChecked });
        Assert.Equal("tax_verification_required", TaxVerificationRules.ExpenseGateError((await VoucherAsync(notChecked))!, true));

        var failed = await ClaimVoucherAsync(claimant: _user);
        await Repo().RejectAsync(_tenant, _dept, failed, _admin, "user", "admin", AdminReject("Hoá đơn đã huỷ trên cổng TCT"), default);
        Assert.Equal("tax_verification_required", TaxVerificationRules.ExpenseGateError((await VoucherAsync(failed))!, true));

        var verified = await ClaimVoucherAsync(claimant: _user);
        await Repo().ConfirmAsync(_tenant, _dept, verified, _admin, Evidence(
            Inv("0301111111", "123", new DateOnly(2026, 9, 18), 1_100_000m), Inv("0302222222", "456", new DateOnly(2026, 9, 17), 2_200_000m)), default);
        Assert.Null(TaxVerificationRules.ExpenseGateError((await VoucherAsync(verified))!, true));

        // A claim without invoices is not gated (unchanged behaviour).
        Assert.Null(TaxVerificationRules.ExpenseGateError((await VoucherAsync(notChecked))!, requiresTaxVerification: false));
    }

    [Fact]
    public async Task LegacyAgentConfirmCall_WithoutBody_IsRejected_NotVerified()
    {
        if (_cs is null) return;
        var id = await InvoiceVoucherAsync(_inv, 11_000_000m);
        Assert.Equal("evidence_required", (await Assert.ThrowsAsync<TaxVerificationException>(() =>
            Repo().ConfirmAsync(_tenant, _dept, id, _admin, null!, default))).Code);
        Assert.Null(await ScalarOrNull<string?>("SELECT tax_verification_status FROM vouchers WHERE id=@id", new { id }));
    }
}
