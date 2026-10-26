using System.Text.Json;
using Aiq.Gateway.Data;
using Aiq.Gateway.Employees;
using Aiq.Gateway.Payments;
using Aiq.Gateway.Vouchers;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Expense-claim fixtures on the real schema (<see cref="TaxVerificationDatabase"/>).
/// Roles: claimant = USER, stage 1 = Admin, stage 2 (accounting + recognition) = Admin2, payer = Admin3.
/// </summary>
public sealed class ExpenseClaimTestData(TaxVerificationDatabase fx)
{
    public sealed record Line(string Category, decimal Gross, decimal Vat = 0m, bool Invoice = true);
    public sealed record Claim(Guid Voucher, Guid ClaimId, Guid Employee, IReadOnlyList<Line> Lines);

    public string Cs => fx.ConnectionString!;
    public NpgsqlConnectionFactory Factory() => new(Cs);
    public IExpenseClaimPayment Payments() => new CashSettlementRepository(Factory());
    public ITaxVerificationRepository Tax() => new TaxVerificationRepository(Factory());
    public ExpenseClaimRepository Claims() => new(Factory(), new EmployeeRepository(Factory()), new Aiq.Gateway.Suppliers.SupplierRepository(Factory()),
        new VoucherRepository(Factory()), new Aiq.Gateway.Contracts.NotificationRepository(Factory()));

    /// <summary>For queries that must yield a value; NULL or no row fails the test loudly.</summary>
    public async Task<T> Scalar<T>(string sql, object? args = null) where T : notnull
    {
        await using var c = new NpgsqlConnection(Cs);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<T>(sql, args)
            ?? throw new InvalidOperationException($"Scalar query returned NULL or no row: {sql}");
    }

    /// <summary>For queries where SQL NULL / no row is a valid, asserted outcome.</summary>
    public async Task<T?> ScalarOrNull<T>(string sql, object? args = null)
    {
        await using var c = new NpgsqlConnection(Cs);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<T>(sql, args);
    }

    /// <summary>Trusted category -> GL mapping (department_expense_policies.debit_account).</summary>
    public Task MapAsync(string category, string account, Guid? dept = null) => Scalar<int>("""
        INSERT INTO department_expense_policies(tenant_id, department_id, category, limit_amount, limit_unit, debit_account)
        SELECT @t, @d, @category, 1000000000000, 'per_claim', @account
        WHERE NOT EXISTS (SELECT 1 FROM department_expense_policies WHERE tenant_id=@t AND department_id=@d AND category=@category);
        SELECT 1;
        """, new { t = fx.Tenant, d = dept ?? fx.Dept, category, account });

    public static readonly Line[] Travel = [new("cong_tac", 3_000_000m), new("cong_tac_ve", 2_000_000m)];

