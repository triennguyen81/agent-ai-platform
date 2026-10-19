using System.Text.RegularExpressions;
using Aiq.Gateway.Data;
using Aiq.Gateway.Payments;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Banking;

// ============================================================================
// Customer / Supplier BANK ACCOUNT MASTER (counterparty_bank_accounts).
// Every method is scoped by (tenantId, departmentId) taken from the
// authenticated principal; the owner must live in that same scope.
// ============================================================================

public static class BankAccountNormalizer
{
    /// <summary>
    /// "0012 3456 789" -> "00123456789". Strips whitespace and safe
    /// separators (. - _ /), upper-cases letters, keeps leading zeros. Never
    /// parsed as a number.
    /// </summary>
    public static string NormalizeAccount(string? account) =>
        Regex.Replace((account ?? string.Empty).Trim().ToUpperInvariant(), @"[\s.\-_/]", "");

    /// <summary>"CÔNG TY TNHH VIỆT TÍN" and "CTY TNHH VIET TIN" both -> "viet tin".</summary>
    public static string NormalizeName(string? name) => CashSettlementRules.NormalizeCompanyName(name);

    public static bool LooksLikeAccount(string normalized) => Regex.IsMatch(normalized, @"^[A-Z0-9]{4,34}$");
}

public static class BankOwnerTypes
{
    public const string Customer = "customer";
    public const string Supplier = "supplier";
}

public sealed record BankAccountRecord(
    Guid Id, string OwnerType, Guid OwnerId, string? BankCode, string? BankName,
    string AccountNumber, string AccountNumberNormalized, string? AccountName, string? Branch,
    string Currency, bool IsPrimary, bool IsActive, string Source, Guid? SourceTransactionId,
    Guid? CreatedBy, DateTime CreatedAt, Guid? UpdatedBy, DateTime UpdatedAt,
    Guid? DeactivatedBy, DateTime? DeactivatedAt);

public sealed record BankAccountInput(
    string? BankCode, string? BankName, string? AccountNumber, string? AccountName,
    string? Branch, string? Currency, bool? IsPrimary);

/// <summary>Another counterparty already holds this account (same tenant+department).</summary>
public sealed record BankAccountConflict(Guid AccountId, string OwnerType, Guid OwnerId, string OwnerName, string? BankCode, string AccountNumber);

public sealed class BankAccountException(string code, string message, object? details = null) : Exception(message)
{
    public string Code { get; } = code;
    public object? Details { get; } = details;
}

public interface IBankAccountRepository
{
    /// <summary>Null when the owner is not in (tenant, department).</summary>
    Task<IReadOnlyList<BankAccountRecord>?> ListAsync(Guid tenantId, Guid departmentId, string ownerType, Guid ownerId, CancellationToken ct);

    Task<BankAccountRecord?> CreateAsync(Guid tenantId, Guid departmentId, string ownerType, Guid ownerId, Guid actorId,
        BankAccountInput input, CancellationToken ct, string source = "manual", Guid? sourceTransactionId = null);

    Task<BankAccountRecord?> UpdateAsync(Guid tenantId, Guid departmentId, string ownerType, Guid ownerId, Guid accountId,
        Guid actorId, BankAccountInput input, CancellationToken ct);

    Task<BankAccountRecord?> SetActiveAsync(Guid tenantId, Guid departmentId, string ownerType, Guid ownerId, Guid accountId,
        Guid actorId, bool active, CancellationToken ct);

    /// <summary>Create inside a caller's transaction (statement-review learning).</summary>
    Task<BankAccountRecord> CreateInTransactionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid departmentId,
        string ownerType, Guid ownerId, Guid actorId, BankAccountInput input, string source, Guid? sourceTransactionId, CancellationToken ct);
}

public sealed class BankAccountRepository(NpgsqlConnectionFactory factory) : IBankAccountRepository
{
    private const string Columns = """
        id AS Id, owner_type AS OwnerType, COALESCE(customer_id, supplier_id) AS OwnerId,
        bank_code AS BankCode, bank_name AS BankName, account_number AS AccountNumber,
        account_number_normalized AS AccountNumberNormalized, account_name AS AccountName, branch AS Branch,
        currency AS Currency, is_primary AS IsPrimary, is_active AS IsActive, source AS Source,
        source_transaction_id AS SourceTransactionId, created_by AS CreatedBy, created_at AS CreatedAt,
        updated_by AS UpdatedBy, updated_at AS UpdatedAt, deactivated_by AS DeactivatedBy, deactivated_at AS DeactivatedAt
        """;

