using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aiq.Gateway.Data;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Vouchers;

// ============================================================================
// Tax verification gate (xác thực hoá đơn với cơ quan thuế).
//
//   * 'verified' is an ADMIN attestation with evidence. The Gateway compares
//     the attested invoices to the voucher's CURRENT invoice identities and
//     binds the result to their fingerprint.
//   * The Agent / any member may only PROPOSE ('pending_review') or record a
//     negative result; a negative result never overrides an ADMIN
//     verification unless an ADMIN records it.
//   * Any same-department ADMIN may verify — including the expense claim's own
//     submitter (ADMIN Department Authority Invariant).
//   * Any later change of the identity fields invalidates the verification.
// ============================================================================

public static class TaxVerificationStatuses
{
    public const string PendingReview = "pending_review";
    public const string Verified = "verified";
    public const string Invalid = "invalid";
    public const string Unavailable = "unavailable";
}

/// <summary>Who/what produced a tax-verification state (event.source).</summary>
public static class TaxVerificationAuthority
{
    public const string AdminManual = "admin_manual";       // the only VERIFIED source today
    public const string AgentProposal = "agent_proposal";   // never authoritative
    public const string UserProposal = "user_proposal";     // never authoritative
    public const string UserReport = "user_report";         // negative result recorded by a member
    public const string System = "system";                  // invalidation / migration
}

/// <summary>Where the ADMIN looked before attesting (evidence.evidence_source).</summary>
public static class TaxVerificationSources
{
    public const string TctPortal = "tct_portal";             // hoadondientu.gdt.gov.vn
    public const string EinvoiceProvider = "einvoice_provider"; // cổng tra cứu của nhà cung cấp HĐĐT
    public const string OriginalDocument = "original_document"; // bản gốc/HĐ giấy có xác nhận
    public static readonly IReadOnlySet<string> All = new HashSet<string> { TctPortal, EinvoiceProvider, OriginalDocument };
}

/// <summary>One invoice as identified by its own data (MST bên bán, ký hiệu, số, ngày, tổng tiền).</summary>
public sealed record TaxInvoiceIdentity(string SellerMst, string? InvoiceSeries, string InvoiceNumber, DateOnly? InvoiceDate, decimal? TotalAmount)
{
    public string Key => $"{TaxVerificationRules.NormalizeMst(SellerMst)}|{TaxVerificationRules.NormalizeNumber(InvoiceNumber)}";
}

public sealed record TaxAttestedInvoice(string SellerMst, string? InvoiceSeries, string InvoiceNumber, DateOnly? InvoiceDate, decimal? TotalAmount, string? LookupCode);

public sealed record TaxConfirmRequest(string Source, string Note, bool PortalChecked, IReadOnlyList<TaxAttestedInvoice> Invoices);

/// <summary>ADMIN decision "không hợp lệ / không tra cứu được", with evidence of where it was checked.</summary>
public sealed record TaxRejectRequest(string Reason, bool ProviderUnavailable, string? Source, string? LookupReference);

/// <summary>What a proposal (Agent / member) recommends. Never authoritative.</summary>
public static class TaxRecommendations
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        TaxVerificationStatuses.Verified, TaxVerificationStatuses.Invalid, TaxVerificationStatuses.Unavailable,
    };
}

public sealed record TaxEvidenceProblem(string Invoice, string Field, string? Expected, string? Attested);

public sealed class TaxVerificationException(string code, string message, object? details = null) : Exception(message)
{
    public string Code { get; } = code;
    public object? Details { get; } = details;
}

public static class TaxVerificationRules
{
    public static readonly IReadOnlySet<string> VerifiableTypes = new HashSet<string>
    {
        VoucherTypes.InvoiceIn, VoucherTypes.InvoiceOut, VoucherTypes.LegacyInvoice, VoucherTypes.ExpenseClaim,
    };

    public static string NormalizeMst(string? mst) =>
        new string((mst ?? "").Where(c => char.IsDigit(c) || c == '-').ToArray()).Trim('-');

