namespace Aiq.Gateway.Governance;

/// <summary>
/// Pure validation for TENANT_OWNER's USER&lt;-&gt;ADMIN role-change action
/// (PATCH /tenant/users/{id}/role). Kept as a standalone pure function so
/// every rejection rule is unit-testable without a database.
///
/// Does not, and structurally cannot, permit any transition into or out of
/// tenant_owner/super_admin: a target holding either role is rejected before
/// the current/requested role check ever runs, and requestedRole is only
/// ever accepted as "user" or "admin". Tenant ownership stays a fully
/// separate workflow (migrations 017/018, /system/tenants/{id}/owner*).
/// </summary>
public static class TenantUserRoleChangePolicy
{
    public static (string? Error, string? FromRole) Validate(
        Guid actorId,
        Guid targetId,
        Guid? targetTenantId,
        Guid callerTenantId,
        IReadOnlyCollection<string> targetRoles,
        string requestedRole)
    {
        if (targetTenantId is null || targetTenantId != callerTenantId) return ("cross_tenant", null);
        if (actorId == targetId) return ("cannot_modify_self", null);
        if (targetRoles.Contains("tenant_owner")) return ("target_is_tenant_owner", null);
        if (targetRoles.Contains("super_admin")) return ("target_is_super_admin", null);

        var currentRole = targetRoles.Contains("admin") ? "admin"
            : targetRoles.Contains("user") ? "user"
            : null;
        if (currentRole is null) return ("target_has_no_modifiable_role", null);
        if (requestedRole != "user" && requestedRole != "admin") return ("invalid_requested_role", null);
        if (requestedRole == currentRole) return ("no_change", null);

        return (null, currentRole);
    }
}
