using Aiq.Gateway.Banking;
using Aiq.Gateway.Tests.Banking;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Customer/Supplier Bank Account Master + bank statement lines -> Customer
/// identification, against real Postgres (shares the settlement fixture so
/// the accounting-regression checks see the real AR/AP/journal tables).
/// </summary>
public sealed partial class CashSettlementIntegrationTests
{
    private IBankAccountRepository BankAccounts() => new BankAccountRepository(Factory());
    private IBankStatementRepository Statements() => new BankStatementRepository(Factory(), BankAccounts());

    private async Task<Guid> StatementVoucherAsync(string doc, Guid? departmentId = null) =>
        await Scalar<Guid>("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, document_number, document_date)
            VALUES (@u, @t, @d, 'bank_statement', @doc, @date) RETURNING id
            """, new { u = _adminId, t = _tenantId, d = departmentId ?? _departmentId, doc, date = Today });

    private sealed record DemoParties(Guid VietTin, Guid MinhLong, Guid KimLong, Guid VtInvoice, Guid MlInvoice);

    /// <summary>Spec test data: Việt Tín 123456789 (VCB), Minh Long 456789123, Kim Long 987654321 (ACB).</summary>
    private async Task<DemoParties> SeedBankingDemoAsync()
    {
        var vt = await SeedCustomerAsync("DEMO-CUS-VIETTIN", "Công ty TNHH Việt Tín", "0307890123");
        var ml = await SeedCustomerAsync("DEMO-CUS-MINHLONG", "Công ty TNHH Minh Long", "0315550001");
        var kl = await SeedSupplierAsync("DEMO-SUP-KIMLONG", "Công ty CP Vật tư Kim Long", "0200000002");
        var vtInv = await BookArInvoiceAsync(vt, "DEMO-INV-VT-001", 85_000_000m);
        var mlInv = await BookArInvoiceAsync(ml, "DEMO-INV-ML-001", 60_000_000m);
        await BookApInvoiceAsync(kl, "TEST-PINV-2001", 16_500_000m);
        var repo = BankAccounts();
        await repo.CreateAsync(_tenantId, _departmentId, "customer", vt, _adminId, new BankAccountInput("VCB", "Vietcombank", "123456789", "CÔNG TY TNHH VIỆT TÍN", null, null, null), default);
        await repo.CreateAsync(_tenantId, _departmentId, "customer", ml, _adminId, new BankAccountInput("VCB", "Vietcombank", "456789123", "CÔNG TY TNHH MINH LONG", null, null, null), default);
        await repo.CreateAsync(_tenantId, _departmentId, "supplier", kl, _adminId, new BankAccountInput("ACB", "ACB", "987654321", "CÔNG TY CP VẬT TƯ KIM LONG", null, null, null), default);
        return new DemoParties(vt, ml, kl, vtInv, mlInv);
    }

    private async Task<string> AccountingSnapshotAsync() =>
        string.Join("|", await ScalarOrNull<string>("SELECT string_agg(customer_code || '=' || current_ar, ',' ORDER BY customer_code) FROM sim_customers"),
            await ScalarOrNull<string>("SELECT string_agg(supplier_code || '=' || current_ap, ',' ORDER BY supplier_code) FROM sim_suppliers"),
            await Scalar<long>("SELECT COUNT(*) FROM sim_ar_ledger"), await Scalar<long>("SELECT COUNT(*) FROM sim_ap_ledger"),
            await Scalar<string>("SELECT COALESCE(string_agg(paid_amount::text || status, ',' ORDER BY id), '') FROM sim_ar_ledger"),
            await Scalar<long>("SELECT COUNT(*) FROM voucher_journal_suggestions"),
            await Scalar<long>("SELECT COUNT(*) FROM payment_applications"), await Scalar<long>("SELECT COUNT(*) FROM cash_transactions"));

    // ------------------------------------------------------------------
    // Bank Account Master
    // ------------------------------------------------------------------

    [Fact]
    public async Task BankAccount_AdminCreatesEditsDeactivates_CustomerAccount_NeverDeleted()
    {
        if (_cs is null) return;
        var c = await SeedCustomerAsync("CUS-BA1", "Công ty BA1", "0300000101");
        var repo = BankAccounts();
        var created = await repo.CreateAsync(_tenantId, _departmentId, "customer", c, _adminId,
            new BankAccountInput("vcb", "Vietcombank", "0012 3456 789", "CTY BA1", null, null, null), default);
        Assert.Equal(("0012 3456 789", "00123456789", "VCB", true, true), (created!.AccountNumber, created.AccountNumberNormalized, created.BankCode, created.IsPrimary, created.IsActive));
        Assert.Equal("text", await Scalar<string>("SELECT data_type FROM information_schema.columns WHERE table_name='counterparty_bank_accounts' AND column_name='account_number' AND table_schema=current_schema()"));

        var edited = await repo.UpdateAsync(_tenantId, _departmentId, "customer", c, created.Id, _adminId,
            new BankAccountInput(null, null, null, "CÔNG TY BA1", "Hà Nội", null, null), default);
        Assert.Equal(("CÔNG TY BA1", "Hà Nội", "00123456789"), (edited!.AccountName, edited.Branch, edited.AccountNumberNormalized));

        var off = await repo.SetActiveAsync(_tenantId, _departmentId, "customer", c, created.Id, _adminId, false, default);
        Assert.Equal((false, _adminId), (off!.IsActive, off.DeactivatedBy));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM counterparty_bank_accounts WHERE id=@id", new { id = created.Id }));
        Assert.True((await repo.SetActiveAsync(_tenantId, _departmentId, "customer", c, created.Id, _adminId, true, default))!.IsActive);
    }

    [Fact]
    public async Task BankAccount_SupplierAccount_AndSameOwnerReAddIsIdempotent()
    {
        if (_cs is null) return;
        var s = await SeedSupplierAsync("SUP-BA1", "NCC BA1", "0200000101");
        var repo = BankAccounts();
        var a = await repo.CreateAsync(_tenantId, _departmentId, "supplier", s, _adminId, new BankAccountInput("ACB", null, "987654321", "NCC BA1", null, null, null), default);
        var again = await repo.CreateAsync(_tenantId, _departmentId, "supplier", s, _adminId, new BankAccountInput("acb", null, "9876 54321", null, null, null, null), default);
        Assert.Equal(a!.Id, again!.Id);
        Assert.Single((await repo.ListAsync(_tenantId, _departmentId, "supplier", s, default))!);
    }

    [Fact]
    public async Task BankAccount_SameAccountForAnotherCounterparty_IsAConflict()
    {
        if (_cs is null) return;
        var vt = await SeedCustomerAsync("CUS-BA2", "VT2", "0300000102");
        var ml = await SeedCustomerAsync("CUS-BA3", "ML3", "0300000103");
        var kl = await SeedSupplierAsync("SUP-BA2", "KL2", "0200000102");
        var repo = BankAccounts();
        await repo.CreateAsync(_tenantId, _departmentId, "customer", vt, _adminId, new BankAccountInput("VCB", null, "123456789", null, null, null, null), default);

        var sameBank = await Assert.ThrowsAsync<BankAccountException>(() => repo.CreateAsync(_tenantId, _departmentId, "customer", ml, _adminId,
            new BankAccountInput("VCB", null, "123 456 789", null, null, null, null), default));
        Assert.Equal("bank_account_owned_by_other", sameBank.Code);
        Assert.Contains("VT2", sameBank.Message);
        var unknownBank = await Assert.ThrowsAsync<BankAccountException>(() => repo.CreateAsync(_tenantId, _departmentId, "supplier", kl, _adminId,
            new BankAccountInput(null, null, "123456789", null, null, null, null), default));
        Assert.Equal("bank_account_owned_by_other", unknownBank.Code);

        // Same digits at a DIFFERENT, known bank is a different account.
        Assert.NotNull(await repo.CreateAsync(_tenantId, _departmentId, "customer", ml, _adminId, new BankAccountInput("ACB", null, "123456789", null, null, null, null), default));
        // Other tenant/department may hold the same number independently.
        var otherDeptCustomer = await SeedCustomerAsync("CUS-BA4", "Other dept", "0300000104", _otherDepartmentId);
        Assert.NotNull(await repo.CreateAsync(_tenantId, _otherDepartmentId, "customer", otherDeptCustomer, _adminId, new BankAccountInput("VCB", null, "123456789", null, null, null, null), default));
    }

    [Fact]
    public async Task BankAccount_CrossDepartmentAndCrossTenant_NotFound()
    {
        if (_cs is null) return;
        var c = await SeedCustomerAsync("CUS-BA5", "BA5", "0300000105");
        var repo = BankAccounts();
        var a = await repo.CreateAsync(_tenantId, _departmentId, "customer", c, _adminId, new BankAccountInput("VCB", null, "55555555", null, null, null, null), default);
        Assert.Null(await repo.ListAsync(_tenantId, _otherDepartmentId, "customer", c, default));
        Assert.Null(await repo.CreateAsync(_tenantId, _otherDepartmentId, "customer", c, _adminId, new BankAccountInput("VCB", null, "66666666", null, null, null, null), default));
        Assert.Null(await repo.CreateAsync(_otherTenantId, _otherTenantDepartmentId, "customer", c, _adminId, new BankAccountInput("VCB", null, "66666666", null, null, null, null), default));
        Assert.Null(await repo.SetActiveAsync(_otherTenantId, _otherTenantDepartmentId, "customer", c, a!.Id, _adminId, false, default));
        Assert.Null(await repo.UpdateAsync(_tenantId, _otherDepartmentId, "customer", c, a.Id, _adminId, new BankAccountInput(null, null, null, "x", null, null, null), default));
        Assert.True(await Scalar<bool>("SELECT is_active FROM counterparty_bank_accounts WHERE id=@id", new { id = a.Id }));
    }

    [Fact]
    public async Task BankAccount_InvalidNumber_Rejected()
    {
        if (_cs is null) return;
        var c = await SeedCustomerAsync("CUS-BA6", "BA6", "0300000106");
        var ex = await Assert.ThrowsAsync<BankAccountException>(() => BankAccounts().CreateAsync(_tenantId, _departmentId, "customer", c, _adminId,
            new BankAccountInput("VCB", null, "12", null, null, null, null), default));
        Assert.Equal("invalid_account_number", ex.Code);
    }

    // ------------------------------------------------------------------
    // Statement import + matching
    // ------------------------------------------------------------------

    [Fact]
    public async Task Statement_ImportsEachLine_MatchesPerSpec_AndChangesNoAccounting()
    {
        if (_cs is null) return;
        var p = await SeedBankingDemoAsync();
        var before = await AccountingSnapshotAsync();
        var v = await StatementVoucherAsync("SK-1");

        var summary = await Statements().ImportAsync(_tenantId, _departmentId, v, BankStatementParser.ParseDelimitedText(BankStatementParserTests.DemoCsv), default);
        Assert.Equal((5, 5, 0), (summary.Lines, summary.Inserted, summary.Duplicates));
        Assert.Equal("0011002345678", summary.CompanyAccountNumber);

        var rows = (await Statements().ListAsync(_tenantId, _departmentId, v, default))!;
        Assert.Equal(5, rows.Count);
        // 1. Việt Tín by exact known account.
        Assert.Equal(("matched", "bank_account_exact", 1.000m, p.VietTin), (rows[0].MatchStatus, rows[0].MatchMethod, rows[0].MatchConfidence, rows[0].CustomerId));
        Assert.Equal(("money_in", 20_000_000m, "123456789", "CÔNG TY TNHH VIỆT TÍN"), (rows[0].Direction, rows[0].Amount, rows[0].CounterpartyAccount, rows[0].CounterpartyName));
        // 2. Minh Long by exact known account.
        Assert.Equal(("matched", "bank_account_exact", p.MinhLong), (rows[1].MatchStatus, rows[1].MatchMethod, rows[1].CustomerId));
        // 3. No account, invoice reference in the description.
        Assert.Equal(("matched", "invoice_reference", p.VietTin), (rows[2].MatchStatus, rows[2].MatchMethod, rows[2].CustomerId));
        // 4. Unknown person — never matched (no customer owes exactly 15M here either).
        Assert.Contains(rows[3].MatchStatus, new[] { "unmatched", "needs_review" });
        Assert.Null(rows[3].CustomerId);
        // 5. Money out, supplier candidate via Supplier Bank Account Master; nothing settled.
        Assert.Equal(("money_out", 5_500_000m, "matched", p.KimLong), (rows[4].Direction, rows[4].Amount, rows[4].MatchStatus, rows[4].SupplierId));
        Assert.Null(rows[4].CustomerId);

        Assert.Equal(before, await AccountingSnapshotAsync());
    }

    [Fact]
    public async Task Statement_ReUpload_DoesNotDuplicateLines()
    {
        if (_cs is null) return;
        await SeedBankingDemoAsync();
        var parsed = BankStatementParser.ParseDelimitedText(BankStatementParserTests.DemoCsv);
        var v1 = await StatementVoucherAsync("SK-A");
        await Statements().ImportAsync(_tenantId, _departmentId, v1, parsed, default);
        var again = await Statements().ImportAsync(_tenantId, _departmentId, v1, parsed, default);
        Assert.Equal((0, 5), (again.Inserted, again.Duplicates));
        var v2 = await StatementVoucherAsync("SK-B");
        var second = await Statements().ImportAsync(_tenantId, _departmentId, v2, parsed, default);
        Assert.Equal((0, 5), (second.Inserted, second.Duplicates));
        Assert.Equal(new[] { v1 }, second.DuplicateOfVouchers);
        Assert.Equal(5, await Scalar<int>("SELECT COUNT(*) FROM sim_bank_transactions WHERE tenant_id=@t", new { t = _tenantId }));
    }

    [Fact]
    public async Task Statement_AccountSaysVietTin_InvoiceSaysMinhLong_NeedsReview()
    {
        if (_cs is null) return;
        var p = await SeedBankingDemoAsync();
        var v = await StatementVoucherAsync("SK-C");
        const string csv = "Ngày,Nội dung,TK đối ứng,Tên đối ứng,Tiền vào,Tiền ra\n04/10/2026,TT HD DEMO-INV-ML-001,123456789,CONG TY TNHH VIET TIN,1000000,\n"
                         + "04/10/2026,CK MST 0315550001 THANH TOAN,,,2000000,\n"
                         + "04/10/2026,THANH TOAN,,CONG TY TNHH VIET TIN,3000000,\n";
        await Statements().ImportAsync(_tenantId, _departmentId, v, BankStatementParser.ParseDelimitedText(csv), default);
        var rows = (await Statements().ListAsync(_tenantId, _departmentId, v, default))!;

        Assert.Equal(("needs_review", "conflict"), (rows[0].MatchStatus, rows[0].MatchMethod));
        Assert.Null(rows[0].CustomerId);
        Assert.Contains("DEMO-INV-ML-001", rows[0].MatchEvidenceJson);
        Assert.Contains(p.VietTin.ToString(), rows[0].MatchEvidenceJson);
        Assert.Contains(p.MinhLong.ToString(), rows[0].MatchEvidenceJson);

        Assert.Equal(("matched", "tax_code", p.MinhLong), (rows[1].MatchStatus, rows[1].MatchMethod, rows[1].CustomerId));
        Assert.Equal(("matched", "name_exact", p.VietTin), (rows[2].MatchStatus, rows[2].MatchMethod, rows[2].CustomerId));
    }

    [Fact]
    public async Task Statement_AmountEqualsAnOpenInvoice_IsOnlyASuggestion()
    {
        if (_cs is null) return;
        var p = await SeedBankingDemoAsync();
        var v = await StatementVoucherAsync("SK-D");
        // 85,000,000 == Việt Tín's open invoice outstanding, but nothing identifies the payer.
        const string csv = "Ngày,Nội dung,TK đối ứng,Tên đối ứng,Tiền vào,Tiền ra\n04/10/2026,THANH TOAN,111222333,NGUYEN VAN B,85000000,\n";
        await Statements().ImportAsync(_tenantId, _departmentId, v, BankStatementParser.ParseDelimitedText(csv), default);
        var row = Assert.Single((await Statements().ListAsync(_tenantId, _departmentId, v, default))!);
        Assert.Equal(("needs_review", "amount_hint"), (row.MatchStatus, row.MatchMethod));
        Assert.Null(row.CustomerId);
        Assert.Contains(p.VietTin.ToString(), row.MatchEvidenceJson);
    }

    // ------------------------------------------------------------------
    // ADMIN review + learning
    // ------------------------------------------------------------------

    [Fact]
    public async Task Admin_SelectsCustomer_SavesAccountExplicitly_NextStatementMatchesByAccount()
    {
        if (_cs is null) return;
        var p = await SeedBankingDemoAsync();
        var before = await AccountingSnapshotAsync();
        var v = await StatementVoucherAsync("SK-E");
        await Statements().ImportAsync(_tenantId, _departmentId, v, BankStatementParser.ParseDelimitedText(BankStatementParserTests.DemoCsv), default);
        var unknown = (await Statements().ListAsync(_tenantId, _departmentId, v, default))![3];
        var autoEvidence = unknown.MatchEvidenceJson;

        // Without the explicit flag nothing is learned.
        var noSave = await Statements().ConfirmMatchAsync(_tenantId, _departmentId, v, unknown.Id, _adminId, p.VietTin, false, default);
        Assert.Null(noSave!.LearnedAccount);
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM counterparty_bank_accounts WHERE account_number_normalized='123888999'"));
        Assert.Equal(("matched", "admin_selected", p.VietTin, _adminId), (noSave.Transaction.MatchStatus, noSave.Transaction.MatchMethod, noSave.Transaction.CustomerId, noSave.Transaction.ConfirmedBy));
        Assert.False(noSave.Transaction.CounterpartyAccountKnownForOwner);

        // Explicit save.
        var saved = await Statements().ConfirmMatchAsync(_tenantId, _departmentId, v, unknown.Id, _adminId, p.VietTin, true, default);
        Assert.Equal(("123888999", "statement_review", unknown.Id, _adminId), (saved!.LearnedAccount!.AccountNumberNormalized, saved.LearnedAccount.Source, saved.LearnedAccount.SourceTransactionId, saved.LearnedAccount.CreatedBy));
        Assert.True(saved.Transaction.CounterpartyAccountKnownForOwner);
        // Original automatic evidence is preserved; both decisions are audited.
        using (var doc = System.Text.Json.JsonDocument.Parse(saved.Transaction.MatchEvidenceJson))
        {
            Assert.Equal(System.Text.Json.JsonDocument.Parse(autoEvidence).RootElement.GetProperty("auto").GetRawText(), doc.RootElement.GetProperty("auto").GetRawText());
            Assert.Equal(2, doc.RootElement.GetProperty("decisions").GetArrayLength());
        }

        // Rematch never overrides an ADMIN decision.
        await Statements().RematchAsync(_tenantId, _departmentId, v, default);
        Assert.Equal("admin_selected", (await Statements().ListAsync(_tenantId, _departmentId, v, default))![3].MatchMethod);

        // A new transfer from that account now resolves by bank account.
        var v2 = await StatementVoucherAsync("SK-F");
        const string csv = "Ngày,Mã GD,Nội dung,TK đối ứng,Tên đối ứng,Tiền vào,Tiền ra\n05/10/2026,FT26278001,THANH TOAN LAN 2,123888999,NGUYEN VAN A,7000000,\n";
        await Statements().ImportAsync(_tenantId, _departmentId, v2, BankStatementParser.ParseDelimitedText(csv), default);
        var next = Assert.Single((await Statements().ListAsync(_tenantId, _departmentId, v2, default))!);
        Assert.Equal(("matched", "bank_account_exact", p.VietTin), (next.MatchStatus, next.MatchMethod, next.CustomerId));

        Assert.Equal(before, await AccountingSnapshotAsync());
    }

    [Fact]
    public async Task Admin_SaveAccountOwnedByAnotherCustomer_IsBlocked_AndNothingChanges()
    {
        if (_cs is null) return;
        var p = await SeedBankingDemoAsync();
        var v = await StatementVoucherAsync("SK-G");
        await Statements().ImportAsync(_tenantId, _departmentId, v, BankStatementParser.ParseDelimitedText(BankStatementParserTests.DemoCsv), default);
        var minhLongLine = (await Statements().ListAsync(_tenantId, _departmentId, v, default))![1]; // account 456789123 = Minh Long

        var ex = await Assert.ThrowsAsync<BankAccountException>(() =>
            Statements().ConfirmMatchAsync(_tenantId, _departmentId, v, minhLongLine.Id, _adminId, p.VietTin, true, default));
        Assert.Equal("bank_account_owned_by_other", ex.Code);
        var after = (await Statements().ListAsync(_tenantId, _departmentId, v, default))![1];
        Assert.Equal(("bank_account_exact", p.MinhLong, (Guid?)null), (after.MatchMethod, after.CustomerId, after.ConfirmedBy));
    }

    [Fact]
    public async Task Admin_Review_IsScoped_AndRejectsForeignCustomer()
    {
        if (_cs is null) return;
        await SeedBankingDemoAsync();
        var v = await StatementVoucherAsync("SK-H");
        await Statements().ImportAsync(_tenantId, _departmentId, v, BankStatementParser.ParseDelimitedText(BankStatementParserTests.DemoCsv), default);
        var line = (await Statements().ListAsync(_tenantId, _departmentId, v, default))![3];
        var foreign = await SeedCustomerAsync("CUS-OTHERDEPT", "Khác phòng", "0300000199", _otherDepartmentId);

        Assert.Null(await Statements().ListAsync(_tenantId, _otherDepartmentId, v, default));
        Assert.Null(await Statements().ListAsync(_otherTenantId, _otherTenantDepartmentId, v, default));
        Assert.Null(await Statements().ConfirmMatchAsync(_otherTenantId, _otherTenantDepartmentId, v, line.Id, _adminId, foreign, false, default));
        var ex = await Assert.ThrowsAsync<BankAccountException>(() => Statements().ConfirmMatchAsync(_tenantId, _departmentId, v, line.Id, _adminId, foreign, false, default));
        Assert.Equal("owner_not_in_scope", ex.Code);
        Assert.Null(await Statements().RematchAsync(_tenantId, _otherDepartmentId, v, default));
    }
}
