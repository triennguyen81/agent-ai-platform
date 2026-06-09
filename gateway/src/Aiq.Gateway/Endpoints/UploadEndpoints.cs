using Aiq.Gateway.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Aiq.Gateway.Endpoints;

public static class UploadEndpoints
{
    public static IEndpointRouteBuilder MapUploadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app
            .MapGroup("/uploads")
            .RequireAuthorization("uploader")
            .RequireRateLimiting("upload");

        group.MapPost("/pdf", ScanPdfAsync)
            .DisableAntiforgery();

        return app;
    }

    private static async Task<IResult> ScanPdfAsync(
        IFormFile file,
        IFileUploadGuard guard,
        HttpContext ctx,
        CancellationToken ct)
    {
        var metadataCheck = guard.ValidateMetadata(file.FileName, file.ContentType, file.Length);
        if (!metadataCheck.Ok)
        {
            return Problem(metadataCheck, StatusCodes.Status400BadRequest);
        }

        await using var stream = file.OpenReadStream();
        var headerBuffer = new byte[16];
        var read = await ReadExactAsync(stream, headerBuffer, ct);

        var bodyCheck = guard.ValidateBody(file.ContentType, headerBuffer.AsSpan(0, read));
        if (!bodyCheck.Ok)
        {
            return Problem(bodyCheck, StatusCodes.Status415UnsupportedMediaType);
        }

        // Re-open the stream to hash the full body — IFormFile streams are seekable here.
        await using var hashStream = file.OpenReadStream();
        var sha256 = await guard.ComputeSha256Async(hashStream, ct);

        return Results.Ok(new
        {
            accepted = true,
            filename = file.FileName,
            contentType = file.ContentType,
            sizeBytes = file.Length,
            sha256,
            uploaderSub = ctx.User.FindFirst("sub")?.Value,
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

    private static IResult Problem(FileUploadValidationResult result, int statusCode) =>
        Results.Problem(
            type: "https://aiq.local/problems/upload",
            title: "Upload rejected",
            statusCode: statusCode,
            detail: result.Reason,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = result.Code,
            });
}
