// Dedicated, repeatable demo-data seed for the "Sáu Trực" tenant's newly
// created Accounting Agent (Trợ lý kế toán). Resolves its target tenant
// from tenant_company_profiles (real data — never a hard-coded tenant id),
// and writes only through the SAME repository classes the Gateway API uses
// (ICustomerRepository/ISupplierRepository/IVoucherRepository/
// IContractRepository) — so every row created here is indistinguishable
// from one created through the real product, and every business-rule side
// effect (current_ar/current_ap recompute, paid_amount/status transitions,
// voucher natural-key upsert) runs exactly as it would in production.
//
// Idempotent: every entity is looked up by its real natural key
// (tax code / voucher natural key / contract title) before creating, and
// AR/AP booking + payment application are idempotent by design in the
// repository layer itself (voucher_id uniqueness). Safe to re-run.
//
// Marker: every demo customer/supplier code is prefixed "DEMO-", every
// demo voucher document_number is prefixed "DEMO-", and every demo
// voucher/contract carries the tag "sau-truc-accounting-demo" — all via
// real, already-exposed repository fields (Notes/Tags), no schema changes,
// no raw metadata columns invented.
//
// Run (from repo root):
//   docker run --rm --network aiq-agentic_aiq-net \
//     -v "$(pwd)":/src -v "$(pwd)/scripts/seed-demo-accounting":/seed -w /seed \
//     -e POSTGRES_HOST=postgres -e POSTGRES_PASSWORD='Nqt@1234' \
//     -e POSTGRES_USER=aiq -e POSTGRES_DB=aiq \
//     mcr.microsoft.com/dotnet/sdk:9.0 dotnet run

using Aiq.Gateway.Contracts;
using Aiq.Gateway.Customers;
using Aiq.Gateway.Data;
using Aiq.Gateway.Suppliers;
using Aiq.Gateway.Vouchers;
using Dapper;
using Npgsql;

const string DemoTag = "sau-truc-accounting-demo";
const string TargetTaxCode = "0123456789"; // from the given Company Profile — never a hard-coded tenant id

Dapper.SqlMapper.AddTypeHandler(new StringArrayTypeHandler());
Dapper.SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
Dapper.SqlMapper.AddTypeHandler(new NullableDateOnlyTypeHandler());

var connString = new NpgsqlConnectionStringBuilder
{
    Host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "postgres",
    Port = 5432,
    Database = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "aiq",
    Username = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "aiq",
    Password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD"),
}.ConnectionString;

var factory = new NpgsqlConnectionFactory(connString);
var customers = new CustomerRepository(factory);
var suppliers = new SupplierRepository(factory);
var vouchers = new VoucherRepository(factory);
var contracts = new ContractRepository(factory);

// ---------------------------------------------------------------------
// 1. Resolve tenant/department/actor from REAL existing data
// ---------------------------------------------------------------------
await using var conn = await factory.OpenAsync(CancellationToken.None);

var tenantRow = await conn.QuerySingleOrDefaultAsync<(Guid TenantId, string LegalName, string ShortName)?>(
    "SELECT tenant_id AS TenantId, legal_name AS LegalName, short_name AS ShortName " +
    "FROM tenant_company_profiles WHERE tax_code = @TargetTaxCode",
    new { TargetTaxCode });
if (tenantRow is null)
{
    Console.Error.WriteLine($"No tenant_company_profiles row for tax_code={TargetTaxCode}. Aborting — refusing to guess a tenant.");
    return 1;
}
var (tenantId, legalName, shortName) = tenantRow.Value;

var departmentId = await conn.ExecuteScalarAsync<Guid?>("""
    SELECT id FROM departments WHERE tenant_id=@tenantId AND status='active'
    ORDER BY is_default DESC, created_at ASC LIMIT 1
    """, new { tenantId });
if (departmentId is null)
{
    Console.Error.WriteLine($"Tenant {tenantId} ({shortName}) has no active department. Aborting.");
    return 1;
}

