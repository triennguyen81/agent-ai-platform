using System.Text;
using System.Text.RegularExpressions;

namespace Aiq.Gateway.Payments;

// ============================================================================
// Chứng từ THU / CHI -> đối trừ AR/AP: the deterministic, side-effect-free
// half of the flow. Nothing here reads the database or trusts an LLM; the
// repository feeds it facts and it returns a classification. A document's
// title ("Phiếu thu"/"Phiếu chi") is only ever a FALLBACK signal for
// direction — the Company Profile (server-side, TENANT_OWNER-configured)
// decides which party is MY_COMPANY first.
// ============================================================================

public sealed record CashParty(string? Name, string? TaxCode)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Name) && string.IsNullOrWhiteSpace(TaxCode);
}

public sealed record CompanyIdentity(string? TaxCode, IReadOnlyList<string> Names);

public static class CashDirections
{
    public const string MoneyIn = "money_in";
    public const string MoneyOut = "money_out";
    public const string NeedsReview = "needs_review";
}

public sealed record CashDirectionResolution(
    string Direction,
    string Source,              // company_profile_tax_code | company_profile_name | voucher_type | none
    CashParty? Counterparty,
    string? Reason);

public sealed record OpenItem(
    Guid Id,
    string DocumentNumber,
    DateOnly DocumentDate,
    DateOnly? DueDate,
    decimal OriginalAmount,
    decimal PaidAmount)
{
    public decimal Outstanding => OriginalAmount - PaidAmount;
}

public sealed record AllocationSuggestion(
    Guid InvoiceId,
    string DocumentNumber,
    decimal Outstanding,
    decimal ApplyAmount,
    string MatchMethod);

public static class MatchCertainty
{
    // Safe to apply on ADMIN approval without a manual invoice pick.
    public const string Certain = "certain";
    // Candidates exist but choosing between them is a judgement call.
    public const string Ambiguous = "ambiguous";
    // Counterparty has no open item — money is held as UNAPPLIED.
    public const string NoOpenItem = "no_open_item";
}

public sealed record MatchSuggestion(
    string Certainty,
    string? Method,
    IReadOnlyList<AllocationSuggestion> Allocations,
    decimal UnappliedAmount,
    string Reason);

public static class SettlementStatuses
{
    public const string Unpaid = "UNPAID";
    public const string Partial = "PARTIAL";
    public const string Paid = "PAID";
    public const string Overdue = "OVERDUE";
}

public static class CashSettlementRules
{
    /// <summary>"cash" | "bank" | null (unknown — the caller must not guess 111 vs 112).</summary>
    public static string? NormalizePaymentMethod(string? raw)
    {
        var s = Fold(raw);
        if (s.Length == 0) return null;
        if (s is "tm" or "cash" or "tien mat" || s.Contains("tien mat")) return "cash";
        if (s is "ck" or "bank" or "unc" or "chuyen khoan" || s.Contains("chuyen khoan") || s.Contains("ngan hang") || s.Contains("uy nhiem chi")) return "bank";
        return null;
    }

    public static readonly IReadOnlySet<string> CashVoucherTypes = new HashSet<string> { "receipt", "payment", "bank_transfer" };

    public static string DefaultCashAccount(string method) => method == "cash" ? "1111" : "1121";

    /// <summary>
    /// Settlement status is independent of POSTED (accounting) status: an
    /// invoice can be POSTED and UNPAID. OVERDUE wins over PARTIAL/UNPAID
    /// once the due date has passed and something is still outstanding.
    /// </summary>
    public static string SettlementStatus(decimal original, decimal paid, DateOnly? dueDate, DateOnly today)
    {
        var outstanding = original - paid;
        if (outstanding <= 0m) return SettlementStatuses.Paid;
        if (dueDate is { } due && due < today) return SettlementStatuses.Overdue;
        return paid > 0m ? SettlementStatuses.Partial : SettlementStatuses.Unpaid;
    }

    /// <summary>DB open-item status for a given paid amount (sim_*_ledger.status).</summary>
    public static string OpenItemStatus(decimal original, decimal paid) =>
        paid <= 0m ? "open" : paid >= original ? "paid" : "partial";

