using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aiq.Gateway.Data;
using Aiq.Gateway.Governance;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Vouchers.DocumentProcessing;

/// <summary>Which agent/version/configuration a document pipeline run uses.</summary>
public sealed record DocumentAgentResolution(
    Guid? AgentId, Guid? AgentVersionId, int? Version, string? AgentName,
    ManifestDocumentProcessing? Config, string? DenialReason)
{
    public string ConfigSource => Config is null && !Pinned ? DocumentConfigSources.Legacy : DocumentConfigSources.Manifest;

    /// <summary>
    /// Resolved from the document's own manifest provenance (re-extraction of
    /// a manifest-processed voucher) rather than current agent selection.
    /// A pinned resolution with a DenialReason is an explicit failure — it is
    /// NEVER replaced by the legacy built-in extraction.
    /// </summary>
    public bool Pinned { get; init; }
}

/// <summary>Explicit re-extraction failures for manifest-processed documents.</summary>
public static class DocumentProvenanceErrors
{
    public const string AgentUnavailable = "processing_agent_unavailable";
    public const string VersionUnavailable = "processing_agent_version_unavailable";
    public const string ProvenanceInvalid = "processing_provenance_invalid";
}

public sealed record ExtractionFailureState(string Code, string? Stage, DateTime? FailedAt);

public sealed record DocumentProvenance(string? ConfigSource, Guid? AgentId, Guid? AgentVersionId)
{
    public bool IsManifest => ConfigSource == DocumentConfigSources.Manifest;
    public bool IsReextract => ConfigSource is not null;
}

public static class DocumentConfigSources
{
    public const string Manifest = "manifest";
    public const string Legacy = "legacy";
}

public sealed record DocumentProcessingRun(
    string Mode, Guid? VoucherId, Guid TenantId, Guid? DepartmentId, Guid? ActorUserId,
    Guid? AgentId, Guid? AgentVersionId, int? AgentVersion, string ConfigSource, string? DocumentType,
    string? SchemaSha256, Guid? WorkflowId, Guid? WorkflowVersionId, IReadOnlyList<object> Skills,
    string? Provider, string? Model, string? ClassificationSource, string? ClassificationStatus,
    decimal? ClassificationConfidence, bool? SchemaValid, IReadOnlyList<string> MissingRequired,
    bool UnsupportedDocumentType, string TriggerKind = "initial", string? ErrorCode = null, Guid? ReviewTaskId = null,
    string? ErrorStage = null, Guid? ExpenseClaimItemId = null);

public sealed record DepartmentDocumentConfig(Guid VersionId, int Version, string VersionStatus, IReadOnlyList<DocumentTypeConfigRecord> Types);

public sealed record DocumentConfigSaveResult(IReadOnlyList<DocumentTypeConfigRecord>? Types, IReadOnlyList<string> Errors, string? Failure)
{
    public static DocumentConfigSaveResult NotFound => new(null, [], "not_found");
    public static DocumentConfigSaveResult NotDraft => new(null, [], "published_versions_are_immutable");
}

public sealed record DocumentTestTarget(Guid AgentId, Guid VersionId, int Version, string VersionStatus, string AgentName, ManifestDocumentProcessing? Config);

