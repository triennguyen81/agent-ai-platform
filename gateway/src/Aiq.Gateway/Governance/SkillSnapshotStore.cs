using System.Security.Cryptography;
using System.Text;
using Aiq.Gateway.Vouchers.DocumentProcessing;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Governance;

/// <summary>
/// Versioned skill content for the Agent runtime. A PUBLISHED (or
/// superseded) Agent version carries an immutable snapshot of every skill it
/// references — assigned skills and document-type skills alike — captured in
/// the publish transaction (agent_version_skill_snapshots, frozen by the
/// published-version trigger). Declarative skills carry their content and a
/// SHA-256 revision id; image (filesystem) skills are recorded by name only
/// and keep loading from the Agent image as before.
///
/// Runtime NEVER reads mutable skill content for a published version: editing
/// a skill after publish only affects drafts / later versions. A draft (Agent
/// Builder test only) resolves the current eligible content.
/// </summary>
public static class SkillSnapshotStore
{
    private sealed record CatalogRow(Guid Id, string SourceKind, string? Content, bool IsActive, bool Eligible);

    /// <summary>
    /// Catalog lookup for one code in the owning agent's context. Null = not
    /// cataloged at all (an image skill — same tolerance as
    /// EnsureSkillsEligibleAsync). Own-department rows win over platform rows.
    /// </summary>
    private static Task<CatalogRow?> FindAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string code, Guid? tenantId, Guid? departmentId, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<CatalogRow?>(new CommandDefinition("""
            SELECT s.id AS Id, s.source_kind AS SourceKind,
                   CASE WHEN s.source_kind = 'declarative' THEN s.declarative_content END AS Content,
                   s.is_active AS IsActive,
                   (s.scope = 'platform' OR (s.scope = 'department' AND s.tenant_id = @tenantId AND s.department_id = @departmentId)) AS Eligible
            FROM skills s
            WHERE s.code = @code
            ORDER BY (s.scope = 'department' AND s.tenant_id = @tenantId AND s.department_id = @departmentId) DESC,
                     (s.scope = 'platform') DESC
            LIMIT 1
            """, new { code, tenantId, departmentId }, tx, cancellationToken: ct));

