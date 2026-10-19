using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Aiq.Gateway.Vouchers.DocumentProcessing;

// ---------------------------------------------------------------------------
// API input (Agent Builder draft editor).
// ---------------------------------------------------------------------------

public sealed record DocumentFieldInput(string? Name, string? Label, string? Type, bool Required, string? Description);

/// <summary>
/// One document type on a draft version. <c>AccountingHandler</c> exists only
/// so a client that tries to set one gets an explicit
/// <c>accounting_handler_not_configurable</c> error instead of a silent
/// ignore — the handler is always taken from <see cref="DocumentTypeCatalog"/>.
/// </summary>
public sealed record DocumentTypeConfigInput(
    string? Code, bool Enabled, string? DisplayName, IReadOnlyList<DocumentFieldInput>? Fields,
    string? ExtractionInstructions, Guid? WorkflowId, string[]? SkillCodes, string? AccountingHandler);

public sealed record ReplaceDocumentProcessingRequest(IReadOnlyList<DocumentTypeConfigInput>? Types);

public sealed record DocumentProcessingTestRequest(string? RawText, Guid? VoucherId, string? VoucherTypeHint);

// ---------------------------------------------------------------------------
// Stored configuration (agent_version_document_types).
// ---------------------------------------------------------------------------

public sealed record DocumentTypeConfigRecord(
    string Code, bool Enabled, string DisplayName, IReadOnlyList<DocumentFieldDefinition> Fields,
    string ExtractionInstructions, Guid? WorkflowId, Guid? WorkflowVersionId, string[] SkillCodes,
    int SortOrder, string AccountingHandler);

// ---------------------------------------------------------------------------
// Manifest shape (AgentExecutionManifest.DocumentProcessing). Mirrors
// agent/src/aiq_agent/runtime/manifest.py ManifestDocumentProcessing.
// ---------------------------------------------------------------------------

public sealed record ManifestDocumentWorkflow(Guid Id, Guid? VersionId, string Name, JsonElement Definition);

public sealed record ManifestDocumentSkill(string Code, string SourceKind, string? ContentSha256, string? Content);

public sealed record ManifestDocumentType(
    string Code, string DisplayName, bool Enabled, string AccountingHandler,
    IReadOnlyList<DocumentFieldDefinition> Fields, JsonElement Schema, string SchemaSha256,
    string ExtractionInstructions, ManifestDocumentWorkflow? Workflow, IReadOnlyList<ManifestDocumentSkill> Skills)
{
    /// <summary>Workflow asks for a human gate before anything downstream.</summary>
    [JsonIgnore] public bool WorkflowRequiresReview => DocumentWorkflow.RequiresReview(Workflow);
}

/// <summary>
/// The published (or, for Agent Builder test mode only, draft) document
/// configuration of ONE agent version. <c>Draft</c> is true only on test
/// manifests; production runtime never sees a draft.
/// </summary>
public sealed record ManifestDocumentProcessing(Guid AgentVersionId, bool Draft, IReadOnlyList<ManifestDocumentType> Types)
{
    public ManifestDocumentType? Find(string? code) =>
        code is null ? null : Types.FirstOrDefault(t => string.Equals(t.Code, code, StringComparison.Ordinal));

    /// <summary>
    /// Whether the version accepts this final type. The legacy bare
    /// "invoice_vat" is accepted when either invoice direction is enabled.
    /// </summary>
    public bool Supports(string? code)
    {
        if (string.Equals(code, VoucherTypes.LegacyInvoice, StringComparison.Ordinal))
            return Find(VoucherTypes.InvoiceOut)?.Enabled == true || Find(VoucherTypes.InvoiceIn)?.Enabled == true;
        return Find(code)?.Enabled == true;
    }

    [JsonIgnore] public IEnumerable<ManifestDocumentType> EnabledTypes => Types.Where(t => t.Enabled);
}

public static class DocumentWorkflow
{
    /// <summary>
    /// A document workflow can only add guidance (prompt steps) or make the
    /// pipeline stricter (require_review). It has no step that can post,
    /// verify, approve, run SQL or skip a gate.
    /// </summary>
    public static bool RequiresReview(ManifestDocumentWorkflow? workflow) =>
        workflow is not null && Steps(workflow.Definition).Any(s => s.Type == "require_review");

    public static IEnumerable<string> PromptLines(ManifestDocumentWorkflow? workflow) =>
        workflow is null ? [] : Steps(workflow.Definition)
            .Where(s => s.Type == "prompt" && !string.IsNullOrWhiteSpace(s.Text)).Select(s => s.Text!.Trim());

    public static IEnumerable<(string Type, string? Text)> Steps(JsonElement definition)
    {
        if (definition.ValueKind != JsonValueKind.Object
            || !definition.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array) yield break;
        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object || !step.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String) continue;
            // Canonical "prompt", legacy "text" (rows written by the older API).
            var text = Aiq.Gateway.Governance.WorkflowDefinitions.PromptText(step);
            yield return (type.GetString()!, text);
        }
    }
}

