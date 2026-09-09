using System.ComponentModel.DataAnnotations;

namespace Aiq.Gateway.Notes;

public sealed record NoteRecord(
    Guid Id,
    Guid UserId,
    string Title,
    string Content,
    string[] Tags,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record CreateNoteRequest(
    [property: Required, StringLength(300, MinimumLength = 1)] string Title,
    string? Content,
    string[]? Tags);

public sealed record UpdateNoteRequest(
    [property: StringLength(300, MinimumLength = 1)] string? Title,
    string? Content,
    string[]? Tags);

public sealed record NotesListResponse(
    IReadOnlyList<NoteRecord> Items,
    int Total);
