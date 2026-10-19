using Aiq.Gateway.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Aiq.Gateway.Tests.Security;

/// <summary>
/// Tax verification routes: only the department ADMIN may decide (confirm =
/// verified, reject = invalid/unavailable); reading and proposing stay member
/// actions (a proposal is never authoritative).
/// </summary>
public sealed class TaxVerificationEndpointPolicyTests
{
    /// <summary>
    /// Registers a placeholder for every injected dependency of the voucher
    /// handlers so the minimal-API factory can build the routes (and expose
    /// their authorization metadata) without a database. Never invoked.
    /// </summary>
    private static IReadOnlyList<RouteEndpoint> MapVoucherRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        // The /vouchers group also mounts the cash-settlement and statement-line
        // routes; lambdas live in compiler-generated nested types.
        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
        var handlerParams = new[] { typeof(VoucherEndpoints), typeof(CashSettlementEndpoints), typeof(BankingEndpoints) }
            .SelectMany(t => t.GetNestedTypes(all).Append(t))
            .SelectMany(t => t.GetMethods(all))
            .SelectMany(m => m.GetParameters()).Select(p => p.ParameterType).Distinct();
        foreach (var t in handlerParams)
        {
            var injected = !t.IsValueType && t != typeof(string)
                && t != typeof(Microsoft.AspNetCore.Http.HttpContext)
                && t != typeof(Microsoft.AspNetCore.Http.IFormFile)
                && !t.Name.EndsWith("Request", StringComparison.Ordinal)
                && !t.Name.EndsWith("Body", StringComparison.Ordinal);
            if (injected && !builder.Services.Any(d => d.ServiceType == t))
                builder.Services.AddSingleton(t, _ => throw new InvalidOperationException("not used in metadata tests"));
        }
        var app = builder.Build();
        app.MapVoucherEndpoints();
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
    }

    private static string[] Policies(RouteEndpoint e) =>
        e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy ?? "").ToArray();

    private static RouteEndpoint Route(string raw, string method) =>
        MapVoucherRoutes().Single(e => e.RoutePattern.RawText!.EndsWith(raw, StringComparison.Ordinal)
            && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));

    [Theory]
    [InlineData("/{id:guid}/tax-verification/confirm")]
    [InlineData("/{id:guid}/tax-verification/reject")]
    public void AuthoritativeDecisions_RequireDepartmentAdmin(string route)
    {
        Assert.Contains("department_admin", Policies(Route(route, "POST")));
    }

    [Theory]
    [InlineData("/{id:guid}/tax-verification", "GET")]
    [InlineData("/{id:guid}/tax-verification/propose", "POST")]
    public void ReadAndPropose_AreNotAdminOnly(string route, string method)
    {
        Assert.DoesNotContain("department_admin", Policies(Route(route, method)));
    }

    [Fact]
    public void DepartmentAdminPolicy_OnlyAdminOfTheDepartment_NotUserNorTenantOwner()
    {
        var tenant = Guid.NewGuid();
        var department = Guid.NewGuid();
        Aiq.Gateway.Middleware.PrincipalContext P(string role, Guid? dept) =>
            new(Guid.NewGuid(), tenant, dept, role, true, true, dept is null ? null : true);
        Assert.True(Aiq.Gateway.Governance.PrincipalAuthorizationPolicy.IsDepartmentAdmin(P("admin", department)));
        Assert.False(Aiq.Gateway.Governance.PrincipalAuthorizationPolicy.IsDepartmentAdmin(P("user", department)));
        Assert.False(Aiq.Gateway.Governance.PrincipalAuthorizationPolicy.IsDepartmentAdmin(P("tenant_owner", null)));
    }

    [Fact]
    public void ExpenseClaimPay_RequiresDepartmentAdmin_ReadDoesNot()
    {
        Assert.Contains("department_admin", Policies(Route("/{id:guid}/expense-claim/pay", "POST")));
        Assert.DoesNotContain("department_admin", Policies(Route("/{id:guid}/expense-claim/payment", "GET")));
    }
}
