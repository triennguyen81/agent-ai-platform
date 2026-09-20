using Aiq.Gateway.Books;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aiq.Gateway.Tests.Books;

/// <summary>
/// AgentCallResult.RunAsync is the fix for the real production bug behind
/// both the Contract Upload and Accounting Invoice extract-text HTTP 500s:
/// ContractEndpoints.ExtractText / AccountingEndpoints.ExtractInvoiceText
/// used to catch only AgentRequestException (the agent responding with an
/// error status) — a genuine transport failure (agent unreachable) threw
/// HttpRequestException straight through as an unhandled exception, which
/// ASP.NET turned into a raw, undiagnosable 500. These tests exercise the
/// exception-to-IResult mapping directly, without any HTTP/DB harness —
/// same "test the pure logic" convention as this project's other
/// Security/Governance policy tests.
/// </summary>
public sealed class AgentCallResultTests
{
    [Fact]
    public async Task RunAsync_Success_ReturnsMappedResult()
    {
        var result = await AgentCallResult.RunAsync(
            () => Task.FromResult(42),
            value => Results.Ok(new { value }),
            CancellationToken.None);

        var ok = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status200OK, ok.StatusCode);
    }

    [Fact]
    public async Task RunAsync_AgentUnreachable_ReturnsStructured503_NeverThrows()
    {
        // This is the exact production failure: HttpClient throws
        // HttpRequestException when the agent service is down/unreachable
        // (confirmed in gateway logs: "Resource temporarily unavailable
        // (agent:8000)"). Must become a clean, structured 503 — never
        // propagate as an unhandled exception.
        var result = await AgentCallResult.RunAsync<int>(
            () => throw new HttpRequestException("Connection refused"),
            value => Results.Ok(value),
            CancellationToken.None);

        var json = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, json.StatusCode);
    }

    [Fact]
    public async Task RunAsync_AgentRespondsWithClientError_MapsUpstreamStatusThrough()
    {
        var result = await AgentCallResult.RunAsync<int>(
            () => throw new AgentRequestException("Agent returned HTTP 422: bad file", 422),
            value => Results.Ok(value),
            CancellationToken.None);

        var json = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(422, json.StatusCode);
    }

    [Fact]
    public async Task RunAsync_AgentRespondsWithServerError_MapsTo502()
    {
        var result = await AgentCallResult.RunAsync<int>(
            () => throw new AgentRequestException("Agent returned HTTP 500: internal", 500),
            value => Results.Ok(value),
            CancellationToken.None);

        var json = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, json.StatusCode);
    }

    [Fact]
    public async Task RunAsync_AgentTimesOut_ReturnsStructured504_NeverThrows()
    {
        // The HttpClient's own request timeout fires as OperationCanceledException
        // even though the caller's own CancellationToken was never cancelled —
        // must be distinguished from a genuine caller-cancelled request.
        var callerToken = CancellationToken.None;
        var result = await AgentCallResult.RunAsync<int>(
            () => throw new OperationCanceledException("Timed out"),
            value => Results.Ok(value),
            callerToken);

        var json = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status504GatewayTimeout, json.StatusCode);
    }

    [Fact]
    public async Task RunAsync_CallerCancelledRequest_PropagatesCancellation_NotMappedTo504()
    {
        // Distinct from a timeout: the CALLER's own token was cancelled
        // (e.g. the browser navigated away) — this must propagate normally
        // (ASP.NET's own client-disconnect handling), never be reported as
        // an agent-side timeout.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => AgentCallResult.RunAsync<int>(
            () => throw new OperationCanceledException("Cancelled"),
            value => Results.Ok(value),
            cts.Token));
    }
}
