using System.Text.Json;
using Aiq.Gateway.Data;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Vouchers.DocumentProcessing;
using Dapper;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// One throwaway DATABASE with the full production schema (init 01..20 + every
/// Gateway migration through 060) for configurable document processing.
/// Tenant A: KT {AdminKt, UserKt}, KD {AdminKd}; Tenant B: X {AdminB}; plus
/// Tenant A's TENANT_OWNER.
/// </summary>
public sealed class DocumentProcessingDatabase : IAsyncLifetime
{
    public string? ConnectionString { get; private set; }
    private readonly string? _base = Environment.GetEnvironmentVariable("AIQ_TEST_POSTGRES") is { Length: > 0 } explicitCs ? explicitCs
        : Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") is { Length: > 0 } password
            ? new NpgsqlConnectionStringBuilder
            {
                Host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "postgres", Port = 5432,
                Database = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "aiq",
                Username = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "aiq", Password = password,
            }.ConnectionString
            : null;
    private readonly string _db = $"aiq_doctest_{Guid.NewGuid():N}";

    public Guid TenantA, TenantB, Kt, Kd, X, AdminKt, UserKt, AdminKd, AdminB, OwnerA, VoucherCapability;

    public async Task InitializeAsync()
    {
        if (_base is null) return;
        await using (var admin = new NpgsqlConnection(_base))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync($"CREATE DATABASE {_db}");
        }
        ConnectionString = new NpgsqlConnectionStringBuilder(_base) { Database = _db, Pooling = false }.ConnectionString;
        await RunAsync(RepoPath("infra", "postgres", "init"), transactional: false);
        await using (var db = new NpgsqlConnection(ConnectionString))
        {
            await db.OpenAsync();
            await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS schema_migrations (version TEXT PRIMARY KEY, checksum TEXT, applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW())");
        }
        await RunAsync(TaxVerificationDatabase.MigrationsDir(), transactional: true);
        await SeedAsync();
    }

    private async Task RunAsync(string dir, bool transactional)
    {
        await using var db = new NpgsqlConnection(ConnectionString);
        await db.OpenAsync();
        foreach (var path in Directory.GetFiles(dir, "*.sql").Order(StringComparer.Ordinal))
        {
            await using var tx = transactional ? await db.BeginTransactionAsync() : null;
            var sql = string.Join("\n", (await File.ReadAllLinesAsync(path)).Where(l => !l.TrimStart().StartsWith('\\')));
            await db.ExecuteAsync(new CommandDefinition(sql, transaction: tx, commandTimeout: 120));
            if (tx is not null) await tx.CommitAsync();
        }
    }

    private static string RepoPath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "infra", "postgres", "init"))) dir = dir.Parent;
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }

    private async Task SeedAsync()
    {
        (TenantA, TenantB, Kt, Kd, X) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        (AdminKt, UserKt, AdminKd, AdminB, OwnerA) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var db = new NpgsqlConnection(ConnectionString);
        await db.OpenAsync();
        await db.ExecuteAsync("""
            INSERT INTO tenants(id, code, name) VALUES (@a, @ac, 'Tenant A'), (@b, @bc, 'Tenant B');
            INSERT INTO departments(id, tenant_id, code, name) VALUES (@kt, @a, 'KT', 'Kế toán'), (@kd, @a, 'KD', 'Kinh doanh'), (@x, @b, 'X', 'Khác');
            INSERT INTO users(id, email, password_hash, tenant_id, department_id) VALUES
                (@adminKt, @e1, 'x', @a, @kt), (@userKt, @e2, 'x', @a, @kt), (@adminKd, @e3, 'x', @a, @kd),
                (@adminB, @e4, 'x', @b, @x), (@ownerA, @e5, 'x', @a, NULL);
            INSERT INTO user_roles(user_id, role_id) SELECT u, r.id FROM (VALUES (@adminKt,'admin'),(@userKt,'user'),(@adminKd,'admin'),(@adminB,'admin'),(@ownerA,'tenant_owner')) v(u, role)
                JOIN roles r ON r.name = v.role;
            """, new
        {
            a = TenantA, ac = "A" + TenantA.ToString("N")[..8], b = TenantB, bc = "B" + TenantB.ToString("N")[..8],
            kt = Kt, kd = Kd, x = X, adminKt = AdminKt, userKt = UserKt, adminKd = AdminKd, adminB = AdminB, ownerA = OwnerA,
            e1 = $"akt-{AdminKt:N}@t.vn", e2 = $"ukt-{UserKt:N}@t.vn", e3 = $"akd-{AdminKd:N}@t.vn", e4 = $"ab-{AdminB:N}@t.vn", e5 = $"o-{OwnerA:N}@t.vn",
        });
        VoucherCapability = await db.ExecuteScalarAsync<Guid>("SELECT id FROM business_capabilities WHERE code='voucher.processing'");
    }

    public async Task DisposeAsync()
    {
        if (_base is null) return;
        await using var admin = new NpgsqlConnection(_base);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {_db} WITH (FORCE)");
    }
}

