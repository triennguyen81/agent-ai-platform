namespace Aiq.Gateway.Accounting;

/// <summary>
/// P1 audit fix (ACCAGENT_PROCESS_AUDIT_REPORT.md §3 "Journal-entry proposal
/// ignores the classified outcome") — deterministic routing from an already-
/// classified Luồng 02 (receipt-match) or Luồng 03 (expense-claim) business
/// outcome to the specific journal entries the business process document's
/// closed situational allow-list permits. This never runs an LLM and never
/// invents an account: an outcome this module cannot map with certainty
/// returns NeedsReview, and an outcome that structurally requires a
/// dependency this system does not have (a live FX rate, a signed AR/AP
/// offset document) returns Blocked instead of guessing.
/// </summary>
public enum JournalRoutingOutcome { Proposed, NeedsReview, Blocked }

public sealed record JournalEntryProposal(int EntryNo, string DebitAccount, string CreditAccount, decimal Amount, string Description);

public sealed record JournalRoutingResult(JournalRoutingOutcome Outcome, IReadOnlyList<JournalEntryProposal> Entries, string? Reason)
{
    public static readonly JournalRoutingResult None = new(JournalRoutingOutcome.NeedsReview, Array.Empty<JournalEntryProposal>(), "no_deterministic_entry_for_outcome");
}

public static class JournalRoutingRules
{
    /// <summary>
    /// The complete set of (debit, credit) account pairs the business
    /// process document's "Bút toán đề xuất theo tình huống" table allows
    /// for a receipt or expense_claim voucher. Anything proposed for one of
    /// those two voucher types — whether generated here or submitted
    /// through the free-form journal-suggestions endpoint — must appear in
    /// this set. Deliberately scoped to receipt/expense_claim only: other
    /// voucher types (invoice_vat, contract, ...) have their own, separate
    /// extraction-skill-documented journal conventions this table does not
    /// govern.
    /// </summary>
    public static readonly IReadOnlySet<(string Debit, string Credit)> AllowedPairs = new HashSet<(string, string)>
    {
        ("1111", "131"), ("1121", "131"),   // thu tiền mặt / chuyển khoản
        ("1121", "3387"), ("1111", "3387"), // trả trước — doanh thu chưa thực hiện (manual-only; see ForReceiptOutcome)
        ("1121", "3388"), ("1111", "3388"), // khách trả thừa
        ("635", "515"), ("515", "635"),     // chênh lệch tỷ giá (chiều thực tế) — only when an FX rate source is configured
        ("331", "131"),                     // bù trừ công nợ hai chiều — only with a signed offset document
        ("141", "1111"), ("141", "1121"),   // chi tạm ứng
        ("641", "141"), ("642", "141"), ("627", "141"),
        ("641", "334"), ("642", "334"), ("627", "334"),
        ("641", "3388"), ("642", "3388"), ("627", "3388"),
        ("1331", "141"), ("1331", "334"), ("1331", "3388"), // thuế GTGT khấu trừ khi quyết toán
        ("334", "141"),                      // thu hồi tạm ứng thừa khi payable âm
    };

    public static bool IsAllowed(string debitAccount, string creditAccount) =>
        AllowedPairs.Contains((debitAccount, creditAccount));

    /// <summary>
    /// Luồng 02 — routes a receipt-match outcome (already computed by
    /// IReceiptMatchRepository.ProposeMatchAsync) to its journal entries.
    /// paymentMethod is the voucher's own extracted "TM"/"CK" field; a null/
    /// unknown method means we cannot pick 1111 vs 1121 with certainty, so
    /// this returns NeedsReview rather than defaulting to a guess.
    /// </summary>
    public static JournalRoutingResult ForReceiptOutcome(
        string outcome,
        string? paymentMethod,
        decimal receiptAmount,
        decimal matchedAmount,
        decimal unallocatedAmount,
        bool fxRateConfigured)
    {
        switch (outcome)
        {
            case "FULL_MATCH":
            case "PARTIAL_PAYMENT":
            {
                if (!TryCashOrBankAccount(paymentMethod, out var cashOrBank))
                {
                    return new(JournalRoutingOutcome.NeedsReview, Array.Empty<JournalEntryProposal>(), "payment_method_unknown");
                }
                return new(JournalRoutingOutcome.Proposed,
                    new[] { new JournalEntryProposal(1, cashOrBank, "131", matchedAmount, "Thu tiền công nợ") },
                    null);
            }

            case "OVERPAYMENT":
            {
                if (!TryCashOrBankAccount(paymentMethod, out var cashOrBank))
                {
                    return new(JournalRoutingOutcome.NeedsReview, Array.Empty<JournalEntryProposal>(), "payment_method_unknown");
                }
                return new(JournalRoutingOutcome.Proposed,
                    new[]
                    {
                        new JournalEntryProposal(1, cashOrBank, "131", matchedAmount, "Thu tiền công nợ — phần khớp hoá đơn"),
                        new JournalEntryProposal(2, cashOrBank, "3388", unallocatedAmount, "Khách trả thừa — chờ xác nhận hoàn trả hoặc cấn trừ đợt sau"),
                    },
                    null);
            }

            case "ADVANCE_PAYMENT":
            {
                if (!TryCashOrBankAccount(paymentMethod, out var cashOrBank))
                {
                    return new(JournalRoutingOutcome.NeedsReview, Array.Empty<JournalEntryProposal>(), "payment_method_unknown");
                }
                // Credit 131 (trả trước) is the always-safe default per the
                // document. 3387 (doanh thu chưa thực hiện) is a distinct,
                // stricter sub-case the extracted data has no signal for —
                // never auto-selected, left to a human's explicit override.
                return new(JournalRoutingOutcome.Proposed,
                    new[] { new JournalEntryProposal(1, cashOrBank, "131", receiptAmount, "Khách trả trước khi có hoá đơn") },
                    null);
            }

            case "FOREIGN_CURRENCY":
                return fxRateConfigured
                    ? new JournalRoutingResult(JournalRoutingOutcome.NeedsReview, Array.Empty<JournalEntryProposal>(), "fx_rate_configured_manual_confirmation_required")
                    : new JournalRoutingResult(JournalRoutingOutcome.Blocked, Array.Empty<JournalEntryProposal>(), "no_fx_rate_source_configured");

            case "UNKNOWN_PAYER":
            case "CUSTOMER_NO_OPEN_RECEIVABLE":
            case "REVERSED_TRANSACTION":
                // PDF: "Không" — no journal entry for these, by design.
                return JournalRoutingResult.None;

            default:
                return JournalRoutingResult.None;
        }
    }

