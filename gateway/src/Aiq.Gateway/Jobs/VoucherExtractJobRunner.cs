using System.Text.Json;
using System.Text.RegularExpressions;
using Aiq.Gateway.Approvals;
using Aiq.Gateway.Books;
using Aiq.Gateway.Customers;
using Aiq.Gateway.Employees;
using Aiq.Gateway.Suppliers;
using Aiq.Gateway.Vouchers;
using Aiq.Gateway.Vouchers.DocumentProcessing;
using Hangfire;
using Microsoft.Extensions.Options;

namespace Aiq.Gateway.Jobs;

// UC-ACC-001 P1 (part 2) — LLM extraction + AR/AP enrichment + 3-way match
// all in Hangfire so Next.js promise lifecycle can't kill it mid-flight.
//
// Runs AFTER VoucherOcrJobRunner (chained via ContinueJobWith on the
// scheduler). If OCR ended in ocr_unavailable / rejected, this job
// short-circuits and does nothing.
public sealed class VoucherExtractJobRunner
{
    private readonly IAgentClient _agent;
    private readonly IVoucherRepository _vouchers;
    private readonly ICustomerRepository _customers;
    private readonly ISupplierRepository _suppliers;
    private readonly IEmployeeRepository _employees;
    private readonly IApprovalRepository _approvals;
    private readonly Aiq.Gateway.Receipts.IReceiptMatchRepository _receiptMatches;
    private readonly IExpenseClaimRepository _expenseClaims;
    private readonly ExpenseRiskOptions _riskOptions;
    private readonly Aiq.Gateway.Governance.ITenantCompanyProfileRepository _companyProfiles;
    private readonly Aiq.Gateway.Banking.IBankStatementRepository _bankStatements;
    private readonly IDocumentProcessingRepository _documents;
    private readonly IDocumentReviewRepository _reviews;
    private readonly Aiq.Gateway.Vouchers.Validation.VoucherValidationService _validation;
    private readonly ILogger<VoucherExtractJobRunner> _logger;

