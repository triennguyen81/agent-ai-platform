using Aiq.Gateway.Vouchers;
using Aiq.Gateway.Vouchers.Validation;

namespace Aiq.Gateway.Tests.Vouchers;

/// <summary>
/// Chứng từ flow v2 PR2: deterministic voucher validator (no I/O, no AI).
/// Rules and tolerances follow docs/flows/chung-tu-proposed.md §3.3 and the
/// approved decisions QĐ 8–11 (§10).
/// </summary>
public sealed class VoucherValidatorTests
{
    private const string Company = "0312345673"; // valid 10-digit checksum
    private const string Supplier = "0101243150";
    private static readonly DateOnly Today = new(2026, 10, 29);

    private static readonly IReadOnlyList<VatRateEntry> Rates =
    [
        new("0", 0m, new DateOnly(2000, 1, 1), null, null),
        new("5", 5m, new DateOnly(2000, 1, 1), null, null),
        new("8", 8m, new DateOnly(2025, 7, 1), new DateOnly(2026, 12, 31), "NĐ 174/2025"),
        new("10", 10m, new DateOnly(2000, 1, 1), null, null),
        new("KCT", null, new DateOnly(2000, 1, 1), null, null),
    ];

    private static VoucherValidationContext Ctx(
        IReadOnlyDictionary<string, decimal>? confidence = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? required = null,
        decimal threshold = 0.80m,
        string? company = Company) =>
        new(company, Rates, Today, required, threshold, confidence);

    /// <summary>A purchase invoice that passes every rule: 10.000.000 + 10% = 11.000.000.</summary>
    private static VoucherRecord Invoice(Func<VoucherRecord, VoucherRecord>? edit = null)
    {
        var v = new VoucherRecord
        {
            VoucherType = VoucherTypes.InvoiceIn,
            DocumentNumber = "0000123",
            DocumentSeries = "1C26TAA",
            DocumentDate = new DateOnly(2026, 9, 15),
            SellerName = "Công ty NCC",
            SellerMst = Supplier,
            BuyerName = "Công ty Chúng ta",
            BuyerMst = Company,
            Subtotal = 10_000_000m,
            VatRate = 10m,
            VatAmount = 1_000_000m,
            TotalAmount = 11_000_000m,
        };
        return edit is null ? v : edit(v);
    }

    private static VoucherLineItemRecord Line(decimal qty, decimal price, decimal subtotal, decimal? rate = 10m, decimal? vat = null) => new()
    {
        Description = "Hàng",
        Quantity = qty,
        UnitPrice = price,
        Subtotal = subtotal,
        VatRate = rate,
        VatAmount = vat ?? (rate is null ? null : Math.Round(subtotal * rate.Value / 100m)),
    };

    private static VoucherJournalRecord Entry(string debit, string credit, decimal amount, bool rejected = false) => new()
    {
        DebitAccount = debit,
        CreditAccount = credit,
        Amount = amount,
        RejectedAt = rejected ? DateTime.UtcNow : null,
    };

    private static VoucherValidationResult Run(VoucherRecord v, IReadOnlyList<VoucherLineItemRecord>? lines = null,
        IReadOnlyList<VoucherJournalRecord>? journal = null, VoucherValidationContext? ctx = null) =>
        VoucherValidator.Validate(new VoucherDetail(v, lines ?? [], journal ?? []), ctx ?? Ctx());

    private static string[] Codes(VoucherValidationResult r, string severity) =>
        r.Issues.Where(i => i.Severity == severity).Select(i => i.Code).Distinct().Order().ToArray();

    // ------------------------------------------------------------ baseline

    [Fact]
    public void A_correct_purchase_invoice_has_no_issue()
    {
        var r = Run(Invoice(),
            [Line(2, 3_000_000m, 6_000_000m), Line(1, 4_000_000m, 4_000_000m)],
            [Entry("1561", "331", 10_000_000m), Entry("1331", "331", 1_000_000m)]);

        Assert.Empty(r.Issues);
        Assert.False(r.HasErrors);
        Assert.Equal(VoucherValidator.Version, r.ValidatorVersion);
    }

    [Fact]
    public void Validation_is_pure_and_deterministic()
    {
        var v = Invoice(x => new VoucherRecord { VoucherType = x.VoucherType, TotalAmount = 5m });
        Assert.Equal(Run(v).Issues, Run(v).Issues);
    }

    // ---------------------------------------------------- required (QĐ 8)

