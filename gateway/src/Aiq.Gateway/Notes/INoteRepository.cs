using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Notes;

public interface INoteRepository
{
    Task<NoteRecord> CreateAsync(Guid tenantId, Guid userId, string title, string content, string[] tags, CancellationToken ct);
    Task<NoteRecord?> FindAsync(Guid tenantId, Guid userId, Guid id, CancellationToken ct);
    Task<IReadOnlyList<NoteRecord>> ListAsync(Guid tenantId, Guid userId, CancellationToken ct);
    Task<NoteRecord?> UpdateAsync(Guid tenantId, Guid userId, Guid id, string? title, string? content, string[]? tags, CancellationToken ct);
    Task<bool> DeleteAsync(Guid tenantId, Guid userId, Guid id, CancellationToken ct);
}

public sealed class NoteRepository : INoteRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public NoteRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<NoteRecord> CreateAsync(
        Guid tenantId, Guid userId, string title, string content, string[] tags, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO notes (tenant_id, user_id, title, content, tags)
            VALUES (@tenantId, @userId, @title, @content, @tags)
            RETURNING
                id          AS Id,
                user_id     AS UserId,
                title       AS Title,
                content     AS Content,
                tags        AS Tags,
                created_at  AS CreatedAt,
                updated_at  AS UpdatedAt;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleAsync<NoteRecord>(
            new CommandDefinition(sql, new { tenantId, userId, title, content, tags }, cancellationToken: ct));
    }

    public async Task<NoteRecord?> FindAsync(Guid tenantId, Guid userId, Guid id, CancellationToken ct)
    {
        const string sql = """
            SELECT
                id          AS Id,
                user_id     AS UserId,
                title       AS Title,
                content     AS Content,
                tags        AS Tags,
                created_at  AS CreatedAt,
                updated_at  AS UpdatedAt
            FROM notes
            WHERE id = @id AND tenant_id = @tenantId AND user_id = @userId;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<NoteRecord>(
            new CommandDefinition(sql, new { tenantId, userId, id }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<NoteRecord>> ListAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        const string sql = """
            SELECT
                id          AS Id,
                user_id     AS UserId,
                title       AS Title,
                content     AS Content,
                tags        AS Tags,
                created_at  AS CreatedAt,
                updated_at  AS UpdatedAt
            FROM notes
            WHERE tenant_id = @tenantId AND user_id = @userId
            ORDER BY updated_at DESC
            LIMIT 500;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<NoteRecord>(
            new CommandDefinition(sql, new { tenantId, userId }, cancellationToken: ct));
        return rows.ToArray();
    }

    public async Task<NoteRecord?> UpdateAsync(
        Guid tenantId, Guid userId, Guid id, string? title, string? content, string[]? tags, CancellationToken ct)
    {
        const string sql = """
            UPDATE notes
            SET title      = COALESCE(@title, title),
                content    = COALESCE(@content, content),
                tags       = COALESCE(@tags, tags),
                updated_at = NOW()
            WHERE id = @id AND tenant_id = @tenantId AND user_id = @userId
            RETURNING
                id          AS Id,
                user_id     AS UserId,
                title       AS Title,
                content     AS Content,
                tags        AS Tags,
                created_at  AS CreatedAt,
                updated_at  AS UpdatedAt;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<NoteRecord>(
            new CommandDefinition(sql, new { tenantId, userId, id, title, content, tags }, cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(Guid tenantId, Guid userId, Guid id, CancellationToken ct)
    {
        const string sql = "DELETE FROM notes WHERE id = @id AND tenant_id = @tenantId AND user_id = @userId;";
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(
            new CommandDefinition(sql, new { tenantId, userId, id }, cancellationToken: ct));
        return rows > 0;
    }
}
