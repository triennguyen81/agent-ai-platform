using System.Text;
using System.Text.Json;

namespace Aiq.Gateway.Vouchers.DocumentProcessing;

/// <summary>Result of applying a published document configuration to one classified extraction.</summary>
public sealed record DocumentPolicyOutcome(
    VoucherTypeClassification Classification,
    bool Unsupported,
    ManifestDocumentType? TypeConfig,
    DocumentSchemaValidation? Schema,
    bool WorkflowReview);

/// <summary>
/// The document-processing core shared by the production extract job and
/// Agent Builder test mode. Pure functions only — no repository, no voucher
/// writes, no accounting effect — so test mode is structurally unable to
/// mutate anything.
/// </summary>
public static class DocumentExtraction
{
    private const int MaxRawTextChars = 20_000;

    /// <summary>
    /// Extraction prompt built from a published (or test-draft) Agent version
    /// configuration. Classification stays open over ALL system codes so the
    /// model's answer is independent corroboration of the deterministic
    /// evidence; the configuration decides which types this Agent then
    /// PROCESSES and with which schema. BLOCK 2 (journal proposal) is fixed
    /// server text — suggestions only, never configurable.
    /// </summary>
    public static string BuildManifestPrompt(ManifestDocumentProcessing config, string? candidateType, bool forced, string rawText)
    {
        var truncated = rawText.Length > MaxRawTextChars ? rawText[..MaxRawTextChars] : rawText;
        var allCodes = string.Join(", ", DocumentTypeCatalog.All.Select(t => t.Code));
        var enabled = config.EnabledTypes.ToList();
        var sb = new StringBuilder();
        sb.AppendLine("Bạn là chuyên viên kế toán VN. Xử lý raw OCR text của 1 chứng từ bên dưới.");
        sb.AppendLine(forced
            ? $"Loại chứng từ đã được ADMIN xác nhận: \"{candidateType}\" — dùng đúng loại này."
            : $"TỰ XÁC ĐỊNH voucher_type từ nội dung (tiêu đề, các dòng thông tin) trong: {allCodes}. Gợi ý ban đầu: \"{candidateType ?? "chưa xác định"}\" — có thể SAI, không được lặp lại gợi ý nếu nội dung cho thấy loại khác.");
        sb.AppendLine($"Trợ lý này chỉ xử lý các loại: {string.Join(", ", enabled.Select(t => t.Code))}. Nếu chứng từ thuộc loại khác, vẫn trả đúng voucher_type đó kèm document_number/document_date (nếu có), không bịa thêm trường.");
        sb.AppendLine();
        sb.AppendLine("**BLOCK 1** — Trích xuất fields theo đúng SCHEMA của voucher_type đã xác định (cấu hình đã publish của trợ lý).");
        sb.AppendLine("Trả về MỘT JSON object PHẲNG: bắt buộc có voucher_type, các trường đúng tên như schema; trường không có trên chứng từ thì bỏ qua hoặc để null. date = YYYY-MM-DD; decimal/integer = số, không kèm đơn vị.");
        foreach (var type in enabled)
        {
            sb.AppendLine();
            sb.AppendLine($"### {type.Code} — {type.DisplayName}{(string.Equals(type.Code, candidateType, StringComparison.Ordinal) ? " (gợi ý)" : "")}");
            foreach (var f in type.Fields)
            {
                sb.Append($"- {f.Name} ({f.Type}{(f.Required ? ", BẮT BUỘC" : "")}): {f.Label}");
                if (!string.IsNullOrWhiteSpace(f.Description)) sb.Append($" — {f.Description}");
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(type.ExtractionInstructions))
                sb.AppendLine($"Hướng dẫn: {type.ExtractionInstructions}");
            foreach (var line in DocumentWorkflow.PromptLines(type.Workflow))
                sb.AppendLine($"Quy trình: {line}");
            foreach (var skill in type.Skills)
            {
                if (skill.SourceKind == "declarative" && skill.Content is not null)
                    sb.AppendLine($"===== SKILL {skill.Code} =====\n{skill.Content}\n===== END SKILL =====");
                else
                    sb.AppendLine($"Áp dụng skill `{skill.Code}`.");
            }
        }
        sb.AppendLine();
        sb.AppendLine("**BLOCK 2** — Áp dụng skill `journal-entry-formatter` + `accounting-vietnam` để đề xuất bút toán N/C theo TT200");
        sb.AppendLine("phù hợp với voucher_type đã xác định ở BLOCK 1 (không mặc định là hoá đơn mua/bán nếu chứng từ không phải hoá đơn).");
        sb.AppendLine("Trả về JSON array các entries. Mỗi entry: {entry_no, debit_account, credit_account, amount, description, confidence}.");
        sb.AppendLine("Đây chỉ là ĐỀ XUẤT; hệ thống tự quyết định ghi nhận. Hợp đồng/sao kê → [].");
        sb.AppendLine();
        sb.AppendLine("**QUAN TRỌNG**: chỉ trả 2 code block JSON, KHÔNG kèm markdown khác, KHÔNG gọi tool:");
        sb.AppendLine("```json\n{ ...fields object... }\n```\n```json\n[ ...entries array... ]\n```");
        sb.AppendLine();
        sb.AppendLine("===== RAW OCR TEXT =====");
        sb.AppendLine(truncated);
        sb.AppendLine("===== END =====");
        return sb.ToString();
    }