    /// <summary>
    /// A claim as ProposeAsync leaves it (incl. the recognition basis) at the given stage:
    /// "proposed" | "dept_reviewed" | "recognized" (stage 2 run for real by Admin2).
    /// </summary>
    public async Task<Claim> CreateAsync(IReadOnlyList<Line>? lines = null, decimal advance = 800_000m, string stage = "recognized",
        bool verifyTax = true, Guid? claimant = null, string outcome = "APPROVED", decimal? employeeOutstanding = null)
    {
        lines ??= Travel;
        foreach (var l in lines) await MapAsync(l.Category, l.Category.StartsWith("cong_tac", StringComparison.Ordinal) ? "642" : "641");
        var code = "NV-" + Guid.NewGuid().ToString("N")[..8];
        var employee = await Scalar<Guid>("""
            INSERT INTO sim_employees(tenant_id, employee_code, full_name, outstanding_advance, ytd_expense)
            VALUES (@t, @code, 'Nguyễn Văn Test', @outstanding, 1000000) RETURNING id
            """, new { t = fx.Tenant, code, outstanding = employeeOutstanding ?? advance });
        var items = lines.Select((l, i) => new
        {
            category = l.Category, description = $"Dòng {i + 1}", amount = l.Gross, vat_amount = l.Vat,
            vendor_mst = l.Invoice ? $"030{i}{Random.Shared.Next(100000, 999999)}0" : null,
            invoice_no = l.Invoice ? $"{Random.Shared.Next(10000, 99999)}" : null,
            document_date = "2026-09-18",
        }).ToArray();
        var approved = lines.Sum(l => l.Gross);
        var vatDeductible = lines.Where(l => l.Invoice).Sum(l => l.Vat);
        var basis = lines.GroupBy(l => l.Category).Select(g => new RecognitionBasisLine(g.Key, g.Sum(l => l.Gross),
            g.Where(l => l.Invoice).Sum(l => l.Vat), g.Sum(l => l.Gross), g.Where(l => l.Invoice).Sum(l => l.Vat))).ToList();
        var voucher = await Scalar<Guid>("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, status, document_number, document_date, total_amount, extracted_fields)
            VALUES (@u, @t, @d, 'expense_claim', 'extracted', @doc, '2026-09-20', @approved, @fields::jsonb) RETURNING id
            """, new { u = claimant ?? fx.User, t = fx.Tenant, d = fx.Dept, doc = "DNTT-" + code, approved, fields = JsonSerializer.Serialize(new { items }) });
        var deptReviewed = stage is "dept_reviewed" or "recognized";
        var claim = await Scalar<Guid>("""
            INSERT INTO expense_claim_results(tenant_id, department_id, voucher_id, employee_id, outcome, review_status,
                requested_amount, approved_amount, advance_amount, payable_amount, vat_deductible_amount, requires_tax_verification,
                dept_reviewed_by, dept_reviewed_at, recognition_basis)
            VALUES (@t, @d, @voucher, @employee, @outcome, @status, @approved, @approved, @advance, @payable, @vat, @requiresTax,
                @deptBy, CASE WHEN @deptBy IS NULL THEN NULL ELSE NOW() END, @basis::jsonb)
            RETURNING id
            """, new { t = fx.Tenant, d = fx.Dept, voucher, employee, outcome, status = deptReviewed ? "dept_reviewed" : "proposed",
                       approved, advance, payable = approved - advance, vat = vatDeductible, requiresTax = lines.Any(l => l.Invoice),
                       deptBy = deptReviewed ? fx.Admin : (Guid?)null, basis = JsonSerializer.Serialize(basis) });
        if (verifyTax && lines.Any(l => l.Invoice))
        {
            await Tax().ConfirmAsync(fx.Tenant, fx.Dept, voucher, fx.Admin, new TaxConfirmRequest(
                TaxVerificationSources.TctPortal, "Đã tra cứu hoadondientu.gdt.gov.vn — hợp lệ", true,
                items.Where(i => i.invoice_no is not null).Select(i => new TaxAttestedInvoice(i.vendor_mst!, null, i.invoice_no!,
                    new DateOnly(2026, 9, 18), i.amount, "L")).ToList()), default);
        }
        var result = new Claim(voucher, claim, employee, lines);
        if (stage == "recognized") await RecognizeAsync(result);
        return result;
    }

    public Task<ExpenseClaimResult?> RecognizeAsync(Claim c, Guid? actor = null, Guid? dept = null) =>
        Claims().AccountingReviewAsync(fx.Tenant, dept ?? fx.Dept, c.Voucher, actor ?? fx.Admin2, default);

    public sealed record State(int Cash, int RecognitionLines, int PaymentLines, string Payment, string Review, string Recognition, decimal Advance, decimal Ytd);

    public async Task<State> StateAsync(Claim c)
    {
        await using var conn = new NpgsqlConnection(Cs);
        await conn.OpenAsync();
        return await conn.QuerySingleAsync<State>("""
            SELECT (SELECT COUNT(*)::int FROM cash_transactions WHERE source_expense_claim_id=@claim) AS Cash,
                   (SELECT COUNT(*)::int FROM voucher_journal_suggestions WHERE expense_claim_id=@claim AND source='posting') AS RecognitionLines,
                   (SELECT COUNT(*)::int FROM voucher_journal_suggestions WHERE voucher_id=@voucher AND source='posting' AND cash_transaction_id IS NOT NULL) AS PaymentLines,
                   r.payment_status AS Payment, r.review_status AS Review, r.recognition_status AS Recognition,
                   e.outstanding_advance AS Advance, e.ytd_expense AS Ytd
            FROM expense_claim_results r JOIN sim_employees e ON e.id=@employee WHERE r.id=@claim
            """, new { claim = c.ClaimId, voucher = c.Voucher, employee = c.Employee });
    }

    public async Task<List<(string Debit, string Credit, decimal Amount)>> RecognitionLinesAsync(Claim c)
    {
        await using var conn = new NpgsqlConnection(Cs);
        await conn.OpenAsync();
        return (await conn.QueryAsync<(string, string, decimal)>(
            "SELECT debit_account, credit_account, amount FROM voucher_journal_suggestions WHERE expense_claim_id=@id AND source='posting' ORDER BY entry_no",
            new { id = c.ClaimId })).ToList();
    }
}
