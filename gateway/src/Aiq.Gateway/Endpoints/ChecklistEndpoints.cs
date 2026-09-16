using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Contracts;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;

namespace Aiq.Gateway.Endpoints;

public static class ChecklistEndpoints
{
    public static IEndpointRouteBuilder MapChecklistEndpoints(this IEndpointRouteBuilder app)
    {
        var contracts=app.MapGroup("/contracts/{contractId:guid}/checklist").RequireAuthorization("department_member").RequireRateLimiting("default");
        contracts.MapGet("/",ListAsync); contracts.MapPost("/",BulkCreateAsync);
        var items=app.MapGroup("/checklist/{itemId:guid}").RequireAuthorization("department_member").RequireRateLimiting("default");
        items.MapPatch("/",UpdateAsync); items.MapDelete("/",DeleteAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(Guid contractId,IChecklistRepository repo,IContractRepository contracts,ICapabilityRepository caps,HttpContext ctx,CancellationToken ct)
    {
        if(!TryScope(ctx,out var p,out var tenantId,out var departmentId)||!await CanUseAsync(p,caps,ct))return Results.Forbid();
        if(await contracts.FindAsync(tenantId,departmentId,contractId,ct) is null)return Results.NotFound();
        var rows=await repo.ListAsync(tenantId,departmentId,contractId,ct);return Results.Ok(new{items=rows,total=rows.Count});
    }

    private static async Task<IResult> BulkCreateAsync(Guid contractId,BulkCreateChecklistRequest request,IChecklistRepository repo,IContractRepository contracts,ICapabilityRepository caps,HttpContext ctx,CancellationToken ct)
    {
        if(!TryValidate(request,out var errors))return Results.ValidationProblem(errors);
        if(request.Items.Count is 0 or >100)return Results.BadRequest(new{error="items phải có từ 1 đến 100 phần tử"});
        if(!TryScope(ctx,out var p,out var tenantId,out var departmentId)||!await CanUseAsync(p,caps,ct))return Results.Forbid();
        var contract=await contracts.FindAsync(tenantId,departmentId,contractId,ct);if(contract is null)return Results.NotFound();
        if(!CanMutate(p,contract.Contract.UserId,contract.Contract.WorkflowState))return Results.Forbid();
        var rows=await repo.BulkCreateAsync(tenantId,departmentId,contractId,request,ct);
        return rows.Count==0?Results.BadRequest(new{error="contract_not_editable"}):Results.Created($"/contracts/{contractId}/checklist",new{items=rows,total=rows.Count});
    }

    private static async Task<IResult> UpdateAsync(Guid itemId,UpdateChecklistItemRequest request,IChecklistRepository repo,ICapabilityRepository caps,HttpContext ctx,CancellationToken ct)
    {
        if(request.Status is {Length:>0} status&&status is not("open" or "in_progress" or "done" or "skipped"))return Results.BadRequest(new{error=$"Status không hợp lệ: {status}"});
        if(!TryScope(ctx,out var p,out var tenantId,out var departmentId)||!await CanUseAsync(p,caps,ct))return Results.Forbid();
        var parent=await repo.FindParentAsync(tenantId,departmentId,itemId,ct);if(parent is null)return Results.NotFound();
        if(!CanMutate(p,parent.CreatedBy,parent.WorkflowState))return Results.Forbid();
        var row=await repo.UpdateAsync(tenantId,departmentId,p.UserId,itemId,request,ct);return row is null?Results.NotFound():Results.Ok(row);
    }

    private static async Task<IResult> DeleteAsync(Guid itemId,IChecklistRepository repo,ICapabilityRepository caps,HttpContext ctx,CancellationToken ct)
    {
        if(!TryScope(ctx,out var p,out var tenantId,out var departmentId)||!await CanUseAsync(p,caps,ct))return Results.Forbid();
        var parent=await repo.FindParentAsync(tenantId,departmentId,itemId,ct);if(parent is null)return Results.NotFound();
        if(!CanMutate(p,parent.CreatedBy,parent.WorkflowState))return Results.Forbid();
        return await repo.DeleteAsync(tenantId,departmentId,itemId,ct)?Results.NoContent():Results.NotFound();
    }

    private static bool CanMutate(PrincipalContext p,Guid createdBy,string state)=>state is not("APPROVED" or "HANDED_OFF" or "REJECTED")&&(p.IsInRole("admin")||createdBy==p.UserId);
    private static async Task<bool> CanUseAsync(PrincipalContext p,ICapabilityRepository repo,CancellationToken ct)
    {if(p.IsInRole("admin"))return true;return (await repo.ResolveAgentAsync(p.TenantId!.Value,p.DepartmentId!.Value,p.UserId,false,"contract.processing",ct)).AgentId is not null;}
    private static bool TryScope(HttpContext ctx,out PrincipalContext p,out Guid tenantId,out Guid departmentId)
    {p=null!;tenantId=departmentId=Guid.Empty;if(!AuthenticatedPrincipalContext.TryGet(ctx,out p)||p.TenantId is not{}t||p.DepartmentId is not{}d||(!p.IsInRole("admin")&&!p.IsInRole("user")))return false;tenantId=t;departmentId=d;return true;}
    private static bool TryValidate(object value,out Dictionary<string,string[]> errors)
    {var results=new List<ValidationResult>();var valid=Validator.TryValidateObject(value,new ValidationContext(value),results,true);errors=results.SelectMany(r=>r.MemberNames.DefaultIfEmpty("request").Select(m=>(m,r.ErrorMessage))).GroupBy(x=>x.m).ToDictionary(g=>g.Key,g=>g.Select(x=>x.ErrorMessage??"invalid").ToArray());return valid;}
}