    [Theory]
    [InlineData("document_date")]
    [InlineData("document_number")]
    [InlineData("seller_name")]
    [InlineData("buyer_name")]
    [InlineData("seller_mst")]
    [InlineData("subtotal")]
    [InlineData("vat_rate")]
    [InlineData("vat_amount")]
    [InlineData("total_amount")]
    public void Each_minimum_invoice_field_is_required(string field)
    {
        var v = Invoice(x => field switch
        {
            "document_date" => Copy(x, documentDate: null),
            "document_number" => Copy(x, documentNumber: ""),
            "seller_name" => Copy(x, sellerName: " "),
            "buyer_name" => Copy(x, buyerName: null),
            "seller_mst" => Copy(x, sellerMst: null),
            "subtotal" => Copy(x, subtotal: null),
            "vat_rate" => Copy(x, vatRate: null),
            "vat_amount" => Copy(x, vatAmount: null),
            "total_amount" => Copy(x, totalAmount: null),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        });

        var r = Run(v);

        Assert.Contains(r.Issues, i => i is { Code: "REQUIRED_FIELD", Severity: "error" } && i.Field == field);
    }

    [Fact]
    public void Mixed_rate_invoice_may_leave_vat_rate_empty_when_every_line_has_one()
    {
        var v = Invoice(x => Copy(x, vatRate: null, subtotal: 3_000_000m, vatAmount: 250_000m, totalAmount: 3_250_000m));

        var r = Run(v, [Line(1, 1_000_000m, 1_000_000m, 5m), Line(1, 2_000_000m, 2_000_000m, 10m)]);

        Assert.DoesNotContain(r.Issues, i => i.Field == "vat_rate");
        Assert.False(r.HasErrors);
    }

    [Fact]
    public void Tenant_override_replaces_the_required_list_for_that_type()
    {
        var required = new Dictionary<string, IReadOnlyList<string>> { [VoucherTypes.InvoiceIn] = ["document_number"] };
        var v = Invoice(x => Copy(x, buyerName: null, sellerName: null));

        var r = Run(v, ctx: Ctx(required: required));

        Assert.DoesNotContain(r.Issues, i => i.Code == "REQUIRED_FIELD");
    }

    [Fact]
    public void Cash_vouchers_require_number_date_and_amount()
    {
        var r = Run(new VoucherRecord { VoucherType = VoucherTypes.Receipt });

        Assert.Equal(["document_date", "document_number", "total_amount"],
            r.Issues.Where(i => i.Code == "REQUIRED_FIELD").Select(i => i.Field!).Order().ToArray());
    }

    // ------------------------------------------------------------ MST (QĐ 9)

    [Theory]
    [InlineData("0101243150")]        // 10 digits
    [InlineData("0101243150-001")]    // 13 digits, 10-3 form
    [InlineData("0101243150001")]     // 13 digits without the dash
    [InlineData("001099012345")]      // 12-digit CCCD used as personal MST
    public void Accepted_mst_formats(string mst)
    {
        Assert.True(MstRules.IsValidFormat(mst));
        var r = Run(Invoice(x => Copy(x, sellerMst: mst)));
        Assert.DoesNotContain(r.Issues, i => i.Code == "MST_FORMAT");
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("01012431500")]       // 11 digits
    [InlineData("0101243150-01")]     // wrong branch suffix
    [InlineData("01012A3150")]
    [InlineData("01-01243150")]       // dash in the wrong place
    public void Rejected_mst_formats_are_errors(string mst)
    {
        Assert.False(MstRules.IsValidFormat(mst));
        var r = Run(Invoice(x => Copy(x, sellerMst: mst)));
        Assert.Contains(r.Issues, i => i is { Code: "MST_FORMAT", Severity: "error", Field: "seller_mst" });
    }

    [Fact]
    public void Bad_mst_checksum_is_only_a_warning()
    {
        Assert.True(MstRules.ChecksumOk("0101243150"));
        Assert.False(MstRules.ChecksumOk("0101243151"));

        var r = Run(Invoice(x => Copy(x, sellerMst: "0101243151")));

        Assert.Contains(r.Issues, i => i is { Code: "MST_CHECKSUM", Severity: "warning", Field: "seller_mst" });
        Assert.False(r.HasErrors);
    }

    [Fact]
    public void Twelve_digit_cccd_has_no_checksum_rule()
    {
        Assert.Null(MstRules.ChecksumOk("001099012345"));
    }