    /// <summary>"0000123" and "123" are the same e-invoice number; non-numeric numbers compare case/space-insensitively.</summary>
    public static string NormalizeNumber(string? number)
    {
        var s = new string((number ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        if (s.Length > 0 && s.All(char.IsDigit)) s = s.TrimStart('0');
        return s.Length == 0 && (number ?? "").Any(char.IsDigit) ? "0" : s;
    }

    public static string NormalizeSeries(string? series) =>
        new string((series ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

    private static string Amount(decimal? a) => a is { } v ? decimal.Round(v, 2).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "";

    /// <summary>
    /// The invoices a voucher stands for. Expense claim: every item that names
    /// an invoice (the same items that set requires_tax_verification). Invoice
    /// vouchers: the voucher's own document. Anything else: none.
    /// </summary>
    public static IReadOnlyList<TaxInvoiceIdentity> IdentitiesOf(VoucherRecord v)
    {
        var type = (v.VoucherType ?? "").ToLowerInvariant();
        if (!VerifiableTypes.Contains(type)) return Array.Empty<TaxInvoiceIdentity>();
        if (type != VoucherTypes.ExpenseClaim)
        {
            return string.IsNullOrWhiteSpace(v.DocumentNumber)
                ? Array.Empty<TaxInvoiceIdentity>()
                : new[] { new TaxInvoiceIdentity(v.SellerMst ?? "", v.DocumentSeries, v.DocumentNumber!, v.DocumentDate, v.TotalAmount) };
        }

        var list = new List<TaxInvoiceIdentity>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(v.ExtractedFieldsJson) ? "{}" : v.ExtractedFieldsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in items.EnumerateArray())
                {
                    if (it.ValueKind != JsonValueKind.Object) continue;
                    var no = Str(it, "invoice_no", "document_number");
                    if (string.IsNullOrWhiteSpace(no)) continue;
                    list.Add(new TaxInvoiceIdentity(Str(it, "vendor_mst", "supplier_mst") ?? "", Str(it, "invoice_series", "ky_hieu"), no!,
                        DateOnly.TryParse(Str(it, "document_date"), System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null,
                        Dec(it, "amount", "so_tien", "total_amount")));
                }
            }
        }
        catch (JsonException) { /* unreadable extraction = nothing verifiable */ }
        return list;
    }

    private static string? Str(JsonElement e, params string[] keys)
    {
        foreach (var k in keys)
            if (e.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString())) return p.GetString()!.Trim();
        return null;
    }

    private static decimal? Dec(JsonElement e, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (!e.TryGetProperty(k, out var p)) continue;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d)) return d;
            if (p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(), System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var s)) return s;
        }
        return null;
    }

    /// <summary>Order-independent hash of the normalized identities; null when there is nothing to verify.</summary>
    public static string? Fingerprint(IReadOnlyList<TaxInvoiceIdentity> identities)
    {
        if (identities.Count == 0) return null;
        var lines = identities
            .Select(i => string.Join("|", NormalizeMst(i.SellerMst), NormalizeSeries(i.InvoiceSeries), NormalizeNumber(i.InvoiceNumber),
                i.InvoiceDate?.ToString("yyyy-MM-dd") ?? "", Amount(i.TotalAmount)))
            .OrderBy(l => l, StringComparer.Ordinal);
        return "v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines)))).ToLowerInvariant();
    }

    /// <summary>
    /// Every current invoice must be attested exactly once with identical data;
    /// nothing else may be attested. The ADMIN attests what the portal showed,
    /// the Gateway decides whether that is THIS voucher's data.
    /// </summary>
    public static IReadOnlyList<TaxEvidenceProblem> Compare(IReadOnlyList<TaxInvoiceIdentity> current, IReadOnlyList<TaxAttestedInvoice> attested)
    {
        var problems = new List<TaxEvidenceProblem>();
        var byKey = attested.GroupBy(a => $"{NormalizeMst(a.SellerMst)}|{NormalizeNumber(a.InvoiceNumber)}").ToDictionary(g => g.Key, g => g.ToList());
        foreach (var dup in byKey.Where(kv => kv.Value.Count > 1))
            problems.Add(new TaxEvidenceProblem(dup.Key, "duplicate", null, $"{dup.Value.Count} lần"));

        foreach (var c in current)
        {
            var label = $"{c.SellerMst} / {c.InvoiceNumber}";
            if (!byKey.TryGetValue(c.Key, out var matches)) { problems.Add(new TaxEvidenceProblem(label, "missing", c.InvoiceNumber, null)); continue; }
            var a = matches[0];
            if (!string.IsNullOrWhiteSpace(c.InvoiceSeries) && NormalizeSeries(c.InvoiceSeries) != NormalizeSeries(a.InvoiceSeries))
                problems.Add(new TaxEvidenceProblem(label, "invoice_series", c.InvoiceSeries, a.InvoiceSeries));
            if (c.InvoiceDate is null) problems.Add(new TaxEvidenceProblem(label, "voucher_invoice_date_missing", null, a.InvoiceDate?.ToString("yyyy-MM-dd")));
            else if (c.InvoiceDate != a.InvoiceDate) problems.Add(new TaxEvidenceProblem(label, "invoice_date", c.InvoiceDate?.ToString("yyyy-MM-dd"), a.InvoiceDate?.ToString("yyyy-MM-dd")));
            if (c.TotalAmount is null) problems.Add(new TaxEvidenceProblem(label, "voucher_total_missing", null, Amount(a.TotalAmount)));
            else if (a.TotalAmount is null || decimal.Round(c.TotalAmount.Value, 2) != decimal.Round(a.TotalAmount.Value, 2))
                problems.Add(new TaxEvidenceProblem(label, "total_amount", Amount(c.TotalAmount), Amount(a.TotalAmount)));
        }
        var currentKeys = current.Select(c => c.Key).ToHashSet();
        foreach (var extra in byKey.Keys.Where(k => !currentKeys.Contains(k)))
            problems.Add(new TaxEvidenceProblem(extra, "not_on_voucher", null, extra));
        return problems;
    }

    /// <summary>
    /// The expense-claim payment gate, from authoritative DB state only (never
    /// extracted JSON, browser input or LLM text). Null = may continue.
    /// </summary>
    public static string? ExpenseGateError(VoucherRecord v, bool requiresTaxVerification) =>
        !requiresTaxVerification || IsVerifiedForCurrentData(v) ? null
        : v.TaxVerificationStatus == TaxVerificationStatuses.Verified ? "tax_verification_stale"
        : "tax_verification_required";

    /// <summary>The gate: an ADMIN-attested verification of exactly the voucher's current invoices.</summary>
    public static bool IsVerifiedForCurrentData(VoucherRecord v) =>
        v.TaxVerificationStatus == TaxVerificationStatuses.Verified
        && v.TaxVerifiedBy is not null
        && v.TaxVerificationFingerprint is { } fp
        && fp == Fingerprint(IdentitiesOf(v));
}