// ---------------------------------------------------------------------------
// Shape validation + normalisation of draft input (no database needed).
// ---------------------------------------------------------------------------

public static class DocumentProcessingValidator
{
    public const int MaxTypes = 32;
    public const int MaxFields = 60;
    private static readonly Regex FieldNameRx = new("^[a-z][a-z0-9_]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex SkillCodeRx = new("^[a-z0-9][a-z0-9._-]*$", RegexOptions.Compiled);

    // Keys the Gateway pipeline writes itself — a configured schema may not
    // claim them, so extracted output can never spoof classification,
    // provenance or enrichment results.
    private static readonly HashSet<string> ReservedFieldNames = new(StringComparer.Ordinal)
    {
        "voucher_type", "voucher_type_classification", "voucher_type_extracted", "voucher_type_uploaded_as",
        "document_processing", "ar_context", "ap_context", "match_context", "po_match_context",
        "receipt_matching", "payment_matching", "expense_claim_matching", "receipt_match_outcome",
        "expense_claim_outcome", "bank_statement_import",
    };

    /// <summary>
    /// Validates and normalises a full draft configuration. Returns structured
    /// error codes (never free text) and the normalised records when valid.
    /// </summary>
    public static (IReadOnlyList<string> Errors, IReadOnlyList<DocumentTypeConfigRecord> Records) Normalize(
        IReadOnlyList<DocumentTypeConfigInput>? types)
    {
        var errors = new List<string>();
        var records = new List<DocumentTypeConfigRecord>();
        if (types is null) return (["document_types_required"], records);
        if (types.Count > MaxTypes) errors.Add("too_many_document_types");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < types.Count; i++)
        {
            var input = types[i];
            var code = (input.Code ?? string.Empty).Trim();
            var system = DocumentTypeCatalog.Find(code);
            if (system is null) { errors.Add($"unknown_document_type:{(code.Length == 0 ? "?" : code)}"); continue; }
            if (!seen.Add(code)) { errors.Add($"duplicate_document_type:{code}"); continue; }
            if (!string.IsNullOrWhiteSpace(input.AccountingHandler)
                && !string.Equals(input.AccountingHandler.Trim(), system.AccountingHandler, StringComparison.Ordinal))
                errors.Add($"accounting_handler_not_configurable:{code}");
            if (!AccountingHandlers.Registered.Contains(system.AccountingHandler))
                errors.Add($"unknown_accounting_handler:{code}");

            var displayName = string.IsNullOrWhiteSpace(input.DisplayName) ? system.DisplayName : input.DisplayName.Trim();
            if (displayName.Length > 160) errors.Add($"display_name_too_long:{code}");
            var instructions = (input.ExtractionInstructions ?? string.Empty).Trim();
            if (instructions.Length > 20000) errors.Add($"instructions_too_long:{code}");

            var fields = new List<DocumentFieldDefinition>();
            var fieldInputs = input.Fields ?? [];
            if (fieldInputs.Count == 0) errors.Add($"schema_has_no_fields:{code}");
            if (fieldInputs.Count > MaxFields) errors.Add($"too_many_fields:{code}");
            var fieldNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in fieldInputs)
            {
                var name = (f.Name ?? string.Empty).Trim();
                var type = (f.Type ?? string.Empty).Trim().ToLowerInvariant();
                if (type == "number") type = DocumentFieldTypes.Decimal;
                if (!FieldNameRx.IsMatch(name)) { errors.Add($"invalid_field_name:{code}:{(name.Length == 0 ? "?" : name)}"); continue; }
                if (ReservedFieldNames.Contains(name)) { errors.Add($"reserved_field_name:{code}:{name}"); continue; }
                if (!fieldNames.Add(name)) { errors.Add($"duplicate_field:{code}:{name}"); continue; }
                if (!DocumentFieldTypes.All.Contains(type)) { errors.Add($"unsupported_field_type:{code}:{name}:{(type.Length == 0 ? "?" : type)}"); continue; }
                var label = string.IsNullOrWhiteSpace(f.Label) ? name : f.Label.Trim();
                var description = string.IsNullOrWhiteSpace(f.Description) ? null : f.Description.Trim();
                if (label.Length > 160 || (description?.Length ?? 0) > 1000) { errors.Add($"field_text_too_long:{code}:{name}"); continue; }
                fields.Add(new DocumentFieldDefinition(name, label, type, f.Required, description));
            }

            var skills = (input.SkillCodes ?? []).Select(s => (s ?? string.Empty).Trim()).Where(s => s.Length > 0)
                .Distinct(StringComparer.Ordinal).ToArray();
            foreach (var skill in skills.Where(s => !SkillCodeRx.IsMatch(s) || s.Length > 120))
                errors.Add($"invalid_skill_code:{code}:{skill}");

            records.Add(new DocumentTypeConfigRecord(code, input.Enabled, displayName, fields, instructions,
                input.WorkflowId, null, skills, i, system.AccountingHandler));
        }