    [Fact]
    public void Mst_comparison_ignores_dashes_and_spaces()
    {
        Assert.Equal("0101243150001", MstRules.Normalize(" 0101243150-001 "));
    }

    // ------------------------------------------------- company party

    [Fact]
    public void Purchase_invoice_buyer_must_be_the_company()
    {
        var r = Run(Invoice(x => Copy(x, buyerMst: "0100109106")));
        Assert.Contains(r.Issues, i => i is { Code: "BUYER_IS_COMPANY", Severity: "error", Field: "buyer_mst" });
    }

    [Fact]
    public void Sales_invoice_seller_must_be_the_company()
    {
        var sale = Invoice(x => Copy(x, voucherType: VoucherTypes.InvoiceOut, sellerMst: Supplier, buyerMst: "0100109106"));
        var r = Run(sale, journal: [Entry("131", "5111", 10_000_000m), Entry("131", "33311", 1_000_000m)]);
        Assert.Contains(r.Issues, i => i is { Code: "SELLER_IS_COMPANY", Severity: "error", Field: "seller_mst" });
    }

    [Fact]
    public void Company_match_ignores_branch_dash_formatting()
    {
        // The profile value may carry stray spaces; MSTs are compared normalized.
        var r = Run(Invoice(), ctx: Ctx(company: " " + Company + " "));
        Assert.DoesNotContain(r.Issues, i => i.Code == "BUYER_IS_COMPANY");
    }

    [Fact]
    public void Missing_company_profile_is_a_warning_not_a_guess()
    {
        var r = Run(Invoice(), ctx: Ctx(company: null));
        Assert.Contains(r.Issues, i => i is { Code: "COMPANY_TAX_CODE_MISSING", Severity: "warning" });
        Assert.DoesNotContain(r.Issues, i => i.Code == "BUYER_IS_COMPANY");
    }

    // ------------------------------------------------ amounts (QĐ 10)

    [Fact]
    public void Total_must_equal_subtotal_minus_discount_plus_vat()
    {
        var r = Run(Invoice(x => Copy(x, totalAmount: 11_000_005m)));
        Assert.Contains(r.Issues, i => i is { Code: "AMOUNT_SUM", Severity: "error", Field: "total_amount" });
    }

    [Fact]
    public void Discount_is_subtracted_before_comparing_the_total()
    {
        var r = Run(Invoice(x => Copy(x, discountAmount: 1_000_000m, vatAmount: 900_000m, totalAmount: 9_900_000m)));
        Assert.DoesNotContain(r.Issues, i => i.Code is "AMOUNT_SUM" or "VAT_AMOUNT");
    }

    [Fact]
    public void One_dong_rounding_is_tolerated_without_lines()
    {
        var r = Run(Invoice(x => Copy(x, totalAmount: 11_000_001m)));
        Assert.DoesNotContain(r.Issues, i => i.Code == "AMOUNT_SUM");
    }

    [Fact]
    public void Total_tolerance_grows_by_one_dong_per_line()
    {
        var lines = new[] { Line(1, 3_000_000m, 3_000_000m), Line(1, 3_000_000m, 3_000_000m), Line(1, 4_000_000m, 4_000_000m) };

        Assert.DoesNotContain(Run(Invoice(x => Copy(x, totalAmount: 11_000_003m)), lines).Issues, i => i.Code == "AMOUNT_SUM");
        Assert.Contains(Run(Invoice(x => Copy(x, totalAmount: 11_000_004m)), lines).Issues, i => i.Code == "AMOUNT_SUM");
    }

    [Fact]
    public void Line_subtotals_must_add_up_to_the_voucher_subtotal()
    {
        var r = Run(Invoice(), [Line(1, 6_000_000m, 6_000_000m), Line(1, 3_000_000m, 3_000_000m)]);
        Assert.Contains(r.Issues, i => i is { Code: "LINE_SUM", Severity: "error", Field: "subtotal" });
    }

    [Fact]
    public void Line_quantity_times_price_off_by_more_than_one_dong_is_a_warning()
    {
        var r = Run(Invoice(), [Line(2, 3_000_000m, 6_000_002m), Line(1, 3_999_998m, 3_999_998m)]);

        var issue = Assert.Single(r.Issues, i => i.Code == "LINE_AMOUNT");
        Assert.Equal("warning", issue.Severity);
        Assert.Equal("line_items[0].subtotal", issue.Field);
    }

