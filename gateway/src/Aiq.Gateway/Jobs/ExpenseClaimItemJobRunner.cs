using System.Text.Json;
using Aiq.Gateway.Books;
using Aiq.Gateway.Configuration;
using Aiq.Gateway.Employees;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Storage;
using Aiq.Gateway.Vouchers;
using Aiq.Gateway.Vouchers.DocumentProcessing;
using Hangfire;
using Microsoft.Extensions.Options;

namespace Aiq.Gateway.Jobs;

/// <summary>
/// ONE bill file → ONE Expense Claim item: OCR → extraction under the
/// uploader's published Accounting Agent version (or the exact legacy call
/// when no configuration applies) → deterministic review rules → item result
/// + canonical document_processing_runs audit → claim re-derivation.
/// Items are independent: a failure here never touches sibling items. No
/// Hangfire auto-retry — a failure is an explicit, item-level retryable
/// state (POST .../items/{id}/retry).
/// </summary>
public sealed class ExpenseClaimItemJobRunner(
    IAgentClient agent,
    IObjectStorage storage,
    IOptions<S3Options> s3,
    IExpenseClaimIntakeRepository items,
    IDocumentProcessingRepository documents,
    ILogger<ExpenseClaimItemJobRunner> logger,
    IAgentUsageRepository? usage = null)
{
    public const string DocumentType = "expense_claim_item";

    [JobDisplayName("Expense claim item · {0}")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task RunAsync(Guid itemId, int attempt, Guid requestedBy, string userToken, CancellationToken ct)
    {
        var snap = await items.LoadForProcessingAsync(itemId, attempt, ct);
        if (snap is null)
        {
            logger.LogInformation("Expense claim item {ItemId} attempt {Attempt} superseded — skipped", itemId, attempt);
            return;
        }
        var item = snap.Item;
        var triggerKind = attempt > 1 ? "reextract" : "initial";
        logger.LogInformation("expense_claim_item_start item={ItemId} claim={ClaimId} attempt={Attempt} actor_id={ActorId}",
            itemId, item.ClaimVoucherId, attempt, requestedBy);

        // ---- OCR (once per item; a retry reuses the stored text) ----------
        var rawText = snap.RawOcrText;
        decimal? ocrConfidence = item.OcrConfidence;
        if (string.IsNullOrWhiteSpace(rawText))
        {
            try
            {
                byte[] bytes;
                await using (var s = await storage.GetAsync(s3.Value.VoucherBucket, snap.ObjectKey, ct))
                {
                    await using var buffer = new MemoryStream();
                    await s.CopyToAsync(buffer, ct);
                    bytes = buffer.ToArray();
                }
                await using var ocrStream = new MemoryStream(bytes, writable: false);
                var ocr = item.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    ? await agent.RunOcrImageAsync(ocrStream, item.OriginalFilename, item.ContentType, ct)
                    : await agent.RunOcrPipelineAsync(ocrStream, item.OriginalFilename, ct);
                rawText = ocr.JoinedText ?? string.Empty;
                ocrConfidence = (decimal)ocr.AverageConfidence;
                await RecordOcrUsageAsync(ocr.LlmUsage, snap.TenantId, snap.DepartmentId, item.CreatedBy, itemId, ct);
                if (string.IsNullOrWhiteSpace(rawText)) throw new InvalidOperationException("empty OCR text");
                await items.SaveOcrAsync(itemId, attempt, rawText, ocrConfidence.Value, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Expense claim item {ItemId}: OCR failed", itemId);
                await FailAsync(snap, attempt, requestedBy, ExpenseClaimItemErrors.OcrUnavailable, "ocr", null, null, triggerKind, ct);
                return;
            }
        }

        // ---- Which Agent / version (same policy as voucher extraction) ----
        // An item already processed under a manifest version is re-run with
        // EXACTLY that version (never silently downgraded to legacy);
        // otherwise the uploader's own capability chain picks the Agent.
        DocumentAgentResolution resolution;
        if (item.ConfigSource == DocumentConfigSources.Manifest && item.AgentVersionId is not null)
            resolution = await documents.ResolvePinnedAsync(snap.TenantId, snap.DepartmentId, item.AgentId, item.AgentVersionId, ct);
        else
        {
            try { resolution = await documents.ResolveForUploaderAsync(snap.TenantId, snap.DepartmentId, item.CreatedBy, null, ct); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Expense claim item {ItemId}: agent resolution failed — legacy extraction", itemId);
                resolution = new(null, null, null, null, null, "resolution_failed");
            }
        }
        if (resolution.Pinned && resolution.DenialReason is { } denial)
        {
            await FailAsync(snap, attempt, requestedBy, denial, "agent_resolution", resolution, null, triggerKind, ct);
            return;
        }
        var typeConfig = resolution.Config?.Find(VoucherTypes.ExpenseClaim);
        if (resolution.Config is { } cfg && !cfg.Supports(VoucherTypes.ExpenseClaim))
        {
            await FailAsync(snap, attempt, requestedBy, ExpenseClaimItemErrors.TypeNotEnabled, "agent_resolution", resolution, null, triggerKind, ct);
            return;
        }

        var categories = await items.ConfiguredCategoriesAsync(snap.TenantId, snap.DepartmentId, ct);
        var prompt = ExpenseClaimItemRules.BuildItemPrompt(typeConfig, categories, rawText);
        AgentTaskResult executed;
        try
        {
            executed = resolution.Config is not null
                ? await agent.RunDocumentExtractionAsync(resolution.AgentId!.Value, resolution.AgentVersionId!.Value, test: false,
                    prompt, userToken, ct, pinVersion: true)
                : await agent.RunAgentTaskWithProvenanceAsync(prompt, userToken, "voucher.processing", resolution.AgentId, 3, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Expense claim item {ItemId}: agent call failed", itemId);
            await FailAsync(snap, attempt, requestedBy, ExtractionFailures.Classify(ex), "agent_execution", resolution, null, triggerKind, ct);
            return;
        }

        var (fields, _) = VoucherExtractJobRunner.ExtractJsonBlocks(executed.FinalAnswer);
        if (fields is null)
        {
            logger.LogWarning("Expense claim item {ItemId}: no JSON in answer answer_length={Length}", itemId, executed.FinalAnswer.Length);
            await FailAsync(snap, attempt, requestedBy, ExtractionFailures.ClassifyAnswer(executed.FinalAnswer), "agent_output", resolution, executed, triggerKind, ct);
            return;
        }

        // Model output → proposed values only. Anything else it returns
        // (gl_account, tax_verified, payable, ...) is kept in extracted_data
        // as evidence and has no effect.
        var proposedCategory = VoucherExtractJobRunner.ReadStringAny(fields, "category", "loai", "expense_type");
        var values = new ExpenseClaimItemValues(
            ExpenseClaimItemRules.MatchCategory(proposedCategory, categories) ?? proposedCategory,
            VoucherExtractJobRunner.ReadStringAny(fields, "merchant_name", "seller_name", "vendor_name"),
            VoucherExtractJobRunner.ReadStringAny(fields, "merchant_tax_code", "seller_mst", "vendor_mst"),
            VoucherExtractJobRunner.ReadStringAny(fields, "document_number", "invoice_no"),
            VoucherExtractJobRunner.ReadDate(fields, "document_date"),
            VoucherExtractJobRunner.ReadDecimalAny(fields, "gross_amount", "total_amount", "amount"),
            VoucherExtractJobRunner.ReadDecimalAny(fields, "vat_amount"),
            ExpenseClaimItemRules.NormalizeCurrency(VoucherExtractJobRunner.ReadStringAny(fields, "currency")),
            VoucherExtractJobRunner.ReadStringAny(fields, "description"));
        var confidence = VoucherExtractJobRunner.ReadDecimalAny(fields, "confidence");
        var duplicates = await items.DuplicateEvidenceAsync(snap.TenantId, item.ClaimVoucherId, itemId, item.Sha256, values, ct);
        var reasons = ExpenseClaimItemRules.ReviewReasons(values, confidence, ocrConfidence, categories, snap.TripStart, snap.TripEnd,
            duplicates, typeConfig?.WorkflowRequiresReview == true);

        var agentId = executed.AgentId ?? resolution.AgentId;
        var versionId = executed.AgentVersionId ?? resolution.AgentVersionId;
        var version = executed.AgentVersion ?? resolution.Version;
        var stored = await items.CompleteAsync(itemId, attempt, requestedBy, new ExpenseClaimItemExtraction(
            values, confidence, JsonSerializer.Serialize(fields), reasons.ToArray(), agentId, versionId, version,
            resolution.ConfigSource, executed.Provider, executed.Model), ct);
        if (!stored)
        {
            logger.LogInformation("Expense claim item {ItemId} attempt {Attempt} superseded before completion", itemId, attempt);
            return;
        }
        await RecordRunAsync(snap, requestedBy, agentId, versionId, version, resolution.ConfigSource, typeConfig, executed.Provider,
            executed.Model, triggerKind, null, null, ct);
        logger.LogInformation(
            "expense_claim_item_done item={ItemId} claim={ClaimId} status={Status} review_reasons={Reasons} runtime_agent_id={AgentId} runtime_agent_version={Version} document_config_source={Source}",
            itemId, item.ClaimVoucherId, reasons.Count == 0 ? "ready" : "needs_review", string.Join(",", reasons), agentId, version, resolution.ConfigSource);
        await items.RecomputeAsync(snap.TenantId, snap.DepartmentId, item.ClaimVoucherId, ct);
    }

    // Best effort — never fails the item (see VoucherOcrJobRunner).
    private async Task RecordOcrUsageAsync(LlmUsageReport? report, Guid tenantId, Guid departmentId, Guid actorId, Guid itemId, CancellationToken ct)
    {
        if (usage is null || report is null) return;
        try { await usage.RecordAsync(AgentUsageEvents.FromOcr(report, tenantId, departmentId, actorId), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Expense claim item {ItemId}: OCR usage not recorded", itemId);
        }
    }

    private async Task FailAsync(ExpenseClaimItemProcessingSnapshot snap, int attempt, Guid actorId, string code, string stage,
        DocumentAgentResolution? resolution, AgentTaskResult? executed, string triggerKind, CancellationToken ct)
    {
        var agentId = executed?.AgentId ?? resolution?.AgentId;
        var versionId = executed?.AgentVersionId ?? resolution?.AgentVersionId;
        var version = executed?.AgentVersion ?? resolution?.Version;
        logger.LogWarning("expense_claim_item_failed item={ItemId} claim={ClaimId} error_code={Code} error_stage={Stage}",
            snap.Item.Id, snap.Item.ClaimVoucherId, code, stage);
        if (!await items.FailAsync(snap.Item.Id, attempt, actorId, code, stage, agentId, versionId, version,
                resolution?.ConfigSource, executed?.Provider, executed?.Model, ct)) return;
        await RecordRunAsync(snap, actorId, agentId, versionId, version, resolution?.ConfigSource ?? DocumentConfigSources.Legacy,
            null, executed?.Provider, executed?.Model, triggerKind, code, stage, ct);
        await items.RecomputeAsync(snap.TenantId, snap.DepartmentId, snap.Item.ClaimVoucherId, ct);
    }

    private async Task RecordRunAsync(ExpenseClaimItemProcessingSnapshot snap, Guid actorId, Guid? agentId, Guid? versionId, int? version,
        string configSource, ManifestDocumentType? type, string? provider, string? model, string triggerKind, string? errorCode,
        string? errorStage, CancellationToken ct)
    {
        try
        {
            await documents.RecordRunAsync(new DocumentProcessingRun(
                "production", snap.Item.ClaimVoucherId, snap.TenantId, snap.DepartmentId, actorId, agentId, versionId, version,
                configSource, DocumentType, type?.SchemaSha256, type?.Workflow?.Id, type?.Workflow?.VersionId, DocumentExtraction.SkillRefs(type),
                provider, model, null, null, null, null, [], false,
                TriggerKind: triggerKind, ErrorCode: errorCode, ErrorStage: errorStage, ExpenseClaimItemId: snap.Item.Id), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Expense claim item {ItemId}: failed to record processing run", snap.Item.Id);
        }
    }
}

public interface IExpenseClaimItemJobScheduler
{
    string Enqueue(Guid itemId, int attempt, Guid requestedBy, string userToken);
}

public sealed class ExpenseClaimItemJobScheduler(IBackgroundJobClient hangfire) : IExpenseClaimItemJobScheduler
{
    public string Enqueue(Guid itemId, int attempt, Guid requestedBy, string userToken) =>
        hangfire.Enqueue<ExpenseClaimItemJobRunner>(r => r.RunAsync(itemId, attempt, requestedBy, userToken, CancellationToken.None));
}