var actorId = await conn.ExecuteScalarAsync<Guid?>("""
    SELECT u.id FROM users u
    JOIN user_roles ur ON ur.user_id = u.id JOIN roles r ON r.id = ur.role_id
    WHERE u.tenant_id=@tenantId AND u.department_id=@departmentId AND u.is_active
      AND r.name IN ('admin', 'tenant_owner')
    ORDER BY (r.name = 'admin') DESC LIMIT 1
    """, new { tenantId, departmentId });
if (actorId is null)
{
    Console.Error.WriteLine($"Tenant {tenantId} has no active admin/tenant_owner to attribute demo records to. Aborting.");
    return 1;
}

Console.WriteLine($"Target tenant: {legalName} ({shortName}) — {tenantId}");
Console.WriteLine($"Target department: {departmentId}");
Console.WriteLine($"Actor (created_by): {actorId}");
Console.WriteLine();

var today = DateOnly.FromDateTime(DateTime.UtcNow);

// ---------------------------------------------------------------------
// 2. Customers
// ---------------------------------------------------------------------
async Task<CustomerRecord> UpsertCustomerAsync(string code, string name, string mst, string address, string phone)
{
    var existing = await customers.FindByMstAsync(tenantId, departmentId.Value, mst, CancellationToken.None);
    if (existing is not null)
    {
        Console.WriteLine($"  customer {code} already exists ({existing.Id}) — skipping create");
        return existing;
    }
    var (created, _) = await customers.CreateAsync(tenantId, departmentId.Value, actorId.Value, new CreateCustomerRequest(
        code, name, mst, address, phone, null, "cong_ty", "company", null, 3_000_000_000m, 30,
        $"Demo seed [{DemoTag}] — dữ liệu minh hoạ cho Trợ lý kế toán, không phải giao dịch thật."),
        CancellationToken.None);
    if (created is null)
    {
        // Hard conflict on customer_code/mst from a concurrent/prior partial
        // run — re-fetch by mst rather than fail the whole seed.
        created = await customers.FindByMstAsync(tenantId, departmentId.Value, mst, CancellationToken.None)
            ?? throw new InvalidOperationException($"Customer {code}/{mst} conflicted but could not be re-resolved.");
    }
    Console.WriteLine($"  customer {code} created ({created.Id})");
    return created;
}

Console.WriteLine("Customers:");
var vietTin = await UpsertCustomerAsync("DEMO-CUS-VIETTIN", "Công ty TNHH Việt Tín", "0307890123",
    "33 Bà Triệu, Hà Nội", "024-3944-5566");
var minhLong = await UpsertCustomerAsync("DEMO-CUS-MINHLONG", "Công ty TNHH Minh Long", "0315550001",
    "88 Nguyễn Trãi, Đà Nẵng", "0236-3900-1122");
Console.WriteLine();

// ---------------------------------------------------------------------
// 3. Suppliers
// ---------------------------------------------------------------------
async Task<SupplierRecord> UpsertSupplierAsync(string code, string name, string mst, string address, string phone)
{
    var existing = await suppliers.FindByMstAsync(tenantId, departmentId.Value, mst, CancellationToken.None);
    if (existing is not null)
    {
        Console.WriteLine($"  supplier {code} already exists ({existing.Id}) — skipping create");
        return existing;
    }
    var (created, _) = await suppliers.CreateAsync(tenantId, departmentId.Value, actorId.Value, new CreateSupplierRequest(
        code, name, mst, "VN", "VND", "Net 30", 30, "company", null, phone, null, address,
        $"Demo seed [{DemoTag}] — dữ liệu minh hoạ cho Trợ lý kế toán, không phải giao dịch thật."),
        CancellationToken.None);
    if (created is null)
    {
        created = await suppliers.FindByMstAsync(tenantId, departmentId.Value, mst, CancellationToken.None)
            ?? throw new InvalidOperationException($"Supplier {code}/{mst} conflicted but could not be re-resolved.");
    }
    Console.WriteLine($"  supplier {code} created ({created.Id})");
    return created;
}

Console.WriteLine("Suppliers:");
var daiPhat = await UpsertSupplierAsync("DEMO-SUP-DAIPHAT", "Công ty TNHH Cung ứng Đại Phát", "0200000001",
    "88 Điện Biên Phủ, TP.HCM", "028-3777-0011");
