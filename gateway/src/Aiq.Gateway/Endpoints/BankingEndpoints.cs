using Aiq.Gateway.Banking;
using Aiq.Gateway.Security;

namespace Aiq.Gateway.Endpoints;

/// <summary>
/// Customer/Supplier Bank Account Master + bank statement lines.
///
/// ACL (ACL V2, unchanged): reading follows the parent module's existing
/// "department_member" + capability gate; every mutation is
/// "department_admin". Tenant/department always come from the authenticated
/// principal (BusinessModuleAuthorization.TryScope) — never from the request.
/// </summary>
public static class BankingEndpoints
{
    public sealed record BankAccountBody(string? BankCode, string? BankName, string? AccountNumber, string? AccountName,
        string? Branch, string? Currency, bool? IsPrimary);

    public sealed record ConfirmMatchBody(Guid? OwnerId, bool? SaveBankAccount);

    /// <summary>Called inside /customers or /suppliers groups.</summary>
    public static RouteGroupBuilder MapBankAccounts(this RouteGroupBuilder group, string ownerType)
    {
        group.MapGet("/{id:guid}/bank-accounts", (Guid id, IBankAccountRepository repo, HttpContext ctx, CancellationToken ct) =>
            ListAsync(ownerType, id, repo, ctx, ct));
        group.MapPost("/{id:guid}/bank-accounts", (Guid id, BankAccountBody body, IBankAccountRepository repo, HttpContext ctx, CancellationToken ct) =>
            Mutate(ctx, (t, d, actor) => repo.CreateAsync(t, d, ownerType, id, actor, ToInput(body), ct)))
            .RequireAuthorization("department_admin");
        group.MapPatch("/{id:guid}/bank-accounts/{accountId:guid}", (Guid id, Guid accountId, BankAccountBody body, IBankAccountRepository repo, HttpContext ctx, CancellationToken ct) =>
            Mutate(ctx, (t, d, actor) => repo.UpdateAsync(t, d, ownerType, id, accountId, actor, ToInput(body), ct)))
            .RequireAuthorization("department_admin");
        group.MapPost("/{id:guid}/bank-accounts/{accountId:guid}/deactivate", (Guid id, Guid accountId, IBankAccountRepository repo, HttpContext ctx, CancellationToken ct) =>
            Mutate(ctx, (t, d, actor) => repo.SetActiveAsync(t, d, ownerType, id, accountId, actor, false, ct)))
            .RequireAuthorization("department_admin");
        group.MapPost("/{id:guid}/bank-accounts/{accountId:guid}/reactivate", (Guid id, Guid accountId, IBankAccountRepository repo, HttpContext ctx, CancellationToken ct) =>
            Mutate(ctx, (t, d, actor) => repo.SetActiveAsync(t, d, ownerType, id, accountId, actor, true, ct)))
            .RequireAuthorization("department_admin");
        return group;
    }

