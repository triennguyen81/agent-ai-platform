using System.Net;
using Microsoft.Extensions.Options;

namespace Aiq.Gateway.Security;

public sealed class AgentActorHeaderOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// CIDRs of the agent's own network (not the whole docker network: the reverse
    /// proxy lives there too). Empty = the header is stripped from every request.
    /// </summary>
    public string[] AgentInternalNetworks { get; set; } = [];
}

/// <summary>
/// QĐ 23: <c>X-AIQ-Actor</c> is an audit label and a secondary refusal signal
/// for the lifecycle endpoints, never an authority. Runs first in the pipeline
/// and removes the header unless the socket peer
/// (<see cref="ConnectionInfo.RemoteIpAddress"/>, never X-Forwarded-For) is in
/// <see cref="AgentActorHeaderOptions.AgentInternalNetworks"/>.
/// </summary>
public sealed class AgentActorHeaderMiddleware
{
    public const string Header = "X-AIQ-Actor";

    private readonly RequestDelegate _next;
    private readonly IReadOnlyList<IPNetwork> _networks;
    private readonly ILogger<AgentActorHeaderMiddleware> _logger;

    public AgentActorHeaderMiddleware(RequestDelegate next, IOptions<AgentActorHeaderOptions> options, ILogger<AgentActorHeaderMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        // A typo here must not silently trust or distrust the agent: fail at startup.
        _networks = options.Value.AgentInternalNetworks
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => IPNetwork.TryParse(c.Trim(), out var n)
                ? n
                : throw new InvalidOperationException($"Security:AgentInternalNetworks: '{c}' is not a valid CIDR."))
            .ToList();
    }

    public Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.Request.Headers.ContainsKey(Header) && !FromAgentNetwork(ctx.Connection.RemoteIpAddress))
        {
            ctx.Request.Headers.Remove(Header);
            _logger.LogDebug("Removed {Header} from a request outside the agent network ({Peer})", Header, ctx.Connection.RemoteIpAddress);
        }
        return _next(ctx);
    }

    private bool FromAgentNetwork(IPAddress? peer)
    {
        if (peer is null || _networks.Count == 0) return false;
        if (peer.IsIPv4MappedToIPv6) peer = peer.MapToIPv4();
        return _networks.Any(n => n.Contains(peer));
    }
}
