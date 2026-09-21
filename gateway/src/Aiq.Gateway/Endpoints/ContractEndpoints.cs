using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Books;
using Aiq.Gateway.Contracts;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;
using Aiq.Gateway.Security;
using Aiq.Gateway.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Aiq.Gateway.Endpoints;

public static class ContractEndpoints
{
    public sealed record ReviewContractRequest(bool Approve, string? Comment);
    public static IEndpointRouteBuilder MapContractEndpoints(this IEndpointRouteBuilder app)
    {
        var g=app.MapGroup("/contracts").RequireAuthorization("department_member").RequireRateLimiting("default");
        g.MapGet("/",List); g.MapPost("/",Create); g.MapGet("/{id:guid}",Get); g.MapPatch("/{id:guid}",Update); g.MapDelete("/{id:guid}",Delete);
        g.MapPost("/{id:guid}/process",Process); g.MapPost("/{id:guid}/review",Review); g.MapPost("/{id:guid}/handoff",Handoff);
        g.MapGet("/{id:guid}/documents",Documents);
        g.MapPost("/{id:guid}/documents",UploadDocument).DisableAntiforgery();
        g.MapDelete("/{id:guid}/documents/{documentId:guid}",DeleteDocument);
        g.MapPost("/extract-text",ExtractText).DisableAntiforgery();
        return app;
    }
    private static async Task<IResult> List(IContractRepository r,ICapabilityRepository c,HttpContext x,CancellationToken ct,string? status=null,string? q=null,int limit=50,int offset=0)
    {if(!Scope(x,out var p,out var t,out var d)||!await Consume(p,"contract.processing",c,ct))return Results.Forbid();var a=await r.ListAsync(t,d,status,q,limit,offset,ct);return Results.Ok(new ContractsListResponse(a,a.Count));}
    private static async Task<IResult> Create(CreateContractRequest q,IContractRepository r,ICapabilityRepository c,HttpContext x,CancellationToken ct)
    {if(!Valid(q,out var e))return Results.ValidationProblem(e);if(!Scope(x,out var p,out var t,out var d)||!await Consume(p,"contract.intake",c,ct))return Results.Forbid();var a=await r.CreateAsync(t,d,p.UserId,q,ct);return Results.Created($"/contracts/{a.Contract.Id}",a);}
    private static async Task<IResult> Get(Guid id,IContractRepository r,ICapabilityRepository c,HttpContext x,CancellationToken ct)
    {if(!Scope(x,out var p,out var t,out var d)||!await Consume(p,"contract.processing",c,ct))return Results.Forbid();var a=await r.FindAggregateAsync(t,d,id,ct);return a is null?Results.NotFound():Results.Ok(a);}
    // ADMIN keeps unrestricted edit access. A USER may edit only a contract
    // they themselves created (contracts.user_id, never client-supplied),
    // only while ADMIN hasn't yet decided it (approve/handoff/reject all
    // close the editable window), and only while their capability grant is
    // still active — created_by gates USER edit rights, never Department
    // ownership itself (that's still tenant_id+department_id via Scope()).
    private static async Task<IResult> Update(Guid id,UpdateContractRequest q,IContractRepository r,ICapabilityRepository c,HttpContext x,CancellationToken ct)
    {
        if(!Scope(x,out var p,out var t,out var d))return Results.Forbid();
        if(!p.IsInRole("admin"))
        {
            if(!await Consume(p,"contract.processing",c,ct))return Results.Forbid();
            var existing=await r.FindAsync(t,d,id,ct);
            if(existing is null)return Results.NotFound();
            if(existing.Contract.UserId!=p.UserId)return Results.Forbid();
            if(existing.Contract.WorkflowState is "APPROVED" or "HANDED_OFF" or "REJECTED")return Results.Forbid();
        }
        var a=await r.UpdateAsync(t,d,id,q,ct);return a is null?Results.NotFound():Results.Ok(a);
    }
    private static async Task<IResult> Delete(Guid id,IContractRepository r,HttpContext x,CancellationToken ct)
    {if(!Admin(x,out _,out var t,out var d))return Results.Forbid();return await r.DeleteAsync(t,d,id,ct)?Results.NoContent():Results.NotFound();}
    private static async Task<IResult> Process(Guid id,ProcessContractRequest q,IContractRepository r,ICapabilityRepository c,IHitlRepository hitl,HttpContext x,CancellationToken ct)
    {if(!Scope(x,out var p,out var t,out var d))return Results.Forbid();var contract=await r.FindAsync(t,d,id,ct);if(contract is null)return Results.NotFound();if(!p.IsInRole("admin")&&contract.Contract.UserId!=p.UserId)return Results.Forbid();if(contract.Contract.WorkflowState is "APPROVED" or "HANDED_OFF" or "REJECTED")return Results.Conflict(new{error="contract_finalized"});var resolved=await c.ResolveAgentAsync(t,d,p.IsInRole("admin")?null:p.UserId,p.IsInRole("admin"),"contract.validate",null,ct);if(resolved.AgentId is null){resolved=await c.ResolveAgentAsync(t,d,p.IsInRole("admin")?null:p.UserId,p.IsInRole("admin"),"contract.processing",null,ct);if(resolved.AgentId is null)return Results.Forbid();}var a=await r.ProcessAsync(t,d,p.UserId,id,q,ct);if(a is null)return Results.Conflict(new{error="contract_not_processable"});if(a.WorkflowState is "WAITING_REVIEW" or "NEEDS_REVIEW"){using var doc=System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new{contractId=id,workflowState=a.WorkflowState,totalValue=q.TotalValue}));var gate=await hitl.EvaluateAsync(t,resolved.AgentId.Value,p.UserId,"contract.handoff",doc.RootElement,ct);await r.SetHitlActionAsync(t,d,id,gate.ActionId,ct);a=a with{HitlActionId=gate.ActionId};}return Results.Ok(a);}
    private static async Task<IResult> Review(Guid id,ReviewContractRequest q,IContractRepository r,IHitlRepository hitl,HttpContext x,CancellationToken ct)
    {if(!Admin(x,out var p,out var t,out var d))return Results.Forbid();var contract=await r.FindAsync(t,d,id,ct);if(contract is null)return Results.NotFound();if(contract.Contract.WorkflowState is not("WAITING_REVIEW" or "NEEDS_REVIEW"))return Results.BadRequest(new{error="contract_not_waiting_review"});if(contract.Contract.HitlActionId is{} actionId){var decision=await hitl.DecideAsync(t,d,actionId,p.UserId,new DecideActionRequest(q.Approve?"approve":"reject",q.Comment),ct);if(decision is null)return Results.BadRequest(new{error="hitl_action_not_pending"});}var a=await r.ReviewAsync(t,d,p.UserId,id,q.Approve,q.Comment,ct);return a is null?Results.BadRequest(new{error="contract_not_waiting_review"}):Results.Ok(new{workflowState=a});}
    private static async Task<IResult> Handoff(Guid id,IContractRepository r,HttpContext x,CancellationToken ct)
    {if(!Admin(x,out var p,out var t,out var d))return Results.Forbid();var a=await r.PrepareHandoffAsync(t,d,p.UserId,id,ct);return a is null?Results.BadRequest(new{error="human_approval_required"}):Results.Ok(a);}
    private static async Task<IResult> Documents(Guid id,IContractRepository contracts,IContractDocumentRepository docs,ICapabilityRepository caps,HttpContext x,CancellationToken ct)
    {if(!Scope(x,out var p,out var t,out var d)||!await Consume(p,"contract.processing",caps,ct))return Results.Forbid();if(await contracts.FindAsync(t,d,id,ct)is null)return Results.NotFound();var rows=await docs.ListAsync(t,d,id,ct);return Results.Ok(new{items=rows,total=rows.Count});}
    private static readonly HashSet<string> DocumentTypes=["contract","sale_contract","purchase_contract","rent_contract","loan_contract","appendix","acceptance","delivery","other"];
    private static async Task<IResult> UploadDocument(Guid id,[FromForm] IFormFile file,[FromForm] string documentType,IContractRepository contracts,IContractDocumentRepository docs,ICapabilityRepository caps,IFileUploadGuard guard,IObjectStorage storage,IOptions<S3Options> options,HttpContext x,CancellationToken ct,[FromForm] string? referencedCode=null)
    {if(!Scope(x,out var p,out var t,out var d)||!await Consume(p,"contract.intake",caps,ct))return Results.Forbid();if(!DocumentTypes.Contains(documentType))return Results.BadRequest(new{error="invalid_document_type"});var contract=await contracts.FindAsync(t,d,id,ct);if(contract is null)return Results.NotFound();if(!CanEdit(p,contract.Contract))return Results.Forbid();var metadata=guard.ValidateMetadata(file.FileName,file.ContentType,file.Length);if(!metadata.Ok)return Results.BadRequest(new{error=metadata.Code,detail=metadata.Reason});await using var input=file.OpenReadStream();await using var buffer=new MemoryStream();await input.CopyToAsync(buffer,ct);buffer.Position=0;var header=buffer.ToArray().AsSpan(0,(int)Math.Min(buffer.Length,16));var bodyCheck=guard.ValidateBody(file.ContentType,header);if(!bodyCheck.Ok)return Results.BadRequest(new{error=bodyCheck.Code,detail=bodyCheck.Reason});buffer.Position=0;var sha=await guard.ComputeSha256Async(buffer,ct);buffer.Position=0;var extension=Path.GetExtension(file.FileName).ToLowerInvariant();var key=TenantObjectKey.Build(t,"contracts",d.ToString("N"),id.ToString("N"),$"{sha}{extension}");var put=await storage.PutAsync(options.Value.ContractBucket,key,buffer,file.ContentType,ct);var row=await docs.AddAsync(t,d,p.UserId,id,documentType,referencedCode,file.FileName,file.ContentType,key,put.Sha256Hex,put.SizeBytes,ct);return row is null?Results.Conflict(new{error="contract_not_editable"}):Results.Created($"/contracts/{id}/documents/{row.Id}",row);}
    // Contract Upload page's file-upload path: upload → extract text →
    // hand the text to the browser for review/edit → user still has to
    // click "Trích xuất + lưu hợp đồng" (POST /api/contracts/upload →
    // save_contract) to actually create anything. This endpoint alone
    // never creates a contract.
    //
    // Own allow-list on purpose — NOT FileUploadOptions/IFileUploadGuard's
    // shared config (that's also used by BookEndpoints and this file's own
    // UploadDocument, both PDF-only today; widening it globally would
    // silently loosen those two as a side effect of this feature).
    private static readonly HashSet<string> ExtractExtensions=[".pdf",".docx",".png",".jpg",".jpeg"];
    private const long ExtractMaxBytes=25L*1024*1024;
    private static readonly Dictionary<string,byte[]> ExtractMagicByExtension=new()
    {
        [".pdf"]=Convert.FromHexString("255044462D"), // %PDF-
        [".docx"]=Convert.FromHexString("504B0304"),  // PK\x03\x04 (docx is a zip)
        [".png"]=Convert.FromHexString("89504E470D0A1A0A"),
        [".jpg"]=Convert.FromHexString("FFD8FF"),
        [".jpeg"]=Convert.FromHexString("FFD8FF"),
    };
    private static readonly Dictionary<string,string> ExtractContentTypeByExtension=new()
    {
        [".pdf"]="application/pdf",
        [".docx"]="application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".png"]="image/png",
        [".jpg"]="image/jpeg",
        [".jpeg"]="image/jpeg",
    };

    private static async Task<IResult> ExtractText(
        [FromForm] IFormFile file,
        IAgentClient agent,
        IFileUploadGuard guard,
        IObjectStorage storage,
        IOptions<S3Options> options,
        ICapabilityRepository caps,
        HttpContext x,
        CancellationToken ct)
    {
        if (!Scope(x, out var p, out var t, out var d) || !await Consume(p, "contract.intake", caps, ct))
            return Results.Forbid();

        var filename = file.FileName ?? "";
        var extension = Path.GetExtension(filename).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(filename))
            return Results.BadRequest(new { error = "missing_filename" });
        if (extension == ".doc")
            return Results.UnprocessableEntity(new
            {
                error = "doc_not_supported",
                detail = "Định dạng .doc cũ chưa được hỗ trợ — vui lòng lưu lại thành .docx hoặc PDF rồi tải lên.",
            });
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
        var header = buffer.ToArray().AsSpan(0, (int)Math.Min(buffer.Length, magic.Length));
        if (header.Length < magic.Length || !header.SequenceEqual(magic))
            return Results.BadRequest(new { error = "magic_byte_mismatch" });
        buffer.Position = 0;

        var contentType = ExtractContentTypeByExtension[extension];

        // Store the original upload now, under a staging key (no contract
        // exists yet — the whole point of this endpoint is text review
        // BEFORE save). Content-hash-keyed like UploadDocument above, so a
        // retry/re-extract of the identical file is a storage no-op. Not
        // linked via IContractDocumentRepository (requires a contract id
        // that doesn't exist yet) — the eventual save_contract call passes
        // this key through as ContractRecord.SourcePdfPath instead, a field
        // that already existed on the contract domain and already flows
        // through end to end (agentic/contract_tools.py save_contract →
        // POST /contracts), just never populated by any caller until now.
        var sha = await guard.ComputeSha256Async(buffer, ct);
        buffer.Position = 0;
        var key = TenantObjectKey.Build(t, "contracts", d.ToString("N"), "staging", $"{sha}{extension}");
        await storage.PutAsync(options.Value.ContractBucket, key, buffer, contentType, ct);
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

    private static async Task<IResult> DeleteDocument(Guid id,Guid documentId,IContractRepository contracts,IContractDocumentRepository docs,ICapabilityRepository caps,IObjectStorage storage,IOptions<S3Options> options,HttpContext x,CancellationToken ct)
    {if(!Scope(x,out var p,out var t,out var d)||!await Consume(p,"contract.intake",caps,ct))return Results.Forbid();var contract=await contracts.FindAsync(t,d,id,ct);if(contract is null)return Results.NotFound();if(!CanEdit(p,contract.Contract))return Results.Forbid();var doc=await docs.FindAsync(t,d,documentId,ct);if(doc is null||doc.ContractId!=id)return Results.NotFound();if(!await docs.DeleteAsync(t,d,documentId,ct))return Results.Conflict(new{error="contract_not_editable"});await storage.DeleteAsync(options.Value.ContractBucket,doc.StorageKey,ct);return Results.NoContent();}
    private static bool CanEdit(PrincipalContext p,ContractRecord c)=>c.WorkflowState is not("APPROVED" or "HANDED_OFF" or "REJECTED")&&(p.IsInRole("admin")||c.UserId==p.UserId);
    private static bool Scope(HttpContext x,out PrincipalContext p,out Guid t,out Guid d){p=null!;t=d=Guid.Empty;if(!AuthenticatedPrincipalContext.TryGet(x,out p)||p.TenantId is not{}a||p.DepartmentId is not{}b||(!p.IsInRole("admin")&&!p.IsInRole("user")))return false;t=a;d=b;return true;}
    private static bool Admin(HttpContext x,out PrincipalContext p,out Guid t,out Guid d)=>Scope(x,out p,out t,out d)&&p.IsInRole("admin");
    private static async Task<bool> Consume(PrincipalContext p,string code,ICapabilityRepository r,CancellationToken ct){if(p.IsInRole("admin"))return true;var a=await r.ResolveAgentAsync(p.TenantId!.Value,p.DepartmentId!.Value,p.UserId,false,code,null,ct);if(a.AgentId is not null)return true;return (await r.ResolveAgentAsync(p.TenantId.Value,p.DepartmentId.Value,p.UserId,false,"contract.processing",null,ct)).AgentId is not null;}
    private static bool Valid(object v,out Dictionary<string,string[]> e){var r=new List<ValidationResult>();var ok=Validator.TryValidateObject(v,new ValidationContext(v),r,true);e=r.SelectMany(a=>a.MemberNames.DefaultIfEmpty("request").Select(m=>(m,a.ErrorMessage))).GroupBy(a=>a.m).ToDictionary(g=>g.Key,g=>g.Select(a=>a.ErrorMessage??"invalid").ToArray());return ok;}
}
