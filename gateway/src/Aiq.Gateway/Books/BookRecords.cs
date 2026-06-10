using System.ComponentModel.DataAnnotations;

namespace Aiq.Gateway.Books;

public sealed record BookRecord(
    Guid Id,
    string Title,
    string? Author,
    string Language,
    string? SourcePath,
    Guid? UploadedBy,
    int? PageCount,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record BookPageRecord(
    Guid Id,
    Guid BookId,
    int PageNumber,
    string? RawText,
    string? CorrectedText,
    float? Confidence,
    DateTime CreatedAt);

public sealed record CreateBookRequest(
    [Required, StringLength(500, MinimumLength = 1)] string Title,
    [StringLength(255)] string? Author,
    [StringLength(8)] string? Language);

public sealed record BookListItem(
    Guid Id,
    string Title,
    string? Author,
    string Language,
    string Status,
    int? PageCount,
    DateTime CreatedAt);

public sealed record BookDetailsResponse(
    BookRecord Book,
    IReadOnlyList<BookPageRecord> Pages);

public sealed record BookListResponse(
    IReadOnlyList<BookListItem> Items,
    int Total,
    int Limit,
    int Offset);
