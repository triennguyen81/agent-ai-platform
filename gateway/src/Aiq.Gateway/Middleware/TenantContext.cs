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
        Aiq.Gateway.Governance.ITenantRepository tenants,
        Aiq.Gateway.Data.IUserRepository users)
    {
        if (ctx.User?.Identity?.IsAuthenticated == true)
        {
            var subRaw = ctx.User.FindFirst("sub")?.Value;
            if (!Guid.TryParse(subRaw, out var userId))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            var user = await users.FindByIdAsync(userId, ctx.RequestAborted);
            if (user is null || !user.IsActive)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            var persistedRoles = await users.GetRolesAsync(userId, ctx.RequestAborted);
            var tokenRoles = ctx.User.FindAll("role").Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
            if (!persistedRoles.ToHashSet(StringComparer.Ordinal).SetEquals(tokenRoles))
            {
                _logger.LogWarning("Rejected stale role claims for user {UserId}", userId);
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var raw = ctx.User.FindFirst("tenant_id")?.Value;
            if (user.TenantId is { } expectedTenant
                && Guid.TryParse(raw, out var claimedTenant)
                && claimedTenant == expectedTenant)
            {
                var tenant = await tenants.FindByIdAsync(expectedTenant, ctx.RequestAborted);
                if (tenant is null || tenant.Status != "active")
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                ctx.Items[TenantContext.ItemKey] = expectedTenant;
            }
            else if (user.TenantId is not null)
            {
                _logger.LogWarning("Rejected stale or mismatched tenant claim for user {UserId}", userId);
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }

        await _next(ctx);
    }
}