    private static string OwnerColumn(string ownerType) => ownerType switch
    {
        BankOwnerTypes.Customer => "customer_id",
        BankOwnerTypes.Supplier => "supplier_id",
        _ => throw new BankAccountException("invalid_owner_type", "owner_type không hợp lệ"),
    };

    private static string OwnerTable(string ownerType) => ownerType == BankOwnerTypes.Customer ? "sim_customers" : "sim_suppliers";

    private static async Task<bool> OwnerInScopeAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, Guid departmentId,
        string ownerType, Guid ownerId, CancellationToken ct) =>
        await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"SELECT EXISTS (SELECT 1 FROM {OwnerTable(ownerType)} WHERE id=@ownerId AND tenant_id=@tenantId AND department_id=@departmentId)",
            new { ownerId, tenantId, departmentId }, tx, cancellationToken: ct));

    public async Task<IReadOnlyList<BankAccountRecord>?> ListAsync(Guid tenantId, Guid departmentId, string ownerType, Guid ownerId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        if (!await OwnerInScopeAsync(conn, null, tenantId, departmentId, ownerType, ownerId, ct)) return null;
        return (await conn.QueryAsync<BankAccountRecord>(new CommandDefinition($"""
            SELECT {Columns} FROM counterparty_bank_accounts
            WHERE tenant_id=@tenantId AND department_id=@departmentId AND {OwnerColumn(ownerType)}=@ownerId
            ORDER BY is_active DESC, is_primary DESC, created_at
            """, new { tenantId, departmentId, ownerId }, cancellationToken: ct))).ToList();
    }

    public async Task<BankAccountRecord?> CreateAsync(Guid tenantId, Guid departmentId, string ownerType, Guid ownerId, Guid actorId,
        BankAccountInput input, CancellationToken ct, string source = "manual", Guid? sourceTransactionId = null)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (!await OwnerInScopeAsync(conn, tx, tenantId, departmentId, ownerType, ownerId, ct)) return null;
        var created = await CreateInTransactionAsync(conn, tx, tenantId, departmentId, ownerType, ownerId, actorId, input, source, sourceTransactionId, ct);
        await tx.CommitAsync(ct);
        return created;
    }

    public async Task<BankAccountRecord> CreateInTransactionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid departmentId,
        string ownerType, Guid ownerId, Guid actorId, BankAccountInput input, string source, Guid? sourceTransactionId, CancellationToken ct)
    {
        var (number, normalized, bankCode) = Validate(input);
        await LockAccountAsync(conn, tx, tenantId, departmentId, normalized, ct);
        await EnsureNoConflictAsync(conn, tx, tenantId, departmentId, ownerType, ownerId, normalized, bankCode, null, ct);

        // Same owner, same account already active -> idempotent, return it.
        var existing = await conn.QueryFirstOrDefaultAsync<BankAccountRecord>(new CommandDefinition($"""
            SELECT {Columns} FROM counterparty_bank_accounts
            WHERE tenant_id=@tenantId AND department_id=@departmentId AND {OwnerColumn(ownerType)}=@ownerId
              AND account_number_normalized=@normalized AND is_active
              AND COALESCE(upper(bank_code),'') = COALESCE(upper(@bankCode),'')
            """, new { tenantId, departmentId, ownerId, normalized, bankCode }, tx, cancellationToken: ct));
        if (existing is not null) return existing;

        var isPrimary = input.IsPrimary ?? !await conn.ExecuteScalarAsync<bool>(new CommandDefinition($"""
            SELECT EXISTS (SELECT 1 FROM counterparty_bank_accounts
              WHERE tenant_id=@tenantId AND department_id=@departmentId AND {OwnerColumn(ownerType)}=@ownerId AND is_active)
            """, new { tenantId, departmentId, ownerId }, tx, cancellationToken: ct));
        if (isPrimary) await ClearPrimaryAsync(conn, tx, tenantId, departmentId, ownerType, ownerId, actorId, ct);

        return await conn.QuerySingleAsync<BankAccountRecord>(new CommandDefinition($"""
            INSERT INTO counterparty_bank_accounts (tenant_id, department_id, owner_type, {OwnerColumn(ownerType)},
                bank_code, bank_name, account_number, account_number_normalized, account_name, account_name_normalized,
                branch, currency, is_primary, is_active, source, source_transaction_id, created_by, updated_by)
            VALUES (@tenantId, @departmentId, @ownerType, @ownerId,
                @bankCode, @bankName, @number, @normalized, @accountName, @accountNameNormalized,
                @branch, @currency, @isPrimary, TRUE, @source, @sourceTransactionId, @actorId, @actorId)
            RETURNING {Columns}
            """, new
        {
            tenantId, departmentId, ownerType, ownerId, bankCode,
            bankName = Clean(input.BankName), number, normalized,
            accountName = Clean(input.AccountName),
            accountNameNormalized = NullIfEmpty(BankAccountNormalizer.NormalizeName(input.AccountName)),
            branch = Clean(input.Branch), currency = Clean(input.Currency)?.ToUpperInvariant() ?? "VND",
            isPrimary, source, sourceTransactionId, actorId,
        }, tx, cancellationToken: ct));
    }

    public async Task<BankAccountRecord?> UpdateAsync(Guid tenantId, Guid departmentId, string ownerType, Guid ownerId, Guid accountId,
        Guid actorId, BankAccountInput input, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var current = await FindAsync(conn, tx, tenantId, departmentId, ownerType, ownerId, accountId, forUpdate: true, ct);
        if (current is null) return null;

        var merged = new BankAccountInput(
            input.BankCode ?? current.BankCode, input.BankName ?? current.BankName,
            input.AccountNumber ?? current.AccountNumber, input.AccountName ?? current.AccountName,
            input.Branch ?? current.Branch, input.Currency ?? current.Currency, input.IsPrimary ?? current.IsPrimary);
        var (number, normalized, bankCode) = Validate(merged);
        if (current.IsActive)
        {
            await LockAccountAsync(conn, tx, tenantId, departmentId, normalized, ct);
            await EnsureNoConflictAsync(conn, tx, tenantId, departmentId, ownerType, ownerId, normalized, bankCode, accountId, ct);
        }
        if (merged.IsPrimary == true && !current.IsPrimary) await ClearPrimaryAsync(conn, tx, tenantId, departmentId, ownerType, ownerId, actorId, ct);

        var updated = await conn.QuerySingleAsync<BankAccountRecord>(new CommandDefinition($"""
            UPDATE counterparty_bank_accounts SET
                bank_code=@bankCode, bank_name=@bankName, account_number=@number, account_number_normalized=@normalized,
                account_name=@accountName, account_name_normalized=@accountNameNormalized, branch=@branch,
                currency=@currency, is_primary=@isPrimary, updated_by=@actorId, updated_at=NOW()
            WHERE id=@accountId
            RETURNING {Columns}
            """, new
        {
            accountId, bankCode, bankName = Clean(merged.BankName), number, normalized,
            accountName = Clean(merged.AccountName),
            accountNameNormalized = NullIfEmpty(BankAccountNormalizer.NormalizeName(merged.AccountName)),
            branch = Clean(merged.Branch), currency = Clean(merged.Currency)?.ToUpperInvariant() ?? "VND",
            isPrimary = merged.IsPrimary ?? false, actorId,
        }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return updated;
    }

    public async Task<BankAccountRecord?> SetActiveAsync(Guid tenantId, Guid departmentId, string ownerType, Guid ownerId, Guid accountId,
        Guid actorId, bool active, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var current = await FindAsync(conn, tx, tenantId, departmentId, ownerType, ownerId, accountId, forUpdate: true, ct);
        if (current is null) return null;
        if (active && !current.IsActive)
        {
            await LockAccountAsync(conn, tx, tenantId, departmentId, current.AccountNumberNormalized, ct);
            await EnsureNoConflictAsync(conn, tx, tenantId, departmentId, ownerType, ownerId,
                current.AccountNumberNormalized, current.BankCode, accountId, ct);
        }
        // Soft only: statement lines may reference this account as audit
        // evidence, so it is never hard-deleted.
        var updated = await conn.QuerySingleAsync<BankAccountRecord>(new CommandDefinition($"""
            UPDATE counterparty_bank_accounts SET
                is_active=@active,
                is_primary = CASE WHEN @active THEN is_primary ELSE FALSE END,
                deactivated_by = CASE WHEN @active THEN NULL ELSE @actorId END,
                deactivated_at = CASE WHEN @active THEN NULL ELSE NOW() END,
                updated_by=@actorId, updated_at=NOW()
            WHERE id=@accountId
            RETURNING {Columns}
            """, new { accountId, active, actorId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return updated;
    }

    // ------------------------------------------------------------------

    private static (string Number, string Normalized, string? BankCode) Validate(BankAccountInput input)
    {
        var number = (input.AccountNumber ?? string.Empty).Trim();
        var normalized = BankAccountNormalizer.NormalizeAccount(number);
        if (!BankAccountNormalizer.LooksLikeAccount(normalized))
            throw new BankAccountException("invalid_account_number", "Số tài khoản không hợp lệ (4–34 ký tự chữ/số).");
        var bankCode = Clean(input.BankCode)?.ToUpperInvariant();
        return (number, normalized, bankCode);
    }

    private static Task LockAccountAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid departmentId, string normalized, CancellationToken ct) =>
        // Serialises concurrent "add the same account" requests in one scope.
        conn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))",
            new { key = $"cba:{tenantId}:{departmentId}:{normalized}" }, tx, cancellationToken: ct));

    /// <summary>
    /// The same account may not be active for two different counterparties.
    /// Bank code only disambiguates when BOTH sides carry one and they
    /// differ; an unknown bank on either side is treated as a conflict
    /// (never silently assign one number to two owners).
    /// </summary>
    private static async Task EnsureNoConflictAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid departmentId,
        string ownerType, Guid ownerId, string normalized, string? bankCode, Guid? exceptAccountId, CancellationToken ct)
    {
        var conflicts = (await conn.QueryAsync<BankAccountConflict>(new CommandDefinition("""
            SELECT a.id AS AccountId, a.owner_type AS OwnerType, COALESCE(a.customer_id, a.supplier_id) AS OwnerId,
                   COALESCE(c.name, s.name) AS OwnerName, a.bank_code AS BankCode, a.account_number AS AccountNumber
            FROM counterparty_bank_accounts a
            LEFT JOIN sim_customers c ON c.id = a.customer_id
            LEFT JOIN sim_suppliers s ON s.id = a.supplier_id
            WHERE a.tenant_id=@tenantId AND a.department_id=@departmentId AND a.is_active
              AND a.account_number_normalized=@normalized
              AND (@exceptAccountId::uuid IS NULL OR a.id <> @exceptAccountId)
              AND NOT (a.owner_type=@ownerType AND COALESCE(a.customer_id, a.supplier_id)=@ownerId)
              AND (a.bank_code IS NULL OR @bankCode::text IS NULL OR upper(a.bank_code) = upper(@bankCode))
            """, new { tenantId, departmentId, normalized, bankCode, exceptAccountId, ownerType, ownerId }, tx, cancellationToken: ct))).ToList();
        if (conflicts.Count > 0)
        {
            var c = conflicts[0];
            throw new BankAccountException("bank_account_owned_by_other",
                $"Tài khoản {c.AccountNumber} đã thuộc {(c.OwnerType == BankOwnerTypes.Customer ? "khách hàng" : "nhà cung cấp")} {c.OwnerName}.",
                new { conflicts });
        }
    }

    private static Task ClearPrimaryAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid departmentId,
        string ownerType, Guid ownerId, Guid actorId, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition($"""
            UPDATE counterparty_bank_accounts SET is_primary=FALSE, updated_by=@actorId, updated_at=NOW()
            WHERE tenant_id=@tenantId AND department_id=@departmentId AND {OwnerColumn(ownerType)}=@ownerId AND is_primary
            """, new { tenantId, departmentId, ownerId, actorId }, tx, cancellationToken: ct));

    private static Task<BankAccountRecord?> FindAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid departmentId,
        string ownerType, Guid ownerId, Guid accountId, bool forUpdate, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<BankAccountRecord>(new CommandDefinition($"""
            SELECT {Columns} FROM counterparty_bank_accounts
            WHERE id=@accountId AND tenant_id=@tenantId AND department_id=@departmentId AND {OwnerColumn(ownerType)}=@ownerId
            {(forUpdate ? "FOR UPDATE" : "")}
            """, new { accountId, tenantId, departmentId, ownerId }, tx, cancellationToken: ct));

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
