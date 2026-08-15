using FluentAssertions;
using NodePilot.Cli.Api;
using NodePilot.Cli.Auth;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;
using NodePilot.Core.Clients;

namespace NodePilot.Cli.Tests.Api;

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
            expiresAt = DateTimeOffset.UtcNow.AddHours(8),
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
    public async Task RefreshesProactivelyBeforeExpiry_AndPersistsServerExpiry()
    {
        var serverExpiry = DateTimeOffset.UtcNow.AddHours(8);
        _tokens.Save("default", new StoredSession
        {
            Server = _server.Url!,
            Token = "expiring-token",
            Username = "admin",
            Role = "Admin",
            UserId = Guid.NewGuid(),
            ExpiresAt = DateTime.UtcNow.AddMinutes(1),
        });

        _server.Given(Request.Create().WithPath("/api/auth/refresh").UsingPost()
                .WithHeader("Authorization", "Bearer expiring-token"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                token = "proactively-rotated-token",
                userId = Guid.NewGuid(),
                username = "admin",
                role = "Admin",
                expiresAt = serverExpiry,
            }));
        _server.Given(Request.Create().WithPath("/api/auth/me").UsingGet()
                .WithHeader("Authorization", "Bearer proactively-rotated-token"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id = Guid.NewGuid(), username = "admin", role = "Admin",
            }));
        // A reactive-only implementation sends the expiring credential first and fails here.
        _server.Given(Request.Create().WithPath("/api/auth/me").UsingGet()
                .WithHeader("Authorization", "Bearer expiring-token"))
            .RespondWith(Response.Create().WithStatusCode(500));

        var handler = new TokenRefreshHandler(_tokens, "default") { InnerHandler = new HttpClientHandler() };
        var http = new HttpClient(handler) { BaseAddress = new Uri(_server.Url + "/") };
        var client = new NodePilotApiClient(http) { BearerToken = "expiring-token" };

        var me = await client.MeAsync(CancellationToken.None);

        me.Username.Should().Be("admin");
        var stored = _tokens.Load("default")!;
        stored.Token.Should().Be("proactively-rotated-token");
        stored.ExpiresAt.Should().BeCloseTo(serverExpiry.UtcDateTime, TimeSpan.FromSeconds(1));
        _server.LogEntries.Count(entry =>
            entry.RequestMessage!.AbsolutePath == "/api/auth/refresh").Should().Be(1);
    }

    [Fact]
    public async Task OlderRefreshResponseWithoutExpiresAt_UsesJwtExpiry_AndDeduplicatesNewProcesses()
    {
        var now = DateTimeOffset.UtcNow;
        var absoluteExpiry = now.AddMinutes(4);
        var clock = new ManualTimeProvider(now);
        const string initialToken = "rolling-upgrade-expiring-token";
        var firstRotatedToken = Jwt(now, absoluteExpiry);
        var secondIssuedAt = now
            + ClientSessionSecurity.SuccessfulRefreshDeduplicationWindow
            + TimeSpan.FromSeconds(1);
        var secondRotatedToken = Jwt(secondIssuedAt, absoluteExpiry);
        _tokens.Save("default", new StoredSession
        {
            Server = _server.Url!,
            Token = initialToken,
            Username = "admin",
            Role = "Admin",
            UserId = Guid.NewGuid(),
            ExpiresAt = absoluteExpiry,
        });

        _server.Given(Request.Create().WithPath("/api/auth/refresh").UsingPost()
                .WithHeader("Authorization", $"Bearer {initialToken}"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                token = firstRotatedToken,
                userId = Guid.NewGuid(),
                username = "admin",
                role = "Admin",
                // Older API: no expiresAt. The JWT exp is authoritative for local scheduling.
            }));
        _server.Given(Request.Create().WithPath("/api/auth/refresh").UsingPost()
                .WithHeader("Authorization", $"Bearer {firstRotatedToken}"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                token = secondRotatedToken,
                userId = Guid.NewGuid(),
                username = "admin",
                role = "Admin",
            }));
        foreach (var token in new[] { firstRotatedToken, secondRotatedToken })
        {
            _server.Given(Request.Create().WithPath("/api/auth/me").UsingGet()
                    .WithHeader("Authorization", $"Bearer {token}"))
                .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
                {
                    id = Guid.NewGuid(), username = "admin", role = "Admin",
                }));
        }

        async Task CallFromNewProcessAsync()
        {
            var processStore = new TokenStore(_dir);
            var current = processStore.Load("default")!;
            var handler = new TokenRefreshHandler(
                processStore, "default", timeProvider: clock)
            {
                InnerHandler = new HttpClientHandler(),
            };
            using var http = new HttpClient(handler) { BaseAddress = new Uri(_server.Url + "/") };
            var client = new NodePilotApiClient(http) { BearerToken = current.Token };
            (await client.MeAsync(CancellationToken.None)).Username.Should().Be("admin");
        }

        await CallFromNewProcessAsync();
        for (var i = 0; i < 5; i++)
            await CallFromNewProcessAsync();

        _server.LogEntries.Count(entry =>
            entry.RequestMessage!.AbsolutePath == "/api/auth/refresh").Should().Be(1,
            "fresh JWT iat must deduplicate sequential short-lived CLI processes");
        _tokens.Load("default")!.ExpiresAt.Should()
            .BeCloseTo(absoluteExpiry, TimeSpan.FromSeconds(1));

        clock.Advance(
            ClientSessionSecurity.SuccessfulRefreshDeduplicationWindow
            + TimeSpan.FromSeconds(1));
        await CallFromNewProcessAsync();

        _server.LogEntries.Count(entry =>
            entry.RequestMessage!.AbsolutePath == "/api/auth/refresh").Should().Be(2,
            "the cross-process dedupe is a cooldown, not a permanent refresh disable");
        _tokens.Load("default")!.Token.Should().Be(secondRotatedToken);
    }

    [Fact]
    public async Task ConcurrentExpiringRequests_ShareOneRefresh()
    {
        _tokens.Save("default", new StoredSession
        {
            Server = _server.Url!,
            Token = "shared-expiring-token",
            Username = "admin",
            Role = "Admin",
            UserId = Guid.NewGuid(),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
        });
        _server.Given(Request.Create().WithPath("/api/auth/refresh").UsingPost()
                .WithHeader("Authorization", "Bearer shared-expiring-token"))
            .RespondWith(Response.Create()
                .WithDelay(TimeSpan.FromMilliseconds(150))
                .WithStatusCode(200)
                .WithBodyAsJson(new
                {
                    token = "shared-fresh-token",
                    userId = Guid.NewGuid(),
                    username = "admin",
                    role = "Admin",
                    expiresAt = DateTimeOffset.UtcNow.AddHours(8),
                }));
        _server.Given(Request.Create().WithPath("/api/auth/me").UsingGet()
                .WithHeader("Authorization", "Bearer shared-fresh-token"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id = Guid.NewGuid(), username = "admin", role = "Admin",
            }));

        var handler = new TokenRefreshHandler(_tokens, "default") { InnerHandler = new HttpClientHandler() };
        var http = new HttpClient(handler) { BaseAddress = new Uri(_server.Url + "/") };
        var client = new NodePilotApiClient(http) { BearerToken = "shared-expiring-token" };

        var callers = await Task.WhenAll(
            client.MeAsync(CancellationToken.None),
            client.MeAsync(CancellationToken.None));

        callers.Should().OnlyContain(me => me.Username == "admin");
        _server.LogEntries.Count(entry =>
            entry.RequestMessage!.AbsolutePath == "/api/auth/refresh").Should().Be(1);
        _server.LogEntries.Count(entry =>
            entry.RequestMessage!.AbsolutePath == "/api/auth/me"
            && entry.RequestMessage.Headers!["Authorization"].First() == "Bearer shared-fresh-token")
            .Should().Be(2);
    }

    [Fact]
    public async Task ConcurrentExpiringRequests_TransientRefreshFailure_UseBoundedCooldown()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new ManualTimeProvider(now);
        _tokens.Save("default", new StoredSession
        {
            Server = _server.Url!,
            Token = "transient-failure-token",
            Username = "admin",
            Role = "Admin",
            UserId = Guid.NewGuid(),
            ExpiresAt = now.AddMinutes(1),
        });
        _server.Given(Request.Create().WithPath("/api/auth/refresh").UsingPost()
                .WithHeader("Authorization", "Bearer transient-failure-token"))
            .RespondWith(Response.Create()
                .WithStatusCode(503));
        _server.Given(Request.Create().WithPath("/api/auth/me").UsingGet()
                .WithHeader("Authorization", "Bearer transient-failure-token"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id = Guid.NewGuid(), username = "admin", role = "Admin",
            }));

        var handler = new TokenRefreshHandler(
            _tokens, "default", timeProvider: clock) { InnerHandler = new HttpClientHandler() };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(_server.Url + "/") };
        var client = new NodePilotApiClient(http) { BearerToken = "transient-failure-token" };

        var callers = await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(_ => client.MeAsync(CancellationToken.None)));

        callers.Should().OnlyContain(me => me.Username == "admin");
        _server.LogEntries.Count(entry =>
            entry.RequestMessage!.AbsolutePath == "/api/auth/refresh").Should().Be(1);
        _server.LogEntries.Count(entry =>
            entry.RequestMessage!.AbsolutePath == "/api/auth/me").Should().Be(100);
        _tokens.Load("default")!.Token.Should().Be("transient-failure-token");

        // The cooldown suppresses a queued herd, but must not disable refresh permanently.
        clock.Advance(ClientSessionSecurity.TransientRefreshFailureCooldown + TimeSpan.FromSeconds(1));
        (await client.MeAsync(CancellationToken.None)).Username.Should().Be("admin");
        _server.LogEntries.Count(entry =>
            entry.RequestMessage!.AbsolutePath == "/api/auth/refresh").Should().Be(2);
    }

    [Fact]
    public async Task IndependentHandlersSharingProfile_CoordinateOneCrossProcessRefresh()
    {
        _tokens.Save("default", new StoredSession
        {
            Server = _server.Url!,
            Token = "cross-process-expiring-token",
            Username = "admin",
            Role = "Admin",
            UserId = Guid.NewGuid(),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
        });
        _server.Given(Request.Create().WithPath("/api/auth/refresh").UsingPost()
                .WithHeader("Authorization", "Bearer cross-process-expiring-token"))
            .RespondWith(Response.Create()
                .WithDelay(TimeSpan.FromMilliseconds(250))
                .WithStatusCode(200)
                .WithBodyAsJson(new
                {
                    token = "cross-process-fresh-token",
                    userId = Guid.NewGuid(),
                    username = "admin",
                    role = "Admin",
                    expiresAt = DateTimeOffset.UtcNow.AddHours(8),
                }));
        _server.Given(Request.Create().WithPath("/api/auth/me").UsingGet()
                .WithHeader("Authorization", "Bearer cross-process-fresh-token"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id = Guid.NewGuid(), username = "admin", role = "Admin",
            }));

        // Separate stores + handlers model the independently hosted CLI and MCP processes.
        // A process-local SemaphoreSlim cannot coordinate these two refresh pipelines.
        var otherProcessStore = new TokenStore(_dir);
        var firstHandler = new TokenRefreshHandler(_tokens, "default")
        {
            InnerHandler = new HttpClientHandler(),
        };
        var secondHandler = new TokenRefreshHandler(otherProcessStore, "default")
        {
            InnerHandler = new HttpClientHandler(),
        };
        using var firstHttp = new HttpClient(firstHandler) { BaseAddress = new Uri(_server.Url + "/") };
        using var secondHttp = new HttpClient(secondHandler) { BaseAddress = new Uri(_server.Url + "/") };
        var firstClient = new NodePilotApiClient(firstHttp) { BearerToken = "cross-process-expiring-token" };
        var secondClient = new NodePilotApiClient(secondHttp) { BearerToken = "cross-process-expiring-token" };

        var callers = await Task.WhenAll(
            firstClient.MeAsync(CancellationToken.None),
            secondClient.MeAsync(CancellationToken.None));

        callers.Should().OnlyContain(me => me.Username == "admin");
        _server.LogEntries.Count(entry =>
            entry.RequestMessage!.AbsolutePath == "/api/auth/refresh").Should().Be(1);
        _tokens.Load("default")!.Token.Should().Be("cross-process-fresh-token");
    }

    [Fact]
    public async Task SameHandler_CanRotateMoreThanOnce_AndFutureRequestsUseLatestToken()
    {
        _server.Given(Request.Create().WithPath("/api/auth/me").UsingGet()
                .WithHeader("Authorization", "Bearer stale-token"))
            .RespondWith(Response.Create().WithStatusCode(401));
        _server.Given(Request.Create().WithPath("/api/auth/refresh").UsingPost()
                .WithHeader("Authorization", "Bearer stale-token"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                token = "rotation-two",
                userId = Guid.NewGuid(),
                username = "admin",
                role = "Admin",
                expiresAt = DateTimeOffset.UtcNow.AddHours(8),
            }));
        _server.Given(Request.Create().WithPath("/api/auth/me").UsingGet()
                .WithHeader("Authorization", "Bearer rotation-two"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id = Guid.NewGuid(), username = "admin", role = "Admin",
            }));

        _server.Given(Request.Create().WithPath("/api/workflows").UsingGet()
                .WithHeader("Authorization", "Bearer rotation-two"))
            .RespondWith(Response.Create().WithStatusCode(401));
        _server.Given(Request.Create().WithPath("/api/auth/refresh").UsingPost()
                .WithHeader("Authorization", "Bearer rotation-two"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                token = "rotation-three",
                userId = Guid.NewGuid(),
                username = "admin",
                role = "Admin",
                expiresAt = DateTimeOffset.UtcNow.AddHours(8),
            }));
        _server.Given(Request.Create().WithPath("/api/workflows").UsingGet()
                .WithHeader("Authorization", "Bearer rotation-three"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("[]"));

        var handler = new TokenRefreshHandler(_tokens, "default") { InnerHandler = new HttpClientHandler() };
        var http = new HttpClient(handler) { BaseAddress = new Uri(_server.Url + "/") };
        var client = new NodePilotApiClient(http) { BearerToken = "stale-token" };

        (await client.MeAsync(CancellationToken.None)).Username.Should().Be("admin");
        (await client.ListWorkflowsAsync(CancellationToken.None)).Should().BeEmpty();

        _tokens.Load("default")!.Token.Should().Be("rotation-three");
        _server.LogEntries.Count(entry =>
            entry.RequestMessage!.AbsolutePath == "/api/auth/refresh").Should().Be(2);
    }

    [Fact]
    public async Task ExpiredSession_DoesNotAttemptRefresh_AndRequiresLogin()
    {
        _tokens.Save("default", new StoredSession
        {
            Server = _server.Url!,
            Token = "expired-token",
            Username = "admin",
            Role = "Admin",
            UserId = Guid.NewGuid(),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });
        _server.Given(Request.Create().WithPath("/api/auth/refresh").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/api/auth/me").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        var handler = new TokenRefreshHandler(_tokens, "default") { InnerHandler = new HttpClientHandler() };
        var http = new HttpClient(handler) { BaseAddress = new Uri(_server.Url + "/") };
        var client = new NodePilotApiClient(http) { BearerToken = "expired-token" };

        var act = () => client.MeAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ApiException>();
        ex.Which.IsUnauthorized.Should().BeTrue();
        _tokens.Load("default").Should().BeNull();
        _server.LogEntries.Should().BeEmpty();
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
            entry.RequestMessage!.AbsolutePath == "/api/auth/refresh");
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private static string Jwt(DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        static string Encode(string value) => Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            iat = issuedAt.ToUnixTimeSeconds(),
            np_iat_ms = issuedAt.ToUnixTimeMilliseconds(),
            exp = expiresAt.ToUnixTimeSeconds(),
        });
        return $"{Encode("{\"alg\":\"none\"}")}.{Encode(payload)}.";
    }
}
