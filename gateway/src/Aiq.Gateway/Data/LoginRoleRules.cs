namespace Aiq.Gateway.Data;

public static class LoginRoleRules
{
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(
        ["super_admin", "tenant_owner", "admin", "user"], StringComparer.OrdinalIgnoreCase);

    public static bool IsAllowed(string? role) =>
        !string.IsNullOrWhiteSpace(role) && Allowed.Contains(role);

    public static bool TryGetExactRole(IEnumerable<string> roles, out string role)
    {
        var supplied = roles.ToArray();
        var persisted = supplied
            .Select(value => value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        role = persisted.Length == 1 && IsAllowed(persisted[0]) ? persisted[0] : string.Empty;
        return supplied.Length == 1 && role.Length > 0;
    }
}
