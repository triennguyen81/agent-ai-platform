using Aiq.Gateway.Governance;
using Aiq.Gateway.Notes;
using Aiq.Gateway.Middleware;
using Aiq.Gateway.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;

namespace Aiq.Gateway.Endpoints;

public static class KnowledgeBaseEndpoints
{
    public sealed record CreateRequest(string Name, string? Description);
    public sealed record StatusRequest(string Status);
    public sealed record ManualDocumentRequest(
        [property: Required, StringLength(300)] string Title,
        [property: Required, StringLength(2_000_000)] string Content,
        [property: MaxLength(50)] string[]? Tags = null,
        Guid? DocumentId = null);

    public static IEndpointRouteBuilder MapKnowledgeBaseEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/department/knowledge-bases")
            .RequireAuthorization("department_admin").RequireRateLimiting("default");
        group.MapGet("/", async (KnowledgeBaseRepository repo, HttpContext ctx, CancellationToken ct) =>
        {
            if (!Scope(ctx, out var t, out var d, out _)) return Results.Forbid();
            return Results.Ok(new { items = await repo.ListAsync(t,d,ct) });
        });
        group.MapPost("/", async (CreateRequest body, KnowledgeBaseRepository repo, HttpContext ctx, CancellationToken ct) =>
        {
            if (!Scope(ctx, out var t, out var d, out var u)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length>300 || body.Description?.Length>4000) return Results.BadRequest();
            var id = await repo.CreateAsync(t,d,u,body.Name.Trim(),body.Description ?? "",ct);
            return Results.Created($"/department/knowledge-bases/{id}",new { id });
        });
        group.MapGet("/{id:guid}/documents", async (Guid id, KnowledgeBaseRepository repo, HttpContext ctx, CancellationToken ct) =>
        {
            if (!await Owns(repo,ctx,id,ct)) return Results.NotFound();
            return Results.Ok(new { items = await repo.DocumentsAsync(id,ct) });
        });
        group.MapPost("/{id:guid}/documents", UploadAsync);
        group.MapPost("/{id:guid}/documents/{documentId:guid}/{action}", async (Guid id, Guid documentId, string action, KnowledgeBaseRepository repo, HttpContext ctx, CancellationToken ct) =>
        {
            if (!await Owns(repo,ctx,id,ct)) return Results.NotFound();
            if (action is not ("reindex" or "disable" or "delete" or "restore")) return Results.BadRequest();
            return await repo.DocumentActionAsync(id,documentId,action,ct) ? Results.NoContent() : Results.NotFound();
        });
        group.MapPatch("/{id:guid}", async (Guid id, StatusRequest body, KnowledgeBaseRepository repo, HttpContext ctx, CancellationToken ct) =>
        {
            if (!await Owns(repo,ctx,id,ct)) return Results.NotFound();
            if (body.Status is not ("active" or "disabled" or "deleted")) return Results.BadRequest();
            await repo.SetStatusAsync(id,body.Status,ct);
            return Results.NoContent();
        });
        group.MapGet("/{id:guid}/agents", async (Guid id, KnowledgeBaseRepository repo, HttpContext ctx, CancellationToken ct) =>
        {
            if (!await Owns(repo,ctx,id,ct) || !Scope(ctx,out var t,out var d,out _)) return Results.NotFound();
            return Results.Ok(new { items = await repo.AgentsAsync(t,d,id,ct) });
        });
        group.MapPut("/{id:guid}/agents/{agentId:guid}", (Guid id, Guid agentId, KnowledgeBaseRepository repo, HttpContext ctx, CancellationToken ct) => Attach(repo,ctx,id,agentId,true,ct));
        group.MapDelete("/{id:guid}/agents/{agentId:guid}", (Guid id, Guid agentId, KnowledgeBaseRepository repo, HttpContext ctx, CancellationToken ct) => Attach(repo,ctx,id,agentId,false,ct));
        return app;
    }

    private static async Task<IResult> UploadAsync(Guid id, KnowledgeBaseRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!await Owns(repo,ctx,id,ct) || !Scope(ctx,out var t,out var d,out var u)) return Results.NotFound();
        if (ctx.Request.HasJsonContentType())
        {
            ManualDocumentRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<ManualDocumentRequest>(ct); }
            catch (JsonException) { return Results.BadRequest(new { error="invalid_document" }); }
            if (body is null || !Validator.TryValidateObject(body, new ValidationContext(body), [], true)
                || body.Title.Contains('\0') || body.Content.Contains('\0')
                || body.Tags?.Any(tag => tag is null || tag.Length>100 || tag.Contains('\0')) == true)
                return Results.BadRequest(new { error="invalid_document" });
            var tags = (body.Tags ?? []).Select(tag => tag.Trim()).Where(tag => tag.Length>0).Distinct().ToArray();
            return await SaveDocumentAsync(repo,t,d,u,id,body.DocumentId,body.Title.Trim(),body.Content,
                "text/markdown",ct,tags,source:"manual");
        }
        if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error="json_or_multipart_required" });
        var form = await ctx.Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length==0 || file.Length>10*1024*1024) return Results.BadRequest(new { error="file_size_1_to_10MB" });
        var title = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(title) || title.Length>300) return Results.BadRequest();
        var extension = Path.GetExtension(title).ToLowerInvariant();
        string content;
        string mime;
        if (extension is ".txt" or ".md")
        {
            using var reader = new StreamReader(file.OpenReadStream(),new System.Text.UTF8Encoding(false,true));
            try { content = await reader.ReadToEndAsync(ct); }
            catch (System.Text.DecoderFallbackException) { return Results.BadRequest(new { error="utf8_required" }); }
            mime = extension==".md" ? "text/markdown" : "text/plain";
        }
        else if (extension is ".docx" or ".pdf" or ".png" or ".jpg" or ".jpeg")
        {
            var options = ctx.RequestServices.GetRequiredService<IOptions<AgentOptions>>().Value;
            using var client = ctx.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient();
            client.Timeout=TimeSpan.FromSeconds(options.TimeoutSeconds);
            using var upload = new MultipartFormDataContent();
            upload.Add(new StreamContent(file.OpenReadStream()),"file",title);
            using var message = new HttpRequestMessage(HttpMethod.Post,$"{options.BaseUrl.TrimEnd('/')}/knowledge/extract") { Content=upload };
            message.Headers.TryAddWithoutValidation("Authorization",ctx.Request.Headers.Authorization.ToString());
            using var response = await client.SendAsync(message,ct);
            if (!response.IsSuccessStatusCode) return Results.Json(new { error="document_extraction_failed" },statusCode:422);
            using var extracted = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            content=extracted.RootElement.GetProperty("text").GetString() ?? "";
            mime=extracted.RootElement.GetProperty("mimeType").GetString() ?? "application/octet-stream";
        }
        else return Results.BadRequest(new { error="supported_types_txt_md_docx_pdf_png_jpeg" });
        if (string.IsNullOrWhiteSpace(content) || content.Contains('\0') || content.Length>2_000_000) return Results.BadRequest(new { error="invalid_text" });
        Guid? documentId = null;
        if (!string.IsNullOrEmpty(form["documentId"]))
        {
            if (!Guid.TryParse(form["documentId"],out var parsed)) return Results.BadRequest();
            documentId=parsed;
        }
        return await SaveDocumentAsync(repo,t,d,u,id,documentId,title,content,mime,ct);
    }

    private static async Task<IResult> SaveDocumentAsync(KnowledgeBaseRepository repo, Guid t, Guid d, Guid u,
        Guid id, Guid? documentId, string title, string content, string mime, CancellationToken ct,
        string[]? tags = null, string source = "upload")
    {
        try
        {
            var saved = await repo.SaveDocumentAsync(t,d,u,id,documentId,title,content,mime,ct,tags,source);
            return saved is null ? Results.NotFound() : Results.Accepted(value:new { id=saved, status="accepted" });
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState=="23505")
        {
            return Results.Conflict(new { error="document_filename_already_exists" });
        }
    }
    private static async Task<IResult> Attach(KnowledgeBaseRepository repo, HttpContext ctx, Guid id, Guid agentId, bool attach, CancellationToken ct)
    {
        if (!await Owns(repo,ctx,id,ct) || !Scope(ctx,out var t,out var d,out var u)) return Results.NotFound();
        return await repo.AttachAsync(t,d,u,id,agentId,attach,ct) ? Results.NoContent() : Results.NotFound();
    }
    private static bool Scope(HttpContext ctx,out Guid t,out Guid d,out Guid u)
    {
        t=d=u=Guid.Empty;
        if (!AuthenticatedPrincipalContext.TryGet(ctx,out var p) || p.TenantId is not {} tenant || p.DepartmentId is not {} department) return false;
        t=tenant;d=department;u=p.UserId;return true;
    }
    private static async Task<bool> Owns(KnowledgeBaseRepository repo,HttpContext ctx,Guid id,CancellationToken ct) =>
        Scope(ctx,out var t,out var d,out _) && await repo.OwnsAsync(t,d,id,ct);
}
