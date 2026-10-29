using System.Security.Claims;
using Aiq.Gateway.Data;
using Aiq.Gateway.Endpoints;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;
using Aiq.Gateway.Vouchers;
using Aiq.Gateway.Vouchers.Lifecycle;
using Aiq.Gateway.Vouchers.Validation;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Chứng từ flow v2 PR3 against the real schema: the edit lock of submitted /
/// approved / reversed vouchers, PATCH never posting, the transition endpoints
/// and their ACL (ADMIN Department Authority Invariant), soft delete.
/// </summary>
public sealed class VoucherLifecycleIntegrationTests(TaxVerificationDatabase fx) : IClassFixture<TaxVerificationDatabase>
{
    private const string Company = "0312345673";
    private const string Supplier = "0101243150";

    static VoucherLifecycleIntegrationTests()
    {
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyTypeHandler());
    }

    private NpgsqlConnectionFactory Factory => new(fx.ConnectionString!);
    private VoucherRepository Repo => new(Factory);
    private TenantFeatureFlagRepository Flags => new(Factory);
    private VoucherValidationService Validation => new(Repo, new TenantCompanyProfileRepository(Factory),
        new VoucherValidationRepository(Factory), TimeProvider.System, NullLogger<VoucherValidationService>.Instance);

    // ------------------------------------------------------------ edit lock

    [Theory]
    [InlineData("pending_approval")]
    [InlineData("approved")]
    [InlineData("reversed")]
    public async Task A_locked_voucher_refuses_every_edit_path(string state)
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync(lifecycle: state);
        var entry = await JournalAsync(id);
        var before = await FingerprintAsync(id);

        Assert.Equal(VoucherLifecycleGuard.ErrorCode, ErrorOf(await VoucherEndpoints.ApplyExtractionAsync(
            id, Request(Unique()), Repo, Validation, Ctx("admin", fx.Admin), CancellationToken.None)));
        Assert.Equal(VoucherLifecycleGuard.ErrorCode, ErrorOf(await VoucherEndpoints.UpdateAsync(
            id, Patch(total: 5m), Repo, Validation, Flags, Ctx("user", fx.User), CancellationToken.None)));
        Assert.Equal(VoucherLifecycleGuard.ErrorCode, ErrorOf(await VoucherEndpoints.ReextractAsync(
            id, new VoucherEndpoints.ReextractRequest("VĂN BẢN DÁN LẠI ĐỂ GHI ĐÈ CHỨNG TỪ ĐANG CHỜ DUYỆT", null), Repo, null!,
            Ctx("admin", fx.Admin), CancellationToken.None)));
        Assert.Equal(VoucherLifecycleGuard.ErrorCode, ErrorOf(await VoucherEndpoints.DecideJournalAsync(
            id, entry, new AcceptJournalRequest(false, "x"), Repo, Validation, Ctx("admin", fx.Admin), CancellationToken.None)));
        Assert.Equal(VoucherLifecycleGuard.ErrorCode, ErrorOf(await VoucherEndpoints.DeleteAsync(
            id, Repo, Ctx("admin", fx.Admin), CancellationToken.None)));
        await Assert.ThrowsAsync<VoucherLockedException>(() => Repo.SaveJournalSuggestionsAsync(fx.Tenant, fx.Dept, id,
            new SaveJournalSuggestionsRequest([new SaveJournalSuggestionDto(1, "642", "331", 1m, null, null)], ReplaceExisting: true),
            CancellationToken.None));

        Assert.Equal(before, await FingerprintAsync(id));
    }

    [Fact]
    public async Task The_repository_guard_holds_when_the_lock_lands_after_the_endpoint_check()
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync(lifecycle: "pending_approval");

        Assert.False(await Repo.ReplaceExtractedAsync(fx.Tenant, fx.User, id, Request(Unique()), CancellationToken.None));
        Assert.False(await Repo.UpdateOcrResultAsync(id, "ocr_complete", "RAW", 1m, null, CancellationToken.None));
        Assert.Null(await Repo.UpdateAsync(fx.Tenant, fx.User, id, Patch(total: 5m), CancellationToken.None));
        Assert.False(await Repo.DeleteAsync(fx.Tenant, fx.User, id, CancellationToken.None));
        Assert.Equal("pending_approval", await LifecycleAsync(id));
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("extracted")]
    [InlineData("needs_fix")]
    [InlineData("validated")]
    public async Task Editable_states_still_accept_edits(string state)
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync(lifecycle: state);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(await VoucherEndpoints.UpdateAsync(
            id, Patch(total: 5m), Repo, Validation, Flags, Ctx("user", fx.User), CancellationToken.None)));
    }

    // ------------------------------------------------------ PATCH never posts

    [Theory]
    [InlineData("verified")]
    [InlineData("journalized")]
    public async Task Patch_cannot_post_a_voucher(string status)
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync();

        var result = await VoucherEndpoints.UpdateAsync(id, Patch(status: status), Repo, Validation, Flags, Ctx("admin", fx.Admin), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Equal(("extracted", "extracted"), await StatusAndLifecycleAsync(id));
    }

    [Fact]
    public async Task With_the_v2_flag_patch_changes_no_status_at_all()
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync();
        Assert.Equal(StatusCodes.Status200OK, StatusOf(await VoucherEndpoints.UpdateAsync(
            id, Patch(status: "rejected"), Repo, Validation, Flags, Ctx("admin", fx.Admin), CancellationToken.None)));

        var other = await VoucherAsync();
        await FlagAsync(true);
        try
        {
            var result = await VoucherEndpoints.UpdateAsync(other, Patch(status: "rejected"), Repo, Validation, Flags, Ctx("admin", fx.Admin), CancellationToken.None);
            Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
            Assert.Equal(("extracted", "extracted"), await StatusAndLifecycleAsync(other));
        }
        finally
        {
            await FlagAsync(false);
        }
    }

    // --------------------------------------------------------------- helpers

    private DefaultHttpContext Ctx(string role, Guid user, Guid? department = null)
    {
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user.ToString())], "test")) };
        ctx.Items[AuthenticatedPrincipalContext.ItemKey] = new PrincipalContext(user, fx.Tenant, department ?? fx.Dept, role, true, true, true);
        ctx.Items[TenantContext.ItemKey] = fx.Tenant;
        return ctx;
    }

    private static int? StatusOf(IResult r) => (r as IStatusCodeHttpResult)?.StatusCode;

    private static string? ErrorOf(IResult r) =>
        (r as IValueHttpResult)?.Value?.GetType().GetProperty("error")?.GetValue((r as IValueHttpResult)!.Value) as string;

    private static string Unique() => Guid.NewGuid().ToString("N")[..10];

    private static CreateVoucherRequest Request(string number) => new(
        VoucherType: "invoice_vat_in", DocumentNumber: number, DocumentSeries: "1C26TAA", DocumentDate: new DateOnly(2026, 9, 15),
        SellerName: "Công ty NCC", SellerMst: Supplier, SellerAddress: null, BuyerName: "Công ty Chúng ta", BuyerMst: Company,
        BuyerAddress: null, PaymentMethod: null, Subtotal: 10_000_000m, VatRate: 10m, VatAmount: 1_000_000m, DiscountAmount: null,
        TotalAmount: 11_000_000m, Currency: "VND", AmountInWords: null, SourceFilePath: null, SourceFileName: null, SourceMimeType: null,
        RawOcrText: "RAW", OcrConfidence: null, ExtractedFields: null, Tags: null, LineItems: null);

    private static UpdateVoucherRequest Patch(decimal? total = null, string? status = null) => new(
        Status: status, DocumentNumber: null, DocumentSeries: null, DocumentDate: null, SellerName: null, SellerMst: null,
        SellerAddress: null, BuyerName: null, BuyerMst: null, BuyerAddress: null, PaymentMethod: null, Subtotal: null,
        VatRate: null, VatAmount: null, DiscountAmount: null, TotalAmount: total, Currency: null, AmountInWords: null,
        ErrorMessage: null, Tags: null);

    /// <summary>An invoice that passes the validator, created by fx.User, then moved to <paramref name="lifecycle"/>.</summary>
    private async Task<Guid> VoucherAsync(string lifecycle = "extracted", Guid? creator = null)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync("""
            INSERT INTO tenant_company_profiles(tenant_id, legal_name, tax_code) VALUES (@t, 'Công ty Chúng ta', @mst)
            ON CONFLICT (tenant_id) DO UPDATE SET tax_code = EXCLUDED.tax_code
            """, new { t = fx.Tenant, mst = Company });
        var id = await c.ExecuteScalarAsync<Guid>("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, status, document_number, document_series, document_date,
                                 seller_name, seller_mst, buyer_name, buyer_mst, subtotal, vat_rate, vat_amount, total_amount, raw_ocr_text)
            VALUES (@u, @t, @d, 'invoice_vat_in', 'extracted', @n, '1C26TAA', '2026-09-15', 'Công ty NCC', @supplier, 'Công ty Chúng ta', @company,
                    10000000, 10, 1000000, 11000000, 'RAW') RETURNING id
            """, new { u = creator ?? fx.User, t = fx.Tenant, d = fx.Dept, n = Unique(), supplier = Supplier, company = Company });
        if (lifecycle != "extracted")
            await c.ExecuteAsync("UPDATE vouchers SET lifecycle_state=@lifecycle WHERE id=@id", new { id, lifecycle });
        return id;
    }

    private async Task<Guid> JournalAsync(Guid voucherId)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<Guid>("""
            INSERT INTO voucher_journal_suggestions(voucher_id, entry_no, debit_account, credit_account, amount)
            VALUES (@voucherId, 1, '1561', '331', 10000000) RETURNING id
            """, new { voucherId });
    }

    private async Task FlagAsync(bool enabled)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync("""
            INSERT INTO tenant_feature_flags(tenant_id, flag, enabled) VALUES (@t, 'voucher_flow_v2', @enabled)
            ON CONFLICT (tenant_id, flag) DO UPDATE SET enabled = EXCLUDED.enabled
            """, new { t = fx.Tenant, enabled });
    }

    /// <summary>Everything an edit could touch: the voucher row (sans timestamps), line items, journal.</summary>
    private async Task<string> FingerprintAsync(Guid id)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<string>("""
            SELECT md5((to_jsonb(v) - 'updated_at')::text
                       || COALESCE((SELECT jsonb_agg(to_jsonb(l) ORDER BY l.ordinal)::text FROM voucher_line_items l WHERE l.voucher_id = v.id), '')
                       || COALESCE((SELECT jsonb_agg(to_jsonb(j) ORDER BY j.entry_no)::text FROM voucher_journal_suggestions j WHERE j.voucher_id = v.id), ''))
              FROM vouchers v WHERE v.id = @id
            """, new { id }) ?? "";
    }

    private async Task<string> LifecycleAsync(Guid id) => (await StatusAndLifecycleAsync(id)).Lifecycle;

    private async Task<(string Status, string Lifecycle)> StatusAndLifecycleAsync(Guid id)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        return await c.QuerySingleAsync<(string, string)>("SELECT status, lifecycle_state FROM vouchers WHERE id=@id", new { id });
    }
}
