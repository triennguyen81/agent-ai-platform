using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Books;
using Aiq.Gateway.Security;
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

        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);

        group.MapPost("/{id:guid}/pages", UploadPagesAsync)
            .RequireAuthorization("uploader")
            .RequireRateLimiting("upload")
            .DisableAntiforgery();

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

        var language = string.IsNullOrWhiteSpace(request.Language) ? "vi" : request.Language;
        var record = await books.CreateAsync(request.Title, request.Author, language, userId, ct);
        return Results.Created($"/books/{record.Id}", record);
    }

    private static async Task<IResult> ListAsync(
        [FromQuery] string? status,
        [FromQuery(Name = "q")] string? query,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        IBookRepository books,
        CancellationToken ct)
    {
        var safeLimit = Math.Clamp(limit ?? 20, 1, 100);
        var safeOffset = Math.Max(offset ?? 0, 0);

        var (items, total) = await books.ListAsync(status, query, safeLimit, safeOffset, ct);
        return Results.Ok(new BookListResponse(items, total, safeLimit, safeOffset));
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        IBookRepository books,
        CancellationToken ct)
    {
        var book = await books.FindByIdAsync(id, ct);
        if (book is null)
        {
            return Results.NotFound();
        }

        var pages = await books.GetPagesAsync(id, ct);
        return Results.Ok(new BookDetailsResponse(book, pages));
    }

    private static async Task<IResult> UploadPagesAsync(
        Guid id,
        IFormFile file,
        IBookRepository books,
        IAgentClient agent,
        IFileUploadGuard guard,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var book = await books.FindByIdAsync(id, ct);
        if (book is null)
        {
            return Results.NotFound();
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

        try
        {
            await using var uploadStream = file.OpenReadStream();
            var ocr = await agent.RunOcrPipelineAsync(uploadStream, file.FileName, ct);

            var upsertInputs = ocr.Pages
                .Select(p => new UpsertPageInput(
                    p.PageNumber,
                    p.RawText,
                    p.CorrectedText,
                    (float)p.Confidence))
                .ToArray();

            await books.UpsertPagesAsync(id, upsertInputs, ct);
            await books.UpdateStatusAndPageCountAsync(id, "processed", ocr.PageCount, ct);

            return Results.Ok(new
            {
                bookId = id,
                pageCount = ocr.PageCount,
                averageConfidence = ocr.AverageConfidence,
                stageDurationsMs = ocr.StageDurationsMs,
            });
        }
        catch (AgentRequestException ex)
        {
            logger.LogWarning(ex, "Agent OCR failed for book {BookId}", id);
            return Results.Problem(
                type: "https://aiq.local/problems/agent",
                title: "Agent OCR failed",
                statusCode: StatusCodes.Status502BadGateway,
                detail: ex.Message);
        }
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
