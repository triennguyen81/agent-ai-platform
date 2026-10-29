using System.Security.Claims;
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
/// Chứng từ flow v2 — PR1 safety guards (docs/flows/chung-tu-proposed.md §9):
/// a voucher with an accounting effect (status 'verified', or a live cash
/// posting) is never rewritten by apply-extraction / reextract / the OCR
/// update / the extract job, never deleted, and POST /vouchers no longer
/// upserts over an existing voucher (P1–P5 in chung-tu-current.md §6).
/// Real schema, throwaway database.
/// </summary>
public sealed class VoucherPostingGuardIntegrationTests(TaxVerificationDatabase fx) : IClassFixture<TaxVerificationDatabase>
{
    static VoucherPostingGuardIntegrationTests()
    {
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyTypeHandler());
    }

    private VoucherRepository Repo => new(new NpgsqlConnectionFactory(fx.ConnectionString!));
    private VoucherValidationService Validation => new(Repo, new TenantCompanyProfileRepository(new NpgsqlConnectionFactory(fx.ConnectionString!)),
        new VoucherValidationRepository(new NpgsqlConnectionFactory(fx.ConnectionString!)), TimeProvider.System,
        NullLogger<VoucherValidationService>.Instance);

    // ------------------------------------------------------------ repository

    [Fact]
    public async Task ReplaceExtracted_never_rewrites_a_verified_voucher()
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync("verified", lineItem: true);
        var before = await SnapshotAsync(id);

        var replaced = await Repo.ReplaceExtractedAsync(fx.Tenant, fx.User, id, Request("HD-NEW", 999m), CancellationToken.None);

        Assert.False(replaced);
        Assert.Equal(before, await SnapshotAsync(id));
    }

    [Fact]
    public async Task ReplaceExtracted_never_rewrites_a_voucher_with_a_live_cash_posting()
    {
        if (fx.ConnectionString is null) return;
        // Defense in depth: even if status drifted, a posted cash transaction freezes the voucher.
        var id = await VoucherAsync("extracted", type: "receipt");
        await PostCashAsync(id);
        var before = await SnapshotAsync(id);

        var replaced = await Repo.ReplaceExtractedAsync(fx.Tenant, fx.User, id, Request("PT-NEW", 5m), CancellationToken.None);

        Assert.False(replaced);
        Assert.Equal(before, await SnapshotAsync(id));
    }

    [Fact]
    public async Task ReplaceExtracted_still_updates_an_unposted_voucher_and_keeps_ocr_confidence()
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync("ocr_complete", ocrConfidence: 0.87m);

        var replaced = await Repo.ReplaceExtractedAsync(fx.Tenant, fx.User, id, Request("HD-OK", 2_000_000m), CancellationToken.None);

        Assert.True(replaced);
        var row = await SnapshotAsync(id);
        Assert.Equal(("extracted", "HD-OK", 2_000_000m), (row.Status, row.Number, row.Total));
        // P12: extraction (OcrConfidence = null) no longer wipes the OCR confidence.
        Assert.Equal(0.87m, row.OcrConfidence);
    }

    [Theory]
    [InlineData("verified")]
    [InlineData("journalized")]
    public async Task UpdateOcrResult_never_touches_a_posted_voucher(string status)
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync(status);
        var before = await SnapshotAsync(id);

        var updated = await Repo.UpdateOcrResultAsync(id, "ocr_complete", "PASTED TEXT OVER A POSTED VOUCHER", 1.0m, null, CancellationToken.None);

        Assert.False(updated);
        Assert.Equal(before, await SnapshotAsync(id));
    }

    [Fact]
    public async Task Delete_never_removes_a_verified_voucher()
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync("verified");

        Assert.False(await Repo.DeleteAsync(fx.Tenant, fx.User, id, CancellationToken.None));
        Assert.True(await ExistsAsync(id));
    }

    [Fact]
    public async Task Create_with_an_existing_identity_writes_nothing()
    {
        if (fx.ConnectionString is null) return;
        var number = Unique("HD");
        var id = await VoucherAsync("verified", number: number);
        var before = await SnapshotAsync(id);

        var created = await Repo.CreateAsync(fx.Tenant, fx.Dept, fx.User, Request(number, 1m), CancellationToken.None);

        Assert.Null(created);
        Assert.Equal(before, await SnapshotAsync(id));
    }

    [Theory]
    [InlineData("verified")]
    [InlineData("journalized")]
    public async Task Extract_job_skips_a_posted_voucher_before_any_side_effect(string status)
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync(status);
        var before = await SnapshotAsync(id);
        // Every collaborator past the voucher lookup is null: touching one fails the test.
        var runner = new Aiq.Gateway.Jobs.VoucherExtractJobRunner(null!, Repo, null!, null!, null!, null!, null!, null!,
            Microsoft.Extensions.Options.Options.Create(new Aiq.Gateway.Jobs.ExpenseRiskOptions()), null!, null!, null!, null!, null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Aiq.Gateway.Jobs.VoucherExtractJobRunner>.Instance);

        await runner.RunForActorAsync(id, fx.User, fx.Tenant, "auto", "token", fx.User, CancellationToken.None);

        Assert.Equal(before, await SnapshotAsync(id));
    }

    // ------------------------------------------------------------- endpoints

    [Theory]
    [InlineData("admin")]
    [InlineData("user")]
    public async Task ApplyExtraction_on_a_verified_voucher_is_409(string role)
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync("verified");
        var before = await SnapshotAsync(id);
        var actor = role == "admin" ? fx.Admin : fx.User;

        var result = await VoucherEndpoints.ApplyExtractionAsync(id, Request("HD-OVERWRITE", 1m), Repo, Validation, Ctx(role, actor), CancellationToken.None);

        AssertPostedConflict(result);
        Assert.Equal(before, await SnapshotAsync(id));
    }

    [Fact]
    public async Task ApplyExtraction_by_a_same_department_admin_updates_someone_elses_unposted_voucher()
    {
        if (fx.ConnectionString is null) return;
        // ADMIN Department Authority Invariant: the ADMIN is never blocked for not being the creator.
        var id = await VoucherAsync("ocr_complete");

        var result = await VoucherEndpoints.ApplyExtractionAsync(id, Request("HD-ADMIN", 3m), Repo, Validation, Ctx("admin", fx.Admin), CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        Assert.Equal("HD-ADMIN", (await SnapshotAsync(id)).Number);
    }

    [Fact]
    public async Task ApplyExtraction_from_another_department_is_not_found()
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync("ocr_complete");
        var before = await SnapshotAsync(id);

        var result = await VoucherEndpoints.ApplyExtractionAsync(id, Request("HD-X", 1m), Repo, Validation, Ctx("admin", fx.Admin, fx.OtherDept), CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
        Assert.Equal(before, await SnapshotAsync(id));
    }

    [Fact]
    public async Task Reextract_with_pasted_text_on_a_verified_voucher_is_409_and_keeps_the_text()
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync("verified");
        var before = await SnapshotAsync(id);

        var result = await VoucherEndpoints.ReextractAsync(id, new VoucherEndpoints.ReextractRequest("HOÁ ĐƠN DÁN LẠI ĐỂ GHI ĐÈ CHỨNG TỪ ĐÃ GHI SỔ", null),
            Repo, null!, Ctx("admin", fx.Admin), CancellationToken.None);

        AssertPostedConflict(result);
        Assert.Equal(before, await SnapshotAsync(id));
    }

    [Fact]
    public async Task Delete_endpoint_on_a_verified_voucher_is_409()
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync("verified");

        var result = await VoucherEndpoints.DeleteAsync(id, Repo, Ctx("admin", fx.Admin), CancellationToken.None);

        AssertPostedConflict(result);
        Assert.True(await ExistsAsync(id));
    }

    [Fact]
    public async Task Delete_endpoint_still_removes_an_unposted_voucher()
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync("extracted");

        var result = await VoucherEndpoints.DeleteAsync(id, Repo, Ctx("admin", fx.Admin), CancellationToken.None);

        Assert.Equal(StatusCodes.Status204NoContent, StatusOf(result));
        Assert.False(await ExistsAsync(id));
    }

    [Fact]
    public async Task Create_duplicate_is_409_and_does_not_overwrite_the_posted_voucher()
    {
        if (fx.ConnectionString is null) return;
        var number = Unique("HD");
        var id = await VoucherAsync("verified", number: number);
        var before = await SnapshotAsync(id);

        var result = await VoucherEndpoints.CreateAsync(Request(number, 1m), Repo, Validation, Ctx("user", fx.User), CancellationToken.None);

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(result));
        Assert.Equal("duplicate_voucher", Body(result)["error"]);
        Assert.Equal(id, Body(result)["existingVoucherId"]);
        Assert.Equal(before, await SnapshotAsync(id));
    }

    [Fact]
    public async Task Create_duplicate_of_another_departments_voucher_is_409_without_leaking_it()
    {
        if (fx.ConnectionString is null) return;
        var number = Unique("HD");
        var other = await VoucherAsync("extracted", number: number, department: fx.OtherDept);
        var before = await SnapshotAsync(other);

        var result = await VoucherEndpoints.CreateAsync(Request(number, 1m), Repo, Validation, Ctx("user", fx.User), CancellationToken.None);

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(result));
        var body = Body(result);
        Assert.Equal("duplicate_voucher", body["error"]);
        Assert.False(body.ContainsKey("existingVoucherId"));
        Assert.DoesNotContain(other.ToString(), System.Text.Json.JsonSerializer.Serialize(body));
        Assert.Equal(before, await SnapshotAsync(other));
    }

    [Fact]
    public async Task Create_of_a_new_voucher_is_201()
    {
        if (fx.ConnectionString is null) return;

        var result = await VoucherEndpoints.CreateAsync(Request(Unique("HD"), 1m), Repo, Validation, Ctx("user", fx.User), CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, StatusOf(result));
    }

    // --------------------------------------------------------------- helpers

    private const string Mst = "0312345678";

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private static CreateVoucherRequest Request(string number, decimal total) => new(
        VoucherType: "invoice_vat_in", DocumentNumber: number, DocumentSeries: "1C26TAA", DocumentDate: new DateOnly(2026, 9, 1),
        SellerName: "NCC", SellerMst: Mst, SellerAddress: null, BuyerName: null, BuyerMst: null, BuyerAddress: null,
        PaymentMethod: null, Subtotal: null, VatRate: null, VatAmount: null, DiscountAmount: null, TotalAmount: total,
        Currency: "VND", AmountInWords: null, SourceFilePath: null, SourceFileName: null, SourceMimeType: null,
        RawOcrText: "RAW", OcrConfidence: null, ExtractedFields: null, Tags: null, LineItems: null);

    private DefaultHttpContext Ctx(string role, Guid user, Guid? department = null)
    {
        var dept = department ?? fx.Dept;
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user.ToString())], "test")),
        };
        ctx.Items[AuthenticatedPrincipalContext.ItemKey] = new PrincipalContext(user, fx.Tenant, dept, role, true, true, true);
        ctx.Items[TenantContext.ItemKey] = fx.Tenant;
        return ctx;
    }

    private static int? StatusOf(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;

    private static Dictionary<string, object?> Body(IResult result)
    {
        var value = (result as IValueHttpResult)?.Value;
        Assert.NotNull(value);
        return value!.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(value));
    }

    private static void AssertPostedConflict(IResult result)
    {
        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(result));
        Assert.Equal(VoucherPostingGuard.ErrorCode, Body(result)["error"]);
    }

    private async Task<Guid> VoucherAsync(string status, string type = "invoice_vat_in", string? number = null,
        decimal? ocrConfidence = null, bool lineItem = false, Guid? department = null)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        var id = await c.ExecuteScalarAsync<Guid>("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, status, document_number, document_date,
                                 seller_mst, total_amount, raw_ocr_text, ocr_confidence)
            VALUES (@u, @t, @d, @type, @status, @number, '2026-09-01', @mst, 1000000, 'ORIGINAL OCR', @ocrConfidence) RETURNING id
            """, new { u = fx.User, t = fx.Tenant, d = department ?? fx.Dept, type, status, number = number ?? Unique("HD"), mst = Mst, ocrConfidence });
        if (lineItem)
            await c.ExecuteAsync("INSERT INTO voucher_line_items(voucher_id, ordinal, description, total_amount) VALUES (@id, 1, 'Dòng gốc', 1000000)", new { id });
        return id;
    }

    private async Task PostCashAsync(Guid voucherId)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        var customer = await c.ExecuteScalarAsync<Guid>(
            "INSERT INTO sim_customers(tenant_id, department_id, customer_code, name, mst) VALUES (@t,@d,@code,'KH','0101010101') RETURNING id",
            new { t = fx.Tenant, d = fx.Dept, code = Unique("KH") });
        await c.ExecuteAsync("""
            INSERT INTO cash_transactions(tenant_id, department_id, voucher_id, direction, counterparty_type, customer_id,
                                          amount, transaction_date, payment_method, cash_account, counter_account, document_number, posted_by)
            VALUES (@t, @d, @v, 'money_in', 'customer', @customer, 1000000, '2026-09-01', 'cash', '1111', '131', 'PT-1', @u)
            """, new { t = fx.Tenant, d = fx.Dept, v = voucherId, customer, u = fx.Admin });
    }

    private sealed record Snapshot(string Status, string? Number, decimal? Total, string? RawText, decimal? OcrConfidence, int Lines, DateTime UpdatedAt);

    private async Task<Snapshot> SnapshotAsync(Guid id)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        return await c.QuerySingleAsync<Snapshot>("""
            SELECT status AS Status, document_number AS Number, total_amount AS Total, raw_ocr_text AS RawText,
                   ocr_confidence AS OcrConfidence,
                   (SELECT COUNT(*)::int FROM voucher_line_items li WHERE li.voucher_id = v.id) AS Lines, updated_at AS UpdatedAt
              FROM vouchers v WHERE id = @id
            """, new { id });
    }

    private async Task<bool> ExistsAsync(Guid id)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM vouchers WHERE id=@id)", new { id });
    }
}
