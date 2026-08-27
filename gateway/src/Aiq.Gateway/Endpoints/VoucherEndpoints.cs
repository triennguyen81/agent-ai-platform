using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Books;
using Aiq.Gateway.Middleware;
using Aiq.Gateway.Storage;
using Aiq.Gateway.Vouchers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Aiq.Gateway.Endpoints;

public static class VoucherEndpoints
{
    private static readonly string[] ImageContentTypes =
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/webp",
        "image/bmp",
        "image/tiff",
        "image/heic",
    };

    public static IEndpointRouteBuilder MapVoucherEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app
            .MapGroup("/vouchers")
            .RequireAuthorization("resource_or_tool")
            .RequireRateLimiting("default");

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapPost("/upload", UploadAsync).DisableAntiforgery();
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPatch("/{id:guid}", UpdateAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);

        group.MapPost("/{id:guid}/journal-suggestions", SaveJournalAsync);
        group.MapPost("/{id:guid}/journal-suggestions/{entryId:guid}/decision", DecideJournalAsync);

        // Preview: streams the uploaded voucher file through the gateway
        // so the browser never needs a direct route to MinIO. Signed URLs
        // would offload the bytes but require MinIO to be publicly
        // reachable + SSL — more infra work for no MVP-critical gain.
        group.MapGet("/{id:guid}/preview", GetPreviewAsync);

        // Idempotent "fill in extracted fields on an existing stub row" —
        // used by the agent's save_voucher tool after OCR + LLM extract so
        // the upload flow doesn't create a duplicate row.
        group.MapPatch("/{id:guid}/apply-extraction", ApplyExtractionAsync);

        return app;
    }

    // ------------------------------------------------------------------
    // Sync upload: image → OCR → return voucherId + raw_text. Extraction
    // + save is done by the web tier via /agent/run with save_voucher tool
    // (mirrors the contract upload flow).
    // ------------------------------------------------------------------
    private const long MaxUploadBytes = 25L * 1024 * 1024;  // 25 MB per voucher

    private static async Task<IResult> UploadAsync(
        IFormFile file,
        IAgentClient agent,
        IVoucherRepository repo,
        IObjectStorage storage,
        IOptions<S3Options> s3Options,
        HttpContext ctx,
        CancellationToken ct,
        string voucherType = "invoice_vat")
    {
        if (file is null || file.Length == 0)
        {
            return Results.BadRequest(new { error = "file trống" });
        }
        if (file.Length > MaxUploadBytes)
        {
            return Results.BadRequest(new { error = $"file quá lớn (>25MB): {file.Length} bytes" });
        }
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        var tenantId = TenantContext.Require(ctx);

        var contentType = (file.ContentType ?? "").ToLowerInvariant();
        var isImage = ImageContentTypes.Contains(contentType);
        var isPdf = contentType == "application/pdf" || file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        if (!isImage && !isPdf)
        {
            return Results.BadRequest(new { error = $"content-type '{contentType}' không hỗ trợ. Dùng image/* hoặc application/pdf." });
        }

        var extension = Path.GetExtension(file.FileName);
        if (string.IsNullOrEmpty(extension))
        {
            extension = isImage ? ".jpg" : ".pdf";
        }

        // ---------------------------------------------------------------
        // E-Step 4 — upload to MinIO/S3 instead of the local Docker
        // volume. Key layout: vouchers/{userId}/{uuid}{ext}. The user
        // prefix lets us implement "delete all files for user X" cheaply
        // (S3 list + delete under prefix) for future GDPR-style requests.
        // Buffer the incoming stream into memory (bounded to 25MB by
        // MaxUploadBytes above) so we can push it to S3 AND re-read it
        // for the OCR call without a second network round-trip.
        // ---------------------------------------------------------------
        var s3Key = TenantObjectKey.Build(tenantId, "vouchers", userId.ToString("N"), $"{Guid.NewGuid():N}{extension}");
        var bucket = s3Options.Value.VoucherBucket;

        byte[] fileBytes;
        await using (var buffer = new MemoryStream(capacity: (int)file.Length))
        {
            await file.CopyToAsync(buffer, ct);
            fileBytes = buffer.ToArray();
        }

        try
        {
            await using var s3UploadStream = new MemoryStream(fileBytes, writable: false);
            await storage.PutAsync(bucket, s3Key, s3UploadStream, contentType, ct);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                type: "https://aiq.local/problems/storage",
                title: "Object storage upload failed",
                statusCode: StatusCodes.Status502BadGateway,
                detail: ex.Message);
        }

        // The stub still records `source_file_path` = the S3 key so
        // downstream code doesn't have to distinguish local vs S3
        // paths. Migration script + serving layer will parse the
        // vouchers/... prefix to detect S3-hosted files.
        var voucherId = await repo.CreateStubAsync(
            tenantId,
            userId,
            voucherType,
            s3Key,
            file.FileName,
            contentType,
            ct);

        // Run OCR inline (typical <10s for 1 image).
        // If PaddleOCR isn't installed in the agent runtime we degrade
        // gracefully: return the staged file's voucherId with status
        // 'ocr_unavailable' so the web tier can prompt the user to paste
        // raw text manually and drive extraction that way.
        string joinedText = string.Empty;
        decimal avgConfidence = 0m;
        var responseStatus = "ocr_complete";
        string? ocrError = null;
        try
        {
            await using var ocrStream = new MemoryStream(fileBytes, writable: false);
            var ocr = isImage
                ? await agent.RunOcrImageAsync(ocrStream, file.FileName, contentType, ct)
                : await agent.RunOcrPipelineAsync(ocrStream, file.FileName, ct);
            joinedText = ocr.JoinedText ?? string.Empty;
            avgConfidence = (decimal)ocr.AverageConfidence;
        }
        catch (AgentRequestException ex)
        {
            // Any OCR failure (503 = pipeline down, 500 = runtime bug like
            // PaddleOCR 3.x mkldnn crash) degrades to ocr_unavailable —
            // the file is safely in S3, the voucher stub is created, and
            // the web tier's paste-text UI lets the user drive extraction
            // without OCR. Rejecting the whole upload would waste the
            // stub row and force the user to re-upload.
            responseStatus = "ocr_unavailable";
            ocrError = ex.UpstreamStatus == 503
                ? "OCR pipeline chưa khả dụng. Vui lòng paste raw text tay để extract."
                : $"OCR runtime lỗi ({ex.UpstreamStatus}). Vui lòng paste raw text tay để extract.";
        }
        await repo.UpdateAsync(
            tenantId,
            userId,
            voucherId,
            new UpdateVoucherRequest(
                Status: responseStatus,
                DocumentNumber: null, DocumentSeries: null, DocumentDate: null,
                SellerName: null, SellerMst: null, SellerAddress: null,
                BuyerName: null, BuyerMst: null, BuyerAddress: null,
                PaymentMethod: null, Subtotal: null, VatRate: null, VatAmount: null,
                DiscountAmount: null, TotalAmount: null, Currency: null,
                AmountInWords: null,
                ErrorMessage: ocrError,
                Tags: null),
            ct);

        return Results.Ok(new
        {
            voucherId,
            status = responseStatus,
            rawText = joinedText,
            ocrConfidence = avgConfidence,
            ocrError,
        });
    }

    private static async Task<IResult> ListAsync(
        IVoucherRepository repo,
        HttpContext ctx,
        CancellationToken ct,
        string? voucherType = null,
        string? status = null,
        string? q = null,
        int limit = 50,
        int offset = 0)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        var tenantId = TenantContext.Require(ctx);

        var items = await repo.ListAsync(tenantId, userId, voucherType, status, q, limit, offset, ct);
        return Results.Ok(new VouchersListResponse(items, items.Count));
    }

    private static async Task<IResult> CreateAsync(
        CreateVoucherRequest request,
        IVoucherRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryValidate(request, out var errors))
        {
            return Results.ValidationProblem(errors);
        }
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        var tenantId = TenantContext.Require(ctx);

        var created = await repo.CreateAsync(tenantId, userId, request, ct);
        return Results.Created($"/vouchers/{created.Voucher.Id}", created);
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        IVoucherRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        var tenantId = TenantContext.Require(ctx);

        var voucher = await repo.FindAsync(tenantId, userId, id, ct);
        return voucher is null ? Results.NotFound() : Results.Ok(voucher);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateVoucherRequest request,
        IVoucherRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        var tenantId = TenantContext.Require(ctx);

        var updated = await repo.UpdateAsync(tenantId, userId, id, request, ct);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    // ApplyExtractionAsync — used by the agent's save_voucher tool AFTER
    // the web upload flow has staged an empty voucher stub. Replaces all
    // extracted fields + line items on the existing row (via
    // ReplaceExtractedAsync) and marks it status='extracted'. Body is
    // the same shape as CreateVoucherRequest, minus source_* fields
    // which were set at stub time.
    // GET /vouchers/{id}/preview
    // Streams the uploaded file from MinIO back to the browser. Wraps
    // the S3 GetObject stream through our own auth layer so voucher
    // access still goes through the standard ACL check.
    //
    // If the row's source_file_path is a legacy local path (not an S3
    // key), we fall back to reading from the local upload volume so
    // pre-migration rows still render.
    private static async Task<IResult> GetPreviewAsync(
        Guid id,
        IVoucherRepository repo,
        IObjectStorage storage,
        IOptions<S3Options> s3Options,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        var tenantId = TenantContext.Require(ctx);

        var voucher = await repo.FindAsync(tenantId, userId, id, ct);
        if (voucher is null)
        {
            return Results.NotFound();
        }
        var path = voucher.Voucher.SourceFilePath;
        if (string.IsNullOrEmpty(path))
        {
            return Results.NotFound(new { error = "voucher has no source file" });
        }
        var contentType = voucher.Voucher.SourceMimeType ?? "application/octet-stream";
        var downloadName = voucher.Voucher.SourceFileName ?? "voucher";

        // S3-hosted rows use the vouchers/... prefix from the E-4 upload
        // path. Everything else is a legacy /var/aiq/uploads/... path
        // from before the migration.
        var isTenantObject = TenantObjectKey.BelongsTo(tenantId, path);
        var isOwnedLegacyObject = path.StartsWith($"vouchers/{userId:N}/", StringComparison.Ordinal);
        if (isTenantObject || isOwnedLegacyObject)
        {
            var signedUrl = await storage.GetSignedUrlAsync(
                s3Options.Value.VoucherBucket,
                path,
                TimeSpan.FromMinutes(2),
                ct);

            // Redirect the browser to the signed URL directly. Keeps
            // gateway CPU/memory idle even for large PDFs — MinIO
            // serves the bytes. Requires Object__PublicUrlPrefix to be
            // set so the URL is reachable from outside Docker net;
            // otherwise fall through to streaming.
            if (!string.IsNullOrEmpty(s3Options.Value.PublicUrlPrefix))
            {
                return Results.Redirect(signedUrl, permanent: false);
            }

            // No public prefix — stream through gateway.
            using var http = new HttpClient();
            var res = await http.GetAsync(signedUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode)
            {
                return Results.Problem(
                    title: "Preview fetch failed",
                    statusCode: (int)res.StatusCode);
            }
            var stream = await res.Content.ReadAsStreamAsync(ct);
            return Results.File(stream, contentType, downloadName);
        }

        // Legacy local-file path — read from Docker volume mount.
        if (!File.Exists(path))
        {
            return Results.NotFound(new { error = "local file missing (migrate to S3 or re-upload)" });
        }
        return Results.File(File.OpenRead(path), contentType, downloadName);
    }

    private static async Task<IResult> ApplyExtractionAsync(
        Guid id,
        CreateVoucherRequest request,
        IVoucherRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryValidate(request, out var errors))
        {
            return Results.ValidationProblem(errors);
        }
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        var tenantId = TenantContext.Require(ctx);

        // Ownership check — 404 if the caller doesn't own the stub.
        var existing = await repo.FindAsync(tenantId, userId, id, ct);
        if (existing is null)
        {
            return Results.NotFound();
        }

        await repo.ReplaceExtractedAsync(tenantId, userId, id, request, ct);
        var updated = await repo.FindAsync(tenantId, userId, id, ct);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        IVoucherRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        var tenantId = TenantContext.Require(ctx);

        var deleted = await repo.DeleteAsync(tenantId, userId, id, ct);
        return deleted ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> SaveJournalAsync(
        Guid id,
        SaveJournalSuggestionsRequest request,
        IVoucherRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        if (request?.Entries is null || request.Entries.Count == 0)
        {
            return Results.BadRequest(new { error = "entries required" });
        }

        var tenantId = TenantContext.Require(ctx);
        if (await repo.FindAsync(tenantId, userId, id, ct) is null) return Results.NotFound();
        var saved = await repo.SaveJournalSuggestionsAsync(tenantId, userId, id, request, ct);
        return Results.Ok(saved);
    }

    private static async Task<IResult> DecideJournalAsync(
        Guid id,
        Guid entryId,
        AcceptJournalRequest request,
        IVoucherRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }

        var updated = await repo.AcceptJournalAsync(TenantContext.Require(ctx), id, entryId, userId, request, ct);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    private static bool TryGetUserId(HttpContext ctx, out Guid userId)
    {
        var raw = ctx.User.FindFirst("sub")?.Value
            ?? ctx.User.FindFirst("nameidentifier")?.Value
            ?? ctx.User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
        return Guid.TryParse(raw, out userId);
    }

    private static bool TryValidate(object instance, out Dictionary<string, string[]> errors)
    {
        var validationCtx = new ValidationContext(instance);
        var results = new List<ValidationResult>();
        var ok = Validator.TryValidateObject(instance, validationCtx, results, validateAllProperties: true);
        errors = results
            .SelectMany(r => r.MemberNames.Select(m => (Member: m, r.ErrorMessage)))
            .GroupBy(t => t.Member)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage ?? "Invalid").ToArray());
        return ok;
    }
}