var kimLong = await UpsertSupplierAsync("DEMO-SUP-KIMLONG", "Công ty CP Vật tư Kim Long", "0200000002",
    "17 Kim Mã, Hà Nội", "024-3955-1122");
Console.WriteLine();

// ---------------------------------------------------------------------
// 4. Sales invoices (AR) — Sáu Trực is seller
// ---------------------------------------------------------------------
async Task<Guid> UpsertSalesInvoiceVoucherAsync(string docNumber, DateOnly docDate, CustomerRecord customer, decimal total)
{
    var subtotal = Math.Round(total / 1.10m, 0);
    var vat = total - subtotal;
    var detail = await vouchers.CreateAsync(tenantId, departmentId.Value, actorId.Value, new CreateVoucherRequest(
        VoucherType: "invoice_vat_out",
        DocumentNumber: docNumber,
        DocumentSeries: "AA/26E",
        DocumentDate: docDate,
        SellerName: legalName,
        SellerMst: TargetTaxCode,
        SellerAddress: "Lạc An, Ninh Thọ",
        BuyerName: customer.Name,
        BuyerMst: customer.Mst,
        BuyerAddress: customer.Address,
        PaymentMethod: "Chuyển khoản",
        Subtotal: subtotal,
        VatRate: 10,
        VatAmount: vat,
        DiscountAmount: 0,
        TotalAmount: total,
        Currency: "VND",
        AmountInWords: null,
        SourceFilePath: null,
        SourceFileName: null,
        SourceMimeType: null,
        RawOcrText: null,
        OcrConfidence: null,
        ExtractedFields: null,
        Tags: [DemoTag, "sales"],
        LineItems: null),
        CancellationToken.None);
    return detail.Voucher.Id;
}

Console.WriteLine("Sales invoices (AR):");

// Việt Tín — INV-001: overdue AND fully unpaid (due 20 days ago)
var vtInv1Due = today.AddDays(-20);
var vtInv1Date = vtInv1Due.AddDays(-30);
var vtInv1VoucherId = await UpsertSalesInvoiceVoucherAsync("DEMO-INV-VT-001", vtInv1Date, vietTin, 85_000_000m);
var vtInv1Book = await customers.BookInvoiceAsync(tenantId, vietTin.Id, vtInv1VoucherId, "DEMO-INV-VT-001", vtInv1Date, vtInv1Due, 85_000_000m, null, null, CancellationToken.None);
Console.WriteLine($"  DEMO-INV-VT-001 (Việt Tín, quá hạn, chưa thu): due {vtInv1Due}, total 85,000,000, ledger rows inserted={vtInv1Book.LedgerRowsInserted}");

// Việt Tín — INV-002: due within next 30 days, unpaid
var vtInv2Due = today.AddDays(17);
var vtInv2Date = today.AddDays(-8);
var vtInv2VoucherId = await UpsertSalesInvoiceVoucherAsync("DEMO-INV-VT-002", vtInv2Date, vietTin, 42_000_000m);
var vtInv2Book = await customers.BookInvoiceAsync(tenantId, vietTin.Id, vtInv2VoucherId, "DEMO-INV-VT-002", vtInv2Date, vtInv2Due, 42_000_000m, null, null, CancellationToken.None);
Console.WriteLine($"  DEMO-INV-VT-002 (Việt Tín, sắp đến hạn): due {vtInv2Due}, total 42,000,000, ledger rows inserted={vtInv2Book.LedgerRowsInserted}");

// Minh Long — INV-001: partially paid
var mlInvDue = today.AddDays(12);
var mlInvDate = today.AddDays(-22);
var mlInvVoucherId = await UpsertSalesInvoiceVoucherAsync("DEMO-INV-ML-001", mlInvDate, minhLong, 60_000_000m);
var mlInvBook = await customers.BookInvoiceAsync(tenantId, minhLong.Id, mlInvVoucherId, "DEMO-INV-ML-001", mlInvDate, mlInvDue, 60_000_000m, null, null, CancellationToken.None);
Console.WriteLine($"  DEMO-INV-ML-001 (Minh Long, thanh toán một phần): due {mlInvDue}, total 60,000,000, ledger rows inserted={mlInvBook.LedgerRowsInserted}");