    private static Task<(Guid? TenantId, Guid? DepartmentId)> OwnerAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid versionId, CancellationToken ct) =>
        conn.QuerySingleAsync<(Guid? TenantId, Guid? DepartmentId)>(new CommandDefinition("""
            SELECT a.owning_tenant_id AS TenantId, a.department_id AS DepartmentId
            FROM agent_versions v JOIN agents a ON a.id = v.agent_id WHERE v.id = @versionId
            """, new { versionId }, tx, cancellationToken: ct));

    private static async Task<List<string>> ReferencedCodesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid versionId, CancellationToken ct) =>
        (await conn.QueryAsync<string>(new CommandDefinition("""
            SELECT skill_name FROM agent_version_skill_assignments WHERE agent_version_id = @versionId
            UNION
            SELECT unnest(skill_codes) FROM agent_version_document_types WHERE agent_version_id = @versionId
            """, new { versionId }, tx, cancellationToken: ct))).OrderBy(c => c, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Publish/validate check for ASSIGNED skills: a cataloged skill must be
    /// active and visible to the owning department (platform or own).
    /// </summary>
    public static async Task<IReadOnlyList<string>> ValidateAssignedAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid versionId, CancellationToken ct)
    {
        var owner = await OwnerAsync(conn, tx, versionId, ct);
        var errors = new List<string>();
        var assigned = await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT skill_name FROM agent_version_skill_assignments WHERE agent_version_id = @versionId ORDER BY skill_name",
            new { versionId }, tx, cancellationToken: ct));
        foreach (var code in assigned)
        {
            var row = await FindAsync(conn, tx, code, owner.TenantId, owner.DepartmentId, ct);
            if (row is null) continue;
            if (!row.Eligible) errors.Add($"skill_not_eligible:{code}");
            else if (!row.IsActive) errors.Add($"skill_inactive:{code}");
            else if (row.SourceKind == "declarative" && string.IsNullOrWhiteSpace(row.Content)) errors.Add($"skill_content_missing:{code}");
        }
        return errors;
    }

    /// <summary>
    /// Inside the publish transaction, while the version is still a draft:
    /// snapshot every referenced skill (assigned + document-type).
    /// </summary>
    public static async Task PinForPublishAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid versionId, CancellationToken ct)
    {
        var owner = await OwnerAsync(conn, tx, versionId, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM agent_version_skill_snapshots WHERE agent_version_id=@versionId", new { versionId }, tx, cancellationToken: ct));
        foreach (var code in await ReferencedCodesAsync(conn, tx, versionId, ct))
        {
            var row = await FindAsync(conn, tx, code, owner.TenantId, owner.DepartmentId, ct);
            if (row is not null && (!row.Eligible || !row.IsActive)) throw new InvalidOperationException("skill_not_eligible");
            var declarative = row?.SourceKind == "declarative";
            if (declarative && string.IsNullOrWhiteSpace(row!.Content)) throw new InvalidOperationException("skill_not_eligible");
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO agent_version_skill_snapshots(agent_version_id, skill_code, skill_id, source_kind, content, content_sha256)
                VALUES (@versionId, @code, @skillId, @sourceKind, @content, @sha)
                """, new
            {
                versionId, code, skillId = row?.Id, sourceKind = declarative ? "declarative" : "filesystem",
                content = declarative ? row!.Content : null, sha = declarative ? Sha256(row!.Content!) : null,
            }, tx, cancellationToken: ct));
        }
    }

    /// <summary>
    /// Skill snapshots for the manifest of one version (assigned skills).
    /// Published/superseded: stored snapshots only — a declarative skill
    /// without a snapshot comes back with no content and the runtime fails
    /// closed rather than read mutable content. Draft: current eligible
    /// content (Agent Builder test mode only).
    /// </summary>
    public static async Task<IReadOnlyList<ManifestDocumentSkill>> ResolveAssignedForManifestAsync(NpgsqlConnection conn, Guid versionId, CancellationToken ct)
    {
        var status = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM agent_versions WHERE id=@versionId", new { versionId }, cancellationToken: ct));
        var assigned = (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT skill_name FROM agent_version_skill_assignments WHERE agent_version_id = @versionId ORDER BY skill_name",
            new { versionId }, cancellationToken: ct))).ToList();
        if (assigned.Count == 0) return [];
        var result = new List<ManifestDocumentSkill>(assigned.Count);
        if (status != "draft")
        {
            var snapshots = (await conn.QueryAsync<(string Code, string SourceKind, string? Sha, string? Content)>(new CommandDefinition("""
                SELECT skill_code AS Code, source_kind AS SourceKind, content_sha256 AS Sha, content AS Content
                FROM agent_version_skill_snapshots WHERE agent_version_id = @versionId
                """, new { versionId }, cancellationToken: ct))).ToDictionary(s => s.Code, StringComparer.Ordinal);
            foreach (var code in assigned)
            {
                if (snapshots.TryGetValue(code, out var snap))
                    result.Add(new ManifestDocumentSkill(code, snap.SourceKind, snap.Sha, snap.Content));
                else
                    // Published before snapshots existed and not a cataloged
                    // declarative skill (migration 062 backfilled those): an
                    // image skill, loaded by name as before.
                    result.Add(new ManifestDocumentSkill(code, "filesystem", null, null));
            }
            return result;
        }
        var owner = await OwnerAsync(conn, null, versionId, ct);
        foreach (var code in assigned)
        {
            var row = await FindAsync(conn, null, code, owner.TenantId, owner.DepartmentId, ct);
            result.Add(row is { SourceKind: "declarative", Eligible: true, IsActive: true, Content: { } content }
                ? new ManifestDocumentSkill(code, "declarative", Sha256(content), content)
                : row is { SourceKind: "declarative" }
                    ? new ManifestDocumentSkill(code, "declarative", null, null)   // ineligible → runtime fails closed
                    : new ManifestDocumentSkill(code, "filesystem", null, null));
        }
        return result;
    }

    public static string Sha256(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