public sealed record TaxVerificationEventView(
    Guid Id, string Event, string ActorType, Guid? ActorId, string? ActorName, string? ActorRole, string? Source,
    string? StatusBefore, string? StatusAfter, string? Reason, string? EvidenceJson, DateTime CreatedAt);

public sealed record TaxVerificationView(
    Guid VoucherId, string VoucherType, bool Verifiable, bool RequiredForPayment, string? Status, string? Reason, DateTime? CheckedAt,
    Guid? VerifiedBy, string? VerifiedByName, string? VerificationSource, string? EvidenceJson, bool Stale, bool EffectivelyVerified,
    Guid SubmittedBy, IReadOnlyList<TaxInvoiceIdentity> Invoices, IReadOnlyList<TaxVerificationEventView> Events,
    string? ProposedRecommendation);

public interface ITaxVerificationRepository
{
    Task<TaxVerificationView?> GetAsync(Guid tenantId, Guid departmentId, Guid voucherId, CancellationToken ct);
    Task<TaxVerificationView?> ProposeAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid actorId, string actorType, string actorRole, string? recommendation, string? evidenceJson, string? reason, CancellationToken ct);
    Task<TaxVerificationView?> ConfirmAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid adminId, TaxConfirmRequest request, CancellationToken ct);
    Task<TaxVerificationView?> RejectAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid actorId, string actorType, string actorRole, TaxRejectRequest request, CancellationToken ct);
}

