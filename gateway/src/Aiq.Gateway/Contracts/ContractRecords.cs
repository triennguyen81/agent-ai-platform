using System.ComponentModel.DataAnnotations;

namespace Aiq.Gateway.Contracts;

public sealed record ContractRecord(
    Guid Id,
    Guid UserId,
    Guid? DepartmentId,
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
    string WorkflowState,
    string DocumentType,
    string? ContractNumber,
    string? ContractCode,
    int? WarrantyMonths,
    Guid? HitlActionId,
    decimal? Subtotal,
    decimal? VatRate,
    decimal? VatAmount,
    decimal? RetentionPercent,
    decimal? PenaltyPercent,
    decimal ProgressPercent,
    string? Direction,
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
    string? ClauseType,
    bool IsOpen,
    string? TriggerEvent,
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

public sealed record ContractPartyDto(string PartyRole, string Name, string? TaxCode, string? Address, string? Representative);
public sealed record ReferencedDocumentDto(string DocumentType, string? ReferencedCode, bool Required = true);

// TriggerEvent/OffsetDays/ExpectedDate model a date that is not yet a
// confirmed legal deadline: it resolves only once TriggerEvent actually
// happens (e.g. "Ký Biên bản giao nhận + hóa đơn GTGT hợp lệ"). When
// TriggerEvent is set, ProcessAsync always stores DueDate as NULL
// server-side regardless of what the caller sends — ExpectedDate is the
// only planning estimate allowed to travel with a conditional date, and it
// is never treated as a confirmed due date.
public sealed record PaymentTermDto(
    int Ordinal, string Description, decimal Percentage, DateOnly? DueDate,
    string? TriggerEvent = null, int? OffsetDays = null, DateOnly? ExpectedDate = null);
public sealed record MilestoneDto(
    int Ordinal, string Name, DateOnly? DueDate, int ReminderDays = 7,
    string? TriggerEvent = null, int? DurationMonths = null, DateOnly? ExpectedDate = null);

public sealed record ProcessContractRequest(
    string? DocumentType,
    string? ContractNumber,
    decimal? Subtotal,
    decimal? VatRate,
    decimal? TotalValue,
    decimal? RetentionPercent,
    decimal? PenaltyPercent,
    IReadOnlyList<ContractPartyDto>? Parties,
    IReadOnlyList<PaymentTermDto>? PaymentTerms,
    IReadOnlyList<MilestoneDto>? Milestones,
    IReadOnlyList<ReferencedDocumentDto>? ReferencedDocuments = null,
    int? WarrantyMonths = null,
    decimal? StandardPenaltyThreshold = 8,
    decimal? StandardRetentionThreshold = 10);

public sealed record ContractProcessResult(string WorkflowState, int AlertCount, int PaymentCount, int MilestoneCount, Guid? HitlActionId = null);
public sealed record ContractAggregate(
    ContractRecord Contract,
    IReadOnlyList<ContractClauseRecord> Clauses,
    IReadOnlyList<dynamic> Parties,
    IReadOnlyList<dynamic> Payments,
    IReadOnlyList<dynamic> Milestones,
    IReadOnlyList<dynamic> Alerts,
    IReadOnlyList<dynamic> CounterpartyProposals,
    IReadOnlyList<dynamic> Documents,
    IReadOnlyList<dynamic> Handoffs,
    IReadOnlyList<dynamic> History);

public sealed record ContractDocumentRecord(
    Guid Id, Guid ContractId, Guid UploadedBy, string DocumentType, string? ReferencedCode,
    string OriginalFilename, string ContentType, string StorageKey, string Sha256, long SizeBytes, DateTime CreatedAt);

public sealed record CreateClauseDto(
    int Ordinal,
    string? Heading,
    [property: Required] string Body,
    bool HasDeadline,
    bool HasPenalty,
    string? ClauseType = null,
    bool IsOpen = false,
    string? TriggerEvent = null);

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
