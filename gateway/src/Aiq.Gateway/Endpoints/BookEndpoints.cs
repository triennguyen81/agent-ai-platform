using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Books;
using Aiq.Gateway.Jobs;
using Aiq.Gateway.Middleware;
using Aiq.Gateway.Security;
using Hangfire;
using Hangfire.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Aiq.Gateway.Endpoints;

public static class BookEndpoints
{
    public static IEndpointRouteBuilder MapBookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app
            .MapGroup("/books")
            .RequireAuthorization()
            .RequireRateLimiting("default");

        group.MapPost("/", CreateAsync)
            .RequireAuthorization("uploader");

        group.MapGet("/", ListAsync).CacheOutput("books-list");
        group.MapGet("/{id:guid}", GetAsync);

        group.MapPost("/{id:guid}/pages", UploadPagesAsync)
            .RequireAuthorization("uploader")
            .RequireRateLimiting("upload")
            .DisableAntiforgery();

        group.MapGet("/{id:guid}/jobs/{jobId}", GetJobStatusAsync);

        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreateBookRequest request,
        IBookRepository books,
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

        var language = string.IsNullOrWhiteSpace(request.Language) ? "vi" : request.Language;
        var record = await books.CreateAsync(tenantId, request.Title, request.Author, language, userId, ct);
        return Results.Created($"/books/{record.Id}", record);
    }

    private static async Task<IResult> ListAsync(
        [FromQuery] string? status,
        [FromQuery(Name = "q")] string? query,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        IBookRepository books,
        HttpContext ctx,
        CancellationToken ct)
    {
        var safeLimit = Math.Clamp(limit ?? 20, 1, 100);
        var safeOffset = Math.Max(offset ?? 0, 0);

        var tenantId = TenantContext.Require(ctx);
        var (items, total) = await books.ListAsync(tenantId, status, query, safeLimit, safeOffset, ct);
        return Results.Ok(new BookListResponse(items, total, safeLimit, safeOffset));
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        IBookRepository books,
        HttpContext ctx,
        CancellationToken ct)
    {
        var tenantId = TenantContext.Require(ctx);
        var book = await books.FindByIdAsync(tenantId, id, ct);
        if (book is null)
        {
            return Results.NotFound();
        }

        var pages = await books.GetPagesAsync(tenantId, id, ct);
        return Results.Ok(new BookDetailsResponse(book, pages));
    }

    private static async Task<IResult> UploadPagesAsync(
        Guid id,
        IFormFile file,
        IBookRepository books,
        IOcrJobScheduler scheduler,
        IFileUploadGuard guard,
        IBookAccessControl acl,
        HttpContext ctx,
        CancellationToken ct)
    {
        var tenantId = TenantContext.Require(ctx);
        var book = await books.FindByIdAsync(tenantId, id, ct);
        if (book is null)
        {
            return Results.NotFound();
        }

        if (!acl.CanMutate(ctx.User, book))
        {
            return Results.Problem(
                type: "https://aiq.local/problems/forbidden",
                title: "Forbidden",
                statusCode: StatusCodes.Status403Forbidden,
                detail: "Only the original uploader or an admin can attach pages to this book.");
        }

        var metadataCheck = guard.ValidateMetadata(file.FileName, file.ContentType, file.Length);
        if (!metadataCheck.Ok)
        {
            return ProblemFrom(metadataCheck, StatusCodes.Status400BadRequest);
        }

        await using (var headerStream = file.OpenReadStream())
        {
            var headerBuffer = new byte[16];
            var read = await ReadExactAsync(headerStream, headerBuffer, ct);
            var bodyCheck = guard.ValidateBody(file.ContentType, headerBuffer.AsSpan(0, read));
            if (!bodyCheck.Ok)
            {
                return ProblemFrom(bodyCheck, StatusCodes.Status415UnsupportedMediaType);
            }
        }

        await books.UpdateStatusAndPageCountAsync(tenantId, id, "queued", book.PageCount ?? 0, ct);

        await using var uploadStream = file.OpenReadStream();
        var jobId = await scheduler.EnqueueAsync(tenantId, id, uploadStream, file.FileName, ct);

        return Results.Accepted(
            uri: $"/books/{id}/jobs/{jobId}",
            value: new
            {
                bookId = id,
                jobId,
                status = "queued",
            });
    }

    private static async Task<IResult> GetJobStatusAsync(
        Guid id,
        string jobId,
        IBookRepository books,
        HttpContext ctx,
        CancellationToken ct)
    {
        var tenantId = TenantContext.Require(ctx);
        if (await books.FindByIdAsync(tenantId, id, ct) is null) return Results.NotFound();
        using var connection = JobStorage.Current.GetConnection();
        var data = connection.GetJobData(jobId);

        if (data is null)
        {
            return Results.NotFound(new { error = $"Unknown job {jobId}." });
        }

        return Results.Ok(new
        {
            bookId = id,
            jobId,
            state = data.State,
            createdAt = data.CreatedAt,
            method = $"{data.Job?.Type.Name}.{data.Job?.Method.Name}",
        });
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }

    private static IResult ProblemFrom(FileUploadValidationResult result, int statusCode) =>
        Results.Problem(
            type: "https://aiq.local/problems/upload",
            title: "Upload rejected",
            statusCode: statusCode,
            detail: result.Reason,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = result.Code,
            });

    private static bool TryGetUserId(HttpContext ctx, out Guid userId)
    {
        var raw = ctx.User.FindFirst("sub")?.Value;
        return Guid.TryParse(raw, out userId);
    }

    private static bool TryValidate(object instance, out Dictionary<string, string[]> errors)
    {
        var context = new ValidationContext(instance);
        var results = new List<ValidationResult>();
        var ok = Validator.TryValidateObject(instance, context, results, validateAllProperties: true);

        errors = results
            .SelectMany(r => r.MemberNames.Select(m => (Member: m, r.ErrorMessage)))
            .GroupBy(t => t.Member)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.ErrorMessage ?? "Invalid").ToArray());

        return ok;
    }
}