    [Fact]
    public void Line_vat_must_match_its_rate_within_one_dong()
    {
        var r = Run(Invoice(), [Line(1, 6_000_000m, 6_000_000m, vat: 600_002m), Line(1, 4_000_000m, 4_000_000m, vat: 399_998m)]);
        Assert.Equal(2, r.Issues.Count(i => i is { Code: "VAT_LINE", Severity: "warning" }));
    }

    [Fact]
    public void Voucher_vat_must_match_its_single_rate()
    {
        var r = Run(Invoice(x => Copy(x, vatAmount: 800_000m, totalAmount: 10_800_000m)));
        Assert.Contains(r.Issues, i => i is { Code: "VAT_AMOUNT", Severity: "warning", Field: "vat_amount" });
    }

    // ---------------------------------------------- VAT rates (QĐ 11)

    [Theory]
    [InlineData(2025, 7, 1)]
    [InlineData(2026, 12, 31)]
    public void Eight_percent_is_valid_inside_its_window(int y, int m, int d)
    {
        var r = Run(Invoice(x => Copy(x, documentDate: new DateOnly(y, m, d), vatRate: 8m, vatAmount: 800_000m, totalAmount: 10_800_000m)),
            ctx: Ctx() with { Today = new DateOnly(2027, 1, 1) });
        Assert.DoesNotContain(r.Issues, i => i.Code == "VAT_RATE");
    }

    [Theory]
    [InlineData(2025, 6, 30)]
    [InlineData(2027, 1, 1)]
    public void Eight_percent_outside_its_window_is_an_error(int y, int m, int d)
    {
        var r = Run(Invoice(x => Copy(x, documentDate: new DateOnly(y, m, d), vatRate: 8m, vatAmount: 800_000m, totalAmount: 10_800_000m)),
            ctx: Ctx() with { Today = new DateOnly(2027, 6, 1) });
        Assert.Contains(r.Issues, i => i is { Code: "VAT_RATE", Severity: "error", Field: "vat_rate" });
    }

    [Fact]
    public void Line_rates_are_checked_by_the_invoice_date_too()
    {
        var r = Run(Invoice(x => Copy(x, vatRate: null, vatAmount: 1_070_000m, totalAmount: 11_070_000m)),
            [Line(1, 6_000_000m, 6_000_000m, 7m), Line(1, 4_000_000m, 4_000_000m, 10m)]);
        Assert.Contains(r.Issues, i => i is { Code: "VAT_RATE", Severity: "error", Field: "line_items[0].vat_rate" });
    }

    [Fact]
    public void Unknown_rate_table_is_a_warning_not_a_pass()
    {
        var r = Run(Invoice(), ctx: Ctx() with { VatRates = [] });
        Assert.Contains(r.Issues, i => i is { Code: "VAT_RATE_TABLE_EMPTY", Severity: "warning" });
    }

    // --------------------------------------------------------------- dates

    [Fact]
    public void A_document_date_one_day_ahead_is_tolerated()
    {
        Assert.DoesNotContain(Run(Invoice(x => Copy(x, documentDate: Today.AddDays(1)))).Issues, i => i.Code == "DATE_FUTURE");
    }

    [Fact]
    public void A_document_date_more_than_one_day_ahead_is_an_error()
    {
        var r = Run(Invoice(x => Copy(x, documentDate: Today.AddDays(2))));
        Assert.Contains(r.Issues, i => i is { Code: "DATE_FUTURE", Severity: "error", Field: "document_date" });
    }

    // 23:30 on 29/10 in Vietnam is 16:30 UTC the same day; 00:30 on 30/10 is
    // 17:30 UTC on 29/10 — the UTC date would be one day behind.
    [Theory]
    [InlineData(16, 30, "2026-10-29")]
    [InlineData(17, 30, "2026-10-30")]
    public void Today_is_the_calendar_date_in_Asia_Ho_Chi_Minh(int utcHour, int utcMinute, string expected)
    {
        var time = new FixedTime(new DateTimeOffset(2026, 10, 29, utcHour, utcMinute, 0, TimeSpan.Zero));
        Assert.Equal(DateOnly.Parse(expected), VoucherValidationService.TodayInVietnam(time));
    }

