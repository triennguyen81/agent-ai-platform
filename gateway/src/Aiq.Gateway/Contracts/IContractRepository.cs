using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Contracts;

public interface IContractRepository
{
    Task<ContractWithClauses> CreateAsync(
        Guid tenantId,
        Guid userId,
        CreateContractRequest request,
        CancellationToken ct);

    Task<ContractWithClauses?> FindAsync(Guid tenantId, Guid userId, Guid id, CancellationToken ct);

    Task<IReadOnlyList<ContractRecord>> ListAsync(
        Guid tenantId,
        Guid userId,
        string? statusFilter,
        string? search,
        int limit,
        int offset,
        CancellationToken ct);

    Task<ContractRecord?> UpdateAsync(
        Guid tenantId,
        Guid userId,
        Guid id,
        UpdateContractRequest request,
        CancellationToken ct);

    Task<bool> DeleteAsync(Guid tenantId, Guid userId, Guid id, CancellationToken ct);
}

public sealed class ContractRepository : IContractRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public ContractRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    private const string ContractSelectColumns = """
            id              AS Id,
            user_id         AS UserId,
            title           AS Title,
            party_a         AS PartyA,
            party_b         AS PartyB,
            signed_date     AS SignedDate,
            effective_date  AS EffectiveDate,
            end_date        AS EndDate,
            total_value     AS TotalValue,
            currency        AS Currency,
            source_pdf_path AS SourcePdfPath,
            raw_text        AS RawText,
            status          AS Status,
            tags            AS Tags,
            created_at      AS CreatedAt,
            updated_at      AS UpdatedAt
        """;

    private const string ClauseSelectColumns = """
            id              AS Id,
            contract_id     AS ContractId,
            ordinal         AS Ordinal,
            heading         AS Heading,
            body            AS Body,
            has_deadline    AS HasDeadline,
            has_penalty     AS HasPenalty,
            created_at      AS CreatedAt
        """;

    public async Task<ContractWithClauses> CreateAsync(
        Guid tenantId, Guid userId, CreateContractRequest request, CancellationToken ct)
    {
        var insertContract = $"""
            INSERT INTO contracts (
                tenant_id, user_id, title, party_a, party_b,
                signed_date, effective_date, end_date,
                total_value, currency, source_pdf_path, raw_text, tags
            ) VALUES (
                @tenantId, @userId, @title, @partyA, @partyB,
                @signedDate, @effectiveDate, @endDate,
                @totalValue, @currency, @sourcePdfPath, @rawText, @tags
            )
            RETURNING {ContractSelectColumns};
            """;

        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var contract = await conn.QuerySingleAsync<ContractRecord>(
            new CommandDefinition(
                insertContract,
                new
                {
                    tenantId,
                    userId,
                    title = request.Title,
                    partyA = request.PartyA,
                    partyB = request.PartyB,
                    signedDate = request.SignedDate,
                    effectiveDate = request.EffectiveDate,
                    endDate = request.EndDate,
                    totalValue = request.TotalValue,
                    currency = request.Currency ?? "VND",
                    sourcePdfPath = request.SourcePdfPath,
                    rawText = request.RawText,
                    tags = request.Tags ?? Array.Empty<string>(),
                },
                transaction: tx,
                cancellationToken: ct));

        var clauses = new List<ContractClauseRecord>();
        if (request.Clauses is { Count: > 0 })
        {
            const string insertClause = """
                INSERT INTO contract_clauses (
                    tenant_id, contract_id, ordinal, heading, body, has_deadline, has_penalty
                ) VALUES (
                    @tenantId, @contractId, @ordinal, @heading, @body, @hasDeadline, @hasPenalty
                )
                RETURNING
                    id              AS Id,
                    contract_id     AS ContractId,
                    ordinal         AS Ordinal,
                    heading         AS Heading,
                    body            AS Body,
                    has_deadline    AS HasDeadline,
                    has_penalty     AS HasPenalty,
                    created_at      AS CreatedAt;
                """;

            foreach (var clause in request.Clauses)
            {
                var inserted = await conn.QuerySingleAsync<ContractClauseRecord>(
                    new CommandDefinition(
                        insertClause,
                        new
                        {
                            tenantId,
                            contractId = contract.Id,
                            clause.Ordinal,
                            clause.Heading,
                            clause.Body,
                            clause.HasDeadline,
                            clause.HasPenalty,
                        },
                        transaction: tx,
                        cancellationToken: ct));
                clauses.Add(inserted);
            }
        }

        await tx.CommitAsync(ct);
        return new ContractWithClauses(contract, clauses);
    }

    public async Task<ContractWithClauses?> FindAsync(Guid tenantId, Guid userId, Guid id, CancellationToken ct)
    {
        var contractSql = $"""
            SELECT {ContractSelectColumns}
            FROM contracts
            WHERE id = @id AND tenant_id = @tenantId AND user_id = @userId;
            """;

        var clausesSql = $"""
            SELECT {ClauseSelectColumns}
            FROM contract_clauses
            WHERE contract_id = @id AND tenant_id = @tenantId
            ORDER BY ordinal;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        var contract = await conn.QuerySingleOrDefaultAsync<ContractRecord>(
            new CommandDefinition(contractSql, new { id, tenantId, userId }, cancellationToken: ct));
        if (contract is null)
        {
            return null;
        }

        var clauses = await conn.QueryAsync<ContractClauseRecord>(
            new CommandDefinition(clausesSql, new { id, tenantId }, cancellationToken: ct));
        return new ContractWithClauses(contract, clauses.ToArray());
    }

    public async Task<IReadOnlyList<ContractRecord>> ListAsync(
        Guid tenantId,
        Guid userId,
        string? statusFilter,
        string? search,
        int limit,
        int offset,
        CancellationToken ct)
    {
        var hasStatus = !string.IsNullOrWhiteSpace(statusFilter);
        var hasSearch = !string.IsNullOrWhiteSpace(search);

        var sql = $"""
            SELECT {ContractSelectColumns}
            FROM contracts
            WHERE tenant_id = @tenantId AND user_id = @userId
              {(hasStatus ? "AND status = @statusFilter" : "")}
              {(hasSearch ? "AND title ILIKE @searchPattern" : "")}
            ORDER BY created_at DESC
            LIMIT @limit OFFSET @offset;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<ContractRecord>(
            new CommandDefinition(
                sql,
                new
                {
                    tenantId,
                    userId,
                    statusFilter,
                    searchPattern = hasSearch ? $"%{search}%" : null,
                    limit = Math.Clamp(limit, 1, 200),
                    offset = Math.Max(0, offset),
                },
                cancellationToken: ct));
        return rows.ToArray();
    }

    public async Task<ContractRecord?> UpdateAsync(
        Guid tenantId, Guid userId, Guid id, UpdateContractRequest request, CancellationToken ct)
    {
        var sql = $"""
            UPDATE contracts SET
                title          = COALESCE(@title, title),
                party_a        = COALESCE(@partyA, party_a),
                party_b        = COALESCE(@partyB, party_b),
                signed_date    = COALESCE(@signedDate, signed_date),
                effective_date = COALESCE(@effectiveDate, effective_date),
                end_date       = COALESCE(@endDate, end_date),
                total_value    = COALESCE(@totalValue, total_value),
                currency       = COALESCE(@currency, currency),
                status         = COALESCE(@status, status),
                tags           = COALESCE(@tags, tags)
            WHERE id = @id AND tenant_id = @tenantId AND user_id = @userId
            RETURNING {ContractSelectColumns};
            """;

        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ContractRecord>(
            new CommandDefinition(
                sql,
                new
                {
                    id,
                    tenantId,
                    userId,
                    title = request.Title,
                    partyA = request.PartyA,
                    partyB = request.PartyB,
                    signedDate = request.SignedDate,
                    effectiveDate = request.EffectiveDate,
                    endDate = request.EndDate,
                    totalValue = request.TotalValue,
                    currency = request.Currency,
                    status = request.Status,
                    tags = request.Tags,
                },
                cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(Guid tenantId, Guid userId, Guid id, CancellationToken ct)
    {
        const string sql = "DELETE FROM contracts WHERE id = @id AND tenant_id = @tenantId AND user_id = @userId;";
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(
            new CommandDefinition(sql, new { id, tenantId, userId }, cancellationToken: ct));
        return rows > 0;
    }
}
