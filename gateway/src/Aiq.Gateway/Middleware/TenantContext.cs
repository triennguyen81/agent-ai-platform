namespace Aiq.Gateway.Middleware;

/// <summary>
/// Extracts and provides the active tenant_id for the current request.
/// The TenantContextMiddleware populates <see cref="HttpContext.Items"/> with
/// the parsed value; downstream handlers read it via <see cref="TryGet"/>.
/// </summary>
public static class TenantContext
{
    public const string ItemKey = "AiqTenantId";

    public static bool TryGet(HttpContext ctx, out Guid tenantId)
    {
        if (ctx.Items.TryGetValue(ItemKey, out var raw) && raw is Guid g)
        {
            tenantId = g;
            return true;
        }
        tenantId = Guid.Empty;
        return false;
    }

    public static Guid Require(HttpContext ctx) =>
        TryGet(ctx, out var t)
            ? t
            : throw new InvalidOperationException("Tenant context not set — request lacks tenant_id claim or middleware not configured.");
}

/// <summary>
/// Reads the tenant_id claim from the JWT and stores it in HttpContext.Items.
/// Falls back to the user's first membership if the claim is missing (legacy
/// tokens issued before G1). Sets nothing for unauthenticated requests so
/// downstream handlers can opt-in.
/// </summary>
public sealed class TenantContextMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantContextMiddleware> _logger;

    public TenantContextMiddleware(RequestDelegate next, ILogger<TenantContextMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext ctx,
        Aiq.Gateway.Governance.ITenantRepository tenants)
    {
        if (ctx.User?.Identity?.IsAuthenticated == true)
        {
            var raw = ctx.User.FindFirst("tenant_id")?.Value;
            if (Guid.TryParse(raw, out var tenantId))
            {
                ctx.Items[TenantContext.ItemKey] = tenantId;
            }
            else
            {
                // Legacy token without tenant_id — default to user's first membership.
                var subRaw = ctx.User.FindFirst("sub")?.Value;
                if (Guid.TryParse(subRaw, out var userId))
                {
                    var memberships = await tenants.ListMembershipsForUserAsync(userId, ctx.RequestAborted);
                    if (memberships.Count > 0)
                    {
                        ctx.Items[TenantContext.ItemKey] = memberships[0].TenantId;
                        _logger.LogDebug(
                            "Tenant fallback applied for user {UserId} -> {TenantCode}",
                            userId, memberships[0].TenantCode);
                    }
                }
            }
        }

        await _next(ctx);
    }
}
