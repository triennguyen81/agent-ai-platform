using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Data;
using Dapper;

namespace Aiq.Gateway.Governance;

public sealed record ReviewBoardRecord(
    Guid Id,
    Guid TenantId,
    string Name,
    int Quorum,
    DateTime CreatedAt);

public sealed record ReviewBoardMemberRecord(
    Guid BoardId,
    Guid UserId,
    string? UserEmail,
    string Role,
    DateTime AddedAt);

public sealed record ReviewRequestRecord(
    Guid Id,
    Guid TenantId,
    Guid AgentId,
    string AgentCode,
    string AgentName,
    Guid BoardId,
    string BoardName,
    Guid RequestedBy,
    string? RequestedByEmail,
    string TargetState,
    string Status,
    string? Rationale,
    DateTime? DecidedAt,
    DateTime CreatedAt,
    int ApproveCount,
    int RejectCount,
    int Quorum);

public sealed record ReviewVoteRecord(
    Guid RequestId,
    Guid VoterId,
    string? VoterEmail,
    string Vote,
    string? Comment,
    DateTime VotedAt);

public sealed record CreateReviewBoardRequest(
    [property: Required, StringLength(120)] string Name,
    [property: Range(1, 20)] int? Quorum);

public sealed record AddBoardMemberRequest(
    [property: Required] Guid UserId,
    [property: RegularExpression("^(reviewer|chair)$")] string? Role);

public sealed record SubmitReviewRequest(
    [property: Required] Guid BoardId,
    string? Rationale);

public sealed record CastVoteRequest(
    [property: Required, RegularExpression("^(approve|reject)$")] string Vote,
    string? Comment);

public interface IReviewBoardRepository
{
    Task<IReadOnlyList<ReviewBoardRecord>> ListBoardsAsync(Guid tenantId, Guid departmentId, CancellationToken ct);
    Task<ReviewBoardRecord> CreateBoardAsync(Guid tenantId, Guid departmentId, CreateReviewBoardRequest request, CancellationToken ct);
    Task<IReadOnlyList<ReviewBoardMemberRecord>> ListMembersAsync(Guid boardId, CancellationToken ct);
    Task<bool> AddMemberAsync(Guid boardId, AddBoardMemberRequest request, CancellationToken ct);
    Task<bool> RemoveMemberAsync(Guid boardId, Guid userId, CancellationToken ct);

    Task<ReviewRequestRecord> SubmitRequestAsync(
        Guid tenantId, Guid departmentId, Guid agentId, Guid requestedBy, SubmitReviewRequest request, CancellationToken ct);

    Task<IReadOnlyList<ReviewRequestRecord>> ListPendingForUserAsync(Guid userId, Guid tenantId, Guid departmentId, CancellationToken ct);
    Task<IReadOnlyList<ReviewRequestRecord>> ListForAgentAsync(Guid agentId, CancellationToken ct);
    Task<ReviewRequestRecord?> FindRequestAsync(Guid requestId, CancellationToken ct);

    /// <summary>
    /// Cast a vote. If the vote crosses the quorum threshold, the request
    /// transitions to approved (or rejected if any reject vote tips it).
    /// Returns the post-vote request state.
    /// </summary>
    Task<ReviewRequestRecord?> CastVoteAsync(
        Guid requestId, Guid voterId, CastVoteRequest request, CancellationToken ct);

    Task<IReadOnlyList<ReviewVoteRecord>> ListVotesAsync(Guid requestId, CancellationToken ct);

    /// <summary>
    /// Used by AgentRepository when transitioning to production: returns
    /// true if there is at least one approved request for (agentId, 'production').
    /// </summary>
    Task<bool> HasApprovedRequestAsync(Guid agentId, string targetState, CancellationToken ct);
    Task<bool> IsBoardInDepartmentAsync(Guid boardId, Guid tenantId, Guid departmentId, CancellationToken ct);
    Task<bool> IsRequestInDepartmentAsync(Guid requestId, Guid tenantId, Guid departmentId, CancellationToken ct);
}

public sealed class ReviewBoardRepository : IReviewBoardRepository
{
    private const string BoardColumns = """
            id         AS Id,
            tenant_id  AS TenantId,
            name       AS Name,
            quorum     AS Quorum,
            created_at AS CreatedAt
        """;

    private const string RequestColumns = """
            r.id            AS Id,
            r.tenant_id     AS TenantId,
            r.agent_id      AS AgentId,
            a.code          AS AgentCode,
            a.name          AS AgentName,
            r.board_id      AS BoardId,
            rb.name         AS BoardName,
            r.requested_by  AS RequestedBy,
            u.email         AS RequestedByEmail,
            r.target_state  AS TargetState,
            r.status        AS Status,
            r.rationale     AS Rationale,
            r.decided_at    AS DecidedAt,
            r.created_at    AS CreatedAt,
            (SELECT COUNT(*) FROM agent_review_votes v WHERE v.request_id = r.id AND v.vote='approve')::int AS ApproveCount,
            (SELECT COUNT(*) FROM agent_review_votes v WHERE v.request_id = r.id AND v.vote='reject')::int  AS RejectCount,
            rb.quorum       AS Quorum
        """;

