using Aiq.Gateway.Endpoints;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;
using Aiq.Gateway.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Aiq.Gateway.Tests.Security;

/// <summary>
/// ADMIN Department Authority Invariant (docs/ACL_V2_RESOURCE_VISIBILITY_RULES.md §15)
/// — the boundaries that still hold: every authoritative accounting decision
/// is department-ADMIN only (USER never self-approves), TENANT_OWNER and
/// SUPER_ADMIN carry no department operational authority, and scope always
/// comes from the authenticated principal's own tenant + department.
/// </summary>
public sealed class AdminDepartmentAuthorityInvariantTests
{
    private static IReadOnlyList<RouteEndpoint> MapVoucherRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
        var handlerParams = new[] { typeof(VoucherEndpoints), typeof(CashSettlementEndpoints), typeof(BankingEndpoints) }
            .SelectMany(t => t.GetNestedTypes(all).Append(t))
            .SelectMany(t => t.GetMethods(all))
            .SelectMany(m => m.GetParameters()).Select(p => p.ParameterType).Distinct();
        foreach (var t in handlerParams)
        {
            var injected = !t.IsValueType && t != typeof(string) && t != typeof(HttpContext) && t != typeof(IFormFile)
                && !t.Name.EndsWith("Request", StringComparison.Ordinal) && !t.Name.EndsWith("Body", StringComparison.Ordinal);
            if (injected && !builder.Services.Any(d => d.ServiceType == t))
                builder.Services.AddSingleton(t, _ => throw new InvalidOperationException("not used in metadata tests"));
        }
        var app = builder.Build();
        app.MapVoucherEndpoints();
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
    }

    private static string[] Policies(RouteEndpoint e) => e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy ?? "").ToArray();

    /// <summary>USER may create/upload, but every authoritative decision requires the department ADMIN policy.</summary>
    [Theory]
    [InlineData("/{id:guid}/expense-claim/department-review")]
    [InlineData("/{id:guid}/expense-claim/accounting-review")]
    [InlineData("/{id:guid}/expense-claim/pay")]
    [InlineData("/{id:guid}/reject-expense-claim")]
    [InlineData("/{id:guid}/grant-expense-exception")]
    [InlineData("/{id:guid}/apply-expense")]
    [InlineData("/{id:guid}/apply-invoice")]
    [InlineData("/{id:guid}/tax-verification/confirm")]
    [InlineData("/{id:guid}/settlement/post")]
    [InlineData("/{id:guid}/document-review/{taskId:guid}/decision")]
    public void Authoritative_decisions_are_department_admin_only(string route)
    {
        var e = MapVoucherRoutes().Single(x => x.RoutePattern.RawText!.EndsWith(route, StringComparison.Ordinal)
            && x.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST"));
        Assert.Contains("department_admin", Policies(e));
    }

    private static PrincipalContext P(string role, Guid? tenant, Guid? dept) =>
        new(Guid.NewGuid(), tenant, dept, role, true, true, dept is null ? null : true);

    [Fact]
    public void Only_a_department_admin_holds_operational_authority()
    {
        var tenant = Guid.NewGuid();
        var dept = Guid.NewGuid();
        Assert.True(PrincipalAuthorizationPolicy.IsDepartmentAdmin(P("admin", tenant, dept)));
        Assert.False(PrincipalAuthorizationPolicy.IsDepartmentAdmin(P("user", tenant, dept)));
        Assert.False(PrincipalAuthorizationPolicy.IsDepartmentAdmin(P("tenant_owner", tenant, null)));
        Assert.False(PrincipalAuthorizationPolicy.IsDepartmentAdmin(P("super_admin", null, null)));
    }

    private static bool Scope(PrincipalContext p, out Guid tenant, out Guid dept)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthenticatedPrincipalContext.ItemKey] = p;
        return BusinessModuleAuthorization.TryScope(ctx, out _, out tenant, out dept);
    }

    [Fact]
    public void Tenant_owner_and_super_admin_never_get_a_department_scope()
    {
        Assert.False(Scope(P("tenant_owner", Guid.NewGuid(), null), out _, out _));
        Assert.False(Scope(P("tenant_owner", Guid.NewGuid(), Guid.NewGuid()), out _, out _));
        Assert.False(Scope(P("super_admin", null, null), out _, out _));
        Assert.False(Scope(P("super_admin", Guid.NewGuid(), Guid.NewGuid()), out _, out _));
    }

    [Fact]
    public void Scope_is_always_the_principals_own_tenant_and_department()
    {
        var tenant = Guid.NewGuid();
        var dept = Guid.NewGuid();
        Assert.True(Scope(P("admin", tenant, dept), out var t, out var d));
        Assert.Equal((tenant, dept), (t, d));
        Assert.True(Scope(P("user", tenant, dept), out t, out d));
        Assert.Equal((tenant, dept), (t, d));
        Assert.False(Scope(P("admin", tenant, null), out _, out _));   // no department → no operational scope
        Assert.False(Scope(P("admin", null, dept), out _, out _));
    }
}
