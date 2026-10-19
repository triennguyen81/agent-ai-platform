using System.Text.Json.Serialization;

namespace Aiq.Gateway.Banking;

// ============================================================================
// Statement line -> Customer (money_in) / Supplier candidate (money_out).
//
// Priority: 1 exact known bank account · 2 explicit tax code · 3 invoice
// reference · 4 customer code · 5 exact normalized legal name · (contract
// reference: not yet) · 6 fuzzy name / amount as SUGGESTIONS only.
//
// A MATCHED result needs at least one strong signal and no disagreement
// between strong signals. Amount alone never identifies anybody.
// Identification only — nothing here touches AR/AP, journals or
// payment applications.
// ============================================================================

public static class MatchStatuses
{
    public const string Matched = "matched";
    public const string NeedsReview = "needs_review";
    public const string Unmatched = "unmatched";
}

public static class MatchMethods
{
    public const string BankAccountExact = "bank_account_exact";
    public const string TaxCode = "tax_code";
    public const string InvoiceReference = "invoice_reference";
    public const string CustomerCode = "customer_code";
    public const string NameExact = "name_exact";
    public const string NameSimilar = "name_similar";
    public const string AmountHint = "amount_hint";
    public const string Conflict = "conflict";
    public const string AdminSelected = "admin_selected";
}

public sealed record MatchParty(Guid Id, string Code, string Name, string? TaxCode);

public sealed record AccountOwnerHit(
    Guid AccountId, string OwnerType, Guid OwnerId, string OwnerName, string? BankCode, string? AccountNameNormalized);

public sealed record InvoiceRefHit(string DocumentNumber, Guid OwnerId, string OwnerName);

public sealed record MatchInputs(
    string OwnerType,                       // customer (money_in) | supplier (money_out)
    string? CounterpartyAccountNormalized,
    string? CounterpartyBankCode,
    string? CounterpartyNameNormalized,
    IReadOnlyList<AccountOwnerHit> AccountHits,
    IReadOnlyList<MatchParty> TaxCodeHits,
    IReadOnlyList<InvoiceRefHit> InvoiceHits,
    IReadOnlyList<MatchParty> CodeHits,
    IReadOnlyList<MatchParty> NameExactHits,
    IReadOnlyList<MatchParty> NameSimilarHits,
    IReadOnlyList<MatchParty> AmountHits);

public sealed record MatchSignal(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("owner_id")] Guid? OwnerId,
    [property: JsonPropertyName("owner_name")] string? OwnerName,
    [property: JsonPropertyName("confidence")] decimal Confidence,
    [property: JsonPropertyName("detail")] string Detail);

public sealed record MatchResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("method")] string? Method,
    [property: JsonPropertyName("confidence")] decimal Confidence,
    [property: JsonPropertyName("owner_type")] string OwnerType,
    [property: JsonPropertyName("owner_id")] Guid? OwnerId,
    [property: JsonPropertyName("bank_account_id")] Guid? BankAccountId,
    [property: JsonPropertyName("signals")] IReadOnlyList<MatchSignal> Signals,
    [property: JsonPropertyName("candidates")] IReadOnlyList<MatchSignal> Candidates,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

public static class CounterpartyMatcher
{
    private static readonly Dictionary<string, (int Rank, decimal Confidence)> Strong = new()
    {
        [MatchMethods.BankAccountExact] = (1, 1.00m),
        [MatchMethods.TaxCode] = (2, 0.97m),
        [MatchMethods.InvoiceReference] = (3, 0.95m),
        [MatchMethods.CustomerCode] = (4, 0.90m),
        [MatchMethods.NameExact] = (5, 0.85m),
    };

