using System.Security.Claims;
using System.Text.Json;
using Aiq.Gateway.Data;
using Aiq.Gateway.Endpoints;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;
using Aiq.Gateway.Vouchers;
using Aiq.Gateway.Vouchers.Validation;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Chứng từ flow v2 PR2 / PR2.x against the real schema: GET /vouchers/{id}/validation
/// only computes; a result is stored (with the voucher snapshot of migration 069)
/// when the voucher changes. vat_rates is replaced by this class's own rows before
/// every test, so nothing depends on the 068 seed.
/// </summary>
public sealed class VoucherValidationIntegrationTests(TaxVerificationDatabase fx) : IClassFixture<TaxVerificationDatabase>, IAsyncLifetime
{
    private const string Company = "0312345673";
    private const string Supplier = "0101243150";
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 3, 0, 0, TimeSpan.Zero);

    static VoucherValidationIntegrationTests()
    {
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyTypeHandler());
    }

    // Reduced 8% windows with gaps between them, like the successive decrees the
    // accountant will add; the dates are test data, not a statement of the law.
    public async Task InitializeAsync()
    {
        if (fx.ConnectionString is null) return;
        await ExecAsync("""
            DELETE FROM vat_rates;
            INSERT INTO vat_rates (code, rate, effective_from, effective_to, legal_ref) VALUES
                ('0',  0,  '2000-01-01', NULL,         'test'),
                ('5',  5,  '2000-01-01', NULL,         'test'),
                ('10', 10, '2000-01-01', NULL,         'test'),
                ('8',  8,  '2022-02-01', '2022-12-31', 'test window A'),
                ('8',  8,  '2023-07-01', '2023-12-31', 'test window B'),
                ('8',  8,  '2025-07-01', '2026-12-31', 'test window C');
            """, new { });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private NpgsqlConnectionFactory Factory => new(fx.ConnectionString!);
    private VoucherRepository Repo => new(Factory);

    private VoucherValidationService Service() => new(Repo, new TenantCompanyProfileRepository(Factory),
        new VoucherValidationRepository(Factory), new FixedTime(Now), NullLogger<VoucherValidationService>.Instance);

    // ------------------------------------------------------------ GET = read

    [Fact]
    public async Task Get_computes_the_issues_and_never_writes()
    {
        if (fx.ConnectionString is null) return;
        await CompanyAsync();
        var id = await InvoiceAsync(total: 11_000_500m);
        var updatedAt = await UpdatedAtAsync(id);

        var result = await Get(id, "user", fx.User);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        var report = Value<VoucherValidationReport>(result);
        Assert.True(report.HasErrors);
        Assert.Contains(report.Issues, i => i.Code == "AMOUNT_SUM");
        Assert.Equal(VoucherValidator.Version, report.ValidatorVersion);
        Assert.Equal(updatedAt, report.VoucherUpdatedAt);
        Assert.Null(await StoredAsync(id));
        Assert.Equal(updatedAt, await UpdatedAtAsync(id));
    }

    [Fact]
    public async Task Get_on_a_posted_voucher_touches_neither_the_voucher_nor_the_stored_result()
    {
        if (fx.ConnectionString is null) return;
        await CompanyAsync();
        var id = await InvoiceAsync(status: "verified");
        var before = await UpdatedAtAsync(id);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(await Get(id, "admin", fx.Admin)));

        Assert.Equal(before, await UpdatedAtAsync(id));
        Assert.Null(await StoredAsync(id));
    }

    // ------------------------------------------------------ stored on change

    [Fact]
    public async Task Create_stores_the_result_with_the_voucher_snapshot()
    {
        if (fx.ConnectionString is null) return;
        await CompanyAsync();

        var created = await VoucherEndpoints.CreateAsync(Request(Unique(), total: 1m), Repo, Service(), Ctx("user", fx.User), CancellationToken.None);

        var id = Value<VoucherDetail>(created).Voucher.Id;
        var stored = await StoredAsync(id);
        Assert.NotNull(stored);
        Assert.True(stored.HasErrors);
        Assert.Equal(VoucherValidator.Version, stored.Version);
        Assert.Equal(await UpdatedAtAsync(id), stored.VoucherUpdatedAt);
        // Nothing changed since: GET reports the same snapshot.
        Assert.Equal(stored.Fingerprint, Value<VoucherValidationReport>(await Get(id, "user", fx.User)).VoucherFingerprint);
    }

    [Fact]
    public async Task Stored_issues_use_the_api_json_shape()
    {
        if (fx.ConnectionString is null) return;
        await CompanyAsync();

        var created = await VoucherEndpoints.CreateAsync(Request(Unique(), total: 1m), Repo, Service(), Ctx("user", fx.User), CancellationToken.None);

        using var doc = JsonDocument.Parse(await IssuesJsonAsync(Value<VoucherDetail>(created).Voucher.Id));
        var issue = doc.RootElement.EnumerateArray().First(e => e.GetProperty("code").GetString() == "AMOUNT_SUM");
        Assert.Equal("error", issue.GetProperty("severity").GetString());
        Assert.Equal("total_amount", issue.GetProperty("field").GetString());
    }

    [Fact]
    public async Task Patch_and_apply_extraction_replace_the_stored_result()
    {
        if (fx.ConnectionString is null) return;
        await CompanyAsync();
        var id = await InvoiceAsync(total: 11_000_500m);

        var patched = await VoucherEndpoints.UpdateAsync(id, Patch(total: 11_000_000m), Repo, Service(), Ctx("user", fx.User), CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusOf(patched));
        var afterPatch = await StoredAsync(id);
        Assert.False(afterPatch!.HasErrors);
        Assert.Equal(await UpdatedAtAsync(id), afterPatch.VoucherUpdatedAt);

        var applied = await VoucherEndpoints.ApplyExtractionAsync(id, Request(Unique(), total: 1m), Repo, Service(),
            Ctx("admin", fx.Admin), CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusOf(applied));
        var afterApply = await StoredAsync(id);
        Assert.True(afterApply!.HasErrors);
        Assert.NotEqual(afterPatch.Fingerprint, afterApply.Fingerprint);
        Assert.Equal(await UpdatedAtAsync(id), afterApply.VoucherUpdatedAt);
    }

    [Fact]
    public async Task Journal_decision_stores_a_new_fingerprint_although_updated_at_is_unchanged()
    {
        if (fx.ConnectionString is null) return;
        await CompanyAsync();
        var id = await InvoiceAsync();
        var entry = await JournalAsync(id, "1561", "", 10_000_000m);
        await Service().RecordAsync(fx.Tenant, fx.Dept, id, CancellationToken.None);
        var before = await StoredAsync(id);
        Assert.Contains("JOURNAL_LINE", await IssuesJsonAsync(id));

        var decided = await VoucherEndpoints.DecideJournalAsync(id, entry, new AcceptJournalRequest(false, "sai TK"), Repo, Service(),
            Ctx("admin", fx.Admin), CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(decided));
        var after = await StoredAsync(id);
        Assert.Equal(before!.VoucherUpdatedAt, after!.VoucherUpdatedAt);
        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
        Assert.DoesNotContain("JOURNAL_LINE", await IssuesJsonAsync(id));
    }

    [Fact]
    public async Task A_change_made_outside_the_hooks_shows_up_as_a_stale_fingerprint()
    {
        if (fx.ConnectionString is null) return;
        await CompanyAsync();
        var id = await InvoiceAsync();
        await Service().RecordAsync(fx.Tenant, fx.Dept, id, CancellationToken.None);
        var stored = await StoredAsync(id);

        // Line items live in their own table: vouchers.updated_at does not move.
        await ExecAsync("""
            INSERT INTO voucher_line_items(voucher_id, ordinal, description, quantity, unit_price, subtotal, vat_rate)
            VALUES (@id, 1, 'Hàng', 1, 10000000, 10000000, 10)
            """, new { id });
        var now = Value<VoucherValidationReport>(await Get(id, "user", fx.User));

        Assert.Equal(stored!.VoucherUpdatedAt, now.VoucherUpdatedAt);
        Assert.NotEqual(stored.Fingerprint, now.VoucherFingerprint);
        Assert.Equal(stored.Fingerprint, (await StoredAsync(id))!.Fingerprint);   // GET still wrote nothing
    }

    // ----------------------------------------------------- data-driven rules

    [Theory]
    [InlineData("2022-06-15", false)] // window A
    [InlineData("2023-03-15", true)]  // gap between A and B
    [InlineData("2023-08-01", false)] // window B
    [InlineData("2025-06-30", true)]  // gap before C
    [InlineData("2026-06-01", false)] // window C
    public async Task Vat_rate_follows_the_windows_in_the_table(string invoiceDate, bool vatRateError)
    {
        if (fx.ConnectionString is null) return;
        await CompanyAsync();
        var id = await InvoiceAsync(rate: 8m, date: DateOnly.Parse(invoiceDate));

        var issues = Value<VoucherValidationReport>(await Get(id, "user", fx.User)).Issues;

        Assert.Equal(vatRateError, issues.Any(i => i.Code == "VAT_RATE"));
    }

    [Fact]
    public async Task Tenant_settings_override_required_fields()
    {
        if (fx.ConnectionString is null) return;
        await CompanyAsync();
        var id = await InvoiceAsync(sellerName: null);
        Assert.Contains(Value<VoucherValidationReport>(await Get(id, "user", fx.User)).Issues, i => i is { Code: "REQUIRED_FIELD", Field: "seller_name" });

        await ExecAsync("""
            INSERT INTO tenant_voucher_settings(tenant_id, required_fields) VALUES (@t, '{"invoice_vat_in": ["document_number"]}')
            ON CONFLICT (tenant_id) DO UPDATE SET required_fields = EXCLUDED.required_fields
            """, new { t = fx.Tenant });
        try
        {
            Assert.DoesNotContain(Value<VoucherValidationReport>(await Get(id, "user", fx.User)).Issues, i => i.Code == "REQUIRED_FIELD");
        }
        finally
        {
            await ExecAsync("DELETE FROM tenant_voucher_settings WHERE tenant_id=@t", new { t = fx.Tenant });
        }
    }

    // ------------------------------------------------------------------ ACL

    [Fact]
    public async Task Another_department_gets_not_found()
    {
        if (fx.ConnectionString is null) return;
        var id = await InvoiceAsync();

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await Get(id, "admin", fx.Admin, fx.OtherDept)));
        Assert.Null(await StoredAsync(id));
    }

    [Theory]
    [InlineData("tenant_owner")]
    [InlineData("super_admin")]
    public async Task Tenant_owner_and_super_admin_have_no_department_scope(string role)
    {
        if (fx.ConnectionString is null) return;
        var id = await InvoiceAsync();

        Assert.IsType<ForbidHttpResult>(await Get(id, role, fx.Admin));
    }

    // --------------------------------------------------------------- helpers

    private sealed record Stored(int Version, bool HasErrors, DateTime? VoucherUpdatedAt, string? Fingerprint);

    private Task<IResult> Get(Guid id, string role, Guid user, Guid? department = null) =>
        VoucherEndpoints.ValidationAsync(id, Service(), Ctx(role, user, department), CancellationToken.None);

    private DefaultHttpContext Ctx(string role, Guid user, Guid? department = null)
    {
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user.ToString())], "test")) };
        ctx.Items[AuthenticatedPrincipalContext.ItemKey] = new PrincipalContext(user, fx.Tenant, department ?? fx.Dept, role, true, true, true);
        ctx.Items[TenantContext.ItemKey] = fx.Tenant;
        return ctx;
    }

    private static int? StatusOf(IResult r) => (r as IStatusCodeHttpResult)?.StatusCode;
    private static T Value<T>(IResult r) => Assert.IsType<T>((r as IValueHttpResult)?.Value);
    private static string Unique() => Guid.NewGuid().ToString("N")[..10];

    private static CreateVoucherRequest Request(string number, decimal total) => new(
        VoucherType: "invoice_vat_in", DocumentNumber: number, DocumentSeries: "1C26TAA", DocumentDate: new DateOnly(2026, 9, 15),
        SellerName: "Công ty NCC", SellerMst: Supplier, SellerAddress: null, BuyerName: "Công ty Chúng ta", BuyerMst: Company,
        BuyerAddress: null, PaymentMethod: null, Subtotal: 10_000_000m, VatRate: 10m, VatAmount: 1_000_000m, DiscountAmount: null,
        TotalAmount: total, Currency: "VND", AmountInWords: null, SourceFilePath: null, SourceFileName: null, SourceMimeType: null,
        RawOcrText: "RAW", OcrConfidence: null, ExtractedFields: null, Tags: null, LineItems: null);

    private static UpdateVoucherRequest Patch(decimal total) => new(
        Status: null, DocumentNumber: null, DocumentSeries: null, DocumentDate: null, SellerName: null, SellerMst: null,
        SellerAddress: null, BuyerName: null, BuyerMst: null, BuyerAddress: null, PaymentMethod: null, Subtotal: null,
        VatRate: null, VatAmount: null, DiscountAmount: null, TotalAmount: total, Currency: null, AmountInWords: null,
        ErrorMessage: null, Tags: null);

    private Task CompanyAsync() => ExecAsync("""
        INSERT INTO tenant_company_profiles(tenant_id, legal_name, tax_code) VALUES (@t, 'Công ty Chúng ta', @mst)
        ON CONFLICT (tenant_id) DO UPDATE SET tax_code = EXCLUDED.tax_code
        """, new { t = fx.Tenant, mst = Company });

    private async Task<Guid> InvoiceAsync(decimal total = 11_000_000m, decimal rate = 10m, DateOnly? date = null,
        string? sellerName = "Công ty NCC", string status = "extracted")
    {
        var vat = 10_000_000m * rate / 100m;
        if (total == 11_000_000m) total = 10_000_000m + vat;
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<Guid>("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, status, document_number, document_series, document_date,
                                 seller_name, seller_mst, buyer_name, buyer_mst, subtotal, vat_rate, vat_amount, total_amount)
            VALUES (@u, @t, @d, 'invoice_vat_in', @status, @number, '1C26TAA', @date, @sellerName, @supplier, 'Công ty Chúng ta', @company,
                    10000000, @rate, @vat, @total) RETURNING id
            """, new { u = fx.User, t = fx.Tenant, d = fx.Dept, status, number = Unique(),
                       date = date ?? new DateOnly(2026, 9, 15), sellerName, supplier = Supplier, company = Company, rate, vat, total });
    }

    private async Task<Guid> JournalAsync(Guid voucherId, string debit, string credit, decimal amount)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<Guid>("""
            INSERT INTO voucher_journal_suggestions(voucher_id, entry_no, debit_account, credit_account, amount)
            VALUES (@voucherId, 1, @debit, @credit, @amount) RETURNING id
            """, new { voucherId, debit, credit, amount });
    }

    private async Task<Stored?> StoredAsync(Guid id)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        return await c.QuerySingleOrDefaultAsync<Stored>("""
            SELECT validator_version AS Version, has_errors AS HasErrors, voucher_updated_at AS VoucherUpdatedAt,
                   voucher_fingerprint AS Fingerprint
              FROM voucher_validations WHERE voucher_id=@id
            """, new { id });
    }

    private async Task<string> IssuesJsonAsync(Guid id)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<string>("SELECT issues::text FROM voucher_validations WHERE voucher_id=@id", new { id }) ?? "";
    }

    private async Task<DateTime> UpdatedAtAsync(Guid id)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<DateTime>("SELECT updated_at FROM vouchers WHERE id=@id", new { id });
    }

    private async Task ExecAsync(string sql, object args)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync(sql, args);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