public sealed class TaxVerificationRepository(NpgsqlConnectionFactory factory) : ITaxVerificationRepository
{
    internal const string VoucherColumns = """
        id AS Id, user_id AS UserId, tenant_id AS TenantId, department_id AS DepartmentId, voucher_type AS VoucherType,
        document_number AS DocumentNumber, document_series AS DocumentSeries, document_date AS DocumentDate,
        seller_mst AS SellerMst, total_amount AS TotalAmount, extracted_fields::text AS ExtractedFieldsJson,
        tax_verification_status AS TaxVerificationStatus, tax_verification_reason AS TaxVerificationReason,
        tax_verification_checked_at AS TaxVerificationCheckedAt, tax_verified_by AS TaxVerifiedBy,
        tax_verification_fingerprint AS TaxVerificationFingerprint
        """;

    private static async Task<VoucherRecord?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, Guid departmentId, Guid voucherId, bool lockRow, CancellationToken ct) =>
        await conn.QuerySingleOrDefaultAsync<VoucherRecord>(new CommandDefinition($"""
            SELECT {VoucherColumns} FROM vouchers
            WHERE id=@voucherId AND tenant_id=@tenantId AND department_id=@departmentId {(lockRow ? "FOR UPDATE" : "")}
            """, new { voucherId, tenantId, departmentId }, tx, cancellationToken: ct));

    private static Task InsertEventAsync(NpgsqlConnection conn, NpgsqlTransaction tx, VoucherRecord v, string evt, string actorType, Guid? actorId,
        string? actorRole, string source, string? statusAfter, string? fingerprint, string? evidenceJson, string? reason, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO voucher_tax_verification_events
                (tenant_id, department_id, voucher_id, event, actor_type, actor_id, actor_role, source, status_before, status_after, fingerprint, evidence, reason)
            VALUES (@TenantId, @DepartmentId, @Id, @evt, @actorType, @actorId, @actorRole, @source, @before, @statusAfter, @fingerprint, @evidence::jsonb, @reason)
            """, new { v.TenantId, v.DepartmentId, v.Id, evt, actorType, actorId, actorRole, source, before = v.TaxVerificationStatus, statusAfter, fingerprint, evidence = evidenceJson, reason },
            tx, cancellationToken: ct));

    public async Task<TaxVerificationView?> GetAsync(Guid tenantId, Guid departmentId, Guid voucherId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await BuildViewAsync(conn, null, tenantId, departmentId, voucherId, ct);
    }

    private static async Task<TaxVerificationView?> BuildViewAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, Guid departmentId, Guid voucherId, CancellationToken ct)
    {
        var v = await LoadAsync(conn, tx, tenantId, departmentId, voucherId, false, ct);
        if (v is null) return null;
        var identities = TaxVerificationRules.IdentitiesOf(v);
        var current = TaxVerificationRules.Fingerprint(identities);
        var extra = await conn.QuerySingleAsync<(string? Evidence, string? VerifierName, bool Required, string? Source)>(new CommandDefinition("""
            SELECT v.tax_verification_evidence::text, COALESCE(u.display_name, u.email),
                   EXISTS (SELECT 1 FROM expense_claim_results r WHERE r.voucher_id = v.id AND r.requires_tax_verification),
                   v.tax_verification_evidence->>'verification_source'
            FROM vouchers v LEFT JOIN users u ON u.id = v.tax_verified_by
            WHERE v.id=@voucherId
            """, new { voucherId }, tx, cancellationToken: ct));
        var events = (await conn.QueryAsync<TaxVerificationEventView>(new CommandDefinition("""
            SELECT e.id AS Id, e.event AS Event, e.actor_type AS ActorType, e.actor_id AS ActorId, COALESCE(u.display_name, u.email) AS ActorName,
                   e.actor_role AS ActorRole, e.source AS Source, e.status_before AS StatusBefore, e.status_after AS StatusAfter, e.reason AS Reason, e.evidence::text AS EvidenceJson, e.created_at AS CreatedAt
            FROM voucher_tax_verification_events e LEFT JOIN users u ON u.id = e.actor_id
            WHERE e.voucher_id=@voucherId AND e.tenant_id=@tenantId
            ORDER BY e.created_at, e.id
            """, new { voucherId, tenantId }, tx, cancellationToken: ct))).ToList();
        var verified = v.TaxVerificationStatus == TaxVerificationStatuses.Verified;
        string? proposed = null;
        if (v.TaxVerificationStatus == TaxVerificationStatuses.PendingReview
            && events.LastOrDefault(e => e.Event == "proposed")?.EvidenceJson is { } pj)
        {
            try
            {
                using var d = JsonDocument.Parse(pj);
                if (d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("recommendation", out var r) && r.ValueKind == JsonValueKind.String)
                    proposed = r.GetString();
            }
            catch (JsonException) { }
        }
        return new TaxVerificationView(v.Id, v.VoucherType, identities.Count > 0, extra.Required, v.TaxVerificationStatus, v.TaxVerificationReason,
            v.TaxVerificationCheckedAt, v.TaxVerifiedBy, extra.VerifierName, extra.Source, extra.Evidence,
            Stale: verified && v.TaxVerificationFingerprint != current,
            EffectivelyVerified: TaxVerificationRules.IsVerifiedForCurrentData(v),
            v.UserId, identities, events, proposed);
    }

    public async Task<TaxVerificationView?> ProposeAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid actorId, string actorType,
        string actorRole, string? recommendation, string? evidenceJson, string? reason, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var v = await LoadAsync(conn, tx, tenantId, departmentId, voucherId, true, ct);
        if (v is null) return null;
        var identities = TaxVerificationRules.IdentitiesOf(v);
        if (identities.Count == 0) throw new TaxVerificationException("not_verifiable", "Chứng từ không có hoá đơn nào cần xác thực.");
        if (recommendation is not null && !TaxRecommendations.All.Contains(recommendation))
            throw new TaxVerificationException("recommendation_invalid", "Đề xuất không hợp lệ.", new { allowed = TaxRecommendations.All });
        // A proposal never touches an ADMIN decision (verified / invalid /
        // unavailable): it is only recorded. Otherwise the authoritative state
        // is 'pending_review' whatever the recommendation says.
        var adminDecided = v.TaxVerificationStatus is TaxVerificationStatuses.Verified or TaxVerificationStatuses.Invalid or TaxVerificationStatuses.Unavailable;
        var after = adminDecided ? v.TaxVerificationStatus : TaxVerificationStatuses.PendingReview;
        if (after == TaxVerificationStatuses.PendingReview)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE vouchers SET tax_verification_status='pending_review', tax_verification_reason=@reason,
                    tax_verification_checked_at=NOW(), tct_verified=FALSE
                WHERE id=@id
                """, new { id = v.Id, reason = reason ?? "Có đề xuất — chờ ADMIN quyết định" }, tx, cancellationToken: ct));
        }
        // Whatever the proposal's evidence claims ("verified successfully"…),
        // it is stored as an unverified proposal — never as authority.
        var proposalJson = JsonSerializer.Serialize(new
        {
            recommendation,
            evidence = evidenceJson is null ? (JsonElement?)null : JsonDocument.Parse(evidenceJson).RootElement.Clone(),
        });
        await InsertEventAsync(conn, tx, v, "proposed", actorType, actorId, actorRole,
            actorType == "agent" ? TaxVerificationAuthority.AgentProposal : TaxVerificationAuthority.UserProposal,
            after, TaxVerificationRules.Fingerprint(identities), proposalJson, reason, ct);
        await tx.CommitAsync(ct);
        return await BuildViewAsync(conn, null, tenantId, departmentId, voucherId, ct);
    }

    public async Task<TaxVerificationView?> ConfirmAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid adminId, TaxConfirmRequest request, CancellationToken ct)
    {
        if (request is null) throw new TaxVerificationException("evidence_required", "Cần bằng chứng xác minh.");
        if (!TaxVerificationSources.All.Contains(request.Source ?? ""))
            throw new TaxVerificationException("evidence_source_invalid", "Nguồn đối chiếu không hợp lệ.", new { allowed = TaxVerificationSources.All });
        if (string.IsNullOrWhiteSpace(request.Note) || request.Note.Trim().Length < 10)
            throw new TaxVerificationException("evidence_note_required", "Cần ghi chú bằng chứng đối chiếu (tối thiểu 10 ký tự).");
        if (!request.PortalChecked)
            throw new TaxVerificationException("evidence_attestation_required", "ADMIN phải xác nhận đã tự đối chiếu hoá đơn trên nguồn đã chọn.");

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var v = await LoadAsync(conn, tx, tenantId, departmentId, voucherId, true, ct);
        if (v is null) return null;
        // ADMIN Department Authority Invariant: the ADMIN who created the claim may attest it.
        var identities = TaxVerificationRules.IdentitiesOf(v);
        if (identities.Count == 0) throw new TaxVerificationException("not_verifiable", "Chứng từ không có hoá đơn nào cần xác thực.");
        // Replay / concurrent double-submit: the row lock above serialises them;
        // a still-valid verification is returned unchanged (no new event,
        // the first evidence is never overwritten).
        if (TaxVerificationRules.IsVerifiedForCurrentData(v))
        {
            await tx.RollbackAsync(ct);
            return await BuildViewAsync(conn, null, tenantId, departmentId, voucherId, ct);
        }
        var problems = TaxVerificationRules.Compare(identities, request.Invoices ?? Array.Empty<TaxAttestedInvoice>());
        if (problems.Count > 0)
            throw new TaxVerificationException("evidence_mismatch",
                "Thông tin hoá đơn đối chiếu không khớp dữ liệu chứng từ hiện tại — sửa chứng từ hoặc kiểm tra lại trên cổng.", new { problems });

        var fingerprint = TaxVerificationRules.Fingerprint(identities)!;
        // Server-built evidence: identity, time and source come from the
        // Gateway, never from the request body.
        var evidence = JsonSerializer.Serialize(new
        {
            verification_source = TaxVerificationAuthority.AdminManual,
            evidence_source = request.Source,
            checked_tax_codes = identities.Select(i => TaxVerificationRules.NormalizeMst(i.SellerMst)).Distinct().ToArray(),
            invoices = request.Invoices!.Select(i => new
            {
                seller_mst = TaxVerificationRules.NormalizeMst(i.SellerMst), invoice_series = i.InvoiceSeries, invoice_number = i.InvoiceNumber,
                invoice_date = i.InvoiceDate, total_amount = i.TotalAmount, lookup_reference = i.LookupCode,
            }),
            comment = request.Note.Trim(), attested_on_source = true,
            verified_by = adminId, verified_at = DateTime.UtcNow, fingerprint,
        });
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE vouchers SET tax_verification_status='verified', tax_verification_reason=NULL, tax_verification_checked_at=NOW(),
                tax_verified_by=@adminId, tax_verification_fingerprint=@fingerprint, tax_verification_evidence=@evidence::jsonb,
                tct_verified=TRUE, tct_verified_at=NOW()
            WHERE id=@id
            """, new { id = v.Id, adminId, fingerprint, evidence }, tx, cancellationToken: ct));
        await InsertEventAsync(conn, tx, v, "verified", "user", adminId, "admin", TaxVerificationAuthority.AdminManual,
            TaxVerificationStatuses.Verified, fingerprint, evidence, request.Note.Trim(), ct);
        await tx.CommitAsync(ct);
        return await BuildViewAsync(conn, null, tenantId, departmentId, voucherId, ct);
    }

    /// <summary>
    /// Authoritative negative result — department ADMIN only (never the Agent,
    /// never a USER: they propose). Requires where it was checked + a reason.
    /// May overturn an earlier ADMIN verification; history is kept.
    /// </summary>
    public async Task<TaxVerificationView?> RejectAsync(Guid tenantId, Guid departmentId, Guid voucherId, Guid actorId, string actorType,
        string actorRole, TaxRejectRequest request, CancellationToken ct)
    {
        if (actorType == "agent" || actorRole != "admin")
            throw new TaxVerificationException("admin_required", "Chỉ ADMIN phòng ban được kết luận hoá đơn không hợp lệ — Agent/người dùng chỉ đề xuất.");
        if (request is null || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 10)
            throw new TaxVerificationException("evidence_reason_required", "Cần lý do/bằng chứng (tối thiểu 10 ký tự).");
        if (!TaxVerificationSources.All.Contains(request.Source ?? ""))
            throw new TaxVerificationException("evidence_source_invalid", "Nguồn đối chiếu không hợp lệ.", new { allowed = TaxVerificationSources.All });

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var v = await LoadAsync(conn, tx, tenantId, departmentId, voucherId, true, ct);
        if (v is null) return null;
        var identities = TaxVerificationRules.IdentitiesOf(v);
        if (identities.Count == 0) throw new TaxVerificationException("not_verifiable", "Chứng từ không có hoá đơn nào cần xác thực.");
        var status = request.ProviderUnavailable ? TaxVerificationStatuses.Unavailable : TaxVerificationStatuses.Invalid;
        var fingerprint = TaxVerificationRules.Fingerprint(identities);
        var evidence = JsonSerializer.Serialize(new
        {
            verification_source = TaxVerificationAuthority.AdminManual, result = status,
            evidence_source = request.Source, lookup_reference = request.LookupReference,
            checked_tax_codes = identities.Select(i => TaxVerificationRules.NormalizeMst(i.SellerMst)).Distinct().ToArray(),
            comment = request.Reason.Trim(), decided_by = actorId, decided_at = DateTime.UtcNow, fingerprint,
        });
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE vouchers SET tax_verification_status=@status, tax_verification_reason=@reason, tax_verification_checked_at=NOW(),
                tax_verified_by=NULL, tax_verification_fingerprint=NULL, tax_verification_evidence=@evidence::jsonb, tct_verified=FALSE
            WHERE id=@id
            """, new { id = v.Id, status, reason = request.Reason.Trim(), evidence }, tx, cancellationToken: ct));
        await InsertEventAsync(conn, tx, v, status, "user", actorId, "admin", TaxVerificationAuthority.AdminManual, status,
            fingerprint, evidence, request.Reason.Trim(), ct);
        await tx.CommitAsync(ct);
        return await BuildViewAsync(conn, null, tenantId, departmentId, voucherId, ct);
    }

    /// <summary>
    /// Called inside every write of the invoice identity fields: a verification
    /// that no longer matches the voucher's data is reset (history kept).
    /// </summary>
    public static async Task InvalidateIfIdentityChangedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid voucherId, CancellationToken ct)
    {
        var v = await conn.QuerySingleOrDefaultAsync<VoucherRecord>(new CommandDefinition(
            $"SELECT {VoucherColumns} FROM vouchers WHERE id=@voucherId AND tenant_id=@tenantId FOR UPDATE",
            new { voucherId, tenantId }, tx, cancellationToken: ct));
        if (v is null || v.TaxVerificationStatus != TaxVerificationStatuses.Verified) return;
        var current = TaxVerificationRules.Fingerprint(TaxVerificationRules.IdentitiesOf(v));
        if (current == v.TaxVerificationFingerprint) return;

        var oldEvidence = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT tax_verification_evidence::text FROM vouchers WHERE id=@voucherId", new { voucherId }, tx, cancellationToken: ct));
        const string reason = "Dữ liệu hoá đơn đã thay đổi sau khi xác thực — cần ADMIN xác thực lại.";
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE vouchers SET tax_verification_status=NULL, tax_verification_reason=@reason, tax_verification_checked_at=NOW(),
                tax_verified_by=NULL, tax_verification_fingerprint=NULL, tax_verification_evidence=NULL, tct_verified=FALSE
            WHERE id=@voucherId
            """, new { voucherId, reason }, tx, cancellationToken: ct));
        await InsertEventAsync(conn, tx, v, "invalidated", "system", null, null, TaxVerificationAuthority.System, null, v.TaxVerificationFingerprint, oldEvidence,
            $"{reason} (fingerprint mới {current ?? "∅"})", ct);
    }
}