    public static MatchResult Match(MatchInputs input)
    {
        var signals = new List<MatchSignal>();
        var warnings = new List<string>();
        var ambiguous = false;
        Guid? bankAccountId = null;
        var partyLabel = input.OwnerType == BankOwnerTypes.Customer ? "khách hàng" : "nhà cung cấp";

        // 1. Exact known bank account (bank code disambiguates only when both sides carry one).
        var compatible = input.AccountHits
            .Where(h => input.CounterpartyBankCode is null || h.BankCode is null
                        || string.Equals(h.BankCode, input.CounterpartyBankCode, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var sameType = compatible.Where(h => h.OwnerType == input.OwnerType).ToList();
        foreach (var other in compatible.Where(h => h.OwnerType != input.OwnerType))
        {
            warnings.Add($"TK {input.CounterpartyAccountNormalized} thuộc {(other.OwnerType == BankOwnerTypes.Customer ? "khách hàng" : "nhà cung cấp")} {other.OwnerName} — không phải {partyLabel}");
        }
        var accountOwners = sameType.GroupBy(h => h.OwnerId).ToList();
        if (accountOwners.Count == 1)
        {
            var hit = accountOwners[0].First();
            bankAccountId = hit.AccountId;
            signals.Add(new MatchSignal(MatchMethods.BankAccountExact, hit.OwnerId, hit.OwnerName, 1.00m,
                $"Số TK {input.CounterpartyAccountNormalized} có trong danh mục TK ngân hàng của {hit.OwnerName}"));
            // Account matches but the name on the transfer is a different party.
            if (!string.IsNullOrEmpty(input.CounterpartyNameNormalized))
            {
                var expected = new[] { hit.AccountNameNormalized, BankAccountNormalizer.NormalizeName(hit.OwnerName) }
                    .Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList();
                if (expected.Count > 0 && !expected.Contains(input.CounterpartyNameNormalized))
                {
                    var overlap = expected.Max(e => TokenOverlap(e, input.CounterpartyNameNormalized));
                    if (overlap == 0m)
                    {
                        ambiguous = true;
                        warnings.Add($"Tên đối ứng \"{input.CounterpartyNameNormalized}\" khác hẳn tên chủ TK ({hit.OwnerName}) — cần ADMIN kiểm tra");
                    }
                    else warnings.Add($"Tên đối ứng \"{input.CounterpartyNameNormalized}\" không trùng khớp hoàn toàn tên chủ TK ({hit.OwnerName})");
                }
            }
        }
        else if (accountOwners.Count > 1)
        {
            ambiguous = true;
            foreach (var g in accountOwners)
                signals.Add(new MatchSignal(MatchMethods.BankAccountExact, g.Key, g.First().OwnerName, 1.00m,
                    $"Số TK {input.CounterpartyAccountNormalized} được khai báo cho nhiều {partyLabel}"));
            warnings.Add($"Số TK {input.CounterpartyAccountNormalized} thuộc {accountOwners.Count} {partyLabel} — cần ADMIN chọn");
        }

        // 2–5. Other strong signals.
        foreach (var p in input.TaxCodeHits.DistinctBy(p => p.Id))
            signals.Add(new MatchSignal(MatchMethods.TaxCode, p.Id, p.Name, 0.97m, $"MST {p.TaxCode} trong nội dung chuyển khoản"));
        foreach (var g in input.InvoiceHits.GroupBy(h => h.OwnerId))
            signals.Add(new MatchSignal(MatchMethods.InvoiceReference, g.Key, g.First().OwnerName, 0.95m,
                $"Hoá đơn {string.Join(", ", g.Select(h => h.DocumentNumber).Distinct())} trong nội dung thuộc {g.First().OwnerName}"));
        foreach (var p in input.CodeHits.DistinctBy(p => p.Id))
            signals.Add(new MatchSignal(MatchMethods.CustomerCode, p.Id, p.Name, 0.90m, $"Mã {p.Code} trong nội dung"));
        var exactNames = input.NameExactHits.DistinctBy(p => p.Id).ToList();
        if (exactNames.Count == 1)
            signals.Add(new MatchSignal(MatchMethods.NameExact, exactNames[0].Id, exactNames[0].Name, 0.85m,
                $"Tên đối ứng trùng tên {partyLabel} {exactNames[0].Name}"));
        else if (exactNames.Count > 1)
        {
            ambiguous = true;
            foreach (var p in exactNames)
                signals.Add(new MatchSignal(MatchMethods.NameExact, p.Id, p.Name, 0.85m, "Tên đối ứng trùng nhiều đối tác"));
        }

        var owners = signals.Where(s => s.OwnerId is not null).GroupBy(s => s.OwnerId!.Value).ToList();

        // Strong signals disagree (or one signal is itself ambiguous).
        if (owners.Count > 1 || ambiguous)
        {
            return new MatchResult(MatchStatuses.NeedsReview, owners.Count > 1 ? MatchMethods.Conflict : signals.FirstOrDefault()?.Method,
                0.50m, input.OwnerType, null, null, signals,
                owners.Select(g => g.OrderBy(s => Strong[s.Method].Rank).First()).ToList(), warnings);
        }
        if (owners.Count == 1)
        {
            var best = owners[0].OrderBy(s => Strong[s.Method].Rank).First();
            var corroborated = owners[0].Select(s => s.Method).Distinct().Count() > 1;
            var confidence = Math.Min(1.00m, best.Confidence + (corroborated && best.Confidence < 1m ? 0.03m : 0m));
            return new MatchResult(MatchStatuses.Matched, best.Method, confidence, input.OwnerType, best.OwnerId,
                best.Method == MatchMethods.BankAccountExact ? bankAccountId : null, signals, Array.Empty<MatchSignal>(), warnings);
        }

        // 6. Suggestions only — never MATCHED.
        var suggestions = new List<MatchSignal>();
        foreach (var p in input.NameSimilarHits.DistinctBy(p => p.Id))
            suggestions.Add(new MatchSignal(MatchMethods.NameSimilar, p.Id, p.Name, 0.40m, $"Tên đối ứng gần giống {p.Name} (chỉ gợi ý)"));
        foreach (var p in input.AmountHits.DistinctBy(p => p.Id).Where(p => suggestions.All(s => s.OwnerId != p.Id)))
            suggestions.Add(new MatchSignal(MatchMethods.AmountHint, p.Id, p.Name, 0.20m, $"Số tiền bằng số còn nợ một hoá đơn của {p.Name} (chỉ gợi ý — không đủ để xác định)"));
        if (suggestions.Count > 0)
            return new MatchResult(MatchStatuses.NeedsReview, suggestions[0].Method, suggestions.Max(s => s.Confidence),
                input.OwnerType, null, null, suggestions, suggestions, warnings);

        if (warnings.Count > 0)
            return new MatchResult(MatchStatuses.NeedsReview, null, 0m, input.OwnerType, null, null, signals, Array.Empty<MatchSignal>(), warnings);
        return new MatchResult(MatchStatuses.Unmatched, null, 0m, input.OwnerType, null, null, signals, Array.Empty<MatchSignal>(),
            new[] { $"Không có căn cứ tin cậy để xác định {partyLabel}" });
    }

    /// <summary>Share of tokens of <paramref name="a"/> also present in <paramref name="b"/> (0..1).</summary>
    public static decimal TokenOverlap(string a, string b)
    {
        var ta = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length >= 2).ToHashSet();
        var tb = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length >= 2).ToHashSet();
        if (ta.Count == 0 || tb.Count == 0) return 0m;
        return (decimal)ta.Count(tb.Contains) / ta.Count;
    }
}
