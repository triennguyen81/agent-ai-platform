namespace Aiq.Gateway.Governance;

public enum ManifestAccessMode
{
    /// <summary>Real end-user execution: published-only, tenant/user assignment enforced.</summary>
    EndUser,
    /// <summary>SUPER_ADMIN testing their own agent: current draft (or published) version, no assignment gate.</summary>
    SuperAdminTest,
    Forbidden,
}

/// <summary>
/// Decides how <c>GET /runtime/agents/{agentId}/manifest</c> resolves a caller.
/// Kept as a pure function so the two mutually-exclusive branches (real chat
/// traffic vs. Agent Builder "Test") are unit-testable without hitting the DB.
/// </summary>
public static class ManifestAccessRules
{
    public static ManifestAccessMode Resolve(bool isSuperAdmin, bool isUser) =>
        isSuperAdmin ? ManifestAccessMode.SuperAdminTest
        : isUser ? ManifestAccessMode.EndUser
        : ManifestAccessMode.Forbidden;
}

/// <summary>
/// The "resource_or_tool" policy (Contracts/Vouchers/Checklist endpoints):
/// admin/super_admin always pass directly; a role=user caller passes ONLY
/// with the tool_execution scope claim that the manifest endpoint mints —
/// never a role claim, since TenantContextMiddleware rejects any token whose
/// role claims don't exactly equal the user's persisted DB roles.
/// </summary>
public static class ResourceOrToolPolicy
{
    public static bool IsAuthorized(bool isSuperAdmin, bool isAdmin, bool isUser, bool hasToolExecutionScope) =>
        isSuperAdmin || isAdmin || (isUser && hasToolExecutionScope);
}
