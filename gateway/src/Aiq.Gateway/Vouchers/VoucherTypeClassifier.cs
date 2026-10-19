using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Aiq.Gateway.Accounting;
using Aiq.Gateway.Payments;

namespace Aiq.Gateway.Vouchers;

// ============================================================================
// Voucher type: AUTO-DETECT + MANUAL OVERRIDE + CONFIDENCE / EVIDENCE.
//
// OCR text -> deterministic evidence (document title line, labelled fields,
// invoice seller/buyer vs Company Profile) -> extractor/LLM classification ->
// combined decision { voucher_type, confidence, evidence, source, status }.
//
// The LLM never silently overrides a deterministic title, never decides
// sales vs purchase when the Company Profile can, and a user-selected type is
// only a hint: disagreement is surfaced, not ignored. The final type is
// decided BEFORE enrichment / settlement / posting, and posting is gated
// while a decision still needs human confirmation.
// ============================================================================

public static class VoucherTypes
{
    public const string Auto = "auto";
    public const string Unclassified = "unclassified";
    public const string InvoiceOut = "invoice_vat_out";
    public const string InvoiceIn = "invoice_vat_in";
    public const string LegacyInvoice = "invoice_vat";
    public const string Receipt = "receipt";
    public const string Payment = "payment";
    public const string BankTransfer = "bank_transfer";
    public const string BankStatement = "bank_statement";
    public const string ExpenseClaim = "expense_claim";
    public const string Contract = "contract";

    /// <summary>Types a user/ADMIN may choose (upload hint or correction).</summary>
    public static readonly IReadOnlySet<string> Selectable = new HashSet<string>
    {
        InvoiceOut, InvoiceIn, Receipt, Payment, BankTransfer, BankStatement, ExpenseClaim, LegacyInvoice, Contract,
    };

    public static bool IsInvoice(string? t) => t is InvoiceOut or InvoiceIn or LegacyInvoice;

    public static string? Normalize(string? t)
    {
        var v = (t ?? string.Empty).Trim().ToLowerInvariant();
        return Selectable.Contains(v) ? v : null;
    }

    public static string Label(string? t) => t switch
    {
        InvoiceOut => "Hóa đơn bán",
        InvoiceIn => "Hóa đơn mua",
        LegacyInvoice => "Hóa đơn VAT",
        Receipt => "Phiếu thu",
        Payment => "Phiếu chi",
        BankTransfer => "Ủy nhiệm chi",
        BankStatement => "Sao kê ngân hàng",
        ExpenseClaim => "Đề nghị hoàn ứng",
        Contract => "Hợp đồng",
        _ => "Chưa xác định",
    };
}

public static class VoucherTypeSources
{
    public const string AutoDetected = "auto_detected";
    public const string UserSelected = "user_selected";
    public const string ExtractorCorrected = "extractor_corrected";
    public const string AdminCorrected = "admin_corrected";
}

public static class VoucherTypeStatuses
{
    public const string Ok = "ok";                                    // >= 0.90, nothing to confirm
    public const string ConfirmRecommended = "confirm_recommended";  // 0.70–0.89, warning only
    public const string NeedsConfirmation = "needs_confirmation";    // < 0.70 / undetermined — blocks posting
    public const string HintMismatch = "hint_mismatch";              // user-selected type ≠ detection — blocks posting
    public const string Conflict = "conflict";                       // title vs LLM disagree — blocks posting
}

/// <summary>
/// What the extract job was asked to do: "auto" (no hint), "&lt;type&gt;" (a
/// user hint) or "admin:&lt;type&gt;" (an ADMIN correction that must win). Kept
/// as one string so the Hangfire job signature never changes.
/// </summary>
public sealed record VoucherTypeRequest(string? Hint, string? Forced)
{
    private const string AdminPrefix = "admin:";

    public static string ForAdmin(string type) => AdminPrefix + type;

    public static VoucherTypeRequest Parse(string? raw)
    {
        var v = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (v.StartsWith(AdminPrefix, StringComparison.Ordinal))
            return new VoucherTypeRequest(null, VoucherTypes.Normalize(v[AdminPrefix.Length..]));
        if (v is "" or VoucherTypes.Auto or VoucherTypes.Unclassified) return new VoucherTypeRequest(null, null);
        return new VoucherTypeRequest(VoucherTypes.Normalize(v), null);
    }
}

