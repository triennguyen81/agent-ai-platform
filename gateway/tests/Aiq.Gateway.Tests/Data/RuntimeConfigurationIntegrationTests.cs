using System.Text.Json;
using Aiq.Gateway.Approvals;
using Aiq.Gateway.Data;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Vouchers.DocumentProcessing;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Agent runtime configuration hardening against the full production schema
/// (init + migrations through 062, see <see cref="DocumentProcessingDatabase"/>):
/// versioned skill snapshots, the department review queue, the restored L2
/// queue, pinned re-extraction provenance and migration 062.
/// </summary>
public sealed class RuntimeConfigurationIntegrationTests(DocumentProcessingDatabase fx) : IClassFixture<DocumentProcessingDatabase>
{
    static RuntimeConfigurationIntegrationTests() => SqlMapper.AddTypeHandler(new StringArrayTypeHandler());

    private NpgsqlConnectionFactory Factory => new(fx.ConnectionString!);
    private DynamicAgentRepository Agents => new(Factory);
    private DocumentProcessingRepository Docs => new(Factory, new CapabilityRepository(Factory));
    private DocumentReviewRepository Reviews => new(Factory);

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static IReadOnlyList<DocumentTypeConfigInput> Types(Guid? receiptWorkflow = null) => DocumentTypeCatalog.All
        .Select(t => new DocumentTypeConfigInput(t.Code, true, null,
            t.DefaultFields.Select(f => new DocumentFieldInput(f.Name, f.Label, f.Type, f.Required, f.Description)).ToList(),
            t.DefaultInstructions, t.Code == "receipt" ? receiptWorkflow : null, null, null)).ToList();

    private async Task<(Guid AgentId, Guid DraftId)> NewAgentAsync(string[]? skills = null, Guid? workflowId = null)
    {
        var agent = await Agents.CreateDepartmentAgentAsync(fx.TenantA, fx.Kt, fx.AdminKt,
            new CreateSystemAgentRequest($"rc-{Guid.NewGuid():N}"[..20], "Trợ lý kế toán", null, null, null, "suggest"), default);
        var draft = (await Agents.FindDepartmentAgentAsync(fx.TenantA, fx.Kt, agent.Id, default))!.CurrentDraftVersionId!.Value;
        await Agents.UpdateDepartmentAgentVersionAsync(fx.TenantA, fx.Kt, agent.Id, draft, fx.AdminKt,
            new UpdateAgentVersionRequest(null, null, null, null, null, null, null, null, null, skills, null, workflowId, null, [fx.VoucherCapability]), default);
        return (agent.Id, draft);
    }

    private async Task<Guid> PublishedWithConfigAsync(Guid agentId, Guid draft, Guid? receiptWorkflow = null)
    {
        Assert.Empty((await Docs.ReplaceDepartmentDraftConfigAsync(fx.TenantA, fx.Kt, agentId, draft, fx.AdminKt, Types(receiptWorkflow), default)).Errors);
        Assert.NotNull(await Agents.PublishDepartmentAgentVersionAsync(fx.TenantA, fx.Kt, agentId, draft, fx.AdminKt, default));
        return draft;
    }

    private async Task<Guid> NewDraftAsync(Guid agentId, string[]? skills = null) =>
        (await Agents.CreateDepartmentAgentVersionAsync(fx.TenantA, fx.Kt, agentId, fx.AdminKt,
            new CreateAgentVersionRequest("ollama", "qwen3:latest", "Bạn là trợ lý kế toán.", 0.2m, 4, 120, 2048, null, [], skills ?? [], null, [], [fx.VoucherCapability]), default))!.Id;