    public static CashDirectionResolution ResolveDirection(
        string voucherType, CashParty payer, CashParty payee, CompanyIdentity? company, string? documentText = null)
    {
        var vt = (voucherType ?? string.Empty).Trim().ToLowerInvariant();
        string? typeDirection = vt switch
        {
            "receipt" => CashDirections.MoneyIn,
            "payment" or "bank_transfer" => CashDirections.MoneyOut,
            _ => null,
        };

        var (payerIsUs, payerBy) = MatchesCompany(payer, company);
        var (payeeIsUs, payeeBy) = MatchesCompany(payee, company);

        if (payerIsUs && payeeIsUs)
        {
            return new(CashDirections.NeedsReview, "company_profile_" + payerBy, null, "both_parties_match_company");
        }
        if (payeeIsUs || payerIsUs)
        {
            var direction = payeeIsUs ? CashDirections.MoneyIn : CashDirections.MoneyOut;
            var counterparty = payeeIsUs ? payer : payee;
            var by = payeeIsUs ? payeeBy : payerBy;
            // A name match is corroborated (and reported) by the exact tax
            // code when the document prints our MST — the stronger signal.
            if (by == "name" && CompanyTaxCodeInText(company, documentText)) by = "tax_code_document";
            if (typeDirection is not null && typeDirection != direction)
            {
                return new(CashDirections.NeedsReview, "company_profile_" + by, counterparty, "direction_conflicts_voucher_type");
            }
            return new(direction, "company_profile_" + by, counterparty.IsEmpty ? null : counterparty, null);
        }

        if (typeDirection is null)
        {
            return new(CashDirections.NeedsReview, "none", null, "unsupported_voucher_type");
        }

        var fallbackCounterparty = typeDirection == CashDirections.MoneyIn ? payer : payee;

        // MY_COMPANY's exact tax code printed anywhere on the document (a
        // phiếu thu/chi letterhead "MST: 0123456789") identifies us even when
        // the extractor left our party fields empty. The side is the one the
        // document type implies, so the counterparty must NOT carry our MST.
        if (CompanyTaxCodeInText(company, documentText)
            && NormalizeTaxCode(fallbackCounterparty.TaxCode) != NormalizeTaxCode(company!.TaxCode))
        {
            return new(typeDirection, "company_profile_tax_code_document",
                fallbackCounterparty.IsEmpty ? null : fallbackCounterparty, null);
        }

        // Neither party identified as MY_COMPANY (e.g. a phiếu thu printed on
        // our own form rarely repeats our name as payee). Fall back to the
        // document type, but the counterparty must still resolve to a real
        // customer/supplier and ADMIN must still approve before anything posts.
        return new(typeDirection, "voucher_type", fallbackCounterparty.IsEmpty ? null : fallbackCounterparty,
            company?.TaxCode is null ? "company_profile_missing" : null);
    }

