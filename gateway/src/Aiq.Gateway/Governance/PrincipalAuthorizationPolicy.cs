using Aiq.Gateway.Middleware;

namespace Aiq.Gateway.Governance;

public static class PrincipalAuthorizationPolicy
{
    public static PrincipalContext? FromAuthorizationResource(object? resource) =>
        resource is HttpContext http
            && AuthenticatedPrincipalContext.TryGet(http, out var principal) ? principal : null;

    public static bool IsAuthenticated(PrincipalContext? principal) =>
        principal is { UserActive: true };

    public static bool IsPlatformAdmin(PrincipalContext? principal) =>
        principal is { UserActive: true, Role: "super_admin", TenantId: null, DepartmentId: null };

    public static bool IsTenantGovernor(PrincipalContext? principal) =>
        principal is { UserActive: true, Role: "tenant_owner", TenantId: not null,
            TenantActive: true, DepartmentId: null };

    public static bool IsDepartmentAdmin(PrincipalContext? principal) =>
        principal is { UserActive: true, Role: "admin", TenantId: not null,
            TenantActive: true, DepartmentId: not null, DepartmentActive: true };

    public static bool IsDepartmentMember(PrincipalContext? principal) =>
        principal is { UserActive: true, Role: "admin" or "user", TenantId: not null,
            TenantActive: true, DepartmentId: not null, DepartmentActive: true };

    public static bool IsSameTenant(PrincipalContext principal, Guid tenantId) =>
        principal.TenantId == tenantId;

    public static bool IsSameDepartment(PrincipalContext principal, Guid tenantId, Guid departmentId) =>
        principal.TenantId == tenantId && principal.DepartmentId == departmentId;
}
