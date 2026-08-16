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
        // Any authenticated tenant member (role >= user) can upload +
        // work with vouchers — that's the daily job of kế toán viên.
        // Repository queries are tenant-scoped so no cross-tenant leak.
        var group = app
            .MapGroup("/vouchers")
            .RequireAuthorization("user")
            .RequireRateLimiting("default");

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapPost("/upload", UploadAsync).DisableAntiforgery();
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPatch("/{id:guid}", UpdateAsync);

        // Destructive delete stays admin-only.
        group.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization("admin");

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

        // Manual re-extract: user clicks "Trích xuất" button on detail page
        // when the automated pipeline failed. Enqueues the same Hangfire
        // extract job (returns fast) instead of running LLM inline. Optional
        // rawText body overrides raw_ocr_text for the paste-fallback flow.
        group.MapPost("/{id:guid}/reextract", ReextractAsync);

        return app;
    }

    private sealed record ReextractRequest(string? RawText, string? VoucherType);

    private static async Task<IResult> ReextractAsync(
        Guid id,
        ReextractRequest? request,
        IVoucherRepository repo,
        Aiq.Gateway.Jobs.IVoucherExtractJobScheduler extractScheduler,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }
        TenantContext.TryGet(ctx, out var tenantId);

        var voucher = await repo.FindAsync(tenantId, userId, id, ct);
        if (voucher is null)
        {
            return Results.NotFound();
        }

        // If caller provided raw text (paste-fallback flow), stash it into
        // the voucher first so the extract job reads it from DB just like
        // OCR-populated rows.
        var newRawText = request?.RawText?.Trim() ?? "";
        if (newRawText.Length >= 20)
        {
            await repo.UpdateOcrResultAsync(
                id,
                status: "ocr_complete",
                rawText: newRawText,
                ocrConfidence: 1.0m,   // user-verified text is 100% by definition
                errorMessage: null,
                ct);
        }

        var voucherType = string.IsNullOrWhiteSpace(request?.VoucherType)
            ? voucher.Voucher.VoucherType
            : request!.VoucherType!.Trim();
        var userToken = ExtractBearerToken(ctx);
        var jobId = extractScheduler.EnqueueImmediate(id, userId, tenantId, voucherType, userToken);

        return Results.Ok(new
        {
            voucherId = id,
            status = "processing",
            extractJobId = jobId,
        });
    }

    // ------------------------------------------------------------------
    // Sync upload: image → OCR → return voucherId + raw_text. Extraction
    // + save is done by the web tier via /agent/run with save_voucher tool
    // (mirrors the contract upload flow).
    // ------------------------------------------------------------------
    private const long MaxUploadBytes = 25L * 1024 * 1024;  // 25 MB per voucher

    // UC-ACC-001 P1: async upload. File goes to S3, Hangfire job runs OCR
    // in the background. Response returns in <5s so Cloudflare doesn't cut
    // us off. Web tier polls voucher detail until status='ocr_complete',
    // then drives the LLM extraction step.
    private static async Task<IResult> UploadAsync(
        IFormFile file,
        IVoucherRepository repo,
        IObjectStorage storage,
        Aiq.Gateway.Jobs.IVoucherOcrJobScheduler ocrScheduler,
        Aiq.Gateway.Jobs.IVoucherExtractJobScheduler extractScheduler,
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
        TenantContext.TryGet(ctx, out var tenantId);

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

        var s3Key = $"vouchers/{userId:N}/{Guid.NewGuid():N}{extension}";
        var bucket = s3Options.Value.VoucherBucket;

        try
        {
            // Stream directly to S3 — no need to buffer the full file in
            // memory anymore since OCR happens in a separate process later.
            await using var readStream = file.OpenReadStream();
            await storage.PutAsync(bucket, s3Key, readStream, contentType, ct);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                type: "https://aiq.local/problems/storage",
                title: "Object storage upload failed",
                statusCode: StatusCodes.Status502BadGateway,
                detail: ex.Message);
        }

        var voucherId = await repo.CreateStubAsync(
            tenantId,
            userId,
            voucherType,
            s3Key,
            file.FileName,
            contentType,
            ct);

        // Enqueue background OCR. Job will:
        //   1. status='processing' (currently 'uploading')
        //   2. Download file from S3, run PaddleOCR
        //   3. status='ocr_complete' + rawText + confidence
        // Any OCR failure → status='ocr_unavailable' + errorMessage.
        var ocrJobId = ocrScheduler.Enqueue(voucherId, s3Key, file.FileName, contentType, isImage);

        // Chain the extraction job so LLM + AR + 3-way match run
        // automatically after OCR succeeds. Uses ContinueJobWith so the
        // extract runner sees rawOcrText in DB. We forward the caller's
        // JWT so /agent/run accepts the request.
        var userToken = ExtractBearerToken(ctx);
        var extractJobId = extractScheduler.Enqueue(
            voucherId, userId, tenantId, voucherType, userToken, ocrJobId);

        return Results.Ok(new
        {
            voucherId,
            status = "uploading",
            ocrJobId,
            extractJobId,
        });
    }

    private static string ExtractBearerToken(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return auth.Substring("Bearer ".Length).Trim();
        }
        return string.Empty;
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
        TenantContext.TryGet(ctx, out var tenantId);

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
        TenantContext.TryGet(ctx, out var tenantId);

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
        TenantContext.TryGet(ctx, out var tenantId);

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
        TenantContext.TryGet(ctx, out var tenantId);

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
        TenantContext.TryGet(ctx, out var tenantId);

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
        if (path.StartsWith("vouchers/", StringComparison.Ordinal))
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
        TenantContext.TryGet(ctx, out var tenantId);

        // Ownership check — 404 if the caller doesn't own the stub.
        var existing = await repo.FindAsync(tenantId, userId, id, ct);
        if (existing is null)
        {
            return Results.NotFound();
        }

        // UC-ACC-001 E2: chống kê khai trùng — cùng seller_mst + document_number.
        // Search hoá đơn khác cùng cặp (loại trừ chính stub này). If found,
        // don't overwrite; return 409 with the duplicate's ID so web can flag.
        if (!string.IsNullOrWhiteSpace(request.SellerMst)
            && !string.IsNullOrWhiteSpace(request.DocumentNumber))
        {
            var maybeDup = await repo.FindDuplicateAsync(
                tenantId, request.SellerMst, request.DocumentNumber, excludeId: id, ct);
            if (maybeDup is not null)
            {
                return Results.Conflict(new
                {
                    error = "duplicate_voucher",
                    message = $"Hoá đơn {request.DocumentNumber} của MST {request.SellerMst} đã tồn tại.",
                    existingVoucherId = maybeDup.Id,
                    existingDocumentDate = maybeDup.DocumentDate,
                });
            }
        }

        await repo.ReplaceExtractedAsync(id, request, ct);
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
        TenantContext.TryGet(ctx, out var tenantId);

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
        if (!TryGetUserId(ctx, out _))
        {
            return Results.Unauthorized();
        }
        if (request?.Entries is null || request.Entries.Count == 0)
        {
            return Results.BadRequest(new { error = "entries required" });
        }

        var saved = await repo.SaveJournalSuggestionsAsync(id, request, ct);
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

        var updated = await repo.AcceptJournalAsync(id, entryId, userId, request, ct);
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