public interface IDocumentProcessingRepository
{
    /// <summary>Config of one version of a Department Agent the caller's own department owns; null otherwise.</summary>
    Task<DepartmentDocumentConfig?> GetDepartmentVersionConfigAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid versionId, CancellationToken ct);

    /// <summary>Replaces the full document configuration of a DRAFT version (validated, all-or-nothing).</summary>
    Task<DocumentConfigSaveResult> ReplaceDepartmentDraftConfigAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid versionId, Guid actorId,
        IReadOnlyList<DocumentTypeConfigInput>? types, CancellationToken ct);

    /// <summary>Agent Builder test target: current draft, else published — same rule as the test manifest.</summary>
    Task<DocumentTestTarget?> GetDepartmentTestTargetAsync(Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct);

    /// <summary>
    /// Production: which agent processes a voucher for this uploader and the
    /// immutable PUBLISHED document configuration of its published version
    /// (null config = legacy). Authorization is the uploader's own
    /// capability chain via ICapabilityRepository.ResolveAgentAsync; a
    /// requested agent is only a hint re-validated there.
    /// </summary>
    Task<DocumentAgentResolution> ResolveForUploaderAsync(Guid tenantId, Guid departmentId, Guid userId, Guid? requestedAgentId, CancellationToken ct);

    Task<Guid?> GetRequestedAgentAsync(Guid voucherId, CancellationToken ct);

    /// <summary>The document's recorded processing provenance (null source = never processed with provenance).</summary>
    Task<DocumentProvenance> GetProvenanceAsync(Guid tenantId, Guid voucherId, CancellationToken ct);

    /// <summary>
    /// Re-extraction of a manifest-processed document: the RECORDED agent +
    /// published version, validated against the document's own tenant and
    /// department (never the original uploader's current eligibility). The
    /// caller's own authorization is enforced again when the Agent fetches the
    /// pinned manifest with the caller's token. Pinned=true; DenialReason is
    /// one of <see cref="DocumentProvenanceErrors"/> on failure.
    /// </summary>
    Task<DocumentAgentResolution> ResolvePinnedAsync(Guid tenantId, Guid departmentId, Guid? agentId, Guid? agentVersionId, CancellationToken ct);

    /// <summary>
    /// Explicit extraction failure: safe code + stage + time. A document with
    /// no successful extraction yet becomes 'extraction_failed' (retryable);
    /// one that already has a successful extraction keeps that result (a
    /// transient re-extract status returns to 'extracted').
    /// </summary>
    Task SetExtractionFailureAsync(Guid tenantId, Guid voucherId, string errorCode, string errorStage, CancellationToken ct);

    /// <summary>The current (unsuperseded) extraction failure of a voucher, if any.</summary>
    Task<ExtractionFailureState?> GetExtractionFailureAsync(Guid tenantId, Guid voucherId, CancellationToken ct);

    /// <summary>Adds/removes one extraction-owned blocker (vouchers.processing_blockers). Throws if not persisted.</summary>
    Task SetProcessingBlockerAsync(Guid tenantId, Guid voucherId, string blocker, bool active, CancellationToken ct);

    /// <summary>True when the user is an active member of the tenant (run-actor sanity check).</summary>
    Task<bool> IsActiveTenantUserAsync(Guid tenantId, Guid userId, CancellationToken ct);
    Task SetRequestedAgentAsync(Guid tenantId, Guid voucherId, Guid requestedAgentId, CancellationToken ct);
    Task<string?> GetDepartmentVoucherRawTextAsync(Guid tenantId, Guid departmentId, Guid voucherId, CancellationToken ct);

    Task<Guid> RecordRunAsync(DocumentProcessingRun run, CancellationToken ct);
    Task SetVoucherProvenanceAsync(Guid tenantId, Guid voucherId, Guid? agentId, Guid? agentVersionId, string configSource, CancellationToken ct);
}

public sealed class DocumentProcessingRepository(NpgsqlConnectionFactory factory, ICapabilityRepository capabilities) : IDocumentProcessingRepository
{
    public const string VoucherCapability = "voucher.processing";

    private const string DepartmentOwnership =
        "a.v2_scope='department' AND a.owning_tenant_id=@tenantId AND a.department_id=@departmentId";

    private sealed record VersionRow(Guid VersionId, int Version, string Status);