public sealed record DeterministicDetection(
    string? Type,               // canonical type, or invoice_vat_out/in when direction resolved
    bool InvoiceCandidate,
    bool TitleInHeader,
    bool TitleConflict,
    string? TitleLine,
    int SupportingFields,
    string? SellerTaxCode,
    string? BuyerTaxCode,
    IReadOnlyList<string> Evidence);

/// <summary>Persisted as extracted_fields.voucher_type_classification (snake_case JSON).</summary>
public sealed record VoucherTypeClassification
{
    [JsonPropertyName("voucher_type")] public string VoucherType { get; init; } = VoucherTypes.Unclassified;
    [JsonPropertyName("detected_voucher_type")] public string? DetectedVoucherType { get; init; }
    [JsonPropertyName("llm_voucher_type")] public string? LlmVoucherType { get; init; }
    [JsonPropertyName("voucher_type_uploaded_as")] public string? UploadedAs { get; init; }
    [JsonPropertyName("confidence")] public decimal Confidence { get; init; }
    [JsonPropertyName("evidence")] public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
    [JsonPropertyName("source")] public string Source { get; init; } = VoucherTypeSources.AutoDetected;
    [JsonPropertyName("status")] public string Status { get; init; } = VoucherTypeStatuses.NeedsConfirmation;
    [JsonPropertyName("requires_confirmation")] public bool RequiresConfirmation { get; init; }
    [JsonPropertyName("confirmed_by")] public Guid? ConfirmedBy { get; init; }
    [JsonPropertyName("confirmed_at")] public DateTime? ConfirmedAt { get; init; }

    public static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    /// <summary>Posting must wait for a human when this is true.</summary>
    [JsonIgnore] public bool BlocksPosting => RequiresConfirmation && ConfirmedBy is null;

    public static VoucherTypeClassification? FromExtractedFields(string? extractedFieldsJson)
    {
        if (string.IsNullOrWhiteSpace(extractedFieldsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(extractedFieldsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("voucher_type_classification", out var el)
                || el.ValueKind != JsonValueKind.Object) return null;
            return el.Deserialize<VoucherTypeClassification>(Json);
        }
        catch (JsonException) { return null; }
    }
}

public static class VoucherTypeClassifier
{
    private const int HeaderLines = 15;

    // Title phrases at the START of a line (folded), most specific first.
    private static readonly (string Phrase, string Type)[] TitleRules =
    {
        ("uy nhiem chi", VoucherTypes.BankTransfer),
        ("lenh chuyen tien", VoucherTypes.BankTransfer),
        ("payment order", VoucherTypes.BankTransfer),
        ("sao ke", VoucherTypes.BankStatement),
        ("bank statement", VoucherTypes.BankStatement),
        ("giay de nghi hoan ung", VoucherTypes.ExpenseClaim),
        ("de nghi hoan ung", VoucherTypes.ExpenseClaim),
        ("giay thanh toan tien tam ung", VoucherTypes.ExpenseClaim),
        ("quyet toan tam ung", VoucherTypes.ExpenseClaim),
        ("phieu chi", VoucherTypes.Payment),
        ("phieu thu", VoucherTypes.Receipt),
        ("hoa don gia tri gia tang", "invoice"),
        ("hoa don gtgt", "invoice"),
        ("hoa don ban hang", "invoice"),
        ("hoa don mua hang", "invoice"),
        ("hoa don dien tu", "invoice"),
        ("hoa don thuong mai", "invoice"),
        ("vat invoice", "invoice"),
    };

