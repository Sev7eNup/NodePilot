using System.Net;
using System.Net.Http;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Engine.Security;
using Xunit;

namespace NodePilot.Engine.Tests.Security;

/// <summary>
/// Pins the DNS-rebinding closure (tracked as audit finding #5). The SocketsHttpHandler that the named
/// "NodePilot" client wires up runs every TCP-connect attempt through the same SSRF policy
/// that NetworkGuard.ValidateUrl applied to the URL — so even if the attacker's DNS rebinds
/// the host between url-check and socket-open, the connect still has to clear the policy.
///
/// We test the callback in isolation (no real socket) by reflection-constructing the
/// SocketsHttpConnectionContext and inspecting which IPs would have been considered. The
/// runtime path resolves DNS for real, which is brittle in CI; the unit-level proof is that
/// the callback delegates to NetworkGuard.EnforceConnect and surfaces IOException when the
/// policy rejects every candidate.
/// </summary>
public class RestApiConnectCallbackTests
{
    [Fact]
    public void BuildDefaultHandler_AttachesConnectCallback()
    {
        // Sanity: the handler returned for the named "NodePilot" client carries a non-null
        // ConnectCallback. Without this the SSRF policy only fires at validate-time and
        // DNS rebinding wins.
        var cfg = new ConfigurationBuilder().Build();
        var opts = new NodePilot.Engine.Options.RestApiProxyOptions { Enabled = false };

        using var handler = RestApiHttpClientProvider.BuildDefaultHandler(opts, cfg);

        handler.ConnectCallback.Should().NotBeNull(
            "the H-3-follow-up DNS-rebinding closure depends on every outbound connect being " +
            "filtered through NetworkGuard.EnforceConnect at TCP-establish time, not just at " +
            "URL-validate time");
    }

    [Fact]
    public void OverrideHandler_DirectMode_AlsoCarriesConnectCallback()
    {
        // proxyMode=direct still uses our SocketsHttpHandler, so the SSRF policy must also
        // apply there. Otherwise a workflow could opt out of the global proxy AND the
        // SSRF guard with one config flip.
        var cfg = new ConfigurationBuilder().Build();
        var factory = new TestHttpClientFactory();
        var provider = new RestApiHttpClientProvider(factory, cfg);

        using var doc = System.Text.Json.JsonDocument.Parse("""{"url":"https://x","proxyMode":"direct"}""");
        var handler = provider.ResolveOverrideHandler(doc.RootElement);

        handler.ConnectCallback.Should().NotBeNull();
    }

    [Fact]
    public async Task ConnectCallback_BlocksLinkLocalHost_RegardlessOfFlag()
    {
        // The ConnectCallback receives a SocketsHttpConnectionContext. We can build one via
        // reflection (the type is internal-ctor in some runtimes, but the test doesn't need
        // to actually open a socket — EnforceConnect throws before reaching the network).
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RestApi:BlockPrivateNetworks"] = "false", // even with private allowed
            }).Build();

        var ctx = BuildContext("169.254.169.254", 80);

        var act = async () => await RestApiHttpClientProvider.ConnectWithSsrfGuardAsync(ctx, cfg, default);

        await act.Should().ThrowAsync<IOException>().WithMessage("*link-local*");
    }

    [Fact]
    public async Task ConnectCallback_BlocksRfc1918_OnDefaultPolicy()
    {
        var cfg = new ConfigurationBuilder().Build(); // missing key → reject (secure-by-default policy)
        var ctx = BuildContext("10.0.0.5", 443);

        var act = async () => await RestApiHttpClientProvider.ConnectWithSsrfGuardAsync(ctx, cfg, default);

        await act.Should().ThrowAsync<IOException>().WithMessage("*loopback/private*");
    }

    [Fact]
    public async Task ConnectCallback_AllowsRfc1918_WhenExplicitlyDisabled()
    {
        // Dev override path: BlockPrivateNetworks=false explicitly. The connect should NOT
        // be rejected by policy. We expect a network-level IOException ("connection refused"
        // or similar) instead — proves the policy let the address through to the actual
        // socket.connect call.
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RestApi:BlockPrivateNetworks"] = "false",
            }).Build();

        // 240.0.0.1 is reserved-future and not normally routable. Some hosts treat the
        // class-E range as a safe "definitely won't reach anyone" sink for tests like
        // this — the call will fail at OS level with "no route" / "connection refused".
        // What we're proving: it isn't pre-empted by the SSRF policy first.
        // Use a port that won't match anything legitimate.
        var ctx = BuildContext("240.0.0.1", 1);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var act = async () => await RestApiHttpClientProvider.ConnectWithSsrfGuardAsync(ctx, cfg, cts.Token);

        // Either the connect failed at OS level (any IOException EXCEPT one that mentions
        // the SSRF guard's vocabulary) or got cancelled by the timeout. Both prove the
        // SSRF policy didn't fire — that's all we want to assert here.
        try
        {
            await act();
        }
        catch (OperationCanceledException) { /* timeout fired before connect — acceptable */ }
        catch (IOException ex) when (!ex.Message.Contains("link-local") && !ex.Message.Contains("loopback/private"))
        {
            // OS-level connect failure — not an SSRF policy rejection. Pass.
        }
        catch (System.Net.Sockets.SocketException)
        {
            // Native connect refusal. Same shape as IOException effectively. Pass.
        }
    }

    /// <summary>
    /// Builds a <see cref="SocketsHttpConnectionContext"/> for the test. The type's
    /// constructor is internal; we use reflection to produce one. The
    /// <see cref="HttpRequestMessage"/> only needs to be non-null — the EnforceConnect
    /// path doesn't read its headers.
    /// </summary>
    private static SocketsHttpConnectionContext BuildContext(string host, int port)
    {
        var ctxType = typeof(SocketsHttpConnectionContext);
        var ctor = ctxType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).First();
        // .NET 8/9/10 SocketsHttpConnectionContext signature: (DnsEndPoint, HttpRequestMessage)
        var endpoint = new DnsEndPoint(host, port);
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://{host}:{port}/");
        return (SocketsHttpConnectionContext)ctor.Invoke(new object[] { endpoint, request });
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
