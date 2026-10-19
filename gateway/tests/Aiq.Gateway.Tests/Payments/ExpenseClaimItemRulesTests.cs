using Aiq.Gateway.Data;
using Aiq.Gateway.Employees;
using Aiq.Gateway.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aiq.Gateway.Tests.Payments;

public sealed class ExpenseClaimItemRulesTests
{
    private static readonly string[] Categories = ["cong_tac", "khach_san", "an_uong"];
    private static readonly ExpenseClaimItemDuplicateEvidence NoDup = new(false, false, false);

    private static ExpenseClaimItemValues V(string? category = "cong_tac", decimal? gross = 600_000m, DateOnly? date = null, decimal? vat = null,
        string? currency = "VND", string? mst = null, string? number = null) =>
        new(category, "Nhà xe", mst, number, date ?? new DateOnly(2026, 9, 18), gross, vat, currency, "Vé xe");

    [Fact]
    public void A_clean_confident_bill_needs_no_review() =>
        Assert.Empty(ExpenseClaimItemRules.ReviewReasons(V(), 0.95m, 0.9m, Categories, new(2026, 9, 15), new(2026, 9, 20), NoDup, false));

    [Theory]
    [InlineData(null, 0.95, ExpenseClaimItemReviewReasons.AmountMissing)]
    [InlineData(600000d, 0.5, ExpenseClaimItemReviewReasons.LowConfidence)]
    [InlineData(600000d, null, ExpenseClaimItemReviewReasons.LowConfidence)]
    public void Missing_amount_or_low_or_missing_confidence_needs_review(double? gross, double? confidence, string reason) =>
        Assert.Contains(reason, ExpenseClaimItemRules.ReviewReasons(V(gross: (decimal?)gross), (decimal?)confidence, null, Categories, null, null, NoDup, false));

    [Fact]
    public void Every_rule_is_detected()
    {
        var reasons = ExpenseClaimItemRules.ReviewReasons(
            V(category: "6428", date: new DateOnly(2026, 9, 1), vat: 900_000m, currency: "USD"), 0.95m, 0.3m, Categories,
            new(2026, 9, 15), new(2026, 9, 20), new(true, true, true), workflowRequiresReview: true);
        Assert.Equal(new[]
        {
            ExpenseClaimItemReviewReasons.CategoryUnknown, ExpenseClaimItemReviewReasons.CurrencyUnsupported,
            ExpenseClaimItemReviewReasons.LowOcrConfidence, ExpenseClaimItemReviewReasons.VatUnclear,
            ExpenseClaimItemReviewReasons.OutsideTripDates, ExpenseClaimItemReviewReasons.DuplicateFile,
            ExpenseClaimItemReviewReasons.DuplicateInvoice, ExpenseClaimItemReviewReasons.DuplicateAmountDate,
            ExpenseClaimItemReviewReasons.WorkflowRequiresReview,
        }, reasons);
    }

    [Fact]
    public void Trip_dates_are_inclusive_and_optional()
    {
        Assert.Empty(ExpenseClaimItemRules.ReviewReasons(V(date: new(2026, 9, 15)), 0.9m, null, Categories, new(2026, 9, 15), new(2026, 9, 20), NoDup, false));
        Assert.Empty(ExpenseClaimItemRules.ReviewReasons(V(date: new(2026, 9, 20)), 0.9m, null, Categories, new(2026, 9, 15), new(2026, 9, 20), NoDup, false));
        Assert.Empty(ExpenseClaimItemRules.ReviewReasons(V(date: new(2025, 1, 1)), 0.9m, null, Categories, null, null, NoDup, false));
    }

    [Fact]
    public void Category_must_be_a_configured_one_and_an_account_number_is_never_a_category()
    {
        Assert.Equal("an_uong", ExpenseClaimItemRules.MatchCategory("AN_UONG ", Categories));
        Assert.Null(ExpenseClaimItemRules.MatchCategory("642", Categories));
        Assert.Null(ExpenseClaimItemRules.MatchCategory(null, Categories));
    }

    [Fact]
    public void Engine_input_treats_only_tax_coded_bills_as_invoices()
    {
        var ticket = ExpenseClaimItemRules.ToEngineInput(1, V(number: "VE-001"));
        Assert.Null(ticket.InvoiceNo);   // a ticket number alone is not a GTGT invoice
        var invoice = ExpenseClaimItemRules.ToEngineInput(2, V(mst: "0301234567", number: "0001234", vat: 200_000m));
        Assert.Equal(("0301234567", "0001234", 200_000m, true), (invoice.VendorMst, invoice.InvoiceNo, invoice.VatAmount!.Value, invoice.ReceiptAttached!.Value));
        Assert.Equal((2, "cong_tac", 600_000m), (invoice.Seq, invoice.Category, invoice.Amount));
    }