    [Theory]
    [InlineData(16, 30, "2026-10-30", false)] // 23:30 VN 29/10: tomorrow is tolerated
    [InlineData(16, 30, "2026-10-31", true)]
    [InlineData(17, 30, "2026-10-31", false)] // 00:30 VN 30/10: 31/10 is tomorrow
    [InlineData(17, 30, "2026-11-01", true)]
    public void Future_date_rule_uses_today_in_Vietnam(int utcHour, int utcMinute, string documentDate, bool future)
    {
        var today = VoucherValidationService.TodayInVietnam(
            new FixedTime(new DateTimeOffset(2026, 10, 29, utcHour, utcMinute, 0, TimeSpan.Zero)));
        var r = Run(Invoice(x => Copy(x, documentDate: DateOnly.Parse(documentDate))), ctx: Ctx() with { Today = today });
        Assert.Equal(future, r.Issues.Any(i => i.Code == "DATE_FUTURE"));
    }

    // -------------------------------------------------------------- journal

    [Fact]
    public void Journal_lines_need_both_accounts_and_a_positive_amount()
    {
        var r = Run(Invoice(), journal: [Entry("1561", "1561", 10_000_000m), Entry("", "331", 1_000_000m), Entry("1331", "331", 0m)]);
        Assert.Equal(3, r.Issues.Count(i => i is { Code: "JOURNAL_LINE", Severity: "error" }));
    }

    [Fact]
    public void Total_debit_must_equal_total_credit()
    {
        // A line missing its credit side books 1.000.000 on the debit side only.
        var r = Run(Invoice(), journal: [Entry("1561", "331", 10_000_000m), Entry("1331", "", 1_000_000m)]);
        var issue = Assert.Single(r.Issues, i => i.Code == "JOURNAL_BALANCE");
        Assert.Equal(("error", "journal"), (issue.Severity, issue.Field));
    }

    [Fact]
    public void A_balanced_journal_has_no_balance_error()
    {
        var r = Run(Invoice(), journal: [Entry("1561", "331", 10_000_000m), Entry("1331", "331", 1_000_000m)]);
        Assert.DoesNotContain(r.Issues, i => i.Code == "JOURNAL_BALANCE");
    }

    [Fact]
    public void Settlement_total_differing_from_the_invoice_total_is_only_a_warning()
    {
        // Partial payment, an advance (141) or an AR/AP offset are legitimate.
        var r = Run(Invoice(), journal: [Entry("1561", "331", 10_000_000m), Entry("1331", "141", 1_000_000m)]);
        var issue = Assert.Single(r.Issues, i => i.Code == "JOURNAL_TOTAL");
        Assert.Equal("warning", issue.Severity);
        Assert.False(r.HasErrors);
    }

    [Fact]
    public void Sales_journal_must_debit_the_invoice_total_to_receivables_or_cash()
    {
        var sale = Invoice(x => Copy(x, voucherType: VoucherTypes.InvoiceOut, sellerMst: Company, buyerMst: Supplier));

        Assert.DoesNotContain(Run(sale, journal: [Entry("131", "5111", 10_000_000m), Entry("131", "33311", 1_000_000m)]).Issues,
            i => i.Code == "JOURNAL_TOTAL");
        Assert.Contains(Run(sale, journal: [Entry("131", "5111", 10_000_000m)]).Issues, i => i.Code == "JOURNAL_TOTAL");
    }

