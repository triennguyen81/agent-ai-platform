using Aiq.Gateway.Data;

namespace Aiq.Gateway.Governance;

public static class DepartmentAccessPolicy
{
    public static string? ValidateMembership(
        UserRecord user,
        IReadOnlySet<string> roles,
        DepartmentRecord? department)
    {
        if (!LoginRoleRules.TryGetExactRole(roles, out var role) || roles.Count != 1)
            return "exactly_one_login_role_required";

        if (role == "super_admin")
            return user.TenantId is null && user.DepartmentId is null
                ? null : "platform_admin_must_be_unscoped";

        if (role == "tenant_owner")
        {
            if (user.TenantId is null) return "tenant_required";
            return user.DepartmentId is null ? null : "tenant_owner_must_be_departmentless";
        }

        if (role is not ("admin" or "user")) return "unsupported_login_role";
        if (user.TenantId is null) return "tenant_required";
        if (user.DepartmentId is null) return "department_required";
        if (department is null || department.TenantId != user.TenantId || department.Id != user.DepartmentId)
            return "department_not_in_tenant";
        return department.Status == "active" ? null : "department_inactive";
    }
}