    public VoucherExtractJobRunner(
        IAgentClient agent,
        IVoucherRepository vouchers,
        ICustomerRepository customers,
        ISupplierRepository suppliers,
        IEmployeeRepository employees,
        IApprovalRepository approvals,
        Aiq.Gateway.Receipts.IReceiptMatchRepository receiptMatches,
        IExpenseClaimRepository expenseClaims,
        IOptions<ExpenseRiskOptions> riskOptions,
        Aiq.Gateway.Governance.ITenantCompanyProfileRepository companyProfiles,
        Aiq.Gateway.Banking.IBankStatementRepository bankStatements,
        IDocumentProcessingRepository documents,
        IDocumentReviewRepository reviews,
        Aiq.Gateway.Vouchers.Validation.VoucherValidationService validation,
        ILogger<VoucherExtractJobRunner> logger)
    {
        _validation = validation;
        _reviews = reviews;
        _bankStatements = bankStatements;
        _documents = documents;
        _companyProfiles = companyProfiles;
        _agent = agent;
        _vouchers = vouchers;
        _customers = customers;
        _suppliers = suppliers;
        _employees = employees;
        _approvals = approvals;
        _receiptMatches = receiptMatches;
        _expenseClaims = expenseClaims;
        _riskOptions = riskOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Upload-chained extraction (and jobs queued before requestedBy
    /// existed): the uploader is both creator and processing actor. Kept with
    /// its original signature so already-enqueued Hangfire jobs still run.
    /// </summary>
    [JobDisplayName("Voucher extract · {0}")]
    [AutomaticRetry(Attempts = 2, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task RunAsync(
        Guid voucherId,
        Guid userId,
        Guid? tenantId,
        string voucherType,
        string userToken,
        CancellationToken ct)
        => RunForActorAsync(voucherId, userId, tenantId, voucherType, userToken, userId, ct);

    /// <summary>
    /// Extraction requested by a specific principal (manual re-extract, ADMIN
    /// type correction). <paramref name="userId"/> stays the document's
    /// creator (row scope); <paramref name="requestedBy"/> is the server-side
    /// authenticated principal that asked for THIS run — recorded as the run
    /// actor, never reconstructed from the voucher.
    /// </summary>
    [JobDisplayName("Voucher extract · {0}")]
    [AutomaticRetry(Attempts = 2, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task RunForActorAsync(
        Guid voucherId,
        Guid userId,
        Guid? tenantId,
        string voucherType,
        string userToken,
        Guid requestedBy,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Voucher extract job start · id={VoucherId} type={Type} actor_id={ActorId}", voucherId, voucherType, requestedBy);

        // Tenant context required — colleague's IVoucherRepository signatures
        // enforce non-nullable Guid tenantId. If Hangfire enqueue lost tenant
        // context (should not happen in normal flow), skip gracefully.
        if (tenantId is not Guid tenantIdValue)
        {
            _logger.LogWarning("Voucher {VoucherId} has no tenant context — extract skipped", voucherId);
            return;
        }

        var stub = await _vouchers.FindAsync(tenantIdValue, userId, voucherId, ct);
        if (stub is null)
        {
            _logger.LogWarning("Voucher {VoucherId} not found — extract skipped", voucherId);
            return;
        }
        // P2: a posted voucher is never re-extracted — refuse before any side
        // effect (bank statement import, review gate, L2 enqueue, provenance).
        if (VoucherPostingGuard.IsPostedStatus(stub.Voucher.Status))
        {
            _logger.LogWarning("Voucher {VoucherId} status={Status} is posted — extract skipped", voucherId, stub.Voucher.Status);
            return;
        }
        // The actor id comes from the enqueueing endpoint's authenticated
        // principal; still refuse anything that is not an active user of THIS
        // tenant (defense in depth against a tampered job payload).
        if (requestedBy != userId && !await _documents.IsActiveTenantUserAsync(tenantIdValue, requestedBy, ct))
        {
            _logger.LogError("Voucher {VoucherId}: extraction refused — actor_id={ActorId} is not an active user of tenant {TenantId}",
                voucherId, requestedBy, tenantIdValue);
            return;
        }
        var actorId = requestedBy;
        if (stub.Voucher.Status is "ocr_unavailable" or "rejected")
        {
            _logger.LogInformation(
                "Voucher {VoucherId} status={Status} — skipping extract", voucherId, stub.Voucher.Status);
            return;
        }
        var rawText = stub.Voucher.RawOcrText ?? "";
        if (string.IsNullOrWhiteSpace(rawText))
        {
            _logger.LogWarning("Voucher {VoucherId} has no rawOcrText — cannot extract", voucherId);
            return;
        }

        // ---- Voucher type: deterministic evidence BEFORE the LLM ----------
        // voucherType here is a request: "auto", a user hint, or
        // "admin:<type>" (an ADMIN correction that must win).
        var typeRequest = Aiq.Gateway.Vouchers.VoucherTypeRequest.Parse(voucherType);
        var profile = await _companyProfiles.GetAsync(tenantIdValue, ct);
        var company = profile is null ? null : new Aiq.Gateway.Payments.CompanyIdentity(profile.TaxCode,
            new[] { profile.LegalName, profile.ShortName }.Concat(profile.Aliases ?? Array.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList());
        var detection = Aiq.Gateway.Vouchers.VoucherTypeClassifier.Detect(rawText, company);

        // ---- Which Agent / version / configuration processes this -------
        // The uploader's own capability chain (voucher.processing) picks the
        // Agent — an upload-time requested agent is only a hint re-validated
        // there. If that Agent's PUBLISHED version carries document
        // configuration, it drives the prompt + schema validation
        // (document_config_source=manifest); otherwise the built-in legacy
        // extraction runs unchanged (document_config_source=legacy).
        var candidateType = typeRequest.Forced ?? detection.Type ?? typeRequest.Hint;
        var forced = typeRequest.Forced is not null;
        // Re-extraction policy: a document that was already processed with a
        // manifest is re-run with its RECORDED agent + published version
        // (reproducibility), authorized by the CURRENT caller's token — never
        // by the original uploader's present eligibility, and never silently
        // downgraded to legacy. Documents without manifest provenance keep
        // the current selection / legacy compatibility behavior.
        var provenance = await _documents.GetProvenanceAsync(tenantIdValue, voucherId, ct);
        var reextract = provenance.IsReextract;
        var triggerKind = reextract ? "reextract" : "initial";
        DocumentAgentResolution resolution = null!;
        AgentTaskResult executed = null!;
        for (var attempt = 1; ; attempt++)
        {
            resolution = provenance.IsManifest
                ? await ResolvePinnedDocumentAgentAsync(tenantIdValue, stub.Voucher.DepartmentId, provenance, ct)
                : await ResolveDocumentAgentAsync(tenantIdValue, stub.Voucher.DepartmentId, userId, voucherId, ct);
            _logger.LogInformation(
                "Voucher {VoucherId}: runtime_agent_id={RuntimeAgentId} runtime_agent_version={RuntimeAgentVersion} document_config_source={Source} reextract={Reextract} pinned={Pinned} denial={Denial}",
                voucherId, resolution.AgentId, resolution.Version, resolution.ConfigSource, reextract, resolution.Pinned, resolution.DenialReason);
            if (resolution.Pinned && resolution.DenialReason is { } pinnedFailure)
            {
                await FailExtractionAsync(tenantIdValue, stub.Voucher.DepartmentId, actorId, voucherId, resolution, null,
                    pinnedFailure, "agent_resolution", triggerKind, ct);
                return;
            }
            try
            {
                if (resolution.Config is { } config)
                {
                    // Tool-less extraction pinned to the exact published
                    // version the prompt/schema were built from.
                    executed = await _agent.RunDocumentExtractionAsync(
                        resolution.AgentId!.Value, resolution.AgentVersionId!.Value, test: false,
                        DocumentExtraction.BuildManifestPrompt(config, candidateType, forced, rawText), userToken, ct,
                        pinVersion: resolution.Pinned);
                }
                else
                {
                    // Legacy: byte-for-byte the previous prompt and /agent/run
                    // call. Ask agent for 2 JSON blocks: fields object +
                    // entries array. The extractor classifies INDEPENDENTLY
                    // (the hint is shown as possibly wrong) so its answer is
                    // real corroboration, not an echo. D6: routes through the
                    // capability resolver (voucher.processing); authorization
                    // is the UPLOADING user's own chain. The resolved agent (if
                    // any) is passed as preferred_agent_id only so provenance
                    // names the agent that really ran — the Agent re-checks it.
                    executed = await _agent.RunAgentTaskWithProvenanceAsync(
                        BuildExtractionPrompt(candidateType, forced, rawText), userToken,
                        capability: "voucher.processing", preferredAgentId: resolution.AgentId, maxTurns: 3, ct);
                }
                break;
            }
            catch (AgentRequestException ex) when (resolution.Pinned && ex.UpstreamStatus is 403 or 409)
            {
                // The current caller may not run the recorded agent/version
                // (403), or the Agent could not load it (409): explicit
                // failure — never legacy.
                await FailExtractionAsync(tenantIdValue, stub.Voucher.DepartmentId, actorId, voucherId, resolution, null,
                    ex.UpstreamStatus == 403 ? DocumentProvenanceErrors.AgentUnavailable : DocumentProvenanceErrors.VersionUnavailable,
                    "agent_resolution", triggerKind, ct);
                return;
            }
            catch (AgentRequestException ex) when (ex.UpstreamStatus == 409 && resolution.Config is not null && attempt == 1)
            {
                // A new version was published between resolution and run —
                // resolve again rather than run a mismatched configuration.
                _logger.LogWarning("Voucher {VoucherId}: agent version changed during extraction — re-resolving", voucherId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Provider/runtime failure after OCR: explicit, visible,
                // retryable failed state — never a silent ocr_complete, never
                // an empty extraction over a previous good one.
                _logger.LogError(ex, "Agent extract call failed for voucher {VoucherId}", voucherId);
                await FailExtractionAsync(tenantIdValue, stub.Voucher.DepartmentId, actorId, voucherId, resolution, null,
                    ExtractionFailures.Classify(ex), "agent_execution", triggerKind, ct);
                return;
            }
        }
        var agentAnswer = executed.FinalAnswer;

        var (fields, entries) = ExtractJsonBlocks(agentAnswer);
        if (fields is null)
        {
            // A provider can answer 200 with a limit/error message instead of
            // JSON (e.g. "You've hit your session limit"). Only the safe code
            // is persisted; the preview stays in server logs.
            var code = ExtractionFailures.ClassifyAnswer(agentAnswer);
            _logger.LogWarning(
                "Voucher {VoucherId}: agent returned no valid fields JSON error_code={ErrorCode} answer_length={Length}",
                voucherId, code, agentAnswer.Length);
            await FailExtractionAsync(tenantIdValue, stub.Voucher.DepartmentId, actorId, voucherId, resolution, executed,
                code, "agent_output", triggerKind, ct);
            return;
        }

        // Enrich context — tuỳ voucher_type gọi AR (customers/sim_ar_ledger)
        // hoặc AP (suppliers/sim_ap_ledger) hoặc expense (employees).
        //
        // voucher_type covered:
        //   receipt          → AR context + receipt_matching (FIFO oldest)
        //   invoice_vat_out  → AR context + 3-way SO match
        //   invoice_vat*     → AR (legacy: pre-universal-extractor tenants)
        //   invoice_vat_in   → AP context (seller = NCC)
        //   payment          → AP context + payment_matching (FIFO oldest)
        //   bank_transfer    → AP context + payment_matching (beneficiary = NCC)
        //   expense_claim    → expense_claim_matching (employee lookup)
        // Combine deterministic evidence + extractor classification + request
        // into ONE final type, decided before enrichment/settlement/posting.
        var classification = Aiq.Gateway.Vouchers.VoucherTypeClassifier.Combine(
            typeRequest, detection, ReadString(fields, "voucher_type"),
            ReadStringAny(fields, "seller_mst", "seller_tax_code"), ReadStringAny(fields, "buyer_mst", "buyer_tax_code"),
            company?.TaxCode);
        if (typeRequest.Forced is not null)
        {
            // Re-extraction after an ADMIN correction: keep who/when.
            var previous = Aiq.Gateway.Vouchers.VoucherTypeClassification.FromExtractedFields(stub.Voucher.ExtractedFieldsJson);
            classification = classification with
            {
                UploadedAs = previous?.UploadedAs,
                ConfirmedBy = previous?.ConfirmedBy,
                ConfirmedAt = previous?.ConfirmedAt,
            };
        }
        // Published document configuration: supported types, schema,
        // workflow review. Only ever ADDS blockers to the classifier's
        // decision — never weakens confidence/conflict/hint gates.
        DocumentPolicyOutcome? policy = null;
        if (resolution.Config is { } publishedConfig)
        {
            policy = DocumentExtraction.ApplyPolicy(publishedConfig, classification, fields,
                $"Trợ lý {resolution.AgentName} v{resolution.Version}");
            classification = policy.Classification;
            if (policy.Unsupported)
                _logger.LogWarning("Voucher {VoucherId}: unsupported_document_type {Type} for agent {AgentId} v{Version}",
                    voucherId, classification.VoucherType, resolution.AgentId, resolution.Version);
        }
        var unsupportedType = policy?.Unsupported == true;
        fields["document_processing"] = DocumentExtraction.ProvenanceBlock(resolution, executed, policy, DateTime.UtcNow);

        if (!string.Equals(classification.VoucherType, voucherType, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Voucher {VoucherId}: voucher_type {Requested} -> {Final} ({Source}, {Confidence}, {Status})",
                voucherId, voucherType, classification.VoucherType, classification.Source, classification.Confidence, classification.Status);
        }
        var llmType = ReadString(fields, "voucher_type");
        if (!string.IsNullOrWhiteSpace(llmType) && !string.Equals(llmType, classification.VoucherType, StringComparison.OrdinalIgnoreCase))
        {
            fields["voucher_type_extracted"] = llmType;
        }
        if (classification.UploadedAs is not null) fields["voucher_type_uploaded_as"] = classification.UploadedAs;
        fields["voucher_type_classification"] = System.Text.Json.JsonSerializer.SerializeToElement(
            classification, Aiq.Gateway.Vouchers.VoucherTypeClassification.Json);
        fields["voucher_type"] = classification.VoucherType;
        voucherType = classification.VoucherType;

        var documentNumber = ReadString(fields, "document_number");
        var vt = voucherType.ToLowerInvariant();

        // An unsupported type gets NO server handler (no AR/AP context,
        // matching, proposals, statement import, journals or approvals) —
        // the extraction is stored for an ADMIN decision only.
        if (tenantId is Guid t && !unsupportedType)
        {
            var d = stub.Voucher.DepartmentId ?? Guid.Empty;
            switch (vt)
            {
                case "receipt":
                {
                    var payerMst = ReadStringAny(fields, "payer_mst", "buyer_mst", "customer_mst");
                    var receiptAmount = ReadDecimalAny(fields, "amount", "total_amount", "so_tien") ?? 0m;
                    var currency = ReadString(fields, "currency") ?? "VND";
                    object? arContext = null;
                    if (!string.IsNullOrWhiteSpace(payerMst))
                    {
                        // Receipt không tăng AR — pass 0m làm projected delta.
                        arContext = await BuildArContextAsync(t, d, payerMst, 0m, ct);
                        if (arContext is not null) fields["ar_context"] = arContext;
                    }
                    if (arContext is not null && receiptAmount > 0m)
                    {
                        var matching = BuildReceiptMatching(arContext, receiptAmount);
                        if (matching is not null) fields["receipt_matching"] = matching;
                    }

                    // Luồng 02 — persist the deterministic outcome
                    // classification (Customer match + AR + Contract
                    // installment linkage). Read-only proposal: never posts
                    // to sim_ar_ledger or contract_payment_schedules.
                    try
                    {
                        var receiptMatch = await _receiptMatches.ProposeMatchAsync(
                            t, d, voucherId, payerMst, receiptAmount, currency, ct);
                        fields["receipt_match_outcome"] = receiptMatch.Outcome;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Receipt/AR match proposal failed for voucher {VoucherId}", voucherId);
                    }
                    break;
                }

                case "invoice_vat_out":
                case "invoice_vat":  // legacy tenants pre-universal-extractor
                {
                    var buyerMst = ReadString(fields, "buyer_mst");
                    var invoiceTotal = ReadDecimal(fields, "total_amount") ?? 0m;
                    if (!string.IsNullOrWhiteSpace(buyerMst))
                    {
                        var arContext = await BuildArContextAsync(t, d, buyerMst, invoiceTotal, ct);
                        if (arContext is not null) fields["ar_context"] = arContext;
                    }
                    if (!string.IsNullOrWhiteSpace(documentNumber))
                    {
                        var matchContext = await BuildMatchContextAsync(t, d, documentNumber, ct);
                        if (matchContext is not null) fields["match_context"] = matchContext;
                    }
                    break;
                }

                case "invoice_vat_in":
                {
                    var sellerMst = ReadStringAny(fields, "seller_mst", "supplier_mst");
                    var invoiceTotal = ReadDecimalAny(fields, "total_amount", "amount") ?? 0m;
                    var paymentMethod = ReadString(fields, "payment_method");
                    if (!string.IsNullOrWhiteSpace(sellerMst))
                    {
                        var apContext = await BuildApContextAsync(t, d, sellerMst, invoiceTotal, paymentMethod, ct);
                        if (apContext is not null) fields["ap_context"] = apContext;
                    }
                    // PO 3-way match: invoice → PO → GRN (+ purchase_contract Sprint 5).
                    object? poMatch = null;
                    if (!string.IsNullOrWhiteSpace(documentNumber))
                    {
                        poMatch = await BuildPoMatchContextAsync(t, d, documentNumber, ct);
                        if (poMatch is not null) fields["po_match_context"] = poMatch;
                    }
                    // no_matching_po flag: invoice > threshold nhưng không match PO nào.
                    // Trưởng phòng cần review manually — có thể là HĐ ngoài PO
                    // (mua vặt) hoặc extract sai document_number.
                    if (invoiceTotal > _riskOptions.InvoiceRequiresPoAbove)
                    {
                        var poMatched = poMatch is not null
                            && System.Text.Json.JsonSerializer.Serialize(poMatch).Contains("\"status\":\"found\"");
                        if (!poMatched && fields.TryGetValue("ap_context", out var apCtxObj) && apCtxObj is not null)
                        {
                            // Mutate risk_flags trong ap_context. Re-serialize để giữ nested shape.
                            var ctxJson = System.Text.Json.JsonSerializer.Serialize(apCtxObj);
                            var ctxDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(ctxJson);
                            if (ctxDict is not null && ctxDict.TryGetValue("risk_flags", out var flagsObj)
                                && flagsObj is System.Text.Json.JsonElement flagsEl
                                && flagsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                var flagsList = new List<string>();
                                foreach (var f in flagsEl.EnumerateArray())
                                {
                                    if (f.ValueKind == System.Text.Json.JsonValueKind.String)
                                    {
                                        flagsList.Add(f.GetString()!);
                                    }
                                }
                                if (!flagsList.Contains("no_matching_po"))
                                {
                                    flagsList.Add("no_matching_po");
                                    ctxDict["risk_flags"] = flagsList;
                                    fields["ap_context"] = ctxDict;
                                }
                            }
                        }
                    }
                    break;
                }

                case "payment":
                case "bank_transfer":
                {
                    // Payment: TM/CK phiếu chi → payee_mst. Bank_transfer: UNC → beneficiary_mst.
                    var payeeMst = vt == "bank_transfer"
                        ? ReadStringAny(fields, "beneficiary_mst", "payee_mst", "supplier_mst", "seller_mst")
                        : ReadStringAny(fields, "payee_mst", "supplier_mst", "seller_mst");
                    var paymentAmount = ReadDecimalAny(fields, "amount", "total_amount", "so_tien") ?? 0m;
                    var paymentMethod = ReadString(fields, "payment_method");

                    object? apContext = null;
                    if (!string.IsNullOrWhiteSpace(payeeMst))
                    {
                        // Payment không tăng AP — pass 0m làm projected delta.
                        apContext = await BuildApContextAsync(t, d, payeeMst, 0m, paymentMethod, ct);
                        if (apContext is not null) fields["ap_context"] = apContext;
                    }
                    if (apContext is not null && paymentAmount > 0m)
                    {
                        var matching = BuildPaymentMatching(apContext, paymentAmount);
                        if (matching is not null) fields["payment_matching"] = matching;
                    }
                    break;
                }

                case "expense_claim":
                {
                    var employeeCode = ReadStringAny(fields, "employee_code", "employee_id", "ma_nv");
                    var totalClaim = ReadDecimalAny(fields, "total_claim", "total_amount", "amount", "so_tien") ?? 0m;
                    var advanceTaken = ReadDecimalAny(fields, "advance_taken", "tam_ung") ?? 0m;

                    // Items có thể ở key `items` (universal-extractor schema) hoặc
                    // `line_items` (legacy invoice_vat schema).
                    List<Dictionary<string, object?>>? items = null;
                    foreach (var itemsKey in new[] { "items", "line_items" })
                    {
                        if (fields.TryGetValue(itemsKey, out var itemsObj) && itemsObj is List<object?> itemsList)
                        {
                            items = itemsList.OfType<Dictionary<string, object?>>().ToList();
                            if (items.Count > 0) break;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(employeeCode))
                    {
                        var claim = await BuildExpenseClaimMatchingAsync(t, employeeCode, totalClaim, advanceTaken, items, ct);
                        if (claim is not null) fields["expense_claim_matching"] = claim;
                    }

                    // Luồng 03 — persist the deterministic outcome
                    // classification (duplicate detection, policy limits,
                    // advance reconciliation, Supplier resolution). Read-only
                    // proposal: never posts to sim_employees/sim_ap_ledger.
                    try
                    {
                        var currency = ReadString(fields, "currency") ?? "VND";
                        var claimItems = new List<Aiq.Gateway.Employees.ExpenseClaimItemInput>();
                        if (items is not null)
                        {
                            for (var idx = 0; idx < items.Count; idx++)
                            {
                                var it = items[idx];
                                var amount = ReadDecimalAny(it, "amount", "so_tien", "total_amount") ?? 0m;
                                claimItems.Add(new Aiq.Gateway.Employees.ExpenseClaimItemInput(
                                    Seq: idx + 1,
                                    Category: ReadStringAny(it, "category", "loai", "loai_chi_phi") ?? "khác",
                                    Description: ReadStringAny(it, "description", "noi_dung", "dien_giai"),
                                    Amount: amount,
                                    ReceiptAttached: ReadBoolAny(it, "receipt_attached", "co_chung_tu"),
                                    VendorMst: ReadStringAny(it, "vendor_mst", "supplier_mst"),
                                    InvoiceNo: ReadStringAny(it, "invoice_no", "document_number"),
                                    DocumentDate: ReadDate(it, "document_date"),
                                    VatAmount: ReadDecimalAny(it, "vat_amount")));
                            }
                        }

                        var expenseClaim = await _expenseClaims.ProposeAsync(
                            t, d, voucherId,
                            new Aiq.Gateway.Employees.ExpenseClaimProposalInput(
                                employeeCode, totalClaim > 0m ? totalClaim : null, advanceTaken, currency, claimItems),
                            ct);
                        fields["expense_claim_outcome"] = expenseClaim.Outcome;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Expense claim outcome proposal failed for voucher {VoucherId}", voucherId);
                    }
                    break;
                }

                // Other types (bank_statement, contract, unknown) — no enrichment.
            }
        }

        // Convert fields object → CreateVoucherRequest for ApplyExtraction.
        var applyRequest = BuildApplyRequest(voucherType, fields, rawText);

        // Duplicate check (E2): if another voucher has same seller_mst + doc_number,
        // flip current to rejected instead of overwriting.
        if (!string.IsNullOrWhiteSpace(applyRequest.SellerMst) && !string.IsNullOrWhiteSpace(applyRequest.DocumentNumber))
        {
            var dup = await _vouchers.FindDuplicateAsync(
                tenantId, applyRequest.SellerMst, applyRequest.DocumentNumber, voucherId, ct);
            if (dup is not null)
            {
                await _vouchers.UpdateOcrResultAsync(
                    voucherId,
                    status: "rejected",
                    rawText: null,
                    ocrConfidence: null,
                    errorMessage: $"Trùng hoá đơn: {applyRequest.DocumentNumber} của MST {applyRequest.SellerMst} đã tồn tại (voucher {dup.Id}).",
                    ct);
                _logger.LogWarning(
                    "Voucher {VoucherId} rejected — duplicate of {DupId}", voucherId, dup.Id);
                return;
            }
        }

        // Bank statement: one document, MANY transactions. Store each line
        // (idempotent by fingerprint) and identify the Customer/Supplier —
        // identification only, no posting/AR/AP effect.
        if (!unsupportedType
            && string.Equals(voucherType, Aiq.Gateway.Vouchers.VoucherTypes.BankStatement, StringComparison.OrdinalIgnoreCase)
            && stub.Voucher.DepartmentId is Guid statementDept)
        {
            var parsed = Aiq.Gateway.Banking.BankStatementParser.ParseDelimitedText(rawText);
            if (parsed.Lines.Count == 0) parsed = Aiq.Gateway.Banking.BankStatementParser.FromExtractedFields(fields);
            if (parsed.CompanyAccountNumber is null && ReadStringAny(fields, "account_number") is { } ownAccount)
                parsed = parsed with { CompanyAccountNumber = ownAccount };
            var summary = await _bankStatements.ImportAsync(tenantIdValue, statementDept, voucherId, parsed, ct);
            fields["bank_statement_import"] = summary;
            _logger.LogInformation("Voucher {VoucherId}: bank statement {Source} lines={Lines} inserted={Inserted} duplicates={Dup}",
                voucherId, summary.Source, summary.Lines, summary.Inserted, summary.Duplicates);
        }

        // ---- Durable gates, persisted BEFORE the extraction becomes visible --
        // Each blocker has ONE owner and lives in its own column — none is
        // folded into the voucher-type confirmation flag, so an ADMIN type
        // confirmation can never clear it (see DocumentAccountingGate):
        //  * require_review → document_review_tasks + document_review_status
        //    (awaiting / review_task_creation_failed), owned by review;
        //  * legacy L2 enqueue failure → processing_blockers, owned by this job.
        Guid? reviewTaskId = null;
        var reviewRequired = policy is { WorkflowReview: true, Unsupported: false };
        if (reviewRequired && policy!.TypeConfig?.Workflow is { } reviewWorkflow)
        {
            var gate = await OpenReviewGateAsync(_reviews, _logger, tenantIdValue, stub.Voucher.DepartmentId, voucherId,
                executed.AgentId ?? resolution.AgentId, executed.AgentVersionId ?? resolution.AgentVersionId, reviewWorkflow,
                classification.VoucherType, actorId, ct);
            reviewTaskId = gate.TaskId;
            fields["document_review"] = gate.FieldsBlock;
        }
        // Legacy L2 queue (sim_approval_queue): an enqueue failure becomes the
        // extraction-owned blocker legacy_l2_failure. Only a run that actually
        // evaluated L2 sets or clears it (re-extraction is the resolution
        // path). The write throws if it cannot be persisted — fail closed.
        if (!unsupportedType)
        {
            var l2Ok = await TryEnqueueForApprovalAsync(_approvals, _logger, tenantIdValue, voucherId, userId, fields, applyRequest, ct);
            if (!l2Ok) fields["approval_queue_error"] = "review_queue_unavailable";
            else fields.Remove("approval_queue_error");
            await _documents.SetProcessingBlockerAsync(tenantIdValue, voucherId, ProcessingBlockers.LegacyL2Failure, active: !l2Ok, ct);
        }
        var blockingReasons = await _reviews.GetBlockingReasonsAsync(tenantIdValue, voucherId, ct);
        _logger.LogInformation(
            "Voucher {VoucherId}: tenant={TenantId} department={DepartmentId} operation=extract actor_id={ActorId} runtime_agent_id={RuntimeAgentId} runtime_agent_version={RuntimeAgentVersion} skill_source={SkillSource} workflow_version={WorkflowVersion} document_config_source={Source} review_required={ReviewRequired} review_task_id={ReviewTaskId} review_blocked={ReviewBlocked} blocking_reasons={BlockingReasons} reextract={Reextract}",
            voucherId, tenantIdValue, stub.Voucher.DepartmentId, actorId, executed.AgentId ?? resolution.AgentId, executed.AgentVersion ?? resolution.Version,
            string.Join(",", policy?.TypeConfig?.Skills.Select(s => $"{s.Code}:{s.SourceKind}") ?? []),
            policy?.TypeConfig?.Workflow?.VersionId, resolution.ConfigSource, reviewRequired, reviewTaskId,
            blockingReasons.Count > 0, string.Join(",", blockingReasons), reextract);

        if (!await _vouchers.ReplaceExtractedAsync(tenantIdValue, userId, voucherId, applyRequest, ct))
        {
            // Posted while the Agent was running: the posting wins, drop this result.
            _logger.LogWarning("Voucher {VoucherId} was posted during extraction — result discarded", voucherId);
            return;
        }
        await RecordProvenanceAsync(tenantIdValue, stub.Voucher.DepartmentId, actorId, voucherId, resolution, executed, policy, classification,
            triggerKind, reviewTaskId, ct);

        // Save journal entries if agent produced valid ones. Never for a bank
        // statement: it is N transactions, not one accounting entry, and
        // statement lines must not lead to journals in this phase (lines are
        // identified only — see Banking/IBankStatementRepository).
        if (entries is not null && entries.Count > 0 && !unsupportedType
            && !string.Equals(voucherType, Aiq.Gateway.Vouchers.VoucherTypes.BankStatement, StringComparison.OrdinalIgnoreCase))
        {
            var normalized = NormalizeEntries(entries);
            if (normalized.Count > 0)
            {
                await _vouchers.SaveJournalSuggestionsAsync(
                    tenantIdValue,
                    stub.Voucher.DepartmentId ?? Guid.Empty,
                    voucherId,
                    new SaveJournalSuggestionsRequest(normalized, ReplaceExisting: true),
                    ct);
            }
        }
        // After fields AND journal are written: covers upload and reextract alike.
        await _validation.TryRecordAsync(tenantIdValue, stub.Voucher.DepartmentId, voucherId, ct);

        _logger.LogInformation(
            "Voucher extract job done · id={VoucherId} fields={FieldCount} entries={EntryCount}",
            voucherId, fields.Count, entries?.Count ?? 0);
    }

    internal sealed record ReviewGateResult(Guid? TaskId, Dictionary<string, object?> FieldsBlock, string? Blocker);

    /// <summary>
    /// Workflow require_review: persist ONE open review task and move the
    /// document to awaiting_review (same transaction). On ANY failure the
    /// document is marked review_task_creation_failed and a blocker is
    /// returned; if even that state cannot be written the exception
    /// propagates, so the job fails and the extraction is never stored.
    /// </summary>
    internal static async Task<ReviewGateResult> OpenReviewGateAsync(
        IDocumentReviewRepository reviews, ILogger logger, Guid tenantId, Guid? departmentId, Guid voucherId,
        Guid? agentId, Guid? agentVersionId, ManifestDocumentWorkflow workflow, string documentType, Guid actorUserId,
        CancellationToken ct)
    {
        try
        {
            if (departmentId is not Guid dept) throw new InvalidOperationException("review_task_creation_failed");
            var (task, created) = await reviews.OpenAsync(new DocumentReviewOpenRequest(
                tenantId, dept, voucherId, agentId, agentVersionId, workflow.Id, workflow.VersionId,
                documentType, ["workflow_require_review"], actorUserId), ct);
            return new ReviewGateResult(task.Id, new Dictionary<string, object?>
            {
                ["status"] = DocumentReviewStatuses.AwaitingReview, ["task_id"] = task.Id.ToString(),
                ["workflow_id"] = workflow.Id.ToString(), ["workflow_version_id"] = workflow.VersionId?.ToString(),
                ["workflow_name"] = workflow.Name, ["created"] = created,
            }, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Voucher {VoucherId}: review_task_creation_failed — document blocked", voucherId);
            await reviews.MarkCreationFailedAsync(tenantId, voucherId, ct);
            return new ReviewGateResult(null, new Dictionary<string, object?>
            {
                ["status"] = DocumentReviewStatuses.CreationFailed, ["error"] = "review_task_creation_failed",
            }, "review_task_creation_failed");
        }
    }

    private async Task<DocumentAgentResolution> ResolvePinnedDocumentAgentAsync(
        Guid tenantId, Guid? departmentId, DocumentProvenance provenance, CancellationToken ct)
    {
        // No catch-and-fallback here: a lookup failure propagates (Hangfire
        // retries) instead of ever turning into a legacy run.
        if (departmentId is not Guid dept)
            return new(provenance.AgentId, provenance.AgentVersionId, null, null, null, DocumentProvenanceErrors.ProvenanceInvalid) { Pinned = true };
        return await _documents.ResolvePinnedAsync(tenantId, dept, provenance.AgentId, provenance.AgentVersionId, ct);
    }

    /// <summary>
    /// Explicit extraction failure (agent resolution, provider/runtime call,
    /// unusable output). Persists ONLY a safe code + stage + time: an initial
    /// extraction becomes 'extraction_failed' (retryable); a document that
    /// already has a successful extraction keeps it untouched as the current
    /// result. A failed run row is appended with the real actor. Re-throws if
    /// the failure itself cannot be recorded (Hangfire then retries).
    /// </summary>
    private async Task FailExtractionAsync(Guid tenantId, Guid? departmentId, Guid actorUserId, Guid voucherId,
        DocumentAgentResolution resolution, AgentTaskResult? executed, string errorCode, string errorStage, string triggerKind, CancellationToken ct)
    {
        _logger.LogWarning(
            "Voucher {VoucherId}: extraction_failed tenant={TenantId} department={DepartmentId} actor_id={ActorId} error_stage={ErrorStage} error_code={ErrorCode} agent_id={AgentId} agent_version={AgentVersion} document_config_source={Source} reextract={Reextract}",
            voucherId, tenantId, departmentId, actorUserId, errorStage, errorCode, resolution.AgentId, resolution.Version,
            resolution.ConfigSource, triggerKind == "reextract");
        await _documents.SetExtractionFailureAsync(tenantId, voucherId, errorCode, errorStage, ct);
        await _documents.RecordRunAsync(new DocumentProcessingRun(
            "production", voucherId, tenantId, departmentId, actorUserId, executed?.AgentId ?? resolution.AgentId,
            executed?.AgentVersionId ?? resolution.AgentVersionId, executed?.AgentVersion ?? resolution.Version, resolution.ConfigSource,
            null, null, null, null, [], executed?.Provider, executed?.Model, null, null, null, null, [], false,
            TriggerKind: triggerKind, ErrorCode: errorCode, ErrorStage: errorStage), ct);
    }

    private async Task<DocumentAgentResolution> ResolveDocumentAgentAsync(
        Guid tenantId, Guid? departmentId, Guid userId, Guid voucherId, CancellationToken ct)
    {
        if (departmentId is not Guid dept) return new(null, null, null, null, null, "voucher_has_no_department");
        try
        {
            var requested = await _documents.GetRequestedAgentAsync(voucherId, ct);
            return await _documents.ResolveForUploaderAsync(tenantId, dept, userId, requested, ct);
        }
        catch (Exception ex)
        {
            // Resolution trouble must not block extraction: fall back to the
            // exact legacy call (the Agent resolves the capability itself).
            _logger.LogWarning(ex, "Voucher {VoucherId}: document agent resolution failed — legacy extraction", voucherId);
            return new(null, null, null, null, null, "resolution_failed");
        }
    }

    private async Task RecordProvenanceAsync(
        Guid tenantId, Guid? departmentId, Guid actorUserId, Guid voucherId, DocumentAgentResolution resolution,
        AgentTaskResult executed, DocumentPolicyOutcome? policy, VoucherTypeClassification classification,
        string triggerKind, Guid? reviewTaskId, CancellationToken ct)
    {
        try
        {
            var agentId = executed.AgentId ?? resolution.AgentId;
            var versionId = executed.AgentVersionId ?? resolution.AgentVersionId;
            await _documents.SetVoucherProvenanceAsync(tenantId, voucherId, agentId, versionId, resolution.ConfigSource, ct);
            var type = policy?.TypeConfig;
            await _documents.RecordRunAsync(new DocumentProcessingRun(
                "production", voucherId, tenantId, departmentId, actorUserId, agentId, versionId,
                executed.AgentVersion ?? resolution.Version, resolution.ConfigSource, classification.VoucherType,
                type?.SchemaSha256, type?.Workflow?.Id, type?.Workflow?.VersionId, DocumentExtraction.SkillRefs(type),
                executed.Provider, executed.Model, classification.Source, classification.Status, classification.Confidence,
                policy?.Schema?.Valid, policy?.Schema?.MissingRequired ?? [], policy?.Unsupported ?? false,
                TriggerKind: triggerKind, ReviewTaskId: reviewTaskId), ct);
        }
        catch (Exception ex)
        {
            // The extraction itself is already saved; a provenance write
            // failure is logged loudly rather than re-running the LLM.
            _logger.LogError(ex, "Voucher {VoucherId}: failed to record document processing provenance", voucherId);
        }
    }

    /// <summary>
    /// Sprint 5 HITL: requires_l2_approval in ar_context / ap_context routes
    /// the voucher to the legacy L2 queue (sim_approval_queue). Returns false
    /// when there was something to enqueue and it could NOT be persisted —
    /// the caller then blocks the document instead of continuing silently.
    /// </summary>
    internal static async Task<bool> TryEnqueueForApprovalAsync(
        IApprovalRepository approvals,
        ILogger logger,
        Guid tenantId,
        Guid voucherId,
        Guid userId,
        Dictionary<string, object?> fields,
        CreateVoucherRequest applyRequest,
        CancellationToken ct)
    {
        try
        {
            var triggers = new List<string>();

            // Aggregate flags from both ar_context and ap_context. Chỉ enqueue
            // nếu có ít nhất 1 trigger cứng (L2 threshold, cash > 30M, overdue).
            foreach (var contextKey in new[] { "ar_context", "ap_context", "po_match_context" })
            {
                if (!fields.TryGetValue(contextKey, out var ctxObj) || ctxObj is null) continue;
                var json = System.Text.Json.JsonSerializer.Serialize(ctxObj);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) continue;

                // Check requires_l2_approval boolean
                if (doc.RootElement.TryGetProperty("requires_l2_approval", out var l2El)
                    && l2El.ValueKind == System.Text.Json.JsonValueKind.True)
                {
                    // Collect all risk_flags for context
                    if (doc.RootElement.TryGetProperty("risk_flags", out var flagsEl)
                        && flagsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var f in flagsEl.EnumerateArray())
                        {
                            if (f.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                triggers.Add(f.GetString()!);
                            }
                        }
                    }
                }
            }

            if (triggers.Count == 0) return true;

            await approvals.EnqueueAsync(
                tenantId,
                voucherId,
                applyRequest.VoucherType,
                applyRequest.DocumentNumber,
                applyRequest.TotalAmount,
                applyRequest.Currency ?? "VND",
                triggers.Distinct().ToList(),
                userId,
                ct);

            logger.LogInformation(
                "Voucher {VoucherId} routed to approval queue: {Triggers}",
                voucherId, string.Join(", ", triggers));
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Voucher {VoucherId}: review_queue_unavailable — L2 approval could not be enqueued; document blocked", voucherId);
            return false;
        }
    }

    // R17/R20 fix — route by voucherType instead of unconditionally telling
    // the model "1 hoá đơn GTGT" (a VAT invoice) and to apply the
    // invoice-only `voucher-invoice-vat` skill for every type. The
    // comprehensive `voucher-universal-extractor` skill documents the
    // correct field schema for all 8 real voucher_type values (including
    // invoice_vat_out/in, receipt, payment, bank_transfer, bank_statement,
    // expense_claim, contract) — that is the skill every non-legacy type
    // must use. `voucher-invoice-vat` is kept only for the bare legacy
    // "invoice_vat" discriminator (see the "legacy tenants pre-universal-
    // extractor" comment on the case-switch above), for backward
    // compatibility with pre-migration tenants only.
    private static readonly HashSet<string> LegacyInvoiceOnlyTypes = new(StringComparer.OrdinalIgnoreCase) { "invoice_vat" };

    // ------------------------------------------------------------------
    // Prompt construction — hardening phase (docs/ACCOUNTING_AGENT_AUDIT.md
    // F-04): this used to describe itself as a "mirror of web-tier prompt",
    // but web/src/app/api/vouchers/upload/route.ts's own copy of this same
    // 2-block prompt + regex parser was confirmed 100% unreachable dead
    // code (the P1 async-pipeline rewrite made this Hangfire job the only
    // caller months ago; the web-tier copy was never removed) and has been
    // deleted. This is now the SOLE canonical background-extraction
    // implementation, not one of two.
    //
    // Distinct, intentionally NOT merged with agent/src/aiq_agent/agentic/
    // voucher_tools.py's extract_voucher_fields/suggest_journal_entry MCP
    // tools: those are thin prompt-wrapper tools for the INTERACTIVE,
    // manifest-driven chat/tool-calling flow (a user or the agent itself
    // decides, mid-conversation, to extract/suggest) — a different call
    // pattern from this synchronous background job, which needs the
    // extracted fields back as a structured return value in the same
    // Hangfire step to immediately chain AR/AP context enrichment. Both
    // ultimately persist through the same save_voucher/save_journal_
    // suggestions-equivalent repository calls and the same manifest/tool-
    // grant/HITL governance — this is parallel entry points into one
    // governed system, not competing business logic.
    // ------------------------------------------------------------------
    private static string BuildExtractionPrompt(string? candidateType, bool forced, string rawText)
    {
        var truncated = rawText.Length > 20_000 ? rawText[..20_000] : rawText;
        var voucherType = candidateType ?? "chưa xác định";
        var skillName = candidateType is not null && LegacyInvoiceOnlyTypes.Contains(candidateType) ? "voucher-invoice-vat" : "voucher-universal-extractor";
        var typeInstruction = forced
            ? $"Loại chứng từ đã được ADMIN xác nhận: \"{candidateType}\" — dùng đúng loại này."
            : $"TỰ XÁC ĐỊNH voucher_type từ nội dung (tiêu đề, các dòng thông tin) trong: invoice_vat_out, invoice_vat_in, receipt, payment, bank_transfer, bank_statement, expense_claim, contract. Gợi ý ban đầu: \"{voucherType}\" — có thể SAI, không được lặp lại gợi ý nếu nội dung cho thấy loại khác.";
        return $$"""
Bạn là chuyên viên kế toán VN. Xử lý raw OCR text của 1 chứng từ bên dưới.
{{typeInstruction}}

**BLOCK 1** — Áp dụng skill `{{skillName}}`, đúng phần mô tả cho voucher_type đã xác định, để trích xuất fields.
Trả về JSON object đúng theo schema của skill cho loại chứng từ này — bắt buộc có voucher_type (giá trị bạn xác định)
và document_number, document_date (YYYY-MM-DD) nếu chứng từ có các trường đó. KHÔNG áp dụng schema hoá đơn GTGT
cho một loại chứng từ khác (ví dụ: không suy diễn seller/buyer/line_items cho phiếu thu, đề nghị hoàn ứng, hay
sao kê ngân hàng — dùng đúng field name của loại đó: employee_code/items[] cho expense_claim, payer/payer_mst
cho receipt, v.v. theo đúng skill).

**BLOCK 2** — Áp dụng skill `journal-entry-formatter` + `accounting-vietnam` để đề xuất bút toán N/C theo TT200
phù hợp với voucher_type đã xác định ở BLOCK 1 (không mặc định là hoá đơn mua/bán nếu chứng từ không phải hoá đơn).
Trả về JSON array các entries. Mỗi entry: {entry_no, debit_account, credit_account, amount, description, confidence}.

**QUAN TRỌNG**: chỉ trả 2 code block JSON, KHÔNG kèm markdown khác:
```json
{ ...fields object... }
```
```json
[ ...entries array... ]
```

===== RAW OCR TEXT =====
{{truncated}}
===== END =====
""";
    }

    // ------------------------------------------------------------------
    // JSON block extraction — sole implementation, see BuildExtractionPrompt
    // above (the former web-tier "mirror" was confirmed dead and removed).
    // ------------------------------------------------------------------
    private static readonly Regex FenceRegex = new(
        @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.Compiled);

    internal static (Dictionary<string, object?>? Fields, List<Dictionary<string, object?>>? Entries) ExtractJsonBlocks(string text)
    {
        Dictionary<string, object?>? fields = null;
        List<Dictionary<string, object?>>? entries = null;

        var blocks = new List<string>();
        foreach (Match m in FenceRegex.Matches(text))
        {
            var raw = m.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(raw)) blocks.Add(raw);
        }

        // Fallback: no fenced blocks — greedy match on the largest {...} and
        // [...] structures. Mirrors the web-tier fallback for models that
        // reply with un-fenced JSON.
        if (blocks.Count == 0)
        {
            var greedyObj = Regex.Match(text, @"\{[\s\S]*\}", RegexOptions.Multiline);
            if (greedyObj.Success) blocks.Add(greedyObj.Value);
            var greedyArr = Regex.Match(text, @"\[[\s\S]*\]", RegexOptions.Multiline);
            if (greedyArr.Success) blocks.Add(greedyArr.Value);
        }

        foreach (var raw in blocks)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    var obj = JsonElementToDictionary(root);
                    if (obj.ContainsKey("voucher_type") || obj.ContainsKey("document_number") || obj.ContainsKey("seller_name") || obj.ContainsKey("buyer_mst"))
                    {
                        fields ??= obj;
                    }
                    else if (obj.TryGetValue("entries", out var maybeEntries) && maybeEntries is List<object?> list)
                    {
                        entries ??= list.OfType<Dictionary<string, object?>>().ToList();
                    }
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    var arr = JsonElementToList(root);
                    entries ??= arr.OfType<Dictionary<string, object?>>().ToList();
                }
            }
            catch (JsonException)
            {
                // Skip malformed block — try the next one.
            }
        }
        return (fields, entries);
    }

    private static Dictionary<string, object?> JsonElementToDictionary(JsonElement el)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in el.EnumerateObject())
        {
            dict[prop.Name] = JsonElementToObject(prop.Value);
        }
        return dict;
    }

    private static List<object?> JsonElementToList(JsonElement el)
    {
        var list = new List<object?>();
        foreach (var item in el.EnumerateArray())
        {
            list.Add(JsonElementToObject(item));
        }
        return list;
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var i) ? i : (object)el.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Object => JsonElementToDictionary(el),
        JsonValueKind.Array => JsonElementToList(el),
        _ => null,
    };

    // ------------------------------------------------------------------
    // Field readers
    // ------------------------------------------------------------------
    internal static string? ReadString(Dictionary<string, object?> obj, string key)
        => obj.TryGetValue(key, out var v) && v is string s ? s.Trim() : null;

    private static decimal? ReadDecimal(Dictionary<string, object?> obj, string key)
    {
        if (!obj.TryGetValue(key, out var v) || v is null) return null;
        return v switch
        {
            decimal d => d,
            long i => i,
            int i => i,
            double dbl => (decimal)dbl,
            string s => ParseFlexibleDecimal(s),
            _ => null,
        };
    }

    // Agent may return numbers as VN-formatted strings like "15.000.000",
    // "15,000,000", "15 000 000" or "15tr". Strip separators + suffix
    // before parsing. Fails soft to null so caller can decide.
    internal static decimal? ParseFlexibleDecimal(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();

        // Handle "1.5tr" / "1,5 triệu" — multiplier suffix.
        decimal multiplier = 1m;
        var lower = s.ToLowerInvariant();
        if (lower.EndsWith("tr") || lower.EndsWith("triệu") || lower.EndsWith("trieu"))
        {
            multiplier = 1_000_000m;
            s = Regex.Replace(s, @"\s*(tr|triệu|trieu)\s*$", "", RegexOptions.IgnoreCase);
        }
        else if (lower.EndsWith("k") || lower.EndsWith("nghìn") || lower.EndsWith("nghin"))
        {
            multiplier = 1_000m;
            s = Regex.Replace(s, @"\s*(k|nghìn|nghin)\s*$", "", RegexOptions.IgnoreCase);
        }
        else if (lower.EndsWith("tỷ") || lower.EndsWith("ty"))
        {
            multiplier = 1_000_000_000m;
            s = Regex.Replace(s, @"\s*(tỷ|ty)\s*$", "", RegexOptions.IgnoreCase);
        }

        // Strip currency symbols + spaces.
        s = Regex.Replace(s, @"[₫\s]|VND|VNĐ|USD|EUR|JPY", "", RegexOptions.IgnoreCase).Trim();
        if (s.Length == 0) return null;

        // Decide decimal separator by heuristics:
        //  - "1.234.567,89" → VN → drop dots, replace comma with dot
        //  - "1,234,567.89" → US → drop commas
        //  - "1234567.89"   → already US
        //  - "1234567,89"   → EU → replace comma with dot
        //  - "1234567"      → integer
        var hasDot = s.Contains('.');
        var hasComma = s.Contains(',');
        if (hasDot && hasComma)
        {
            var lastDot = s.LastIndexOf('.');
            var lastComma = s.LastIndexOf(',');
            if (lastDot > lastComma)
            {
                // US: 1,234,567.89 — drop commas
                s = s.Replace(",", "");
            }
            else
            {
                // VN/EU: 1.234.567,89 — drop dots, comma → dot
                s = s.Replace(".", "").Replace(",", ".");
            }
        }
        else if (hasComma)
        {
            // Could be thousands separator "1,234,567" OR EU decimal "1234,89".
            // Heuristic: if there are ≥ 2 commas OR the group after last comma
            // is exactly 3 digits, treat as thousands separator.
            var parts = s.Split(',');
            if (parts.Length > 2 || (parts.Length == 2 && parts[1].Length == 3))
            {
                s = s.Replace(",", "");
            }
            else
            {
                s = s.Replace(",", ".");
            }
        }
        else if (hasDot)
        {
            // "1.234.567" (VN thousands) vs "1234.89" (US decimal).
            var parts = s.Split('.');
            if (parts.Length > 2 || (parts.Length == 2 && parts[1].Length == 3))
            {
                s = s.Replace(".", "");
            }
        }

        return decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed * multiplier
            : null;
    }

    // Accept alternate keys the model sometimes returns.
    internal static string? ReadStringAny(Dictionary<string, object?> obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            var v = ReadString(obj, key);
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    internal static decimal? ReadDecimalAny(Dictionary<string, object?> obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            var v = ReadDecimal(obj, key);
            if (v is not null) return v;
        }
        return null;
    }

    // bool? so callers can distinguish "field absent/unparseable" (null —
    // benefit of the doubt) from an explicit false (the model actually said
    // no receipt attached). Handles a real JSON boolean (the extractor
    // skill's documented shape) and common string fallbacks.
    private static bool? ReadBoolAny(Dictionary<string, object?> obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetValue(key, out var v) || v is null) continue;
            if (v is bool b) return b;
            if (v is string s)
            {
                var norm = s.Trim().ToLowerInvariant();
                if (norm is "true" or "yes" or "có" or "co" or "1") return true;
                if (norm is "false" or "no" or "không" or "khong" or "0") return false;
            }
        }
        return null;
    }

    internal static DateOnly? ReadDate(Dictionary<string, object?> obj, string key)
    {
        var s = ReadString(obj, key);
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateOnly.TryParse(s, out var d) ? d : null;
    }

    // ------------------------------------------------------------------
    // Payload builders
    // ------------------------------------------------------------------
    private static CreateVoucherRequest BuildApplyRequest(
        string voucherType,
        Dictionary<string, object?> fields,
        string rawText)
    {
        var lineItems = fields.TryGetValue("line_items", out var liObj) && liObj is List<object?> liList
            ? liList.OfType<Dictionary<string, object?>>().Select((li, i) => new CreateVoucherLineItemDto(
                Ordinal: (int)(ReadDecimal(li, "ordinal") ?? i + 1),
                ItemCode: ReadString(li, "item_code"),
                Description: (ReadString(li, "description") ?? "").Length > 500
                    ? ReadString(li, "description")![..500]
                    : ReadString(li, "description") ?? "",
                Unit: ReadString(li, "unit"),
                Quantity: ReadDecimal(li, "quantity"),
                UnitPrice: ReadDecimal(li, "unit_price"),
                Subtotal: ReadDecimal(li, "subtotal"),
                VatRate: ReadDecimal(li, "vat_rate"),
                VatAmount: ReadDecimal(li, "vat_amount"),
                TotalAmount: ReadDecimal(li, "total_amount"))).ToList()
            : new List<CreateVoucherLineItemDto>();

        var tags = fields.TryGetValue("tags", out var tagsObj) && tagsObj is List<object?> tagsList
            ? tagsList.OfType<string>().ToArray()
            : Array.Empty<string>();

        // Receipt / payment / bank_transfer vouchers carry payer/payee/amount
        // instead of seller/buyer/total_amount. Money flows payer -> payee,
        // mapped onto the same buyer (pays) -> seller (receives) columns the
        // invoice types use, so the cash-settlement flow and the voucher list
        // see one consistent shape. Which side is MY_COMPANY is decided later
        // from the server-side Company Profile — never from this mapping.
        var resolvedType = (ReadString(fields, "voucher_type") ?? voucherType).ToLowerInvariant();
        var isCash = resolvedType is "receipt" or "payment" or "bank_transfer";
        string? CashOr(string invoiceKey, params string[] cashKeys) =>
            ReadString(fields, invoiceKey) ?? (isCash ? ReadStringAny(fields, cashKeys) : null);

        return new CreateVoucherRequest(
            VoucherType: ReadString(fields, "voucher_type") ?? voucherType,
            DocumentNumber: ReadString(fields, "document_number"),
            DocumentSeries: ReadString(fields, "document_series"),
            DocumentDate: ReadDate(fields, "document_date"),
            SellerName: CashOr("seller_name", "payee_name", "beneficiary_name", "recipient_name", "payee"),
            SellerMst: CashOr("seller_mst", "payee_mst", "beneficiary_mst", "recipient_mst"),
            SellerAddress: ReadString(fields, "seller_address"),
            BuyerName: CashOr("buyer_name", "payer_name", "payer"),
            BuyerMst: CashOr("buyer_mst", "payer_mst"),
            BuyerAddress: ReadString(fields, "buyer_address"),
            PaymentMethod: CashOr("payment_method", "method"),
            Subtotal: ReadDecimal(fields, "subtotal"),
            VatRate: ReadDecimal(fields, "vat_rate"),
            VatAmount: ReadDecimal(fields, "vat_amount"),
            DiscountAmount: ReadDecimal(fields, "discount_amount"),
            TotalAmount: ReadDecimal(fields, "total_amount") ?? (isCash ? ReadDecimalAny(fields, "amount", "so_tien") : null),
            Currency: ReadString(fields, "currency") ?? "VND",
            AmountInWords: ReadString(fields, "amount_in_words"),
            SourceFilePath: null,
            SourceFileName: null,
            SourceMimeType: null,
            RawOcrText: rawText,
            OcrConfidence: null,
            ExtractedFields: fields,
            Tags: tags,
            LineItems: lineItems);
    }

    private List<SaveJournalSuggestionDto> NormalizeEntries(List<Dictionary<string, object?>> entries)
    {
        var list = new List<SaveJournalSuggestionDto>();
        int idx = 0;
        foreach (var e in entries)
        {
            idx++;
            var debit = ReadStringAny(e, "debit_account", "debit", "tk_no", "no");
            var credit = ReadStringAny(e, "credit_account", "credit", "tk_co", "co");
            var amount = ReadDecimalAny(e, "amount", "value", "so_tien") ?? 0m;

            if (string.IsNullOrWhiteSpace(debit) || string.IsNullOrWhiteSpace(credit) || amount <= 0m)
            {
                // Log each drop so we can see agent output format quirks.
                _logger.LogWarning(
                    "Journal entry #{Idx} dropped: debit='{Debit}' credit='{Credit}' amount={Amount} raw={Raw}",
                    idx, debit ?? "?", credit ?? "?", amount,
                    System.Text.Json.JsonSerializer.Serialize(e));
                continue;
            }

            list.Add(new SaveJournalSuggestionDto(
                EntryNo: (int)(ReadDecimalAny(e, "entry_no", "no", "stt") ?? idx),
                DebitAccount: debit,
                CreditAccount: credit,
                Amount: amount,
                Description: ReadStringAny(e, "description", "desc", "dien_giai"),
                Confidence: ReadDecimalAny(e, "confidence")));
        }
        return list;
    }

    // ------------------------------------------------------------------
    // AR + 3-way match enrichment
    // ------------------------------------------------------------------
    private async Task<object?> BuildArContextAsync(Guid tenantId, Guid departmentId, string buyerMst, decimal invoiceTotal, CancellationToken ct)
    {
        try
        {
            var customer = await _customers.FindByMstAsync(tenantId, departmentId, buyerMst, ct);
            if (customer is null)
            {
                return new { status = "customer_not_found", buyer_mst = buyerMst };
            }
            var report = await _customers.GetReceivablesAsync(tenantId, departmentId, customer.Id, ct);
            if (report is null) return new { status = "ar_lookup_failed", customer };

            var projectedBalance = report.CurrentBalance131 + invoiceTotal;
            var projectedPct = report.CreditLimit > 0
                ? (int)Math.Round(projectedBalance / report.CreditLimit * 100m)
                : 0;
            var flags = new List<string>();
            if (invoiceTotal > 500_000_000m) flags.Add("invoice_over_500m_requires_l2_approval");
            if (report.Aging.Over90 > 0) flags.Add("has_overdue_90d");
            if (projectedBalance > report.CreditLimit && report.CreditLimit > 0) flags.Add("would_breach_credit_limit");
            else if (projectedPct >= 80) flags.Add("credit_used_gte_80pct");
            if (report.OverdueAmount > 0) flags.Add("has_overdue_amount");

            return new
            {
                status = "found",
                customer,
                current_balance_131 = report.CurrentBalance131,
                credit_limit = report.CreditLimit,
                credit_used_percent = report.CreditUsedPercent,
                projected_balance_131 = projectedBalance,
                projected_credit_used_percent = projectedPct,
                aging = report.Aging,
                overdue_amount = report.OverdueAmount,
                last_payment_date = report.LastPaymentDate,
                payment_term_default = report.PaymentTermDefault,
                open_invoices = report.OpenInvoices,
                risk_flags = flags,
                requires_l2_approval = flags.Contains("invoice_over_500m_requires_l2_approval")
                    || flags.Contains("would_breach_credit_limit")
                    || flags.Contains("has_overdue_90d"),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AR enrichment failed for MST {Mst}", buyerMst);
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Deterministic receipt matching (FIFO oldest-first) + discount detection.
    //
    // Runs *after* AR lookup so we have the customer's open invoices with
    // due dates. Applies the receipt amount to invoices in due-date order,
    // returning the per-invoice application breakdown plus a discount hint
    // when the receipt is slightly less than the target invoice (< 3%
    // shortfall on a fully-applied line → likely chiết khấu thanh toán sớm).
    //
    // No LLM call — the customer's open A/R is authoritative, so this is
    // a plain accounting rule with no variance.
    // ------------------------------------------------------------------
    private static object? BuildReceiptMatching(object arContext, decimal receiptAmount)
    {
        // arContext is the anonymous object produced by BuildArContextAsync.
        // We reflect over it (or serialize→parse) to get open_invoices.
        var json = System.Text.Json.JsonSerializer.Serialize(arContext);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }
        if (!doc.RootElement.TryGetProperty("open_invoices", out var openEl)
            || openEl.ValueKind != System.Text.Json.JsonValueKind.Array
            || openEl.GetArrayLength() == 0)
        {
            return new
            {
                status = "no_open_invoices",
                receipt_amount = receiptAmount,
                message = "Khách hàng không có hoá đơn chưa thu — thu vào 511/711 (không phải giảm 131).",
            };
        }

        // Snapshot to a mutable list ordered by due date (oldest first).
        var invoices = new List<(string DocumentNumber, DateOnly DocumentDate, DateOnly? DueDate, decimal Remaining)>();
        foreach (var item in openEl.EnumerateArray())
        {
            var docNum = item.TryGetProperty("DocumentNumber", out var dn) ? dn.GetString() ?? "?" : "?";
            var docDate = item.TryGetProperty("DocumentDate", out var dd) && dd.ValueKind == System.Text.Json.JsonValueKind.String
                ? DateOnly.Parse(dd.GetString()!) : DateOnly.MinValue;
            DateOnly? dueDate = null;
            if (item.TryGetProperty("DueDate", out var due) && due.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                dueDate = DateOnly.Parse(due.GetString()!);
            }
            var debit = item.TryGetProperty("Debit", out var deb) ? deb.GetDecimal() : 0m;
            if (debit > 0m)
            {
                invoices.Add((docNum, docDate, dueDate, debit));
            }
        }
        invoices.Sort((a, b) =>
        {
            var byDue = (a.DueDate ?? DateOnly.MaxValue).CompareTo(b.DueDate ?? DateOnly.MaxValue);
            return byDue != 0 ? byDue : a.DocumentDate.CompareTo(b.DocumentDate);
        });

        var applications = new List<object>();
        var remaining = receiptAmount;
        decimal totalApplied = 0m;
        decimal? discount = null;
        string? discountReason = null;
        foreach (var (docNum, docDate, dueDate, invRemaining) in invoices)
        {
            if (remaining <= 0m) break;
            decimal applied;
            decimal invAfter;
            var shortfall = invRemaining - remaining;
            var shortfallPct = invRemaining > 0m ? shortfall / invRemaining : 0m;

            // Chiết khấu thanh toán sớm heuristic: shortfall ≤ 3% AND
            // receipt at least covers 97% AND invoice not yet overdue.
            // Apply full invoice value + book discount as 5211 line.
            var withinDiscountWindow = dueDate is null || DateOnly.FromDateTime(DateTime.UtcNow) <= dueDate;
            if (shortfall > 0m && shortfallPct <= 0.03m && withinDiscountWindow && applications.Count == 0)
            {
                applied = invRemaining;
                invAfter = 0m;
                discount = shortfall;
                discountReason = $"Chiết khấu thanh toán sớm cho HĐ {docNum} ({shortfallPct:P1}).";
                remaining -= remaining;
                totalApplied += invRemaining;
            }
            else
            {
                applied = Math.Min(remaining, invRemaining);
                invAfter = invRemaining - applied;
                remaining -= applied;
                totalApplied += applied;
            }
            applications.Add(new
            {
                document_number = docNum,
                document_date = docDate,
                due_date = dueDate,
                invoice_remaining_before = invRemaining,
                applied,
                invoice_remaining_after = invAfter,
            });
        }

        // If receipt exceeds all open A/R, remainder goes to prepayment (131+).
        string status;
        string message;
        if (remaining > 0m)
        {
            status = "overpaid";
            message = $"Thu dư {remaining:N0} VNĐ — treo vào TK 3387 (KH trả trước) hoặc trả lại.";
        }
        else if (discount is not null)
        {
            status = "matched_with_discount";
            message = discountReason ?? "Áp có chiết khấu.";
        }
        else if (applications.Count == 1 && ((dynamic)applications[0]).invoice_remaining_after == 0m)
        {
            status = "matched_full";
            message = "Thu đủ 1 hoá đơn.";
        }
        else if (applications.Count > 1)
        {
            status = "matched_multiple";
            message = $"Áp cho {applications.Count} hoá đơn (thứ tự due-date).";
        }
        else
        {
            status = "matched_partial";
            message = "Thu 1 phần hoá đơn.";
        }

        return new
        {
            status,
            receipt_amount = receiptAmount,
            total_applied = totalApplied,
            remaining_overpayment = Math.Max(remaining, 0m),
            discount,
            discount_reason = discountReason,
            applications,
            message,
        };
    }

    private async Task<object?> BuildMatchContextAsync(Guid tenantId, Guid departmentId, string documentNumber, CancellationToken ct)
    {
        try
        {
            var match = await _customers.MatchInvoiceToSalesOrderAsync(tenantId, departmentId, documentNumber, ct);
            if (match is null)
            {
                return new { status = "no_match", document_number = documentNumber };
            }
            return new
            {
                status = "found",
                quality = match.MatchQuality,
                sales_order = match.SalesOrder,
                delivery_note = match.DeliveryNote,
                contract = match.Contract,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "3-way match failed for doc {Doc}", documentNumber);
            return null;
        }
    }

    // AP 3-way match: invoice_vat_in → PO → GRN. Purchase contract sẽ thêm Sprint 5.
    private async Task<object?> BuildPoMatchContextAsync(Guid tenantId, Guid departmentId, string documentNumber, CancellationToken ct)
    {
        try
        {
            var match = await _suppliers.MatchInvoiceToPurchaseOrderAsync(tenantId, departmentId, documentNumber, ct);
            if (match is null)
            {
                return new { status = "no_match", document_number = documentNumber };
            }
            return new
            {
                status = "found",
                quality = match.MatchQuality,
                purchase_order = match.PurchaseOrder,
                goods_received_note = match.GoodsReceivedNote,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AP 3-way match failed for doc {Doc}", documentNumber);
            return null;
        }
    }

    // ------------------------------------------------------------------
    // AP enrichment (mirror BuildArContextAsync).
    //
    // Đầu vào: seller_mst (invoice_vat_in) / beneficiary_mst (bank_transfer) /
    // payee_mst (payment). Trả về NCC + current_ap + aging + 5 risk flags.
    //
    // paymentMethod dùng để trigger TT78 flag khi HĐ đầu vào > 30M mà thanh
    // toán TM (không có chứng từ NH → không được khấu trừ VAT).
    // ------------------------------------------------------------------
    private async Task<object?> BuildApContextAsync(
        Guid tenantId, Guid departmentId, string supplierMst, decimal invoiceTotal, string? paymentMethod, CancellationToken ct)
    {
        try
        {
            var supplier = await _suppliers.FindByMstAsync(tenantId, departmentId, supplierMst, ct);
            if (supplier is null)
            {
                return new { status = "supplier_not_found", supplier_mst = supplierMst };
            }
            var report = await _suppliers.GetPayablesAsync(tenantId, departmentId, supplier.Id, ct);
            if (report is null) return new { status = "ap_lookup_failed", supplier };

            var projectedBalance = report.CurrentBalance331 + invoiceTotal;
            var flags = new List<string>();

            // TT78 điều 6: HĐ đầu vào > threshold + thanh toán TM → không khấu trừ VAT.
            var isCash = paymentMethod is not null
                && (paymentMethod.Equals("TM", StringComparison.OrdinalIgnoreCase)
                    || paymentMethod.Contains("tiền mặt", StringComparison.OrdinalIgnoreCase)
                    || paymentMethod.Contains("cash", StringComparison.OrdinalIgnoreCase));
            if (invoiceTotal > _riskOptions.CashPaymentVatDeductibleThreshold && isCash)
            {
                flags.Add("expense_over_30m_cash_payment");
            }

            if (invoiceTotal > _riskOptions.L2ApprovalAbove) flags.Add("expense_over_500m_requires_l2_approval");
            if (report.Aging.Over90 > 0) flags.Add("has_overdue_90d");
            if (report.OverdueAmount > 0) flags.Add("has_overdue_payable");

            // First transaction: current_ap = 0 và không có ledger row nào.
            if (supplier.CurrentAp == 0m && report.OpenInvoices.Count == 0)
            {
                flags.Add("first_transaction_with_supplier");
            }

            return new
            {
                status = "found",
                supplier,
                current_balance_331 = report.CurrentBalance331,
                projected_balance_331 = projectedBalance,
                aging = report.Aging,
                overdue_amount = report.OverdueAmount,
                last_payment_date = report.LastPaymentDate,
                payment_term_default = report.PaymentTermDefault,
                open_invoices = report.OpenInvoices,
                risk_flags = flags,
                requires_l2_approval = flags.Contains("expense_over_500m_requires_l2_approval")
                    || flags.Contains("expense_over_30m_cash_payment")
                    || flags.Contains("has_overdue_90d"),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AP enrichment failed for MST {Mst}", supplierMst);
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Deterministic payment matching (FIFO oldest-first).
    //
    // Mirror BuildReceiptMatching nhưng KHÔNG auto-detect chiết khấu — bên AP
    // chiết khấu thanh toán được hưởng thường negotiate với NCC, không auto
    // suy diễn từ shortfall. Nếu LLM extract explicit `discount` thì caller
    // sẽ handle riêng (Sprint 2 apply-expense endpoint).
    // ------------------------------------------------------------------
    private static object? BuildPaymentMatching(object apContext, decimal paymentAmount)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(apContext);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

        if (!doc.RootElement.TryGetProperty("open_invoices", out var openEl)
            || openEl.ValueKind != System.Text.Json.JsonValueKind.Array
            || openEl.GetArrayLength() == 0)
        {
            return new
            {
                status = "no_open_payables",
                payment_amount = paymentAmount,
                message = "NCC không có HĐ chưa trả — chi ứng trước / prepayment (treo TK 331 dư Nợ).",
            };
        }

        // AP open payables: credit > 0, debit = 0. Sort by due date oldest.
        var payables = new List<(string DocumentNumber, DateOnly DocumentDate, DateOnly? DueDate, decimal Remaining)>();
        foreach (var item in openEl.EnumerateArray())
        {
            var docNum = item.TryGetProperty("DocumentNumber", out var dn) ? dn.GetString() ?? "?" : "?";
            var docDate = item.TryGetProperty("DocumentDate", out var dd) && dd.ValueKind == System.Text.Json.JsonValueKind.String
                ? DateOnly.Parse(dd.GetString()!) : DateOnly.MinValue;
            DateOnly? dueDate = null;
            if (item.TryGetProperty("DueDate", out var due) && due.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                dueDate = DateOnly.Parse(due.GetString()!);
            }
            var credit = item.TryGetProperty("Credit", out var cr) ? cr.GetDecimal() : 0m;
            if (credit > 0m)
            {
                payables.Add((docNum, docDate, dueDate, credit));
            }
        }
        payables.Sort((a, b) =>
        {
            var byDue = (a.DueDate ?? DateOnly.MaxValue).CompareTo(b.DueDate ?? DateOnly.MaxValue);
            return byDue != 0 ? byDue : a.DocumentDate.CompareTo(b.DocumentDate);
        });

        var applications = new List<object>();
        var remaining = paymentAmount;
        decimal totalApplied = 0m;
        foreach (var (docNum, docDate, dueDate, invRemaining) in payables)
        {
            if (remaining <= 0m) break;
            var applied = Math.Min(remaining, invRemaining);
            var invAfter = invRemaining - applied;
            remaining -= applied;
            totalApplied += applied;
            applications.Add(new
            {
                document_number = docNum,
                document_date = docDate,
                due_date = dueDate,
                invoice_remaining_before = invRemaining,
                applied,
                invoice_remaining_after = invAfter,
            });
        }

        string status;
        string message;
        if (remaining > 0m)
        {
            status = "overpaid";
            message = $"Chi dư {remaining:N0} VNĐ — treo TK 331 dư Nợ (ứng trước NCC).";
        }
        else if (applications.Count == 1 && ((dynamic)applications[0]).invoice_remaining_after == 0m)
        {
            status = "matched_full";
            message = "Trả đủ 1 hoá đơn NCC.";
        }
        else if (applications.Count > 1)
        {
            status = "matched_multiple";
            message = $"Áp trả {applications.Count} hoá đơn (thứ tự due-date).";
        }
        else
        {
            status = "matched_partial";
            message = "Trả 1 phần hoá đơn.";
        }

        return new
        {
            status,
            payment_amount = paymentAmount,
            total_applied = totalApplied,
            remaining_overpayment = Math.Max(remaining, 0m),
            applications,
            message,
        };
    }

    // ------------------------------------------------------------------
    // Expense claim matching (hoàn ứng nhân viên).
    //
    // Tra sim_employees theo code → tính reimbursement + budget check +
    // per-category journal breakdown. Category mapping (VN accounting):
    //   airfare/hotel/meal/transport/lưu trú/vé/đi lại → Nợ 6417 (BH)
    //     hoặc 6428 (QLDN) tuỳ dept của nhân viên
    //   gift/entertainment/quà/tiếp khách → Nợ 6428 + TT96 warning
    //     (giới hạn 15% doanh thu, cần theo dõi riêng)
    // Có 141 = advance_covered, Có 111 = remaining_to_reimburse.
    // ------------------------------------------------------------------
    private async Task<object?> BuildExpenseClaimMatchingAsync(
        Guid tenantId,
        string employeeCode,
        decimal totalClaim,
        decimal advanceTaken,
        List<Dictionary<string, object?>>? items,
        CancellationToken ct)
    {
        try
        {
            var emp = await _employees.FindByCodeAsync(tenantId, employeeCode, ct);
            if (emp is null)
            {
                return new { status = "employee_not_found", employee_code = employeeCode };
            }

            var advanceCovered = Math.Min(totalClaim, emp.OutstandingAdvance);
            var remainingToReimburse = Math.Max(0m, totalClaim - advanceCovered);

            var annualCeiling = emp.MonthlyExpenseBudget * 12m;
            var budgetExceeded = annualCeiling > 0m && (emp.YtdExpense + totalClaim) > annualCeiling;

            // Default debit account by department. Sales dept → 6417, others → 6428.
            var isSalesDept = !string.IsNullOrEmpty(emp.Department)
                && emp.Department.Contains("Kinh doanh", StringComparison.OrdinalIgnoreCase);
            var defaultDebitAcct = isSalesDept ? "6417" : "6428";

            // Parse items[] → per-category breakdown.
            var breakdown = new List<object>();
            var normalizedItems = new List<object>();
            var hasTT96Category = false;
            if (items is { Count: > 0 })
            {
                var groups = new Dictionary<string, (string acct, decimal amount, bool tt96)>();
                foreach (var it in items)
                {
                    var rawCategory = ReadStringAny(it, "category", "loai", "loai_chi_phi") ?? "khác";
                    var description = ReadStringAny(it, "description", "noi_dung", "dien_giai") ?? "";
                    var amount = ReadDecimalAny(it, "amount", "so_tien", "total_amount") ?? 0m;
                    var receipt = ReadStringAny(it, "receipt_attached", "chung_tu") ?? "";

                    var (acct, tt96) = MapCategoryToAccount(rawCategory, defaultDebitAcct);
                    if (tt96) hasTT96Category = true;

                    normalizedItems.Add(new
                    {
                        category = rawCategory,
                        description,
                        amount,
                        receipt_attached = receipt,
                        journal_account = acct,
                        tt96_flagged = tt96,
                    });

                    var key = $"{acct}:{rawCategory}";
                    if (groups.TryGetValue(key, out var existing))
                    {
                        groups[key] = (acct, existing.amount + amount, tt96);
                    }
                    else
                    {
                        groups[key] = (acct, amount, tt96);
                    }
                }
                foreach (var (key, val) in groups)
                {
                    breakdown.Add(new
                    {
                        debit_account = val.acct,
                        category = key.Split(':', 2)[1],
                        amount = val.amount,
                        tt96_flagged = val.tt96,
                    });
                }
            }
            else
            {
                // Fallback nếu không có items — 1 line duy nhất bằng totalClaim.
                breakdown.Add(new
                {
                    debit_account = defaultDebitAcct,
                    category = "khác",
                    amount = totalClaim,
                    tt96_flagged = false,
                });
            }

            var journalHint = advanceCovered > 0m
                ? $"Nợ {defaultDebitAcct} {totalClaim:N0} / Có 141 {advanceCovered:N0} + Có 111 {remainingToReimburse:N0}"
                : $"Nợ {defaultDebitAcct} {totalClaim:N0} / Có 111 {totalClaim:N0}";

            return new
            {
                status = "found",
                employee = emp,
                total_claim = totalClaim,
                advance_taken = advanceTaken,
                advance_covered = advanceCovered,
                remaining_to_reimburse = remainingToReimburse,
                annual_ceiling = annualCeiling,
                budget_exceeded = budgetExceeded,
                default_debit_account = defaultDebitAcct,
                journal_breakdown = breakdown,
                items = normalizedItems,
                has_tt96_category = hasTT96Category,
                suggested_journal_hint = journalHint,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Expense claim enrichment failed for employee {Code}", employeeCode);
            return null;
        }
    }

    // Category → account map. Returns (account, tt96_flag).
    // Gift/entertainment fixed to 6428 with TT96 warning (khấu trừ tối đa 15%
    // doanh thu). Other categories fall through to caller's default.
    private static (string Account, bool Tt96Flag) MapCategoryToAccount(string category, string defaultAcct)
    {
        var c = category.ToLowerInvariant();
        // TT96 nhóm — gift/entertainment/tiếp khách
        if (c.Contains("gift") || c.Contains("quà") || c.Contains("qua")
            || c.Contains("entertainment") || c.Contains("tiếp khách") || c.Contains("tiep khach"))
        {
            return ("6428", true);
        }
        // Travel expenses — use dept default (6417 sales, 6428 others)
        if (c.Contains("airfare") || c.Contains("vé máy bay") || c.Contains("ve may bay")
            || c.Contains("hotel") || c.Contains("khách sạn") || c.Contains("khach san") || c.Contains("lưu trú") || c.Contains("luu tru")
            || c.Contains("meal") || c.Contains("ăn uống") || c.Contains("an uong") || c.Contains("food")
            || c.Contains("transport") || c.Contains("đi lại") || c.Contains("di lai") || c.Contains("taxi"))
        {
            return (defaultAcct, false);
        }
        return (defaultAcct, false);
    }
}

public interface IVoucherExtractJobScheduler
{
    string Enqueue(
        Guid voucherId,
        Guid userId,
        Guid? tenantId,
        string voucherType,
        string userToken,
        string parentJobId);

    // Immediate enqueue — used when user manually retries extraction via
    // the detail-page button, no parent OCR job to chain on.
    string EnqueueImmediate(
        Guid voucherId,
        Guid userId,
        Guid? tenantId,
        string voucherType,
        string userToken,
        Guid requestedBy);
}

public sealed class VoucherExtractJobScheduler : IVoucherExtractJobScheduler
{
    private readonly IBackgroundJobClient _hangfire;
    private readonly ILogger<VoucherExtractJobScheduler> _logger;

    public VoucherExtractJobScheduler(IBackgroundJobClient hangfire, ILogger<VoucherExtractJobScheduler> logger)
    {
        _hangfire = hangfire;
        _logger = logger;
    }

    public string EnqueueImmediate(
        Guid voucherId,
        Guid userId,
        Guid? tenantId,
        string voucherType,
        string userToken,
        Guid requestedBy)
    {
        // requestedBy: the endpoint's authenticated principal — never client input.
        var jobId = _hangfire.Enqueue<VoucherExtractJobRunner>(
            runner => runner.RunForActorAsync(voucherId, userId, tenantId, voucherType, userToken, requestedBy, CancellationToken.None));
        _logger.LogInformation(
            "Enqueued immediate voucher extract job {JobId} for voucher {VoucherId}",
            jobId, voucherId);
        return jobId;
    }

    public string Enqueue(
        Guid voucherId,
        Guid userId,
        Guid? tenantId,
        string voucherType,
        string userToken,
        string parentJobId)
    {
        // Chain: run only after OCR job finishes (succeeded state).
        // If OCR failed, extraction job stays queued but the runner
        // itself will short-circuit on status='ocr_unavailable'.
        var jobId = _hangfire.ContinueJobWith<VoucherExtractJobRunner>(
            parentJobId,
            runner => runner.RunAsync(voucherId, userId, tenantId, voucherType, userToken, CancellationToken.None));
        _logger.LogInformation(
            "Chained voucher extract job {JobId} after OCR job {ParentJobId}",
            jobId, parentJobId);
        return jobId;
    }
}