    [Fact]
    public void Rejected_suggestions_are_ignored()
    {
        var r = Run(Invoice(), journal:
        [
            Entry("1561", "331", 10_000_000m), Entry("1331", "331", 1_000_000m),
            Entry("642", "331", 99m, rejected: true),
        ]);
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void A_malformed_account_code_is_a_warning_until_the_chart_exists()
    {
        var r = Run(Invoice(), journal: [Entry("15x1", "331", 10_000_000m), Entry("1331", "331", 1_000_000m)]);
        Assert.Contains(r.Issues, i => i is { Code: "JOURNAL_ACCOUNT", Severity: "warning" });
    }

    [Fact]
    public void No_journal_means_no_journal_checks()
    {
        Assert.DoesNotContain(Run(Invoice()).Issues, i => i.Code.StartsWith("JOURNAL_", StringComparison.Ordinal));
    }

    // ------------------------------------------------- confidence (QĐ 7)

    [Fact]
    public void Low_confidence_required_fields_are_warnings_at_the_tenant_threshold()
    {
        var confidence = new Dictionary<string, decimal> { ["seller_mst"] = 0.79m, ["total_amount"] = 0.80m, ["seller_address"] = 0.1m };

        var r = Run(Invoice(), ctx: Ctx(confidence: confidence));

        var low = Assert.Single(r.Issues, i => i.Code == "LOW_CONFIDENCE");
        Assert.Equal(("warning", "seller_mst"), (low.Severity, low.Field));
        Assert.Empty(Run(Invoice(), ctx: Ctx(confidence: confidence, threshold: 0.5m)).Issues);
    }

    // --------------------------------------------------------------- helper

    // ---- PERIOD_LOCKED (PR4, §10.1 P1, P11) ---------------------------------

    private static VoucherValidationContext WithClosed(params string[] periods) => Ctx() with
    {
        ClosedPeriods = periods.Select(p => new Aiq.Gateway.Vouchers.Periods.AccountingPeriod(int.Parse(p[..4]), int.Parse(p[5..]))).ToHashSet(),
    };

    [Fact]
    public void A_voucher_of_a_closed_period_is_an_error_on_its_accounting_date()
    {
        var r = Run(Invoice(), ctx: WithClosed("2026-09"));
        Assert.Contains(r.Issues, i => i is { Code: "PERIOD_LOCKED", Severity: "error", Field: "document_date" });
        Assert.True(r.HasErrors);

        var booked = Run(Copy(Invoice(), accountingDate: new DateOnly(2026, 9, 30)), ctx: WithClosed("2026-09"));
        Assert.Contains(booked.Issues, i => i is { Code: "PERIOD_LOCKED", Field: "accounting_date" });
    }

    [Fact]
    public void The_accounting_date_moves_a_late_invoice_out_of_a_closed_period()
    {
        // Invoice dated 15/09 (September closed), booked on 01/10.
        var r = Run(Copy(Invoice(), accountingDate: new DateOnly(2026, 10, 1)), ctx: WithClosed("2026-09"));
        Assert.DoesNotContain(r.Issues, i => i.Code == "PERIOD_LOCKED");
    }

    [Fact]
    public void No_closed_periods_or_no_date_means_no_period_rule()
    {
        Assert.DoesNotContain(Run(Invoice()).Issues, i => i.Code == "PERIOD_LOCKED");                 // tenant without v2: null set
        Assert.DoesNotContain(Run(Invoice(), ctx: WithClosed("2026-08", "2026-10")).Issues, i => i.Code == "PERIOD_LOCKED");
        Assert.DoesNotContain(Run(Copy(Invoice(), documentDate: null), ctx: WithClosed("2026-09")).Issues, i => i.Code == "PERIOD_LOCKED");
    }

    [Fact]
    public void The_validator_version_names_the_period_rule() => Assert.Equal(3, VoucherValidator.Version);

    private static VoucherRecord Copy(VoucherRecord x,
        string? voucherType = null,
        Optional<DateOnly?> documentDate = default,
        Optional<string?> documentNumber = default,
        Optional<string?> sellerName = default,
        Optional<string?> buyerName = default,
        Optional<string?> sellerMst = default,
        Optional<string?> buyerMst = default,
        Optional<decimal?> subtotal = default,
        Optional<decimal?> vatRate = default,
        Optional<decimal?> vatAmount = default,
        Optional<decimal?> discountAmount = default,
        Optional<decimal?> totalAmount = default,
        Optional<DateOnly?> accountingDate = default) => new()
    {
        AccountingDate = accountingDate.Or(x.AccountingDate),
        VoucherType = voucherType ?? x.VoucherType,
        DocumentNumber = documentNumber.Or(x.DocumentNumber),
        DocumentSeries = x.DocumentSeries,
        DocumentDate = documentDate.Or(x.DocumentDate),
        SellerName = sellerName.Or(x.SellerName),
        SellerMst = sellerMst.Or(x.SellerMst),
        BuyerName = buyerName.Or(x.BuyerName),
        BuyerMst = buyerMst.Or(x.BuyerMst),
        Subtotal = subtotal.Or(x.Subtotal),
        VatRate = vatRate.Or(x.VatRate),
        VatAmount = vatAmount.Or(x.VatAmount),
        DiscountAmount = discountAmount.Or(x.DiscountAmount),
        TotalAmount = totalAmount.Or(x.TotalAmount),
    };

    /// <summary>Distinguishes "not passed" from an explicit null.</summary>
    private readonly struct Optional<T>
    {
        private readonly bool _set;
        private readonly T _value;
        private Optional(T value) { _set = true; _value = value; }
        public static implicit operator Optional<T>(T value) => new(value);
        public T Or(T fallback) => _set ? _value : fallback;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
