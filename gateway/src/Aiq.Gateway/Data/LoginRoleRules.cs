namespace Aiq.Gateway.Data;

public static class LoginRoleRules
{
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(
        ["super_admin", "tenant_owner", "admin", "user"], StringComparer.OrdinalIgnoreCase);

    public static bool IsAllowed(string? role) =>
        !string.IsNullOrWhiteSpace(role) && Allowed.Contains(role);
}
