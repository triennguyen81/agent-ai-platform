using System.ComponentModel.DataAnnotations;

namespace Aiq.Gateway.Auth;

public sealed record RegisterRequest(
    [property: Required, EmailAddress] string Email,
    [property: Required, MinLength(8)] string Password,
    string? DisplayName);

public sealed record LoginRequest(
    [property: Required, EmailAddress] string Email,
    [property: Required] string Password);

public sealed record AuthResponse(
    string Token,
    DateTimeOffset ExpiresAt,
    Guid UserId,
    string Email,
    IReadOnlyCollection<string> Roles,
    bool MustChangePassword = false);

// [property: ...] required — see the comment on ChangeTenantUserRoleRequest
// in Endpoints/UserEndpoints.cs: a validation attribute on a positional
// record parameter binds to the constructor parameter, not the generated
// property, so Validator.TryValidateObject (reflection-based) silently
// never runs it unless the attribute is placed on the property instead.
public sealed record ChangePasswordRequest(
    [property: Required] string CurrentPassword,
    [property: Required, MinLength(8)] string NewPassword,
    [property: Required] string ConfirmPassword);

public sealed record ResetPasswordRequest(
    [property: Required, MinLength(8)] string NewPassword,
    [property: Required] string ConfirmPassword);

public sealed record ExternalLinkRequest(
    [property: Required] string Provider,
    [property: Required] string ExternalId,
    [property: Required, EmailAddress] string Email,
    string? DisplayName,
    string[]? AdditionalRoles = null);