    private async Task<Guid> VoucherAsync(Guid userId, Guid? department = null, string type = "receipt")
    {
        await using var conn = await OpenAsync();
        return await conn.ExecuteScalarAsync<Guid>("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, status) VALUES (@u, @t, @d, @type, 'extracted') RETURNING id
            """, new { u = userId, t = fx.TenantA, d = department ?? fx.Kt, type });
    }

    // ============================================================ SKILLS

    [Fact]
    public async Task Published_skill_snapshot_is_immutable_while_drafts_see_new_content()
    {
        if (fx.ConnectionString is null) return;
        var skillRepo = new SkillRepository(Factory);
        var skill = await skillRepo.CreateDepartmentSkillAsync(fx.TenantA, fx.Kt, fx.AdminKt,
            new CreateDepartmentSkillRequest($"nguon-{Guid.NewGuid():N}"[..14], "Nguồn cấu hình", null, "REVISION A"), default);
        var (agentId, v1) = await NewAgentAsync(skills: [skill.Code]);
        Assert.NotNull(await Agents.PublishDepartmentAgentVersionAsync(fx.TenantA, fx.Kt, agentId, v1, fx.AdminKt, default));

        // Editing a skill referenced by a published version is allowed now …
        Assert.NotNull(await skillRepo.UpdateDepartmentSkillContentAsync(fx.TenantA, fx.Kt, skill.Id, new UpdateDepartmentSkillContentRequest("REVISION B"), default));

        // … and production still runs revision A.
        var prod = (await Agents.GetDepartmentAgentManifestAsync(fx.AdminKt, fx.TenantA, fx.Kt, agentId, true, default)).Manifest!;
        var snap = Assert.Single(prod.SkillSnapshots!);
        Assert.Equal(("declarative", "REVISION A"), (snap.SourceKind, snap.Content));
        Assert.Equal(SkillSnapshotStore.Sha256("REVISION A"), snap.ContentSha256);

        // A new draft (test manifest) sees revision B without touching production.
        await NewDraftAsync(agentId, skills: [skill.Code]);
        var test = (await Agents.GetDepartmentAgentTestManifestAsync(fx.AdminKt, fx.TenantA, fx.Kt, agentId, default)).Manifest!;
        Assert.Equal("REVISION B", Assert.Single(test.SkillSnapshots!).Content);
        prod = (await Agents.GetDepartmentAgentManifestAsync(fx.AdminKt, fx.TenantA, fx.Kt, agentId, true, default)).Manifest!;
        Assert.Equal("REVISION A", Assert.Single(prod.SkillSnapshots!).Content);
    }

    [Fact]
    public async Task Inactive_assigned_skill_blocks_publish_and_image_skills_stay_name_only()
    {
        if (fx.ConnectionString is null) return;
        var skillRepo = new SkillRepository(Factory);
        var skill = await skillRepo.CreateDepartmentSkillAsync(fx.TenantA, fx.Kt, fx.AdminKt,
            new CreateDepartmentSkillRequest($"off-{Guid.NewGuid():N}"[..14], "Tắt", null, "X"), default);
        var (agentId, draft) = await NewAgentAsync(skills: [skill.Code, "accounting-vietnam"]);
        await skillRepo.SetDepartmentSkillEnabledAsync(fx.TenantA, fx.Kt, skill.Id, false, default);
        var validation = (await Agents.ValidateDepartmentAgentVersionAsync(fx.TenantA, fx.Kt, agentId, draft, default))!;
        Assert.Contains($"skill_inactive:{skill.Code}", validation.Errors);
        Assert.Null(await Agents.PublishDepartmentAgentVersionAsync(fx.TenantA, fx.Kt, agentId, draft, fx.AdminKt, default));

        await skillRepo.SetDepartmentSkillEnabledAsync(fx.TenantA, fx.Kt, skill.Id, true, default);
        Assert.NotNull(await Agents.PublishDepartmentAgentVersionAsync(fx.TenantA, fx.Kt, agentId, draft, fx.AdminKt, default));
        var manifest = (await Agents.GetDepartmentAgentManifestAsync(fx.AdminKt, fx.TenantA, fx.Kt, agentId, true, default)).Manifest!;
        var image = manifest.SkillSnapshots!.Single(s => s.Code == "accounting-vietnam");
        Assert.Equal(("filesystem", null), (image.SourceKind, image.Content));
        Assert.Empty(manifest.Tools);   // skills never add tool grants
    }

    // ===================================================== WORKFLOW (UI → runtime)

    [Fact]
    public async Task Ui_authored_workflow_prompt_reaches_the_published_manifest()
    {
        if (fx.ConnectionString is null) return;
        var workflows = new WorkflowRepository(Factory);
        Assert.True(WorkflowDefinitions.TryCanonicalize(JsonSerializer.SerializeToElement(new
        {
            steps = new object[] { new { type = "prompt", prompt = "Ghi rõ 'Nguồn cấu hình phiên bản' khi tóm tắt." } },
            requiresApproval = false,
        }), out var canonical, out _));
        var wf = await workflows.CreateDepartmentWorkflowAsync(fx.TenantA, fx.Kt, fx.AdminKt, new CreateDepartmentWorkflowRequest("Tóm tắt", null, canonical), default);
        await workflows.PublishDepartmentWorkflowVersionAsync(fx.TenantA, fx.Kt, wf.Id, wf.CurrentDraftVersionId!.Value, fx.AdminKt, default);
        var (agentId, draft) = await NewAgentAsync(workflowId: wf.Id);
        Assert.NotNull(await Agents.PublishDepartmentAgentVersionAsync(fx.TenantA, fx.Kt, agentId, draft, fx.AdminKt, default));
        var manifest = (await Agents.GetDepartmentAgentManifestAsync(fx.AdminKt, fx.TenantA, fx.Kt, agentId, true, default)).Manifest!;
        var step = manifest.Workflow!.Definition.GetProperty("steps")[0];
        Assert.Equal("Ghi rõ 'Nguồn cấu hình phiên bản' khi tóm tắt.", step.GetProperty("prompt").GetString());
    }

    // ======================================================= REVIEW QUEUE

    private DocumentReviewOpenRequest Open(Guid voucherId, Guid workflowId, Guid? workflowVersionId = null, Guid? dept = null) =>
        new(fx.TenantA, dept ?? fx.Kt, voucherId, null, null, workflowId, workflowVersionId, "receipt", ["workflow_require_review"], fx.AdminKt);

    [Fact]
    public async Task Require_review_persists_exactly_one_task_even_when_repeated_or_concurrent()
    {
        if (fx.ConnectionString is null) return;
        var voucherId = await VoucherAsync(fx.UserKt);
        var workflowId = Guid.NewGuid();
        var workflowVersion = Guid.NewGuid();
        var (first, created) = await Reviews.OpenAsync(Open(voucherId, workflowId, workflowVersion), default);
        Assert.True(created);
        var (again, createdAgain) = await Reviews.OpenAsync(Open(voucherId, workflowId, workflowVersion), default);
        Assert.False(createdAgain);
        Assert.Equal(first.Id, again.Id);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Reviews.OpenAsync(Open(voucherId, workflowId, workflowVersion), default)));
        Assert.All(concurrent, r => Assert.Equal(first.Id, r.Task.Id));

        await using var conn = await OpenAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_review_tasks WHERE voucher_id=@voucherId", new { voucherId }));
        Assert.Equal(DocumentReviewStatuses.AwaitingReview, await Reviews.GetGateStatusAsync(fx.TenantA, voucherId, default));
    }

    [Fact]
    public async Task Only_the_documents_own_department_can_decide_and_decisions_are_final()
    {
        if (fx.ConnectionString is null) return;
        var voucherId = await VoucherAsync(fx.UserKt);
        var (task, _) = await Reviews.OpenAsync(Open(voucherId, Guid.NewGuid()), default);

        Assert.Null(await Reviews.DecideAsync(fx.TenantA, fx.Kd, voucherId, task.Id, fx.AdminKd, "approved", null, default));   // other department
        Assert.Null(await Reviews.DecideAsync(fx.TenantB, fx.X, voucherId, task.Id, fx.AdminB, "approved", null, default));     // other tenant
        Assert.Null(await Reviews.ListForVoucherAsync(fx.TenantA, fx.Kd, voucherId, default));
        Assert.Equal(DocumentReviewStatuses.AwaitingReview, await Reviews.GetGateStatusAsync(fx.TenantA, voucherId, default));

        var decided = await Reviews.DecideAsync(fx.TenantA, fx.Kt, voucherId, task.Id, fx.AdminKt, "approved", "OK", default);
        Assert.Equal(("approved", fx.AdminKt), (decided!.Status, decided.DecidedBy!.Value));
        Assert.Equal(DocumentReviewStatuses.Approved, await Reviews.GetGateStatusAsync(fx.TenantA, voucherId, default));
        Assert.Null(await Reviews.DecideAsync(fx.TenantA, fx.Kt, voucherId, task.Id, fx.AdminKt, "rejected", null, default));

        await using var conn = await OpenAsync();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(
            "UPDATE document_review_tasks SET status='open', decided_by=NULL, decided_at=NULL WHERE id=@id", new { id = task.Id }));
        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public async Task Rejection_keeps_the_document_blocked_and_a_new_review_reopens_the_gate()
    {
        if (fx.ConnectionString is null) return;
        var voucherId = await VoucherAsync(fx.UserKt);
        var wf = Guid.NewGuid();
        var (task, _) = await Reviews.OpenAsync(Open(voucherId, wf), default);
        await Reviews.DecideAsync(fx.TenantA, fx.Kt, voucherId, task.Id, fx.AdminKt, "rejected", "sai", default);
        Assert.True(DocumentReviewStatuses.Blocks(await Reviews.GetGateStatusAsync(fx.TenantA, voucherId, default)));
        var (reopened, created) = await Reviews.OpenAsync(Open(voucherId, wf), default);   // re-extraction
        Assert.True(created);
        Assert.NotEqual(task.Id, reopened.Id);
        Assert.Equal(DocumentReviewStatuses.AwaitingReview, await Reviews.GetGateStatusAsync(fx.TenantA, voucherId, default));
    }

    [Fact]
    public async Task Review_task_for_a_document_outside_scope_is_never_created()
    {
        if (fx.ConnectionString is null) return;
        var voucherId = await VoucherAsync(fx.UserKt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Reviews.OpenAsync(Open(voucherId, Guid.NewGuid(), dept: fx.Kd), default));
        await using var conn = await OpenAsync();
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_review_tasks WHERE voucher_id=@voucherId", new { voucherId }));
    }

    [Fact]
    public async Task Missing_queue_infrastructure_throws_instead_of_pretending()
    {
        if (fx.ConnectionString is null) return;
        var voucherId = await VoucherAsync(fx.UserKt);
        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("ALTER TABLE document_review_tasks RENAME TO document_review_tasks_hidden", transaction: tx);
        // Same connection/transaction: the repository opens its own connection,
        // so exercise the exact INSERT it issues against the hidden table.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(
            "INSERT INTO document_review_tasks(tenant_id, department_id, voucher_id, workflow_key, document_type) VALUES (@t, @d, @v, @w, 'receipt')",
            new { t = fx.TenantA, d = fx.Kt, v = voucherId, w = Guid.NewGuid() }, tx));
        Assert.Equal("42P01", ex.SqlState);
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Legacy_l2_queue_exists_again_and_enqueues()
    {
        if (fx.ConnectionString is null) return;
        var voucherId = await VoucherAsync(fx.UserKt, type: "invoice_vat_in");
        var item = await new ApprovalRepository(Factory).EnqueueAsync(fx.TenantA, voucherId, "invoice_vat_in", "HD-1", 600_000_000m, "VND",
            ["invoice_over_500m_requires_l2_approval"], fx.UserKt, default);
        Assert.Equal("pending", item.Status);
    }

    // ===================================================== RE-EXTRACTION

    [Fact]
    public async Task Reextract_uses_the_recorded_version_even_after_uploader_lost_access_and_v2_published()
    {
        if (fx.ConnectionString is null) return;
        var (agentId, v1) = await NewAgentAsync();
        await PublishedWithConfigAsync(agentId, v1);
        Assert.True(await Agents.AssignUserToDepartmentAgentAsync(fx.TenantA, fx.Kt, agentId, fx.UserKt, fx.AdminKt, default));
        var voucherId = await VoucherAsync(fx.UserKt);
        await Docs.SetVoucherProvenanceAsync(fx.TenantA, voucherId, agentId, v1, DocumentConfigSources.Manifest, default);

        // Newer published version + uploader loses the assignment.
        var v2 = await NewDraftAsync(agentId);
        await PublishedWithConfigAsync(agentId, v2);
        Assert.True(await Agents.RevokeUserFromDepartmentAgentAsync(fx.TenantA, fx.Kt, agentId, fx.UserKt, default));
        // The OLD path (uploader eligibility) would now find nothing → legacy:
        Assert.Null((await Docs.ResolveForUploaderAsync(fx.TenantA, fx.Kt, fx.UserKt, null, default)).AgentId);

        var provenance = await Docs.GetProvenanceAsync(fx.TenantA, voucherId, default);
        Assert.True(provenance.IsManifest);
        var pinned = await Docs.ResolvePinnedAsync(fx.TenantA, fx.Kt, provenance.AgentId, provenance.AgentVersionId, default);
        Assert.Null(pinned.DenialReason);
        Assert.True(pinned.Pinned);
        Assert.Equal((v1, 1, "manifest"), (pinned.AgentVersionId!.Value, pinned.Version!.Value, pinned.ConfigSource));
        Assert.Equal(v1, pinned.Config!.AgentVersionId);

        // The current ADMIN may run exactly v1 (not v2); the unassigned uploader may not.
        var admin = await Agents.GetDepartmentAgentManifestAsync(fx.AdminKt, fx.TenantA, fx.Kt, agentId, true, default, pinnedVersionId: v1);
        Assert.Equal((v1, 1), (admin.Manifest!.PublishedVersionId, admin.Manifest.Version));
        Assert.Equal(v1, admin.Manifest.DocumentProcessing!.AgentVersionId);
        var user = await Agents.GetDepartmentAgentManifestAsync(fx.UserKt, fx.TenantA, fx.Kt, agentId, false, default, pinnedVersionId: v1);
        Assert.Null(user.Manifest);
        // Unpinned production still runs the newest published version.
        Assert.Equal(v2, (await Agents.GetDepartmentAgentManifestAsync(fx.AdminKt, fx.TenantA, fx.Kt, agentId, true, default)).Manifest!.PublishedVersionId);
    }

    [Fact]
    public async Task Draft_version_can_never_be_pinned()
    {
        if (fx.ConnectionString is null) return;
        var (agentId, v1) = await NewAgentAsync();
        await PublishedWithConfigAsync(agentId, v1);
        var draft = await NewDraftAsync(agentId);
        var manifest = await Agents.GetDepartmentAgentManifestAsync(fx.AdminKt, fx.TenantA, fx.Kt, agentId, true, default, pinnedVersionId: draft);
        Assert.Equal("pinned_version_not_published", manifest.DenialReason);
        Assert.Equal(DocumentProvenanceErrors.VersionUnavailable,
            (await Docs.ResolvePinnedAsync(fx.TenantA, fx.Kt, agentId, draft, default)).DenialReason);
    }

    [Fact]
    public async Task Broken_or_foreign_provenance_fails_explicitly_never_legacy()
    {
        if (fx.ConnectionString is null) return;
        var (agentId, v1) = await NewAgentAsync();
        await PublishedWithConfigAsync(agentId, v1);
        var (otherAgent, otherV1) = await NewAgentAsync();
        await PublishedWithConfigAsync(otherAgent, otherV1);

        async Task<DocumentAgentResolution> Pin(Guid? a, Guid? v, Guid? dept = null) =>
            await Docs.ResolvePinnedAsync(fx.TenantA, dept ?? fx.Kt, a, v, default);

        Assert.Equal(DocumentProvenanceErrors.ProvenanceInvalid, (await Pin(null, v1)).DenialReason);
        Assert.Equal(DocumentProvenanceErrors.ProvenanceInvalid, (await Pin(agentId, null)).DenialReason);
        Assert.Equal(DocumentProvenanceErrors.AgentUnavailable, (await Pin(Guid.NewGuid(), v1)).DenialReason);
        Assert.Equal(DocumentProvenanceErrors.VersionUnavailable, (await Pin(agentId, Guid.NewGuid())).DenialReason);
        Assert.Equal(DocumentProvenanceErrors.ProvenanceInvalid, (await Pin(agentId, otherV1)).DenialReason);   // version of another agent
        Assert.Equal(DocumentProvenanceErrors.ProvenanceInvalid, (await Pin(agentId, v1, fx.Kd)).DenialReason); // agent of another department
        await Agents.SetDepartmentAgentEnabledAsync(fx.TenantA, fx.Kt, agentId, fx.AdminKt, false, default);
        Assert.Equal(DocumentProvenanceErrors.AgentUnavailable, (await Pin(agentId, v1)).DenialReason);         // kill switch respected
        Assert.All(new[] { await Pin(agentId, v1), await Pin(null, null) }, r =>
        {
            Assert.True(r.Pinned);
            Assert.Null(r.Config);
            Assert.Equal(DocumentConfigSources.Manifest, r.ConfigSource);   // never reported as legacy
        });
    }

    [Fact]
    public async Task True_legacy_vouchers_keep_legacy_compatibility()
    {
        if (fx.ConnectionString is null) return;
        var neverProcessed = await VoucherAsync(fx.UserKt);
        var legacyProcessed = await VoucherAsync(fx.UserKt);
        await Docs.SetVoucherProvenanceAsync(fx.TenantA, legacyProcessed, null, null, DocumentConfigSources.Legacy, default);
        var a = await Docs.GetProvenanceAsync(fx.TenantA, neverProcessed, default);
        var b = await Docs.GetProvenanceAsync(fx.TenantA, legacyProcessed, default);
        Assert.False(a.IsManifest);
        Assert.False(a.IsReextract);
        Assert.False(b.IsManifest);
        Assert.True(b.IsReextract);
    }

    [Fact]
    public async Task Reextract_runs_are_distinguished_in_the_audit()
    {
        if (fx.ConnectionString is null) return;
        var voucherId = await VoucherAsync(fx.UserKt);
        var id = await Docs.RecordRunAsync(new DocumentProcessingRun("production", voucherId, fx.TenantA, fx.Kt, fx.AdminKt, null, null, null,
            DocumentConfigSources.Manifest, null, null, null, null, [], null, null, null, null, null, null, [], false,
            TriggerKind: "reextract", ErrorCode: DocumentProvenanceErrors.AgentUnavailable), default);
        await using var conn = await OpenAsync();
        var row = await conn.QuerySingleAsync<(string Trigger, string Error)>(
            "SELECT trigger_kind, error_code FROM document_processing_runs WHERE id=@id", new { id });
        Assert.Equal(("reextract", "processing_agent_unavailable"), (row.Trigger, row.Error));
    }

    // =========================================================== MIGRATION

    [Fact]
    public async Task Migration_062_is_idempotent_and_backfills_pre_existing_published_skill_snapshots()
    {
        if (fx.ConnectionString is null) return;
        var skillRepo = new SkillRepository(Factory);
        var skill = await skillRepo.CreateDepartmentSkillAsync(fx.TenantA, fx.Kt, fx.AdminKt,
            new CreateDepartmentSkillRequest($"bf-{Guid.NewGuid():N}"[..14], "Backfill", null, "PUBLISHED CONTENT"), default);
        var (agentId, v1) = await NewAgentAsync(skills: [skill.Code]);
        Assert.NotNull(await Agents.PublishDepartmentAgentVersionAsync(fx.TenantA, fx.Kt, agentId, v1, fx.AdminKt, default));
        await using var conn = await OpenAsync();
        // Simulate a version published before this phase (no snapshot rows).
        await conn.ExecuteAsync("""
            ALTER TABLE agent_version_skill_snapshots DISABLE TRIGGER trg_protect_published_version_skill_snapshots;
            DELETE FROM agent_version_skill_snapshots WHERE agent_version_id=@v1;
            ALTER TABLE agent_version_skill_snapshots ENABLE TRIGGER trg_protect_published_version_skill_snapshots;
            """, new { v1 });

        var sql = await File.ReadAllTextAsync(Path.Combine(TaxVerificationDatabase.MigrationsDir(), "062_runtime_configuration_hardening.sql"));
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await conn.ExecuteAsync(sql, transaction: tx);
            await conn.ExecuteAsync(sql, transaction: tx);   // second run: no-op
            await tx.CommitAsync();
        }
        Assert.Equal("PUBLISHED CONTENT", await conn.ExecuteScalarAsync<string>(
            "SELECT content FROM agent_version_skill_snapshots WHERE agent_version_id=@v1 AND skill_code=@code", new { v1, code = skill.Code }));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM agent_version_skill_snapshots WHERE agent_version_id=@v1", new { v1 }));
    }
}