public sealed class DocumentProcessingIntegrationTests(DocumentProcessingDatabase fx) : IClassFixture<DocumentProcessingDatabase>
{
    static DocumentProcessingIntegrationTests() => SqlMapper.AddTypeHandler(new StringArrayTypeHandler());

    private NpgsqlConnectionFactory Factory => new(fx.ConnectionString!);
    private DynamicAgentRepository Agents => new(Factory);
    private DocumentProcessingRepository Docs => new(Factory, new CapabilityRepository(Factory));

    private static DocumentTypeConfigInput Type(string code, bool enabled = true, string? instructions = null,
        IReadOnlyList<DocumentFieldInput>? fields = null, Guid? workflowId = null, string[]? skills = null)
    {
        var system = DocumentTypeCatalog.Find(code)!;
        return new(code, enabled, null,
            fields ?? system.DefaultFields.Select(f => new DocumentFieldInput(f.Name, f.Label, f.Type, f.Required, f.Description)).ToList(),
            instructions ?? system.DefaultInstructions, workflowId, skills, null);
    }

    private static readonly JsonElement OneStep =
        JsonSerializer.SerializeToElement(new { steps = new object[] { new { type = "prompt", prompt = "Kiểm tra" } }, requiresApproval = false });

    private static IReadOnlyList<DocumentTypeConfigInput> AllSystemTypes() => DocumentTypeCatalog.All.Select(t => Type(t.Code)).ToList();

    /// <summary>A Department Agent of KT declaring voucher.processing, with its v1 draft.</summary>
    private async Task<(Guid AgentId, Guid DraftId)> NewKtAgentAsync()
    {
        var agent = await Agents.CreateDepartmentAgentAsync(fx.TenantA, fx.Kt, fx.AdminKt,
            new CreateSystemAgentRequest($"kt-{Guid.NewGuid():N}"[..20], "Trợ lý kế toán", null, null, null, "suggest"), default);
        // The initial v1 draft is created by an AFTER INSERT trigger — re-read.
        var draft = (await Agents.FindDepartmentAgentAsync(fx.TenantA, fx.Kt, agent.Id, default))!.CurrentDraftVersionId!.Value;
        await Agents.UpdateDepartmentAgentVersionAsync(fx.TenantA, fx.Kt, agent.Id, draft, fx.AdminKt,
            new UpdateAgentVersionRequest(null, null, null, null, null, null, null, null, null, null, null, null, null, [fx.VoucherCapability]), default);
        return (agent.Id, draft);
    }

    private async Task<Guid> NewDraftAsync(Guid agentId) =>
        (await Agents.CreateDepartmentAgentVersionAsync(fx.TenantA, fx.Kt, agentId, fx.AdminKt,
            new CreateAgentVersionRequest("ollama", "qwen3:latest", "Bạn là trợ lý kế toán.", 0.2m, 4, 120, 2048, null, [], [], null, [], [fx.VoucherCapability]), default))!.Id;

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    // ===================================================== CONFIGURATION