    public async Task<DepartmentDocumentConfig?> GetDepartmentVersionConfigAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid versionId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var version = await conn.QuerySingleOrDefaultAsync<VersionRow>(new CommandDefinition($"""
            SELECT v.id AS VersionId, v.version AS Version, v.status AS Status
            FROM agent_versions v JOIN agents a ON a.id=v.agent_id AND {DepartmentOwnership}
            WHERE v.id=@versionId AND v.agent_id=@agentId
            """, new { tenantId, departmentId, agentId, versionId }, cancellationToken: ct));
        if (version is null) return null;
        var types = await DocumentProcessingStore.ReadAsync(conn, versionId, null, ct);
        return new DepartmentDocumentConfig(version.VersionId, version.Version, version.Status, types);
    }

    public async Task<DocumentConfigSaveResult> ReplaceDepartmentDraftConfigAsync(Guid tenantId, Guid departmentId, Guid agentId, Guid versionId, Guid actorId,
        IReadOnlyList<DocumentTypeConfigInput>? types, CancellationToken ct)
    {
        var (shapeErrors, records) = DocumentProcessingValidator.Normalize(types);
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var version = await conn.QuerySingleOrDefaultAsync<VersionRow>(new CommandDefinition($"""
            SELECT v.id AS VersionId, v.version AS Version, v.status AS Status
            FROM agent_versions v JOIN agents a ON a.id=v.agent_id AND {DepartmentOwnership}
            WHERE v.id=@versionId AND v.agent_id=@agentId
            FOR UPDATE OF v
            """, new { tenantId, departmentId, agentId, versionId }, tx, cancellationToken: ct));
        if (version is null) return DocumentConfigSaveResult.NotFound;
        if (version.Status != "draft") return DocumentConfigSaveResult.NotDraft;
        if (shapeErrors.Count > 0) return new(null, shapeErrors, null);

        // Unknown workflow ids must be a validation error, not an FK failure.
        var workflowIds = records.Where(r => r.WorkflowId is not null).Select(r => r.WorkflowId!.Value).Distinct().ToArray();
        var existing = (await conn.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM workflows WHERE id = ANY(@workflowIds)", new { workflowIds }, tx, cancellationToken: ct))).ToHashSet();
        var unknownWorkflow = records.Where(r => r.WorkflowId is { } id && !existing.Contains(id)).Select(r => $"missing_workflow:{r.Code}").ToList();
        if (unknownWorkflow.Count > 0) return new(null, unknownWorkflow, null);

        await DocumentProcessingStore.ReplaceAsync(conn, tx, versionId, records, actorId, ct);
        var errors = await DocumentProcessingStore.ValidateAsync(conn, tx, versionId, ct);
        if (errors.Count > 0)
        {
            await tx.RollbackAsync(ct);
            return new(null, errors, null);
        }
        await tx.CommitAsync(ct);
        return new(await DocumentProcessingStore.ReadAsync(conn, versionId, null, ct), [], null);
    }

    public async Task<DocumentTestTarget?> GetDepartmentTestTargetAsync(Guid tenantId, Guid departmentId, Guid agentId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<(Guid VersionId, int Version, string Status, string Name)?>(new CommandDefinition($"""
            SELECT v.id AS VersionId, v.version AS Version, v.status AS Status, a.name AS Name
            FROM agents a JOIN agent_versions v ON v.id=COALESCE(a.current_draft_version_id, a.published_version_id) AND v.agent_id=a.id
            WHERE a.id=@agentId AND {DepartmentOwnership}
            """, new { tenantId, departmentId, agentId }, cancellationToken: ct));
        if (row is null) return null;
        var config = await DocumentProcessingStore.ResolveForManifestAsync(conn, row.Value.VersionId, ct);
        return new DocumentTestTarget(agentId, row.Value.VersionId, row.Value.Version, row.Value.Status, row.Value.Name, config);
    }

    public async Task<DocumentAgentResolution> ResolveForUploaderAsync(Guid tenantId, Guid departmentId, Guid userId, Guid? requestedAgentId, CancellationToken ct)
    {
        string? role;
        await using (var conn = await factory.OpenAsync(ct))
        {
            role = await conn.ExecuteScalarAsync<string?>(new CommandDefinition("""
                SELECT r.name FROM users u JOIN user_roles ur ON ur.user_id=u.id JOIN roles r ON r.id=ur.role_id
                WHERE u.id=@userId AND u.tenant_id=@tenantId AND u.is_active
                LIMIT 1
                """, new { userId, tenantId }, cancellationToken: ct));
        }
        if (role is not ("admin" or "user")) return new(null, null, null, null, null, "role_not_eligible");
        var isAdmin = role == "admin";
        var resolution = await capabilities.ResolveAgentAsync(
            tenantId, departmentId, isAdmin ? null : userId, isAdmin, VoucherCapability, requestedAgentId, ct);
        if (resolution.AgentId is not { } agentId) return new(null, null, null, null, null, resolution.DenialReason ?? "capability_not_available");

        await using var conn2 = await factory.OpenAsync(ct);
        var published = await conn2.QuerySingleOrDefaultAsync<(Guid VersionId, int Version, string Name)?>(new CommandDefinition("""
            SELECT v.id AS VersionId, v.version AS Version, a.name AS Name
            FROM agents a JOIN agent_versions v ON v.id=a.published_version_id AND v.agent_id=a.id AND v.status='published'
            WHERE a.id=@agentId
            """, new { agentId }, cancellationToken: ct));
        if (published is null) return new(agentId, null, null, null, null, "agent_not_published");
        var config = await DocumentProcessingStore.ResolveForManifestAsync(conn2, published.Value.VersionId, ct);
        return new(agentId, published.Value.VersionId, published.Value.Version, published.Value.Name, config, null);
    }

    private sealed record PinnedRow(
        Guid? AgentId, string? AgentName, string? V2Scope, Guid? OwningTenantId, Guid? DepartmentId,
        bool AgentEnabled, string? LifecycleState, bool Entitled,
        Guid? VersionId, Guid? VersionAgentId, int? Version, string? VersionStatus);

    public async Task<DocumentProvenance> GetProvenanceAsync(Guid tenantId, Guid voucherId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<DocumentProvenance>(new CommandDefinition("""
            SELECT document_config_source AS ConfigSource, processed_by_agent_id AS AgentId,
                   processed_by_agent_version_id AS AgentVersionId
            FROM vouchers WHERE id=@voucherId AND tenant_id=@tenantId
            """, new { tenantId, voucherId }, cancellationToken: ct)) ?? new DocumentProvenance(null, null, null);
    }

    public async Task<DocumentAgentResolution> ResolvePinnedAsync(Guid tenantId, Guid departmentId, Guid? agentId, Guid? agentVersionId, CancellationToken ct)
    {
        DocumentAgentResolution Fail(string code, int? version = null, string? name = null) =>
            new(agentId, agentVersionId, version, name, null, code) { Pinned = true };
        if (agentId is null || agentVersionId is null) return Fail(DocumentProvenanceErrors.ProvenanceInvalid);

        await using var conn = await factory.OpenAsync(ct);
        var row = await conn.QuerySingleAsync<PinnedRow>(new CommandDefinition("""
            SELECT a.id AS AgentId, a.name AS AgentName, a.v2_scope AS V2Scope,
                   a.owning_tenant_id AS OwningTenantId, a.department_id AS DepartmentId,
                   COALESCE(a.disabled_at IS NULL, FALSE) AS AgentEnabled, a.lifecycle_state AS LifecycleState,
                   COALESCE(EXISTS (SELECT 1 FROM agent_tenant_assignments ata WHERE ata.agent_id=a.id AND ata.tenant_id=@tenantId AND ata.status='active')
                    AND EXISTS (SELECT 1 FROM agent_department_assignments ada WHERE ada.agent_id=a.id AND ada.tenant_id=@tenantId
                                AND ada.department_id=@departmentId AND ada.status='active'), FALSE) AS Entitled,
                   v.id AS VersionId, v.agent_id AS VersionAgentId, v.version AS Version, v.status AS VersionStatus
            FROM (SELECT 1) one
            LEFT JOIN agents a ON a.id=@agentId
            LEFT JOIN agent_versions v ON v.id=@agentVersionId
            """, new { tenantId, departmentId, agentId, agentVersionId }, cancellationToken: ct));

        if (row.AgentId is null) return Fail(DocumentProvenanceErrors.AgentUnavailable);
        if (row.VersionId is null) return Fail(DocumentProvenanceErrors.VersionUnavailable, null, row.AgentName);
        if (row.VersionAgentId != agentId) return Fail(DocumentProvenanceErrors.ProvenanceInvalid, row.Version, row.AgentName);
        if (row.VersionStatus is not ("published" or "superseded")) return Fail(DocumentProvenanceErrors.VersionUnavailable, row.Version, row.AgentName);
        // The recorded agent must belong to THIS document's tenant+department:
        // structural ownership for a Department Agent, active entitlement for
        // a platform agent.
        var inScope = row.V2Scope == "department"
            ? row.OwningTenantId == tenantId && row.DepartmentId == departmentId
            : row.Entitled;
        if (!inScope)
            return Fail(row.V2Scope == "department" ? DocumentProvenanceErrors.ProvenanceInvalid : DocumentProvenanceErrors.AgentUnavailable, row.Version, row.AgentName);
        if (!row.AgentEnabled || row.LifecycleState != "production") return Fail(DocumentProvenanceErrors.AgentUnavailable, row.Version, row.AgentName);

        var config = await DocumentProcessingStore.ResolveForManifestAsync(conn, agentVersionId.Value, ct);
        if (config is null || config.Draft) return Fail(DocumentProvenanceErrors.ProvenanceInvalid, row.Version, row.AgentName);
        return new DocumentAgentResolution(agentId, agentVersionId, row.Version, row.AgentName, config, null) { Pinned = true };
    }

    public async Task SetExtractionFailureAsync(Guid tenantId, Guid voucherId, string errorCode, string errorStage, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE vouchers SET
                extraction_error_code = @errorCode, extraction_error_stage = @errorStage, extraction_failed_at = NOW(),
                -- No successful extraction yet → explicit extraction_failed.
                -- A previous successful extraction stays the current result:
                -- a transient re-extract state returns to 'extracted'.
                status = CASE
                    WHEN status NOT IN ('processing', 'ocr_complete', 'extraction_failed') THEN status
                    WHEN document_config_source IS NOT NULL
                      OR COALESCE(extracted_fields, '{}'::jsonb) ?| ARRAY['voucher_type_classification', 'document_processing'] THEN 'extracted'
                    ELSE 'extraction_failed' END
            WHERE id=@voucherId AND tenant_id=@tenantId
            """, new { tenantId, voucherId, errorCode, errorStage }, cancellationToken: ct));
        if (n != 1) throw new InvalidOperationException("extraction_failure_not_recorded");
    }

    public async Task<ExtractionFailureState?> GetExtractionFailureAsync(Guid tenantId, Guid voucherId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ExtractionFailureState>(new CommandDefinition(
            "SELECT extraction_error_code AS Code, extraction_error_stage AS Stage, extraction_failed_at AS FailedAt FROM vouchers WHERE id=@voucherId AND tenant_id=@tenantId AND extraction_error_code IS NOT NULL",
            new { tenantId, voucherId }, cancellationToken: ct));
    }

    public async Task SetProcessingBlockerAsync(Guid tenantId, Guid voucherId, string blocker, bool active, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE vouchers SET processing_blockers = CASE
                WHEN @active THEN ARRAY(SELECT DISTINCT b FROM unnest(array_append(processing_blockers, @blocker)) b ORDER BY b)
                ELSE array_remove(processing_blockers, @blocker) END
            WHERE id=@voucherId AND tenant_id=@tenantId
            """, new { tenantId, voucherId, blocker, active }, cancellationToken: ct));
        if (n != 1) throw new InvalidOperationException("processing_blocker_not_recorded");
    }

    public async Task<bool> IsActiveTenantUserAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM users WHERE id=@userId AND tenant_id=@tenantId AND is_active)",
            new { tenantId, userId }, cancellationToken: ct));
    }

    public async Task<Guid?> GetRequestedAgentAsync(Guid voucherId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT requested_agent_id FROM vouchers WHERE id=@voucherId", new { voucherId }, cancellationToken: ct));
    }

    public async Task SetRequestedAgentAsync(Guid tenantId, Guid voucherId, Guid requestedAgentId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE vouchers SET requested_agent_id=@requestedAgentId WHERE id=@voucherId AND tenant_id=@tenantId",
            new { tenantId, voucherId, requestedAgentId }, cancellationToken: ct));
    }

    public async Task<string?> GetDepartmentVoucherRawTextAsync(Guid tenantId, Guid departmentId, Guid voucherId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT raw_ocr_text FROM vouchers WHERE id=@voucherId AND tenant_id=@tenantId AND department_id=@departmentId",
            new { tenantId, departmentId, voucherId }, cancellationToken: ct));
    }

    public async Task<Guid> RecordRunAsync(DocumentProcessingRun run, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO document_processing_runs(mode, voucher_id, tenant_id, department_id, actor_user_id, agent_id,
                agent_version_id, agent_version, config_source, document_type, schema_sha256, workflow_id,
                workflow_version_id, skills, provider, model, classification_source, classification_status,
                classification_confidence, schema_valid, missing_required, unsupported_document_type,
                trigger_kind, error_code, review_task_id, error_stage, expense_claim_item_id)
            VALUES (@Mode, @VoucherId, @TenantId, @DepartmentId, @ActorUserId, @AgentId, @AgentVersionId, @AgentVersion,
                @ConfigSource, @DocumentType, @SchemaSha256, @WorkflowId, @WorkflowVersionId, @skills::jsonb, @Provider,
                @Model, @ClassificationSource, @ClassificationStatus, @ClassificationConfidence, @SchemaValid,
                @missingRequired, @UnsupportedDocumentType, @TriggerKind, @ErrorCode, @ReviewTaskId, @ErrorStage, @ExpenseClaimItemId)
            RETURNING id
            """, new
        {
            run.Mode, run.VoucherId, run.TenantId, run.DepartmentId, run.ActorUserId, run.AgentId, run.AgentVersionId,
            run.AgentVersion, run.ConfigSource, run.DocumentType, run.SchemaSha256, run.WorkflowId, run.WorkflowVersionId,
            skills = JsonSerializer.Serialize(run.Skills), run.Provider, run.Model, run.ClassificationSource,
            run.ClassificationStatus, run.ClassificationConfidence, run.SchemaValid,
            missingRequired = run.MissingRequired.ToArray(), run.UnsupportedDocumentType,
            run.TriggerKind, run.ErrorCode, run.ReviewTaskId, run.ErrorStage, run.ExpenseClaimItemId,
        }, cancellationToken: ct));
    }

    public async Task SetVoucherProvenanceAsync(Guid tenantId, Guid voucherId, Guid? agentId, Guid? agentVersionId, string configSource, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE vouchers SET processed_by_agent_id=@agentId, processed_by_agent_version_id=@agentVersionId,
                document_config_source=@configSource,
                -- a successful extraction supersedes an earlier failure
                extraction_error_code=NULL, extraction_error_stage=NULL, extraction_failed_at=NULL
            WHERE id=@voucherId AND tenant_id=@tenantId
            """, new { tenantId, voucherId, agentId, agentVersionId, configSource }, cancellationToken: ct));
    }
}

