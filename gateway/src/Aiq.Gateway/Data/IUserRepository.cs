using Dapper;

namespace Aiq.Gateway.Data;

public interface IUserRepository
{
    Task<UserRecord?> FindByEmailAsync(string email, CancellationToken ct);
    Task<UserRecord>  CreateAsync(string email, string passwordHash, string? displayName, CancellationToken ct);
    Task              AssignRoleAsync(Guid userId, string roleName, CancellationToken ct);
    Task<IReadOnlyCollection<string>> GetRolesAsync(Guid userId, CancellationToken ct);
}

public sealed class UserRepository : IUserRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public UserRepository(NpgsqlConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<UserRecord?> FindByEmailAsync(string email, CancellationToken ct)
    {
        const string sql = """
            SELECT id, email, password_hash AS PasswordHash, display_name AS DisplayName,
                   is_active AS IsActive, created_at AS CreatedAt
            FROM users
            WHERE email = @email
            LIMIT 1;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<UserRecord>(
            new CommandDefinition(sql, new { email }, cancellationToken: ct));
    }

    public async Task<UserRecord> CreateAsync(
        string email, string passwordHash, string? displayName, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO users (email, password_hash, display_name)
            VALUES (@email, @passwordHash, @displayName)
            RETURNING id, email, password_hash AS PasswordHash, display_name AS DisplayName,
                      is_active AS IsActive, created_at AS CreatedAt;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleAsync<UserRecord>(
            new CommandDefinition(sql, new { email, passwordHash, displayName },
                cancellationToken: ct));
    }

    public async Task AssignRoleAsync(Guid userId, string roleName, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO user_roles (user_id, role_id)
            SELECT @userId, r.id FROM roles r WHERE r.name = @roleName
            ON CONFLICT DO NOTHING;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(
            new CommandDefinition(sql, new { userId, roleName }, cancellationToken: ct));
    }

    public async Task<IReadOnlyCollection<string>> GetRolesAsync(Guid userId, CancellationToken ct)
    {
        const string sql = """
            SELECT r.name
            FROM roles r
            INNER JOIN user_roles ur ON ur.role_id = r.id
            WHERE ur.user_id = @userId;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        var roles = await conn.QueryAsync<string>(
            new CommandDefinition(sql, new { userId }, cancellationToken: ct));
        return roles.ToArray();
    }
}
