namespace Aiq.Gateway.Vouchers.DocumentProcessing;

// ============================================================================
// SYSTEM document types. The canonical code is the semantic identity of a
// document and names exactly one SERVER-OWNED handler (sales invoice, cash
// receipt, …) implemented in Gateway code. An Agent version may enable or
// disable a system type and tune how it is EXTRACTED (fields, instructions,
// workflow, skills) — it can never rename the code, replace the handler, or
// carry account numbers / Dr-Cr / posting logic. There is intentionally no
// field anywhere in the per-version configuration through which a handler
// could be supplied.
// ============================================================================

public static class DocumentFieldTypes
{
    public const string String = "string";
    public const string Decimal = "decimal";
    public const string Integer = "integer";
    public const string Date = "date";
    public const string Boolean = "boolean";
    public const string Array = "array";
    public const string Object = "object";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        String, Decimal, Integer, Date, Boolean, Array, Object,
    };
}

/// <summary>Named server-side handlers. Configuration may only REFERENCE a system type, whose handler is fixed here.</summary>
public static class AccountingHandlers
{
    public const string SalesInvoice = "sales_invoice";
    public const string PurchaseInvoice = "purchase_invoice";
    public const string CashReceipt = "cash_receipt";
    public const string CashPayment = "cash_payment";
    public const string BankTransferPayment = "bank_transfer_payment";
    public const string BankStatementImport = "bank_statement_import";
    public const string ExpenseClaim = "expense_claim";
    public const string ContractReference = "contract_reference";

    public static readonly IReadOnlySet<string> Registered = new HashSet<string>(StringComparer.Ordinal)
    {
        SalesInvoice, PurchaseInvoice, CashReceipt, CashPayment, BankTransferPayment,
        BankStatementImport, ExpenseClaim, ContractReference,
    };
}

public sealed record DocumentFieldDefinition(
    string Name, string Label, string Type, bool Required, string? Description);

public sealed record SystemDocumentType(
    string Code, string DisplayName, string AccountingHandler,
    IReadOnlyList<DocumentFieldDefinition> DefaultFields, string DefaultInstructions);

public static class DocumentTypeCatalog
{
    private static DocumentFieldDefinition F(string name, string label, string type = DocumentFieldTypes.String,
        bool required = false, string? description = null) => new(name, label, type, required, description);

    private static readonly DocumentFieldDefinition[] InvoiceCommon =
    {
        F("document_number", "Số hóa đơn", required: true),
        F("document_series", "Ký hiệu hóa đơn"),
        F("document_date", "Ngày hóa đơn", DocumentFieldTypes.Date),
        F("seller_name", "Tên người bán"),
        F("seller_mst", "MST người bán", description: "Đúng 10 hoặc 13 số như in trên hóa đơn"),
        F("seller_address", "Địa chỉ người bán"),
        F("buyer_name", "Tên người mua"),
        F("buyer_mst", "MST người mua", description: "Đúng 10 hoặc 13 số như in trên hóa đơn"),
        F("buyer_address", "Địa chỉ người mua"),
        F("payment_method", "Hình thức thanh toán", description: "CK / TM / MIXED"),
        F("payment_terms_days", "Số ngày nợ", DocumentFieldTypes.Integer, description: "0 nếu trả ngay"),
        F("subtotal", "Tiền trước thuế", DocumentFieldTypes.Decimal),
        F("vat_rate", "Thuế suất (%)", DocumentFieldTypes.Decimal),
        F("vat_amount", "Tiền thuế GTGT", DocumentFieldTypes.Decimal),
        F("total_amount", "Tổng thanh toán", DocumentFieldTypes.Decimal, required: true),
        F("currency", "Tiền tệ", description: "Mặc định VND"),
        F("line_items", "Dòng hàng hóa/dịch vụ", DocumentFieldTypes.Array,
            description: "Mỗi dòng: ordinal, description, unit, quantity, unit_price, subtotal, vat_rate, vat_amount, total_amount"),
    };

    private static readonly DocumentFieldDefinition[] CashCommon =
    {
        F("document_number", "Số chứng từ"),
        F("document_date", "Ngày chứng từ", DocumentFieldTypes.Date),
        F("payer_name", "Bên nộp/chi tiền"),
        F("payer_mst", "MST bên nộp/chi tiền"),
        F("payee_name", "Bên nhận tiền"),
        F("payee_mst", "MST bên nhận tiền"),
        F("reason", "Lý do"),
        F("invoice_number", "Số hóa đơn được thanh toán", description: "Chỉ khi có trên chứng từ"),
        F("contract_number", "Số hợp đồng", description: "Chỉ khi có trên chứng từ"),
        F("amount", "Số tiền", DocumentFieldTypes.Decimal, required: true),
        F("currency", "Tiền tệ"),
        F("payment_method", "Hình thức", description: "TM (tiền mặt) / CK (chuyển khoản)"),
        F("bank_account", "Tài khoản ngân hàng"),
    };

