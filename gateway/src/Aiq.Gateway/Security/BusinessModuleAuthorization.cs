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

    // R16 — separation of duties. A reviewer may never approve/reject/settle
    // a record they themselves created, regardless of role. Backend-enforced
    // (never trust a frontend-only restriction) — every ADMIN-decision
    // endpoint that settles a business consequence (receipt confirm/reject,
    // expense confirm/reject/grant-exception, invoice apply) must call this
    // before acting. Not gated by role: an ADMIN who happens to be the
    // creator is still blocked — "creator" is the disqualifier, not "USER".
    public static bool IsSelfApproval(Guid creatorUserId, Guid reviewerUserId) => creatorUserId == reviewerUserId;

    public static async Task<bool> CanUseAnyAsync(HttpContext context, ICapabilityRepository capabilities, CancellationToken ct, params string[] codes)
    {
        if (!TryScope(context, out var principal, out var tenantId, out var departmentId)) return false;
        if (principal.IsInRole("admin")) return true;
        foreach (var code in codes)
        {
            var resolved = await capabilities.ResolveAgentAsync(tenantId, departmentId, principal.UserId, false, code, ct);
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