    /// <summary>
    /// Invoice references a bank transfer description EXPLICITLY makes, even
    /// to invoices that do not exist or are closed: a token equal to any known
    /// invoice number (any customer, any status), a token right after an
    /// invoice keyword (HD / HĐ / HOA DON / INV / INVOICE), or an INV-style
    /// code. Used so a named-but-not-open invoice is never silently replaced
    /// by "some other open invoice".
    /// </summary>
    public static IReadOnlyList<string> ExplicitInvoiceReferences(string? description, IEnumerable<string> knownInvoiceNumbers)
    {
        if (string.IsNullOrWhiteSpace(description)) return Array.Empty<string>();
        var known = knownInvoiceNumbers.GroupBy(NormalizeDocNumber, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var raw = Regex.Split(description.Trim(), @"[\s,;:()]+").Where(t => t.Length > 0).ToList();
        var result = new List<string>();
        for (var i = 0; i < raw.Count; i++)
        {
            var token = raw[i].Trim('.', '-', '_', '/');
            if (token.Length == 0) continue;
            var norm = NormalizeDocNumber(token);
            var folded = Fold(token);
            if (folded is "hd" or "hđ" or "inv" or "invoice" or "so" or "hoa" or "don") continue;
            var prev = i > 0 ? Fold(raw[i - 1]) : "";
            var prev2 = i > 1 ? Fold(raw[i - 2]) + " " + prev : "";
            var afterKeyword = prev is "hd" or "inv" or "invoice" or "hdon" || prev2.EndsWith("hoa don", StringComparison.Ordinal);
            var invStyle = Regex.IsMatch(token.ToUpperInvariant(), @"(^|[-_])INV([-_]|$)") && token.Length >= 5;
            if (known.TryGetValue(norm, out var exact)) result.Add(exact);
            else if ((afterKeyword && (token.Any(char.IsDigit) || token.Length >= 5)) || invStyle) result.Add(token.ToUpperInvariant());
        }
        return result.Distinct().ToList();
    }

    /// <summary>True when the Company Profile's exact tax code appears as a standalone digit run in the text.</summary>
    public static bool CompanyTaxCodeInText(CompanyIdentity? company, string? text)
    {
        var tax = NormalizeTaxCode(company?.TaxCode);
        if (tax.Length < 10 || string.IsNullOrEmpty(text)) return false;
        return Regex.IsMatch(text, $@"(?<!\d){Regex.Escape(tax)}(?!\d)");
    }

    /// <summary>Which of the given document numbers the document's references point at (same token rules as matching).</summary>
    public static IReadOnlyList<string> FindReferencedDocumentNumbers(IEnumerable<string> documentNumbers, IEnumerable<string?> references)
    {
        var tokens = ReferenceTokens(references);
        return documentNumbers
            .Where(d => tokens.Contains(NormalizeDocNumber(d)) || (DigitsKey(d) is { } k && tokens.Contains(k)))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Matching priority (first rule that yields a result wins):
    ///   1. exact invoice number/reference found in the document
    ///   2. exact counterparty + exact outstanding amount (single invoice)
    ///   3. single open invoice whose outstanding covers the amount
    ///   4. otherwise ADMIN must choose (Ambiguous)
    /// Amount beyond what the matched invoice(s) can absorb is never spilled
    /// onto some other invoice — it stays UNAPPLIED.
    /// </summary>
    public static MatchSuggestion SuggestAllocations(
        decimal amount, IReadOnlyList<OpenItem> openItems, IEnumerable<string?> references,
        IReadOnlyCollection<string>? explicitInvoiceReferences = null)
    {
        var items = openItems.Where(i => i.Outstanding > 0m)
            .OrderBy(i => i.DueDate ?? DateOnly.MaxValue).ThenBy(i => i.DocumentDate).ThenBy(i => i.DocumentNumber)
            .ToList();
        if (amount <= 0m)
        {
            return new(MatchCertainty.Ambiguous, null, Array.Empty<AllocationSuggestion>(), 0m, "amount_missing");
        }
        if (items.Count == 0)
        {
            return new(MatchCertainty.NoOpenItem, null, Array.Empty<AllocationSuggestion>(), amount, "no_open_item_for_counterparty");
        }

        var tokens = ReferenceTokens(references);
        var byReference = items.Where(i => tokens.Contains(NormalizeDocNumber(i.DocumentNumber))
                                        || (DigitsKey(i.DocumentNumber) is { } k && tokens.Contains(k))).ToList();
        if (byReference.Count > 0)
        {
            return Allocate(amount, byReference, "invoice_reference", MatchCertainty.Certain, "invoice_number_in_document");
        }

        // The document explicitly names an invoice that is not open (already
        // PAID, or not booked): never auto-apply to a DIFFERENT invoice on
        // amount alone — ADMIN must choose (and the choice is audited as
        // admin_selected against the stored document reference).
        var referenceNotOpen = explicitInvoiceReferences is { Count: > 0 };

        var exact = items.Where(i => i.Outstanding == amount).ToList();
        if (referenceNotOpen)
        {
            var hint = exact.Count > 0 ? exact.Take(1).ToList() : items;
            return Allocate(amount, hint, exact.Count > 0 ? "exact_amount" : "outstanding_compatible",
                MatchCertainty.Ambiguous, "document_reference_not_open");
        }
        if (exact.Count == 1)
        {
            return Allocate(amount, exact, "exact_amount", MatchCertainty.Certain, "exact_outstanding_amount");
        }
        if (exact.Count > 1)
        {
            return Allocate(amount, exact.Take(1).ToList(), "exact_amount", MatchCertainty.Ambiguous, "multiple_invoices_with_exact_amount");
        }

        if (items.Count == 1 && items[0].Outstanding >= amount)
        {
            return Allocate(amount, items, "outstanding_compatible", MatchCertainty.Certain, "single_open_invoice_covers_amount");
        }

        // Several open invoices and nothing in the document says which one:
        // pre-fill oldest-due-first as a hint only; ADMIN must confirm.
        return Allocate(amount, items, "outstanding_compatible", MatchCertainty.Ambiguous, "multiple_open_invoices_admin_selection_required");
    }

    private static MatchSuggestion Allocate(decimal amount, List<OpenItem> targets, string method, string certainty, string reason)
    {
        var remaining = amount;
        var allocations = new List<AllocationSuggestion>();
        foreach (var item in targets)
        {
            if (remaining <= 0m) break;
            var apply = Math.Min(remaining, item.Outstanding);
            allocations.Add(new AllocationSuggestion(item.Id, item.DocumentNumber, item.Outstanding, apply, method));
            remaining -= apply;
        }
        return new MatchSuggestion(certainty, method, allocations, remaining, reason);
    }

    // ------------------------------------------------------------------
    // Normalisation helpers
    // ------------------------------------------------------------------

    private static (bool Match, string By) MatchesCompany(CashParty party, CompanyIdentity? company)
    {
        if (company is null || party.IsEmpty) return (false, "");
        var partyTax = NormalizeTaxCode(party.TaxCode);
        var companyTax = NormalizeTaxCode(company.TaxCode);
        if (partyTax.Length > 0 && companyTax.Length > 0)
        {
            // A present-but-different tax code is decisive: never fall
            // through to a fuzzy name match that could contradict it.
            return partyTax == companyTax ? (true, "tax_code") : (false, "");
        }
        var partyName = NormalizeCompanyName(party.Name);
        if (partyName.Length == 0) return (false, "");
        return company.Names.Any(n => NormalizeCompanyName(n) is { Length: > 0 } cn && cn == partyName)
            ? (true, "name")
            : (false, "");
    }

    public static string NormalizeTaxCode(string? taxCode) =>
        new string((taxCode ?? string.Empty).Where(char.IsDigit).ToArray());

    private static readonly string[] LegalFormWords =
    {
        "cong ty", "cty", "trach nhiem huu han", "tnhh", "co phan", "cp", "ctcp", "mtv", "mot thanh vien", "jsc", "co", "ltd", "company",
    };

    public static string NormalizeCompanyName(string? name)
    {
        var s = " " + Regex.Replace(Fold(name), "[^a-z0-9 ]", " ") + " ";
        foreach (var w in LegalFormWords) s = s.Replace(" " + w + " ", " ");
        return Regex.Replace(s, "\\s+", " ").Trim();
    }

    public static string NormalizeDocNumber(string? doc) =>
        Regex.Replace((doc ?? string.Empty).ToUpperInvariant(), "[^A-Z0-9]", "");

    // "0000501" and "501" are the same e-invoice number; only for purely
    // numeric document numbers (prefixed numbers must match exactly).
    private static string? DigitsKey(string? doc)
    {
        var d = (doc ?? string.Empty).Trim();
        if (d.Length == 0 || !d.All(char.IsDigit)) return null;
        var trimmed = d.TrimStart('0');
        return trimmed.Length == 0 ? null : "#" + trimmed;
    }

    private static HashSet<string> ReferenceTokens(IEnumerable<string?> references)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in references)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            // Whole value first (an invoice_number field), then each token of
            // free text ("Thanh toan HD DEMO-ST-VT-001 dot 1").
            tokens.Add(NormalizeDocNumber(r));
            foreach (var raw in Regex.Split(r.ToUpperInvariant(), "[^A-Z0-9\\-_/\\.]+"))
            {
                var t = NormalizeDocNumber(raw);
                if (t.Length < 3) continue;
                tokens.Add(t);
                if (DigitsKey(raw) is { } k) tokens.Add(k);
            }
        }
        tokens.Remove("");
        return tokens;
    }

    // ICU-independent Vietnamese fold. The gateway runs with
    // InvariantGlobalization=true, where string.Normalize(FormD) does NOT
    // decompose "ể"/"ả"/... — so diacritics are mapped explicitly, and any
    // already-decomposed combining marks (U+0300–U+036F, e.g. from OCR
    // output in NFD) are dropped.
    private static readonly Dictionary<char, char> VietnameseFold = BuildFold();

    private static Dictionary<char, char> BuildFold()
    {
        var map = new Dictionary<char, char>();
        void Add(string chars, char to)
        {
            foreach (var c in chars) { map[c] = to; }
        }
        Add("àáảãạăằắẳẵặâầấẩẫậÀÁẢÃẠĂẰẮẲẴẶÂẦẤẨẪẬ", 'a');
        Add("èéẻẽẹêềếểễệÈÉẺẼẸÊỀẾỂỄỆ", 'e');
        Add("ìíỉĩịÌÍỈĨỊ", 'i');
        Add("òóỏõọôồốổỗộơờớởỡợÒÓỎÕỌÔỒỐỔỖỘƠỜỚỞỠỢ", 'o');
        Add("ùúủũụưừứửữựÙÚỦŨỤƯỪỨỬỮỰ", 'u');
        Add("ỳýỷỹỵỲÝỶỸỴ", 'y');
        Add("đĐ", 'd');
        return map;
    }

    /// <summary>Lower-cased, diacritic-free Vietnamese text (ICU-independent).</summary>
    public static string FoldVietnamese(string? s) => Fold(s);

    private static string Fold(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Trim())
        {
            if (c >= '\u0300' && c <= '\u036F') continue;
            sb.Append(VietnameseFold.TryGetValue(c, out var folded) ? folded : char.ToLowerInvariant(c));
        }
        return Regex.Replace(sb.ToString(), "\\s+", " ");
    }
}
