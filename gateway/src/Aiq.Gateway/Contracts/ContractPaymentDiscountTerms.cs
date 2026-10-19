using Aiq.Gateway.Data;
using Aiq.Gateway.Payments;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Contracts;

public sealed record ContractPaymentDiscountTermRecord(
    Guid Id, Guid ContractId, int Revision, bool Enabled, string DiscountType, decimal RatePercent, string Basis,
    int DaysCount, string Currency, string Source, string? SourceReference, string? Note, Guid CreatedBy,
    DateTime CreatedAt, DateTime? SupersededAt);

public sealed record SaveContractPaymentDiscountRequest(
    bool? Enabled, decimal? RatePercent, string? Basis, int? DaysCount, string? Note, string? SourceReference);

public sealed record ContractPaymentDiscountView(
    Guid ContractId, string WorkflowState, string? Direction, bool Editable, bool Effective,
    ContractPaymentDiscountTermRecord? Current, IReadOnlyList<ContractPaymentDiscountTermRecord> Revisions);

public enum DiscountTermSaveStatus { Saved, NotFound, ContractFinalized, Invalid }

public sealed record DiscountTermSaveResult(
    DiscountTermSaveStatus Status, ContractPaymentDiscountTermRecord? Term, IReadOnlyDictionary<string, string[]>? Errors = null);

/// <summary>
/// Structured payment-discount terms owned by a Contract. Editable only
/// while the Contract itself is editable (same window as every other
/// contract field: closed by APPROVED / HANDED_OFF / REJECTED); used for
/// settlement only once the Contract is APPROVED/HANDED_OFF. Every change is
/// a new immutable revision + a contract_history event.
/// </summary>
public interface IContractPaymentDiscountRepository
{
    Task<ContractPaymentDiscountView?> GetAsync(Guid tenantId, Guid departmentId, Guid contractId, CancellationToken ct);
    Task<DiscountTermSaveResult> SaveAsync(Guid tenantId, Guid departmentId, Guid contractId, Guid actorId,
        SaveContractPaymentDiscountRequest request, CancellationToken ct);
}

public static class ContractPaymentDiscountValidation
{
    /// <summary>Exclusive structural bounds; any tighter business ceiling is an accountant decision (not invented here).</summary>
    public const decimal MaxRatePercentExclusive = 100m;
    public const int MaxDays = 3650;

    public static Dictionary<string, string[]> Validate(SaveContractPaymentDiscountRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        if (r.Enabled is null) errors["enabled"] = ["Bắt buộc."];
        if (r.RatePercent is not { } rate) errors["ratePercent"] = ["Bắt buộc."];
        else if (rate <= 0m || rate >= MaxRatePercentExclusive) errors["ratePercent"] = ["Tỷ lệ chiết khấu phải lớn hơn 0 và nhỏ hơn 100%."];
        else if (decimal.Round(rate, 4) != rate) errors["ratePercent"] = ["Tối đa 4 chữ số thập phân."];
        if (r.DaysCount is not { } days) errors["daysCount"] = ["Bắt buộc."];
        else if (days < 0 || days > MaxDays) errors["daysCount"] = [$"Số ngày phải trong khoảng 0–{MaxDays}."];
        if (string.IsNullOrWhiteSpace(r.Basis) || !PaymentDiscountBases.Supported.Contains(r.Basis))
            errors["basis"] = ["Chỉ hỗ trợ mốc tính: ngày hoá đơn (invoice_date)."];
        if (r.Note is { Length: > 1000 }) errors["note"] = ["Tối đa 1000 ký tự."];
        if (r.SourceReference is { Length: > 500 }) errors["sourceReference"] = ["Tối đa 500 ký tự."];
        return errors;
    }
}

public sealed class ContractPaymentDiscountRepository(NpgsqlConnectionFactory factory) : IContractPaymentDiscountRepository
{
    private const string Columns = """
        id AS Id, contract_id AS ContractId, revision AS Revision, enabled AS Enabled, discount_type AS DiscountType,
        rate_percent AS RatePercent, basis AS Basis, days_count AS DaysCount, currency AS Currency, source AS Source,
        source_reference AS SourceReference, note AS Note, created_by AS CreatedBy, created_at AS CreatedAt,
        superseded_at AS SupersededAt
        """;

    private static readonly HashSet<string> FinalizedStates = new(StringComparer.Ordinal) { "APPROVED", "HANDED_OFF", "REJECTED" };