        if (records.Count > 0 && records.All(r => !r.Enabled)) errors.Add("no_enabled_document_types");
        return (errors, errors.Count == 0 ? records : []);
    }

    /// <summary>Defaults for every system type — the "load defaults" starting point of a migration.</summary>
    public static IReadOnlyList<DocumentTypeConfigRecord> SystemDefaults() =>
        DocumentTypeCatalog.All.Select((t, i) => new DocumentTypeConfigRecord(
            t.Code, true, t.DisplayName, t.DefaultFields, t.DefaultInstructions, null, null, [], i, t.AccountingHandler)).ToList();
}

// ---------------------------------------------------------------------------
// JSON Schema derivation + validation of extracted output.
// ---------------------------------------------------------------------------

public sealed record DocumentSchemaValidation(
    bool Valid, IReadOnlyList<string> MissingRequired, IReadOnlyList<string> TypeErrors);

public static class DocumentSchema
{
    private static readonly Regex IsoDateRx = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);

    /// <summary>Deterministic JSON Schema (draft 2020-12) for one type's fields.</summary>
    public static JsonElement Build(string code, IReadOnlyList<DocumentFieldDefinition> fields)
    {
        var properties = new JsonObject();
        foreach (var f in fields)
        {
            var prop = f.Type switch
            {
                DocumentFieldTypes.Decimal => new JsonObject { ["type"] = "number" },
                DocumentFieldTypes.Integer => new JsonObject { ["type"] = "integer" },
                DocumentFieldTypes.Date => new JsonObject { ["type"] = "string", ["format"] = "date" },
                DocumentFieldTypes.Boolean => new JsonObject { ["type"] = "boolean" },
                DocumentFieldTypes.Array => new JsonObject { ["type"] = "array" },
                DocumentFieldTypes.Object => new JsonObject { ["type"] = "object" },
                _ => new JsonObject { ["type"] = "string" },
            };
            prop["title"] = f.Label;
            if (f.Description is not null) prop["description"] = f.Description;
            properties[f.Name] = prop;
        }
        var schema = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = code,
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(fields.Where(f => f.Required).Select(f => (JsonNode)JsonValue.Create(f.Name)!).ToArray()),
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    public static string Sha256(JsonElement schema) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(schema.GetRawText()))).ToLowerInvariant();

    /// <summary>
    /// Validates the extractor's flat fields object against the configured
    /// schema. Passing this proves the SHAPE only — LLM output is never
    /// authoritative because it matches JSON; classification, posting
    /// blockers and ADMIN confirmation still apply.
    /// </summary>
    public static DocumentSchemaValidation Validate(IReadOnlyList<DocumentFieldDefinition> fields, IReadOnlyDictionary<string, object?> output)
    {
        var missing = new List<string>();
        var typeErrors = new List<string>();
        foreach (var f in fields)
        {
            output.TryGetValue(f.Name, out var value);
            if (IsEmpty(value))
            {
                if (f.Required) missing.Add(f.Name);
                continue;
            }
            if (!Conforms(f.Type, value!)) typeErrors.Add($"{f.Name}:{f.Type}");
        }
        return new DocumentSchemaValidation(missing.Count == 0 && typeErrors.Count == 0, missing, typeErrors);
    }

    private static bool IsEmpty(object? value) => value switch
    {
        null => true,
        string s => string.IsNullOrWhiteSpace(s),
        System.Collections.ICollection c => c.Count == 0,
        _ => false,
    };

    private static bool Conforms(string type, object value) => type switch
    {
        DocumentFieldTypes.String => value is string,
        DocumentFieldTypes.Decimal => value is long or int or decimal or double
            || (value is string s && Aiq.Gateway.Jobs.VoucherExtractJobRunner.ParseFlexibleDecimal(s.TrimEnd('%', ' ')) is not null),
        DocumentFieldTypes.Integer => value is long or int
            || (value is decimal d && d == decimal.Truncate(d))
            || (value is string si && long.TryParse(si.Trim(), out _)),
        DocumentFieldTypes.Date => value is string ds && IsoDateRx.IsMatch(ds.Trim()) && DateOnly.TryParse(ds.Trim(), out _),
        DocumentFieldTypes.Boolean => value is bool
            || (value is string b && b.Trim().ToLowerInvariant() is "true" or "false"),
        DocumentFieldTypes.Array => value is System.Collections.IList,
        DocumentFieldTypes.Object => value is IDictionary<string, object?>,
        _ => false,
    };
}