    // Labelled fields that corroborate a title (folded "contains").
    private static readonly Dictionary<string, (string Pattern, string Label)[]> SupportRules = new()
    {
        [VoucherTypes.Receipt] = new[] { ("nop tien", "nộp tiền"), ("ly do thu", "Lý do thu"), ("nhan tien", "nhận tiền") },
        [VoucherTypes.Payment] = new[] { ("tra tien", "trả tiền"), ("ly do chi", "Lý do chi"), ("nhan tien", "nhận tiền") },
        [VoucherTypes.BankTransfer] = new[] { ("thu huong", "thụ hưởng"), ("tai khoan tra", "Tài khoản trả"), ("tai khoan nhan", "Tài khoản nhận"), ("ngan hang", "Ngân hàng") },
        [VoucherTypes.BankStatement] = new[] { ("so du", "Số dư"), ("tien vao", "Tiền vào"), ("tien ra", "Tiền ra"), ("ky sao ke", "Kỳ sao kê"), ("chu tai khoan", "Chủ tài khoản") },
        [VoucherTypes.ExpenseClaim] = new[] { ("tam ung", "tạm ứng"), ("ma nhan vien", "Mã nhân viên"), ("hoan ung", "hoàn ứng"), ("chi phi thuc te", "Chi phí thực tế") },
        ["invoice"] = new[] { ("nguoi ban", "Người bán"), ("nguoi mua", "Người mua"), ("don vi ban hang", "Đơn vị bán hàng"), ("don vi mua hang", "Đơn vị mua hàng"), ("thue gtgt", "Thuế GTGT"), ("truoc vat", "Tiền trước VAT"), ("thue suat", "Thuế suất") },
    };

    private static readonly Regex TaxCodeRx = new(@"(?<!\d)(\d{10}(?:-\d{3})?)(?!\d)", RegexOptions.Compiled);

    public static DeterministicDetection Detect(string? rawText, CompanyIdentity? company)
    {
        var lines = (rawText ?? string.Empty).Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var folded = lines.Select(CashSettlementRules.FoldVietnamese).ToList();
        var evidence = new List<string>();

        string? TitleOf(string f)
        {
            var t = Regex.Replace(f, @"^[^a-z0-9]+", "");
            foreach (var (phrase, type) in TitleRules)
                if (t.StartsWith(phrase, StringComparison.Ordinal)) return type;
            return null;
        }

        var headerTitles = new List<(int Index, string Type)>();
        for (var i = 0; i < Math.Min(HeaderLines, folded.Count); i++)
            if (TitleOf(folded[i]) is { } t) headerTitles.Add((i, t));

        int titleIndex = -1; string? titleType = null; var inHeader = false;
        if (headerTitles.Count > 0) { (titleIndex, titleType) = headerTitles[0]; inHeader = true; }
        else
        {
            for (var i = HeaderLines; i < folded.Count; i++)
                if (TitleOf(folded[i]) is { } t) { titleIndex = i; titleType = t; break; }
        }
        // Structured exports (CSV/XLSX statements) have no title line, only a
        // column header — "Ngày | Nội dung | Tiền vào | Tiền ra | Số dư".
        if (titleType is null)
        {
            for (var i = 0; i < Math.Min(HeaderLines, lines.Count); i++)
            {
                if (!Aiq.Gateway.Banking.BankStatementParser.LooksLikeStatementHeader(lines[i])) continue;
                titleIndex = i; titleType = VoucherTypes.BankStatement; inHeader = true;
                break;
            }
        }
        var titleConflict = headerTitles.Select(h => h.Type).Distinct().Count() > 1;
        var titleLine = titleIndex >= 0 ? Short(lines[titleIndex]) : null;
        if (titleLine is not null)
            evidence.Add(headerTitles.Count == 0 && titleType == VoucherTypes.BankStatement && inHeader
                ? $"Dòng tiêu đề cột sao kê: \"{titleLine}\""
                : $"Tiêu đề OCR: \"{titleLine}\"");
        if (titleConflict)
            evidence.Add("Nhiều tiêu đề khác loại ở đầu chứng từ: " + string.Join(", ", headerTitles.Select(h => $"\"{Short(lines[h.Index])}\"")));

        var supporting = 0;
        if (titleType is not null && SupportRules.TryGetValue(titleType, out var rules))
        {
            foreach (var (pattern, label) in rules)
            {
                var idx = folded.FindIndex(f => f.Contains(pattern, StringComparison.Ordinal) && TitleOf(f) is null);
                if (idx < 0) continue;
                supporting++;
                var ev = idx == titleIndex
                    ? $"Cột \"{label}\" trong dòng tiêu đề"
                    : $"Dòng \"{FieldLabel(lines[idx])}\"";
                if (!evidence.Contains(ev)) evidence.Add(ev);
            }
        }

        // Invoice direction: seller/buyer tax codes under their section labels,
        // compared with the trusted Company Profile tax code.
        string? sellerTax = null, buyerTax = null;
        string? type = titleType;
        if (titleType == "invoice")
        {
            string? section = null;
            for (var i = 0; i < folded.Count; i++)
            {
                var f = folded[i];
                if (Regex.IsMatch(f, @"^(nguoi ban|don vi ban hang|ben ban)\b")) section = "seller";
                else if (Regex.IsMatch(f, @"^(nguoi mua|don vi mua hang|ben mua)\b")) section = "buyer";
                if (section is null || !(f.Contains("mst") || f.Contains("ma so thue"))) continue;
                // pdftotext keeps "MST  0123456789" on one line; image OCR
                // (PaddleOCR) often puts the number on the NEXT line.
                var m = TaxCodeRx.Match(lines[i]);
                if (!m.Success && i + 1 < lines.Count) m = TaxCodeRx.Match(lines[i + 1]);
                if (!m.Success) continue;
                if (section == "seller" && sellerTax is null) sellerTax = m.Value;
                if (section == "buyer" && buyerTax is null) buyerTax = m.Value;
            }
            var resolution = InvoiceDirectionResolver.Resolve(sellerTax, buyerTax, company?.TaxCode);
            type = resolution.Direction switch
            {
                InvoiceDirection.Sales => VoucherTypes.InvoiceOut,
                InvoiceDirection.Purchase => VoucherTypes.InvoiceIn,
                _ => null,
            };
            if (type == VoucherTypes.InvoiceOut) evidence.Add($"Người bán MST {sellerTax} = MST công ty (Hồ sơ công ty) → hóa đơn bán");
            else if (type == VoucherTypes.InvoiceIn) evidence.Add($"Người mua MST {buyerTax} = MST công ty (Hồ sơ công ty) → hóa đơn mua");
            else evidence.Add("Chưa xác định được bên bán/mua là công ty mình từ OCR");
        }

        return new DeterministicDetection(type, titleType == "invoice", inHeader, titleConflict, titleLine,
            supporting, sellerTax, buyerTax, evidence);
    }

