using System.Net;
using System.Text.Json;
using FluentAssertions;
using NodePilot.Cli.Api;
using NodePilot.Cli.Auth;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace NodePilot.Cli.Tests;

public sealed class TokenRefreshHandlerTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly string _dir;
    private readonly TokenStore _tokens;

    public TokenRefreshHandlerTests()
    {
        _server = WireMockServer.Start();
        _dir = Path.Combine(Path.GetTempPath(), "np-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _tokens = new TokenStore(_dir);
        _tokens.Save("default", new StoredSession
        {
            Server = _server.Url!,
            Token = "stale-token",
            Username = "admin",
            Role = "Admin",
            UserId = Guid.NewGuid(),
            ExpiresAt = DateTime.UtcNow.AddHours(12),
        });
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Refreshes_OnUnauthorized_AndReplaysOriginalRequest()
    {
        var rotated = new
        {
            token = "fresh-token",
            userId = Guid.NewGuid(),
            username = "admin",
            role = "Admin",
        };

        _server.Given(Request.Create().WithPath("/api/auth/refresh").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(rotated));

        // First call to /api/auth/me returns 401 with stale token, second call returns 200 with fresh token.
        _server.Given(Request.Create().WithPath("/api/auth/me").UsingGet().WithHeader("Authorization", "Bearer stale-token"))
               .RespondWith(Response.Create().WithStatusCode(401));
        _server.Given(Request.Create().WithPath("/api/auth/me").UsingGet().WithHeader("Authorization", "Bearer fresh-token"))
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   id = Guid.NewGuid(), username = "admin", role = "Admin",
               }));

        var handler = new TokenRefreshHandler(_tokens, "default") { InnerHandler = new HttpClientHandler() };
        var http = new HttpClient(handler) { BaseAddress = new Uri(_server.Url + "/") };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "stale-token");
        var client = new NodePilotApiClient(http) { BearerToken = "stale-token" };

        var me = await client.MeAsync(CancellationToken.None);
        me.Username.Should().Be("admin");

        // Persisted token rotated.
        _tokens.Load("default")!.Token.Should().Be("fresh-token");
    }

    [Fact]
    public async Task SecondUnauthorized_SurfacesAsApiException()
    {
        _server.Given(Request.Create().WithPath("/api/auth/refresh").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(401));
        _server.Given(Request.Create().WithPath("/api/auth/me").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(401));

        var handler = new TokenRefreshHandler(_tokens, "default") { InnerHandler = new HttpClientHandler() };
        var http = new HttpClient(handler) { BaseAddress = new Uri(_server.Url + "/") };
        var client = new NodePilotApiClient(http) { BearerToken = "stale-token" };

        Func<Task> act = () => client.MeAsync(CancellationToken.None);
        var ex = await act.Should().ThrowAsync<ApiException>();
        ex.Which.IsUnauthorized.Should().BeTrue();
    }

    [Fact]
    public async Task StoreChangedAfterClientCreation_ToDifferentOrigin_DoesNotRefreshWithStoredToken()
    {
        _server.Given(Request.Create().WithPath("/api/auth/me").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(401));

        var handler = new TokenRefreshHandler(_tokens, "default") { InnerHandler = new HttpClientHandler() };
        var http = new HttpClient(handler) { BaseAddress = new Uri(_server.Url + "/") };
        var client = new NodePilotApiClient(http) { BearerToken = "stale-token" };

        _tokens.Save("default", new StoredSession
        {
            Server = "https://attacker.example",
            Token = "foreign-origin-token",
            Username = "admin",
            Role = "Admin",
            UserId = Guid.NewGuid(),
            ExpiresAt = DateTime.UtcNow.AddHours(12),
        });

        var act = () => client.MeAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ApiException>();
        ex.Which.IsUnauthorized.Should().BeTrue();
        _server.LogEntries.Should().NotContain(entry =>
            entry.RequestMessage.AbsolutePath == "/api/auth/refresh");
    }
}
