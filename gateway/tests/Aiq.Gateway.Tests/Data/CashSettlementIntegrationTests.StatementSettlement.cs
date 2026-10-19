using Aiq.Gateway.Banking;
using Aiq.Gateway.Payments;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Bank statement money_in line -> matched Customer -> open AR -> ADMIN ->
/// the SAME canonical cash settlement as Phiếu thu (source_type =
/// bank_statement_line). Real Postgres, shared settlement fixture.
/// </summary>
public sealed partial class CashSettlementIntegrationTests
{
    private IStatementSettlement Settle() => new CashSettlementRepository(Factory());

    private const string CompanyAccount = "0011002345678";

    private async Task<Guid> SeedCompanyBankAccountAsync(string gl = "1121") =>
        await Scalar<Guid>("""
            INSERT INTO sim_bank_accounts(tenant_id, account_number, bank_name, account_holder, gl_account_code, is_primary, current_balance)
            VALUES (@t, '0011 0023 45678', 'Vietcombank', 'Sáu Trực', @gl, true, 100000000) RETURNING id
            """, new { t = _tenantId, gl });

    private static string Csv(params string[] lines) =>
        "Số tài khoản: 0011 0023 45678\nNgày,Mã GD,Nội dung,TK đối ứng,Tên đối ứng,Ngân hàng đối ứng,Tiền vào,Tiền ra\n" + string.Join("\n", lines) + "\n";

    private async Task<(Guid Voucher, List<StatementTransactionRecord> Rows)> ImportStatementAsync(string doc, string csv)
    {
        var v = await StatementVoucherAsync(doc);
        await Statements().ImportAsync(_tenantId, _departmentId, v, BankStatementParser.ParseDelimitedText(csv), default);
        return (v, (await Statements().ListAsync(_tenantId, _departmentId, v, default))!.ToList());
    }

    private async Task<(Guid Customer, Guid Invoice)> VietTinWithInvoiceAsync(decimal outstanding = 39_600_000m, string doc = "DEMO-INV-VT-001")
    {
        var vt = await SeedCustomerAsync("DEMO-CUS-VIETTIN", "Công ty TNHH Việt Tín", "0307890123");
        var inv = await BookArInvoiceAsync(vt, doc, outstanding);
        await BankAccounts().CreateAsync(_tenantId, _departmentId, "customer", vt, _adminId,
            new BankAccountInput("VCB", "Vietcombank", "123456789", "CÔNG TY TNHH VIỆT TÍN", null, null, null), default);
        return (vt, inv);
    }

    private Task<decimal> ArAsync(Guid customerId) => Scalar<decimal>("SELECT current_ar FROM sim_customers WHERE id=@customerId", new { customerId });

    // CASE 1 + 2 ---------------------------------------------------------

    [Fact]
    public async Task Case1And2_PartialThenFull_ThroughCanonicalSettlement()
    {
        if (_cs is null) return;
        var bank = await SeedCompanyBankAccountAsync();
        var (vt, inv) = await VietTinWithInvoiceAsync();
        var (v, rows) = await ImportStatementAsync("SK-P1", Csv(
            "10/10/2026,FT9001,TT DEMO-INV-VT-001,123456789,CÔNG TY TNHH VIỆT TÍN,VCB,20000000,",
            "12/10/2026,FT9002,TT DEMO-INV-VT-001 LAN 2,123456789,CÔNG TY TNHH VIỆT TÍN,VCB,19600000,"));
        Assert.Equal(bank, rows[0].CompanyBankAccountId);

        var ctx = (await Settle().GetStatementLineContextAsync(_tenantId, _departmentId, v, rows[0].Id, default))!;
        Assert.Empty(ctx.Blockers);
        Assert.Equal((InvoiceMatchStatuses.AutoMatched, StatementAccountingStatuses.NotPosted), (ctx.InvoiceMatch.Status, ctx.AccountingStatus));
        var sug = Assert.Single(ctx.InvoiceMatch.Suggestion!.Allocations);
        Assert.Equal((inv, 39_600_000m, 20_000_000m), (sug.InvoiceId, sug.Outstanding, sug.ApplyAmount));
        Assert.Equal(("1121", "131", 20_000_000m), (ctx.ProposedJournal[0].DebitAccount, ctx.ProposedJournal[0].CreditAccount, ctx.ProposedJournal[0].Amount));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM cash_transactions")); // context never posts

        var arBefore = await ArAsync(vt);
        var p1 = (await Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, new PostStatementLineRequest(null, false, false), default))!;
        Assert.Equal("posted", p1.Outcome);
        Assert.Equal(("1121", "131", 20_000_000m), (p1.Journal[0].DebitAccount, p1.Journal[0].CreditAccount, p1.Journal[0].Amount));
        Assert.Equal(("bank_statement_line", rows[0].Id, "FT9001", bank), (p1.Posting!.SourceType, p1.Posting.SourceBankTransactionId, p1.Posting.BankReference, p1.Posting.BankAccountId));
        Assert.Equal(("invoice_reference", 20_000_000m), (Assert.Single(p1.Applications).MatchMethod, p1.Applications[0].AppliedAmount));
        Assert.Equal((20_000_000m, "partial"), await ArInvoiceAsync(inv));
        Assert.Equal(arBefore - 20_000_000m, await ArAsync(vt));
        Assert.Equal(120_000_000m, await Scalar<decimal>("SELECT current_balance FROM sim_bank_accounts WHERE id=@bank", new { bank }));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM voucher_journal_suggestions WHERE cash_transaction_id=@id AND source='posting' AND accepted_at IS NOT NULL", new { id = p1.Posting.Id }));
        var listed = (await Statements().ListAsync(_tenantId, _departmentId, v, default))![0];
        Assert.Equal(("POSTED", p1.Posting.Id), (listed.AccountingStatus, listed.CashTransactionId));

