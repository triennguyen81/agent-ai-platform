using System.Text.Json;
using System.Text.RegularExpressions;
using Aiq.Gateway.Books;
using Aiq.Gateway.Customers;
using Aiq.Gateway.Employees;
using Aiq.Gateway.Suppliers;
using Aiq.Gateway.Vouchers;
using Hangfire;

namespace Aiq.Gateway.Jobs;

// UC-ACC-001 P1 (part 2) — LLM extraction + AR/AP enrichment + 3-way match
// all in Hangfire so Next.js promise lifecycle can't kill it mid-flight.
//
// Runs AFTER VoucherOcrJobRunner (chained via ContinueJobWith on the
// scheduler). If OCR ended in ocr_unavailable / rejected, this job
// short-circuits and does nothing.
public sealed class VoucherExtractJobRunner
{
    private readonly IAgentClient _agent;
    private readonly IVoucherRepository _vouchers;
    private readonly ICustomerRepository _customers;
    private readonly ISupplierRepository _suppliers;
    private readonly IEmployeeRepository _employees;
    private readonly ILogger<VoucherExtractJobRunner> _logger;

    public VoucherExtractJobRunner(
        IAgentClient agent,
        IVoucherRepository vouchers,
        ICustomerRepository customers,
        ISupplierRepository suppliers,
        IEmployeeRepository employees,
        ILogger<VoucherExtractJobRunner> logger)
    {
        _agent = agent;
        _vouchers = vouchers;
        _customers = customers;
        _suppliers = suppliers;
        _employees = employees;
        _logger = logger;
    }

