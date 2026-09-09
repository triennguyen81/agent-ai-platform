namespace Aiq.Gateway.Middleware;

public sealed record PrincipalContext(
    Guid UserId,
    Guid? TenantId,
    Guid? DepartmentId,
    string Role,
    bool UserActive,
    bool? TenantActive,
    bool? DepartmentActive,
    string? DepartmentCode = null,
    string? DepartmentName = null)
{
    public IReadOnlySet<string> Roles => new HashSet<string>([Role], StringComparer.Ordinal);
    public bool IsInRole(string role) => string.Equals(Role, role, StringComparison.Ordinal);
}

public static class AuthenticatedPrincipalContext
{
    public const string ItemKey = "AiqPrincipalContext";

    public static bool TryGet(HttpContext ctx, out PrincipalContext principal)
    {
        if (ctx.Items.TryGetValue(ItemKey, out var raw) && raw is PrincipalContext value)
        {
            principal = value;
            return true;
        }
        principal = null!;
        return false;
    }

    public static PrincipalContext Require(HttpContext ctx) =>
        TryGet(ctx, out var principal)
            ? principal
            : throw new InvalidOperationException("Authenticated principal context is not set.");
}

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
        if (AuthenticatedPrincipalContext.TryGet(ctx, out var principal)
            && principal.TenantId is { } scopedTenant)
        {
            tenantId = scopedTenant;
            return true;
        }
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
        Aiq.Gateway.Data.IUserRepository users,
        Aiq.Gateway.Data.IDepartmentRepository departments)
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
            var persistedRoles = (await users.GetRolesAsync(userId, ctx.RequestAborted))
                .ToHashSet(StringComparer.Ordinal);
            var tokenRoles = ctx.User.FindAll("role").Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
            if (!persistedRoles.SetEquals(tokenRoles))
            {
                _logger.LogWarning("Rejected stale role claims for user {UserId}", userId);
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            if (!Aiq.Gateway.Data.LoginRoleRules.TryGetExactRole(persistedRoles, out var persistedRole)
                || persistedRoles.Count != 1)
            {
                _logger.LogWarning("Rejected non-canonical login-role set for user {UserId}", userId);
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var raw = ctx.User.FindFirst("tenant_id")?.Value;
            Aiq.Gateway.Governance.TenantRecord? tenant = null;
            if (user.TenantId is { } expectedTenant
                && Guid.TryParse(raw, out var claimedTenant)
                && claimedTenant == expectedTenant)
            {
                tenant = await tenants.FindByIdAsync(expectedTenant, ctx.RequestAborted);
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
            else if (!string.IsNullOrWhiteSpace(raw))
            {
                _logger.LogWarning("Rejected unexpected tenant claim for unscoped user {UserId}", userId);
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var isDepartmentMember = persistedRoles.Contains("admin") || persistedRoles.Contains("user");
            Aiq.Gateway.Data.DepartmentRecord? department = null;
            if (isDepartmentMember)
            {
                if (user.TenantId is { } memberTenant && user.DepartmentId is { } departmentId)
                    department = await departments.FindAsync(memberTenant, departmentId, ctx.RequestAborted);
            }
            var membershipError = Aiq.Gateway.Governance.DepartmentAccessPolicy.ValidateMembership(
                user, persistedRoles, department);
            if (membershipError is not null)
            {
                _logger.LogWarning("Rejected invalid department context {Reason} for user {UserId}", membershipError, userId);
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            ctx.Items[AuthenticatedPrincipalContext.ItemKey] = new PrincipalContext(
                userId, user.TenantId, user.DepartmentId, persistedRole,
                user.IsActive, tenant is null ? null : tenant.Status == "active",
                department is null ? null : department.Status == "active",
                department?.Code, department?.Name);
        }

        await _next(ctx);
    }
}
