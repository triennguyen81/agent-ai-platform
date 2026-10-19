using Aiq.Gateway.Data;
using Aiq.Gateway.Payments;
using Aiq.Gateway.Vouchers;
using Dapper;
using Npgsql;
using State = Aiq.Gateway.Tests.Data.ExpenseClaimTestData.State;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Expense claim ACTUAL payment ("Duyệt & chi tiền") through the canonical cash
/// infrastructure, on the real schema. Since migration 059 a payable claim is
/// first RECOGNIZED at stage 2 (Cr 334 = payable, employee advance/YTD updated
/// once); payment only settles the 334 liability (Dr 334 / Cr 111x|112x).
/// Default claim: 5,000,000 expense − 800,000 advance = 4,200,000 payable.
/// </summary>
public sealed class ExpenseClaimPaymentIntegrationTests(TaxVerificationDatabase fx) : IClassFixture<TaxVerificationDatabase>
{
    static ExpenseClaimPaymentIntegrationTests()
    {
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyTypeHandler());
    }

    private readonly ExpenseClaimTestData d = new(fx);
    private bool Skip => fx.ConnectionString is null;

    /// <summary>Recognized, unpaid: advance cleared (800k -> 0), YTD 1M + 5M.</summary>
    private static readonly State Recognized = new(0, 3, 0, "unpaid", "accounting_reviewed", "recognized", 0m, 6_000_000m); // 642/141 0.8M, 642/334 2.2M, 642/334 2M
    private static readonly State Paid = new(1, 3, 1, "paid", "confirmed", "recognized", 0m, 6_000_000m);

    private Task<Guid> BankAsync(Guid? tenant = null, string gl = "1121", decimal balance = 100_000_000m) =>
        d.Scalar<Guid>("""
            INSERT INTO sim_bank_accounts(tenant_id, account_number, bank_name, account_holder, gl_account_code, current_balance)
            VALUES (@t, @n, 'Vietcombank', 'Sáu Trực', @gl, @balance) RETURNING id
            """, new { t = tenant ?? fx.Tenant, n = "00" + Random.Shared.NextInt64(10_000_000_000, 99_999_999_999), gl, balance });

    private Task<ExpenseClaimPaymentResult?> PayAsync(ExpenseClaimTestData.Claim c, string method, Guid? bank = null, Guid? actor = null, Guid? dept = null, Guid? tenant = null) =>
        d.Payments().PayExpenseClaimAsync(tenant ?? fx.Tenant, dept ?? fx.Dept, c.Voucher, actor ?? fx.Admin3, new ExpenseClaimPayRequest(method, bank), default);

    private static async Task<string> Blocked(Func<Task> act) => (await Assert.ThrowsAsync<CashSettlementException>(act)).Code;

    [Fact]
    public async Task Case1_RecognizedClaim_BankPayment_OneCashTx_OneJournal_Paid_EmployeeNotTouchedAgain()
    {
        if (Skip) return;
        var c = await d.CreateAsync();
        var bank = await BankAsync();
        Assert.Equal(Recognized, await d.StateAsync(c));
        var before = (await d.Payments().GetExpenseClaimPaymentAsync(fx.Tenant, fx.Dept, c.Voucher, fx.Admin3, default))!;
        Assert.Empty(before.Blockers);

        var r = (await PayAsync(c, "bank", bank))!;
        Assert.Equal("paid", r.Outcome);
        var p = r.Context.Payment!;
        Assert.Equal((4_200_000m, "bank", "1121", "334", bank, "posted"), (p.Amount, p.PaymentMethod, p.CashAccount, p.CounterAccount, p.BankAccountId, p.Status));
        var j = Assert.Single(p.Journal);
        Assert.Equal(("334", "1121", 4_200_000m), (j.DebitAccount, j.CreditAccount, j.Amount));
        Assert.Equal(Paid, await d.StateAsync(c));
        var row = await d.Scalar<string>("""
            SELECT c.source_type || '|' || c.counterparty_type || '|' || c.employee_id || '|' || r.paid_by || '|' || (r.paid_cash_transaction_id = c.id)
            FROM expense_claim_results r JOIN cash_transactions c ON c.source_expense_claim_id = r.id WHERE r.id=@id
            """, new { id = c.ClaimId });
        Assert.Equal($"expense_claim|employee|{c.Employee}|{fx.Admin3}|true", row);
        Assert.Equal(95_800_000m, await d.Scalar<decimal>("SELECT current_balance FROM sim_bank_accounts WHERE id=@bank", new { bank }));
    }

    [Fact]
    public async Task Case2_CashPayment_Uses1111()
    {
        if (Skip) return;
        var c = await d.CreateAsync();
        var r = (await PayAsync(c, "cash"))!;
        Assert.Equal(("334", "1111"), (r.Context.Payment!.Journal[0].DebitAccount, r.Context.Payment.Journal[0].CreditAccount));
        Assert.Equal(Paid, await d.StateAsync(c));
    }

    [Fact]
    public async Task Case3_TaxNoLongerVerified_Blocked_NoMutation()
    {
        if (Skip) return;
        var c = await d.CreateAsync();
        await d.Tax().RejectAsync(fx.Tenant, fx.Dept, c.Voucher, fx.Admin, "user", "admin",
            new TaxRejectRequest("Tra cứu lại: hoá đơn đã bị huỷ", false, TaxVerificationSources.TctPortal, null), default);
        Assert.Equal("tax_verification_required", await Blocked(() => PayAsync(c, "cash")));
        Assert.Equal(Recognized, await d.StateAsync(c));
    }

    [Theory]
    [InlineData("proposed")]
    [InlineData("dept_reviewed")]
    public async Task Case4_ApprovalIncomplete_Blocked_NoMutation(string stage)
    {
        if (Skip) return;
        var c = await d.CreateAsync(stage: stage);
        Assert.Equal("approval_incomplete", await Blocked(() => PayAsync(c, "cash")));
        Assert.Equal((0, stage, "unrecognized"), ((await d.StateAsync(c)).Cash, (await d.StateAsync(c)).Review, (await d.StateAsync(c)).Recognition));
    }

    [Theory]
    [InlineData("REJECTED", "claim_not_payable")]
    [InlineData("RETURN_FOR_DOCUMENTS", "claim_not_payable")]
    [InlineData("WAITING_EXCEPTION_APPROVAL", "exception_approval_pending")]
    public async Task Case4b_NonPayableOutcomes_Blocked(string outcome, string code)
    {
        if (Skip) return;
        var c = await d.CreateAsync(stage: "dept_reviewed", outcome: outcome);
        Assert.Equal(code, await Blocked(() => PayAsync(c, "cash")));
        Assert.Equal(0, (await d.StateAsync(c)).Cash);
    }

    [Fact]
    public async Task Case5_ClaimantAdmin_AndEarlierStageReviewers_MayPay()
    {
        if (Skip) return;
        // ADMIN Department Authority Invariant: the payer may be the claimant or an earlier reviewer.
        var own = await d.CreateAsync(claimant: fx.Admin3);
        Assert.Equal("paid", (await PayAsync(own, "cash", actor: fx.Admin3))!.Outcome);
        var c = await d.CreateAsync();
        Assert.Equal("paid", (await PayAsync(c, "cash", actor: fx.Admin))!.Outcome);    // stage-1 reviewer
        var c2 = await d.CreateAsync();
        Assert.Equal("paid", (await PayAsync(c2, "cash", actor: fx.Admin2))!.Outcome);  // accounting reviewer
        Assert.Equal(Paid, await d.StateAsync(c));
    }

    [Fact]
    public async Task Case6_OtherDepartmentOrTenant_NotFound_NoMutation()
    {
        if (Skip) return;
        var c = await d.CreateAsync();
        Assert.Null(await PayAsync(c, "cash", dept: fx.OtherDept));
        Assert.Null(await PayAsync(c, "cash", tenant: Guid.NewGuid()));
        Assert.Null(await d.Payments().GetExpenseClaimPaymentAsync(fx.Tenant, fx.OtherDept, c.Voucher, fx.Admin3, default));
        Assert.Equal(Recognized, await d.StateAsync(c));
    }

    [Fact]
    public async Task Case7_DoubleSubmit_OnePayment()
    {
        if (Skip) return;
        var c = await d.CreateAsync();
        var bank = await BankAsync();
        Assert.Equal("paid", (await PayAsync(c, "bank", bank))!.Outcome);
        Assert.Equal("already_paid", (await PayAsync(c, "bank", bank))!.Outcome);
        Assert.Equal(Paid, await d.StateAsync(c));
        Assert.Equal(95_800_000m, await d.Scalar<decimal>("SELECT current_balance FROM sim_bank_accounts WHERE id=@bank", new { bank }));
    }

    [Fact]
    public async Task Case8_ConcurrentPay_ExactlyOneSucceeds()
    {
        if (Skip) return;
        var c = await d.CreateAsync();
        var bank = await BankAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => PayAsync(c, "bank", bank)));
        Assert.Equal(1, results.Count(r => r!.Outcome == "paid"));
        Assert.Equal(4, results.Count(r => r!.Outcome == "already_paid"));
        Assert.Equal(Paid, await d.StateAsync(c));
        Assert.Equal(95_800_000m, await d.Scalar<decimal>("SELECT current_balance FROM sim_bank_accounts WHERE id=@bank", new { bank }));
    }

    [Fact]
    public async Task Case9_InvalidPaymentSource_Blocked()
    {
        if (Skip) return;
        var c = await d.CreateAsync();
        var foreign = await d.Scalar<Guid>("INSERT INTO tenants(id, code, name) VALUES (gen_random_uuid(), @code, 'Other') RETURNING id",
            new { code = "O" + Guid.NewGuid().ToString("N")[..8] });
        var foreignBank = await BankAsync(foreign);
        var notBankGl = await BankAsync(gl: "1111");
        Assert.Equal("company_bank_account_invalid", await Blocked(() => PayAsync(c, "bank", foreignBank)));
        Assert.Equal("company_bank_account_invalid", await Blocked(() => PayAsync(c, "bank", Guid.NewGuid())));
        Assert.Equal("company_bank_account_invalid", await Blocked(() => PayAsync(c, "bank", notBankGl)));
        Assert.Equal("company_bank_account_required", await Blocked(() => PayAsync(c, "bank", null)));
        Assert.Equal("payment_source_required", await Blocked(() => PayAsync(c, "642")));
        Assert.Equal(Recognized, await d.StateAsync(c));
    }

    [Fact]
    public async Task Case10_LlmSayingPaid_ChangesNothing()
    {
        if (Skip) return;
        var c = await d.CreateAsync(stage: "dept_reviewed");
        await d.Scalar<int>("""
            UPDATE vouchers SET extracted_fields = extracted_fields || '{"payment_status":"paid","paid":true,"note":"Expense claim paid successfully"}'::jsonb
            WHERE id=@id RETURNING 1
            """, new { id = c.Voucher });
        await new VoucherRepository(d.Factory()).SaveJournalSuggestionsAsync(fx.Tenant, fx.Dept, c.Voucher,
            new SaveJournalSuggestionsRequest(new[] { new SaveJournalSuggestionDto(1, "642", "334", 5_000_000m, "LLM: đã chi", 0.99m) }, true), default);
        var ctx = (await d.Payments().GetExpenseClaimPaymentAsync(fx.Tenant, fx.Dept, c.Voucher, fx.Admin3, default))!;
        Assert.Equal(("unpaid", (ExpenseClaimPaymentPosting?)null), (ctx.PaymentStatus, ctx.Payment));
        Assert.Equal(new State(0, 0, 0, "unpaid", "dept_reviewed", "unrecognized", 800_000m, 1_000_000m), await d.StateAsync(c));
    }

    [Fact]
    public async Task Case11_PaymentJournalFailure_RollsBackEverything()
    {
        if (Skip) return;
        var c = await d.CreateAsync();
        var bank = await BankAsync();
        var fn = $"fail_pay_{c.Voucher:N}";
        await d.Scalar<int>($$"""
            CREATE OR REPLACE FUNCTION {{fn}}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.voucher_id = '{{c.Voucher}}' AND NEW.cash_transaction_id IS NOT NULL THEN RAISE EXCEPTION 'journal write failed'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER trg_{{fn}} BEFORE INSERT ON voucher_journal_suggestions FOR EACH ROW EXECUTE FUNCTION {{fn}}();
            SELECT 1;
            """);
        try
        {
            await Assert.ThrowsAnyAsync<PostgresException>(() => PayAsync(c, "bank", bank));
            Assert.Equal(Recognized, await d.StateAsync(c));
            Assert.Equal(100_000_000m, await d.Scalar<decimal>("SELECT current_balance FROM sim_bank_accounts WHERE id=@bank", new { bank }));
        }
        finally { await d.Scalar<int>($"DROP TRIGGER trg_{fn} ON voucher_journal_suggestions; SELECT 1;"); }
    }

    [Fact]
    public async Task Case12_ExistingChain_Unchanged_CashClaimsNeedPayment()
    {
        if (Skip) return;
        var claims = d.Claims();
        var fresh = await d.CreateAsync(stage: "proposed");
        Assert.Equal("dept_reviewed", (await claims.DepartmentReviewAsync(fx.Tenant, fx.Dept, fresh.Voucher, fx.Admin, default))!.ReviewStatus);
        // The same ADMIN may run stage 2 as well (ADMIN Department Authority Invariant).
        Assert.Equal("recognized", (await claims.AccountingReviewAsync(fx.Tenant, fx.Dept, fresh.Voucher, fx.Admin, default))!.RecognitionStatus);
        // Cash due -> the old confirm cannot approve without paying.
        Assert.Null(await claims.ConfirmAsync(fx.Tenant, fx.Dept, fresh.Voucher, fx.Admin, default));
        Assert.Equal(Recognized, await d.StateAsync(fresh));
    }

    [Fact]
    public async Task Payment_ProvesTheRecognizedLiability()
    {
        if (Skip) return;
        var c = await d.CreateAsync();
        // Tamper with the posted 334 credit (direct DB write — not reachable through the app).
        // Postings are immutable (trigger); bypass triggers for this one tamper simulation.
        await d.Scalar<int>("""
            SET session_replication_role = replica;
            UPDATE voucher_journal_suggestions SET amount = amount - 1 WHERE expense_claim_id=@id AND credit_account='334';
            SET session_replication_role = origin;
            SELECT 1;
            """, new { id = c.ClaimId });
        Assert.Equal("recognition_payable_mismatch", await Blocked(() => PayAsync(c, "cash")));
        Assert.Equal(0, (await d.StateAsync(c)).Cash);
    }

    [Fact]
    public async Task Database_PaidWithoutPosting_OrWithoutRecognition_IsImpossible()
    {
        if (Skip) return;
        var c = await d.CreateAsync(stage: "dept_reviewed");
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            d.Scalar<int>("UPDATE expense_claim_results SET payment_status='paid' WHERE id=@id RETURNING 1", new { id = c.ClaimId }));
        Assert.Contains(ex.ConstraintName, new[] { "chk_expense_claim_payment_status", "chk_expense_claim_paid_after_recognition" });
    }
}
