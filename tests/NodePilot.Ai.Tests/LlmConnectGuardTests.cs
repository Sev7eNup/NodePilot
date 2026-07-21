using System.Net;
using System.Reflection;
using FluentAssertions;
using NodePilot.Ai;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

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
    // ---- IsLinkLocal classification matrix (private static → Reflection) --------------

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
    [InlineData("169.253.0.1")]            // boundary: second octet != 254 → NOT link-local
    [InlineData("168.254.0.1")]            // boundary: first octet != 169 → NOT link-local
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

    private static HttpClient NewGuardedClient() =>
        new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
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
}
