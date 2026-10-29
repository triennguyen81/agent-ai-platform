using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Vouchers.Lifecycle;

public static class TenantFeatureFlags
{
    /// <summary>Chứng từ flow v2 (QĐ 20): off by default; set per tenant in tenant_feature_flags.</summary>
    public const string VoucherFlowV2 = "voucher_flow_v2";
}

public interface ITenantFeatureFlags
{
    Task<bool> IsEnabledAsync(Guid tenantId, string flag, CancellationToken ct);
}

public sealed class TenantFeatureFlagRepository(NpgsqlConnectionFactory factory) : ITenantFeatureFlags
{
    public async Task<bool> IsEnabledAsync(Guid tenantId, string flag, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool?>(new CommandDefinition(
            "SELECT enabled FROM tenant_feature_flags WHERE tenant_id=@tenantId AND flag=@flag",
            new { tenantId, flag }, cancellationToken: ct)) ?? false;
    }
}
