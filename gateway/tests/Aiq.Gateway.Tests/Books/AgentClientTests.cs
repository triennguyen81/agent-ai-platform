using System.Net;
using System.Net.Http.Json;
using Aiq.Gateway.Books;

namespace Aiq.Gateway.Tests.Books;

/// <summary>
/// D6 bug-fix regression coverage: RunAgentTaskAsync previously sent the
/// caller's token as a `user_token` JSON body field (which
/// agent/src/aiq_agent/api/schemas.py's AgentRunRequest has never declared,
/// and its extra="forbid" config rejects) and set no Authorization header
/// at all — so every call has always failed the Agent service's
/// authentication before this session's fix. These tests pin the corrected
/// wire contract: Bearer header carries the token, the JSON body carries
/// only task/capability/max_turns.
/// </summary>
public sealed class AgentClientTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { final_answer = "ok" }),
            };
        }
    }

    private static (IAgentClient Client, CapturingHandler Handler) BuildClient()
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://agent.invalid") };
        var client = new AgentClient(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentClient>.Instance);
        return (client, handler);
    }

    [Fact]
    public async Task RunAgentTaskAsync_SendsBearerAuthorizationHeader_NotABodyField()
    {
        var (client, handler) = BuildClient();
        await client.RunAgentTaskAsync("do the thing", "the-users-jwt", "voucher.processing", 3, default);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("the-users-jwt", handler.LastRequest.Headers.Authorization?.Parameter);
        Assert.DoesNotContain("user_token", handler.LastBody);
    }

    [Fact]
    public async Task RunAgentTaskAsync_BodyCarriesCapabilityNotSkills()
    {
        var (client, handler) = BuildClient();
        await client.RunAgentTaskAsync("do the thing", "token", "voucher.processing", 3, default);

        Assert.Contains("\"capability\":\"voucher.processing\"", handler.LastBody);
        Assert.DoesNotContain("\"skills\"", handler.LastBody);
    }

    [Fact]
    public async Task RunAgentTaskAsync_PostsToAgentRunEndpoint()
    {
        var (client, handler) = BuildClient();
        await client.RunAgentTaskAsync("do the thing", "token", "voucher.processing", 3, default);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/agent/run", handler.LastRequest.RequestUri!.AbsolutePath);
    }
}
