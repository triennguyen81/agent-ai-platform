using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Books;

public interface IBookRepository
{
    Task<BookRecord> CreateAsync(
        string title,
        string? author,
        string language,
        Guid uploadedBy,
        CancellationToken ct);

    Task<BookRecord?> FindByIdAsync(Guid id, CancellationToken ct);

    Task<(IReadOnlyList<BookListItem> Items, int Total)> ListAsync(
        string? status,
        string? query,
        int limit,
        int offset,
        CancellationToken ct);

    Task<IReadOnlyList<BookPageRecord>> GetPagesAsync(Guid bookId, CancellationToken ct);

    Task UpsertPagesAsync(
        Guid bookId,
        IReadOnlyList<UpsertPageInput> pages,
        CancellationToken ct);

    Task UpdateStatusAndPageCountAsync(
        Guid bookId,
        string status,
        int pageCount,
        CancellationToken ct);
}

public sealed record UpsertPageInput(
    int PageNumber,
    string RawText,
    string CorrectedText,
    float Confidence);

public sealed class BookRepository : IBookRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public BookRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<BookRecord> CreateAsync(
        string title,
        string? author,
        string language,
        Guid uploadedBy,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO books (title, author, language, uploaded_by, status)
            VALUES (@title, @author, @language, @uploadedBy, 'pending')
            RETURNING
                id            AS Id,
                title         AS Title,
                author        AS Author,
                language      AS Language,
                source_path   AS SourcePath,
                uploaded_by   AS UploadedBy,
                page_count    AS PageCount,
                status        AS Status,
                created_at    AS CreatedAt,
                updated_at    AS UpdatedAt;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleAsync<BookRecord>(
            new CommandDefinition(
                sql,
                new { title, author, language, uploadedBy },
                cancellationToken: ct));
    }

    public async Task<BookRecord?> FindByIdAsync(Guid id, CancellationToken ct)
    {
        const string sql = """
            SELECT
                id            AS Id,
                title         AS Title,
                author        AS Author,
                language      AS Language,
                source_path   AS SourcePath,
                uploaded_by   AS UploadedBy,
                page_count    AS PageCount,
                status        AS Status,
                created_at    AS CreatedAt,
                updated_at    AS UpdatedAt
            FROM books
            WHERE id = @id;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<BookRecord>(
            new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<(IReadOnlyList<BookListItem> Items, int Total)> ListAsync(
        string? status,
        string? query,
        int limit,
        int offset,
        CancellationToken ct)
    {
        const string sql = """
            WITH filtered AS (
                SELECT id, title, author, language, status, page_count, created_at
                FROM books
                WHERE (@status IS NULL OR status = @status)
                  AND (@query  IS NULL OR title ILIKE '%' || @query || '%')
            )
            SELECT
                (SELECT COUNT(*) FROM filtered) AS Total,
                id          AS Id,
                title       AS Title,
                author      AS Author,
                language    AS Language,
                status      AS Status,
                page_count  AS PageCount,
                created_at  AS CreatedAt
            FROM filtered
            ORDER BY created_at DESC
            LIMIT @limit OFFSET @offset;
            """;

        await using var conn = await _factory.OpenAsync(ct);

        var rows = (await conn.QueryAsync<(int Total, Guid Id, string Title, string? Author,
            string Language, string Status, int? PageCount, DateTime CreatedAt)>(
                new CommandDefinition(
                    sql,
                    new { status, query, limit, offset },
                    cancellationToken: ct))).ToList();

        if (rows.Count == 0)
        {
            // We still need the total count when the page is empty.
            const string countOnly = """
                SELECT COUNT(*)::int FROM books
                WHERE (@status IS NULL OR status = @status)
                  AND (@query  IS NULL OR title ILIKE '%' || @query || '%');
                """;
            var total = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition(countOnly, new { status, query }, cancellationToken: ct));
            return (Array.Empty<BookListItem>(), total);
        }

        var items = rows
            .Select(r => new BookListItem(r.Id, r.Title, r.Author, r.Language, r.Status, r.PageCount, r.CreatedAt))
            .ToArray();

        return (items, rows[0].Total);
    }

    public async Task<IReadOnlyList<BookPageRecord>> GetPagesAsync(Guid bookId, CancellationToken ct)
    {
        const string sql = """
            SELECT
                id              AS Id,
                book_id         AS BookId,
                page_number     AS PageNumber,
                raw_text        AS RawText,
                corrected_text  AS CorrectedText,
                confidence      AS Confidence,
                created_at      AS CreatedAt
            FROM book_pages
            WHERE book_id = @bookId
            ORDER BY page_number ASC;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        var pages = await conn.QueryAsync<BookPageRecord>(
            new CommandDefinition(sql, new { bookId }, cancellationToken: ct));
        return pages.ToArray();
    }

    public async Task UpsertPagesAsync(
        Guid bookId,
        IReadOnlyList<UpsertPageInput> pages,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO book_pages (book_id, page_number, raw_text, corrected_text, confidence)
            VALUES (@bookId, @pageNumber, @rawText, @correctedText, @confidence)
            ON CONFLICT (book_id, page_number) DO UPDATE
            SET raw_text       = EXCLUDED.raw_text,
                corrected_text = EXCLUDED.corrected_text,
                confidence     = EXCLUDED.confidence,
                updated_at     = NOW();
            """;

        await using var conn = await _factory.OpenAsync(ct);
        foreach (var page in pages)
        {
            await conn.ExecuteAsync(
                new CommandDefinition(
                    sql,
                    new
                    {
                        bookId,
                        pageNumber = page.PageNumber,
                        rawText = page.RawText,
                        correctedText = page.CorrectedText,
                        confidence = page.Confidence,
                    },
                    cancellationToken: ct));
        }
    }

    public async Task UpdateStatusAndPageCountAsync(
        Guid bookId,
        string status,
        int pageCount,
        CancellationToken ct)
    {
        const string sql = """
            UPDATE books
            SET status = @status,
                page_count = @pageCount,
                updated_at = NOW()
            WHERE id = @bookId;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(
            new CommandDefinition(sql, new { bookId, status, pageCount }, cancellationToken: ct));
    }
}
