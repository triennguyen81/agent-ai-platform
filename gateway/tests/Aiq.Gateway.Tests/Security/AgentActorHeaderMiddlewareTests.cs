using System.Net;
using Aiq.Gateway.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aiq.Gateway.Tests.Security;

/// <summary>
/// QĐ 23 (docs/flows/chung-tu-proposed.md §6.3): X-AIQ-Actor is a secondary,
/// audit-grade signal. The Gateway removes it from every request whose socket
/// peer is not inside Security:AgentInternalNetworks; X-Forwarded-For is never
/// trusted; an empty list strips it always (fail closed).
/// </summary>
public sealed class AgentActorHeaderMiddlewareTests
{
    private static async Task<string?> SeenByHandler(string[] networks, string peer, IDictionary<string, string>? headers = null)
    {
        string? seen = "<not run>";
        var middleware = new AgentActorHeaderMiddleware(
            ctx => { seen = ctx.Request.Headers.TryGetValue(AgentActorHeaderMiddleware.Header, out var v) ? v.ToString() : null; return Task.CompletedTask; },
            Options.Create(new AgentActorHeaderOptions { AgentInternalNetworks = networks }),
            NullLogger<AgentActorHeaderMiddleware>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        ctx.Request.Headers[AgentActorHeaderMiddleware.Header] = "agent";
        foreach (var (k, v) in headers ?? new Dictionary<string, string>()) ctx.Request.Headers[k] = v;
        await middleware.InvokeAsync(ctx);
        return seen;
    }

    [Fact]
    public async Task An_external_peer_loses_the_header()
    {
        Assert.Null(await SeenByHandler(["172.30.0.0/29"], "203.0.113.7"));
    }

    [Fact]
    public async Task The_agent_network_keeps_the_header()
    {
        Assert.Equal("agent", await SeenByHandler(["172.30.0.0/29"], "172.30.0.3"));
        // Kestrel reports IPv4 peers as IPv4-mapped IPv6 on a dual-stack socket.
        Assert.Equal("agent", await SeenByHandler(["172.30.0.0/29"], "::ffff:172.30.0.3"));
    }

    [Fact]
    public async Task A_spoofed_forwarded_for_never_counts()
    {
        Assert.Null(await SeenByHandler(["172.30.0.0/29"], "203.0.113.7",
            new Dictionary<string, string> { ["X-Forwarded-For"] = "172.30.0.3", ["X-Real-IP"] = "172.30.0.3" }));
    }

    [Fact]
    public async Task An_empty_list_always_strips()
    {
        Assert.Null(await SeenByHandler([], "172.30.0.3"));
        Assert.Null(await SeenByHandler([], "127.0.0.1"));
    }

    [Fact]
    public async Task A_neighbour_outside_the_cidr_loses_the_header()
    {
        // The reverse proxy sits on the same docker network but outside the agent's range.
        Assert.Null(await SeenByHandler(["172.30.0.0/29"], "172.30.0.9"));
    }

    [Fact]
    public async Task Requests_without_the_header_pass_through()
    {
        string? seen = "<not run>";
        var middleware = new AgentActorHeaderMiddleware(
            ctx => { seen = ctx.Request.Headers[AgentActorHeaderMiddleware.Header].ToString(); return Task.CompletedTask; },
            Options.Create(new AgentActorHeaderOptions()), NullLogger<AgentActorHeaderMiddleware>.Instance);
        await middleware.InvokeAsync(new DefaultHttpContext());
        Assert.Equal("", seen);
    }

    [Fact]
    public void A_malformed_cidr_fails_at_startup()
    {
        Assert.Throws<InvalidOperationException>(() => new AgentActorHeaderMiddleware(
            _ => Task.CompletedTask,
            Options.Create(new AgentActorHeaderOptions { AgentInternalNetworks = ["172.30.0.0/99"] }),
            NullLogger<AgentActorHeaderMiddleware>.Instance));
    }
}
