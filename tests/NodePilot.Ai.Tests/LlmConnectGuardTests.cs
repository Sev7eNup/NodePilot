using System.Net;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;
using NodePilot.TestCommons;

namespace NodePilot.Ai.Tests;

/// <summary>
/// SSRF connect-time guard for the LLM named HttpClient (security-audit finding L-4): closes the
/// DNS-rebinding window by rejecting any link-local destination IP (169.254/16, which includes
/// cloud metadata services, and IPv6 fe80::/10) at TCP-connect time — loopback/private IPs stay
/// DELIBERATELY allowed (needed for local Ollama/LM Studio). Tested on two levels: (1) the
/// classification matrix of <c>IsLinkLocal</c> (private static, reached via reflection), and
/// (2) the real end-to-end behavior through the <c>ConnectCallback</c> itself.
/// </summary>
public sealed class LlmConnectGuardTests
{
    // ---- IsLinkLocal classification matrix (private static -> Reflection) --------------

    private static bool IsLinkLocal(string ip)
    {
        var method = typeof(LlmConnectGuard).GetMethod(
            "IsLinkLocal", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull("the guard classifies each resolved IP via IsLinkLocal");
        return (bool)method!.Invoke(null, new object[] { IPAddress.Parse(ip) })!;
    }

    [Theory]
    [InlineData("169.254.169.254")]        // AWS/Azure/GCP IMDS
    [InlineData("169.254.0.1")]            // 169.254/16 lower bound
    [InlineData("169.254.255.255")]        // 169.254/16 upper bound
    [InlineData("fe80::1")]                // IPv6 link-local
    [InlineData("fe80::abcd:1234")]        // IPv6 link-local
    [InlineData("::ffff:169.254.169.254")] // IPv4-mapped IPv6 pointing at IMDS
    public void IsLinkLocal_LinkLocalAddresses_ReturnTrue(string ip)
    {
        IsLinkLocal(ip).Should().BeTrue();
    }

    [Theory]
    [InlineData("169.253.0.1")]            // boundary: second octet != 254 -> NOT link-local
    [InlineData("168.254.0.1")]            // boundary: first octet != 169 -> NOT link-local
    [InlineData("127.0.0.1")]              // loopback — deliberately allowed for local LLMs
    [InlineData("10.0.0.5")]               // private — allowed
    [InlineData("192.168.1.10")]           // private — allowed
    [InlineData("8.8.8.8")]                // public
    [InlineData("::1")]                    // IPv6 loopback
    [InlineData("2001:4860:4860::8888")]   // public IPv6
    [InlineData("::ffff:10.0.0.5")]        // IPv4-mapped private
    public void IsLinkLocal_NonLinkLocalAddresses_ReturnFalse(string ip)
    {
        IsLinkLocal(ip).Should().BeFalse();
    }

    // ---- End-to-end via the real ConnectCallback -------------------------------------

    /// <summary>
    /// Mirrors the production handler from <see
    /// cref="LlmServiceCollectionExtensions.AddNodePilotAi"/>
    /// so this suite exercises the guard in the shape it actually ships in. Production carries
    /// <c>UseProxy = true</c> with a configured <see cref="LlmConfiguredProxy"/>; in
    /// <see cref="LlmProxyMode.Off"/> — the default asserted here — that proxy bypasses every
    /// destination, so the connect goes direct and the callback sees the real host. Building it
    /// that way rather than hard-coding <c>UseProxy = false</c> keeps the copy honest.
    /// </summary>
    private static HttpClient NewGuardedClient() =>
        new HttpClient(new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = new LlmConfiguredProxy(new StaticOptionsMonitor<LlmOptions>(LlmTestOptions.WithProfile())),
            AllowAutoRedirect = false,
            ConnectCallback = LlmConnectGuard.ConnectAsync,
        });

    [Fact]
    public async Task ConnectAsync_LoopbackEndpoint_IsAllowed_AndReachesServer()
    {
        // Loopback is explicitly permitted — a real request through the guard must succeed.
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/ping").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("pong"));

        using var client = NewGuardedClient();
        var resp = await client.GetAsync($"{server.Url!.TrimEnd('/')}/ping");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Be("pong");
    }

    [Theory]
    [InlineData("http://169.254.169.254:8080/latest/meta-data/")] // AWS IMDS via literal IP
    [InlineData("http://[fe80::1]:8080/")]                        // IPv6 link-local via literal IP
    public async Task ConnectAsync_LinkLocalEndpoint_IsRejected(string url)
    {
        using var client = NewGuardedClient();

        Func<Task> act = () => client.GetAsync(url);

        // The guard throws IOException inside ConnectCallback; HttpClient surfaces it as
        // HttpRequestException with the guard's reason preserved in the exception chain.
        (await act.Should().ThrowAsync<Exception>())
            .Which.ToString().Should().Contain("SSRF guard rejected");
    }

    // ---- Stage naming: which half of "cannot reach the endpoint" actually failed ------

    [Fact]
    public async Task ConnectAsync_UnresolvableHost_FailsAtTheDnsStage()
    {
        // .invalid is reserved by RFC 2606 precisely so it can never resolve.
        using var client = NewGuardedClient();

        Func<Task> act = () => client.GetAsync("http://nodepilot-endpoint.invalid/v1/models");

        (await act.Should().ThrowAsync<Exception>())
            .Which.ToString().Should().Contain("LLM endpoint DNS:",
                "name resolution and a dropped connection need different fixes and must not read alike");
    }

    [Fact]
    public async Task ConnectAsync_ClosedPort_FailsAtTheTcpStage_AndSaysSomethingAnswered()
    {
        // Port 1 on loopback: refused, not dropped — the distinction an operator needs, because a
        // refusal means the host is reachable and the listener is the problem.
        using var client = NewGuardedClient();

        Func<Task> act = () => client.GetAsync("http://127.0.0.1:1/v1/models");

        var message = (await act.Should().ThrowAsync<Exception>()).Which.ToString();
        message.Should().Contain("LLM endpoint TCP:");
        message.Should().Contain("refused");
        message.Should().Contain("127.0.0.1", "the addresses actually tried are the diagnostic");
    }

    [Fact]
    public async Task ConnectAsync_ReachableEndpoint_LogsTheResolvedAddresses()
    {
        // The single most useful line when an endpoint "works from my machine" but not from the
        // service: a stale AAAA record or a different DNS suffix both look identical from outside.
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/ping").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("pong"));

        var logger = new CapturingLogger();
        using var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = LlmConnectGuard.HandshakeTimeout,
            ConnectCallback = (ctx, ct) => LlmConnectGuard.ConnectAsync(ctx, logger, ct),
        });

        await client.GetAsync($"{server.Url!.TrimEnd('/')}/ping", TestContext.Current.CancellationToken);

        logger.Messages.Should().Contain(m => m.Contains("resolved to"));
        logger.Messages.Should().Contain(m => m.Contains("TCP to"));
    }

}