    private readonly NpgsqlConnectionFactory _factory;

    public ReviewBoardRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<ReviewBoardRecord>> ListBoardsAsync(Guid tenantId, Guid departmentId, CancellationToken ct)
    {
        var sql = $"SELECT {BoardColumns} FROM review_boards WHERE tenant_id = @tenantId AND department_id = @departmentId ORDER BY name";
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<ReviewBoardRecord>(
            new CommandDefinition(sql, new { tenantId, departmentId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<ReviewBoardRecord> CreateBoardAsync(
        Guid tenantId, Guid departmentId, CreateReviewBoardRequest request, CancellationToken ct)
    {
        var sql = $"""
            INSERT INTO review_boards (tenant_id, department_id, name, quorum)
            VALUES (@tenantId, @departmentId, @Name, COALESCE(@Quorum, 1))
            RETURNING {BoardColumns}
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleAsync<ReviewBoardRecord>(
            new CommandDefinition(sql, new { tenantId, departmentId, request.Name, request.Quorum }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ReviewBoardMemberRecord>> ListMembersAsync(Guid boardId, CancellationToken ct)
    {
        const string sql = """
            SELECT
                m.board_id   AS BoardId,
                m.user_id    AS UserId,
                u.email      AS UserEmail,
                m.role       AS Role,
                m.added_at   AS AddedAt
            FROM review_board_members m
            JOIN users u ON u.id = m.user_id
            WHERE m.board_id = @boardId
            ORDER BY m.added_at
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<ReviewBoardMemberRecord>(
            new CommandDefinition(sql, new { boardId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<bool> AddMemberAsync(Guid boardId, AddBoardMemberRequest request, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO review_board_members (board_id, user_id, role)
            VALUES (@boardId, @UserId, COALESCE(@Role, 'reviewer'))
            ON CONFLICT (board_id, user_id) DO UPDATE SET role = EXCLUDED.role
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(
            new CommandDefinition(sql, new { boardId, request.UserId, request.Role }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<bool> RemoveMemberAsync(Guid boardId, Guid userId, CancellationToken ct)
    {
        const string sql = "DELETE FROM review_board_members WHERE board_id = @boardId AND user_id = @userId";
        await using var conn = await _factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(
            new CommandDefinition(sql, new { boardId, userId }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<ReviewRequestRecord> SubmitRequestAsync(
        Guid tenantId, Guid departmentId, Guid agentId, Guid requestedBy, SubmitReviewRequest request, CancellationToken ct)
    {
        const string insertSql = """
            INSERT INTO agent_review_requests
                (tenant_id, department_id, agent_id, board_id, requested_by, target_state, status, rationale)
            SELECT @tenantId, @departmentId, @agentId, rb.id, @requestedBy, 'production', 'pending', @Rationale
            FROM review_boards rb
            WHERE rb.id=@BoardId AND rb.tenant_id=@tenantId AND rb.department_id=@departmentId
            RETURNING id
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var id = await conn.QuerySingleAsync<Guid>(
            new CommandDefinition(insertSql, new
            {
                tenantId, departmentId, agentId, requestedBy, request.BoardId, request.Rationale,
            }, cancellationToken: ct));
        return (await FindRequestAsync(id, ct))!;
    }

    public async Task<IReadOnlyList<ReviewRequestRecord>> ListPendingForUserAsync(Guid userId, Guid tenantId, Guid departmentId, CancellationToken ct)
    {
        var sql = $"""
            SELECT {RequestColumns}
            FROM agent_review_requests r
            JOIN agents a   ON a.id = r.agent_id
            JOIN review_boards rb ON rb.id = r.board_id
            JOIN review_board_members m ON m.board_id = r.board_id AND m.user_id = @userId
            LEFT JOIN users u ON u.id = r.requested_by
            WHERE r.status = 'pending' AND r.tenant_id=@tenantId AND r.department_id=@departmentId
            ORDER BY r.created_at DESC
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<ReviewRequestRecord>(
            new CommandDefinition(sql, new { userId, tenantId, departmentId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<ReviewRequestRecord>> ListForAgentAsync(Guid agentId, CancellationToken ct)
    {
        var sql = $"""
            SELECT {RequestColumns}
            FROM agent_review_requests r
            JOIN agents a   ON a.id = r.agent_id
            JOIN review_boards rb ON rb.id = r.board_id
            LEFT JOIN users u ON u.id = r.requested_by
            WHERE r.agent_id = @agentId
            ORDER BY r.created_at DESC
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<ReviewRequestRecord>(
            new CommandDefinition(sql, new { agentId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<ReviewRequestRecord?> FindRequestAsync(Guid requestId, CancellationToken ct)
    {
        var sql = $"""
            SELECT {RequestColumns}
            FROM agent_review_requests r
            JOIN agents a   ON a.id = r.agent_id
            JOIN review_boards rb ON rb.id = r.board_id
            LEFT JOIN users u ON u.id = r.requested_by
            WHERE r.id = @requestId
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<ReviewRequestRecord>(
            new CommandDefinition(sql, new { requestId }, cancellationToken: ct));
    }

    public async Task<ReviewRequestRecord?> CastVoteAsync(
        Guid requestId, Guid voterId, CastVoteRequest request, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Ensure voter is on the board for this request
        const string memberSql = """
            SELECT 1
            FROM agent_review_requests r
            JOIN review_board_members m ON m.board_id = r.board_id
            WHERE r.id = @requestId AND m.user_id = @voterId
            """;
        var isMember = await conn.QueryFirstOrDefaultAsync<int?>(
            new CommandDefinition(memberSql, new { requestId, voterId }, transaction: tx, cancellationToken: ct));
        if (isMember is null)
        {
            throw new InvalidOperationException("Voter is not on the review board for this request.");
        }

        // Must still be pending
        const string statusSql = "SELECT status FROM agent_review_requests WHERE id = @requestId FOR UPDATE";
        var status = await conn.QuerySingleAsync<string>(
            new CommandDefinition(statusSql, new { requestId }, transaction: tx, cancellationToken: ct));
        if (status != "pending")
        {
            throw new InvalidOperationException($"Request is no longer pending (status={status}).");
        }

        // Upsert vote
        const string voteSql = """
            INSERT INTO agent_review_votes (request_id, voter_id, vote, comment)
            VALUES (@requestId, @voterId, @Vote, @Comment)
            ON CONFLICT (request_id, voter_id)
            DO UPDATE SET vote = EXCLUDED.vote, comment = EXCLUDED.comment, voted_at = NOW()
            """;
        await conn.ExecuteAsync(new CommandDefinition(voteSql, new
        {
            requestId, voterId, request.Vote, request.Comment,
        }, transaction: tx, cancellationToken: ct));

        // Re-tally
        const string tallySql = """
            SELECT
                rb.quorum,
                (SELECT COUNT(*) FROM agent_review_votes v WHERE v.request_id = r.id AND v.vote='approve')::int AS approve_count,
                (SELECT COUNT(*) FROM agent_review_votes v WHERE v.request_id = r.id AND v.vote='reject')::int  AS reject_count
            FROM agent_review_requests r
            JOIN review_boards rb ON rb.id = r.board_id
            WHERE r.id = @requestId
            """;
        var tally = await conn.QuerySingleAsync<(int Quorum, int ApproveCount, int RejectCount)>(
            new CommandDefinition(tallySql, new { requestId }, transaction: tx, cancellationToken: ct));

        // Decision rules:
        //   - Any reject vote → request rejected immediately (conservative)
        //   - approve_count >= quorum → request approved
        string? newStatus = null;
        if (tally.RejectCount > 0) newStatus = "rejected";
        else if (tally.ApproveCount >= tally.Quorum) newStatus = "approved";

        if (newStatus is not null)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE agent_review_requests SET status = @newStatus, decided_at = NOW() WHERE id = @requestId",
                new { newStatus, requestId }, transaction: tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return await FindRequestAsync(requestId, ct);
    }

    public async Task<IReadOnlyList<ReviewVoteRecord>> ListVotesAsync(Guid requestId, CancellationToken ct)
    {
        const string sql = """
            SELECT
                v.request_id  AS RequestId,
                v.voter_id    AS VoterId,
                u.email       AS VoterEmail,
                v.vote        AS Vote,
                v.comment     AS Comment,
                v.voted_at    AS VotedAt
            FROM agent_review_votes v
            LEFT JOIN users u ON u.id = v.voter_id
            WHERE v.request_id = @requestId
            ORDER BY v.voted_at
            """;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<ReviewVoteRecord>(
            new CommandDefinition(sql, new { requestId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<bool> HasApprovedRequestAsync(Guid agentId, string targetState, CancellationToken ct)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM agent_review_requests
                WHERE agent_id = @agentId
                  AND target_state = @targetState
                  AND status = 'approved'
            )
            """;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(
            new CommandDefinition(sql, new { agentId, targetState }, cancellationToken: ct));
    }

    public async Task<bool> IsBoardInDepartmentAsync(
        Guid boardId, Guid tenantId, Guid departmentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM review_boards
            WHERE id=@boardId AND tenant_id=@tenantId AND department_id=@departmentId)
            """, new { boardId, tenantId, departmentId }, cancellationToken: ct));
    }

    public async Task<bool> IsRequestInDepartmentAsync(
        Guid requestId, Guid tenantId, Guid departmentId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM agent_review_requests
            WHERE id=@requestId AND tenant_id=@tenantId AND department_id=@departmentId)
            """, new { requestId, tenantId, departmentId }, cancellationToken: ct));
    }
}
