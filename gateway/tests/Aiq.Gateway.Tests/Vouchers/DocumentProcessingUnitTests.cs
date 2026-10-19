using System.Text.Json;
using Aiq.Gateway.Books;
using Aiq.Gateway.Endpoints;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;
using Aiq.Gateway.Vouchers;
using Aiq.Gateway.Vouchers.DocumentProcessing;
using Microsoft.AspNetCore.Http;

namespace Aiq.Gateway.Tests.Vouchers;

/// <summary>
/// Configurable document processing — the pure core (no database): draft
/// validation, JSON Schema derivation, extracted-output validation, the
/// classification policy, the manifest-driven prompt and Agent Builder test
/// mode's no-mutation guarantee.
/// </summary>
public sealed class DocumentProcessingUnitTests
{
    private static DocumentTypeConfigInput Input(string code, bool enabled = true, string? handler = null,
        IReadOnlyList<DocumentFieldInput>? fields = null, Guid? workflowId = null, string[]? skills = null) =>
        new(code, enabled, null,
            fields ?? [new DocumentFieldInput("document_number", "Số hóa đơn", "string", true, null),
                       new DocumentFieldInput("total_amount", "Tổng", "decimal", true, null)],
            "instr", workflowId, skills, handler);

    // ---------------------------------------------------------------- validation

    [Fact]
    public void Valid_config_normalizes_with_fixed_handler()
    {
        var (errors, records) = DocumentProcessingValidator.Normalize([Input("invoice_vat_out"), Input("receipt")]);
        Assert.Empty(errors);
        Assert.Equal(AccountingHandlers.SalesInvoice, records[0].AccountingHandler);
        Assert.Equal(AccountingHandlers.CashReceipt, records[1].AccountingHandler);
        Assert.Equal("Hóa đơn bán", records[0].DisplayName);
    }

    [Theory]
    [InlineData("money_mover", "unknown_document_type:money_mover")]
    [InlineData("", "unknown_document_type:?")]
    public void Unknown_document_type_blocked(string code, string expected)
    {
        var (errors, records) = DocumentProcessingValidator.Normalize([Input(code)]);
        Assert.Contains(expected, errors);
        Assert.Empty(records);
    }

    [Fact]
    public void Duplicate_document_type_blocked()
    {
        var (errors, _) = DocumentProcessingValidator.Normalize([Input("receipt"), Input("receipt")]);
        Assert.Contains("duplicate_document_type:receipt", errors);
    }

    [Fact]
    public void Accounting_handler_cannot_be_reassigned_by_configuration()
    {
        // "invoice_vat_out → workflow that posts Dr/Cr arbitrarily" must be impossible.
        var (errors, _) = DocumentProcessingValidator.Normalize([Input("invoice_vat_out", handler: "post_arbitrary_journal")]);
        Assert.Contains("accounting_handler_not_configurable:invoice_vat_out", errors);
        var (sameErrors, _) = DocumentProcessingValidator.Normalize([Input("invoice_vat_out", handler: AccountingHandlers.SalesInvoice)]);
        Assert.Empty(sameErrors);
    }

    [Fact]
    public void Every_system_type_maps_to_a_registered_handler()
    {
        foreach (var t in DocumentTypeCatalog.All) Assert.Contains(t.AccountingHandler, AccountingHandlers.Registered);
        Assert.Equal(8, DocumentTypeCatalog.All.Count);
    }

    [Theory]
    [InlineData("Bad Name", "decimal", "invalid_field_name:receipt:Bad Name")]
    [InlineData("amount", "money", "unsupported_field_type:receipt:amount:money")]
    [InlineData("amount", "", "unsupported_field_type:receipt:amount:?")]
    [InlineData("voucher_type", "string", "reserved_field_name:receipt:voucher_type")]
    [InlineData("document_processing", "object", "reserved_field_name:receipt:document_processing")]
    public void Invalid_schema_blocked(string name, string type, string expected)
    {
        var (errors, _) = DocumentProcessingValidator.Normalize([Input("receipt", fields: [new DocumentFieldInput(name, null, type, false, null)])]);
        Assert.Contains(expected, errors);
    }