/// <summary>
/// Connection/transaction-level helpers shared with DynamicAgentRepository so
/// version create/validate/publish and manifest composition stay in ONE
/// transaction with the rest of the version lifecycle.
/// </summary>
public static class DocumentProcessingStore
{
    // Property-mapped (not a positional record): Dapper's constructor
    // matching sees text[] as System.Array and refuses string[].
    private sealed class TypeRow
    {
        public string Code { get; set; } = "";
        public bool Enabled { get; set; }
        public string DisplayName { get; set; } = "";
        public string FieldsJson { get; set; } = "[]";
        public string ExtractionInstructions { get; set; } = "";
        public Guid? WorkflowId { get; set; }
        public Guid? WorkflowVersionId { get; set; }
        public string? WorkflowSnapshotJson { get; set; }
        public string[] SkillCodes { get; set; } = [];
        public int SortOrder { get; set; }
    }

    private const string TypeColumns = """
        code AS Code, enabled AS Enabled, display_name AS DisplayName, fields::text AS FieldsJson,
        extraction_instructions AS ExtractionInstructions, workflow_id AS WorkflowId,
        workflow_version_id AS WorkflowVersionId, workflow_snapshot::text AS WorkflowSnapshotJson,
        skill_codes AS SkillCodes, sort_order AS SortOrder
        """;

