using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;

namespace Aiq.Gateway.Security;

public static class BusinessModuleAuthorization
{
    public static bool TryScope(HttpContext context, out PrincipalContext principal, out Guid tenantId, out Guid departmentId)
    {
        principal = null!;
        tenantId = departmentId = Guid.Empty;
        if (!AuthenticatedPrincipalContext.TryGet(context, out principal)
            || principal.TenantId is not { } tenant
            || principal.DepartmentId is not { } department
            || (!principal.IsInRole("admin") && !principal.IsInRole("user"))) return false;
        tenantId = tenant;
        departmentId = department;
        return true;
    }

    // ADMIN Department Authority Invariant (docs/ACL_V2_RESOURCE_VISIBILITY_RULES.md §15):
    // a same-department ADMIN has full operational authority — create, review,
    // approve every stage, recognize, pay — INCLUDING on records they created or
    // reviewed themselves. There is deliberately NO creator/claimant/previous-
    // reviewer check for ADMIN (the former R16 IsSelfApproval was removed).
    // USER never reaches a decision endpoint (route policy department_admin);
    // TENANT_OWNER / SUPER_ADMIN fail TryScope; other departments/tenants are
    // not found. A future ADMIN separation-of-duties rule needs an explicit
    // product decision first.

    public static async Task<bool> CanUseAnyAsync(HttpContext context, ICapabilityRepository capabilities, CancellationToken ct, params string[] codes)
    {
        if (!TryScope(context, out var principal, out var tenantId, out var departmentId)) return false;
        if (principal.IsInRole("admin")) return true;
        foreach (var code in codes)
        {
            var resolved = await capabilities.ResolveAgentAsync(tenantId, departmentId, principal.UserId, false, code, null, ct);
            if (resolved.AgentId is not null) return true;
        }
        return false;
    }

    public static TBuilder RequireBusinessCapability<TBuilder>(this TBuilder builder, params string[] codes)
        where TBuilder : IEndpointConventionBuilder
    {
        return builder.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            var capabilities = context.RequestServices.GetRequiredService<ICapabilityRepository>();
            return await CanUseAnyAsync(context, capabilities, context.RequestAborted, codes)
                ? await next(invocation)
                : Results.Forbid();
        });
    }
}
