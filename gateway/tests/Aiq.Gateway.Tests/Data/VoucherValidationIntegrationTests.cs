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
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Chứng từ flow v2 PR2: GET /vouchers/{id}/validation against the real schema
/// (migration 068 seed for vat_rates, tenant settings, stored result).
/// </summary>
public sealed class VoucherValidationIntegrationTests(TaxVerificationDatabase fx) : IClassFixture<TaxVerificationDatabase>
{
    private const string Company = "0312345673";
    private const string Supplier = "0101243150";
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 3, 0, 0, TimeSpan.Zero);

    static VoucherValidationIntegrationTests()
    {
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyTypeHandler());
    }

    private VoucherValidationService Service()
    {
        var f = new NpgsqlConnectionFactory(fx.ConnectionString!);
        return new VoucherValidationService(new VoucherRepository(f), new TenantCompanyProfileRepository(f),
            new VoucherValidationRepository(f), new FixedTime(Now));
    }

    [Fact]
    public async Task Returns_and_stores_the_issues_then_replaces_them_on_the_next_call()
    {
        if (fx.ConnectionString is null) return;
        await CompanyAsync();
        var id = await InvoiceAsync(total: 11_000_500m);

        var first = await Get(id, "user", fx.User);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(first));
        var stored = Value<StoredVoucherValidation>(first);
        Assert.True(stored.HasErrors);
        Assert.Contains(stored.Issues, i => i.Code == "AMOUNT_SUM");
        Assert.Equal((VoucherValidator.Version, true), await StoredAsync(id));

        await ExecAsync("UPDATE vouchers SET total_amount = 11000000 WHERE id=@id", new { id });
        var second = Value<StoredVoucherValidation>(await Get(id, "admin", fx.Admin));

        Assert.False(second.HasErrors);
        Assert.Equal((VoucherValidator.Version, false), await StoredAsync(id));
        var issues = await IssuesJsonAsync(id);
        Assert.DoesNotContain("AMOUNT_SUM", issues);
    }

    [Fact]
    public async Task Stored_issues_use_the_api_json_shape()
    {
        if (fx.ConnectionString is null) return;
        await CompanyAsync();
        var id = await InvoiceAsync(total: 1m);

        await Get(id, "user", fx.User);

        using var doc = JsonDocument.Parse(await IssuesJsonAsync(id));
        var issue = doc.RootElement.EnumerateArray().First(e => e.GetProperty("code").GetString() == "AMOUNT_SUM");
        Assert.Equal("error", issue.GetProperty("severity").GetString());
        Assert.Equal("total_amount", issue.GetProperty("field").GetString());
    }

    [Fact]
    public async Task The_seeded_8_percent_window_is_applied_by_invoice_date()
    {
        if (fx.ConnectionString is null) return;
        await CompanyAsync();
        var inside = await InvoiceAsync(rate: 8m, date: new DateOnly(2026, 6, 1));
        var before = await InvoiceAsync(rate: 8m, date: new DateOnly(2025, 6, 30));

        Assert.DoesNotContain(Value<StoredVoucherValidation>(await Get(inside, "user", fx.User)).Issues, i => i.Code == "VAT_RATE");
        Assert.Contains(Value<StoredVoucherValidation>(await Get(before, "user", fx.User)).Issues, i => i.Code == "VAT_RATE");
    }

    [Fact]
    public async Task Tenant_settings_override_required_fields()
    {
        if (fx.ConnectionString is null) return;
        await CompanyAsync();
        var id = await InvoiceAsync(sellerName: null);
        Assert.Contains(Value<StoredVoucherValidation>(await Get(id, "user", fx.User)).Issues, i => i is { Code: "REQUIRED_FIELD", Field: "seller_name" });

        await ExecAsync("""
            INSERT INTO tenant_voucher_settings(tenant_id, required_fields) VALUES (@t, '{"invoice_vat_in": ["document_number"]}')
            ON CONFLICT (tenant_id) DO UPDATE SET required_fields = EXCLUDED.required_fields
            """, new { t = fx.Tenant });
        try
        {
            Assert.DoesNotContain(Value<StoredVoucherValidation>(await Get(id, "user", fx.User)).Issues, i => i.Code == "REQUIRED_FIELD");
        }
        finally
        {
            await ExecAsync("DELETE FROM tenant_voucher_settings WHERE tenant_id=@t", new { t = fx.Tenant });
        }
    }

    [Fact]
    public async Task Validating_a_posted_voucher_never_touches_its_row()
    {
        if (fx.ConnectionString is null) return;
        await CompanyAsync();
        var id = await InvoiceAsync(status: "verified");
        var before = await UpdatedAtAsync(id);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(await Get(id, "admin", fx.Admin)));

        Assert.Equal(before, await UpdatedAtAsync(id));
    }

    [Fact]
    public async Task Another_department_gets_not_found_and_nothing_is_stored()
    {
        if (fx.ConnectionString is null) return;
        var id = await InvoiceAsync();

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await Get(id, "admin", fx.Admin, fx.OtherDept)));
        Assert.Null(await StoredOrNullAsync(id));
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

    private Task<IResult> Get(Guid id, string role, Guid user, Guid? department = null)
    {
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user.ToString())], "test")) };
        ctx.Items[AuthenticatedPrincipalContext.ItemKey] = new PrincipalContext(user, fx.Tenant, department ?? fx.Dept, role, true, true, true);
        ctx.Items[TenantContext.ItemKey] = fx.Tenant;
        return VoucherEndpoints.ValidationAsync(id, Service(), ctx, CancellationToken.None);
    }

    private static int? StatusOf(IResult r) => (r as IStatusCodeHttpResult)?.StatusCode;
    private static T Value<T>(IResult r) => Assert.IsType<T>((r as IValueHttpResult)?.Value);

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
            """, new { u = fx.User, t = fx.Tenant, d = fx.Dept, status, number = Guid.NewGuid().ToString("N")[..10],
                       date = date ?? new DateOnly(2026, 9, 15), sellerName, supplier = Supplier, company = Company, rate, vat, total });
    }

    private async Task<(int, bool)> StoredAsync(Guid id) => (await StoredOrNullAsync(id))!.Value;

    private async Task<(int, bool)?> StoredOrNullAsync(Guid id)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        return await c.QuerySingleOrDefaultAsync<(int, bool)?>(
            "SELECT validator_version, has_errors FROM voucher_validations WHERE voucher_id=@id", new { id });
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
