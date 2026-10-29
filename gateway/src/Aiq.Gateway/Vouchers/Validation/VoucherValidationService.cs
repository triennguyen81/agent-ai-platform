using System.Text.Json;
using Aiq.Gateway.Data;
using Aiq.Gateway.Governance;
using Dapper;

namespace Aiq.Gateway.Vouchers.Validation;

public sealed record TenantVoucherSettings(decimal ConfidenceThreshold, IReadOnlyDictionary<string, IReadOnlyList<string>>? RequiredFields);

public sealed record StoredVoucherValidation(
    Guid VoucherId, int ValidatorVersion, bool HasErrors, IReadOnlyList<ValidationIssue> Issues, DateTime ValidatedAt);

public interface IVoucherValidationRepository
{
    Task<IReadOnlyList<VatRateEntry>> GetVatRatesAsync(CancellationToken ct);
    Task<TenantVoucherSettings?> GetSettingsAsync(Guid tenantId, CancellationToken ct);
    Task<StoredVoucherValidation> SaveAsync(Guid tenantId, Guid voucherId, VoucherValidationResult result, CancellationToken ct);
}

public sealed class VoucherValidationRepository(NpgsqlConnectionFactory factory) : IVoucherValidationRepository
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record VatRateRow(string Code, decimal? Rate, DateTime EffectiveFrom, DateTime? EffectiveTo, string? LegalRef);

    public async Task<IReadOnlyList<VatRateEntry>> GetVatRatesAsync(CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<VatRateRow>(new CommandDefinition("""
            SELECT code AS Code, rate AS Rate, effective_from AS EffectiveFrom, effective_to AS EffectiveTo, legal_ref AS LegalRef
              FROM vat_rates ORDER BY code, effective_from
            """, cancellationToken: ct));
        return rows.Select(r => new VatRateEntry(r.Code, r.Rate, DateOnly.FromDateTime(r.EffectiveFrom),
            r.EffectiveTo is { } to ? DateOnly.FromDateTime(to) : null, r.LegalRef)).ToList();
    }

    public async Task<TenantVoucherSettings?> GetSettingsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<(decimal Threshold, string? Required)>(new CommandDefinition(
            "SELECT confidence_threshold, required_fields::text FROM tenant_voucher_settings WHERE tenant_id=@tenantId",
            new { tenantId }, cancellationToken: ct));
        if (row == default) return null;
        var required = row.Required is null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, string[]>>(row.Required)!
                .ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
        return new TenantVoucherSettings(row.Threshold, required);
    }

    public async Task<StoredVoucherValidation> SaveAsync(Guid tenantId, Guid voucherId, VoucherValidationResult result, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var validatedAt = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition("""
            INSERT INTO voucher_validations (voucher_id, tenant_id, validator_version, has_errors, issues, validated_at)
            VALUES (@voucherId, @tenantId, @version, @hasErrors, @issues::jsonb, NOW())
            ON CONFLICT (voucher_id) DO UPDATE SET
                validator_version = EXCLUDED.validator_version,
                has_errors        = EXCLUDED.has_errors,
                issues            = EXCLUDED.issues,
                validated_at      = EXCLUDED.validated_at
            WHERE voucher_validations.tenant_id = EXCLUDED.tenant_id
            RETURNING validated_at
            """, new
        {
            voucherId, tenantId, version = result.ValidatorVersion, hasErrors = result.HasErrors,
            issues = JsonSerializer.Serialize(result.Issues, Json),
        }, cancellationToken: ct));
        return new StoredVoucherValidation(voucherId, result.ValidatorVersion, result.HasErrors, result.Issues, validatedAt);
    }
}

/// <summary>
/// Loads what <see cref="VoucherValidator"/> needs (voucher, company MST, VAT
/// table, tenant settings, today in Vietnam), runs it, stores the latest
/// result. Advisory only in PR2: no state change depends on it.
/// </summary>
public sealed class VoucherValidationService(
    IVoucherRepository vouchers,
    ITenantCompanyProfileRepository companyProfiles,
    IVoucherValidationRepository validations,
    TimeProvider time)
{
    /// <summary>Null when the voucher is not in this tenant + department.</summary>
    public async Task<StoredVoucherValidation?> ValidateAsync(Guid tenantId, Guid departmentId, Guid voucherId, CancellationToken ct)
    {
        var detail = await vouchers.FindInDepartmentAsync(tenantId, departmentId, voucherId, ct);
        if (detail is null) return null;
        var profile = await companyProfiles.GetAsync(tenantId, ct);
        var settings = await validations.GetSettingsAsync(tenantId, ct);
        var ctx = new VoucherValidationContext(
            profile?.TaxCode,
            await validations.GetVatRatesAsync(ct),
            TodayInVietnam(time),
            settings?.RequiredFields,
            settings?.ConfidenceThreshold ?? 0.80m);
        return await validations.SaveAsync(tenantId, voucherId, VoucherValidator.Validate(detail, ctx), ct);
    }

    // Asia/Ho_Chi_Minh from the tz database; a fixed UTC+7 zone when the image
    // has none (Vietnam has had no DST since 1975, so the two agree).
    private static readonly TimeZoneInfo Vietnam = FindVietnam();

    private static TimeZoneInfo FindVietnam()
    {
        foreach (var id in new[] { "Asia/Ho_Chi_Minh", "SE Asia Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("Asia/Ho_Chi_Minh", TimeSpan.FromHours(7), "Asia/Ho_Chi_Minh", "ICT");
    }

    internal static DateOnly TodayInVietnam(TimeProvider time) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), Vietnam).DateTime);
}