// Receipt: Minh Long pays 30,000,000 of the 60,000,000 invoice.
// SellerMst MUST be set (to our own tax code — we are the one being paid)
// for CreateAsync's natural-key upsert (tenant_id, seller_mst,
// document_number, document_date) to find/reuse the SAME voucher row on
// re-run — that partial unique index only applies when seller_mst IS NOT
// NULL. Leaving it null would mint a brand-new voucher id every re-run and
// silently DOUBLE-APPLY the receipt (ApplyReceiptAsync is idempotent only
// by voucherId, and a new id is never "the same" application).
var mlReceiptDetail = await vouchers.CreateAsync(tenantId, departmentId.Value, actorId.Value, new CreateVoucherRequest(
    VoucherType: "receipt", DocumentNumber: "DEMO-RCPT-ML-001", DocumentSeries: null,
    DocumentDate: today.AddDays(-5), SellerName: legalName, SellerMst: TargetTaxCode, SellerAddress: "Lạc An, Ninh Thọ",
    BuyerName: minhLong.Name, BuyerMst: minhLong.Mst, BuyerAddress: null,
    PaymentMethod: "Chuyển khoản", Subtotal: null, VatRate: null, VatAmount: null, DiscountAmount: null,
    TotalAmount: 30_000_000m, Currency: "VND", AmountInWords: null,
    SourceFilePath: null, SourceFileName: null, SourceMimeType: null, RawOcrText: null, OcrConfidence: null,
    ExtractedFields: null, Tags: [DemoTag, "sales"], LineItems: null), CancellationToken.None);
var mlReceiptApply = await customers.ApplyReceiptAsync(tenantId, minhLong.Id, mlReceiptDetail.Voucher.Id,
    "DEMO-RCPT-ML-001", today.AddDays(-5),
    [new ReceiptApplicationInput("DEMO-INV-ML-001", 30_000_000m)], 0, null, CancellationToken.None);
Console.WriteLine($"  DEMO-RCPT-ML-001 applied to DEMO-INV-ML-001: {mlReceiptApply.TotalApplied:N0} VND (rows inserted={mlReceiptApply.LedgerRowsInserted})");
Console.WriteLine();

// ---------------------------------------------------------------------
// 5. Purchase invoices (AP) — Sáu Trực is buyer
// ---------------------------------------------------------------------
async Task<Guid> UpsertPurchaseInvoiceVoucherAsync(string docNumber, DateOnly docDate, SupplierRecord supplier, decimal total)
{
    var subtotal = Math.Round(total / 1.10m, 0);
    var vat = total - subtotal;
    var detail = await vouchers.CreateAsync(tenantId, departmentId.Value, actorId.Value, new CreateVoucherRequest(
        VoucherType: "invoice_vat_in",
        DocumentNumber: docNumber,
        DocumentSeries: "BB/26E",
        DocumentDate: docDate,
        SellerName: supplier.Name,
        SellerMst: supplier.Mst,
        SellerAddress: supplier.Address,
        BuyerName: legalName,
        BuyerMst: TargetTaxCode,
        BuyerAddress: "Lạc An, Ninh Thọ",
        PaymentMethod: "Chuyển khoản",
        Subtotal: subtotal,
        VatRate: 10,
        VatAmount: vat,
        DiscountAmount: 0,
        TotalAmount: total,
        Currency: "VND",
        AmountInWords: null,
        SourceFilePath: null,
        SourceFileName: null,
        SourceMimeType: null,
        RawOcrText: null,
        OcrConfidence: null,
        ExtractedFields: null,
        Tags: [DemoTag, "purchase"],
        LineItems: null),
        CancellationToken.None);
    return detail.Voucher.Id;
}

Console.WriteLine("Purchase invoices (AP):");

// Đại Phát — INV-001: overdue, fully unpaid
var dpInv1Due = today.AddDays(-19);
var dpInv1Date = dpInv1Due.AddDays(-30);
var dpInv1VoucherId = await UpsertPurchaseInvoiceVoucherAsync("DEMO-PINV-DP-001", dpInv1Date, daiPhat, 95_000_000m);
var dpInv1Book = await suppliers.BookInvoiceAsync(tenantId, daiPhat.Id, dpInv1VoucherId, "DEMO-PINV-DP-001", dpInv1Date, dpInv1Due, 95_000_000m, null, null, CancellationToken.None);
Console.WriteLine($"  DEMO-PINV-DP-001 (Đại Phát, quá hạn, chưa trả): due {dpInv1Due}, total 95,000,000, ledger rows inserted={dpInv1Book.LedgerRowsInserted}");

