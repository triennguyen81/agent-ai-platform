using Hangfire.Dashboard;

namespace Aiq.Gateway.Jobs;

/// <summary>
/// Restricts the Hangfire dashboard to authenticated users carrying the "admin" role.
/// JWT bearer tokens cannot be sent by the browser when navigating to the dashboard,
/// so this filter expects a session cookie or another upstream auth mechanism. For
/// dev we additionally allow loopback hosts.
/// </summary>
public sealed class JobsAuthorizationFilter : IDashboardAuthorizationFilter
{
    private readonly bool _allowLoopback;

    public JobsAuthorizationFilter(bool allowLoopback)
    {
        _allowLoopback = allowLoopback;
    }

    public bool Authorize(DashboardContext context)
    {
        var http = context.GetHttpContext();

        if (_allowLoopback && IsLoopback(http))
        {
            return true;
        }

        return http.User.IsInRole("admin");
    }

    private static bool IsLoopback(HttpContext http)
    {
        var remote = http.Connection.RemoteIpAddress;
        return remote is not null && System.Net.IPAddress.IsLoopback(remote);
    }
}
