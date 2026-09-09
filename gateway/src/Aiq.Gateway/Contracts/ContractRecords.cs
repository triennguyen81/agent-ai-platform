using System.ComponentModel.DataAnnotations;

namespace Aiq.Gateway.Contracts;

public sealed record ContractRecord(
    Guid Id,
    Guid UserId,
    string Title,
    string? PartyA,
    string? PartyB,
    DateOnly? SignedDate,
    DateOnly? EffectiveDate,
    DateOnly? EndDate,
    decimal? TotalValue,
    string Currency,
    string? SourcePdfPath,
    string? RawText,
    string Status,
    string[] Tags,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ContractClauseRecord(
    Guid Id,
    Guid ContractId,
    int Ordinal,
    string? Heading,
    string Body,
    bool HasDeadline,
    bool HasPenalty,
    DateTime CreatedAt);

public sealed record ContractWithClauses(
    ContractRecord Contract,
    IReadOnlyList<ContractClauseRecord> Clauses);

public sealed record CreateContractRequest(
    [property: Required, StringLength(500, MinimumLength = 1)] string Title,
    string? PartyA,
    string? PartyB,
    DateOnly? SignedDate,
    DateOnly? EffectiveDate,
    DateOnly? EndDate,
    decimal? TotalValue,
    string? Currency,
    string? SourcePdfPath,
    string? RawText,
    string[]? Tags,
    IReadOnlyList<CreateClauseDto>? Clauses);

public sealed record CreateClauseDto(
    int Ordinal,
    string? Heading,
    [property: Required] string Body,
    bool HasDeadline,
    bool HasPenalty);

public sealed record UpdateContractRequest(
    string? Title,
    string? PartyA,
    string? PartyB,
    DateOnly? SignedDate,
    DateOnly? EffectiveDate,
    DateOnly? EndDate,
    decimal? TotalValue,
    string? Currency,
    string? Status,
    string[]? Tags);

public sealed record ContractsListResponse(
    IReadOnlyList<ContractRecord> Items,
    int Total);
