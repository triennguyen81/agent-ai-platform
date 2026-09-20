namespace Aiq.Gateway.Data;

public sealed record UserRecord(
    Guid Id,
    string Email,
    string PasswordHash,
    string? DisplayName,
    bool IsActive,
    DateTime CreatedAt,
    Guid? TenantId = null,
    Guid? DepartmentId = null,
    string? ExternalProvider = null,
    string? ExternalId = null,
    bool MustChangePassword = false);