    public async Task<ContractPaymentDiscountView?> GetAsync(Guid tenantId, Guid departmentId, Guid contractId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var contract = await conn.QuerySingleOrDefaultAsync<(string State, string? Direction)?>(new CommandDefinition(
            "SELECT workflow_state, direction FROM contracts WHERE id=@contractId AND tenant_id=@tenantId AND department_id=@departmentId",
            new { contractId, tenantId, departmentId }, cancellationToken: ct));
        if (contract is not { } c) return null;
        var revisions = (await conn.QueryAsync<ContractPaymentDiscountTermRecord>(new CommandDefinition($"""
            SELECT {Columns} FROM contract_payment_discount_terms
            WHERE contract_id=@contractId AND tenant_id=@tenantId AND department_id=@departmentId
            ORDER BY revision DESC
            """, new { contractId, tenantId, departmentId }, cancellationToken: ct))).AsList();
        var current = revisions.FirstOrDefault(r => r.SupersededAt is null);
        return new ContractPaymentDiscountView(contractId, c.State, c.Direction, !FinalizedStates.Contains(c.State),
            ContractEffectiveStates.All.Contains(c.State) && current is { Enabled: true }, current, revisions);
    }

    public async Task<DiscountTermSaveResult> SaveAsync(Guid tenantId, Guid departmentId, Guid contractId, Guid actorId,
        SaveContractPaymentDiscountRequest request, CancellationToken ct)
    {
        var errors = ContractPaymentDiscountValidation.Validate(request);
        if (errors.Count > 0) return new(DiscountTermSaveStatus.Invalid, null, errors);

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // Contract row lock: serialises term edits with each other and with
        // settlement confirmation (which reads the contract FOR SHARE).
        var contract = await conn.QuerySingleOrDefaultAsync<(string State, string? Currency)?>(new CommandDefinition(
            "SELECT workflow_state, currency FROM contracts WHERE id=@contractId AND tenant_id=@tenantId AND department_id=@departmentId FOR UPDATE",
            new { contractId, tenantId, departmentId }, tx, cancellationToken: ct));
        if (contract is not { } c) return new(DiscountTermSaveStatus.NotFound, null);
        if (FinalizedStates.Contains(c.State)) return new(DiscountTermSaveStatus.ContractFinalized, null);

        var previous = await conn.QuerySingleOrDefaultAsync<(Guid Id, int Revision)?>(new CommandDefinition(
            "SELECT id, revision FROM contract_payment_discount_terms WHERE contract_id=@contractId AND superseded_at IS NULL",
            new { contractId }, tx, cancellationToken: ct));
        if (previous is { } p)
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE contract_payment_discount_terms SET superseded_at=NOW(), superseded_by=@actorId WHERE id=@id",
                new { id = p.Id, actorId }, tx, cancellationToken: ct));
        var revision = (await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT MAX(revision) FROM contract_payment_discount_terms WHERE contract_id=@contractId",
            new { contractId }, tx, cancellationToken: ct)) ?? 0) + 1;
        var currency = string.IsNullOrWhiteSpace(c.Currency) ? "VND" : c.Currency.Trim().ToUpperInvariant();
        var term = await conn.QuerySingleAsync<ContractPaymentDiscountTermRecord>(new CommandDefinition($"""
            INSERT INTO contract_payment_discount_terms(tenant_id, department_id, contract_id, revision, enabled, rate_percent,
                basis, days_count, currency, source, source_reference, note, created_by)
            VALUES (@tenantId, @departmentId, @contractId, @revision, @enabled, @rate, @basis, @days, @currency, 'manual',
                @sourceReference, @note, @actorId)
            RETURNING {Columns}
            """, new
        {
            tenantId, departmentId, contractId, revision, enabled = request.Enabled!.Value, rate = request.RatePercent!.Value,
            basis = request.Basis, days = request.DaysCount!.Value, currency,
            sourceReference = Clean(request.SourceReference), note = Clean(request.Note), actorId,
        }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO contract_history(contract_id, tenant_id, department_id, actor_id, event_type, from_state, to_state, details)
            VALUES (@contractId, @tenantId, @departmentId, @actorId, 'payment_discount_term_changed', @state, @state,
                jsonb_build_object('revision', @revision, 'previousRevision', @previousRevision, 'enabled', @enabled,
                    'ratePercent', @rate, 'basis', @basis, 'daysCount', @days, 'currency', @currency, 'source', 'manual'))
            """, new
        {
            contractId, tenantId, departmentId, actorId, state = c.State, revision, previousRevision = previous?.Revision,
            enabled = term.Enabled, rate = term.RatePercent, basis = term.Basis, days = term.DaysCount, currency,
        }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new(DiscountTermSaveStatus.Saved, term);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