    /// <summary>Combine deterministic evidence, the extractor's own type and the request (hint / ADMIN override).</summary>
    public static VoucherTypeClassification Combine(
        VoucherTypeRequest request,
        DeterministicDetection det,
        string? llmType,
        string? extractedSellerTax,
        string? extractedBuyerTax,
        string? companyTaxCode)
    {
        var llm = (llmType ?? string.Empty).Trim().ToLowerInvariant();
        llm = VoucherTypes.Normalize(llm) ?? "";
        var evidence = new List<string>(det.Evidence);
        var hint = request.Hint;

        if (request.Forced is { } forced)
        {
            evidence.Insert(0, $"ADMIN chọn loại chứng từ: {VoucherTypes.Label(forced)}");
            return new VoucherTypeClassification
            {
                VoucherType = forced, DetectedVoucherType = det.Type, LlmVoucherType = Nullable(llm),
                Confidence = 1.0m, Evidence = evidence, Source = VoucherTypeSources.AdminCorrected,
                Status = VoucherTypeStatuses.Ok, RequiresConfirmation = false,
            };
        }

        // Invoice title but direction not provable from OCR sections: the
        // extractor's seller/buyer tax codes, still judged by the Company Profile.
        var detected = det.Type;
        var directionFromExtract = false;
        if (det.InvoiceCandidate && detected is null)
        {
            var r = InvoiceDirectionResolver.Resolve(extractedSellerTax, extractedBuyerTax, companyTaxCode);
            detected = r.Direction switch
            {
                InvoiceDirection.Sales => VoucherTypes.InvoiceOut,
                InvoiceDirection.Purchase => VoucherTypes.InvoiceIn,
                _ => null,
            };
            if (detected is not null)
            {
                directionFromExtract = true;
                evidence.Add($"Chiều hóa đơn theo MST trích xuất + Hồ sơ công ty → {VoucherTypes.Label(detected)}");
            }
        }

        decimal confidence;
        var conflict = det.TitleConflict;
        if (detected is not null)
        {
            confidence = (det.TitleInHeader ? 0.80m : 0.70m) + Math.Min(0.10m, 0.05m * det.SupportingFields);
            if (directionFromExtract) confidence -= 0.05m;
            if (llm.Length > 0)
            {
                if (Agrees(llm, detected))
                {
                    confidence += 0.08m;
                    evidence.Add($"AI trích xuất cũng nhận diện: {VoucherTypes.Label(llm == VoucherTypes.LegacyInvoice ? detected : llm)}");
                }
                else
                {
                    conflict = true;
                    evidence.Add($"⚠ AI trích xuất nhận diện khác: {VoucherTypes.Label(llm)} — mâu thuẫn với tiêu đề chứng từ");
                }
            }
        }
        else if (det.InvoiceCandidate)
        {
            // Hóa đơn, but neither OCR nor extracted MSTs tie it to MY_COMPANY.
            detected = VoucherTypes.IsInvoice(llm) && llm != VoucherTypes.LegacyInvoice ? llm : null;
            confidence = 0.55m;
            evidence.Add("Không xác định chắc chắn hóa đơn bán hay mua theo Hồ sơ công ty — cần xác nhận");
        }
        else if (llm.Length > 0)
        {
            detected = llm;
            confidence = 0.72m;
            evidence.Add($"Chỉ dựa trên AI trích xuất: {VoucherTypes.Label(llm)} (không thấy tiêu đề chứng từ rõ ràng)");
        }
        else
        {
            confidence = 0.30m;
            evidence.Add("Không nhận diện được loại chứng từ");
        }
        confidence = Math.Round(Math.Min(confidence, 0.99m), 2);

        string final, source, status;
        if (conflict)
        {
            final = hint ?? detected ?? Nullable(llm) ?? VoucherTypes.Unclassified;
            confidence = Math.Min(confidence, 0.50m);
            source = hint is not null ? VoucherTypeSources.UserSelected : VoucherTypeSources.AutoDetected;
            status = VoucherTypeStatuses.Conflict;
        }
        else if (detected is null)
        {
            final = hint ?? VoucherTypes.Unclassified;
            source = hint is not null ? VoucherTypeSources.UserSelected : VoucherTypeSources.AutoDetected;
            status = VoucherTypeStatuses.NeedsConfirmation;
        }
        else if (hint is not null && !SameType(hint, detected))
        {
            // Strong detection wins as the working type (settlement/enrichment
            // must never run on a label the document contradicts), but the
            // mismatch is surfaced and posting waits for confirmation.
            var strong = confidence >= 0.90m;
            final = strong ? detected : hint;
            source = strong ? VoucherTypeSources.ExtractorCorrected : VoucherTypeSources.UserSelected;
            status = VoucherTypeStatuses.HintMismatch;
            evidence.Add($"⚠ Loại đã chọn khi tải lên: {VoucherTypes.Label(hint)} — khác kết quả nhận diện");
        }
        else
        {
            final = detected;
            source = hint is not null ? VoucherTypeSources.UserSelected : VoucherTypeSources.AutoDetected;
            status = confidence >= 0.90m ? VoucherTypeStatuses.Ok
                : confidence >= 0.70m ? VoucherTypeStatuses.ConfirmRecommended
                : VoucherTypeStatuses.NeedsConfirmation;
        }

        return new VoucherTypeClassification
        {
            VoucherType = final,
            DetectedVoucherType = detected,
            LlmVoucherType = Nullable(llm),
            UploadedAs = hint,
            Confidence = confidence,
            Evidence = evidence,
            Source = source,
            Status = status,
            RequiresConfirmation = status is VoucherTypeStatuses.Conflict or VoucherTypeStatuses.HintMismatch or VoucherTypeStatuses.NeedsConfirmation,
        };
    }

    // The invoice family agrees with a Company-Profile-resolved direction:
    // the LLM never decides sales vs purchase when the profile can.
    private static bool Agrees(string llm, string detected) =>
        llm == detected || (VoucherTypes.IsInvoice(llm) && VoucherTypes.IsInvoice(detected));

    private static bool SameType(string hint, string detected) =>
        hint == detected || (hint == VoucherTypes.LegacyInvoice && VoucherTypes.IsInvoice(detected));

    private static string? Nullable(string s) => s.Length == 0 ? null : s;

    private static string Short(string line) => line.Length > 70 ? line[..70] + "…" : line;

    // "Đơn vị trả tiền                   CÔNG TY TNHH SÁU TRỰC" -> "Đơn vị trả tiền"
    private static string FieldLabel(string line) => Short(Regex.Split(line, @"\s{2,}|:\s")[0].Trim());
}