        // CASE 2 — exact remaining amount -> PAID.
        var p2 = (await Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[1].Id, _adminId, new PostStatementLineRequest(null, false, false), default))!;
        Assert.Equal("posted", p2.Outcome);
        Assert.Equal((39_600_000m, "paid"), await ArInvoiceAsync(inv));
        Assert.Equal(arBefore - 39_600_000m, await ArAsync(vt));
        Assert.Equal(2, await Scalar<int>("SELECT COUNT(*) FROM cash_transactions WHERE voucher_id=@v AND status='posted'", new { v }));
    }

    // CASE 3 ---------------------------------------------------------------

    [Fact]
    public async Task Case3_OnePayment_TwoInvoices_OnePosting_TwoApplications()
    {
        if (_cs is null) return;
        await SeedCompanyBankAccountAsync();
        var (vt, a) = await VietTinWithInvoiceAsync(20_000_000m, "INV-A-001");
        var b = await BookArInvoiceAsync(vt, "INV-B-001", 30_000_000m);
        var (v, rows) = await ImportStatementAsync("SK-M", Csv("10/10/2026,FT9101,VIET TIN THANH TOAN,123456789,CÔNG TY TNHH VIỆT TÍN,VCB,50000000,"));

        var ctx = (await Settle().GetStatementLineContextAsync(_tenantId, _departmentId, v, rows[0].Id, default))!;
        Assert.Equal(InvoiceMatchStatuses.NeedsReview, ctx.InvoiceMatch.Status);
        var ex = await Assert.ThrowsAsync<CashSettlementException>(() => Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, new PostStatementLineRequest(null, false, false), default));
        Assert.Equal("admin_selection_required", ex.Code);
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM cash_transactions"));

        var posted = (await Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId,
            new PostStatementLineRequest(new[] { new AllocationInput(a, 20_000_000m), new AllocationInput(b, 30_000_000m) }, false, false), default))!;
        Assert.Equal(2, posted.Applications.Count);
        Assert.All(posted.Applications, x => Assert.Equal("admin_selected", x.MatchMethod));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM cash_transactions"));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM voucher_journal_suggestions WHERE source='posting'"));
        Assert.Equal((20_000_000m, "paid"), await ArInvoiceAsync(a));
        Assert.Equal((30_000_000m, "paid"), await ArInvoiceAsync(b));
        Assert.Equal(0m, posted.Posting!.UnappliedAmount);
    }

    // CASE 4 ---------------------------------------------------------------

    [Fact]
    public async Task Case4_ReferenceToNonOpenInvoice_NeverAutoAppliedElsewhere()
    {
        if (_cs is null) return;
        await SeedCompanyBankAccountAsync();
        var (vt, inv) = await VietTinWithInvoiceAsync(20_000_000m, "DEMO-INV-VT-777"); // equals the amount
        var paid = await BookArInvoiceAsync(vt, "DEMO-INV-VT-001", 5_000_000m);
        await Scalar<int>("UPDATE sim_ar_ledger SET paid_amount=debit, status='paid' WHERE id=@paid RETURNING 1", new { paid });
        var (v, rows) = await ImportStatementAsync("SK-U", Csv(
            "10/10/2026,FT9201,TT INV-DOES-NOT-EXIST,123456789,CÔNG TY TNHH VIỆT TÍN,VCB,20000000,",
            "10/10/2026,FT9202,TT DEMO-INV-VT-001,123456789,CÔNG TY TNHH VIỆT TÍN,VCB,20000000,"));

        var c1 = (await Settle().GetStatementLineContextAsync(_tenantId, _departmentId, v, rows[0].Id, default))!;
        Assert.Equal(InvoiceMatchStatuses.NeedsReview, c1.InvoiceMatch.Status);
        Assert.Contains("INV-DOES-NOT-EXIST", c1.InvoiceMatch.ExplicitReferences);
        Assert.Equal("Chứng từ tham chiếu INV-DOES-NOT-EXIST không còn công nợ phù hợp.", c1.InvoiceMatch.Message);
        Assert.Equal("not_booked", Assert.Single(c1.InvoiceMatch.ReferenceProblems).Problem);
        Assert.Equal("admin_selection_required", (await Assert.ThrowsAsync<CashSettlementException>(() =>
            Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, new PostStatementLineRequest(null, false, false), default))).Code);

        var c2 = (await Settle().GetStatementLineContextAsync(_tenantId, _departmentId, v, rows[1].Id, default))!;
        Assert.Equal((InvoiceMatchStatuses.NeedsReview, "not_open", "PAID"), (c2.InvoiceMatch.Status, c2.InvoiceMatch.ReferenceProblems[0].Problem, c2.InvoiceMatch.ReferenceProblems[0].Status));
        Assert.Equal((0m, "open"), await ArInvoiceAsync(inv));

        // ADMIN may still choose explicitly; the document reference is kept for audit.
        var posted = (await Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId,
            new PostStatementLineRequest(new[] { new AllocationInput(inv, 20_000_000m) }, false, false), default))!;
        Assert.Equal(("admin_selected", "INV-DOES-NOT-EXIST"), (posted.Applications[0].MatchMethod, posted.Posting!.DocumentReference));
        var evidence = await Scalar<string>("SELECT settlement_evidence::text FROM sim_bank_transactions WHERE id=@id", new { id = rows[0].Id });
        Assert.Contains("auto_suggestion", evidence);
        Assert.Contains("document_reference_not_open", evidence);
    }

    [Fact]
    public async Task Case4b_ReferenceToAnotherCustomersInvoice_NeedsReview()
    {
        if (_cs is null) return;
        await SeedCompanyBankAccountAsync();
        var (vt, _) = await VietTinWithInvoiceAsync(20_000_000m, "DEMO-INV-VT-777");
        var ml = await SeedCustomerAsync("DEMO-CUS-MINHLONG", "Công ty TNHH Minh Long", "0315550001");
        await BookArInvoiceAsync(ml, "DEMO-INV-ML-001", 20_000_000m);
        // Admin confirmed Việt Tín for a transfer that names Minh Long's invoice.
        var (v, rows) = await ImportStatementAsync("SK-X", Csv("10/10/2026,FT9301,TT DEMO-INV-ML-001,999000111,ANH B,TCB,20000000,"));
        await Statements().ConfirmMatchAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, vt, false, default);
        var ctx = (await Settle().GetStatementLineContextAsync(_tenantId, _departmentId, v, rows[0].Id, default))!;
        Assert.Equal(InvoiceMatchStatuses.NeedsReview, ctx.InvoiceMatch.Status);
        Assert.Equal(("other_customer", "Công ty TNHH Minh Long"), (ctx.InvoiceMatch.ReferenceProblems[0].Problem, ctx.InvoiceMatch.ReferenceProblems[0].OwnerName));
    }

    // CASE 5 + money_out ------------------------------------------------------

    [Fact]
    public async Task Case5_UnmatchedCustomer_AndMoneyOut_AreBlocked()
    {
        if (_cs is null) return;
        await SeedCompanyBankAccountAsync();
        await VietTinWithInvoiceAsync();
        var before = await AccountingSnapshotAsync();
        var (v, rows) = await ImportStatementAsync("SK-B", Csv(
            "10/10/2026,FT9401,THANH TOAN,123888999,NGUYEN VAN A,TCB,15000000,",
            "10/10/2026,FT9402,CHI NCC,987654321,KIM LONG,ACB,,5500000"));
        var c1 = (await Settle().GetStatementLineContextAsync(_tenantId, _departmentId, v, rows[0].Id, default))!;
        Assert.Contains("customer_not_matched", c1.Blockers);
        Assert.Equal("customer_not_matched", (await Assert.ThrowsAsync<CashSettlementException>(() =>
            Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, new PostStatementLineRequest(null, true, false), default))).Code);
        var c2 = (await Settle().GetStatementLineContextAsync(_tenantId, _departmentId, v, rows[1].Id, default))!;
        Assert.Contains("not_money_in", c2.Blockers);
        Assert.Equal("not_money_in", (await Assert.ThrowsAsync<CashSettlementException>(() =>
            Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[1].Id, _adminId, new PostStatementLineRequest(null, true, false), default))).Code);
        Assert.Equal(before, await AccountingSnapshotAsync());
    }

    // CASE 6 ---------------------------------------------------------------

    [Fact]
    public async Task Case6_CompanyBankAccountNotLinked_Blocks_UntilAdminLinksExistingAccount()
    {
        if (_cs is null) return;
        await VietTinWithInvoiceAsync();
        var (v, rows) = await ImportStatementAsync("SK-C6", Csv("10/10/2026,FT9501,TT DEMO-INV-VT-001,123456789,CÔNG TY TNHH VIỆT TÍN,VCB,20000000,"));
        Assert.Null(rows[0].CompanyBankAccountId);
        var ctx = (await Settle().GetStatementLineContextAsync(_tenantId, _departmentId, v, rows[0].Id, default))!;
        Assert.Contains("company_bank_account_not_linked", ctx.Blockers);
        var ex = await Assert.ThrowsAsync<CashSettlementException>(() =>
            Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, new PostStatementLineRequest(null, false, false), default));
        Assert.Equal(("company_bank_account_not_linked", "Chưa liên kết tài khoản ngân hàng của công ty."), (ex.Code, ex.Message));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM cash_transactions"));

        // Only an EXISTING company account of this tenant can be linked.
        await Assert.ThrowsAsync<CashSettlementException>(() => Settle().LinkCompanyBankAccountAsync(_tenantId, _departmentId, v, Guid.NewGuid(), _adminId, default));
        Assert.Null(await Settle().LinkCompanyBankAccountAsync(_tenantId, _otherDepartmentId, v, Guid.NewGuid(), _adminId, default));
        var bank = await Scalar<Guid>("""
            INSERT INTO sim_bank_accounts(tenant_id, account_number, bank_name, account_holder, gl_account_code, is_primary)
            VALUES (@t, '999888777', 'ACB', 'Sáu Trực', '1121', false) RETURNING id
            """, new { t = _tenantId });
        Assert.Equal(1, await Settle().LinkCompanyBankAccountAsync(_tenantId, _departmentId, v, bank, _adminId, default));
        Assert.DoesNotContain("company_bank_account_not_linked",
            (await Settle().GetStatementLineContextAsync(_tenantId, _departmentId, v, rows[0].Id, default))!.Blockers);
    }

    // CASE 7 ---------------------------------------------------------------

    [Fact]
    public async Task Case7_ExistingPhieuThu_ProbableDuplicate_NeverAutoPosted_ReconcileLinksInstead()
    {
        if (_cs is null) return;
        await SeedCompanyBankAccountAsync();
        var (vt, inv) = await VietTinWithInvoiceAsync();
        // The Phiếu thu for the same transfer, posted the existing way.
        var pt = await ReceiptVoucherAsync("PT-DUP-1", 20_000_000m, "Công ty TNHH Việt Tín", "0307890123", "DEMO-INV-VT-001");
        var receipt = (await Settlement().PostAsync(_tenantId, _departmentId, pt, _adminId, Approve(), default))!;
        var snap = await AccountingSnapshotAsync();

        var date = Today.ToString("dd/MM/yyyy");
        var (v, rows) = await ImportStatementAsync("SK-D", Csv($"{date},FT9601,TT DEMO-INV-VT-001,123456789,CÔNG TY TNHH VIỆT TÍN,VCB,20000000,"));
        var ctx = (await Settle().GetStatementLineContextAsync(_tenantId, _departmentId, v, rows[0].Id, default))!;
        Assert.Contains("possible_duplicate", ctx.Blockers);
        var dup = Assert.Single(ctx.Duplicates);
        Assert.Equal((receipt.Posting!.Id, "probable", "voucher"), (dup.CashTransactionId, dup.Kind, dup.SourceType));
        Assert.Equal("possible_duplicate", (await Assert.ThrowsAsync<CashSettlementException>(() =>
            Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, new PostStatementLineRequest(null, false, false), default))).Code);
        Assert.Equal(snap, await AccountingSnapshotAsync());

        // ADMIN reconciles: link, no second accounting effect.
        var reconciled = (await Settle().ReconcileStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, receipt.Posting.Id, default))!;
        Assert.Equal((StatementAccountingStatuses.Reconciled, receipt.Posting.Id), (reconciled.AccountingStatus, reconciled.ReconciledWith!.Id));
        Assert.Equal(snap, await AccountingSnapshotAsync());
        Assert.Equal("already_reconciled", (await Assert.ThrowsAsync<CashSettlementException>(() =>
            Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, new PostStatementLineRequest(null, false, true), default))).Code);
        Assert.Equal((20_000_000m, "partial"), await ArInvoiceAsync(inv));
        Assert.Equal("RECONCILED", (await Statements().ListAsync(_tenantId, _departmentId, v, default))![0].AccountingStatus);
        // The same existing posting cannot explain a second line.
        var (v2, rows2) = await ImportStatementAsync("SK-D2", Csv($"{date},FT9602,VIET TIN CK,123456789,CÔNG TY TNHH VIỆT TÍN,VCB,20000000,"));
        Assert.Empty((await Settle().GetStatementLineContextAsync(_tenantId, _departmentId, v2, rows2[0].Id, default))!.Duplicates);
    }

    [Fact]
    public async Task Case7b_SameBankReference_IsExactDuplicate_AndAdminCanOverrideOnlyProbable()
    {
        if (_cs is null) return;
        await SeedCompanyBankAccountAsync();
        var (vt, inv) = await VietTinWithInvoiceAsync();
        var pt = await ReceiptVoucherAsync("PT-DUP-2", 5_000_000m, "Công ty TNHH Việt Tín", "0307890123", "DEMO-INV-VT-001");
        var receipt = (await Settlement().PostAsync(_tenantId, _departmentId, pt, _adminId, Approve(), default))!;
        await Scalar<int>("UPDATE cash_transactions SET bank_reference='FT9701' WHERE id=@id RETURNING 1", new { id = receipt.Posting!.Id });
        var (v, rows) = await ImportStatementAsync("SK-E7", Csv("01/01/2027,FT9701,CK,123456789,CÔNG TY TNHH VIỆT TÍN,VCB,7000000,"));
        var ctx = (await Settle().GetStatementLineContextAsync(_tenantId, _departmentId, v, rows[0].Id, default))!;
        Assert.Equal("exact", Assert.Single(ctx.Duplicates).Kind);
        Assert.Contains("already_recorded_elsewhere", ctx.Blockers);
        // ConfirmNotDuplicate does NOT override a deterministic identity.
        Assert.Equal("already_recorded_elsewhere", (await Assert.ThrowsAsync<CashSettlementException>(() =>
            Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, new PostStatementLineRequest(null, true, true), default))).Code);
    }

    [Fact]
    public async Task Case7c_ProbableDuplicate_AdminConfirmsNotDuplicate_PostsAndAudits()
    {
        if (_cs is null) return;
        await SeedCompanyBankAccountAsync();
        var (vt, inv) = await VietTinWithInvoiceAsync();
        var pt = await ReceiptVoucherAsync("PT-DUP-3", 10_000_000m, "Công ty TNHH Việt Tín", "0307890123", "DEMO-INV-VT-001");
        await Settlement().PostAsync(_tenantId, _departmentId, pt, _adminId, Approve(), default);
        var (v, rows) = await ImportStatementAsync("SK-E8", Csv($"{Today:dd/MM/yyyy},FT9801,TT DEMO-INV-VT-001 DOT 2,123456789,CÔNG TY TNHH VIỆT TÍN,VCB,10000000,"));
        var posted = (await Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, new PostStatementLineRequest(null, false, true), default))!;
        Assert.Equal("posted", posted.Outcome);
        Assert.Equal((20_000_000m, "partial"), await ArInvoiceAsync(inv));
        Assert.Contains("not_duplicate_confirmed", await Scalar<string>("SELECT settlement_evidence::text FROM sim_bank_transactions WHERE id=@id", new { id = rows[0].Id }));
    }

    // CASE 8 ---------------------------------------------------------------

    [Fact]
    public async Task Case8_ConcurrentPosts_OnePosting_NoDuplicateEffects()
    {
        if (_cs is null) return;
        await SeedCompanyBankAccountAsync();
        var (vt, inv) = await VietTinWithInvoiceAsync();
        var (v, rows) = await ImportStatementAsync("SK-CC", Csv("10/10/2026,FT9901,TT DEMO-INV-VT-001,123456789,CÔNG TY TNHH VIỆT TÍN,VCB,20000000,"));
        var arBefore = await ArAsync(vt);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, new PostStatementLineRequest(null, false, false), default)));
        Assert.Equal(1, results.Count(r => r!.Outcome == "posted"));
        Assert.Equal(5, results.Count(r => r!.Outcome == "already_posted"));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM cash_transactions"));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM payment_applications"));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM voucher_journal_suggestions WHERE source='posting'"));
        Assert.Equal(arBefore - 20_000_000m, await ArAsync(vt));
        Assert.Equal((20_000_000m, "partial"), await ArInvoiceAsync(inv));
    }

    // CASE 9 + reallocate ------------------------------------------------------

    [Fact]
    public async Task Case9_Unapply_ThenReallocate_SameCustomerOnly()
    {
        if (_cs is null) return;
        await SeedCompanyBankAccountAsync();
        var (vt, inv) = await VietTinWithInvoiceAsync();
        var other = await BookArInvoiceAsync(vt, "DEMO-INV-VT-002", 42_000_000m);
        var ml = await SeedCustomerAsync("DEMO-CUS-MINHLONG", "Công ty TNHH Minh Long", "0315550001");
        var mlInv = await BookArInvoiceAsync(ml, "DEMO-INV-ML-001", 60_000_000m);
        var (v, rows) = await ImportStatementAsync("SK-U9", Csv("10/10/2026,FT9911,TT DEMO-INV-VT-001,123456789,CÔNG TY TNHH VIỆT TÍN,VCB,20000000,"));
        var arBefore = await ArAsync(vt);
        var posted = (await Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, new PostStatementLineRequest(null, false, false), default))!;

        var un = (await Settle().UnapplyStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, null, "sai hoá đơn", default))!;
        Assert.Equal("unapplied", un.Outcome);
        Assert.Equal((0m, "open"), await ArInvoiceAsync(inv));
        Assert.Equal(arBefore, await ArAsync(vt));
        Assert.Equal(("posted", 20_000_000m), (un.Posting!.Status, un.Posting.UnappliedAmount));
        var ctx = (await Settle().GetStatementLineContextAsync(_tenantId, _departmentId, v, rows[0].Id, default))!;
        Assert.Equal((StatementAccountingStatuses.Posted, "UNAPPLIED", posted.Posting!.Id), (ctx.AccountingStatus, ctx.AllocationStatus, ctx.Posting!.Id));
        Assert.Equal("reversed", Assert.Single(ctx.Applications).Status);

        Assert.Equal("invoice_not_open_for_counterparty", (await Assert.ThrowsAsync<CashSettlementException>(() =>
            Settle().AllocateStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, new[] { new AllocationInput(mlInv, 20_000_000m) }, default))).Code);
        var re = (await Settle().AllocateStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, new[] { new AllocationInput(other, 20_000_000m) }, default))!;
        Assert.Equal("allocated", re.Outcome);
        Assert.Equal((20_000_000m, "partial"), await ArInvoiceAsync(other));
        Assert.Equal(arBefore - 20_000_000m, await ArAsync(vt));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM cash_transactions"));
        var history = await Scalar<string>("SELECT settlement_evidence::text FROM sim_bank_transactions WHERE id=@id", new { id = rows[0].Id });
        Assert.Contains("\"unapplied\"", history);
        Assert.Contains("\"allocated\"", history);
    }

    // CASE 10 --------------------------------------------------------------

    [Fact]
    public async Task Case10_Reverse_RestoresAr_KeepsLine_AllowsCorrectedRepost()
    {
        if (_cs is null) return;
        var bank = await SeedCompanyBankAccountAsync();
        var (vt, inv) = await VietTinWithInvoiceAsync();
        var (v, rows) = await ImportStatementAsync("SK-R", Csv("10/10/2026,FT9921,TT DEMO-INV-VT-001,123456789,CÔNG TY TNHH VIỆT TÍN,VCB,20000000,"));
        var arBefore = await ArAsync(vt);
        await Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, new PostStatementLineRequest(null, false, false), default);

        // Re-assigning the customer of an accounted line is refused.
        Assert.Equal("line_already_accounted", (await Assert.ThrowsAsync<BankAccountException>(() =>
            Statements().ConfirmMatchAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, vt, false, default))).Code);

        var rev = (await Settle().ReverseStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, "nhập nhầm", default))!;
        Assert.Equal(("reversed", "131", "1121"), (rev.Outcome, rev.Journal[0].DebitAccount, rev.Journal[0].CreditAccount));
        Assert.Equal((0m, "open"), await ArInvoiceAsync(inv));
        Assert.Equal(arBefore, await ArAsync(vt));
        Assert.Equal(100_000_000m, await Scalar<decimal>("SELECT current_balance FROM sim_bank_accounts WHERE id=@bank", new { bank }));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM sim_bank_transactions WHERE id=@id", new { id = rows[0].Id }));
        var listed = (await Statements().ListAsync(_tenantId, _departmentId, v, default))![0];
        Assert.Equal("REVERSED", listed.AccountingStatus);
        // Line postings/reversals never touch the statement voucher's own status.
        Assert.Equal("extracted", await Scalar<string>("SELECT status FROM vouchers WHERE id=@v", new { v }));

        var repost = (await Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, new PostStatementLineRequest(null, false, false), default))!;
        Assert.Equal("posted", repost.Outcome);
        Assert.Equal(2, await Scalar<int>("SELECT COUNT(*) FROM cash_transactions WHERE source_bank_transaction_id=@id", new { id = rows[0].Id }));
        Assert.Equal((20_000_000m, "partial"), await ArInvoiceAsync(inv));
    }

    // Scope + voucher regression -------------------------------------------

    [Fact]
    public async Task StatementSettlement_CrossScope_NotFound_AndVoucherPostingsUnaffected()
    {
        if (_cs is null) return;
        await SeedCompanyBankAccountAsync();
        var (vt, inv) = await VietTinWithInvoiceAsync();
        var (v, rows) = await ImportStatementAsync("SK-S", Csv("10/10/2026,FT9931,TT DEMO-INV-VT-001,123456789,CÔNG TY TNHH VIỆT TÍN,VCB,20000000,"));
        Assert.Null(await Settle().GetStatementLineContextAsync(_tenantId, _otherDepartmentId, v, rows[0].Id, default));
        Assert.Null(await Settle().PostStatementLineAsync(_otherTenantId, _otherTenantDepartmentId, v, rows[0].Id, _adminId, new PostStatementLineRequest(null, false, false), default));
        Assert.Null(await Settle().ReverseStatementLineAsync(_tenantId, _otherDepartmentId, v, rows[0].Id, _adminId, null, default));

        // Two lines of ONE statement voucher can both be posted (per-line uniqueness), and a
        // Phiếu thu for the same customer still posts exactly as before.
        await Settle().PostStatementLineAsync(_tenantId, _departmentId, v, rows[0].Id, _adminId, new PostStatementLineRequest(null, false, false), default);
        var pt = await ReceiptVoucherAsync("PT-REG", 1_000_000m, "Công ty TNHH Việt Tín", "0307890123", "DEMO-INV-VT-001");
        var receipt = (await Settlement().PostAsync(_tenantId, _departmentId, pt, _adminId, Approve(), default))!;
        Assert.Equal(("posted", "voucher", (Guid?)null), (receipt.Outcome, receipt.Posting!.SourceType, receipt.Posting.SourceBankTransactionId));
        Assert.Equal("already_posted", (await Settlement().PostAsync(_tenantId, _departmentId, pt, _adminId, Approve(), default))!.Outcome);
        Assert.Equal((21_000_000m, "partial"), await ArInvoiceAsync(inv));
        Assert.Null((await Settlement().GetContextAsync(_tenantId, _departmentId, v, default)));  // statement voucher is not a Phiếu thu
    }
}
