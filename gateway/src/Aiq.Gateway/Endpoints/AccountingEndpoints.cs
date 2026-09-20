using Aiq.Gateway.Books;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;
using Aiq.Gateway.Security;
using Aiq.Gateway.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Aiq.Gateway.Endpoints;

/// <summary>
/// Accounting Invoice page (/accounting/invoice) — today a fully ephemeral,
/// no-persistence flow: raw text goes to the agent's `accounting.invoice`
/// capability ("Trích xuất trường" / "Đề xuất bút toán"), which returns
/// markdown; there is no invoice table, id, or repository anywhere in this
/// codebase (confirmed — this codebase's only persisted accounting/invoice
/// documents live under the separate Voucher domain, VoucherEndpoints.cs /
/// IVoucherRepository.cs, which this page does not use).
///
/// This file adds exactly one route: upload a file, get extracted text
/// back for the page's existing textarea. It never creates or mutates any
/// accounting state — same non-goal as ContractEndpoints.ExtractText.
/// </summary>
public static class AccountingEndpoints
{
    public static IEndpointRouteBuilder MapAccountingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/accounting/invoice/extract-text", ExtractInvoiceText)
            .RequireAuthorization("department_member")
            .RequireRateLimiting("default")
            .DisableAntiforgery();
        return app;
    }

    // Own allow-list on purpose, same rationale as ContractEndpoints.ExtractText
    // — not FileUploadOptions/IFileUploadGuard's shared (PDF-only) config.
    // XML added here specifically for Vietnamese e-invoice exports, which
    // vouchers/contracts never accept.
    private static readonly HashSet<string> ExtractExtensions=[".pdf",".docx",".png",".jpg",".jpeg",".xml"];
    private const long ExtractMaxBytes=25L*1024*1024;
    private static readonly Dictionary<string,byte[]?> ExtractMagicByExtension=new()
    {
        [".pdf"]=Convert.FromHexString("255044462D"),        // %PDF-
        [".docx"]=Convert.FromHexString("504B0304"),         // PK\x03\x04 (docx is a zip)
        [".png"]=Convert.FromHexString("89504E470D0A1A0A"),
        [".jpg"]=Convert.FromHexString("FFD8FF"),
        [".jpeg"]=Convert.FromHexString("FFD8FF"),
        [".xml"]=null, // XML has no fixed magic byte prefix (BOM/whitespace vary) — validated by well-formedness downstream instead.
    };
    private static readonly Dictionary<string,string> ExtractContentTypeByExtension=new()
    {
        [".pdf"]="application/pdf",
        [".docx"]="application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".png"]="image/png",
        [".jpg"]="image/jpeg",
        [".jpeg"]="image/jpeg",
        [".xml"]="application/xml",
    };

    private static async Task<IResult> ExtractInvoiceText(
        [FromForm] IFormFile file,
        IAgentClient agent,
        IFileUploadGuard guard,
        IObjectStorage storage,
        IOptions<S3Options> options,
        ICapabilityRepository caps,
        HttpContext x,
        CancellationToken ct)
    {
        if (!AuthenticatedPrincipalContext.TryGet(x, out var p) || p.TenantId is not { } t || p.DepartmentId is not { } d)
            return Results.Forbid();
        if (!await Consume(p, t, d, caps, ct))
            return Results.Forbid();

        var filename = file.FileName ?? "";
        var extension = Path.GetExtension(filename).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(filename))
            return Results.BadRequest(new { error = "missing_filename" });
        if (!ExtractExtensions.Contains(extension))
            return Results.BadRequest(new { error = "unsupported_file_type" });
        if (file.Length <= 0)
            return Results.BadRequest(new { error = "empty_body" });
        if (file.Length > ExtractMaxBytes)
            return Results.BadRequest(new { error = "payload_too_large" });

        await using var input = file.OpenReadStream();
        await using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        var magic = ExtractMagicByExtension[extension];
        if (magic is not null)
        {
            var header = buffer.ToArray().AsSpan(0, (int)Math.Min(buffer.Length, magic.Length));
            if (header.Length < magic.Length || !header.SequenceEqual(magic))
                return Results.BadRequest(new { error = "magic_byte_mismatch" });
            buffer.Position = 0;
        }

        var contentType = ExtractContentTypeByExtension[extension];

        // Reuses the existing voucher-uploads MinIO bucket (no new bucket
        // provisioning) under a distinct key prefix — this page has no
        // invoice/contract id to key by (no persistence exists), so, like
        // Contract Upload's pre-save staging path, keys by content hash
        // only: a retry/re-extract of the identical file is a storage
        // no-op, and it never collides with a real voucher upload's own
        // {userId}/{guid}{ext} key shape.
        var sha = await guard.ComputeSha256Async(buffer, ct);
        buffer.Position = 0;
        var key = TenantObjectKey.Build(t, "accounting", "invoice-staging", p.UserId.ToString("N"), $"{sha}{extension}");
        await storage.PutAsync(options.Value.VoucherBucket, key, buffer, contentType, ct);
        buffer.Position = 0;

        return await AgentCallResult.RunAsync(
            () => agent.ExtractDocumentTextAsync(buffer, filename, contentType, ct),
            extracted => Results.Ok(new
            {
                text = extracted.Text,
                charCount = extracted.CharCount,
                mimeType = extracted.MimeType,
                extractionMethod = extracted.ExtractionMethod,
                originalFilename = filename,
                sourceObjectKey = key,
            }),
            ct);
    }

    private static async Task<bool> Consume(PrincipalContext p, Guid tenantId, Guid departmentId, ICapabilityRepository r, CancellationToken ct)
    {
        if (p.IsInRole("admin")) return true;
        if (!p.IsInRole("user")) return false;
        var a = await r.ResolveAgentAsync(tenantId, departmentId, p.UserId, false, "accounting.invoice", ct);
        return a.AgentId is not null;
    }
}
