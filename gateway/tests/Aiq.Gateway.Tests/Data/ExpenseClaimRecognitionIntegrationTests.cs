using Aiq.Gateway.Data;
using Aiq.Gateway.Employees;
using Aiq.Gateway.Payments;
using Aiq.Gateway.Vouchers;
using Dapper;
using Npgsql;
using L = Aiq.Gateway.Tests.Data.ExpenseClaimTestData.Line;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Authoritative expense recognition at accounting approval (stage 2 "Duyệt kế
/// toán"), before payment, on the real schema. Accounts only from
/// department_expense_policies.debit_account (cong_tac* -> 642, others -> 641
/// in these fixtures); 141 / 334 / 1331 from the deterministic router.
/// </summary>
public sealed class ExpenseClaimRecognitionIntegrationTests(TaxVerificationDatabase fx) : IClassFixture<TaxVerificationDatabase>
{
    static ExpenseClaimRecognitionIntegrationTests()
    {
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyTypeHandler());
    }

    private readonly ExpenseClaimTestData d = new(fx);
    private bool Skip => fx.ConnectionString is null;

    // Case A: hotel 5.5M incl. 0.5M deductible VAT (invoice) + meals 3.7M (no invoice) = 9.2M; advance 5M -> payable 4.2M.
    private static readonly L[] CaseA = [new("cong_tac", 5_500_000m, 500_000m), new("an_uong", 3_700_000m, 0m, Invoice: false)];

    private Task<string> Refused(Func<Task> act) => Assert.ThrowsAsync<ExpenseRecognitionException>(act).ContinueWith(t => t.Result.Code);

    // CASE 1 — company owes employee, then the existing payment -----------------

    [Fact]
    public async Task Case1_CompanyOwesEmployee_RecognizedOnce_ThenPaid_EmployeeUntouchedByPayment()
    {
        if (Skip) return;
        var c = await d.CreateAsync(CaseA, advance: 5_000_000m, stage: "dept_reviewed");
        var r = (await d.RecognizeAsync(c))!;
        Assert.Equal(("accounting_reviewed", "recognized", "unpaid", fx.Admin2), (r.ReviewStatus, r.RecognitionStatus, r.PaymentStatus, r.RecognizedBy!.Value));

        var lines = await d.RecognitionLinesAsync(c);
        Assert.Equal(new[] { ("642", "141", 5_000_000m), ("641", "334", 3_700_000m), ("1331", "334", 500_000m) }, lines);
        Assert.Equal(5_000_000m, lines.Where(l => l.Credit == "141").Sum(l => l.Amount));   // advance cleared
        Assert.Equal(4_200_000m, lines.Where(l => l.Credit == "334").Sum(l => l.Amount));   // employee payable
        Assert.Equal(9_200_000m, lines.Sum(l => l.Amount));                                 // total recognized
        Assert.Equal(new ExpenseClaimTestData.State(0, 3, 0, "unpaid", "accounting_reviewed", "recognized", 0m, 10_200_000m), await d.StateAsync(c));

        var bank = await d.Scalar<Guid>("""
            INSERT INTO sim_bank_accounts(tenant_id, account_number, bank_name, account_holder, gl_account_code, current_balance)
            VALUES (@t, @n, 'Vietcombank', 'Sáu Trực', '1121', 50000000) RETURNING id
            """, new { t = fx.Tenant, n = "77" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999) });
        var paid = (await d.Payments().PayExpenseClaimAsync(fx.Tenant, fx.Dept, c.Voucher, fx.Admin3, new ExpenseClaimPayRequest("bank", bank), default))!;
        Assert.Equal("paid", paid.Outcome);
        Assert.Equal(("334", "1121", 4_200_000m), (paid.Context.Payment!.Journal[0].DebitAccount, paid.Context.Payment.Journal[0].CreditAccount, paid.Context.Payment.Journal[0].Amount));
        // Recognition unchanged; employee advance/YTD NOT touched again by the payment.
        Assert.Equal(new ExpenseClaimTestData.State(1, 3, 1, "paid", "confirmed", "recognized", 0m, 10_200_000m), await d.StateAsync(c));
        Assert.False(paid.Context.Recognition.IsPreview);
        Assert.Equal((5_000_000m, 4_200_000m), (paid.Context.Recognition.AdvanceCleared, paid.Context.Recognition.EmployeePayable));
    }

    // CASE 2 — exact advance: recognized, no 334, not_due, no cash -----------------

    [Fact]
    public async Task Case2_ExactAdvance_RecognizedAgainst141Only_NotDue_NoCash()
    {
        if (Skip) return;
        var c = await d.CreateAsync([new("cong_tac", 5_000_000m)], advance: 5_000_000m);
        var lines = await d.RecognitionLinesAsync(c);
        Assert.Equal(new[] { ("642", "141", 5_000_000m) }, lines);
        Assert.DoesNotContain(lines, l => l.Credit == "334");
        Assert.Equal(new ExpenseClaimTestData.State(0, 1, 0, "not_due", "accounting_reviewed", "recognized", 0m, 6_000_000m), await d.StateAsync(c));
        Assert.Equal("no_payment_due", (await Assert.ThrowsAsync<CashSettlementException>(() =>
            d.Payments().PayExpenseClaimAsync(fx.Tenant, fx.Dept, c.Voucher, fx.Admin3, new ExpenseClaimPayRequest("cash", null), default))).Code);

        // Final (no-cash) approval closes the chain — never re-applies the employee update.
        var confirmed = (await d.Claims().ConfirmAsync(fx.Tenant, fx.Dept, c.Voucher, fx.Admin3, default))!;
        Assert.Equal(("confirmed", "not_due", "recognized"), (confirmed.ReviewStatus, confirmed.PaymentStatus, confirmed.RecognitionStatus));
        Assert.Equal(new ExpenseClaimTestData.State(0, 1, 0, "not_due", "confirmed", "recognized", 0m, 6_000_000m), await d.StateAsync(c));
    }

    // CASE 3 — pay before recognition ----------------------------------------------

    [Fact]
    public async Task Case3_PayBeforeRecognition_Blocked()
    {
        if (Skip) return;
        var notYet = await d.CreateAsync(stage: "dept_reviewed");
        var ctx = (await d.Payments().GetExpenseClaimPaymentAsync(fx.Tenant, fx.Dept, notYet.Voucher, fx.Admin3, default))!;
        Assert.Contains(ctx.Blockers, b => b.Code == "expense_not_recognized" && b.Message == "Đề nghị chưa được ghi nhận kế toán.");

        // A claim that reached accounting_reviewed WITHOUT recognition (pre-059 shape) is still refused.
        var legacy = await d.CreateAsync(stage: "dept_reviewed");
        await d.Scalar<int>("UPDATE expense_claim_results SET review_status='accounting_reviewed', accounting_reviewed_by=@a, accounting_reviewed_at=NOW() WHERE id=@id RETURNING 1",
            new { a = fx.Admin2, id = legacy.ClaimId });
        var ex = await Assert.ThrowsAsync<CashSettlementException>(() =>
            d.Payments().PayExpenseClaimAsync(fx.Tenant, fx.Dept, legacy.Voucher, fx.Admin3, new ExpenseClaimPayRequest("cash", null), default));
        Assert.Equal(("expense_not_recognized", "Đề nghị chưa được ghi nhận kế toán."), (ex.Code, ex.Message));
        Assert.Equal(0, (await d.StateAsync(legacy)).Cash);
    }

    // CASE 4 + 5 — idempotency / concurrency -----------------------------------------

    [Fact]
    public async Task Case4_DoubleRecognition_OneJournal_OneEmployeeUpdate()
    {
        if (Skip) return;
        var c = await d.CreateAsync(CaseA, advance: 5_000_000m);
        var again = (await d.RecognizeAsync(c))!; // same reviewer retries
        Assert.Equal("recognized", again.RecognitionStatus);
        Assert.Null(await d.RecognizeAsync(c, actor: fx.Admin3)); // not dept_reviewed any more
        Assert.Equal(new ExpenseClaimTestData.State(0, 3, 0, "unpaid", "accounting_reviewed", "recognized", 0m, 10_200_000m), await d.StateAsync(c));
        var ctx = (await d.Payments().GetExpenseClaimPaymentAsync(fx.Tenant, fx.Dept, c.Voucher, fx.Admin3, default))!;
        Assert.Equal(("recognized", false, fx.Admin2), (ctx.Recognition.Status, ctx.Recognition.IsPreview, ctx.Recognition.RecognizedBy!.Value));
    }

    [Fact]
    public async Task Case5_ConcurrentRecognition_ExactlyOne()
    {
        if (Skip) return;
        var c = await d.CreateAsync(CaseA, advance: 5_000_000m, stage: "dept_reviewed");
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => d.RecognizeAsync(c)));
        Assert.All(results, r => Assert.Equal("recognized", r!.RecognitionStatus));
        Assert.Equal(new ExpenseClaimTestData.State(0, 3, 0, "unpaid", "accounting_reviewed", "recognized", 0m, 10_200_000m), await d.StateAsync(c));
    }

    // CASE 6 — tax ---------------------------------------------------------------------

    [Fact]
    public async Task Case6_TaxNotVerified_RecognitionBlocked()
    {
        if (Skip) return;
        var c = await d.CreateAsync(CaseA, advance: 5_000_000m, stage: "dept_reviewed", verifyTax: false);
        Assert.Equal("tax_verification_required", await Refused(() => d.RecognizeAsync(c)));
        Assert.Equal(new ExpenseClaimTestData.State(0, 0, 0, "unpaid", "dept_reviewed", "unrecognized", 5_000_000m, 1_000_000m), await d.StateAsync(c));
    }

    // CASE 7 + 8 — authority / scope (USER is refused by the route policy) -------------

    [Fact]
    public async Task Case7_StageOneReviewer_OrClaimantAdmin_MayRecognize()
    {
        if (Skip) return;
        // ADMIN Department Authority Invariant: no different-actor / not-the-claimant rule.
        var c = await d.CreateAsync(stage: "dept_reviewed");
        Assert.Equal("recognized", (await d.RecognizeAsync(c, actor: fx.Admin))!.RecognitionStatus); // stage-1 reviewer
        var own = await d.CreateAsync(stage: "dept_reviewed", claimant: fx.Admin3);
        Assert.Equal("recognized", (await d.RecognizeAsync(own, actor: fx.Admin3))!.RecognitionStatus); // claimant
        Assert.Equal(6_000_000m, (await d.StateAsync(own)).Ytd);   // employee updated exactly once
    }

    [Fact]
    public async Task Case8_OtherDepartment_NoMutation()
    {
        if (Skip) return;
        var c = await d.CreateAsync(stage: "dept_reviewed");
        Assert.Null(await d.RecognizeAsync(c, dept: fx.OtherDept));
        Assert.Equal(new ExpenseClaimTestData.State(0, 0, 0, "unpaid", "dept_reviewed", "unrecognized", 800_000m, 1_000_000m), await d.StateAsync(c));
    }

    // CASE 9 — AI account injection -----------------------------------------------------

    [Fact]
    public async Task Case9_AiSuggestedAccount_NeverPosted_TrustedMappingUsed_SuggestionPreserved()
    {
        if (Skip) return;
        var c = await d.CreateAsync(CaseA, advance: 5_000_000m, stage: "dept_reviewed");
        await d.Scalar<int>("""UPDATE vouchers SET extracted_fields = extracted_fields || '{"gl_account":"641999","debit_account":"9999"}'::jsonb WHERE id=@id RETURNING 1""", new { id = c.Voucher });
        await new VoucherRepository(d.Factory()).SaveJournalSuggestionsAsync(fx.Tenant, fx.Dept, c.Voucher,
            new SaveJournalSuggestionsRequest(new[] { new SaveJournalSuggestionDto(1, "9999", "334", 9_200_000m, "AI: chi phí", 0.99m) }, true), default);

        await d.RecognizeAsync(c);
        var lines = await d.RecognitionLinesAsync(c);
        Assert.DoesNotContain(lines, l => l.Debit is "9999" or "641999");
        Assert.Equal(new[] { "642", "641", "1331" }, lines.Select(l => l.Debit).ToArray());
        // The AI suggestion is kept as history, visibly superseded.
        Assert.Equal("superseded_by_recognition", await d.Scalar<string>(
            "SELECT reject_reason FROM voucher_journal_suggestions WHERE voucher_id=@id AND source='suggestion' AND debit_account='9999'", new { id = c.Voucher }));
    }

    [Fact]
    public async Task MissingOrDisallowedMapping_Blocks()
    {
        if (Skip) return;
        var unmapped = await d.CreateAsync([new("qua_tang_" + Guid.NewGuid().ToString("N")[..6], 1_000_000m)], advance: 0m, stage: "dept_reviewed", verifyTax: false);
        await d.Scalar<int>("DELETE FROM department_expense_policies WHERE category LIKE 'qua_tang_%'; SELECT 1;");
        await d.Scalar<int>("UPDATE expense_claim_results SET requires_tax_verification=FALSE WHERE id=@id RETURNING 1", new { id = unmapped.ClaimId });
        Assert.Equal("expense_account_mapping_missing", await Refused(() => d.RecognizeAsync(unmapped)));

        var bad = "bad_" + Guid.NewGuid().ToString("N")[..6];
        await d.MapAsync(bad, "9999");
        var disallowed = await d.CreateAsync([new(bad, 1_000_000m, 0m, Invoice: false)], advance: 0m, stage: "dept_reviewed");
        Assert.Equal("expense_account_not_allowed", await Refused(() => d.RecognizeAsync(disallowed)));
        Assert.Equal(0, (await d.StateAsync(disallowed)).RecognitionLines);
    }

    // CASE 10 — atomicity -----------------------------------------------------------------

    [Fact]
    public async Task Case10_JournalFailure_RollsBackRecognition_AndEmployee()
    {
        if (Skip) return;
        var c = await d.CreateAsync(CaseA, advance: 5_000_000m, stage: "dept_reviewed");
        var fn = $"fail_recog_{c.ClaimId:N}";
        await d.Scalar<int>($$"""
            CREATE OR REPLACE FUNCTION {{fn}}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.expense_claim_id = '{{c.ClaimId}}' AND NEW.debit_account = '1331' THEN RAISE EXCEPTION 'journal write failed'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER trg_{{fn}} BEFORE INSERT ON voucher_journal_suggestions FOR EACH ROW EXECUTE FUNCTION {{fn}}();
            SELECT 1;
            """);
        try
        {
            await Assert.ThrowsAnyAsync<PostgresException>(() => d.RecognizeAsync(c));
            Assert.Equal(new ExpenseClaimTestData.State(0, 0, 0, "unpaid", "dept_reviewed", "unrecognized", 5_000_000m, 1_000_000m), await d.StateAsync(c));
        }
        finally { await d.Scalar<int>($"DROP TRIGGER trg_{fn} ON voucher_journal_suggestions; SELECT 1;"); }
    }

    // CASE 11 — edit after recognition ------------------------------------------------------

    [Fact]
    public async Task Case11_EditOrReextractAfterRecognition_IsRefused_NoStaleJournal()
    {
        if (Skip) return;
        var c = await d.CreateAsync(CaseA, advance: 5_000_000m);
        var vouchers = new VoucherRepository(d.Factory());
        UpdateVoucherRequest Edit(decimal total) => new(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, total, null, null, null, null);

        var edit = await Assert.ThrowsAsync<PostgresException>(() => vouchers.UpdateAsync(fx.Tenant, fx.User, c.Voucher, Edit(12_000_000m), default));
        Assert.Equal("23001", edit.SqlState);
        await Assert.ThrowsAsync<PostgresException>(() => vouchers.ReplaceExtractedAsync(fx.Tenant, fx.User, c.Voucher, new CreateVoucherRequest(
            "expense_claim", "X", null, null, null, null, null, null, null, null, null, null, null, null, null, 12_000_000m,
            "VND", null, null, null, null, null, null, new { items = Array.Empty<object>() }, null, null), default));
        await Assert.ThrowsAsync<PostgresException>(() =>
            d.Scalar<int>("UPDATE expense_claim_results SET payable_amount = 8000000 WHERE id=@id RETURNING 1", new { id = c.ClaimId }));

        Assert.Equal(9_200_000m, await d.Scalar<decimal>("SELECT total_amount FROM vouchers WHERE id=@id", new { id = c.Voucher }));
        Assert.Equal(4_200_000m, await d.Scalar<decimal>("SELECT payable_amount FROM expense_claim_results WHERE id=@id", new { id = c.ClaimId }));
        Assert.Equal(4_200_000m, (await d.RecognitionLinesAsync(c)).Where(l => l.Credit == "334").Sum(l => l.Amount));
    }

    // CASE 13 — employee refund ---------------------------------------------------------------

    [Fact]
    public async Task Case13_EmployeeRefund_NotRecognized_NothingInvented()
    {
        if (Skip) return;
        var c = await d.CreateAsync([new("cong_tac", 4_200_000m)], advance: 5_000_000m, stage: "dept_reviewed");
        Assert.Equal("employee_refund_not_supported", await Refused(() => d.RecognizeAsync(c)));
        Assert.Equal(new ExpenseClaimTestData.State(0, 0, 0, "unpaid", "dept_reviewed", "unrecognized", 5_000_000m, 1_000_000m), await d.StateAsync(c));
        Assert.Equal(0, await d.Scalar<int>("SELECT COUNT(*) FROM voucher_journal_suggestions WHERE voucher_id=@id AND source='posting'", new { id = c.Voucher }));
    }

    // CASE 14 — LLM "recognized" text --------------------------------------------------------

    [Fact]
    public async Task Case14_LlmSayingRecognized_NoAuthoritativeJournal()
    {
        if (Skip) return;
        var c = await d.CreateAsync(CaseA, advance: 5_000_000m, stage: "dept_reviewed");
        await d.Scalar<int>("""UPDATE vouchers SET extracted_fields = extracted_fields || '{"recognition_status":"recognized","note":"Expense recognized successfully."}'::jsonb WHERE id=@id RETURNING 1""", new { id = c.Voucher });
        await new VoucherRepository(d.Factory()).SaveJournalSuggestionsAsync(fx.Tenant, fx.Dept, c.Voucher,
            new SaveJournalSuggestionsRequest(new[] { new SaveJournalSuggestionDto(1, "642", "334", 9_200_000m, "AI: đã ghi nhận", 0.99m) }, true), default);
        Assert.Equal(new ExpenseClaimTestData.State(0, 0, 0, "unpaid", "dept_reviewed", "unrecognized", 5_000_000m, 1_000_000m), await d.StateAsync(c));
        var ctx = (await d.Payments().GetExpenseClaimPaymentAsync(fx.Tenant, fx.Dept, c.Voucher, fx.Admin2, default))!;
        Assert.True(ctx.Recognition.IsPreview); // shown as "sẽ ghi nhận", never as posted
        Assert.Equal(new[] { "642", "641", "1331" }, ctx.Recognition.Lines.Select(l => l.DebitAccount).ToArray());
    }

    // Integrity --------------------------------------------------------------------------------

    [Fact]
    public async Task AdvanceChangedSinceProposal_Or_BasisMissing_Blocks()
    {
        if (Skip) return;
        var c = await d.CreateAsync(CaseA, advance: 5_000_000m, stage: "dept_reviewed", employeeOutstanding: 3_000_000m);
        Assert.Equal("advance_changed", await Refused(() => d.RecognizeAsync(c)));
        var noBasis = await d.CreateAsync(stage: "dept_reviewed");
        await d.Scalar<int>("UPDATE expense_claim_results SET recognition_basis=NULL WHERE id=@id RETURNING 1", new { id = noBasis.ClaimId });
        Assert.Equal("recognition_basis_missing", await Refused(() => d.RecognizeAsync(noBasis)));
    }

    [Fact]
    public async Task Database_RecognizedWithoutPosting_IsImpossible()
    {
        if (Skip) return;
        var c = await d.CreateAsync(stage: "dept_reviewed");
        var ex = await Assert.ThrowsAsync<PostgresException>(() => d.Scalar<int>(
            "UPDATE expense_claim_results SET recognition_status='recognized', recognized_by=@a, recognized_at=NOW() WHERE id=@id RETURNING 1",
            new { a = fx.Admin2, id = c.ClaimId }));
        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public void Basis_ResidualsReconcileToTotals_AndRouterGetsNetAmounts()
    {
        var basis = ExpenseRecognition.BuildBasis(
            new Dictionary<string, decimal> { ["a"] = 3_333_333.335m, ["b"] = 1_666_666.665m },
            new Dictionary<string, decimal> { ["a"] = 303_030.3035m },
            new Dictionary<string, decimal> { ["a"] = 4_000_000m, ["b"] = 2_000_000m },
            new Dictionary<string, decimal> { ["a"] = 363_636m },
            5_000_000m, 303_030.30m);
        Assert.Equal(5_000_000m, basis.Sum(b => b.Gross));
        Assert.Equal(303_030.30m, basis.Sum(b => b.VatDeductible));
        var net = ExpenseRecognition.NetByCategory(basis);
        Assert.Equal(5_000_000m - 303_030.30m, net.Values.Sum());
    }

    // Authoritative journal immutability (pre-existing defect fixed here) -------------

    [Fact]
    public async Task AiSuggestionSave_AfterRecognition_NeverDeletesOrOverwritesPostings()
    {
        if (Skip) return;
        var c = await d.CreateAsync(CaseA, advance: 5_000_000m);
        var posted = await d.RecognitionLinesAsync(c);
        var vouchers = new VoucherRepository(d.Factory());
        // An LLM "replace all suggestions" with entry_no 1 (= a posted line's number).
        var saved = await vouchers.SaveJournalSuggestionsAsync(fx.Tenant, fx.Dept, c.Voucher,
            new SaveJournalSuggestionsRequest(new[] { new SaveJournalSuggestionDto(1, "9999", "334", 1m, "AI", 0.9m) }, true), default);
        Assert.Equal(posted, await d.RecognitionLinesAsync(c));
        Assert.NotEqual(1, Assert.Single(saved).EntryNo);
        Assert.Equal("suggestion", saved[0].Source);

        var postingId = await d.Scalar<Guid>("SELECT id FROM voucher_journal_suggestions WHERE expense_claim_id=@id LIMIT 1", new { id = c.ClaimId });
        Assert.Null(await vouchers.AcceptJournalAsync(fx.Tenant, fx.Dept, c.Voucher, postingId, fx.Admin, new AcceptJournalRequest(false, "AI"), default));
        var del = await Assert.ThrowsAsync<PostgresException>(() => d.Scalar<int>("DELETE FROM voucher_journal_suggestions WHERE id=@postingId RETURNING 1", new { postingId }));
        Assert.Equal("23001", del.SqlState);
        await Assert.ThrowsAsync<PostgresException>(() => d.Scalar<int>("UPDATE voucher_journal_suggestions SET amount=1 WHERE id=@postingId RETURNING 1", new { postingId }));
        Assert.Equal(posted, await d.RecognitionLinesAsync(c));
    }
}