    public static readonly IReadOnlyList<SystemDocumentType> All = new[]
    {
        new SystemDocumentType(VoucherTypes.InvoiceOut, "Hóa đơn bán", AccountingHandlers.SalesInvoice,
            InvoiceCommon,
            "Công ty mình là BÊN BÁN. Ghi đúng số liệu in trên hóa đơn; không tự suy ra MST."),
        new SystemDocumentType(VoucherTypes.InvoiceIn, "Hóa đơn mua", AccountingHandlers.PurchaseInvoice,
            InvoiceCommon.Append(F("expense_category", "Nhóm chi phí", description: "material / office / travel / marketing / service")).ToArray(),
            "Công ty mình là BÊN MUA. Ghi đúng số liệu in trên hóa đơn; không tự suy ra MST."),
        new SystemDocumentType(VoucherTypes.Receipt, "Phiếu thu", AccountingHandlers.CashReceipt,
            CashCommon.Append(F("payer_type", "Loại bên nộp", description: "customer / employee / other")).ToArray(),
            "Chỉ ghi lại đúng những gì có trên chứng từ — KHÔNG tự suy ra MST hay số hóa đơn. Hệ thống tự xác định công ty mình theo Hồ sơ công ty."),
        new SystemDocumentType(VoucherTypes.Payment, "Phiếu chi", AccountingHandlers.CashPayment,
            CashCommon.Append(F("payee_type", "Loại bên nhận", description: "supplier / employee / other")).ToArray(),
            "payee_* là bên NHẬN tiền, payer_* là bên CHI. Chỉ ghi đúng những gì có trên chứng từ."),
        new SystemDocumentType(VoucherTypes.BankTransfer, "Ủy nhiệm chi", AccountingHandlers.BankTransferPayment,
            new[]
            {
                F("document_number", "Số UNC"),
                F("document_date", "Ngày", DocumentFieldTypes.Date),
                F("from_account", "Tài khoản trả"),
                F("to_account", "Tài khoản nhận"),
                F("beneficiary_name", "Đơn vị thụ hưởng"),
                F("beneficiary_mst", "MST thụ hưởng"),
                F("amount", "Số tiền", DocumentFieldTypes.Decimal, required: true),
                F("currency", "Tiền tệ"),
                F("content", "Nội dung"),
                F("invoice_number", "Số hóa đơn trong nội dung"),
                F("fee", "Phí ngân hàng", DocumentFieldTypes.Decimal),
                F("fee_bearer", "Bên chịu phí", description: "sender / receiver"),
            },
            "Ghi đúng tài khoản, đơn vị thụ hưởng và số tiền như trên ủy nhiệm chi."),
        new SystemDocumentType(VoucherTypes.BankStatement, "Sao kê ngân hàng", AccountingHandlers.BankStatementImport,
            new[]
            {
                F("account_number", "Số tài khoản"),
                F("bank_name", "Ngân hàng"),
                F("period_from", "Từ ngày", DocumentFieldTypes.Date),
                F("period_to", "Đến ngày", DocumentFieldTypes.Date),
                F("opening_balance", "Số dư đầu kỳ", DocumentFieldTypes.Decimal),
                F("closing_balance", "Số dư cuối kỳ", DocumentFieldTypes.Decimal),
                F("transactions", "Các dòng giao dịch", DocumentFieldTypes.Array,
                    description: "Mỗi dòng: date, description, debit, credit, balance, counterparty_name, counterparty_account"),
            },
            "Sao kê là NHIỀU giao dịch — liệt kê từng dòng, không gộp thành một bút toán."),
        new SystemDocumentType(VoucherTypes.ExpenseClaim, "Đề nghị hoàn ứng", AccountingHandlers.ExpenseClaim,
            new[]
            {
                F("document_number", "Số đề nghị"),
                F("document_date", "Ngày", DocumentFieldTypes.Date),
                F("employee_name", "Nhân viên"),
                F("employee_code", "Mã nhân viên"),
                F("department", "Bộ phận"),
                F("purpose", "Mục đích"),
                F("trip_from", "Từ ngày", DocumentFieldTypes.Date),
                F("trip_to", "Đến ngày", DocumentFieldTypes.Date),
                F("items", "Các khoản chi", DocumentFieldTypes.Array,
                    description: "Mỗi khoản: category, description, amount, receipt_attached, vendor_mst, invoice_no, document_date, vat_amount"),
                F("total_claim", "Tổng đề nghị", DocumentFieldTypes.Decimal, required: true),
                F("advance_taken", "Đã tạm ứng", DocumentFieldTypes.Decimal),
                F("reimbursement_needed", "Cần hoàn", DocumentFieldTypes.Decimal),
            },
            "vat_amount chỉ ghi khi hóa đơn tách VAT; không tự suy ra bằng % cố định."),
        new SystemDocumentType(VoucherTypes.Contract, "Hợp đồng", AccountingHandlers.ContractReference,
            new[]
            {
                F("contract_number", "Số hợp đồng"),
                F("contract_date", "Ngày ký", DocumentFieldTypes.Date),
                F("party_a", "Bên A"),
                F("party_b", "Bên B"),
                F("party_b_mst", "MST bên B"),
                F("contract_value", "Giá trị hợp đồng", DocumentFieldTypes.Decimal),
                F("currency", "Tiền tệ"),
                F("payment_schedule", "Lịch thanh toán", DocumentFieldTypes.Array,
                    description: "Mỗi đợt: milestone, percentage, amount, expected_date"),
                F("accounting_notes", "Ghi chú kế toán", DocumentFieldTypes.Object),
            },
            "Hợp đồng không phát sinh bút toán riêng — block 2 trả []."),
    };

    private static readonly Dictionary<string, SystemDocumentType> ByCode =
        All.ToDictionary(t => t.Code, StringComparer.Ordinal);

    public static SystemDocumentType? Find(string? code) =>
        code is not null && ByCode.TryGetValue(code, out var t) ? t : null;
}