    [Fact]
    public async Task Draft_config_create_edit_enable_disable_round_trip()
    {
        if (fx.ConnectionString is null) return;
        var (agentId, draft) = await NewKtAgentAsync();

        var saved = await Docs.ReplaceDepartmentDraftConfigAsync(fx.TenantA, fx.Kt, agentId, draft, fx.AdminKt, AllSystemTypes(), default);
        Assert.Empty(saved.Errors);
        Assert.Equal(8, saved.Types!.Count);

        // edit one harmless description + disable payment
        var edited = AllSystemTypes().Select(t => t.Code switch
        {
            "invoice_vat_out" => t with { Fields = t.Fields!.Select(f => f.Name == "buyer_mst" ? f with { Description = "MST khách hàng (10 số)" } : f).ToList() },
            "payment" => t with { Enabled = false },
            _ => t,
        }).ToList();
        Assert.Empty((await Docs.ReplaceDepartmentDraftConfigAsync(fx.TenantA, fx.Kt, agentId, draft, fx.AdminKt, edited, default)).Errors);

        var config = (await Docs.GetDepartmentVersionConfigAsync(fx.TenantA, fx.Kt, agentId, draft, default))!;
        Assert.Equal("MST khách hàng (10 số)", config.Types.Single(t => t.Code == "invoice_vat_out").Fields.Single(f => f.Name == "buyer_mst").Description);
        Assert.False(config.Types.Single(t => t.Code == "payment").Enabled);
        Assert.Equal("sales_invoice", config.Types.Single(t => t.Code == "invoice_vat_out").AccountingHandler);
    }

