using Aiq.Gateway.Banking;
using Aiq.Gateway.Contracts;
using Aiq.Gateway.Payments;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Contract payment (early-settlement) discount: structured, revisioned
/// Contract term → deterministic eligibility → read-only proposal on a
/// Phiếu thu AND a bank-statement money_in line (same calculator, same
/// confirmation core) → ADMIN confirmation re-evaluated server-side → stops
/// at payment_discount_accounting_policy_missing (no approved discount
/// account exists) with ZERO settlement effect.
/// </summary>
public sealed partial class CashSettlementIntegrationTests
{
    private const string DiscountCustomerMst = "0309990001";
    private IContractPaymentDiscountRepository DiscountTerms() => new ContractPaymentDiscountRepository(Factory());

    private sealed record DiscountSetup(Guid Customer, Guid Contract, Guid Invoice, Guid TermId, int Revision);

    private async Task<Guid> DiscountContractAsync(string number, string buyerMst = DiscountCustomerMst, Guid? departmentId = null,
        string currency = "VND")
    {
        var id = await Scalar<Guid>("""
            INSERT INTO contracts(tenant_id, department_id, title, contract_number, workflow_state, direction, currency)
            VALUES (@t, @d, @number, @number, 'WAITING_REVIEW', 'sales', @currency) RETURNING id
            """, new { t = _tenantId, d = departmentId ?? _departmentId, number, currency });
        await Scalar<int>("""
            INSERT INTO contract_parties(contract_id, tenant_id, department_id, party_role, name, tax_code) VALUES
              (@id, @t, @d, 'seller', 'Sáu Trực', @me), (@id, @t, @d, 'buyer', 'LIVE-TEST Khách CK', @buyerMst) RETURNING 1
            """, new { id, t = _tenantId, d = departmentId ?? _departmentId, me = MyTaxCode, buyerMst });
        return id;
    }

    private async Task<DiscountSetup> DiscountScenarioAsync(string tag, decimal invoice = 100_000_000m, int invoiceDaysAgo = 3,
        decimal rate = 2m, int days = 10, bool enabled = true, string state = "APPROVED", bool linkInvoice = true,
        string buyerMst = DiscountCustomerMst, string currency = "VND")
    {
        var customer = await ScalarOrNull<Guid?>("SELECT id FROM sim_customers WHERE tenant_id=@t AND mst=@m", new { t = _tenantId, m = DiscountCustomerMst })
            ?? await SeedCustomerAsync("LIVE-TEST-CUS-CK", "Công ty LIVE-TEST Khách CK", DiscountCustomerMst);
        var contract = await DiscountContractAsync($"LIVE-TEST-DISCOUNT-{tag}", buyerMst, currency: currency);
        var saved = await DiscountTerms().SaveAsync(_tenantId, _departmentId, contract, _adminId,
            new SaveContractPaymentDiscountRequest(enabled, rate, "invoice_date", days, "2% trong 10 ngày kể từ ngày hoá đơn", "Điều 5"), default);
        Assert.Equal(DiscountTermSaveStatus.Saved, saved.Status);
        await Scalar<int>("UPDATE contracts SET workflow_state=@state WHERE id=@contract RETURNING 1", new { state, contract });
        var doc = $"HD-CK-{tag}";
        await Customers().BookInvoiceAsync(_tenantId, customer, Guid.NewGuid(), doc, Today.AddDays(-invoiceDaysAgo), Today.AddDays(30),
            invoice, linkInvoice ? contract : null, null, default);
        var inv = await Scalar<Guid>("SELECT id FROM sim_ar_ledger WHERE document_number=@doc AND document_type='invoice'", new { doc });
        return new DiscountSetup(customer, contract, inv, saved.Term!.Id, saved.Term.Revision);
    }

