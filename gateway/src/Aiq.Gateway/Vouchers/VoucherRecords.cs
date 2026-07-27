using System.ComponentModel.DataAnnotations;

namespace Aiq.Gateway.Vouchers;

// Dapper materializes rows via parameterless ctor + init setters when the
// record has too many nullable value-type params (which trips Dapper's
// GenerateDeserializerFromMap on constructor matching).
public sealed class VoucherRecord
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public Guid? TenantId { get; init; }
    public string VoucherType { get; init; } = "";
    public string Status { get; init; } = "processing";
    public string? DocumentNumber { get; init; }
    public string? DocumentSeries { get; init; }
    public DateOnly? DocumentDate { get; init; }
    public string? SellerName { get; init; }
    public string? SellerMst { get; init; }
    public string? SellerAddress { get; init; }
    public string? BuyerName { get; init; }
    public string? BuyerMst { get; init; }
    public string? BuyerAddress { get; init; }
    public decimal? Subtotal { get; init; }
    public decimal? VatRate { get; init; }
    public decimal? VatAmount { get; init; }
    public decimal? DiscountAmount { get; init; }
    public decimal? TotalAmount { get; init; }
    public string Currency { get; init; } = "VND";
    public string? AmountInWords { get; init; }
    public string? PaymentMethod { get; init; }
    public bool TctVerified { get; init; }
    public DateTime? TctVerifiedAt { get; init; }
    public string? SourceFilePath { get; init; }
    public string? SourceFileName { get; init; }
    public string? SourceMimeType { get; init; }
    public string? RawOcrText { get; init; }
    public decimal? OcrConfidence { get; init; }
    public string? ErrorMessage { get; init; }
    public string[] Tags { get; init; } = Array.Empty<string>();
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed class VoucherLineItemRecord
{
    public Guid Id { get; init; }
    public Guid VoucherId { get; init; }
    public int Ordinal { get; init; }
    public string? ItemCode { get; init; }
    public string Description { get; init; } = "";
    public string? Unit { get; init; }
    public decimal? Quantity { get; init; }
    public decimal? UnitPrice { get; init; }
    public decimal? Subtotal { get; init; }
    public decimal? VatRate { get; init; }
    public decimal? VatAmount { get; init; }
    public decimal? TotalAmount { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed class VoucherJournalRecord
{
    public Guid Id { get; init; }
    public Guid VoucherId { get; init; }
    public int EntryNo { get; init; }
    public string DebitAccount { get; init; } = "";
    public string CreditAccount { get; init; } = "";
    public decimal Amount { get; init; }
    public string? Description { get; init; }
    public decimal? Confidence { get; init; }
    public Guid? AcceptedBy { get; init; }
    public DateTime? AcceptedAt { get; init; }
    public DateTime? RejectedAt { get; init; }
    public string? RejectReason { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record VoucherDetail(
    VoucherRecord Voucher,
    IReadOnlyList<VoucherLineItemRecord> LineItems,
    IReadOnlyList<VoucherJournalRecord> JournalSuggestions);

public sealed record CreateVoucherLineItemDto(
    int Ordinal,
    string? ItemCode,
    [Required] string Description,
    string? Unit,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal? Subtotal,
    decimal? VatRate,
    decimal? VatAmount,
    decimal? TotalAmount);

public sealed record CreateVoucherRequest(
    [Required] string VoucherType,
    string? DocumentNumber,
    string? DocumentSeries,
    DateOnly? DocumentDate,
    string? SellerName,
    string? SellerMst,
    string? SellerAddress,
    string? BuyerName,
    string? BuyerMst,
    string? BuyerAddress,
    string? PaymentMethod,
    decimal? Subtotal,
    decimal? VatRate,
    decimal? VatAmount,
    decimal? DiscountAmount,
    decimal? TotalAmount,
    string? Currency,
    string? AmountInWords,
    string? SourceFilePath,
    string? SourceFileName,
    string? SourceMimeType,
    string? RawOcrText,
    decimal? OcrConfidence,
    object? ExtractedFields,
    string[]? Tags,
    IReadOnlyList<CreateVoucherLineItemDto>? LineItems);

public sealed record UpdateVoucherRequest(
    string? Status,
    string? DocumentNumber,
    string? DocumentSeries,
    DateOnly? DocumentDate,
    string? SellerName,
    string? SellerMst,
    string? SellerAddress,
    string? BuyerName,
    string? BuyerMst,
    string? BuyerAddress,
    string? PaymentMethod,
    decimal? Subtotal,
    decimal? VatRate,
    decimal? VatAmount,
    decimal? DiscountAmount,
    decimal? TotalAmount,
    string? Currency,
    string? AmountInWords,
    string? ErrorMessage,
    string[]? Tags);

public sealed record SaveJournalSuggestionDto(
    int EntryNo,
    [Required] string DebitAccount,
    [Required] string CreditAccount,
    decimal Amount,
    string? Description,
    decimal? Confidence);

public sealed record SaveJournalSuggestionsRequest(
    IReadOnlyList<SaveJournalSuggestionDto> Entries,
    bool ReplaceExisting);

public sealed record AcceptJournalRequest(
    bool Accept,
    string? RejectReason);

public sealed record VouchersListResponse(
    IReadOnlyList<VoucherRecord> Items,
    int Total);

public sealed record VoucherUploadResponse(
    Guid VoucherId,
    string Status);