    [Fact]
    public void Blank_currency_follows_the_existing_vnd_rule() =>
        Assert.Equal(("VND", "VND", "USD"), (ExpenseClaimItemRules.NormalizeCurrency(null), ExpenseClaimItemRules.NormalizeCurrency(" vnd"), ExpenseClaimItemRules.NormalizeCurrency("usd")));

    [Fact]
    public void Item_prompt_is_one_bill_and_never_asks_for_accounts_or_tax_status()
    {
        var p = ExpenseClaimItemRules.BuildItemPrompt(null, Categories, "HOA DON KHACH SAN 2.200.000");
        Assert.Contains("ĐÚNG MỘT khoản chi", p);
        Assert.Contains("cong_tac, khach_san, an_uong", p);
        Assert.Contains("KHÔNG đề xuất tài khoản kế toán", p);
        Assert.DoesNotContain("debit_account", p);
        Assert.Contains("HOA DON KHACH SAN 2.200.000", p);
    }

    [Fact]
    public void Preview_never_shows_a_final_payable_while_items_are_pending()
    {
        static ExpenseClaimItemRecord Item(string status, decimal gross) => new(Guid.NewGuid(), Guid.NewGuid(), 1, "a.pdf", "application/pdf", 1, "s",
            status, [], 1, null, null, "cong_tac", null, null, null, null, gross, null, "VND", null,
            null, null, null, null, null, null, null, null, null, null, null, null, null, null,
            null, null, null, null, null, null, null, null, null, Guid.NewGuid(), DateTime.UtcNow);
        var pending = ExpenseClaimIntakeRepository.Totals([Item("ready", 2_800_000m), Item("needs_review", 900_000m)], 2_000_000m);
        Assert.Equal((2_800_000m, 900_000m, (decimal?)null), (pending.ReadyTotal, pending.UnresolvedKnownTotal, pending.ExpectedPayable));
        var done = ExpenseClaimIntakeRepository.Totals([Item("ready", 2_800_000m), Item("ready", 900_000m), Item("excluded", 5_000_000m)], 2_000_000m);
        Assert.Equal((3_700_000m, 1_700_000m, 1), (done.ReadyTotal, done.ExpectedPayable!.Value, done.ExcludedCount));
    }

    // ----------------------------------------------------------------- ACL

    private static IReadOnlyList<RouteEndpoint> Map()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        // Registered so minimal APIs bind them as services (as in Program.cs), never as a request body.
        builder.Services.AddScoped<IExpenseClaimIntakeRepository>(_ => null!);
        builder.Services.AddScoped<Aiq.Gateway.Storage.IObjectStorage>(_ => null!);
        builder.Services.AddScoped<Aiq.Gateway.Jobs.IExpenseClaimItemJobScheduler>(_ => null!);
        builder.Services.AddOptions<Aiq.Gateway.Storage.S3Options>();
        var app = builder.Build();
        app.MapExpenseClaimEndpoints();
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToList();
    }

    private static string[] Policies(RouteEndpoint e) => e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy ?? "").ToArray();

    [Theory]
    [InlineData("/expense-claims/{id:guid}/items/{itemId:guid}", "PATCH")]
    [InlineData("/expense-claims/{id:guid}/items/{itemId:guid}/exclude", "POST")]
    [InlineData("/expense-claims/{id:guid}/items/{itemId:guid}/retry", "POST")]
    public void Item_review_actions_require_department_admin(string route, string method)
    {
        var e = Map().Single(x => x.RoutePattern.RawText == route
            && x.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
        Assert.Equal(new[] { "department_member", "department_admin" }, Policies(e));
    }

    [Theory]
    [InlineData("/expense-claims/", "POST")]
    [InlineData("/expense-claims/{id:guid}/items", "POST")]
    [InlineData("/expense-claims/{id:guid}", "GET")]
    [InlineData("/expense-claims/employees", "GET")]
    public void Intake_and_reads_follow_the_voucher_upload_rule_department_member(string route, string method)
    {
        var e = Map().Single(x => x.RoutePattern.RawText == route
            && x.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
        Assert.Equal(new[] { "department_member" }, Policies(e));
    }
}