    private Task<Guid> DiscountReceiptAsync(string doc, decimal amount, DateOnly? date = null, string? extraFieldsJson = null) =>
        Scalar<Guid>("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, document_number, document_date,
                seller_name, seller_mst, buyer_name, buyer_mst, total_amount, payment_method, extracted_fields)
            VALUES (@u, @t, @d, 'receipt', @doc, @date, 'Cong Ty TNHH Sáu Trực', @me, 'Công ty LIVE-TEST Khách CK', @mst,
                @amount, 'Chuyển khoản', COALESCE(@extra, '{"reason":"Thu tiền hàng"}')::jsonb) RETURNING id
            """, new { u = _adminId, t = _tenantId, d = _departmentId, doc, date = date ?? Today, me = MyTaxCode,
                       mst = DiscountCustomerMst, amount, extra = extraFieldsJson });

    private static ConfirmPaymentDiscountRequest ConfirmFrom(PaymentDiscountEvaluation e) =>
        new(e.InvoiceId, e.TermId, e.TermRevision, e.CalculatedDiscount, e.OpenAmount);

    private async Task<PaymentDiscountEvaluation> SingleProposalAsync(Guid receipt) =>
        Assert.Single((await Settlement().GetContextAsync(_tenantId, _departmentId, receipt, default))!.DiscountProposals).Evaluation;

    private async Task<string> SettlementSnapshotAsync() =>
        string.Join("|", await AccountingSnapshotAsync(),
            await Scalar<string>("SELECT COALESCE(string_agg(current_balance::text, ',' ORDER BY id), '') FROM sim_bank_accounts"));

    // =================================================== CONTRACT TERM

    [Fact]
    public async Task Term_is_revisioned_audited_and_frozen_once_the_contract_is_approved()
    {
        if (_cs is null) return;
        var contract = await DiscountContractAsync("LIVE-TEST-DISCOUNT-REV");
        var r1 = await DiscountTerms().SaveAsync(_tenantId, _departmentId, contract, _adminId, new(true, 2m, "invoice_date", 10, null, null), default);
        var r2 = await DiscountTerms().SaveAsync(_tenantId, _departmentId, contract, _adminId, new(true, 1.5m, "invoice_date", 7, null, null), default);
        Assert.Equal((1, 2), (r1.Term!.Revision, r2.Term!.Revision));
        var view = (await DiscountTerms().GetAsync(_tenantId, _departmentId, contract, default))!;
        Assert.Equal((r2.Term.Id, 1.5m, 7), (view.Current!.Id, view.Current.RatePercent, view.Current.DaysCount));
        Assert.Equal(2, view.Revisions.Count);
        Assert.NotNull(view.Revisions.Single(r => r.Revision == 1).SupersededAt);
        Assert.False(view.Effective);   // not approved yet
        Assert.Equal(2, await Scalar<int>("SELECT COUNT(*) FROM contract_history WHERE contract_id=@contract AND event_type='payment_discount_term_changed'", new { contract }));
        Assert.Equal("VND", view.Current.Currency);

        // A revision row is immutable.
        await Assert.ThrowsAsync<PostgresException>(() =>
            Scalar<int>("UPDATE contract_payment_discount_terms SET rate_percent=50 WHERE id=@id RETURNING 1", new { id = r2.Term.Id }));

        await Scalar<int>("UPDATE contracts SET workflow_state='APPROVED' WHERE id=@contract RETURNING 1", new { contract });
        Assert.True((await DiscountTerms().GetAsync(_tenantId, _departmentId, contract, default))!.Effective);
        Assert.Equal(DiscountTermSaveStatus.ContractFinalized,
            (await DiscountTerms().SaveAsync(_tenantId, _departmentId, contract, _adminId, new(true, 9m, "invoice_date", 30, null, null), default)).Status);
        Assert.Equal(2, await Scalar<int>("SELECT COUNT(*) FROM contract_payment_discount_terms WHERE contract_id=@contract", new { contract }));
    }

    [Theory]
    [InlineData(0, "invoice_date", 10, "ratePercent")]
    [InlineData(-1, "invoice_date", 10, "ratePercent")]
    [InlineData(100, "invoice_date", 10, "ratePercent")]
    [InlineData(2, "invoice_due_date", 10, "basis")]
    [InlineData(2, "formula:amount*0.5", 10, "basis")]
    [InlineData(2, "invoice_date", -1, "daysCount")]
    public async Task Invalid_terms_are_rejected_and_nothing_is_stored(double rate, string basis, int days, string field)
    {
        if (_cs is null) return;
        var contract = await DiscountContractAsync($"LIVE-TEST-DISCOUNT-INV-{field}-{days}");
        var r = await DiscountTerms().SaveAsync(_tenantId, _departmentId, contract, _adminId, new(true, (decimal)rate, basis, days, null, null), default);
        Assert.Equal(DiscountTermSaveStatus.Invalid, r.Status);
        Assert.Contains(field, r.Errors!.Keys);
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM contract_payment_discount_terms WHERE contract_id=@contract", new { contract }));
    }

    [Fact]
    public async Task Terms_are_invisible_and_unwritable_across_department_and_tenant()
    {
        if (_cs is null) return;
        var contract = await DiscountContractAsync("LIVE-TEST-DISCOUNT-SCOPE");
        Assert.Null(await DiscountTerms().GetAsync(_otherTenantId, _otherTenantDepartmentId, contract, default));
        Assert.Null(await DiscountTerms().GetAsync(_tenantId, _otherDepartmentId, contract, default));
        Assert.Equal(DiscountTermSaveStatus.NotFound,
            (await DiscountTerms().SaveAsync(_otherTenantId, _otherTenantDepartmentId, contract, _adminId, new(true, 2m, "invoice_date", 10, null, null), default)).Status);
        Assert.Equal(DiscountTermSaveStatus.NotFound,
            (await DiscountTerms().SaveAsync(_tenantId, _otherDepartmentId, contract, _adminId, new(true, 2m, "invoice_date", 10, null, null), default)).Status);
    }

    // ===================================================== ELIGIBILITY

    [Fact]
    public async Task Full_payment_within_window_is_proposed_confirm_stops_at_policy_and_settles_nothing()
    {
        if (_cs is null) return;
        var s = await DiscountScenarioAsync("FULL");
        var receipt = await DiscountReceiptAsync("PT-CK-FULL", 98_000_000m);
        var e = await SingleProposalAsync(receipt);
        Assert.Equal((PaymentDiscountStatus.Eligible, PaymentDiscountReasons.Eligible), (e.Status, e.ReasonCode));
        Assert.Equal((2_000_000m, 98_000_000m, 0m, 100_000_000m), (e.CalculatedDiscount, e.CashToApply, e.ExcessCash, e.DiscountBasisAmount!.Value));
        Assert.Equal((s.Contract, s.TermId, s.Revision, 2m), (e.ContractId!.Value, e.TermId!.Value, e.TermRevision!.Value, e.RatePercent!.Value));
        Assert.Equal(Today.AddDays(-3).AddDays(10), e.QualifyingDeadline);
        Assert.Equal(Today, e.ReceiptDate);
        var proposal = Assert.Single((await Settlement().GetContextAsync(_tenantId, _departmentId, receipt, default))!.DiscountProposals);
        Assert.Equal((false, PaymentDiscountAccountingPolicy.MissingCode), (proposal.AccountingPolicyAvailable, proposal.BlockedBy));

        var before = await SettlementSnapshotAsync();
        var ex = await Assert.ThrowsAsync<CashSettlementException>(() =>
            Settlement().ConfirmPaymentDiscountAsync(_tenantId, _departmentId, receipt, _adminId, ConfirmFrom(e), default));
        Assert.Equal(PaymentDiscountAccountingPolicy.MissingCode, ex.Code);
        var details = Assert.IsType<PaymentDiscountConflictDetails>(ex.Details);
        Assert.Contains("debit_account_for_payment_discount", details.MissingPolicy!);
        Assert.Equal(before, await SettlementSnapshotAsync());               // zero accounting effect
        Assert.Equal((0m, "open"), await ArInvoiceAsync(s.Invoice));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM cash_transactions WHERE voucher_id=@receipt", new { receipt }));
        var audit = await Row<(string Outcome, decimal Discount, int Revision, Guid Actor, DateOnly Deadline, string Reason)>("""
            SELECT outcome, calculated_discount, term_revision, actor_id, qualifying_deadline, reason_code
            FROM payment_discount_decisions WHERE voucher_id=@receipt
            """, new { receipt });
        Assert.Equal(("blocked_accounting_policy_missing", 2_000_000m, s.Revision, _adminId, e.QualifyingDeadline!.Value, PaymentDiscountReasons.Eligible), audit);

        // "Không áp dụng chiết khấu": the normal settlement is unchanged — cash only, never the discount.
        var posted = (await Settlement().PostAsync(_tenantId, _departmentId, receipt, _adminId, Approve(), default))!;
        Assert.Equal(98_000_000m, Assert.Single(posted.Applications).AppliedAmount);
        Assert.Equal((98_000_000m, "partial"), await ArInvoiceAsync(s.Invoice));
        Assert.Equal(2_000_000m, await ArAsync(s.Customer));
        Assert.Empty((await Settlement().GetContextAsync(_tenantId, _departmentId, receipt, default))!.DiscountProposals);
        Assert.Equal("already_posted", (await Assert.ThrowsAsync<CashSettlementException>(() =>
            Settlement().ConfirmPaymentDiscountAsync(_tenantId, _departmentId, receipt, _adminId, ConfirmFrom(e), default))).Code);
    }

    [Theory]
    [InlineData(10, true)]    // receipt ON the deadline → eligible (inclusive)
    [InlineData(11, false)]   // one day late
    [InlineData(0, true)]     // same day as the invoice
    public async Task Deadline_is_inclusive_and_late_payment_gets_no_discount(int invoiceDaysAgo, bool eligible)
    {
        if (_cs is null) return;
        await DiscountScenarioAsync($"DL{invoiceDaysAgo}", invoiceDaysAgo: invoiceDaysAgo);
        var e = await SingleProposalAsync(await DiscountReceiptAsync($"PT-CK-DL{invoiceDaysAgo}", 98_000_000m));
        Assert.Equal(eligible, e.Eligible);
        if (!eligible) Assert.Equal((PaymentDiscountReasons.PaymentAfterDeadline, 0m), (e.ReasonCode, e.CalculatedDiscount));
    }

    [Fact]
    public async Task Late_receipt_settles_normally_and_leaves_the_shortfall_open()
    {
        if (_cs is null) return;
        var s = await DiscountScenarioAsync("LATE", invoiceDaysAgo: 11);
        var receipt = await DiscountReceiptAsync("PT-CK-LATE", 98_000_000m);
        Assert.False((await SingleProposalAsync(receipt)).Eligible);
        await Settlement().PostAsync(_tenantId, _departmentId, receipt, _adminId, Approve(), default);
        Assert.Equal((98_000_000m, "partial"), await ArInvoiceAsync(s.Invoice));
        Assert.Equal(2_000_000m, await ArAsync(s.Customer));
    }

    [Fact]
    public async Task No_contract_link_means_not_applicable_and_settlement_is_unchanged()
    {
        if (_cs is null) return;
        var s = await DiscountScenarioAsync("NOLINK", linkInvoice: false);
        var receipt = await DiscountReceiptAsync("PT-CK-NOLINK", 98_000_000m);
        Assert.Empty((await Settlement().GetContextAsync(_tenantId, _departmentId, receipt, default))!.DiscountProposals);
        // Never inferred from the same customer / tax code / amount.
        var ex = await Assert.ThrowsAsync<CashSettlementException>(() => Settlement().ConfirmPaymentDiscountAsync(_tenantId, _departmentId, receipt, _adminId,
            new(s.Invoice, s.TermId, s.Revision, 2_000_000m, 100_000_000m), default));
        Assert.Equal(PaymentDiscountConflicts.EligibilityChanged, ex.Code);
        Assert.Equal(PaymentDiscountReasons.NoContractLink, Assert.IsType<PaymentDiscountConflictDetails>(ex.Details).ReasonCode);
        Assert.Equal("posted", (await Settlement().PostAsync(_tenantId, _departmentId, receipt, _adminId, Approve(), default))!.Outcome);
        Assert.Equal((98_000_000m, "partial"), await ArInvoiceAsync(s.Invoice));
    }

    [Theory]
    [InlineData("WAITING_REVIEW", PaymentDiscountReasons.ContractNotEffective)]
    [InlineData("NEEDS_REVIEW", PaymentDiscountReasons.ContractNotEffective)]
    [InlineData("REJECTED", PaymentDiscountReasons.ContractNotEffective)]
    [InlineData("HANDED_OFF", PaymentDiscountReasons.Eligible)]
    public async Task Only_an_approved_contract_produces_a_discount(string state, string reason)
    {
        if (_cs is null) return;
        await DiscountScenarioAsync($"ST-{state}", state: state);
        Assert.Equal(reason, (await SingleProposalAsync(await DiscountReceiptAsync($"PT-CK-{state}", 98_000_000m))).ReasonCode);
    }

    [Fact]
    public async Task Disabled_term_is_ignored_entirely()
    {
        if (_cs is null) return;
        await DiscountScenarioAsync("OFF", enabled: false);
        var receipt = await DiscountReceiptAsync("PT-CK-OFF", 98_000_000m);
        Assert.Empty((await Settlement().GetContextAsync(_tenantId, _departmentId, receipt, default))!.DiscountProposals);
    }

    [Fact]
    public async Task Wrong_customer_or_contract_scope_or_currency_is_not_eligible()
    {
        if (_cs is null) return;
        await DiscountScenarioAsync("CUS", buyerMst: "0300000999");
        Assert.Equal(PaymentDiscountReasons.ContractCustomerMismatch,
            (await SingleProposalAsync(await DiscountReceiptAsync("PT-CK-CUS", 98_000_000m))).ReasonCode);

        // Same customer, invoice linked to a contract of ANOTHER department.
        await Scalar<int>("DELETE FROM sim_ar_ledger WHERE document_number='HD-CK-CUS' RETURNING 1");
        var foreign = await DiscountContractAsync("LIVE-TEST-DISCOUNT-OTHER-DEPT", departmentId: _otherDepartmentId);
        await Scalar<int>("UPDATE contracts SET workflow_state='APPROVED' WHERE id=@foreign RETURNING 1", new { foreign });
        await Scalar<int>("""
            INSERT INTO contract_payment_discount_terms(tenant_id, department_id, contract_id, revision, enabled, rate_percent, basis, days_count, currency, created_by)
            VALUES (@t, @d, @foreign, 1, true, 2, 'invoice_date', 10, 'VND', @a) RETURNING 1
            """, new { t = _tenantId, d = _otherDepartmentId, foreign, a = _adminId });
        var customer = await Scalar<Guid>("SELECT id FROM sim_customers WHERE mst=@m", new { m = DiscountCustomerMst });
        await Customers().BookInvoiceAsync(_tenantId, customer, Guid.NewGuid(), "HD-CK-FOREIGN", Today.AddDays(-2), Today.AddDays(30), 100_000_000m, foreign, null, default);
        Assert.Equal(PaymentDiscountReasons.ContractScopeMismatch,
            (await SingleProposalAsync(await DiscountReceiptAsync("PT-CK-FOREIGN", 98_000_000m))).ReasonCode);
    }

    [Fact]
    public async Task Currency_mismatch_is_not_eligible()
    {
        if (_cs is null) return;
        await DiscountScenarioAsync("USD", currency: "USD");
        Assert.Equal(PaymentDiscountReasons.CurrencyMismatch,
            (await SingleProposalAsync(await DiscountReceiptAsync("PT-CK-USD", 98_000_000m))).ReasonCode);
    }

    // ========================================================= PARTIAL / OVER

    [Fact]
    public async Task Partial_payment_gets_no_invented_discount()
    {
        if (_cs is null) return;
        var s = await DiscountScenarioAsync("PART");
        var e = await SingleProposalAsync(await DiscountReceiptAsync("PT-CK-PART", 50_000_000m));
        Assert.Equal((false, PaymentDiscountReasons.PartialPolicyUndefined, 0m, 48_000_000m),
            (e.Eligible, e.ReasonCode, e.CalculatedDiscount, e.RemainingCashRequired));

        // An invoice already partly settled: no discount on the remainder either.
        var first = await DiscountReceiptAsync("PT-CK-PART-1", 30_000_000m);
        await Settlement().PostAsync(_tenantId, _departmentId, first, _adminId, new PostCashRequest(null, [new AllocationInput(s.Invoice, 30_000_000m)], false, null), default);
        var rest = await SingleProposalAsync(await DiscountReceiptAsync("PT-CK-PART-2", 68_600_000m));
        Assert.Equal((false, PaymentDiscountReasons.PartialPolicyUndefined), (rest.Eligible, rest.ReasonCode));
    }

    [Fact]
    public async Task Overpayment_keeps_existing_unapplied_semantics()
    {
        if (_cs is null) return;
        var s = await DiscountScenarioAsync("OVER");
        var receipt = await DiscountReceiptAsync("PT-CK-OVER", 105_000_000m);
        var e = await SingleProposalAsync(receipt);
        Assert.Equal((true, 98_000_000m, 2_000_000m, 7_000_000m), (e.Eligible, e.CashToApply, e.CalculatedDiscount, e.ExcessCash));
        // Without discount the existing engine is unchanged: an overpayment needs the ADMIN's
        // explicit allocation (100M), and the 5M remainder stays unapplied.
        Assert.Equal("admin_selection_required", (await Assert.ThrowsAsync<CashSettlementException>(() =>
            Settlement().PostAsync(_tenantId, _departmentId, receipt, _adminId, Approve(), default))).Code);
        var posted = (await Settlement().PostAsync(_tenantId, _departmentId, receipt, _adminId,
            new PostCashRequest(null, [new AllocationInput(s.Invoice, 100_000_000m)], false, null), default))!;
        Assert.Equal((100_000_000m, 5_000_000m), (posted.Posting!.AppliedAmount, posted.Posting.UnappliedAmount));
        Assert.Equal((100_000_000m, "paid"), await ArInvoiceAsync(s.Invoice));
    }

    // ===================================================== CONFIRMATION SAFETY

    [Fact]
    public async Task Stale_or_browser_supplied_values_are_rejected_never_applied()
    {
        if (_cs is null) return;
        var s = await DiscountScenarioAsync("STALE");
        var receipt = await DiscountReceiptAsync("PT-CK-STALE", 98_000_000m);
        var e = await SingleProposalAsync(receipt);
        async Task<string> Code(ConfirmPaymentDiscountRequest r) => (await Assert.ThrowsAsync<CashSettlementException>(() =>
            Settlement().ConfirmPaymentDiscountAsync(_tenantId, _departmentId, receipt, _adminId, r, default))).Code;

        Assert.Equal(PaymentDiscountConflicts.EligibilityChanged, await Code(ConfirmFrom(e) with { DiscountAmount = 50_000_000m }));
        Assert.Equal(PaymentDiscountConflicts.EligibilityChanged, await Code(ConfirmFrom(e) with { DiscountAmount = null }));
        Assert.Equal(PaymentDiscountConflicts.TermsChanged, await Code(ConfirmFrom(e) with { TermRevision = 99 }));
        Assert.Equal(PaymentDiscountConflicts.TermsChanged, await Code(ConfirmFrom(e) with { TermId = Guid.NewGuid() }));
        Assert.Equal(PaymentDiscountConflicts.InvoiceBalanceChanged, await Code(ConfirmFrom(e) with { OpenAmount = 99_000_000m }));
        Assert.Equal(PaymentDiscountConflicts.InvoiceNotOpen, await Code(ConfirmFrom(e) with { InvoiceId = Guid.NewGuid() }));

        // Invoice balance changes after the proposal was shown.
        var other = await DiscountReceiptAsync("PT-CK-STALE-2", 1_000_000m);
        await Settlement().PostAsync(_tenantId, _departmentId, other, _adminId, new PostCashRequest(null, [new AllocationInput(s.Invoice, 1_000_000m)], false, null), default);
        Assert.Equal(PaymentDiscountConflicts.InvoiceBalanceChanged, await Code(ConfirmFrom(e)));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM cash_transactions WHERE voucher_id=@receipt", new { receipt }));
        Assert.True(await Scalar<int>("SELECT COUNT(*) FROM payment_discount_decisions WHERE voucher_id=@receipt AND outcome='rejected_stale'", new { receipt }) >= 5);
    }

    [Fact]
    public async Task Concurrent_confirmations_never_settle_anything()
    {
        if (_cs is null) return;
        var s = await DiscountScenarioAsync("CONC");
        var receipt = await DiscountReceiptAsync("PT-CK-CONC", 98_000_000m);
        var e = await SingleProposalAsync(receipt);
        var before = await SettlementSnapshotAsync();
        var codes = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            try { await Settlement().ConfirmPaymentDiscountAsync(_tenantId, _departmentId, receipt, _adminId, ConfirmFrom(e), default); return "applied"; }
            catch (CashSettlementException ex) { return ex.Code; }
        }));
        Assert.All(codes, c => Assert.Equal(PaymentDiscountAccountingPolicy.MissingCode, c));
        Assert.Equal(before, await SettlementSnapshotAsync());
        Assert.Equal((0m, "open"), await ArInvoiceAsync(s.Invoice));
        Assert.Equal(6, await Scalar<int>("SELECT COUNT(*) FROM payment_discount_decisions WHERE voucher_id=@receipt", new { receipt }));
    }

    [Fact]
    public async Task Extracted_or_injected_discount_values_have_no_authority()
    {
        if (_cs is null) return;
        await DiscountScenarioAsync("LLM");
        // An extraction (or prompt injection) claiming 50% / a GL account changes nothing.
        var receipt = await DiscountReceiptAsync("PT-CK-LLM", 98_000_000m, extraFieldsJson:
            """{"reason":"Thu tiền hàng","payment_discount_rate":50,"discount_amount":50000000,"discount_gl_account":"811","early_payment_discount_pct":50}""");
        var e = await SingleProposalAsync(receipt);
        Assert.Equal((2m, 2_000_000m), (e.RatePercent!.Value, e.CalculatedDiscount));
        Assert.Equal(PaymentDiscountAccountingPolicy.MissingCode, (await Assert.ThrowsAsync<CashSettlementException>(() =>
            Settlement().ConfirmPaymentDiscountAsync(_tenantId, _departmentId, receipt, _adminId, ConfirmFrom(e), default))).Code);
        Assert.Null(PaymentDiscountAccountingPolicy.ResolveDiscountDebitAccount());
    }

    [Fact]
    public async Task Receipt_date_comes_from_the_document_not_today()
    {
        if (_cs is null) return;
        await DiscountScenarioAsync("DATE", invoiceDaysAgo: 30);
        // Received within the window (document date), entered today: eligible.
        var e = await SingleProposalAsync(await DiscountReceiptAsync("PT-CK-DATE", 98_000_000m, date: Today.AddDays(-25)));
        Assert.Equal((true, Today.AddDays(-25)), (e.Eligible, e.ReceiptDate!.Value));
    }

    // ===================================================== BANK STATEMENT

    [Fact]
    public async Task Bank_statement_line_uses_the_same_calculator_and_the_review_gate()
    {
        if (_cs is null) return;
        await SeedCompanyBankAccountAsync();
        var s = await DiscountScenarioAsync("BANK");
        await BankAccounts().CreateAsync(_tenantId, _departmentId, "customer", s.Customer, _adminId,
            new BankAccountInput("VCB", "Vietcombank", "555666777", "CONG TY LIVE-TEST KHACH CK", null, null, null), default);
        var (v, rows) = await ImportStatementAsync("SK-CK-1", Csv(
            $"{Today:dd/MM/yyyy},FT8801,TT HD-CK-BANK,555666777,CONG TY LIVE-TEST KHACH CK,VCB,98000000,"));
        var ctx = (await Settle().GetStatementLineContextAsync(_tenantId, _departmentId, v, rows[0].Id, default))!;
        var e = Assert.Single(ctx.DiscountProposals).Evaluation;
        // Identical to the Phiếu thu evaluation for the same facts.
        var manual = await SingleProposalAsync(await DiscountReceiptAsync("PT-CK-BANK-CMP", 98_000_000m));
        Assert.Equal(manual with { CashAvailable = 0 }, e with { CashAvailable = 0 });

        await Scalar<int>("UPDATE vouchers SET document_review_status='awaiting_review' WHERE id=@v RETURNING 1", new { v });
        var before = await SettlementSnapshotAsync();
        Assert.Equal("document_review_required", (await Assert.ThrowsAsync<CashSettlementException>(() =>
            Settle().ConfirmStatementLinePaymentDiscountAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, ConfirmFrom(e), default))).Code);
        Assert.Equal(before, await SettlementSnapshotAsync());

        await Scalar<int>("UPDATE vouchers SET document_review_status='approved' WHERE id=@v RETURNING 1", new { v });
        Assert.Equal(PaymentDiscountAccountingPolicy.MissingCode, (await Assert.ThrowsAsync<CashSettlementException>(() =>
            Settle().ConfirmStatementLinePaymentDiscountAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, ConfirmFrom(e), default))).Code);
        Assert.Equal(before, await SettlementSnapshotAsync());
        Assert.Equal("bank_statement_line", await Scalar<string>(
            "SELECT source_type FROM payment_discount_decisions WHERE bank_transaction_id=@id", new { id = rows[0].Id }));

        // Normal path still posts the cash (no discount) once the gate is released.
        Assert.Equal("posted", (await Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId,
            new PostStatementLineRequest([new AllocationInput(s.Invoice, 98_000_000m)], false, true), default))!.Outcome);
        Assert.Equal((98_000_000m, "partial"), await ArInvoiceAsync(s.Invoice));
    }
}