    private static readonly JsonSerializerOptions FieldJson = new(JsonSerializerDefaults.Web);

    private static async Task<List<TypeRow>> ReadRowsAsync(NpgsqlConnection conn, Guid versionId, NpgsqlTransaction? tx, CancellationToken ct) =>
        (await conn.QueryAsync<TypeRow>(new CommandDefinition(
            $"SELECT {TypeColumns} FROM agent_version_document_types WHERE agent_version_id=@versionId ORDER BY sort_order, code",
            new { versionId }, tx, cancellationToken: ct))).AsList();

    private static DocumentTypeConfigRecord ToRecord(TypeRow row) => new(
        row.Code, row.Enabled, row.DisplayName,
        JsonSerializer.Deserialize<List<DocumentFieldDefinition>>(row.FieldsJson, FieldJson) ?? [],
        row.ExtractionInstructions, row.WorkflowId, row.WorkflowVersionId, row.SkillCodes, row.SortOrder,
        DocumentTypeCatalog.Find(row.Code)?.AccountingHandler ?? string.Empty);

    public static async Task<IReadOnlyList<DocumentTypeConfigRecord>> ReadAsync(NpgsqlConnection conn, Guid versionId, NpgsqlTransaction? tx, CancellationToken ct) =>
        (await ReadRowsAsync(conn, versionId, tx, ct)).Select(ToRecord).ToList();

