using System.ComponentModel.DataAnnotations;

namespace Aiq.Gateway.Auth;

public sealed record RegisterRequest(
    [Required, EmailAddress] string Email,
    [Required, MinLength(8)] string Password,
    string? DisplayName);

public sealed record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password);

public sealed record AuthResponse(
    string Token,
    DateTimeOffset ExpiresAt,
    Guid UserId,
    string Email,
    IReadOnlyCollection<string> Roles);

public sealed record ExternalLinkRequest(
    [Required] string Provider,
    [Required] string ExternalId,
    [Required, EmailAddress] string Email,
    string? DisplayName,
    string[]? AdditionalRoles = null);
