namespace Aiq.Gateway.Accounting;

// Deterministic SALES vs PURCHASE classification for both an invoice
// (seller_mst/buyer_mst on a voucher) and a contract (seller/buyer party tax
// codes) — same rule, same two inputs either way. Never asks an LLM, never
// reads document text, never trusts a tenant_id/company name supplied by the
// browser: the ONLY trusted "who are we" input is `companyTaxId`, which the
// caller must resolve from server-side tenant configuration
// (tenants.company_tax_id — see migration 045) before calling this.
//
// Root-caused bug: ApplyInvoiceAsync/ApplyExpenseAsync previously trusted
// vouchers.voucher_type ('invoice_vat_out'/'invoice_vat_in') as-is, and that
// field is set by the LLM extraction pipeline from raw document text — the
// exact anti-pattern this resolver replaces at the point AR/AP is created.
public enum InvoiceDirection
{
    Sales,
    Purchase,
    NeedsReview,
}

public sealed record DirectionResolution(InvoiceDirection Direction, string? CounterpartyTaxId, string Reason);

public static class InvoiceDirectionResolver
{
    public static DirectionResolution Resolve(string? sellerTaxId, string? buyerTaxId, string? companyTaxId)
    {
        var seller = Normalize(sellerTaxId);
        var buyer = Normalize(buyerTaxId);
        var company = Normalize(companyTaxId);

        if (company is null)
        {
            return new(InvoiceDirection.NeedsReview, null, "company_tax_id_not_configured");
        }
        if (seller is null || buyer is null)
        {
            return new(InvoiceDirection.NeedsReview, null, "seller_or_buyer_tax_id_missing");
        }

        var sellerIsUs = seller == company;
        var buyerIsUs = buyer == company;

        if (sellerIsUs && !buyerIsUs)
        {
            return new(InvoiceDirection.Sales, buyer, "seller_tax_id_matches_company");
        }
        if (buyerIsUs && !sellerIsUs)
        {
            return new(InvoiceDirection.Purchase, seller, "buyer_tax_id_matches_company");
        }
        if (sellerIsUs && buyerIsUs)
        {
            return new(InvoiceDirection.NeedsReview, null, "both_parties_match_company");
        }
        return new(InvoiceDirection.NeedsReview, null, "neither_party_matches_company");
    }

    // Vietnamese MST comparison must ignore whitespace/case and an optional
    // "-NNN" branch suffix — a branch office invoice (0301234567-001) is
    // still the same legal entity as its parent (0301234567) for direction
    // purposes, same normalization spirit as CustomerValidation's format
    // check (Customers/ICustomerRepository.cs) but comparing, not validating.
    private static string? Normalize(string? taxId)
    {
        if (string.IsNullOrWhiteSpace(taxId)) return null;
        var trimmed = taxId.Trim();
        var dash = trimmed.IndexOf('-');
        var root = dash >= 0 ? trimmed[..dash] : trimmed;
        return root.Length == 0 ? null : root;
    }
}