// Đại Phát — INV-002: not yet due, unpaid
var dpInv2Due = today.AddDays(25);
var dpInv2Date = today.AddDays(-5);
var dpInv2VoucherId = await UpsertPurchaseInvoiceVoucherAsync("DEMO-PINV-DP-002", dpInv2Date, daiPhat, 30_000_000m);
var dpInv2Book = await suppliers.BookInvoiceAsync(tenantId, daiPhat.Id, dpInv2VoucherId, "DEMO-PINV-DP-002", dpInv2Date, dpInv2Due, 30_000_000m, null, null, CancellationToken.None);
Console.WriteLine($"  DEMO-PINV-DP-002 (Đại Phát, chưa đến hạn): due {dpInv2Due}, total 30,000,000, ledger rows inserted={dpInv2Book.LedgerRowsInserted}");

// Kim Long — INV-001: due within 30 days, partially paid
var klInvDue = today.AddDays(15);
var klInvDate = today.AddDays(-15);
var klInvVoucherId = await UpsertPurchaseInvoiceVoucherAsync("DEMO-PINV-KL-001", klInvDate, kimLong, 54_000_000m);
var klInvBook = await suppliers.BookInvoiceAsync(tenantId, kimLong.Id, klInvVoucherId, "DEMO-PINV-KL-001", klInvDate, klInvDue, 54_000_000m, null, null, CancellationToken.None);
Console.WriteLine($"  DEMO-PINV-KL-001 (Kim Long, sắp đến hạn, trả một phần): due {klInvDue}, total 54,000,000, ledger rows inserted={klInvBook.LedgerRowsInserted}");

// Payment: pay 20,000,000 of the 54,000,000 Kim Long invoice
var klPaymentDetail = await vouchers.CreateAsync(tenantId, departmentId.Value, actorId.Value, new CreateVoucherRequest(
    VoucherType: "payment", DocumentNumber: "DEMO-PMT-KL-001", DocumentSeries: null,
    DocumentDate: today.AddDays(-3), SellerName: kimLong.Name, SellerMst: kimLong.Mst, SellerAddress: null,
    BuyerName: legalName, BuyerMst: TargetTaxCode, BuyerAddress: null,
    PaymentMethod: "Chuyển khoản", Subtotal: null, VatRate: null, VatAmount: null, DiscountAmount: null,
    TotalAmount: 20_000_000m, Currency: "VND", AmountInWords: null,
    SourceFilePath: null, SourceFileName: null, SourceMimeType: null, RawOcrText: null, OcrConfidence: null,
    ExtractedFields: null, Tags: [DemoTag, "purchase"], LineItems: null), CancellationToken.None);
var klPaymentApply = await suppliers.ApplyExpenseAsync(tenantId, kimLong.Id, klPaymentDetail.Voucher.Id,
    "DEMO-PMT-KL-001", today.AddDays(-3),
    [new ExpenseApplicationInput("DEMO-PINV-KL-001", 20_000_000m)], 0, null, CancellationToken.None);
Console.WriteLine($"  DEMO-PMT-KL-001 applied to DEMO-PINV-KL-001: {klPaymentApply.TotalApplied:N0} VND (rows inserted={klPaymentApply.LedgerRowsInserted})");
Console.WriteLine();

