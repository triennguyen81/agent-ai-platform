using System.Security.Claims;
using System.Text.Json;
using Aiq.Gateway.Data;
using Aiq.Gateway.Endpoints;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;
using Aiq.Gateway.Vouchers;
using Aiq.Gateway.Vouchers.Lifecycle;
using Aiq.Gateway.Vouchers.Periods;
using Aiq.Gateway.Vouchers.Validation;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// /accounting-periods (docs/flows/chung-tu-proposed.md §5, §10.1) and the
/// 409 period_locked every voucher write answers in a closed period. ADMIN
/// Department Authority Invariant: a same-department ADMIN closes and reopens;
/// USER, TENANT_OWNER, SUPER_ADMIN and the agent never do; another department
/// only ever touches its own periods. Only for v2 tenants.
/// </summary>
public sealed class AccountingPeriodEndpointTests(TaxVerificationDatabase fx) : IClassFixture<TaxVerificationDatabase>
{
    // "Now" is 15/12/2011 in Vietnam for every test here.
    private static readonly TimeProvider Time = new FixedTime(new DateTimeOffset(2011, 12, 15, 3, 0, 0, TimeSpan.Zero));

    static AccountingPeriodEndpointTests()
    {
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyTypeHandler());
    }

    private NpgsqlConnectionFactory Factory => new(fx.ConnectionString!);
    private AccountingPeriodRepository Periods => new(Factory);
    private TenantFeatureFlagRepository Flags => new(Factory);
    private VoucherRepository Repo => new(Factory);
    private VoucherValidationService Validation => new(Repo, new TenantCompanyProfileRepository(Factory), new VoucherValidationRepository(Factory),
        Flags, new VoucherLifecycleRepository(Factory), Periods, Time, NullLogger<VoucherValidationService>.Instance);

    private Task<IResult> Close(string period, string role = "admin", Guid? user = null, Guid? department = null, bool agent = false) =>
        AccountingPeriodEndpoints.CloseAsync(period, Periods, Flags, Time, Ctx(role, user ?? fx.Admin, department, agent), CancellationToken.None);

    private Task<IResult> Reopen(string period, string? reason, string role = "admin", Guid? department = null, bool agent = false) =>
        AccountingPeriodEndpoints.ReopenAsync(period, new PeriodReopenRequest(reason), Periods, Flags, Ctx(role, fx.Admin, department, agent), CancellationToken.None);

    // ------------------------------------------------------------ route policy

    [Theory]
    [InlineData("/accounting-periods/{period}/close")]
    [InlineData("/accounting-periods/{period}/reopen")]
    public void Close_and_reopen_are_department_admin_only(string route)
    {
        var e = Routes().Single(x => x.RoutePattern.RawText == route);
        Assert.Contains("department_admin", e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy));
    }

    [Theory]
    [InlineData("/accounting-periods/")]
    [InlineData("/accounting-periods/{period}/events")]
    public void Reading_periods_is_for_department_members(string route)
    {
        var e = Routes().Single(x => x.RoutePattern.RawText == route);
        var policies = e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToArray();
        Assert.Contains("department_member", policies);
        Assert.DoesNotContain("department_admin", policies);
    }

    // ------------------------------------------------------------ v2 off

    [Fact]
    public async Task Without_the_v2_flag_periods_are_not_available_and_nothing_changes()
    {
        if (fx.ConnectionString is null) return;
        Assert.Equal(VoucherLifecycleEndpoints.FlowDisabled, ErrorOf(await Close("2011-01")));
        Assert.Equal(VoucherLifecycleEndpoints.FlowDisabled, ErrorOf(await AccountingPeriodEndpoints.ListAsync(
            2011, Periods, Flags, Time, Ctx("admin", fx.Admin), CancellationToken.None)));

        // A closed row (left by an earlier v2 period, say) blocks nothing without the flag.
        await ExecAsync("""
            INSERT INTO accounting_periods(tenant_id, department_id, year, month, status, closed_at) VALUES (@t, @d, 2011, 2, 'closed', NOW())
            """, new { t = fx.Tenant, d = fx.Dept });
        var id = await VoucherAsync(new DateOnly(2011, 2, 10));
        var patched = await VoucherEndpoints.UpdateAsync(id, Patch(total: 3m), Repo, Validation, Flags, Ctx("user", fx.User), CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusOf(patched));
        Assert.DoesNotContain((await Validation.ComputeAsync(fx.Tenant, fx.Dept, id, CancellationToken.None))!.Issues, i => i.Code == "PERIOD_LOCKED");
        // accountingDate is a v2 field.
        Assert.Equal("accounting_date_requires_v2", ErrorOf(await VoucherEndpoints.UpdateAsync(
            id, Patch(accountingDate: new DateOnly(2011, 3, 1)), Repo, Validation, Flags, Ctx("user", fx.User), CancellationToken.None)));
    }

    // ------------------------------------------------------------ ACL

    [Fact]
    public async Task A_department_admin_closes_and_reopens_with_a_reason() => await WithV2Async(async () =>
    {
        var closed = await Close("2011-03");
        Assert.Equal(StatusCodes.Status200OK, StatusOf(closed));
        Assert.Equal("closed", Json(closed).GetProperty("period").GetProperty("status").GetString());

        Assert.Equal(AccountingPeriodErrors.ReasonRequired, ErrorOf(await Reopen("2011-03", null)));
        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(await Reopen("2011-03", " ")));

        var reopened = await Reopen("2011-03", "Thiếu HĐ điện tháng 3");
        Assert.Equal(StatusCodes.Status200OK, StatusOf(reopened));
        var events = Json(await AccountingPeriodEndpoints.EventsAsync("2011-03", Periods, Flags, Ctx("user", fx.User), CancellationToken.None));
        Assert.Equal(["period_closed", "period_reopened"], events.EnumerateArray().Select(e => e.GetProperty("eventType").GetString()));
    });

    [Fact]
    public async Task A_user_tenant_owner_super_admin_and_the_agent_cannot_close_or_reopen() => await WithV2Async(async () =>
    {
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await Close("2011-04", "user", fx.User)));
        Assert.IsType<ForbidHttpResult>(await Close("2011-04", "tenant_owner", department: Guid.Empty));
        Assert.IsType<ForbidHttpResult>(await Close("2011-04", "super_admin", department: Guid.Empty));
        var agent = await Close("2011-04", agent: true);
        Assert.Equal((StatusCodes.Status403Forbidden, "forbidden_actor"), (StatusOf(agent), ErrorOf(agent)));
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await Reopen("2011-04", "x", "user")));
        Assert.Equal("open", (await GetAsync(fx.Dept, "2011-04")).Status);
    });

    [Fact]
    public async Task Another_department_only_ever_touches_its_own_periods() => await WithV2Async(async () =>
    {
        Assert.Equal(StatusCodes.Status200OK, StatusOf(await Close("2011-05", department: fx.OtherDept)));
        Assert.Equal("closed", (await GetAsync(fx.OtherDept, "2011-05")).Status);
        Assert.Equal("open", (await GetAsync(fx.Dept, "2011-05")).Status);
        // Its history is not readable from here.
        var events = Json(await AccountingPeriodEndpoints.EventsAsync("2011-05", Periods, Flags, Ctx("admin", fx.Admin), CancellationToken.None));
        Assert.Equal(0, events.GetArrayLength());
        // And it cannot reopen ours.
        await Close("2011-05");
        Assert.Equal(StatusCodes.Status200OK, StatusOf(await Reopen("2011-05", "x", department: fx.OtherDept)));
        Assert.Equal("closed", (await GetAsync(fx.Dept, "2011-05")).Status);
    });

    // ------------------------------------------------------------ refusals

    [Fact]
    public async Task Refusals_name_what_refused_them() => await WithV2Async(async () =>
    {
        Assert.Equal("invalid_period", ErrorOf(await Close("2011-13")));
        Assert.Equal(AccountingPeriodErrors.InFuture, ErrorOf(await Close("2012-01")));

        await VoucherAsync(new DateOnly(2011, 6, 10));
        var previous = await Close("2011-07");
        Assert.Equal((StatusCodes.Status409Conflict, AccountingPeriodErrors.PreviousOpen), (StatusOf(previous), ErrorOf(previous)));
        Assert.Equal("2011-06", Json(previous).GetProperty("previous").GetString());

        var pending = await VoucherAsync(new DateOnly(2011, 6, 11), "pending_approval");
        var unposted = await Close("2011-06");
        Assert.Equal(AccountingPeriodErrors.HasUnposted, ErrorOf(unposted));
        Assert.Equal([pending.ToString()], Json(unposted).GetProperty("voucherIds").EnumerateArray().Select(e => e.GetString()));
    });

    [Fact]
    public async Task Reopening_a_month_before_a_closed_one_warns() => await WithV2Async(async () =>
    {
        await Close("2011-08");
        await Close("2011-09");
        var reopened = Json(await Reopen("2011-08", "Điều chỉnh"));
        var warning = reopened.GetProperty("warnings")[0];
        Assert.Equal(("next_period_closed", "2011-09"), (warning.GetProperty("code").GetString(), warning.GetProperty("period").GetString()));
    });

    // ------------------------------------------------------------ period_locked

    [Fact]
    public async Task Writes_in_a_closed_period_answer_409_period_locked_with_the_period() => await WithV2Async(async () =>
    {
        var id = await VoucherAsync(new DateOnly(2011, 10, 31));
        await Close("2011-10");
        Assert.Contains((await Validation.ComputeAsync(fx.Tenant, fx.Dept, id, CancellationToken.None))!.Issues,
            i => i is { Code: "PERIOD_LOCKED", Severity: "error" });

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            VoucherEndpoints.UpdateAsync(id, Patch(total: 9m), Repo, Validation, Flags, Ctx("admin", fx.Admin), CancellationToken.None));
        var mapped = AccountingPeriodEndpoints.PeriodLockedResult(ex);
        Assert.NotNull(mapped);
        Assert.Equal((StatusCodes.Status409Conflict, AccountingPeriodErrors.PeriodLocked), (StatusOf(mapped!), ErrorOf(mapped!)));
        Assert.Equal("2011-10", Json(mapped!).GetProperty("period").GetString());
        Assert.Null(AccountingPeriodEndpoints.PeriodLockedResult(new InvalidOperationException("x")));

        // The global handler answers the same for routes outside /vouchers.
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        Assert.True(await new PeriodLockedExceptionHandler().TryHandleAsync(http, ex, CancellationToken.None));
        Assert.Equal(StatusCodes.Status409Conflict, http.Response.StatusCode);
        http.Response.Body.Position = 0;
        Assert.Contains("\"period\":\"2011-10\"", await new StreamReader(http.Response.Body).ReadToEndAsync());
    });

    /// <summary>apply-invoice / apply-expense book the ledger before the voucher row changes: refused up front.</summary>
    [Fact]
    public async Task Legacy_recognition_is_refused_before_booking_in_a_closed_period()
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync(new DateOnly(2010, 4, 10));
        var voucher = (await Repo.FindInDepartmentAsync(fx.Tenant, fx.Dept, id, CancellationToken.None))!.Voucher;
        await ExecAsync("""
            INSERT INTO accounting_periods(tenant_id, department_id, year, month, status, closed_at) VALUES (@t, @d, 2010, 4, 'closed', NOW())
            """, new { t = fx.Tenant, d = fx.Dept });

        // Without the v2 flag nothing changes.
        Assert.Null(await AccountingPeriodEndpoints.LockedBeforePostingAsync(voucher, fx.Tenant, fx.Dept, Flags, Periods, CancellationToken.None));
        await WithV2Async(async () =>
        {
            var locked = await AccountingPeriodEndpoints.LockedBeforePostingAsync(voucher, fx.Tenant, fx.Dept, Flags, Periods, CancellationToken.None);
            Assert.Equal(AccountingPeriodErrors.PeriodLocked, ErrorOf(locked!));
            Assert.Null(await AccountingPeriodEndpoints.LockedBeforePostingAsync(new VoucherRecord { DocumentDate = voucher.DocumentDate, AccountingDate = new DateOnly(2010, 5, 1) },
                fx.Tenant, fx.Dept, Flags, Periods, CancellationToken.None));
            Assert.Null(await AccountingPeriodEndpoints.LockedBeforePostingAsync(new VoucherRecord { DocumentDate = null },
                fx.Tenant, fx.Dept, Flags, Periods, CancellationToken.None));
        });
    }

    [Fact]
    public async Task A_late_invoice_is_booked_in_the_open_period_through_its_accounting_date() => await WithV2Async(async () =>
    {
        await Close("2011-11");
        var request = new CreateVoucherRequest(VoucherTypes.InvoiceIn, Unique(), "1C11TAA", new DateOnly(2011, 11, 28), "NCC", "0101243150", null,
            "Chúng ta", "0312345673", null, null, 100m, 10m, 10m, null, 110m, "VND", null, null, null, null, "RAW", null, null, null, null);

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            Repo.CreateAsync(fx.Tenant, fx.Dept, fx.User, request, CancellationToken.None));
        Assert.Equal("2011-11", AccountingPeriodErrors.LockedPeriodOf(refused));

        var created = await Repo.CreateAsync(fx.Tenant, fx.Dept, fx.User, request with { AccountingDate = new DateOnly(2011, 12, 1) }, CancellationToken.None);
        Assert.Equal(new DateOnly(2011, 12, 1), created!.Voucher.AccountingDate);

        // Moving it back into the closed month is refused; editing it in December is fine.
        await Assert.ThrowsAsync<PostgresException>(() => VoucherEndpoints.UpdateAsync(created.Voucher.Id, Patch(accountingDate: new DateOnly(2011, 11, 30)),
            Repo, Validation, Flags, Ctx("user", fx.User), CancellationToken.None));
        Assert.Equal(StatusCodes.Status200OK, StatusOf(await VoucherEndpoints.UpdateAsync(created.Voucher.Id,
            Patch(accountingDate: new DateOnly(2011, 12, 2)), Repo, Validation, Flags, Ctx("user", fx.User), CancellationToken.None)));
    });

    // --------------------------------------------------------------- helpers

    private static IReadOnlyList<RouteEndpoint> Routes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(TimeProvider.System);
        foreach (var t in new[] { typeof(IAccountingPeriodRepository), typeof(ITenantFeatureFlags) })
            builder.Services.AddSingleton(t, _ => throw new InvalidOperationException("not used in metadata tests"));
        var app = builder.Build();
        app.MapAccountingPeriodEndpoints();
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
    }

    private async Task WithV2Async(Func<Task> body)
    {
        if (fx.ConnectionString is null) return;
        await ExecAsync("""
            INSERT INTO tenant_feature_flags(tenant_id, flag, enabled) VALUES (@t, 'voucher_flow_v2', true)
            ON CONFLICT (tenant_id, flag) DO UPDATE SET enabled = true
            """, new { t = fx.Tenant });
        try { await body(); }
        finally { await ExecAsync("UPDATE tenant_feature_flags SET enabled = false WHERE tenant_id=@t", new { t = fx.Tenant }); }
    }

    private DefaultHttpContext Ctx(string role, Guid user, Guid? department = null, bool agent = false)
    {
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user.ToString())], "test")) };
        Guid? dept = department == Guid.Empty ? null : department ?? fx.Dept;
        ctx.Items[AuthenticatedPrincipalContext.ItemKey] = new PrincipalContext(user, fx.Tenant, dept, role, true, true, dept is null ? null : true);
        ctx.Items[TenantContext.ItemKey] = fx.Tenant;
        ctx.TraceIdentifier = "req-" + Unique();
        if (agent) ctx.Request.Headers["X-AIQ-Actor"] = "agent";
        return ctx;
    }

    private async Task<AccountingPeriodRecord> GetAsync(Guid department, string period) =>
        (await Periods.ListAsync(fx.Tenant, department, int.Parse(period[..4]), CancellationToken.None)).Single(p => p.Period == period);

    private async Task ExecAsync(string sql, object args)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync(sql, args);
    }

    private async Task<Guid> VoucherAsync(DateOnly date, string lifecycle = "extracted")
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        var id = await c.ExecuteScalarAsync<Guid>("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, status, document_number, document_date, total_amount)
            VALUES (@u, @t, @d, 'invoice_vat_in', 'extracted', @n, @date, 1) RETURNING id
            """, new { u = fx.User, t = fx.Tenant, d = fx.Dept, n = Unique(), date = date.ToDateTime(TimeOnly.MinValue) });
        if (lifecycle != "extracted")
            await c.ExecuteAsync("UPDATE vouchers SET lifecycle_state=@lifecycle WHERE id=@id", new { id, lifecycle });
        return id;
    }

    private static UpdateVoucherRequest Patch(decimal? total = null, DateOnly? accountingDate = null) => new(
        null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, total, null, null, null, null, accountingDate);

    private static int? StatusOf(IResult r) => (r as IStatusCodeHttpResult)?.StatusCode;

    private static string? ErrorOf(IResult r) =>
        (r as IValueHttpResult)?.Value?.GetType().GetProperty("error")?.GetValue((r as IValueHttpResult)!.Value) as string;

    private static JsonElement Json(IResult r) =>
        JsonSerializer.SerializeToElement((r as IValueHttpResult)!.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static string Unique() => Guid.NewGuid().ToString("N")[..10];

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
