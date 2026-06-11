namespace Aiq.Gateway.Data;

public sealed record UserRecord(
    Guid Id,
    string Email,
    string PasswordHash,
    string? DisplayName,
    bool IsActive,
    DateTime CreatedAt,
    string? ExternalProvider = null,
    string? ExternalId = null);
