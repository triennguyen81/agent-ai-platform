using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Contracts;

public sealed record ChecklistItemRecord(
    Guid Id,
    Guid ContractId,
    Guid? ClauseId,
    string Title,
    string? Description,
    DateOnly? DueDate,
    int RemindDaysBefore,
    string? AssigneeEmail,
    string Status,
    DateTime? CompletedAt,
    Guid? CompletedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record CreateChecklistItemDto(
    Guid? ClauseId,
    [Required, StringLength(500, MinimumLength = 1)] string Title,
    string? Description,
    DateOnly? DueDate,
    int? RemindDaysBefore,
    string? AssigneeEmail);

public sealed record BulkCreateChecklistRequest(
    [Required] IReadOnlyList<CreateChecklistItemDto> Items,
    bool ReplaceExisting);

public sealed record UpdateChecklistItemRequest(
    string? Title,
    string? Description,
    DateOnly? DueDate,
    int? RemindDaysBefore,
    string? AssigneeEmail,
    string? Status);

public interface IChecklistRepository
{
    Task<IReadOnlyList<ChecklistItemRecord>> ListAsync(
        Guid tenantId, Guid userId, Guid contractId, CancellationToken ct);

    Task<IReadOnlyList<ChecklistItemRecord>> BulkCreateAsync(
        Guid tenantId,
        Guid userId,
        Guid contractId,
        BulkCreateChecklistRequest request,
        CancellationToken ct);

    Task<ChecklistItemRecord?> UpdateAsync(
        Guid tenantId,
        Guid userId,
        Guid itemId,
        UpdateChecklistItemRequest request,
        CancellationToken ct);

    Task<bool> DeleteAsync(Guid tenantId, Guid userId, Guid itemId, CancellationToken ct);
}

public sealed class ChecklistRepository : IChecklistRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public ChecklistRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    private const string SelectColumns = """
            i.id                  AS Id,
            i.contract_id         AS ContractId,
            i.clause_id           AS ClauseId,
            i.title               AS Title,
            i.description         AS Description,
            i.due_date            AS DueDate,
            i.remind_days_before  AS RemindDaysBefore,
            i.assignee_email      AS AssigneeEmail,
            i.status              AS Status,
            i.completed_at        AS CompletedAt,
            i.completed_by        AS CompletedBy,
            i.created_at          AS CreatedAt,
            i.updated_at          AS UpdatedAt
        """;

    public async Task<IReadOnlyList<ChecklistItemRecord>> ListAsync(
        Guid tenantId, Guid userId, Guid contractId, CancellationToken ct)
    {
        var sql = $"""
            SELECT {SelectColumns}
            FROM contract_checklist_items i
            INNER JOIN contracts c ON c.id = i.contract_id
            WHERE c.id = @contractId AND c.tenant_id = @tenantId AND c.user_id = @userId
            ORDER BY
                CASE i.status
                    WHEN 'open' THEN 1
                    WHEN 'in_progress' THEN 2
                    WHEN 'done' THEN 3
                    WHEN 'skipped' THEN 4
                    ELSE 5
                END,
                i.due_date NULLS LAST,
                i.created_at;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<ChecklistItemRecord>(
            new CommandDefinition(sql, new { tenantId, userId, contractId }, cancellationToken: ct));
        return rows.ToArray();
    }

    public async Task<IReadOnlyList<ChecklistItemRecord>> BulkCreateAsync(
        Guid tenantId,
        Guid userId,
        Guid contractId,
        BulkCreateChecklistRequest request,
        CancellationToken ct)
    {
        // Ownership check — caller's user must own the contract within this tenant.
        const string ownsSql = "SELECT 1 FROM contracts WHERE id = @contractId AND tenant_id = @tenantId AND user_id = @userId;";
        await using var conn = await _factory.OpenAsync(ct);
        var owns = await conn.QuerySingleOrDefaultAsync<int?>(
            new CommandDefinition(ownsSql, new { contractId, tenantId, userId }, cancellationToken: ct));
        if (owns is null)
        {
            return Array.Empty<ChecklistItemRecord>();
        }

        await using var tx = await conn.BeginTransactionAsync(ct);

        if (request.ReplaceExisting)
        {
            const string deleteSql = "DELETE FROM contract_checklist_items WHERE contract_id = @contractId AND tenant_id = @tenantId;";
            await conn.ExecuteAsync(
                new CommandDefinition(deleteSql, new { contractId, tenantId }, transaction: tx, cancellationToken: ct));
        }

        const string insertSql = """
            INSERT INTO contract_checklist_items (
                tenant_id, contract_id, clause_id, title, description,
                due_date, remind_days_before, assignee_email
            ) VALUES (
                @tenantId, @contractId, @clauseId, @title, @description,
                @dueDate, @remindDaysBefore, @assigneeEmail
            )
            RETURNING
                id                  AS Id,
                contract_id         AS ContractId,
                clause_id           AS ClauseId,
                title               AS Title,
                description         AS Description,
                due_date            AS DueDate,
                remind_days_before  AS RemindDaysBefore,
                assignee_email      AS AssigneeEmail,
                status              AS Status,
                completed_at        AS CompletedAt,
                completed_by        AS CompletedBy,
                created_at          AS CreatedAt,
                updated_at          AS UpdatedAt;
            """;

        var created = new List<ChecklistItemRecord>();
        foreach (var item in request.Items)
        {
            var row = await conn.QuerySingleAsync<ChecklistItemRecord>(
                new CommandDefinition(
                    insertSql,
                    new
                    {
                        tenantId,
                        contractId,
                        clauseId = item.ClauseId,
                        title = item.Title,
                        description = item.Description,
                        dueDate = item.DueDate,
                        remindDaysBefore = item.RemindDaysBefore ?? 7,
                        assigneeEmail = item.AssigneeEmail,
                    },
                    transaction: tx,
                    cancellationToken: ct));
            created.Add(row);
        }

        await tx.CommitAsync(ct);
        return created;
    }

    public async Task<ChecklistItemRecord?> UpdateAsync(
        Guid tenantId,
        Guid userId,
        Guid itemId,
        UpdateChecklistItemRequest request,
        CancellationToken ct)
    {
        var sql = $"""
            UPDATE contract_checklist_items i SET
                title               = COALESCE(@title, i.title),
                description         = COALESCE(@description, i.description),
                due_date            = COALESCE(@dueDate, i.due_date),
                remind_days_before  = COALESCE(@remindDaysBefore, i.remind_days_before),
                assignee_email      = COALESCE(@assigneeEmail, i.assignee_email),
                status              = COALESCE(@status, i.status),
                completed_by        = CASE
                                          WHEN @status = 'done' THEN @userId
                                          ELSE i.completed_by
                                      END
            FROM contracts c
            WHERE i.id = @itemId
              AND i.contract_id = c.id
              AND c.tenant_id = @tenantId
              AND c.user_id = @userId
            RETURNING {SelectColumns};
            """;

        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ChecklistItemRecord>(
            new CommandDefinition(
                sql,
                new
                {
                    itemId,
                    tenantId,
                    userId,
                    title = request.Title,
                    description = request.Description,
                    dueDate = request.DueDate,
                    remindDaysBefore = request.RemindDaysBefore,
                    assigneeEmail = request.AssigneeEmail,
                    status = request.Status,
                },
                cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(Guid tenantId, Guid userId, Guid itemId, CancellationToken ct)
    {
        const string sql = """
            DELETE FROM contract_checklist_items i
            USING contracts c
            WHERE i.id = @itemId
              AND i.contract_id = c.id
              AND c.tenant_id = @tenantId
              AND c.user_id = @userId;
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(
            new CommandDefinition(sql, new { itemId, tenantId, userId }, cancellationToken: ct));
        return rows > 0;
    }
}
