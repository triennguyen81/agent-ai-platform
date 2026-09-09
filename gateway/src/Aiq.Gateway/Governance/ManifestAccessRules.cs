namespace Aiq.Gateway.Governance;

public enum ManifestAccessMode
{
    /// <summary>Real end-user execution: published-only, tenant + department + direct user assignment enforced.</summary>
    EndUser,
    /// <summary>
    /// D3: department manager execution — published-only, tenant + department
    /// assignment enforced, deliberately WITHOUT a direct user-agent
    /// assignment requirement (see IDynamicAgentRepository.GetAdminManifestAsync).
    /// </summary>
    AdminDepartmentExecution,
    /// <summary>SUPER_ADMIN testing their own agent: current draft (or published) version, no assignment gate.</summary>
    SuperAdminTest,
    Forbidden,
}

/// <summary>
/// Decides how <c>GET /runtime/agents/{agentId}/manifest</c> resolves a caller.
/// Kept as a pure function so every branch (real chat traffic, department
/// ADMIN execution, Agent Builder "Test") is unit-testable without hitting
/// the DB. Centralizes the one D3 authorization decision this rule exists
/// for — callers branch on the returned mode, they never re-derive it from
/// role strings themselves.
///
/// TENANT_OWNER is deliberately NOT a parameter: per approved D3 policy,
/// "TENANT_OWNER does NOT automatically gain production execution... if
/// owner needs production execution, existing explicit assignment policy
/// remains" — today that explicit policy is "none" (an owner can never be an
/// agent_assignments target), so an owner falls through to Forbidden exactly
/// like any other caller that is none of super_admin/admin/user. This is
/// unchanged from pre-D3 behavior, not a new restriction.
/// </summary>
public static class ManifestAccessRules
{
    public static ManifestAccessMode Resolve(bool isSuperAdmin, bool isAdmin, bool isUser) =>
        isSuperAdmin ? ManifestAccessMode.SuperAdminTest
        : isAdmin ? ManifestAccessMode.AdminDepartmentExecution
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
