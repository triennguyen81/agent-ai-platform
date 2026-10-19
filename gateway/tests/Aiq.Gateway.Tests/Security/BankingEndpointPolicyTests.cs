using Aiq.Gateway.Banking;
using Aiq.Gateway.Data;
using Aiq.Gateway.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Aiq.Gateway.Tests.Security;

/// <summary>
/// ACL V2 unchanged: Bank Account Master and statement-review MUTATIONS are
/// "department_admin" (USER denied, TENANT_OWNER not involved); reads keep
/// the parent module's member access.
/// </summary>
public sealed class BankingEndpointPolicyTests
{
    private static IReadOnlyList<RouteEndpoint> Map()
    {
        var builder = WebApplication.CreateBuilder();
        var factory = new NpgsqlConnectionFactory("Host=unused");
        builder.Services.AddSingleton<IBankAccountRepository>(new BankAccountRepository(factory));
        builder.Services.AddSingleton<IBankStatementRepository>(sp => new BankStatementRepository(factory, sp.GetRequiredService<IBankAccountRepository>()));
        builder.Services.AddSingleton<Aiq.Gateway.Payments.IStatementSettlement>(new Aiq.Gateway.Payments.CashSettlementRepository(factory));
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.MapGroup("/customers").MapBankAccounts(BankOwnerTypes.Customer);
        app.MapGroup("/suppliers").MapBankAccounts(BankOwnerTypes.Supplier);
        app.MapGroup("/vouchers").MapBankStatementLines();
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
    }

    private static string[] Policies(RouteEndpoint e) => e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy ?? "").ToArray();

    private static string Method(RouteEndpoint e) =>
        e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods.Single();

    [Fact]
    public void EveryMutation_RequiresDepartmentAdmin_ReadsDoNot()
    {
        var endpoints = Map();
        Assert.Equal(22, endpoints.Count);   // + settlement/discount/confirm
        foreach (var e in endpoints)
        {
            if (Method(e) == "GET") Assert.DoesNotContain("department_admin", Policies(e));
            else Assert.Equal(new[] { "department_admin" }, Policies(e));
        }
    }
}