    /// <summary>Called inside the /vouchers group.</summary>
    public static RouteGroupBuilder MapBankStatementLines(this RouteGroupBuilder group)
    {
        group.MapGet("/{id:guid}/bank-transactions", async (Guid id, IBankStatementRepository repo, HttpContext ctx, CancellationToken ct) =>
        {
            if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var t, out var d)) return Results.Forbid();
            var rows = await repo.ListAsync(t, d, id, ct);
            return rows is null ? Results.NotFound() : Results.Ok(new { transactions = rows, canReview = principal.IsInRole("admin") });
        });
        group.MapPost("/{id:guid}/bank-transactions/rematch", async (Guid id, IBankStatementRepository repo, HttpContext ctx, CancellationToken ct) =>
        {
            if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var t, out var d)) return Results.Forbid();
            var n = await repo.RematchAsync(t, d, id, ct);
            return n is null ? Results.NotFound() : Results.Ok(new { rematched = n });
        }).RequireAuthorization("department_admin");
        group.MapPost("/{id:guid}/bank-transactions/{transactionId:guid}/match",
            (Guid id, Guid transactionId, ConfirmMatchBody body, IBankStatementRepository repo, HttpContext ctx, CancellationToken ct) =>
            {
                if (body.OwnerId is not { } ownerId) return Task.FromResult(Results.BadRequest(new { error = "ownerId required" }));
                return Mutate(ctx, (t, d, actor) => repo.ConfirmMatchAsync(t, d, id, transactionId, actor, ownerId, body.SaveBankAccount ?? false, ct));
            }).RequireAuthorization("department_admin");

        // ---- money_in line -> canonical cash settlement (AR) -------------
        group.MapGet("/company-bank-accounts", async (Aiq.Gateway.Payments.IStatementSettlement settle, HttpContext ctx, CancellationToken ct) =>
        {
            if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var t, out _)) return Results.Forbid();
            return Results.Ok(await settle.ListCompanyBankAccountsAsync(t, ct));
        });
        group.MapPost("/{id:guid}/bank-transactions/company-account", (Guid id, LinkCompanyAccountBody body, Aiq.Gateway.Payments.IStatementSettlement settle, HttpContext ctx, CancellationToken ct) =>
            body.BankAccountId is not { } bid ? Task.FromResult(Results.BadRequest(new { error = "bankAccountId required" }))
            : Settle(ctx, async (t, d, actor) => await settle.LinkCompanyBankAccountAsync(t, d, id, bid, actor, ct) is { } n ? new { linkedLines = n } : null))
            .RequireAuthorization("department_admin");
        group.MapGet("/{id:guid}/bank-transactions/{txId:guid}/settlement", async (Guid id, Guid txId, Aiq.Gateway.Payments.IStatementSettlement settle, HttpContext ctx, CancellationToken ct) =>
        {
            if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var t, out var d)) return Results.Forbid();
            var context = await settle.GetStatementLineContextAsync(t, d, id, txId, ct);
            return context is null ? Results.NotFound() : Results.Ok(new { context, canApprove = principal.IsInRole("admin") });
        });
        group.MapPost("/{id:guid}/bank-transactions/{txId:guid}/settlement/post", (Guid id, Guid txId, PostLineBody? body, Aiq.Gateway.Payments.IStatementSettlement settle, HttpContext ctx, CancellationToken ct) =>
            Settle(ctx, (t, d, actor) => settle.PostStatementLineAsync(t, d, id, txId, actor,
                new Aiq.Gateway.Payments.PostStatementLineRequest(body?.Allocations, body?.LeaveUnapplied ?? false, body?.ConfirmNotDuplicate ?? false), ct)))
            .RequireAuthorization("department_admin");
        group.MapPost("/{id:guid}/bank-transactions/{txId:guid}/settlement/discount/confirm", (Guid id, Guid txId, Aiq.Gateway.Payments.ConfirmPaymentDiscountRequest body, Aiq.Gateway.Payments.IStatementSettlement settle, HttpContext ctx, CancellationToken ct) =>
            Settle(ctx, (t, d, actor) => settle.ConfirmStatementLinePaymentDiscountAsync(t, d, id, txId, actor, body, ct)))
            .RequireAuthorization("department_admin");
        group.MapPost("/{id:guid}/bank-transactions/{txId:guid}/settlement/allocate", (Guid id, Guid txId, AllocateLineBody? body, Aiq.Gateway.Payments.IStatementSettlement settle, HttpContext ctx, CancellationToken ct) =>
            Settle(ctx, (t, d, actor) => settle.AllocateStatementLineAsync(t, d, id, txId, actor,
                (IReadOnlyList<Aiq.Gateway.Payments.AllocationInput>?)body?.Allocations ?? Array.Empty<Aiq.Gateway.Payments.AllocationInput>(), ct)))
            .RequireAuthorization("department_admin");
        group.MapPost("/{id:guid}/bank-transactions/{txId:guid}/settlement/unapply", (Guid id, Guid txId, UnapplyLineBody? body, Aiq.Gateway.Payments.IStatementSettlement settle, HttpContext ctx, CancellationToken ct) =>
            Settle(ctx, (t, d, actor) => settle.UnapplyStatementLineAsync(t, d, id, txId, actor, body?.ApplicationId, body?.Reason, ct)))
            .RequireAuthorization("department_admin");
        group.MapPost("/{id:guid}/bank-transactions/{txId:guid}/settlement/reverse", (Guid id, Guid txId, ReverseLineBody? body, Aiq.Gateway.Payments.IStatementSettlement settle, HttpContext ctx, CancellationToken ct) =>
            Settle(ctx, (t, d, actor) => settle.ReverseStatementLineAsync(t, d, id, txId, actor, body?.Reason, ct)))
            .RequireAuthorization("department_admin");
        group.MapPost("/{id:guid}/bank-transactions/{txId:guid}/settlement/reconcile", (Guid id, Guid txId, ReconcileLineBody body, Aiq.Gateway.Payments.IStatementSettlement settle, HttpContext ctx, CancellationToken ct) =>
            body.CashTransactionId is not { } cid ? Task.FromResult(Results.BadRequest(new { error = "cashTransactionId required" }))
            : Settle(ctx, (t, d, actor) => settle.ReconcileStatementLineAsync(t, d, id, txId, actor, cid, ct)))
            .RequireAuthorization("department_admin");
        return group;
    }

    public sealed record LinkCompanyAccountBody(Guid? BankAccountId);
    public sealed record PostLineBody(List<Aiq.Gateway.Payments.AllocationInput>? Allocations, bool? LeaveUnapplied, bool? ConfirmNotDuplicate);
    public sealed record AllocateLineBody(List<Aiq.Gateway.Payments.AllocationInput>? Allocations);
    public sealed record UnapplyLineBody(Guid? ApplicationId, string? Reason);
    public sealed record ReverseLineBody(string? Reason);
    public sealed record ReconcileLineBody(Guid? CashTransactionId);

    private static async Task<IResult> Settle<T>(HttpContext ctx, Func<Guid, Guid, Guid, Task<T?>> action) where T : class
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var t, out var d)) return Results.Forbid();
        try
        {
            var result = await action(t, d, principal.UserId);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (Aiq.Gateway.Payments.CashSettlementException ex)
        {
            return CashSettlementEndpoints.ToProblem(ex, ctx, t, d, principal.UserId);
        }
    }

    private static async Task<IResult> ListAsync(string ownerType, Guid id, IBankAccountRepository repo, HttpContext ctx, CancellationToken ct)
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out _, out var t, out var d)) return Results.Forbid();
        var rows = await repo.ListAsync(t, d, ownerType, id, ct);
        return rows is null ? Results.NotFound() : Results.Ok(rows);
    }

    private static async Task<IResult> Mutate<T>(HttpContext ctx, Func<Guid, Guid, Guid, Task<T?>> action) where T : class
    {
        if (!BusinessModuleAuthorization.TryScope(ctx, out var principal, out var t, out var d)) return Results.Forbid();
        try
        {
            var result = await action(t, d, principal.UserId);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (BankAccountException ex)
        {
            return ex.Code is "invalid_account_number" or "no_counterparty_account" or "invalid_owner_type"
                ? Results.BadRequest(new { error = ex.Code, message = ex.Message, details = ex.Details })
                : Results.Conflict(new { error = ex.Code, message = ex.Message, details = ex.Details });
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
        {
            return Results.Conflict(new { error = "bank_account_owned_by_other", message = "Tài khoản ngân hàng này đã được khai báo." });
        }
    }

    private static BankAccountInput ToInput(BankAccountBody b) =>
        new(b.BankCode, b.BankName, b.AccountNumber, b.AccountName, b.Branch, b.Currency, b.IsPrimary);
}