    /// <summary>
    /// Applies the configuration AFTER the canonical classifier decided the
    /// final type. Can only add blockers (never removes conflict / mismatch /
    /// low-confidence gates): an unsupported type, a schema violation or a
    /// workflow require_review step each force human confirmation.
    /// </summary>
    public static DocumentPolicyOutcome ApplyPolicy(
        ManifestDocumentProcessing config, VoucherTypeClassification classification,
        IReadOnlyDictionary<string, object?> fields, string agentLabel)
    {
        var finalType = classification.VoucherType;
        if (string.Equals(finalType, VoucherTypes.Unclassified, StringComparison.Ordinal))
            return new DocumentPolicyOutcome(classification, false, null, null, false);

        var evidence = new List<string>();
        var unsupported = !config.Supports(finalType);
        var typeConfig = config.Find(finalType);
        DocumentSchemaValidation? schema = null;
        var review = false;
        if (unsupported)
        {
            evidence.Add($"⚠ {agentLabel} không hỗ trợ loại chứng từ {VoucherTypes.Label(finalType)} ({finalType}) — không xử lý nghiệp vụ, cần ADMIN chọn loại khác hoặc bật loại này trong cấu hình");
        }
        else if (typeConfig is not null)
        {
            schema = DocumentSchema.Validate(typeConfig.Fields, fields);
            if (schema.MissingRequired.Count > 0)
                evidence.Add($"⚠ Thiếu trường bắt buộc theo cấu hình ({typeConfig.Code}): {string.Join(", ", schema.MissingRequired)}");
            if (schema.TypeErrors.Count > 0)
                evidence.Add($"⚠ Sai kiểu dữ liệu theo cấu hình ({typeConfig.Code}): {string.Join(", ", schema.TypeErrors)}");
            // require_review is enforced by a persisted, department-scoped
            // review task + vouchers.document_review_status (see
            // DocumentReviewRepository) — not by the type-confirmation flag,
            // which an ADMIN type confirmation would otherwise clear.
            review = typeConfig.WorkflowRequiresReview;
        }

        if (evidence.Count == 0)
            return new DocumentPolicyOutcome(classification, false, typeConfig, schema, review);

        var blocked = classification with
        {
            Status = classification.Status is VoucherTypeStatuses.Conflict or VoucherTypeStatuses.HintMismatch
                ? classification.Status : VoucherTypeStatuses.NeedsConfirmation,
            RequiresConfirmation = true,
            // A new blocker needs a NEW human look — an earlier confirmation
            // of the type does not cover a schema/workflow/support problem.
            ConfirmedBy = null,
            ConfirmedAt = null,
            Evidence = classification.Evidence.Concat(evidence).ToList(),
        };
        return new DocumentPolicyOutcome(blocked, unsupported, typeConfig, schema, review);
    }

    /// <summary>
    /// fields["document_processing"]: the per-document provenance summary
    /// persisted with the extraction (references and hashes, never the full
    /// manifest).
    /// </summary>
    public static Dictionary<string, object?> ProvenanceBlock(
        DocumentAgentResolution resolution, AgentTaskResult? executed, DocumentPolicyOutcome? policy, DateTime extractedAt)
    {
        var type = policy?.TypeConfig;
        return new Dictionary<string, object?>
        {
            ["config_source"] = resolution.ConfigSource,
            ["agent_id"] = (executed?.AgentId ?? resolution.AgentId)?.ToString(),
            ["agent_name"] = resolution.AgentName,
            ["agent_version_id"] = (executed?.AgentVersionId ?? resolution.AgentVersionId)?.ToString(),
            ["agent_version"] = executed?.AgentVersion ?? resolution.Version,
            ["provider"] = executed?.Provider,
            ["model"] = executed?.Model,
            ["document_type"] = policy?.Classification.VoucherType,
            ["unsupported_document_type"] = policy?.Unsupported ?? false,
            ["schema_sha256"] = type?.SchemaSha256,
            ["schema_valid"] = policy?.Schema?.Valid,
            ["missing_required"] = policy?.Schema?.MissingRequired.ToList(),
            ["type_errors"] = policy?.Schema?.TypeErrors.ToList(),
            ["workflow_id"] = type?.Workflow?.Id.ToString(),
            ["workflow_version_id"] = type?.Workflow?.VersionId?.ToString(),
            ["workflow_requires_review"] = policy?.WorkflowReview ?? false,
            ["skills"] = type?.Skills.Select(s => new Dictionary<string, object?>
            {
                ["code"] = s.Code, ["source_kind"] = s.SourceKind, ["content_sha256"] = s.ContentSha256,
            }).ToList(),
            ["extracted_at"] = extractedAt.ToString("O"),
            ["pinned_version"] = resolution.Pinned,
        };
    }

    public static IReadOnlyList<object> SkillRefs(ManifestDocumentType? type) =>
        type?.Skills.Select(s => (object)new { code = s.Code, sourceKind = s.SourceKind, contentSha256 = s.ContentSha256 }).ToList()
        ?? (IReadOnlyList<object>)[];
}

/// <summary>What actually executed — returned by the Agent service, never assumed.</summary>
public sealed record AgentTaskResult(
    string FinalAnswer, Guid? AgentId, Guid? AgentVersionId, int? AgentVersion, string? Provider, string? Model);