    private static bool TryCashOrBankAccount(string? paymentMethod, out string account)
    {
        switch ((paymentMethod ?? string.Empty).Trim().ToUpperInvariant())
        {
            case "TM":
                account = "1111";
                return true;
            case "CK":
                account = "1121";
                return true;
            default:
                account = string.Empty;
                return false;
        }
    }

    /// <summary>
    /// Luồng 03 — routes an approved/partially-approved expense claim's
    /// already-computed totals to the deterministic legs of "Quyết toán chi
    /// phí" that don't depend on a category→GL-account mapping (VAT is
    /// always 1331; the advance-offset and payable/recovery legs are always
    /// 141/334 respectively). The expense-recognition debit leg (which of
    /// 641/642/627 per category) is supplied by the caller only when every
    /// category on the claim has a real, admin-configured account
    /// (department_expense_policies.debit_account) — if any category is
    /// unconfigured, categoryDebitAccounts simply won't have an entry for
    /// it and the whole result comes back NeedsReview rather than a
    /// partially-invented composite entry.
    /// </summary>
    public static JournalRoutingResult ForExpenseSettlement(
        IReadOnlyDictionary<string, decimal> approvedByCategory,
        IReadOnlyDictionary<string, string> categoryDebitAccounts,
        decimal vatDeductibleAmount,
        decimal advanceAmount,
        decimal payableAmount)
    {
        var missingCategories = approvedByCategory.Keys
            .Where(c => !categoryDebitAccounts.ContainsKey(c))
            .ToArray();
        if (missingCategories.Length > 0)
        {
            return new JournalRoutingResult(
                JournalRoutingOutcome.NeedsReview,
                Array.Empty<JournalEntryProposal>(),
                "no_configured_debit_account_for_category:" + string.Join(",", missingCategories));
        }

        // Debit legs (expense recognition per category + VAT) are credited
        // against 141 (advance on hand) first, up to whatever advance was
        // actually taken, then any remainder against 334 (phải trả người
        // lao động) — this is what balances the PDF's own worked example
        // (three debit lines, split 141/334 credit) rather than assigning
        // an entire debit line to one credit account regardless of size.
        var debitLegs = new List<(string Account, decimal Amount, string Description)>();
        foreach (var (category, amount) in approvedByCategory)
        {
            if (amount <= 0m) continue;
            var account = categoryDebitAccounts[category];
            if (!AllowedPairs.Any(p => p.Debit == account))
            {
                // Configured account isn't one this table recognizes at all
                // — never silently propose an out-of-list entry.
                return new JournalRoutingResult(JournalRoutingOutcome.NeedsReview, Array.Empty<JournalEntryProposal>(), $"configured_debit_account_not_allowed:{category}={account}");
            }
            debitLegs.Add((account, amount, $"Chi phí — {category}"));
        }
        if (vatDeductibleAmount > 0m)
        {
            debitLegs.Add(("1331", vatDeductibleAmount, "Thuế GTGT được khấu trừ"));
        }

        var entries = new List<JournalEntryProposal>();
        var n = 1;
        var remainingAdvance = Math.Max(advanceAmount, 0m);

        foreach (var (account, amount, description) in debitLegs)
        {
            var toAdvance = Math.Min(amount, remainingAdvance);
            if (toAdvance > 0m)
            {
                entries.Add(new JournalEntryProposal(n++, account, "141", toAdvance, description));
                remainingAdvance -= toAdvance;
            }
            var toPayable = amount - toAdvance;
            if (toPayable > 0m)
            {
                entries.Add(new JournalEntryProposal(n++, account, "334", toPayable, description));
            }
        }

        if (payableAmount < 0m)
        {
            // Employee owes the company back — the advance covered more
            // than the approved expense. PDF: "chênh lệch... hoặc thu hồi".
            entries.Add(new JournalEntryProposal(n++, "334", "141", -payableAmount, "Thu hồi tạm ứng thừa"));
        }

        if (entries.Count == 0)
        {
            return JournalRoutingResult.None;
        }

        return new JournalRoutingResult(JournalRoutingOutcome.Proposed, entries, null);
    }
}