    public static async Task ReplaceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid versionId,
        IReadOnlyList<DocumentTypeConfigRecord> records, Guid actorId, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM agent_version_document_types WHERE agent_version_id=@versionId", new { versionId }, tx, cancellationToken: ct));
        foreach (var r in records)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO agent_version_document_types(agent_version_id, code, enabled, display_name, fields,
                    extraction_instructions, workflow_id, skill_codes, sort_order, updated_by)
                VALUES (@versionId, @Code, @Enabled, @DisplayName, @fields::jsonb, @ExtractionInstructions,
                    @WorkflowId, @SkillCodes, @SortOrder, @actorId)
                """, new
            {
                versionId, r.Code, r.Enabled, r.DisplayName, fields = JsonSerializer.Serialize(r.Fields, FieldJson),
                r.ExtractionInstructions, r.WorkflowId, r.SkillCodes, r.SortOrder, actorId,
            }, tx, cancellationToken: ct));
        }
    }

    /// <summary>
    /// A new draft inherits the agent's currently published document
    /// configuration (drafts are edited copies; the published rows are never
    /// touched). No-op when the published version has none.
    /// </summary>
    public static Task CopyFromPublishedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid agentId, Guid newVersionId, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO agent_version_document_types(agent_version_id, code, enabled, display_name, fields,
                extraction_instructions, workflow_id, skill_codes, sort_order, updated_by)
            SELECT @newVersionId, d.code, d.enabled, d.display_name, d.fields, d.extraction_instructions,
                d.workflow_id, d.skill_codes, d.sort_order, d.updated_by
            FROM agents a JOIN agent_version_document_types d ON d.agent_version_id=a.published_version_id
            WHERE a.id=@agentId AND a.published_version_id IS NOT NULL AND a.published_version_id <> @newVersionId
              AND NOT EXISTS (SELECT 1 FROM agent_version_document_types x WHERE x.agent_version_id=@newVersionId)
            """, new { agentId, newVersionId }, tx, cancellationToken: ct));

    /// <summary>
    /// Publish/validate-time checks that need the database. Empty list when
    /// the version has no document configuration (legacy stays valid).
    /// Error codes are structured: kind:type[:detail].
    /// </summary>
    public static async Task<IReadOnlyList<string>> ValidateAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid versionId, CancellationToken ct)
    {
        var records = await ReadAsync(conn, versionId, tx, ct);
        if (records.Count == 0) return [];

        // Re-run shape validation on what is stored — the catalog or field
        // rules may have tightened since the draft was saved.
        var (errors, _) = DocumentProcessingValidator.Normalize(records.Select(r => new DocumentTypeConfigInput(
            r.Code, r.Enabled, r.DisplayName,
            r.Fields.Select(f => new DocumentFieldInput(f.Name, f.Label, f.Type, f.Required, f.Description)).ToList(),
            r.ExtractionInstructions, r.WorkflowId, r.SkillCodes, null)).ToList());
        var result = new List<string>(errors);

        var owner = await conn.QuerySingleAsync<(Guid AgentId, Guid? TenantId, Guid? DepartmentId)>(new CommandDefinition("""
            SELECT a.id AS AgentId, a.owning_tenant_id AS TenantId, a.department_id AS DepartmentId
            FROM agent_versions v JOIN agents a ON a.id=v.agent_id WHERE v.id=@versionId
            """, new { versionId }, tx, cancellationToken: ct));
        var assignedSkills = (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT skill_name FROM agent_version_skill_assignments WHERE agent_version_id=@versionId",
            new { versionId }, tx, cancellationToken: ct))).ToHashSet(StringComparer.Ordinal);

        foreach (var r in records)
        {
            if (r.WorkflowId is { } workflowId)
            {
                var wf = await ResolveWorkflowAsync(conn, tx, workflowId, owner.TenantId, owner.DepartmentId, ct);
                if (wf is null) result.Add($"missing_workflow:{r.Code}");
                else
                {
                    foreach (var (type, _) in DocumentWorkflow.Steps(wf.Value.Definition))
                        if (!WorkflowStepTypes.Known.Contains(type)) result.Add($"invalid_workflow_step:{r.Code}:{type}");
                }
            }
            foreach (var skill in r.SkillCodes)
            {
                var source = await ResolveSkillSourceAsync(conn, tx, skill, owner.TenantId, owner.DepartmentId, ct);
                if (source is null) result.Add($"missing_skill:{r.Code}:{skill}");
                // A filesystem skill's content ships with the Agent image and
                // only reaches the model through the version's own skill
                // assignments — require it there so the runtime really loads it.
                else if (source.Value.SourceKind == "filesystem" && !assignedSkills.Contains(skill))
                    result.Add($"document_type_skill_not_assigned:{r.Code}:{skill}");
            }
        }
        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Called inside the publish transaction BEFORE the status flip (the
    /// immutability trigger freezes everything once the version leaves
    /// 'draft'): pins each workflow's published version + definition and
    /// snapshots referenced skill content.
    /// </summary>
    public static async Task PinForPublishAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid versionId, CancellationToken ct)
    {
        // Skills first and ALWAYS: every assigned skill (not only
        // document-type skills) is snapshotted, config or no config.
        await Aiq.Gateway.Governance.SkillSnapshotStore.PinForPublishAsync(conn, tx, versionId, ct);
        var records = await ReadAsync(conn, versionId, tx, ct);
        if (records.Count == 0) return;
        var owner = await conn.QuerySingleAsync<(Guid? TenantId, Guid? DepartmentId)>(new CommandDefinition("""
            SELECT a.owning_tenant_id AS TenantId, a.department_id AS DepartmentId
            FROM agent_versions v JOIN agents a ON a.id=v.agent_id WHERE v.id=@versionId
            """, new { versionId }, tx, cancellationToken: ct));

        foreach (var r in records.Where(r => r.WorkflowId is not null))
        {
            var wf = await ResolveWorkflowAsync(conn, tx, r.WorkflowId!.Value, owner.TenantId, owner.DepartmentId, ct)
                ?? throw new InvalidOperationException("workflow_not_eligible");
            var snapshot = JsonSerializer.Serialize(new { name = wf.Name, definition = wf.Definition });
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE agent_version_document_types SET workflow_version_id=@workflowVersionId, workflow_snapshot=@snapshot::jsonb
                WHERE agent_version_id=@versionId AND code=@Code
                """, new { versionId, r.Code, workflowVersionId = wf.VersionId, snapshot }, tx, cancellationToken: ct));
        }
    }

    /// <summary>
    /// The document configuration as the manifest carries it. Published /
    /// superseded versions read ONLY their pinned snapshots; a draft (Agent
    /// Builder test mode only) reads the current eligible workflow/skill
    /// content and is flagged Draft=true. Null when the version has no
    /// document configuration (legacy).
    /// </summary>
    public static async Task<ManifestDocumentProcessing?> ResolveForManifestAsync(NpgsqlConnection conn, Guid versionId, CancellationToken ct)
    {
        var rows = await ReadRowsAsync(conn, versionId, null, ct);
        if (rows.Count == 0) return null;
        var owner = await conn.QuerySingleAsync<(string Status, Guid? TenantId, Guid? DepartmentId)>(new CommandDefinition("""
            SELECT v.status AS Status, a.owning_tenant_id AS TenantId, a.department_id AS DepartmentId
            FROM agent_versions v JOIN agents a ON a.id=v.agent_id WHERE v.id=@versionId
            """, new { versionId }, cancellationToken: ct));
        var draft = owner.Status == "draft";
        var snapshots = draft ? new Dictionary<string, ManifestDocumentSkill>(StringComparer.Ordinal)
            : (await conn.QueryAsync<(string SkillCode, string SourceKind, string? ContentSha256, string? Content)>(new CommandDefinition("""
                SELECT skill_code AS SkillCode, source_kind AS SourceKind, content_sha256 AS ContentSha256, content AS Content
                FROM agent_version_skill_snapshots WHERE agent_version_id=@versionId
                """, new { versionId }, cancellationToken: ct)))
                .ToDictionary(s => s.SkillCode, s => new ManifestDocumentSkill(s.SkillCode, s.SourceKind, s.ContentSha256, s.Content), StringComparer.Ordinal);

        var types = new List<ManifestDocumentType>(rows.Count);
        foreach (var row in rows)
        {
            var record = ToRecord(row);
            ManifestDocumentWorkflow? workflow = null;
            if (row.WorkflowId is { } workflowId)
            {
                if (!draft && row.WorkflowSnapshotJson is not null)
                {
                    using var doc = JsonDocument.Parse(row.WorkflowSnapshotJson);
                    workflow = new ManifestDocumentWorkflow(workflowId, row.WorkflowVersionId,
                        doc.RootElement.GetProperty("name").GetString() ?? string.Empty,
                        doc.RootElement.GetProperty("definition").Clone());
                }
                else if (draft && await ResolveWorkflowAsync(conn, null, workflowId, owner.TenantId, owner.DepartmentId, ct) is { } live)
                {
                    workflow = new ManifestDocumentWorkflow(workflowId, live.VersionId, live.Name, live.Definition);
                }
            }
            var skills = new List<ManifestDocumentSkill>();
            foreach (var code in record.SkillCodes)
            {
                if (!draft)
                {
                    if (snapshots.TryGetValue(code, out var snap)) skills.Add(snap);
                    continue;
                }
                if (await ResolveSkillSourceAsync(conn, null, code, owner.TenantId, owner.DepartmentId, ct) is { } src)
                    skills.Add(new ManifestDocumentSkill(code, src.SourceKind, src.Content is null ? null : Sha256(src.Content), src.Content));
            }
            var schema = DocumentSchema.Build(record.Code, record.Fields);
            types.Add(new ManifestDocumentType(record.Code, record.DisplayName, record.Enabled, record.AccountingHandler,
                record.Fields, schema, DocumentSchema.Sha256(schema), record.ExtractionInstructions, workflow, skills));
        }
        return new ManifestDocumentProcessing(versionId, draft, types);
    }

    /// <summary>Eligible workflow: platform, or this exact department's own PUBLISHED workflow (same rule as EnsureWorkflowEligibleAsync).</summary>
    private static async Task<(Guid? VersionId, string Name, JsonElement Definition)?> ResolveWorkflowAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid workflowId, Guid? tenantId, Guid? departmentId, CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<(Guid? VersionId, string Name, string DefinitionJson)?>(new CommandDefinition("""
            SELECT CASE WHEN w.scope='department' THEN w.published_version_id END AS VersionId, w.name AS Name,
                   CASE WHEN w.scope='department' THEN wv.definition::text ELSE w.definition::text END AS DefinitionJson
            FROM workflows w
            LEFT JOIN workflow_versions wv ON wv.id=w.published_version_id
            WHERE w.id=@workflowId AND w.is_active
              AND (w.scope='platform'
                   OR (w.scope='department' AND w.tenant_id=@tenantId AND w.department_id=@departmentId AND wv.id IS NOT NULL))
            """, new { workflowId, tenantId, departmentId }, tx, cancellationToken: ct));
        if (row is null) return null;
        using var doc = JsonDocument.Parse(row.Value.DefinitionJson);
        return (row.Value.VersionId, row.Value.Name, doc.RootElement.Clone());
    }

    /// <summary>Own-department skill wins over a platform skill with the same code; inactive or foreign skills are not eligible.</summary>
    private static Task<(Guid SkillId, string SourceKind, string? Content)?> ResolveSkillSourceAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, string code, Guid? tenantId, Guid? departmentId, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<(Guid SkillId, string SourceKind, string? Content)?>(new CommandDefinition("""
            SELECT s.id AS SkillId, s.source_kind AS SourceKind,
                   CASE WHEN s.source_kind='declarative' THEN s.declarative_content END AS Content
            FROM skills s
            WHERE s.code=@code AND s.is_active
              AND (s.scope='platform' OR (s.scope='department' AND s.tenant_id=@tenantId AND s.department_id=@departmentId))
            ORDER BY (s.scope='department') DESC
            LIMIT 1
            """, new { code, tenantId, departmentId }, tx, cancellationToken: ct));

    private static string Sha256(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
