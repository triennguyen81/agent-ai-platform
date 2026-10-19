using System.Security.Cryptography;
using Aiq.Gateway.Configuration;
using Aiq.Gateway.Employees;
using Aiq.Gateway.Jobs;
using Aiq.Gateway.Security;
using Aiq.Gateway.Storage;
using Microsoft.Extensions.Options;

namespace Aiq.Gateway.Endpoints;

/// <summary>
/// Multi-file Expense Claim intake: ONE upload action → ONE claim (an
/// expense_claim voucher) → ONE item per file. Creating / adding bills
/// follows the existing voucher-upload rule (any department member; the
/// claim's creator or an ADMIN may add files); item review actions are
/// department-ADMIN only. Approval, recognition and payment stay on the
/// existing /vouchers/{id}/expense-claim/* endpoints (ADMIN Department
/// Authority Invariant: one ADMIN may run the whole chain).
/// </summary>
public static class ExpenseClaimEndpoints
{
    private static readonly Dictionary<string, byte[][]> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = [[0x25, 0x50, 0x44, 0x46]],
        ["image/jpeg"] = [[0xFF, 0xD8, 0xFF]],
        ["image/jpg"] = [[0xFF, 0xD8, 0xFF]],
        ["image/png"] = [[0x89, 0x50, 0x4E, 0x47]],
        ["image/webp"] = [[0x52, 0x49, 0x46, 0x46]],
    };

    public static IEndpointRouteBuilder MapExpenseClaimEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/expense-claims").RequireAuthorization("department_member").RequireRateLimiting("default");
        g.MapGet("/employees", ListEmployeesAsync);
        g.MapPost("/", CreateAsync).DisableAntiforgery();
        g.MapGet("/{id:guid}", GetAsync);
        g.MapPost("/{id:guid}/items", AddItemsAsync).DisableAntiforgery();
        g.MapGet("/{id:guid}/items/{itemId:guid}/file", GetItemFileAsync);
        g.MapPatch("/{id:guid}/items/{itemId:guid}", ConfirmItemAsync).RequireAuthorization("department_admin");
        g.MapPost("/{id:guid}/items/{itemId:guid}/exclude", ExcludeItemAsync).RequireAuthorization("department_admin");
        g.MapPost("/{id:guid}/items/{itemId:guid}/retry", RetryItemAsync).RequireAuthorization("department_admin");
        return app;
    }

    public sealed record ExcludeItemBody(string? Reason);
    public sealed record FileResult(int Index, string FileName, string Outcome, Guid? ItemId, string? Error);

    private static async Task<IResult> ListEmployeesAsync(IExpenseClaimIntakeRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var tenantId, out _)) return Results.Forbid();
        return Results.Ok(new { items = await repo.ListEmployeesAsync(tenantId, ct) });
    }

    private static async Task<IResult> CreateAsync(IExpenseClaimIntakeRepository repo, IObjectStorage storage, IOptions<S3Options> s3,
        IExpenseClaimItemJobScheduler scheduler, HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "multipart_form_required" });
        var form = await ctx.Request.ReadFormAsync(ct);
        if (!Guid.TryParse(form["employeeId"], out var employeeId)) return Results.BadRequest(new { error = "employee_required" });
        var tripStart = ParseDate(form["tripStart"]);
        var tripEnd = ParseDate(form["tripEnd"]);
        if (tripStart is not null && tripEnd is not null && tripEnd < tripStart) return Results.BadRequest(new { error = "trip_dates_invalid" });
        var requestId = Clean(form["requestId"]);
        var files = form.Files.GetFiles("files");
        if (files.Count == 0) return Results.BadRequest(new { error = "files_required" });
        if (files.Count > ExpenseClaimItemRules.MaxFilesPerBatch)
            return Results.BadRequest(new { error = "too_many_files", max = ExpenseClaimItemRules.MaxFilesPerBatch });
        // Validate every file BEFORE creating anything: nothing valid → no claim.
        var checks = await Task.WhenAll(files.Select(ValidateAsync));
        if (checks.All(c => c.Error is not null))
            return Results.BadRequest(new { error = "no_valid_files", files = checks.Select((c, i) => new FileResult(i, files[i].FileName, "rejected", null, c.Error)) });

        var created = await repo.CreateClaimAsync(tenantId, departmentId, principal.UserId,
            new CreateExpenseClaimCommand(employeeId, Clean(form["purpose"]), tripStart, tripEnd, Clean(form["reference"]), requestId), ct);
        if (created is not { } claim) return Results.BadRequest(new { error = "employee_not_found" });
        var results = await StoreItemsAsync(repo, storage, s3.Value, scheduler, ctx, tenantId, departmentId, principal.UserId, claim.ClaimId,
            files, checks, requestId, ct);
        return Results.Ok(new { claimId = claim.ClaimId, created = claim.Created, files = results });
    }

    private static async Task<IResult> AddItemsAsync(Guid id, IExpenseClaimIntakeRepository repo, IObjectStorage storage, IOptions<S3Options> s3,
        IExpenseClaimItemJobScheduler scheduler, HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        var view = await repo.GetAsync(tenantId, departmentId, id, ct);
        if (view is null) return Results.NotFound();
        // Same rule as voucher re-extraction: the claim's creator or an ADMIN.
        if (!principal.IsInRole("admin") && view.Intake.CreatedBy != principal.UserId) return Results.Forbid();
        if (view.Locked) return Results.Conflict(new { error = "expense_claim_locked", message = "Đề nghị đã được ghi nhận kế toán — không thể thêm khoản chi." });
        if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "multipart_form_required" });
        var form = await ctx.Request.ReadFormAsync(ct);
        var files = form.Files.GetFiles("files");
        if (files.Count == 0) return Results.BadRequest(new { error = "files_required" });
        if (files.Count > ExpenseClaimItemRules.MaxFilesPerBatch)
            return Results.BadRequest(new { error = "too_many_files", max = ExpenseClaimItemRules.MaxFilesPerBatch });
        var checks = await Task.WhenAll(files.Select(ValidateAsync));
        try
        {
            var results = await StoreItemsAsync(repo, storage, s3.Value, scheduler, ctx, tenantId, departmentId, principal.UserId, id,
                files, checks, Clean(form["requestId"]), ct);
            return Results.Ok(new { claimId = id, files = results });
        }
        catch (ExpenseClaimItemException ex) { return Problem(ex); }
    }

    /// <summary>Each file is persisted and enqueued independently: one bad file never loses its siblings.</summary>
    private static async Task<List<FileResult>> StoreItemsAsync(IExpenseClaimIntakeRepository repo, IObjectStorage storage, S3Options s3,
        IExpenseClaimItemJobScheduler scheduler, HttpContext ctx, Guid tenantId, Guid departmentId, Guid actorId, Guid claimId,
        IReadOnlyList<IFormFile> files, (byte[]? Bytes, string? Sha256, string? Error)[] checks, string? requestId, CancellationToken ct)
    {
        var token = BearerToken(ctx);
        var results = new List<FileResult>();
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var check = checks[i];
            if (check.Error is not null) { results.Add(new(i, file.FileName, "rejected", null, check.Error)); continue; }
            // A replay of the same request (same requestId + file index) returns the existing item: no new object, no new job.
            if (requestId is not null && await repo.FindItemByRequestAsync(claimId, requestId, i, ct) is { } existing)
            {
                results.Add(new(i, file.FileName, "existing", existing.Id, null));
                continue;
            }
            var key = TenantObjectKey.Build(tenantId, "expense-claims", claimId.ToString("N"), $"{Guid.NewGuid():N}{Extension(file)}");
            try
            {
                await using var stream = new MemoryStream(check.Bytes!, writable: false);
                await storage.PutAsync(s3.VoucherBucket, key, stream, file.ContentType.ToLowerInvariant(), ct);
                var (item, created) = await repo.AddItemAsync(tenantId, departmentId, claimId, actorId,
                    new NewExpenseClaimItem(requestId, requestId is null ? null : i, key, Path.GetFileName(file.FileName),
                        file.ContentType.ToLowerInvariant(), file.Length, check.Sha256!), ct);
                if (created) scheduler.Enqueue(item.Id, item.Attempt, actorId, token);
                results.Add(new(i, file.FileName, created ? "accepted" : "existing", item.Id, null));
            }
            catch (ExpenseClaimItemException) { throw; }
            catch (Exception)
            {
                results.Add(new(i, file.FileName, "rejected", null, "storage_failed"));
            }
        }
        return results;
    }

    private static async Task<(byte[]? Bytes, string? Sha256, string? Error)> ValidateAsync(IFormFile file)
    {
        if (file.Length <= 0) return (null, null, "file_empty");
        if (file.Length > ExpenseClaimItemRules.MaxFileBytes) return (null, null, "file_too_large");
        if (!AllowedTypes.TryGetValue(file.ContentType ?? "", out var magics)) return (null, null, "content_type_not_supported");
        await using var input = file.OpenReadStream();
        await using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        if (!magics.Any(m => bytes.Length >= m.Length && bytes.AsSpan(0, m.Length).SequenceEqual(m))) return (null, null, "file_content_mismatch");
        return (bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), null);
    }

    private static async Task<IResult> GetAsync(Guid id, IExpenseClaimIntakeRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        var view = await repo.GetAsync(tenantId, departmentId, id, ct);
        if (view is null) return Results.NotFound();
        var isAdmin = principal.IsInRole("admin");
        return Results.Ok(new
        {
            claim = view,
            canManageItems = isAdmin && !view.Locked,
            canAddFiles = !view.Locked && (isAdmin || view.Intake.CreatedBy == principal.UserId),
        });
    }

    private static async Task<IResult> GetItemFileAsync(Guid id, Guid itemId, IExpenseClaimIntakeRepository repo, IObjectStorage storage,
        IOptions<S3Options> s3, HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var tenantId, out var departmentId)) return Results.Forbid();
        var file = await repo.GetItemFileAsync(tenantId, departmentId, id, itemId, ct);
        if (file is not { } f || !TenantObjectKey.BelongsTo(tenantId, f.ObjectKey)) return Results.NotFound();
        var stream = await storage.GetAsync(s3.Value.VoucherBucket, f.ObjectKey, ct);
        return Results.File(stream, f.ContentType, f.FileName);
    }

    private static async Task<IResult> ConfirmItemAsync(Guid id, Guid itemId, ConfirmExpenseClaimItemRequest body, IExpenseClaimIntakeRepository repo,
        HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        try
        {
            var item = await repo.ConfirmItemAsync(tenantId, departmentId, id, itemId, principal.UserId, body, ct);
            if (item is null) return Results.NotFound();
            await repo.RecomputeAsync(tenantId, departmentId, id, ct);
            return Results.Ok(item);
        }
        catch (ExpenseClaimItemException ex) { return Problem(ex); }
    }

    private static async Task<IResult> ExcludeItemAsync(Guid id, Guid itemId, ExcludeItemBody? body, IExpenseClaimIntakeRepository repo,
        HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        try
        {
            var item = await repo.ExcludeItemAsync(tenantId, departmentId, id, itemId, principal.UserId, body?.Reason, ct);
            if (item is null) return Results.NotFound();
            await repo.RecomputeAsync(tenantId, departmentId, id, ct);
            return Results.Ok(item);
        }
        catch (ExpenseClaimItemException ex) { return Problem(ex); }
    }

    private static async Task<IResult> RetryItemAsync(Guid id, Guid itemId, IExpenseClaimIntakeRepository repo, IExpenseClaimItemJobScheduler scheduler,
        HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var tenantId, out var departmentId)) return Results.Forbid();
        try
        {
            var attempt = await repo.BeginRetryAsync(tenantId, departmentId, id, itemId, principal.UserId, ct);
            if (attempt is null) return Results.NotFound();
            scheduler.Enqueue(itemId, attempt.Value, principal.UserId, BearerToken(ctx));
            return Results.Ok(new { itemId, attempt, status = ExpenseClaimItemStatuses.Processing });
        }
        catch (ExpenseClaimItemException ex) { return Problem(ex); }
    }

    private static IResult Problem(ExpenseClaimItemException ex) =>
        ex.Code == "not_found" ? Results.NotFound() : Results.Conflict(new { error = ex.Code, message = ex.Message });

    private static DateOnly? ParseDate(string? s) =>
        DateOnly.TryParseExact(s?.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Extension(IFormFile file)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        return ext is ".pdf" or ".jpg" or ".jpeg" or ".png" or ".webp" ? ext : file.ContentType.Contains("pdf") ? ".pdf" : ".jpg";
    }

    private static string BearerToken(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth["Bearer ".Length..].Trim() : string.Empty;
    }
}