    [Fact]
    public async Task Publish_freezes_config_and_draft_changes_never_reach_published_runtime()
    {
        if (fx.ConnectionString is null) return;
        var (agentId, v1) = await NewKtAgentAsync();
        Assert.Empty((await Docs.ReplaceDepartmentDraftConfigAsync(fx.TenantA, fx.Kt, agentId, v1, fx.AdminKt, AllSystemTypes(), default)).Errors);
        Assert.NotNull(await Agents.PublishDepartmentAgentVersionAsync(fx.TenantA, fx.Kt, agentId, v1, fx.AdminKt, default));

        // Published rows are immutable: repository refuses, DB trigger refuses.
        var refused = await Docs.ReplaceDepartmentDraftConfigAsync(fx.TenantA, fx.Kt, agentId, v1, fx.AdminKt, AllSystemTypes(), default);
        Assert.Equal("published_versions_are_immutable", refused.Failure);
        await using (var conn = await OpenAsync())
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(
                "UPDATE agent_version_document_types SET enabled=false WHERE agent_version_id=@v1", new { v1 }));
            Assert.Equal("23514", ex.SqlState);
            await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(
                "DELETE FROM agent_version_document_types WHERE agent_version_id=@v1", new { v1 }));
        }

        // New draft inherits v1's config; editing it does not touch v1.
        var v2 = await NewDraftAsync(agentId);
        var inherited = (await Docs.GetDepartmentVersionConfigAsync(fx.TenantA, fx.Kt, agentId, v2, default))!;
        Assert.Equal(8, inherited.Types.Count);
        var changed = AllSystemTypes().Select(t => t.Code == "invoice_vat_out"
            ? t with { Fields = [.. t.Fields!, new DocumentFieldInput("project_code", "Mã dự án", "string", true, null)] } : t).ToList();
        Assert.Empty((await Docs.ReplaceDepartmentDraftConfigAsync(fx.TenantA, fx.Kt, agentId, v2, fx.AdminKt, changed, default)).Errors);

        // Production resolution = published v1, without the draft field.
        var runtime = await Docs.ResolveForUploaderAsync(fx.TenantA, fx.Kt, fx.AdminKt, agentId, default);
        Assert.Equal(agentId, runtime.AgentId);
        Assert.Equal(v1, runtime.AgentVersionId);
        Assert.Equal(DocumentConfigSources.Manifest, runtime.ConfigSource);
        Assert.False(runtime.Config!.Draft);
        Assert.DoesNotContain(runtime.Config.Find("invoice_vat_out")!.Fields, f => f.Name == "project_code");

        // The execution manifest of the published version carries the same.
        var manifest = (await Agents.GetDepartmentAgentManifestAsync(fx.AdminKt, fx.TenantA, fx.Kt, agentId, true, default)).Manifest!;
        Assert.Equal(v1, manifest.DocumentProcessing!.AgentVersionId);
        Assert.DoesNotContain(manifest.DocumentProcessing.Find("invoice_vat_out")!.Fields, f => f.Name == "project_code");

        // Only the Agent Builder test manifest sees the draft.
        var test = (await Agents.GetDepartmentAgentTestManifestAsync(fx.AdminKt, fx.TenantA, fx.Kt, agentId, default)).Manifest!;
        Assert.True(test.DocumentProcessing!.Draft);
        Assert.Contains(test.DocumentProcessing.Find("invoice_vat_out")!.Fields, f => f.Name == "project_code");
    }

    [Fact]
    public async Task Workflow_and_skill_are_pinned_at_publish()
    {
        if (fx.ConnectionString is null) return;
        var workflows = new WorkflowRepository(Factory);
        var skills = new SkillRepository(Factory);
        var wf = await workflows.CreateDepartmentWorkflowAsync(fx.TenantA, fx.Kt, fx.AdminKt, new CreateDepartmentWorkflowRequest(
            "Duyệt phiếu thu", null, JsonSerializer.SerializeToElement(new { steps = new object[] { new { type = "prompt", prompt = "Bước v1" }, new { type = "require_review" } }, requiresApproval = false })), default);
        var wfV1 = await workflows.PublishDepartmentWorkflowVersionAsync(fx.TenantA, fx.Kt, wf.Id, wf.CurrentDraftVersionId!.Value, fx.AdminKt, default);
        var skill = await skills.CreateDepartmentSkillAsync(fx.TenantA, fx.Kt, fx.AdminKt,
            new CreateDepartmentSkillRequest($"pt-{Guid.NewGuid():N}"[..12], "Hướng dẫn phiếu thu", null, "NỘI DUNG GỐC"), default);

        var (agentId, v1) = await NewKtAgentAsync();
        var types = AllSystemTypes().Select(t => t.Code == "receipt" ? t with { WorkflowId = wf.Id, SkillCodes = [skill.Code] } : t).ToList();
        Assert.Empty((await Docs.ReplaceDepartmentDraftConfigAsync(fx.TenantA, fx.Kt, agentId, v1, fx.AdminKt, types, default)).Errors);
        Assert.NotNull(await Agents.PublishDepartmentAgentVersionAsync(fx.TenantA, fx.Kt, agentId, v1, fx.AdminKt, default));

        // Later changes to the workflow and the skill …
        var wfDraft2 = await workflows.CreateDepartmentWorkflowDraftVersionAsync(fx.TenantA, fx.Kt, wf.Id, fx.AdminKt,
            JsonSerializer.SerializeToElement(new { steps = new object[] { new { type = "prompt", prompt = "Bước v2" } }, requiresApproval = false }), default);
        await workflows.PublishDepartmentWorkflowVersionAsync(fx.TenantA, fx.Kt, wf.Id, wfDraft2!.Id, fx.AdminKt, default);
        Assert.NotNull(await skills.UpdateDepartmentSkillContentAsync(fx.TenantA, fx.Kt, skill.Id, new UpdateDepartmentSkillContentRequest("NỘI DUNG MỚI"), default));

        // … never reach the published version.
        var receipt = (await Docs.ResolveForUploaderAsync(fx.TenantA, fx.Kt, fx.AdminKt, agentId, default)).Config!.Find("receipt")!;
        Assert.Equal(wfV1!.Id, receipt.Workflow!.VersionId);
        Assert.True(receipt.WorkflowRequiresReview);
        Assert.Contains("Bước v1", DocumentWorkflow.PromptLines(receipt.Workflow));
        var snap = Assert.Single(receipt.Skills);
        Assert.Equal("declarative", snap.SourceKind);
        Assert.Equal("NỘI DUNG GỐC", snap.Content);
        Assert.NotNull(snap.ContentSha256);
    }

    // ======================================================== VALIDATION

    [Fact]
    public async Task Invalid_configurations_are_rejected_with_structured_errors()
    {
        if (fx.ConnectionString is null) return;
        var (agentId, draft) = await NewKtAgentAsync();
        async Task<IReadOnlyList<string>> Save(params DocumentTypeConfigInput[] types) =>
            (await Docs.ReplaceDepartmentDraftConfigAsync(fx.TenantA, fx.Kt, agentId, draft, fx.AdminKt, types, default)).Errors;

        Assert.Contains("duplicate_document_type:receipt", await Save(Type("receipt"), Type("receipt")));
        Assert.Contains("unsupported_field_type:receipt:amount:money", await Save(Type("receipt", fields: [new("amount", null, "money", true, null)])));
        Assert.Contains("missing_skill:receipt:no-such-skill", await Save(Type("receipt", skills: ["no-such-skill"])));
        Assert.Contains("missing_workflow:receipt", await Save(Type("receipt", workflowId: Guid.NewGuid())));
        Assert.Contains("accounting_handler_not_configurable:receipt", await Save(Type("receipt") with { AccountingHandler = "cash_payment" }));

        // A filesystem (image) skill must also be assigned to the version so the runtime loads it.
        await using (var conn = await OpenAsync())
            await conn.ExecuteAsync("INSERT INTO skills(code, name, scope, source_kind) VALUES ('fs-doc-skill','fs','platform','filesystem') ON CONFLICT DO NOTHING");
        Assert.Contains("document_type_skill_not_assigned:receipt:fs-doc-skill", await Save(Type("receipt", skills: ["fs-doc-skill"])));

        // Nothing was persisted by any rejected save.
        Assert.Empty((await Docs.GetDepartmentVersionConfigAsync(fx.TenantA, fx.Kt, agentId, draft, default))!.Types);
    }

    [Fact]
    public async Task Foreign_or_unpublished_workflow_is_not_eligible_and_publish_revalidates()
    {
        if (fx.ConnectionString is null) return;
        var workflows = new WorkflowRepository(Factory);
        var unpublished = await workflows.CreateDepartmentWorkflowAsync(fx.TenantA, fx.Kt, fx.AdminKt, new CreateDepartmentWorkflowRequest("Nháp", null, OneStep), default);
        var foreign = await workflows.CreateDepartmentWorkflowAsync(fx.TenantA, fx.Kd, fx.AdminKd, new CreateDepartmentWorkflowRequest("Của KD", null, OneStep), default);
        await workflows.PublishDepartmentWorkflowVersionAsync(fx.TenantA, fx.Kd, foreign.Id, foreign.CurrentDraftVersionId!.Value, fx.AdminKd, default);

        var (agentId, draft) = await NewKtAgentAsync();
        foreach (var wfId in new[] { unpublished.Id, foreign.Id })
            Assert.Contains("missing_workflow:receipt", (await Docs.ReplaceDepartmentDraftConfigAsync(fx.TenantA, fx.Kt, agentId, draft, fx.AdminKt,
                [Type("receipt", workflowId: wfId)], default)).Errors);

        // Valid at save time, deactivated before publish → publish blocked.
        var ok = await workflows.CreateDepartmentWorkflowAsync(fx.TenantA, fx.Kt, fx.AdminKt, new CreateDepartmentWorkflowRequest("Hợp lệ", null, OneStep), default);
        await workflows.PublishDepartmentWorkflowVersionAsync(fx.TenantA, fx.Kt, ok.Id, ok.CurrentDraftVersionId!.Value, fx.AdminKt, default);
        Assert.Empty((await Docs.ReplaceDepartmentDraftConfigAsync(fx.TenantA, fx.Kt, agentId, draft, fx.AdminKt, [Type("receipt", workflowId: ok.Id)], default)).Errors);
        await workflows.SetDepartmentWorkflowEnabledAsync(fx.TenantA, fx.Kt, ok.Id, false, default);
        var validation = (await Agents.ValidateDepartmentAgentVersionAsync(fx.TenantA, fx.Kt, agentId, draft, default))!;
        Assert.False(validation.Valid);
        Assert.Contains("missing_workflow:receipt", validation.Errors);
        Assert.Null(await Agents.PublishDepartmentAgentVersionAsync(fx.TenantA, fx.Kt, agentId, draft, fx.AdminKt, default));
    }

    // =============================================================== ACL

    [Fact]
    public async Task Cross_department_and_cross_tenant_admins_cannot_read_or_edit()
    {
        if (fx.ConnectionString is null) return;
        var (agentId, draft) = await NewKtAgentAsync();
        Assert.Null(await Docs.GetDepartmentVersionConfigAsync(fx.TenantA, fx.Kd, agentId, draft, default));
        Assert.Null(await Docs.GetDepartmentVersionConfigAsync(fx.TenantB, fx.X, agentId, draft, default));
        Assert.Equal("not_found", (await Docs.ReplaceDepartmentDraftConfigAsync(fx.TenantA, fx.Kd, agentId, draft, fx.AdminKd, AllSystemTypes(), default)).Failure);
        Assert.Equal("not_found", (await Docs.ReplaceDepartmentDraftConfigAsync(fx.TenantB, fx.X, agentId, draft, fx.AdminB, AllSystemTypes(), default)).Failure);
        Assert.Null(await Docs.GetDepartmentTestTargetAsync(fx.TenantA, fx.Kd, agentId, default));
        Assert.Null(await Docs.GetDepartmentVoucherRawTextAsync(fx.TenantB, fx.X, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Requested_agent_is_only_a_hint_reauthorized_per_uploader()
    {
        if (fx.ConnectionString is null) return;
        var (agentId, v1) = await NewKtAgentAsync();
        Assert.NotNull(await Agents.PublishDepartmentAgentVersionAsync(fx.TenantA, fx.Kt, agentId, v1, fx.AdminKt, default));
        // KD's admin asking for KT's agent gets nothing.
        var denied = await Docs.ResolveForUploaderAsync(fx.TenantA, fx.Kd, fx.AdminKd, agentId, default);
        Assert.Null(denied.AgentId);
        // An unassigned USER gets nothing; a TENANT_OWNER is not a document-processing principal.
        Assert.Null((await Docs.ResolveForUploaderAsync(fx.TenantA, fx.Kt, fx.UserKt, agentId, default)).AgentId);
        Assert.Equal("role_not_eligible", (await Docs.ResolveForUploaderAsync(fx.TenantA, fx.Kt, fx.OwnerA, agentId, default)).DenialReason);
    }

    // =========================================================== RUNTIME

    [Fact]
    public async Task Agent_without_document_config_resolves_to_legacy()
    {
        if (fx.ConnectionString is null) return;
        // Fresh tenant-B department so this agent is the only eligible one.
        var agent = await Agents.CreateDepartmentAgentAsync(fx.TenantB, fx.X, fx.AdminB,
            new CreateSystemAgentRequest($"leg-{Guid.NewGuid():N}"[..20], "Legacy", null, null, null, "suggest"), default);
        var v1 = (await Agents.FindDepartmentAgentAsync(fx.TenantB, fx.X, agent.Id, default))!.CurrentDraftVersionId!.Value;
        await Agents.UpdateDepartmentAgentVersionAsync(fx.TenantB, fx.X, agent.Id, v1, fx.AdminB,
            new UpdateAgentVersionRequest(null, null, null, null, null, null, null, null, null, null, null, null, null, [fx.VoucherCapability]), default);
        Assert.NotNull(await Agents.PublishDepartmentAgentVersionAsync(fx.TenantB, fx.X, agent.Id, v1, fx.AdminB, default));

        var resolution = await Docs.ResolveForUploaderAsync(fx.TenantB, fx.X, fx.AdminB, null, default);
        Assert.Equal(agent.Id, resolution.AgentId);
        Assert.Equal(v1, resolution.AgentVersionId);
        Assert.Null(resolution.Config);
        Assert.Equal(DocumentConfigSources.Legacy, resolution.ConfigSource);
        var manifest = (await Agents.GetDepartmentAgentManifestAsync(fx.AdminB, fx.TenantB, fx.X, agent.Id, true, default)).Manifest!;
        Assert.Null(manifest.DocumentProcessing);
    }

    // ====================================================== TRACEABILITY

    [Fact]
    public async Task Voucher_provenance_and_append_only_run_audit()
    {
        if (fx.ConnectionString is null) return;
        var (agentId, v1) = await NewKtAgentAsync();
        Assert.Empty((await Docs.ReplaceDepartmentDraftConfigAsync(fx.TenantA, fx.Kt, agentId, v1, fx.AdminKt, AllSystemTypes(), default)).Errors);
        Assert.NotNull(await Agents.PublishDepartmentAgentVersionAsync(fx.TenantA, fx.Kt, agentId, v1, fx.AdminKt, default));
        await using var conn = await OpenAsync();
        var voucherId = await conn.ExecuteScalarAsync<Guid>("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, status) VALUES (@u, @t, @d, 'receipt', 'ocr_complete') RETURNING id
            """, new { u = fx.AdminKt, t = fx.TenantA, d = fx.Kt });

        await Docs.SetRequestedAgentAsync(fx.TenantA, voucherId, agentId, default);
        Assert.Equal(agentId, await Docs.GetRequestedAgentAsync(voucherId, default));
        await Docs.SetVoucherProvenanceAsync(fx.TenantA, voucherId, agentId, v1, DocumentConfigSources.Manifest, default);
        var runId = await Docs.RecordRunAsync(new DocumentProcessingRun("production", voucherId, fx.TenantA, fx.Kt, fx.AdminKt,
            agentId, v1, 1, DocumentConfigSources.Manifest, "receipt", "abc", null, null, [new { code = "x" }], "ollama", "qwen3",
            "auto_detected", "ok", 0.95m, true, [], false), default);

        var row = await conn.QuerySingleAsync<(Guid? A, Guid? V, string S)>(
            "SELECT processed_by_agent_id, processed_by_agent_version_id, document_config_source FROM vouchers WHERE id=@voucherId", new { voucherId });
        Assert.Equal((agentId, v1, "manifest"), (row.A, row.V, row.S));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT agent_version FROM document_processing_runs WHERE id=@runId", new { runId }));

        var ex = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync("UPDATE document_processing_runs SET model='forged' WHERE id=@runId", new { runId }));
        Assert.Equal("23514", ex.SqlState);
        await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync("DELETE FROM document_processing_runs WHERE id=@runId", new { runId }));
        // Test-mode runs can't claim a voucher; production runs must have one.
        await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(
            "INSERT INTO document_processing_runs(mode, voucher_id, tenant_id, config_source) VALUES ('test', @voucherId, @t, 'manifest')", new { voucherId, t = fx.TenantA }));
        await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(
            "INSERT INTO document_processing_runs(mode, tenant_id, config_source) VALUES ('production', @t, 'manifest')", new { t = fx.TenantA }));
        // Deleting the voucher cascades its run history.
        await conn.ExecuteAsync("DELETE FROM vouchers WHERE id=@voucherId", new { voucherId });
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_processing_runs WHERE id=@runId", new { runId }));
    }
}
