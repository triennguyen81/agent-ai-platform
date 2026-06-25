using System.ComponentModel.DataAnnotations;
using Aiq.Gateway.Contracts;
using Microsoft.AspNetCore.Http;

namespace Aiq.Gateway.Endpoints;

public static class ContractEndpoints
{
    public static IEndpointRouteBuilder MapContractEndpoints(this IEndpointRouteBuilder app)
    {
        // admin / super_admin can manage contracts. Pure consumers (role=user)
        // don't get access — contracts contain business-sensitive data.
        var group = app
            .MapGroup("/contracts")
            .RequireAuthorization("admin")
            .RequireRateLimiting("default");

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPatch("/{id:guid}", UpdateAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);

        return app;
    }

    private static async Task<IResult> ListAsync(
        IContractRepository repo,
        HttpContext ctx,
        CancellationToken ct,
        string? status = null,
        string? q = null,
        int limit = 50,
        int offset = 0)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }

        var items = await repo.ListAsync(userId, status, q, limit, offset, ct);
        return Results.Ok(new ContractsListResponse(items, items.Count));
    }

    private static async Task<IResult> CreateAsync(
        CreateContractRequest request,
        IContractRepository repo,
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

        var created = await repo.CreateAsync(userId, request, ct);
        return Results.Created($"/contracts/{created.Contract.Id}", created);
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        IContractRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }

        var contract = await repo.FindAsync(userId, id, ct);
        return contract is null ? Results.NotFound() : Results.Ok(contract);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateContractRequest request,
        IContractRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }

        var updated = await repo.UpdateAsync(userId, id, request, ct);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        IContractRepository repo,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            return Results.Unauthorized();
        }

        var deleted = await repo.DeleteAsync(userId, id, ct);
        return deleted ? Results.NoContent() : Results.NotFound();
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
        var ctx = new ValidationContext(instance);
        var results = new List<ValidationResult>();
        var ok = Validator.TryValidateObject(instance, ctx, results, validateAllProperties: true);
        errors = results
            .SelectMany(r => r.MemberNames.Select(m => (Member: m, r.ErrorMessage)))
            .GroupBy(t => t.Member)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage ?? "Invalid").ToArray());
        return ok;
    }
}
