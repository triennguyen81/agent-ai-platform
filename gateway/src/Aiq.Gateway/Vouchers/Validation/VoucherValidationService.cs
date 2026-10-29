using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aiq.Gateway.Data;
using Aiq.Gateway.Governance;
using Dapper;

namespace Aiq.Gateway.Vouchers.Validation;

public sealed record TenantVoucherSettings(decimal ConfidenceThreshold, IReadOnlyDictionary<string, IReadOnlyList<string>>? RequiredFields);

/// <summary>
/// A validator result plus the voucher state it describes. <c>VoucherUpdatedAt</c>
/// and <c>VoucherFingerprint</c> are what a stored row is compared against to tell
/// whether it still matches the voucher (migration 069).
/// </summary>
public sealed record VoucherValidationReport(
    Guid VoucherId, int ValidatorVersion, bool HasErrors, IReadOnlyList<ValidationIssue> Issues,
    DateTime VoucherUpdatedAt, string VoucherFingerprint, DateTime ValidatedAt);

public interface IVoucherValidationRepository
{
    Task<IReadOnlyList<VatRateEntry>> GetVatRatesAsync(CancellationToken ct);
    Task<TenantVoucherSettings?> GetSettingsAsync(Guid tenantId, CancellationToken ct);
    Task<VoucherValidationReport> SaveAsync(Guid tenantId, VoucherValidationReport report, CancellationToken ct);
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

    public async Task<VoucherValidationReport> SaveAsync(Guid tenantId, VoucherValidationReport report, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var validatedAt = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition("""
            INSERT INTO voucher_validations (voucher_id, tenant_id, validator_version, has_errors, issues, validated_at,
                                             voucher_updated_at, voucher_fingerprint)
            VALUES (@voucherId, @tenantId, @version, @hasErrors, @issues::jsonb, NOW(), @voucherUpdatedAt, @fingerprint)
            ON CONFLICT (voucher_id) DO UPDATE SET
                validator_version   = EXCLUDED.validator_version,
                has_errors          = EXCLUDED.has_errors,
                issues              = EXCLUDED.issues,
                validated_at        = EXCLUDED.validated_at,
                voucher_updated_at  = EXCLUDED.voucher_updated_at,
                voucher_fingerprint = EXCLUDED.voucher_fingerprint
            WHERE voucher_validations.tenant_id = EXCLUDED.tenant_id
            RETURNING validated_at
            """, new
        {
            voucherId = report.VoucherId, tenantId, version = report.ValidatorVersion, hasErrors = report.HasErrors,
            issues = JsonSerializer.Serialize(report.Issues, Json),
            voucherUpdatedAt = report.VoucherUpdatedAt, fingerprint = report.VoucherFingerprint,
        }, cancellationToken: ct));
        return report with { ValidatedAt = validatedAt };
    }
}

/// <summary>
/// Loads what <see cref="VoucherValidator"/> needs (voucher, company MST, VAT
/// table, tenant settings, today in Vietnam) and runs it. <see cref="ComputeAsync"/>
/// never writes (GET); <see cref="RecordAsync"/> also stores the result with the
/// voucher snapshot, and is called only when the voucher changes (and at submit,
/// PR3). Advisory only so far: no state change depends on it.
/// </summary>
public sealed class VoucherValidationService(
    IVoucherRepository vouchers,
    ITenantCompanyProfileRepository companyProfiles,
    IVoucherValidationRepository validations,
    TimeProvider time,
    ILogger<VoucherValidationService> logger)
{
    /// <summary>Null when the voucher is not in this tenant + department.</summary>
    public async Task<VoucherValidationReport?> ComputeAsync(Guid tenantId, Guid departmentId, Guid voucherId, CancellationToken ct)
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
        var result = VoucherValidator.Validate(detail, ctx);
        return new VoucherValidationReport(voucherId, result.ValidatorVersion, result.HasErrors, result.Issues,
            detail.Voucher.UpdatedAt, Fingerprint(detail), time.GetUtcNow().UtcDateTime);
    }

    /// <summary>Computes and stores the latest result. Null when the voucher is not in this tenant + department.</summary>
    public async Task<VoucherValidationReport?> RecordAsync(Guid tenantId, Guid departmentId, Guid voucherId, CancellationToken ct)
    {
        var report = await ComputeAsync(tenantId, departmentId, voucherId, ct);
        return report is null ? null : await validations.SaveAsync(tenantId, report, ct);
    }

    /// <summary>
    /// <see cref="RecordAsync"/> after a voucher change. The change has already
    /// committed, so a failure here is logged, not surfaced: a missing or older
    /// row reads as stale by its snapshot, and submit (PR3) re-validates anyway.
    /// </summary>
    public async Task TryRecordAsync(Guid tenantId, Guid? departmentId, Guid voucherId, CancellationToken ct)
    {
        if (departmentId is not { } dept)
        {
            logger.LogWarning("Voucher {VoucherId}: no department — validation result not stored", voucherId);
            return;
        }
        try
        {
            await RecordAsync(tenantId, dept, voucherId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Voucher {VoucherId}: storing the validation result failed", voucherId);
        }
    }

    /// <summary>SHA-256 (lowercase hex) of the validator input: voucher row, line items, journal suggestions.</summary>
    internal static string Fingerprint(VoucherDetail detail) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { detail.Voucher, detail.LineItems, detail.JournalSuggestions }, VoucherValidationRepository.Json))));

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