    [Fact]
    public void Duplicate_field_and_empty_schema_blocked()
    {
        var (dup, _) = DocumentProcessingValidator.Normalize([Input("receipt", fields:
            [new DocumentFieldInput("amount", null, "decimal", true, null), new DocumentFieldInput("amount", null, "string", false, null)])]);
        Assert.Contains("duplicate_field:receipt:amount", dup);
        var (empty, _) = DocumentProcessingValidator.Normalize([Input("receipt", fields: [])]);
        Assert.Contains("schema_has_no_fields:receipt", empty);
    }

    [Fact]
    public void All_disabled_is_rejected_and_null_input_is_rejected()
    {
        Assert.Contains("no_enabled_document_types", DocumentProcessingValidator.Normalize([Input("receipt", enabled: false)]).Errors);
        Assert.Contains("document_types_required", DocumentProcessingValidator.Normalize(null).Errors);
    }

    [Fact]
    public void Number_alias_maps_to_decimal()
    {
        var (errors, records) = DocumentProcessingValidator.Normalize([Input("receipt", fields: [new DocumentFieldInput("amount", "Số tiền", "number", true, null)])]);
        Assert.Empty(errors);
        Assert.Equal(DocumentFieldTypes.Decimal, records[0].Fields[0].Type);
    }

    // ------------------------------------------------------------------ schema

