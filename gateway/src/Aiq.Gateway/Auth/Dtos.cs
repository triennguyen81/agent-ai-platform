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
    IReadOnlyCollection<string> Roles);

public sealed record ExternalLinkRequest(
    [property: Required] string Provider,
    [property: Required] string ExternalId,
    [property: Required, EmailAddress] string Email,
    string? DisplayName,
    string[]? AdditionalRoles = null);
