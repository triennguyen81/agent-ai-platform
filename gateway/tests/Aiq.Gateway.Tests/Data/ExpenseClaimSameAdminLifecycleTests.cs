using System.Text.Json;
using Aiq.Gateway.Data;
using Aiq.Gateway.Employees;
using Aiq.Gateway.Payments;
using Aiq.Gateway.Vouchers;
using Dapper;
using Npgsql;
using L = Aiq.Gateway.Tests.Data.ExpenseClaimTestData.Line;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Executable documentation of the ADMIN Department Authority Invariant
/// (docs/ACL_V2_RESOURCE_VISIBILITY_RULES.md §15): ONE same-department ADMIN
/// creates an Expense Claim and performs every later step — department
/// approval, tax verification, accounting approval + recognition, payment.
/// If any of these ever fails because the actor is the creator / claimant /
/// a previous reviewer, an ADMIN self-approval restriction was reintroduced.
/// Isolation (other department / other tenant) must still hold.
/// </summary>
public sealed class ExpenseClaimSameAdminLifecycleTests(TaxVerificationDatabase fx) : IClassFixture<TaxVerificationDatabase>
{
    static ExpenseClaimSameAdminLifecycleTests()
    {
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyTypeHandler());
    }

    private readonly ExpenseClaimTestData d = new(fx);
    private bool Skip => fx.ConnectionString is null;

    private async Task<List<TaxAttestedInvoice>> InvoicesOfAsync(Guid voucher)
    {
        var json = await d.Scalar<string>("SELECT extracted_fields::text FROM vouchers WHERE id=@voucher", new { voucher });
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .Where(i => i.TryGetProperty("invoice_no", out var n) && n.ValueKind == JsonValueKind.String)
            .Select(i => new TaxAttestedInvoice(i.GetProperty("vendor_mst").GetString()!, null, i.GetProperty("invoice_no").GetString()!,
                new DateOnly(2026, 9, 18), i.GetProperty("amount").GetDecimal(), "L"))
            .ToList();
    }

    [Fact]
    public async Task Same_admin_completes_the_whole_single_document_claim_lifecycle()
    {
        if (Skip) return;
        var admin = fx.Admin;
        // Hotel 5.5M incl. 0.5M VAT (invoice → tax verification required) + meals 3.7M; advance 5M → payable 4.2M.
        var c = await d.CreateAsync([new L("cong_tac", 5_500_000m, 500_000m), new L("an_uong", 3_700_000m, 0m, Invoice: false)],
            advance: 5_000_000m, stage: "proposed", verifyTax: false, claimant: admin);
        Assert.Equal(admin, await d.Scalar<Guid>("SELECT user_id FROM vouchers WHERE id=@v", new { v = c.Voucher }));
        var claims = d.Claims();

        // 1. Department approval by the claimant ADMIN.
        Assert.Equal("dept_reviewed", (await claims.DepartmentReviewAsync(fx.Tenant, fx.Dept, c.Voucher, admin, default))!.ReviewStatus);
        // The tax gate itself is unchanged: recognition waits for an authoritative verification.
        Assert.Equal("tax_verification_required", (await Assert.ThrowsAsync<ExpenseRecognitionException>(() =>
            claims.AccountingReviewAsync(fx.Tenant, fx.Dept, c.Voucher, admin, default))).Code);
        // 2. Tax verification by the same ADMIN (the claimant).
        var tax = (await d.Tax().ConfirmAsync(fx.Tenant, fx.Dept, c.Voucher, admin, new TaxConfirmRequest(
            TaxVerificationSources.TctPortal, "Đã tra cứu cổng hoá đơn điện tử", true, await InvoicesOfAsync(c.Voucher)), default))!;
        Assert.True(tax.EffectivelyVerified);
        // 3. Accounting approval + authoritative recognition by the same ADMIN (also the stage-1 reviewer).
        var recognized = (await claims.AccountingReviewAsync(fx.Tenant, fx.Dept, c.Voucher, admin, default))!;
        Assert.Equal(("accounting_reviewed", "recognized", admin, admin), (recognized.ReviewStatus, recognized.RecognitionStatus, recognized.DeptReviewedBy!.Value, recognized.RecognizedBy!.Value));
        Assert.Equal(new ExpenseClaimTestData.State(0, 3, 0, "unpaid", "accounting_reviewed", "recognized", 0m, 10_200_000m), await d.StateAsync(c));
        // 4. Payment by the same ADMIN.
        var paid = (await d.Payments().PayExpenseClaimAsync(fx.Tenant, fx.Dept, c.Voucher, admin, new ExpenseClaimPayRequest("cash", null), default))!;
        Assert.Equal("paid", paid.Outcome);
        Assert.Equal(("334", "1111", 4_200_000m), (paid.Context.Payment!.Journal[0].DebitAccount, paid.Context.Payment.Journal[0].CreditAccount, paid.Context.Payment.Journal[0].Amount));
        Assert.Equal(new ExpenseClaimTestData.State(1, 3, 1, "paid", "confirmed", "recognized", 0m, 10_200_000m), await d.StateAsync(c));
    }

    [Fact]
    public async Task Same_admin_exact_advance_claim_closes_without_cash()
    {
        if (Skip) return;
        var c = await d.CreateAsync([new L("cong_tac", 2_000_000m, 0m, Invoice: false)], advance: 2_000_000m, stage: "proposed", claimant: fx.Admin);
        var claims = d.Claims();
        await claims.DepartmentReviewAsync(fx.Tenant, fx.Dept, c.Voucher, fx.Admin, default);
        Assert.Equal("not_due", (await claims.AccountingReviewAsync(fx.Tenant, fx.Dept, c.Voucher, fx.Admin, default))!.PaymentStatus);
        Assert.Equal("confirmed", (await claims.ConfirmAsync(fx.Tenant, fx.Dept, c.Voucher, fx.Admin, default))!.ReviewStatus);
    }

    [Fact]
    public async Task Same_admin_authority_never_crosses_department_or_tenant()
    {
        if (Skip) return;
        var c = await d.CreateAsync(stage: "proposed", claimant: fx.Admin);
        var claims = d.Claims();
        // Other department / other tenant: not found, no mutation.
        Assert.Null(await claims.DepartmentReviewAsync(fx.Tenant, fx.OtherDept, c.Voucher, fx.Admin, default));
        Assert.Null(await claims.DepartmentReviewAsync(Guid.NewGuid(), fx.Dept, c.Voucher, fx.Admin, default));
        await claims.DepartmentReviewAsync(fx.Tenant, fx.Dept, c.Voucher, fx.Admin, default);
        Assert.Null(await claims.AccountingReviewAsync(fx.Tenant, fx.OtherDept, c.Voucher, fx.Admin, default));
        Assert.Null(await claims.AccountingReviewAsync(Guid.NewGuid(), fx.Dept, c.Voucher, fx.Admin, default));
        Assert.Null(await d.Tax().ConfirmAsync(fx.Tenant, fx.OtherDept, c.Voucher, fx.Admin, new TaxConfirmRequest(
            TaxVerificationSources.TctPortal, "Đã tra cứu cổng hoá đơn điện tử", true, await InvoicesOfAsync(c.Voucher)), default));
        Assert.Equal(new ExpenseClaimTestData.State(0, 0, 0, "unpaid", "dept_reviewed", "unrecognized", 800_000m, 1_000_000m), await d.StateAsync(c));
        await d.Tax().ConfirmAsync(fx.Tenant, fx.Dept, c.Voucher, fx.Admin, new TaxConfirmRequest(
            TaxVerificationSources.TctPortal, "Đã tra cứu cổng hoá đơn điện tử", true, await InvoicesOfAsync(c.Voucher)), default);
        await claims.AccountingReviewAsync(fx.Tenant, fx.Dept, c.Voucher, fx.Admin, default);
        Assert.Null(await d.Payments().PayExpenseClaimAsync(fx.Tenant, fx.OtherDept, c.Voucher, fx.Admin, new ExpenseClaimPayRequest("cash", null), default));
        Assert.Null(await d.Payments().PayExpenseClaimAsync(Guid.NewGuid(), fx.Dept, c.Voucher, fx.Admin, new ExpenseClaimPayRequest("cash", null), default));
        Assert.Equal(0, (await d.StateAsync(c)).Cash);
    }
}
