using Aiq.Gateway.Data;
using Aiq.Gateway.Endpoints;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;
using Aiq.Gateway.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Aiq.Gateway.Tests.Security;

/// <summary>
/// Final ACL rule for Chứng từ THU / CHI: the owning Department's ADMIN has
/// full operational authority (post / allocate / unapply / reverse, including
/// on its own voucher); USER may read but never post; nothing requires
/// TENANT_OWNER, SUPER_ADMIN or a second ADMIN.
/// </summary>
public sealed class CashSettlementEndpointPolicyTests
{
    private static IReadOnlyList<RouteEndpoint> MapSettlementEndpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ICashSettlementRepository>(new CashSettlementRepository(new NpgsqlConnectionFactory("Host=unused")));
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.MapGroup("/vouchers").MapCashSettlement();
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
    }

    private static string[] Policies(RouteEndpoint e) =>
        e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy ?? "").ToArray();

    [Theory]
    [InlineData("/vouchers/{id:guid}/settlement/post")]
    [InlineData("/vouchers/{id:guid}/settlement/allocate")]
    [InlineData("/vouchers/{id:guid}/settlement/unapply")]
    [InlineData("/vouchers/{id:guid}/settlement/reverse")]
    [InlineData("/vouchers/{id:guid}/settlement/discount/confirm")]
    public void EveryMutation_RequiresExactlyDepartmentAdmin(string route)
    {
        var endpoint = MapSettlementEndpoints().Single(e => e.RoutePattern.RawText == route);
        Assert.Equal(new[] { "department_admin" }, Policies(endpoint));
    }

    [Fact]
    public void Read_HasNoAdminOnlyPolicy()
    {
        var endpoint = MapSettlementEndpoints().Single(e => e.RoutePattern.RawText == "/vouchers/{id:guid}/settlement");
        Assert.DoesNotContain("department_admin", Policies(endpoint));
    }

    [Fact]
    public void DepartmentAdminPolicy_AdminPasses_UserAndTenantOwnerDoNot()
    {
        var tenant = Guid.NewGuid();
        var department = Guid.NewGuid();
        PrincipalContext P(string role, Guid? dept) =>
            new(Guid.NewGuid(), tenant, dept, role, true, true, dept is null ? null : true);

        Assert.True(PrincipalAuthorizationPolicy.IsDepartmentAdmin(P("admin", department)));
        Assert.False(PrincipalAuthorizationPolicy.IsDepartmentAdmin(P("user", department)));
        // TENANT_OWNER is neither required nor implicitly granted department posting.
        Assert.False(PrincipalAuthorizationPolicy.IsDepartmentAdmin(P("tenant_owner", null)));
    }
}