    [Fact]
    public void Json_schema_is_deterministic_and_lists_required()
    {
        var fields = DocumentTypeCatalog.Find("invoice_vat_out")!.DefaultFields;
        var a = DocumentSchema.Build("invoice_vat_out", fields);
        var b = DocumentSchema.Build("invoice_vat_out", fields);
        Assert.Equal(DocumentSchema.Sha256(a), DocumentSchema.Sha256(b));
        Assert.Equal("object", a.GetProperty("type").GetString());
        var required = a.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["document_number", "total_amount"], required);
        Assert.Equal("number", a.GetProperty("properties").GetProperty("total_amount").GetProperty("type").GetString());
        Assert.Equal("date", a.GetProperty("properties").GetProperty("document_date").GetProperty("format").GetString());
    }

    [Fact]
    public void Required_field_validation()
    {
        var fields = DocumentTypeCatalog.Find("invoice_vat_out")!.DefaultFields;
        var ok = DocumentSchema.Validate(fields, new Dictionary<string, object?>
        {
            ["document_number"] = "0000123", ["total_amount"] = 1_100_000L, ["document_date"] = "2026-09-01",
            ["vat_rate"] = "10%", ["line_items"] = new List<object?>(),
        });
        Assert.True(ok.Valid);

        var missing = DocumentSchema.Validate(fields, new Dictionary<string, object?> { ["document_number"] = "  ", ["total_amount"] = null });
        Assert.False(missing.Valid);
        Assert.Equal(["document_number", "total_amount"], missing.MissingRequired);
    }

    [Fact]
    public void Type_validation_rejects_wrong_shapes()
    {
        var fields = DocumentTypeCatalog.Find("invoice_vat_out")!.DefaultFields;
        var bad = DocumentSchema.Validate(fields, new Dictionary<string, object?>
        {
            ["document_number"] = 123L,            // string field returned as a number
            ["total_amount"] = "một triệu",        // unparseable decimal
            ["document_date"] = "01/09/2026",      // not ISO
            ["line_items"] = "x",                  // not an array
        });
        Assert.False(bad.Valid);
        Assert.Contains("document_number:string", bad.TypeErrors);
        Assert.Contains("total_amount:decimal", bad.TypeErrors);
        Assert.Contains("document_date:date", bad.TypeErrors);
        Assert.Contains("line_items:array", bad.TypeErrors);
    }

    // ------------------------------------------------------------------ policy

    private static ManifestDocumentProcessing Config(params (string Code, bool Enabled)[] types) =>
        new(Guid.NewGuid(), false, types.Select(t =>
        {
            var system = DocumentTypeCatalog.Find(t.Code)!;
            var schema = DocumentSchema.Build(t.Code, system.DefaultFields);
            return new ManifestDocumentType(t.Code, system.DisplayName, t.Enabled, system.AccountingHandler, system.DefaultFields,
                schema, DocumentSchema.Sha256(schema), system.DefaultInstructions, null, []);
        }).ToList());

    private static VoucherTypeClassification Classified(string type, string status = VoucherTypeStatuses.Ok, Guid? confirmedBy = null) =>
        new() { VoucherType = type, Confidence = 0.95m, Status = status, RequiresConfirmation = status != VoucherTypeStatuses.Ok, ConfirmedBy = confirmedBy };

    [Fact]
    public void Disabled_type_is_unsupported_and_blocks_posting()
    {
        var config = Config(("invoice_vat_out", true), ("payment", false));
        var outcome = DocumentExtraction.ApplyPolicy(config, Classified("payment"), new Dictionary<string, object?>(), "Trợ lý KT v2");
        Assert.True(outcome.Unsupported);
        Assert.Equal(VoucherTypeStatuses.NeedsConfirmation, outcome.Classification.Status);
        Assert.True(outcome.Classification.BlocksPosting);
        Assert.Contains(outcome.Classification.Evidence, e => e.Contains("không hỗ trợ"));
    }

    [Fact]
    public void Type_absent_from_config_is_unsupported()
    {
        var outcome = DocumentExtraction.ApplyPolicy(Config(("receipt", true)), Classified("bank_statement"), new Dictionary<string, object?>(), "x");
        Assert.True(outcome.Unsupported);
    }

    [Fact]
    public void Supported_valid_extraction_is_unchanged()
    {
        var c = Classified("receipt");
        var outcome = DocumentExtraction.ApplyPolicy(Config(("receipt", true)), c, new Dictionary<string, object?> { ["amount"] = 5_000_000L }, "x");
        Assert.False(outcome.Unsupported);
        Assert.True(outcome.Schema!.Valid);
        Assert.Same(c, outcome.Classification);
    }

    [Fact]
    public void Missing_required_field_requires_new_confirmation()
    {
        var previouslyConfirmed = Classified("receipt", confirmedBy: Guid.NewGuid());
        var outcome = DocumentExtraction.ApplyPolicy(Config(("receipt", true)), previouslyConfirmed, new Dictionary<string, object?>(), "x");
        Assert.False(outcome.Schema!.Valid);
        Assert.True(outcome.Classification.BlocksPosting);
        Assert.Null(outcome.Classification.ConfirmedBy);
    }

    [Fact]
    public void Policy_never_weakens_conflict_or_hint_mismatch()
    {
        var conflict = Classified("payment", VoucherTypeStatuses.Conflict);
        var outcome = DocumentExtraction.ApplyPolicy(Config(("receipt", true)), conflict, new Dictionary<string, object?>(), "x");
        Assert.Equal(VoucherTypeStatuses.Conflict, outcome.Classification.Status);
        Assert.True(outcome.Classification.RequiresConfirmation);
    }

    [Fact]
    public void Legacy_invoice_supported_when_either_direction_enabled()
    {
        Assert.True(Config(("invoice_vat_in", true)).Supports(VoucherTypes.LegacyInvoice));
        Assert.False(Config(("receipt", true)).Supports(VoucherTypes.LegacyInvoice));
    }

    [Fact]
    public void Workflow_require_review_is_reported_for_the_persisted_review_gate()
    {
        var baseConfig = Config(("receipt", true));
        var wf = new ManifestDocumentWorkflow(Guid.NewGuid(), Guid.NewGuid(), "Duyệt phiếu thu",
            JsonSerializer.SerializeToElement(new { steps = new object[] { new { type = "prompt", prompt = "Kiểm tra người nộp" }, new { type = "require_review" } } }));
        var config = baseConfig with { Types = [baseConfig.Types[0] with { Workflow = wf }] };
        var c = Classified("receipt");
        var outcome = DocumentExtraction.ApplyPolicy(config, c, new Dictionary<string, object?> { ["amount"] = 1L }, "x");
        // The gate is the persisted review task + vouchers.document_review_status
        // (an ADMIN type confirmation must not be able to clear it).
        Assert.True(outcome.WorkflowReview);
        Assert.Same(c, outcome.Classification);
        Assert.Contains("Kiểm tra người nộp", DocumentExtraction.BuildManifestPrompt(config, "receipt", false, "PHIẾU THU"));
    }

    // ------------------------------------------------------------------ prompt

    [Fact]
    public void Prompt_uses_configured_schema_and_lists_only_enabled_types()
    {
        var config = Config(("invoice_vat_out", true), ("payment", false));
        var t = config.Types[0];
        var custom = config with
        {
            Types = [t with { Fields = [.. t.Fields, new DocumentFieldDefinition("project_code", "Mã dự án", "string", false, "Mã công trình in góc phải")] }, config.Types[1]],
        };
        var prompt = DocumentExtraction.BuildManifestPrompt(custom, "invoice_vat_out", false, "HÓA ĐƠN GTGT");
        Assert.Contains("project_code (string): Mã dự án — Mã công trình in góc phải", prompt);
        Assert.Contains("### invoice_vat_out", prompt);
        Assert.DoesNotContain("### payment", prompt);
        Assert.Contains("Trợ lý này chỉ xử lý các loại: invoice_vat_out.", prompt);
    }

    [Fact]
    public void Prompt_inlines_declarative_skill_snapshot()
    {
        var config = Config(("receipt", true));
        var withSkill = config with { Types = [config.Types[0] with { Skills = [new ManifestDocumentSkill("pt-guide", "declarative", "ab", "QUY TẮC PHIẾU THU v7")] }] };
        Assert.Contains("QUY TẮC PHIẾU THU v7", DocumentExtraction.BuildManifestPrompt(withSkill, null, false, "x"));
    }

    // --------------------------------------------------------------- test mode

    private sealed class RecordingDocs(ManifestDocumentProcessing config) : IDocumentProcessingRepository
    {
        public List<DocumentProcessingRun> Runs { get; } = [];
        public int ForbiddenWrites { get; private set; }
        public Task<DocumentTestTarget?> GetDepartmentTestTargetAsync(Guid t, Guid d, Guid a, CancellationToken ct) =>
            Task.FromResult<DocumentTestTarget?>(new(a, config.AgentVersionId, 3, "draft", "Trợ lý kế toán", config));
        public Task<Guid> RecordRunAsync(DocumentProcessingRun run, CancellationToken ct) { Runs.Add(run); return Task.FromResult(Guid.NewGuid()); }
        public Task<string?> GetDepartmentVoucherRawTextAsync(Guid t, Guid d, Guid v, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task SetVoucherProvenanceAsync(Guid t, Guid v, Guid? a, Guid? av, string s, CancellationToken ct) { ForbiddenWrites++; return Task.CompletedTask; }
        public Task SetRequestedAgentAsync(Guid t, Guid v, Guid a, CancellationToken ct) { ForbiddenWrites++; return Task.CompletedTask; }
        public Task<DepartmentDocumentConfig?> GetDepartmentVersionConfigAsync(Guid t, Guid d, Guid a, Guid v, CancellationToken ct) => throw new NotSupportedException();
        public Task<DocumentConfigSaveResult> ReplaceDepartmentDraftConfigAsync(Guid t, Guid d, Guid a, Guid v, Guid actor, IReadOnlyList<DocumentTypeConfigInput>? types, CancellationToken ct) { ForbiddenWrites++; throw new NotSupportedException(); }
        public Task<DocumentAgentResolution> ResolveForUploaderAsync(Guid t, Guid d, Guid u, Guid? r, CancellationToken ct) => throw new NotSupportedException();
        public Task<Guid?> GetRequestedAgentAsync(Guid v, CancellationToken ct) => throw new NotSupportedException();
        public Task<DocumentProvenance> GetProvenanceAsync(Guid t, Guid v, CancellationToken ct) => throw new NotSupportedException();
        public Task<DocumentAgentResolution> ResolvePinnedAsync(Guid t, Guid d, Guid? a, Guid? av, CancellationToken ct) => throw new NotSupportedException();
        public Task SetExtractionFailureAsync(Guid t, Guid v, string c, string st, CancellationToken ct) { ForbiddenWrites++; return Task.CompletedTask; }
        public Task<ExtractionFailureState?> GetExtractionFailureAsync(Guid t, Guid v, CancellationToken ct) => throw new NotSupportedException();
        public Task SetProcessingBlockerAsync(Guid t, Guid v, string b, bool active, CancellationToken ct) { ForbiddenWrites++; return Task.CompletedTask; }
        public Task<bool> IsActiveTenantUserAsync(Guid t, Guid u, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeAgent(string answer, Guid versionId) : IAgentClient
    {
        public (Guid Agent, Guid Version, bool Test)? Call { get; private set; }
        public Task<AgentTaskResult> RunDocumentExtractionAsync(Guid agentId, Guid expectedVersionId, bool test, string task, string userToken, CancellationToken ct, bool pinVersion = false)
        {
            Call = (agentId, expectedVersionId, test);
            return Task.FromResult(new AgentTaskResult(answer, agentId, versionId, 3, "claude_sdk", "sonnet"));
        }
        public Task<AgentOcrResponse> RunOcrPipelineAsync(Stream s, string f, CancellationToken ct) => throw new NotSupportedException();
        public Task<AgentOcrResponse> RunOcrImageAsync(Stream s, string f, string c, CancellationToken ct) => throw new NotSupportedException();
        public Task<AgentDocumentExtractResponse> ExtractDocumentTextAsync(Stream s, string f, string c, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> RunAgentTaskAsync(string task, string token, string capability, int maxTurns, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class NoProfile : ITenantCompanyProfileRepository
    {
        public Task<CompanyProfileRecord?> GetAsync(Guid tenantId, CancellationToken ct) => Task.FromResult<CompanyProfileRecord?>(null);
        public Task<CompanyProfileRecord> UpsertAsync(Guid t, Guid a, UpsertCompanyProfileRequest r, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Builder_test_mode_extracts_and_performs_no_accounting_mutation()
    {
        var config = Config(("receipt", true), ("payment", true));
        var docs = new RecordingDocs(config);
        var agentId = Guid.NewGuid();
        var agent = new FakeAgent("""
            ```json
            {"voucher_type":"receipt","document_number":"PT-01","amount":20000000,"payer_name":"Việt Tín"}
            ```
            ```json
            [{"entry_no":1,"debit_account":"111","credit_account":"131","amount":20000000}]
            ```
            """, config.AgentVersionId);
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = "Bearer admin.jwt";
        ctx.Items[AuthenticatedPrincipalContext.ItemKey] = new PrincipalContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "admin", true, true, true);

        var result = await DepartmentAgentEndpoints.TestDocumentProcessingAsync(agentId,
            new DocumentProcessingTestRequest("PHIẾU THU\nSố: PT-01\nLý do thu: thanh toán\nNgười nộp tiền: Việt Tín", null, null),
            docs, new NoProfile(), agent, ctx, CancellationToken.None);

        var body = JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value);
        Assert.Equal("test", body.GetProperty("mode").GetString());
        Assert.Equal("receipt", body.GetProperty("detectedType").GetString());
        Assert.True(body.GetProperty("schemaValid").GetBoolean());
        Assert.Equal(0, body.GetProperty("accountingMutations").GetInt32());
        Assert.Equal(20000000, body.GetProperty("extractedFields").GetProperty("amount").GetInt64());
        Assert.Equal((agentId, config.AgentVersionId, true), agent.Call);
        Assert.Equal(0, docs.ForbiddenWrites);
        var run = Assert.Single(docs.Runs);
        Assert.Equal("test", run.Mode);
        Assert.Null(run.VoucherId);
    }

    [Fact]
    public async Task Builder_test_mode_requires_department_scope()
    {
        var docs = new RecordingDocs(Config(("receipt", true)));
        var ctx = new DefaultHttpContext();   // no authenticated department principal
        var result = await DepartmentAgentEndpoints.TestDocumentProcessingAsync(Guid.NewGuid(),
            new DocumentProcessingTestRequest("x", null, null), docs, new NoProfile(), new FakeAgent("", Guid.NewGuid()), ctx, CancellationToken.None);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>(result);
        Assert.Empty(docs.Runs);
    }

    [Theory]
    [InlineData("user", false)]
    [InlineData("tenant_owner", false)]
    [InlineData("super_admin", false)]
    [InlineData("admin", true)]
    public void Only_department_admin_may_edit_or_publish(string role, bool allowed)
    {
        var principal = new PrincipalContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), role, true, true, true);
        Assert.Equal(allowed, PrincipalAuthorizationPolicy.IsDepartmentAdmin(principal));
    }
}