    [JobDisplayName("Voucher extract · {0}")]
    [AutomaticRetry(Attempts = 2, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task RunAsync(
        Guid voucherId,
        Guid userId,
        Guid? tenantId,
        string voucherType,
        string userToken,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Voucher extract job start · id={VoucherId} type={Type}", voucherId, voucherType);

        var stub = await _vouchers.FindAsync(tenantId, userId, voucherId, ct);
        if (stub is null)
        {
            _logger.LogWarning("Voucher {VoucherId} not found — extract skipped", voucherId);
            return;
        }
        if (stub.Voucher.Status is "ocr_unavailable" or "rejected")
        {
            _logger.LogInformation(
                "Voucher {VoucherId} status={Status} — skipping extract", voucherId, stub.Voucher.Status);
            return;
        }
        var rawText = stub.Voucher.RawOcrText ?? "";
        if (string.IsNullOrWhiteSpace(rawText))
        {
            _logger.LogWarning("Voucher {VoucherId} has no rawOcrText — cannot extract", voucherId);
            return;
        }

        // Ask agent for 2 JSON blocks: fields object + entries array.
        var task = BuildExtractionPrompt(voucherType, rawText);
        string agentAnswer;
        try
        {
            agentAnswer = await _agent.RunAgentTaskAsync(
                task,
                userToken,
                skills: new[] { "voucher-invoice-vat", "accounting-vietnam", "journal-entry-formatter", "vietnamese-style" },
                maxTurns: 3,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent extract call failed for voucher {VoucherId}", voucherId);
            throw;
        }

        var (fields, entries) = ExtractJsonBlocks(agentAnswer);
        if (fields is null)
        {
            _logger.LogWarning(
                "Voucher {VoucherId}: agent returned no valid fields JSON — status stays ocr_complete. Response preview: {Preview}",
                voucherId,
                agentAnswer.Length > 800 ? agentAnswer[..800] : agentAnswer);
            return;
        }

        // Enrich context — tuỳ voucher_type gọi AR (customers/sim_ar_ledger)
        // hoặc AP (suppliers/sim_ap_ledger) hoặc expense (employees).
        //
        // voucher_type covered:
        //   receipt          → AR context + receipt_matching (FIFO oldest)
        //   invoice_vat_out  → AR context + 3-way SO match
        //   invoice_vat*     → AR (legacy: pre-universal-extractor tenants)
        //   invoice_vat_in   → AP context (seller = NCC)
        //   payment          → AP context + payment_matching (FIFO oldest)
        //   bank_transfer    → AP context + payment_matching (beneficiary = NCC)
        //   expense_claim    → expense_claim_matching (employee lookup)
        var documentNumber = ReadString(fields, "document_number");
        var vt = voucherType.ToLowerInvariant();

        if (tenantId is Guid t)
        {
            switch (vt)
            {
                case "receipt":
                {
                    var payerMst = ReadStringAny(fields, "payer_mst", "buyer_mst", "customer_mst");
                    var receiptAmount = ReadDecimalAny(fields, "amount", "total_amount", "so_tien") ?? 0m;
                    object? arContext = null;
                    if (!string.IsNullOrWhiteSpace(payerMst))
                    {
                        // Receipt không tăng AR — pass 0m làm projected delta.
                        arContext = await BuildArContextAsync(t, payerMst, 0m, ct);
                        if (arContext is not null) fields["ar_context"] = arContext;
                    }
                    if (arContext is not null && receiptAmount > 0m)
                    {
                        var matching = BuildReceiptMatching(arContext, receiptAmount);
                        if (matching is not null) fields["receipt_matching"] = matching;
                    }
                    break;
                }

                case "invoice_vat_out":
                case "invoice_vat":  // legacy tenants pre-universal-extractor
                {
                    var buyerMst = ReadString(fields, "buyer_mst");
                    var invoiceTotal = ReadDecimal(fields, "total_amount") ?? 0m;
                    if (!string.IsNullOrWhiteSpace(buyerMst))
                    {
                        var arContext = await BuildArContextAsync(t, buyerMst, invoiceTotal, ct);
                        if (arContext is not null) fields["ar_context"] = arContext;
                    }
                    if (!string.IsNullOrWhiteSpace(documentNumber))
                    {
                        var matchContext = await BuildMatchContextAsync(t, documentNumber, ct);
                        if (matchContext is not null) fields["match_context"] = matchContext;
                    }
                    break;
                }

                case "invoice_vat_in":
                {
                    var sellerMst = ReadStringAny(fields, "seller_mst", "supplier_mst");
                    var invoiceTotal = ReadDecimalAny(fields, "total_amount", "amount") ?? 0m;
                    var paymentMethod = ReadString(fields, "payment_method");
                    if (!string.IsNullOrWhiteSpace(sellerMst))
                    {
                        var apContext = await BuildApContextAsync(t, sellerMst, invoiceTotal, paymentMethod, ct);
                        if (apContext is not null) fields["ap_context"] = apContext;
                    }
                    // PO 3-way match (po_match_context) — Sprint 4.
                    break;
                }

                case "payment":
                case "bank_transfer":
                {
                    // Payment: TM/CK phiếu chi → payee_mst. Bank_transfer: UNC → beneficiary_mst.
                    var payeeMst = vt == "bank_transfer"
                        ? ReadStringAny(fields, "beneficiary_mst", "payee_mst", "supplier_mst", "seller_mst")
                        : ReadStringAny(fields, "payee_mst", "supplier_mst", "seller_mst");
                    var paymentAmount = ReadDecimalAny(fields, "amount", "total_amount", "so_tien") ?? 0m;
                    var paymentMethod = ReadString(fields, "payment_method");

                    object? apContext = null;
                    if (!string.IsNullOrWhiteSpace(payeeMst))
                    {
                        // Payment không tăng AP — pass 0m làm projected delta.
                        apContext = await BuildApContextAsync(t, payeeMst, 0m, paymentMethod, ct);
                        if (apContext is not null) fields["ap_context"] = apContext;
                    }
                    if (apContext is not null && paymentAmount > 0m)
                    {
                        var matching = BuildPaymentMatching(apContext, paymentAmount);
                        if (matching is not null) fields["payment_matching"] = matching;
                    }
                    break;
                }

                case "expense_claim":
                {
                    var employeeCode = ReadStringAny(fields, "employee_code", "employee_id", "ma_nv");
                    var totalClaim = ReadDecimalAny(fields, "total_claim", "total_amount", "amount", "so_tien") ?? 0m;
                    var advanceTaken = ReadDecimalAny(fields, "advance_taken", "tam_ung") ?? 0m;
                    if (!string.IsNullOrWhiteSpace(employeeCode))
                    {
                        var claim = await BuildExpenseClaimMatchingAsync(t, employeeCode, totalClaim, advanceTaken, ct);
                        if (claim is not null) fields["expense_claim_matching"] = claim;
                    }
                    break;
                }

                // Other types (bank_statement, contract, unknown) — no enrichment.
            }
        }

        // Convert fields object → CreateVoucherRequest for ApplyExtraction.
        var applyRequest = BuildApplyRequest(voucherType, fields, rawText);

        // Duplicate check (E2): if another voucher has same seller_mst + doc_number,
        // flip current to rejected instead of overwriting.
        if (!string.IsNullOrWhiteSpace(applyRequest.SellerMst) && !string.IsNullOrWhiteSpace(applyRequest.DocumentNumber))
        {
            var dup = await _vouchers.FindDuplicateAsync(
                tenantId, applyRequest.SellerMst, applyRequest.DocumentNumber, voucherId, ct);
            if (dup is not null)
            {
                await _vouchers.UpdateOcrResultAsync(
                    voucherId,
                    status: "rejected",
                    rawText: null,
                    ocrConfidence: null,
                    errorMessage: $"Trùng hoá đơn: {applyRequest.DocumentNumber} của MST {applyRequest.SellerMst} đã tồn tại (voucher {dup.Id}).",
                    ct);
                _logger.LogWarning(
                    "Voucher {VoucherId} rejected — duplicate of {DupId}", voucherId, dup.Id);
                return;
            }
        }

        await _vouchers.ReplaceExtractedAsync(voucherId, applyRequest, ct);

        // Save journal entries if agent produced valid ones.
        if (entries is not null && entries.Count > 0)
        {
            var normalized = NormalizeEntries(entries);
            if (normalized.Count > 0)
            {
                await _vouchers.SaveJournalSuggestionsAsync(
                    voucherId,
                    new SaveJournalSuggestionsRequest(normalized, ReplaceExisting: true),
                    ct);
            }
        }

        _logger.LogInformation(
            "Voucher extract job done · id={VoucherId} fields={FieldCount} entries={EntryCount}",
            voucherId, fields.Count, entries?.Count ?? 0);
    }

    // ------------------------------------------------------------------
    // Prompt construction (mirror of web-tier prompt so extraction behaves
    // identically whether it runs in web or gateway job).
    // ------------------------------------------------------------------
    private static string BuildExtractionPrompt(string voucherType, string rawText)
    {
        var truncated = rawText.Length > 20_000 ? rawText[..20_000] : rawText;
        return $$"""
Bạn là chuyên viên kế toán VN. Xử lý raw OCR text của 1 hoá đơn GTGT bên dưới.

**BLOCK 1** — Áp dụng skill `voucher-invoice-vat` để trích xuất fields.
Trả về JSON object có voucher_type="{{voucherType}}", document_number, document_series, document_date (YYYY-MM-DD), seller_name, seller_mst, seller_address, buyer_name, buyer_mst, buyer_address, payment_method (CK/TM/MIXED), subtotal, vat_rate, vat_amount, discount_amount, total_amount, currency, amount_in_words, line_items[] (mỗi item: ordinal, description, unit, quantity, unit_price, subtotal, vat_rate, vat_amount, total_amount), tags[].

**BLOCK 2** — Áp dụng skill `journal-entry-formatter` + `accounting-vietnam` để đề xuất bút toán N/C theo TT200.
Trả về JSON array các entries. Mỗi entry: {entry_no, debit_account, credit_account, amount, description, confidence}.
Quy tắc: Hoá đơn VAT đầu vào (mua) → Nợ 156/152/641/642 + Nợ 1331 / Có 331 (công nợ) hoặc 111/112 (trả ngay). Hoá đơn đầu ra (bán) → Nợ 131/111/112 / Có 511 + Có 3331.

**QUAN TRỌNG**: chỉ trả 2 code block JSON, KHÔNG kèm markdown khác:
```json
{ ...fields object... }
```
```json
[ ...entries array... ]
```

===== RAW OCR TEXT =====
{{truncated}}
===== END =====
""";
    }

    // ------------------------------------------------------------------
    // JSON block extraction (mirror of web-tier extractJsonBlocks).
    // ------------------------------------------------------------------
    private static readonly Regex FenceRegex = new(
        @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.Compiled);

    private static (Dictionary<string, object?>? Fields, List<Dictionary<string, object?>>? Entries) ExtractJsonBlocks(string text)
    {
        Dictionary<string, object?>? fields = null;
        List<Dictionary<string, object?>>? entries = null;

        var blocks = new List<string>();
        foreach (Match m in FenceRegex.Matches(text))
        {
            var raw = m.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(raw)) blocks.Add(raw);
        }

        // Fallback: no fenced blocks — greedy match on the largest {...} and
        // [...] structures. Mirrors the web-tier fallback for models that
        // reply with un-fenced JSON.
        if (blocks.Count == 0)
        {
            var greedyObj = Regex.Match(text, @"\{[\s\S]*\}", RegexOptions.Multiline);
            if (greedyObj.Success) blocks.Add(greedyObj.Value);
            var greedyArr = Regex.Match(text, @"\[[\s\S]*\]", RegexOptions.Multiline);
            if (greedyArr.Success) blocks.Add(greedyArr.Value);
        }

        foreach (var raw in blocks)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    var obj = JsonElementToDictionary(root);
                    if (obj.ContainsKey("voucher_type") || obj.ContainsKey("document_number") || obj.ContainsKey("seller_name") || obj.ContainsKey("buyer_mst"))
                    {
                        fields ??= obj;
                    }
                    else if (obj.TryGetValue("entries", out var maybeEntries) && maybeEntries is List<object?> list)
                    {
                        entries ??= list.OfType<Dictionary<string, object?>>().ToList();
                    }
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    var arr = JsonElementToList(root);
                    entries ??= arr.OfType<Dictionary<string, object?>>().ToList();
                }
            }
            catch (JsonException)
            {
                // Skip malformed block — try the next one.
            }
        }
        return (fields, entries);
    }

    private static Dictionary<string, object?> JsonElementToDictionary(JsonElement el)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in el.EnumerateObject())
        {
            dict[prop.Name] = JsonElementToObject(prop.Value);
        }
        return dict;
    }

    private static List<object?> JsonElementToList(JsonElement el)
    {
        var list = new List<object?>();
        foreach (var item in el.EnumerateArray())
        {
            list.Add(JsonElementToObject(item));
        }
        return list;
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var i) ? i : (object)el.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Object => JsonElementToDictionary(el),
        JsonValueKind.Array => JsonElementToList(el),
        _ => null,
    };

    // ------------------------------------------------------------------
    // Field readers
    // ------------------------------------------------------------------
    private static string? ReadString(Dictionary<string, object?> obj, string key)
        => obj.TryGetValue(key, out var v) && v is string s ? s.Trim() : null;

    private static decimal? ReadDecimal(Dictionary<string, object?> obj, string key)
    {
        if (!obj.TryGetValue(key, out var v) || v is null) return null;
        return v switch
        {
            decimal d => d,
            long i => i,
            int i => i,
            double dbl => (decimal)dbl,
            string s => ParseFlexibleDecimal(s),
            _ => null,
        };
    }

    // Agent may return numbers as VN-formatted strings like "15.000.000",
    // "15,000,000", "15 000 000" or "15tr". Strip separators + suffix
    // before parsing. Fails soft to null so caller can decide.
    private static decimal? ParseFlexibleDecimal(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();

        // Handle "1.5tr" / "1,5 triệu" — multiplier suffix.
        decimal multiplier = 1m;
        var lower = s.ToLowerInvariant();
        if (lower.EndsWith("tr") || lower.EndsWith("triệu") || lower.EndsWith("trieu"))
        {
            multiplier = 1_000_000m;
            s = Regex.Replace(s, @"\s*(tr|triệu|trieu)\s*$", "", RegexOptions.IgnoreCase);
        }
        else if (lower.EndsWith("k") || lower.EndsWith("nghìn") || lower.EndsWith("nghin"))
        {
            multiplier = 1_000m;
            s = Regex.Replace(s, @"\s*(k|nghìn|nghin)\s*$", "", RegexOptions.IgnoreCase);
        }
        else if (lower.EndsWith("tỷ") || lower.EndsWith("ty"))
        {
            multiplier = 1_000_000_000m;
            s = Regex.Replace(s, @"\s*(tỷ|ty)\s*$", "", RegexOptions.IgnoreCase);
        }

        // Strip currency symbols + spaces.
        s = Regex.Replace(s, @"[₫\s]|VND|VNĐ|USD|EUR|JPY", "", RegexOptions.IgnoreCase).Trim();
        if (s.Length == 0) return null;

        // Decide decimal separator by heuristics:
        //  - "1.234.567,89" → VN → drop dots, replace comma with dot
        //  - "1,234,567.89" → US → drop commas
        //  - "1234567.89"   → already US
        //  - "1234567,89"   → EU → replace comma with dot
        //  - "1234567"      → integer
        var hasDot = s.Contains('.');
        var hasComma = s.Contains(',');
        if (hasDot && hasComma)
        {
            var lastDot = s.LastIndexOf('.');
            var lastComma = s.LastIndexOf(',');
            if (lastDot > lastComma)
            {
                // US: 1,234,567.89 — drop commas
                s = s.Replace(",", "");
            }
            else
            {
                // VN/EU: 1.234.567,89 — drop dots, comma → dot
                s = s.Replace(".", "").Replace(",", ".");
            }
        }
        else if (hasComma)
        {
            // Could be thousands separator "1,234,567" OR EU decimal "1234,89".
            // Heuristic: if there are ≥ 2 commas OR the group after last comma
            // is exactly 3 digits, treat as thousands separator.
            var parts = s.Split(',');
            if (parts.Length > 2 || (parts.Length == 2 && parts[1].Length == 3))
            {
                s = s.Replace(",", "");
            }
            else
            {
                s = s.Replace(",", ".");
            }
        }
        else if (hasDot)
        {
            // "1.234.567" (VN thousands) vs "1234.89" (US decimal).
            var parts = s.Split('.');
            if (parts.Length > 2 || (parts.Length == 2 && parts[1].Length == 3))
            {
                s = s.Replace(".", "");
            }
        }

        return decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed * multiplier
            : null;
    }

    // Accept alternate keys the model sometimes returns.
    private static string? ReadStringAny(Dictionary<string, object?> obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            var v = ReadString(obj, key);
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    private static decimal? ReadDecimalAny(Dictionary<string, object?> obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            var v = ReadDecimal(obj, key);
            if (v is not null) return v;
        }
        return null;
    }

    private static DateOnly? ReadDate(Dictionary<string, object?> obj, string key)
    {
        var s = ReadString(obj, key);
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateOnly.TryParse(s, out var d) ? d : null;
    }

    // ------------------------------------------------------------------
    // Payload builders
    // ------------------------------------------------------------------
    private static CreateVoucherRequest BuildApplyRequest(
        string voucherType,
        Dictionary<string, object?> fields,
        string rawText)
    {
        var lineItems = fields.TryGetValue("line_items", out var liObj) && liObj is List<object?> liList
            ? liList.OfType<Dictionary<string, object?>>().Select((li, i) => new CreateVoucherLineItemDto(
                Ordinal: (int)(ReadDecimal(li, "ordinal") ?? i + 1),
                ItemCode: ReadString(li, "item_code"),
                Description: (ReadString(li, "description") ?? "").Length > 500
                    ? ReadString(li, "description")![..500]
                    : ReadString(li, "description") ?? "",
                Unit: ReadString(li, "unit"),
                Quantity: ReadDecimal(li, "quantity"),
                UnitPrice: ReadDecimal(li, "unit_price"),
                Subtotal: ReadDecimal(li, "subtotal"),
                VatRate: ReadDecimal(li, "vat_rate"),
                VatAmount: ReadDecimal(li, "vat_amount"),
                TotalAmount: ReadDecimal(li, "total_amount"))).ToList()
            : new List<CreateVoucherLineItemDto>();

        var tags = fields.TryGetValue("tags", out var tagsObj) && tagsObj is List<object?> tagsList
            ? tagsList.OfType<string>().ToArray()
            : Array.Empty<string>();

        return new CreateVoucherRequest(
            VoucherType: ReadString(fields, "voucher_type") ?? voucherType,
            DocumentNumber: ReadString(fields, "document_number"),
            DocumentSeries: ReadString(fields, "document_series"),
            DocumentDate: ReadDate(fields, "document_date"),
            SellerName: ReadString(fields, "seller_name"),
            SellerMst: ReadString(fields, "seller_mst"),
            SellerAddress: ReadString(fields, "seller_address"),
            BuyerName: ReadString(fields, "buyer_name"),
            BuyerMst: ReadString(fields, "buyer_mst"),
            BuyerAddress: ReadString(fields, "buyer_address"),
            PaymentMethod: ReadString(fields, "payment_method"),
            Subtotal: ReadDecimal(fields, "subtotal"),
            VatRate: ReadDecimal(fields, "vat_rate"),
            VatAmount: ReadDecimal(fields, "vat_amount"),
            DiscountAmount: ReadDecimal(fields, "discount_amount"),
            TotalAmount: ReadDecimal(fields, "total_amount"),
            Currency: ReadString(fields, "currency") ?? "VND",
            AmountInWords: ReadString(fields, "amount_in_words"),
            SourceFilePath: null,
            SourceFileName: null,
            SourceMimeType: null,
            RawOcrText: rawText,
            OcrConfidence: null,
            ExtractedFields: fields,
            Tags: tags,
            LineItems: lineItems);
    }

    private List<SaveJournalSuggestionDto> NormalizeEntries(List<Dictionary<string, object?>> entries)
    {
        var list = new List<SaveJournalSuggestionDto>();
        int idx = 0;
        foreach (var e in entries)
        {
            idx++;
            var debit = ReadStringAny(e, "debit_account", "debit", "tk_no", "no");
            var credit = ReadStringAny(e, "credit_account", "credit", "tk_co", "co");
            var amount = ReadDecimalAny(e, "amount", "value", "so_tien") ?? 0m;

            if (string.IsNullOrWhiteSpace(debit) || string.IsNullOrWhiteSpace(credit) || amount <= 0m)
            {
                // Log each drop so we can see agent output format quirks.
                _logger.LogWarning(
                    "Journal entry #{Idx} dropped: debit='{Debit}' credit='{Credit}' amount={Amount} raw={Raw}",
                    idx, debit ?? "?", credit ?? "?", amount,
                    System.Text.Json.JsonSerializer.Serialize(e));
                continue;
            }

            list.Add(new SaveJournalSuggestionDto(
                EntryNo: (int)(ReadDecimalAny(e, "entry_no", "no", "stt") ?? idx),
                DebitAccount: debit,
                CreditAccount: credit,
                Amount: amount,
                Description: ReadStringAny(e, "description", "desc", "dien_giai"),
                Confidence: ReadDecimalAny(e, "confidence")));
        }
        return list;
    }

    // ------------------------------------------------------------------
    // AR + 3-way match enrichment
    // ------------------------------------------------------------------
    private async Task<object?> BuildArContextAsync(Guid tenantId, string buyerMst, decimal invoiceTotal, CancellationToken ct)
    {
        try
        {
            var customer = await _customers.FindByMstAsync(tenantId, buyerMst, ct);
            if (customer is null)
            {
                return new { status = "customer_not_found", buyer_mst = buyerMst };
            }
            var report = await _customers.GetReceivablesAsync(tenantId, customer.Id, ct);
            if (report is null) return new { status = "ar_lookup_failed", customer };

            var projectedBalance = report.CurrentBalance131 + invoiceTotal;
            var projectedPct = report.CreditLimit > 0
                ? (int)Math.Round(projectedBalance / report.CreditLimit * 100m)
                : 0;
            var flags = new List<string>();
            if (invoiceTotal > 500_000_000m) flags.Add("invoice_over_500m_requires_l2_approval");
            if (report.Aging.Over90 > 0) flags.Add("has_overdue_90d");
            if (projectedBalance > report.CreditLimit && report.CreditLimit > 0) flags.Add("would_breach_credit_limit");
            else if (projectedPct >= 80) flags.Add("credit_used_gte_80pct");
            if (report.OverdueAmount > 0) flags.Add("has_overdue_amount");

            return new
            {
                status = "found",
                customer,
                current_balance_131 = report.CurrentBalance131,
                credit_limit = report.CreditLimit,
                credit_used_percent = report.CreditUsedPercent,
                projected_balance_131 = projectedBalance,
                projected_credit_used_percent = projectedPct,
                aging = report.Aging,
                overdue_amount = report.OverdueAmount,
                last_payment_date = report.LastPaymentDate,
                payment_term_default = report.PaymentTermDefault,
                open_invoices = report.OpenInvoices,
                risk_flags = flags,
                requires_l2_approval = flags.Contains("invoice_over_500m_requires_l2_approval")
                    || flags.Contains("would_breach_credit_limit")
                    || flags.Contains("has_overdue_90d"),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AR enrichment failed for MST {Mst}", buyerMst);
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Deterministic receipt matching (FIFO oldest-first) + discount detection.
    //
    // Runs *after* AR lookup so we have the customer's open invoices with
    // due dates. Applies the receipt amount to invoices in due-date order,
    // returning the per-invoice application breakdown plus a discount hint
    // when the receipt is slightly less than the target invoice (< 3%
    // shortfall on a fully-applied line → likely chiết khấu thanh toán sớm).
    //
    // No LLM call — the customer's open A/R is authoritative, so this is
    // a plain accounting rule with no variance.
    // ------------------------------------------------------------------
    private static object? BuildReceiptMatching(object arContext, decimal receiptAmount)
    {
        // arContext is the anonymous object produced by BuildArContextAsync.
        // We reflect over it (or serialize→parse) to get open_invoices.
        var json = System.Text.Json.JsonSerializer.Serialize(arContext);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }
        if (!doc.RootElement.TryGetProperty("open_invoices", out var openEl)
            || openEl.ValueKind != System.Text.Json.JsonValueKind.Array
            || openEl.GetArrayLength() == 0)
        {
            return new
            {
                status = "no_open_invoices",
                receipt_amount = receiptAmount,
                message = "Khách hàng không có hoá đơn chưa thu — thu vào 511/711 (không phải giảm 131).",
            };
        }

        // Snapshot to a mutable list ordered by due date (oldest first).
        var invoices = new List<(string DocumentNumber, DateOnly DocumentDate, DateOnly? DueDate, decimal Remaining)>();
        foreach (var item in openEl.EnumerateArray())
        {
            var docNum = item.TryGetProperty("DocumentNumber", out var dn) ? dn.GetString() ?? "?" : "?";
            var docDate = item.TryGetProperty("DocumentDate", out var dd) && dd.ValueKind == System.Text.Json.JsonValueKind.String
                ? DateOnly.Parse(dd.GetString()!) : DateOnly.MinValue;
            DateOnly? dueDate = null;
            if (item.TryGetProperty("DueDate", out var due) && due.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                dueDate = DateOnly.Parse(due.GetString()!);
            }
            var debit = item.TryGetProperty("Debit", out var deb) ? deb.GetDecimal() : 0m;
            if (debit > 0m)
            {
                invoices.Add((docNum, docDate, dueDate, debit));
            }
        }
        invoices.Sort((a, b) =>
        {
            var byDue = (a.DueDate ?? DateOnly.MaxValue).CompareTo(b.DueDate ?? DateOnly.MaxValue);
            return byDue != 0 ? byDue : a.DocumentDate.CompareTo(b.DocumentDate);
        });

        var applications = new List<object>();
        var remaining = receiptAmount;
        decimal totalApplied = 0m;
        decimal? discount = null;
        string? discountReason = null;
        foreach (var (docNum, docDate, dueDate, invRemaining) in invoices)
        {
            if (remaining <= 0m) break;
            decimal applied;
            decimal invAfter;
            var shortfall = invRemaining - remaining;
            var shortfallPct = invRemaining > 0m ? shortfall / invRemaining : 0m;

            // Chiết khấu thanh toán sớm heuristic: shortfall ≤ 3% AND
            // receipt at least covers 97% AND invoice not yet overdue.
            // Apply full invoice value + book discount as 5211 line.
            var withinDiscountWindow = dueDate is null || DateOnly.FromDateTime(DateTime.UtcNow) <= dueDate;
            if (shortfall > 0m && shortfallPct <= 0.03m && withinDiscountWindow && applications.Count == 0)
            {
                applied = invRemaining;
                invAfter = 0m;
                discount = shortfall;
                discountReason = $"Chiết khấu thanh toán sớm cho HĐ {docNum} ({shortfallPct:P1}).";
                remaining -= remaining;
                totalApplied += invRemaining;
            }
            else
            {
                applied = Math.Min(remaining, invRemaining);
                invAfter = invRemaining - applied;
                remaining -= applied;
                totalApplied += applied;
            }
            applications.Add(new
            {
                document_number = docNum,
                document_date = docDate,
                due_date = dueDate,
                invoice_remaining_before = invRemaining,
                applied,
                invoice_remaining_after = invAfter,
            });
        }

        // If receipt exceeds all open A/R, remainder goes to prepayment (131+).
        string status;
        string message;
        if (remaining > 0m)
        {
            status = "overpaid";
            message = $"Thu dư {remaining:N0} VNĐ — treo vào TK 3387 (KH trả trước) hoặc trả lại.";
        }
        else if (discount is not null)
        {
            status = "matched_with_discount";
            message = discountReason ?? "Áp có chiết khấu.";
        }
        else if (applications.Count == 1 && ((dynamic)applications[0]).invoice_remaining_after == 0m)
        {
            status = "matched_full";
            message = "Thu đủ 1 hoá đơn.";
        }
        else if (applications.Count > 1)
        {
            status = "matched_multiple";
            message = $"Áp cho {applications.Count} hoá đơn (thứ tự due-date).";
        }
        else
        {
            status = "matched_partial";
            message = "Thu 1 phần hoá đơn.";
        }

        return new
        {
            status,
            receipt_amount = receiptAmount,
            total_applied = totalApplied,
            remaining_overpayment = Math.Max(remaining, 0m),
            discount,
            discount_reason = discountReason,
            applications,
            message,
        };
    }

    private async Task<object?> BuildMatchContextAsync(Guid tenantId, string documentNumber, CancellationToken ct)
    {
        try
        {
            var match = await _customers.MatchInvoiceToSalesOrderAsync(tenantId, documentNumber, ct);
            if (match is null)
            {
                return new { status = "no_match", document_number = documentNumber };
            }
            return new
            {
                status = "found",
                quality = match.MatchQuality,
                sales_order = match.SalesOrder,
                delivery_note = match.DeliveryNote,
                contract = match.Contract,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "3-way match failed for doc {Doc}", documentNumber);
            return null;
        }
    }

    // ------------------------------------------------------------------
    // AP enrichment (mirror BuildArContextAsync).
    //
    // Đầu vào: seller_mst (invoice_vat_in) / beneficiary_mst (bank_transfer) /
    // payee_mst (payment). Trả về NCC + current_ap + aging + 5 risk flags.
    //
    // paymentMethod dùng để trigger TT78 flag khi HĐ đầu vào > 30M mà thanh
    // toán TM (không có chứng từ NH → không được khấu trừ VAT).
    // ------------------------------------------------------------------
    private async Task<object?> BuildApContextAsync(
        Guid tenantId, string supplierMst, decimal invoiceTotal, string? paymentMethod, CancellationToken ct)
    {
        try
        {
            var supplier = await _suppliers.FindByMstAsync(tenantId, supplierMst, ct);
            if (supplier is null)
            {
                return new { status = "supplier_not_found", supplier_mst = supplierMst };
            }
            var report = await _suppliers.GetPayablesAsync(tenantId, supplier.Id, ct);
            if (report is null) return new { status = "ap_lookup_failed", supplier };

            var projectedBalance = report.CurrentBalance331 + invoiceTotal;
            var flags = new List<string>();

            // TT78 điều 6: HĐ đầu vào > 30M thanh toán TM → không khấu trừ VAT.
            var isCash = paymentMethod is not null
                && (paymentMethod.Equals("TM", StringComparison.OrdinalIgnoreCase)
                    || paymentMethod.Contains("tiền mặt", StringComparison.OrdinalIgnoreCase)
                    || paymentMethod.Contains("cash", StringComparison.OrdinalIgnoreCase));
            if (invoiceTotal > 30_000_000m && isCash)
            {
                flags.Add("expense_over_30m_cash_payment");
            }

            if (invoiceTotal > 500_000_000m) flags.Add("expense_over_500m_requires_l2_approval");
            if (report.Aging.Over90 > 0) flags.Add("has_overdue_90d");
            if (report.OverdueAmount > 0) flags.Add("has_overdue_payable");

            // First transaction: current_ap = 0 và không có ledger row nào.
            if (supplier.CurrentAp == 0m && report.OpenInvoices.Count == 0)
            {
                flags.Add("first_transaction_with_supplier");
            }

            return new
            {
                status = "found",
                supplier,
                current_balance_331 = report.CurrentBalance331,
                projected_balance_331 = projectedBalance,
                aging = report.Aging,
                overdue_amount = report.OverdueAmount,
                last_payment_date = report.LastPaymentDate,
                payment_term_default = report.PaymentTermDefault,
                open_invoices = report.OpenInvoices,
                risk_flags = flags,
                requires_l2_approval = flags.Contains("expense_over_500m_requires_l2_approval")
                    || flags.Contains("expense_over_30m_cash_payment")
                    || flags.Contains("has_overdue_90d"),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AP enrichment failed for MST {Mst}", supplierMst);
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Deterministic payment matching (FIFO oldest-first).
    //
    // Mirror BuildReceiptMatching nhưng KHÔNG auto-detect chiết khấu — bên AP
    // chiết khấu thanh toán được hưởng thường negotiate với NCC, không auto
    // suy diễn từ shortfall. Nếu LLM extract explicit `discount` thì caller
    // sẽ handle riêng (Sprint 2 apply-expense endpoint).
    // ------------------------------------------------------------------
    private static object? BuildPaymentMatching(object apContext, decimal paymentAmount)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(apContext);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

        if (!doc.RootElement.TryGetProperty("open_invoices", out var openEl)
            || openEl.ValueKind != System.Text.Json.JsonValueKind.Array
            || openEl.GetArrayLength() == 0)
        {
            return new
            {
                status = "no_open_payables",
                payment_amount = paymentAmount,
                message = "NCC không có HĐ chưa trả — chi ứng trước / prepayment (treo TK 331 dư Nợ).",
            };
        }

        // AP open payables: credit > 0, debit = 0. Sort by due date oldest.
        var payables = new List<(string DocumentNumber, DateOnly DocumentDate, DateOnly? DueDate, decimal Remaining)>();
        foreach (var item in openEl.EnumerateArray())
        {
            var docNum = item.TryGetProperty("DocumentNumber", out var dn) ? dn.GetString() ?? "?" : "?";
            var docDate = item.TryGetProperty("DocumentDate", out var dd) && dd.ValueKind == System.Text.Json.JsonValueKind.String
                ? DateOnly.Parse(dd.GetString()!) : DateOnly.MinValue;
            DateOnly? dueDate = null;
            if (item.TryGetProperty("DueDate", out var due) && due.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                dueDate = DateOnly.Parse(due.GetString()!);
            }
            var credit = item.TryGetProperty("Credit", out var cr) ? cr.GetDecimal() : 0m;
            if (credit > 0m)
            {
                payables.Add((docNum, docDate, dueDate, credit));
            }
        }
        payables.Sort((a, b) =>
        {
            var byDue = (a.DueDate ?? DateOnly.MaxValue).CompareTo(b.DueDate ?? DateOnly.MaxValue);
            return byDue != 0 ? byDue : a.DocumentDate.CompareTo(b.DocumentDate);
        });

        var applications = new List<object>();
        var remaining = paymentAmount;
        decimal totalApplied = 0m;
        foreach (var (docNum, docDate, dueDate, invRemaining) in payables)
        {
            if (remaining <= 0m) break;
            var applied = Math.Min(remaining, invRemaining);
            var invAfter = invRemaining - applied;
            remaining -= applied;
            totalApplied += applied;
            applications.Add(new
            {
                document_number = docNum,
                document_date = docDate,
                due_date = dueDate,
                invoice_remaining_before = invRemaining,
                applied,
                invoice_remaining_after = invAfter,
            });
        }

        string status;
        string message;
        if (remaining > 0m)
        {
            status = "overpaid";
            message = $"Chi dư {remaining:N0} VNĐ — treo TK 331 dư Nợ (ứng trước NCC).";
        }
        else if (applications.Count == 1 && ((dynamic)applications[0]).invoice_remaining_after == 0m)
        {
            status = "matched_full";
            message = "Trả đủ 1 hoá đơn NCC.";
        }
        else if (applications.Count > 1)
        {
            status = "matched_multiple";
            message = $"Áp trả {applications.Count} hoá đơn (thứ tự due-date).";
        }
        else
        {
            status = "matched_partial";
            message = "Trả 1 phần hoá đơn.";
        }

        return new
        {
            status,
            payment_amount = paymentAmount,
            total_applied = totalApplied,
            remaining_overpayment = Math.Max(remaining, 0m),
            applications,
            message,
        };
    }

    // ------------------------------------------------------------------
    // Expense claim matching (hoàn ứng nhân viên).
    //
    // Tra sim_employees theo code → tính reimbursement + budget check + hint
    // journal breakdown. Frontend Sprint 3 sẽ render ExpenseClaimPanel.
    // ------------------------------------------------------------------
    private async Task<object?> BuildExpenseClaimMatchingAsync(
        Guid tenantId, string employeeCode, decimal totalClaim, decimal advanceTaken, CancellationToken ct)
    {
        try
        {
            var emp = await _employees.FindByCodeAsync(tenantId, employeeCode, ct);
            if (emp is null)
            {
                return new { status = "employee_not_found", employee_code = employeeCode };
            }

            // Advance covered = min(total claim, outstanding advance).
            var advanceCovered = Math.Min(totalClaim, emp.OutstandingAdvance);
            var remainingToReimburse = Math.Max(0m, totalClaim - advanceCovered);

            // Budget guard: yearly ceiling = monthly × 12.
            var annualCeiling = emp.MonthlyExpenseBudget * 12m;
            var budgetExceeded = annualCeiling > 0m && (emp.YtdExpense + totalClaim) > annualCeiling;

            // Journal hint: Nợ 6417/6428 / Có 141 (advance_covered) + Có 111/112 (remainder).
            var journalHint = $"Nợ 6417 {totalClaim:N0} / Có 141 {advanceCovered:N0} + Có 111 {remainingToReimburse:N0}";

            return new
            {
                status = "found",
                employee = emp,
                total_claim = totalClaim,
                advance_taken = advanceTaken,
                advance_covered = advanceCovered,
                remaining_to_reimburse = remainingToReimburse,
                annual_ceiling = annualCeiling,
                budget_exceeded = budgetExceeded,
                suggested_journal_hint = journalHint,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Expense claim enrichment failed for employee {Code}", employeeCode);
            return null;
        }
    }
}

public interface IVoucherExtractJobScheduler
{
    string Enqueue(
        Guid voucherId,
        Guid userId,
        Guid? tenantId,
        string voucherType,
        string userToken,
        string parentJobId);

    // Immediate enqueue — used when user manually retries extraction via
    // the detail-page button, no parent OCR job to chain on.
    string EnqueueImmediate(
        Guid voucherId,
        Guid userId,
        Guid? tenantId,
        string voucherType,
        string userToken);
}

public sealed class VoucherExtractJobScheduler : IVoucherExtractJobScheduler
{
    private readonly IBackgroundJobClient _hangfire;
    private readonly ILogger<VoucherExtractJobScheduler> _logger;

    public VoucherExtractJobScheduler(IBackgroundJobClient hangfire, ILogger<VoucherExtractJobScheduler> logger)
    {
        _hangfire = hangfire;
        _logger = logger;
    }

    public string EnqueueImmediate(
        Guid voucherId,
        Guid userId,
        Guid? tenantId,
        string voucherType,
        string userToken)
    {
        var jobId = _hangfire.Enqueue<VoucherExtractJobRunner>(
            runner => runner.RunAsync(voucherId, userId, tenantId, voucherType, userToken, CancellationToken.None));
        _logger.LogInformation(
            "Enqueued immediate voucher extract job {JobId} for voucher {VoucherId}",
            jobId, voucherId);
        return jobId;
    }

    public string Enqueue(
        Guid voucherId,
        Guid userId,
        Guid? tenantId,
        string voucherType,
        string userToken,
        string parentJobId)
    {
        // Chain: run only after OCR job finishes (succeeded state).
        // If OCR failed, extraction job stays queued but the runner
        // itself will short-circuit on status='ocr_unavailable'.
        var jobId = _hangfire.ContinueJobWith<VoucherExtractJobRunner>(
            parentJobId,
            runner => runner.RunAsync(voucherId, userId, tenantId, voucherType, userToken, CancellationToken.None));
        _logger.LogInformation(
            "Chained voucher extract job {JobId} after OCR job {ParentJobId}",
            jobId, parentJobId);
        return jobId;
    }
}
