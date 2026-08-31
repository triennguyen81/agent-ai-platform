using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Governance;

public sealed record WorkflowRecord(
    Guid Id, string Name, string? Description, string DefinitionJson, bool IsActive,
    Guid? CreatedBy, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record CreateWorkflowRequest(
    [Required, StringLength(160, MinimumLength = 2)] string Name,
    [StringLength(2000)] string? Description,
    JsonElement? Definition);

public sealed record UpdateWorkflowRequest(
    [StringLength(160, MinimumLength = 2)] string? Name,
    [StringLength(2000)] string? Description,
    JsonElement? Definition,
    bool? IsActive);

/// <summary>Step type names the runtime knows how to execute — kept in sync with runtime/executors.py's _WORKFLOW_STEP_TYPES.</summary>
public static class WorkflowStepTypes
{
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(
        ["prompt", "require_knowledge", "require_tool"], StringComparer.Ordinal);
}

public interface IWorkflowRepository
{
    Task<IReadOnlyList<WorkflowRecord>> ListAsync(bool activeOnly, CancellationToken ct);
    Task<WorkflowRecord?> FindAsync(Guid id, CancellationToken ct);
    Task<WorkflowRecord> CreateAsync(Guid actorId, CreateWorkflowRequest request, CancellationToken ct);
    Task<WorkflowRecord?> UpdateAsync(Guid id, UpdateWorkflowRequest request, CancellationToken ct);
}

public sealed class WorkflowRepository : IWorkflowRepository
{
    private const string Columns = """
        id AS Id, name AS Name, description AS Description, definition::text AS DefinitionJson,
        is_active AS IsActive, created_by AS CreatedBy, created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    private readonly NpgsqlConnectionFactory _factory;
    public WorkflowRepository(NpgsqlConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<WorkflowRecord>> ListAsync(bool activeOnly, CancellationToken ct)
    {
        var sql = $"SELECT {Columns} FROM workflows {(activeOnly ? "WHERE is_active = TRUE" : "")} ORDER BY name";
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<WorkflowRecord>(new CommandDefinition(sql, cancellationToken: ct))).AsList();
    }

    public async Task<WorkflowRecord?> FindAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<WorkflowRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM workflows WHERE id=@id", new { id }, cancellationToken: ct));
    }

    public async Task<WorkflowRecord> CreateAsync(Guid actorId, CreateWorkflowRequest request, CancellationToken ct)
    {
        var definition = request.Definition?.GetRawText() ?? """{"steps":[],"requiresApproval":false}""";
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleAsync<WorkflowRecord>(new CommandDefinition($"""
            INSERT INTO workflows (name, description, definition, created_by)
            VALUES (@Name, @Description, @definition::jsonb, @actorId)
            RETURNING {Columns}
            """, new { request.Name, request.Description, definition, actorId }, cancellationToken: ct));
    }

    public async Task<WorkflowRecord?> UpdateAsync(Guid id, UpdateWorkflowRequest request, CancellationToken ct)
    {
        var definition = request.Definition?.GetRawText();
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<WorkflowRecord>(new CommandDefinition($"""
            UPDATE workflows SET
                name = COALESCE(@Name, name),
                description = COALESCE(@Description, description),
                definition = COALESCE(@definition::jsonb, definition),
                is_active = COALESCE(@IsActive, is_active),
                updated_at = NOW()
            WHERE id = @id
            RETURNING {Columns}
            """, new { id, request.Name, request.Description, definition, request.IsActive }, cancellationToken: ct));
    }
}