// ---------------------------------------------------------------------
// 6. Contracts — 1 sales (Sáu Trực = seller), 1 purchase (Sáu Trực = buyer)
//    No repository method creates contract_parties rows (see
//    seed_business_samples.sql precedent) — guarded WHERE NOT EXISTS
//    insert, the same idempotency technique that existing script uses for
//    the same real gap.
// ---------------------------------------------------------------------
async Task<Guid> UpsertContractAsync(string title, string partyA, string partyB, decimal totalValue, DateOnly signedDate)
{
    var existingId = await conn.ExecuteScalarAsync<Guid?>(
        "SELECT id FROM contracts WHERE tenant_id=@tenantId AND title=@title", new { tenantId, title });
    if (existingId is { } id)
    {
        Console.WriteLine($"  contract '{title}' already exists ({id}) — skipping create");
        return id;
    }
    var created = await contracts.CreateAsync(tenantId, departmentId.Value, actorId.Value, new CreateContractRequest(
        Title: title, PartyA: partyA, PartyB: partyB,
        SignedDate: signedDate, EffectiveDate: signedDate, EndDate: signedDate.AddMonths(12),
        TotalValue: totalValue, Currency: "VND", SourcePdfPath: null, RawText: null,
        Tags: [DemoTag], Clauses: null), CancellationToken.None);
    Console.WriteLine($"  contract '{title}' created ({created.Contract.Id})");
    return created.Contract.Id;
}

async Task EnsurePartyAsync(Guid contractId, string role, string name, string? taxCode, string? address, string? representative)
{
    var exists = await conn.ExecuteScalarAsync<bool>(
        "SELECT EXISTS(SELECT 1 FROM contract_parties WHERE contract_id=@contractId AND party_role=@role)",
        new { contractId, role });
    if (exists) return;
    await conn.ExecuteAsync(
        "INSERT INTO contract_parties (contract_id, tenant_id, department_id, party_role, name, tax_code, address, representative) " +
        "VALUES (@contractId, @tenantId, @departmentId, @role, @name, @taxCode, @address, @representative)",
        new { contractId, tenantId, departmentId, role, name, taxCode, address, representative });
}

Console.WriteLine("Contracts:");
var salesContractId = await UpsertContractAsync(
    $"Hợp đồng cung cấp dịch vụ {shortName} - Việt Tín [{DemoTag}]",
    legalName, vietTin.Name, 150_000_000m, today.AddDays(-40));
await EnsurePartyAsync(salesContractId, "seller", legalName, TargetTaxCode, "Lạc An, Ninh Thọ", null);
await EnsurePartyAsync(salesContractId, "buyer", vietTin.Name, vietTin.Mst, vietTin.Address, null);

var purchaseContractId = await UpsertContractAsync(
    $"Hợp đồng mua vật tư {shortName} - Đại Phát [{DemoTag}]",
    legalName, daiPhat.Name, 95_000_000m, today.AddDays(-35));
await EnsurePartyAsync(purchaseContractId, "buyer", legalName, TargetTaxCode, "Lạc An, Ninh Thọ", null);
await EnsurePartyAsync(purchaseContractId, "seller", daiPhat.Name, daiPhat.Mst, daiPhat.Address, null);
Console.WriteLine();

// ---------------------------------------------------------------------
// 7. Verify — real balances after seeding
// ---------------------------------------------------------------------
Console.WriteLine("=== Verify: AR (customers) ===");
foreach (var c in new[] { vietTin, minhLong })
{
    var receivables = await customers.GetReceivablesAsync(tenantId, departmentId.Value, c.Id, CancellationToken.None);
    Console.WriteLine($"  {c.Name}: current_ar={receivables?.CurrentBalance131:N0} overdue={receivables?.OverdueAmount:N0} open_invoices={receivables?.OpenInvoices.Count}");
}

Console.WriteLine("=== Verify: AP (suppliers) ===");
foreach (var s in new[] { daiPhat, kimLong })
{
    var payables = await suppliers.GetPayablesAsync(tenantId, departmentId.Value, s.Id, CancellationToken.None);
    Console.WriteLine($"  {s.Name}: current_ap={payables?.CurrentBalance331:N0} overdue={payables?.OverdueAmount:N0} open_invoices={payables?.OpenInvoices.Count}");
}

var apSummary = await suppliers.GetApSummaryAsync(tenantId, departmentId.Value, CancellationToken.None);
Console.WriteLine($"=== AP summary: total={apSummary.TotalPayable:N0} overdue={apSummary.OverdueAmount:N0} due<=30d={apSummary.DueWithin30Days:N0} suppliers={apSummary.SupplierCount}");

Console.WriteLine();
Console.WriteLine("Seed complete.");
return 0;
