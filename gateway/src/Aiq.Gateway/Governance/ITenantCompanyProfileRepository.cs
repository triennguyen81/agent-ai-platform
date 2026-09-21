using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Governance;

/// <summary>
/// AIQ's own legal-entity identity per tenant ("công ty chủ thể") —
/// tenant_company_profiles (migration 049), strictly 1:1 by tenant_id.
/// Deliberately separate from ITenantRepository.GetCompanyTaxIdAsync/
/// SetCompanyTaxIdAsync (tenants.company_tax_id — already load-bearing for
/// InvoiceDirectionResolver's AR/AP check); the two are independent and this
/// migration does not touch that column. Every method here takes tenantId
/// as an explicit parameter that callers MUST derive from
/// AuthenticatedPrincipalContext/TenantContext, never from request input —
/// enforced at the endpoint layer (TenantCompanyProfileEndpoints), not here.
/// </summary>
public interface ITenantCompanyProfileRepository
{
    Task<CompanyProfileRecord?> GetAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// TENANT_OWNER-only create/update (enforced by the endpoint's
    /// "tenant_governance" policy) — a single upsert keyed by the
    /// table's own tenant_id primary key, so "create" and "update" are the
    /// same operation and there is never more than one row per tenant.
    /// </summary>
    Task<CompanyProfileRecord> UpsertAsync(Guid tenantId, Guid actorId, UpsertCompanyProfileRequest request, CancellationToken ct);
}

public sealed record CompanyProfileRecord(
    Guid TenantId, string? LegalName, string? TaxCode, string? ShortName, string[] Aliases,
    string? RegisteredAddress, string? LegalRepresentative, string? RepresentativeTitle,
    string? Phone, string? Email, string? Website, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record UpsertCompanyProfileRequest(
    string? LegalName, string? TaxCode, string? ShortName, string[]? Aliases,
    string? RegisteredAddress, string? LegalRepresentative, string? RepresentativeTitle,
    string? Phone, string? Email, string? Website);

public sealed class TenantCompanyProfileRepository(NpgsqlConnectionFactory factory) : ITenantCompanyProfileRepository
{
    private const string SelectColumns = """
        tenant_id AS "TenantId", legal_name AS "LegalName", tax_code AS "TaxCode",
        short_name AS "ShortName", aliases AS "Aliases", registered_address AS "RegisteredAddress",
        legal_representative AS "LegalRepresentative", representative_title AS "RepresentativeTitle",
        phone AS "Phone", email AS "Email", website AS "Website",
        created_at AS "CreatedAt", updated_at AS "UpdatedAt"
        """;

    public async Task<CompanyProfileRecord?> GetAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<CompanyProfileRecord>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM tenant_company_profiles WHERE tenant_id=@tenantId",
            new { tenantId }, cancellationToken: ct));
    }

    public async Task<CompanyProfileRecord> UpsertAsync(
        Guid tenantId, Guid actorId, UpsertCompanyProfileRequest request, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var row = await conn.QuerySingleAsync<CompanyProfileRecord>(new CommandDefinition($"""
            INSERT INTO tenant_company_profiles (
                tenant_id, legal_name, tax_code, short_name, aliases, registered_address,
                legal_representative, representative_title, phone, email, website, updated_by
            ) VALUES (
                @tenantId, @legalName, @taxCode, @shortName, @aliases, @registeredAddress,
                @legalRepresentative, @representativeTitle, @phone, @email, @website, @actorId
            )
            ON CONFLICT (tenant_id) DO UPDATE SET
                legal_name = EXCLUDED.legal_name,
                tax_code = EXCLUDED.tax_code,
                short_name = EXCLUDED.short_name,
                aliases = EXCLUDED.aliases,
                registered_address = EXCLUDED.registered_address,
                legal_representative = EXCLUDED.legal_representative,
                representative_title = EXCLUDED.representative_title,
                phone = EXCLUDED.phone,
                email = EXCLUDED.email,
                website = EXCLUDED.website,
                updated_by = EXCLUDED.updated_by,
                updated_at = NOW()
            RETURNING {SelectColumns}
            """, new
        {
            tenantId,
            legalName = Normalize(request.LegalName),
            taxCode = Normalize(request.TaxCode),
            shortName = Normalize(request.ShortName),
            aliases = request.Aliases ?? [],
            registeredAddress = Normalize(request.RegisteredAddress),
            legalRepresentative = Normalize(request.LegalRepresentative),
            representativeTitle = Normalize(request.RepresentativeTitle),
            phone = Normalize(request.Phone),
            email = Normalize(request.Email),
            website = Normalize(request.Website),
            actorId,
        }, cancellationToken: ct));
        return row;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
