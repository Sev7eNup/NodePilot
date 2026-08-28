using System.Net;
using FluentAssertions;
using NodePilot.Mcp.Api;
using NodePilot.Mcp.Auth;
using NodePilot.Mcp.Config;
using NodePilot.Mcp.Mapping;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;
using NodePilot.Core.Clients;

namespace NodePilot.Mcp.Tests.Api;

/// <summary>Plumbing coverage: error mapping, config resolution, client guard, token store,
/// factory.</summary>
[Collection("EnvMutating")]
public sealed class InfraTests
{
    // ---- ApiErrorMapper -----------------------------------------------------

    [Fact]
    public async Task ApiErrorMapper_MapsStatusCodesToActionableMessages()
    {
        (await Throws(new ApiException(HttpStatusCode.Unauthorized, null, null, null))).Should().Contain("np auth login");
        (await Throws(new ApiException(HttpStatusCode.Forbidden, "Forbidden", "needs Admin", null))).Should().Contain("Permission denied");
        (await Throws(new ApiException((HttpStatusCode)423, "Locked", "by bob", null))).Should().Contain("checked out");
        (await Throws(new ApiException(HttpStatusCode.Conflict, "Conflict", "already", null))).Should().Contain("Conflict");
        (await Throws(new ApiException(HttpStatusCode.NotFound, "NotFound", "no such", null))).Should().Contain("Not found");
        (await Throws(new NotConfiguredException("set NODEPILOT_MCP_SERVER"))).Should().Contain("NODEPILOT_MCP_SERVER");
        (await Throws(new HttpRequestException("dns"))).Should().Contain("Cannot reach");
    }

    private static async Task<string> Throws(Exception toThrow)
    {
        try { await ApiErrorMapper.Guard<int>(() => throw toThrow); return "(no throw)"; }
        catch (Exception ex) { return ex.Message; }
    }

    // ---- NodePilotApiClient guard ------------------------------------------

    [Fact]
    public async Task ApiClient_WithoutBaseAddress_ThrowsNotConfigured()
    {
        var client = new NodePilotApiClient(new HttpClient());
        await Assert.ThrowsAsync<NotConfiguredException>(() => client.MeAsync(CancellationToken.None));
    }

    // ---- TokenStore round-trip ---------------------------------------------

    [Fact]
    public void TokenStore_SavesAndLoads_AndSanitizesProfile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "np-mcp-tok-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TokenStore(dir);
            var session = new StoredSession { Server = "https://x/", Token = "jwt", Username = "admin", UserId = Guid.NewGuid(), Role = "Admin", ExpiresAt = DateTime.UtcNow.AddHours(12) };
            store.Save("pro/d:1", session); // illegal path chars -> sanitized

            var loaded = store.Load("pro/d:1");
            loaded.Should().NotBeNull();
            loaded!.Token.Should().Be("jwt");
            loaded.Role.Should().Be("Admin");
            store.Load("does-not-exist").Should().BeNull();
        }
        finally { TryDelete(dir); }
    }

    // ---- McpServerConfig resolution ----------------------------------------

    [Fact]
    public void Resolve_RawEnvToken_IsNotRefreshable_AndServerFromEnv()
    {
        WithEnv(new() { ["NODEPILOT_MCP_SERVER"] = "https://env-srv/", ["NODEPILOT_MCP_TOKEN"] = "raw-bearer" }, () =>
        {
            var dir = Temp();
            try
            {
                var session = new McpServerConfig(new ClientConfigStore(dir), new TokenStore(dir)).Resolve();
                session.Server.Should().Be("https://env-srv/");
                session.Token.Should().Be("raw-bearer");
                session.UsesRefreshableSession.Should().BeFalse();
                session.HasServer.Should().BeTrue();
            }
            finally { TryDelete(dir); }
        });
    }

    [Fact]
    public void Resolve_FallsBackToCliProfileServerAndDpapiSession()
    {
        WithEnv(ClearedConfigEnv(), () =>
        {
            var dir = Temp();
            try
            {
                File.WriteAllText(Path.Combine(dir, "config.json"),
                    "{\"defaultProfile\":\"prod\",\"profiles\":{\"prod\":{\"server\":\"https://prod-srv/\"}}}");
                var tokens = new TokenStore(dir);
                tokens.Save("prod", new StoredSession { Server = "https://prod-srv/", Token = "dpapi-jwt", Username = "u", UserId = Guid.NewGuid(), Role = "Operator", ExpiresAt = DateTime.UtcNow.AddHours(12) });

                var session = new McpServerConfig(new ClientConfigStore(dir), tokens).Resolve();
                session.Profile.Should().Be("prod");
                session.Server.Should().Be("https://prod-srv/");
                session.Token.Should().Be("dpapi-jwt");
                session.UsesRefreshableSession.Should().BeTrue(); // came from the store -> auto-refresh wired
            }
            finally { TryDelete(dir); }
        });
    }

    [Fact]
    public void IsDestructiveAllowed_ReadsEnvFlag()
    {
        WithEnv(new() { ["NODEPILOT_MCP_ALLOW_DESTRUCTIVE"] = "true" }, () => McpServerConfig.IsDestructiveAllowed().Should().BeTrue());
        WithEnv(new() { ["NODEPILOT_MCP_ALLOW_DESTRUCTIVE"] = "0" }, () => McpServerConfig.IsDestructiveAllowed().Should().BeFalse());
    }

    // ---- ApiClientFactory ---------------------------------------------------

    [Fact]
    public void ApiClientFactory_BuildsConfiguredClientFromSession()
    {
        WithEnv(new() { ["NODEPILOT_MCP_SERVER"] = "https://factory-srv/", ["NODEPILOT_MCP_TOKEN"] = "tok" }, () =>
        {
            var dir = Temp();
            try
            {
                var cfg = new McpServerConfig(new ClientConfigStore(dir), new TokenStore(dir));
                var client = new ApiClientFactory(cfg, new TokenStore(dir)).Create();
                client.Session!.HasServer.Should().BeTrue();
                client.Session.HasToken.Should().BeTrue();
                client.BearerToken.Should().Be("tok");
            }
            finally { TryDelete(dir); }
        });
    }

    [Fact]
    public void ApiClientFactory_RejectsInsecureServerUrl()
    {
        WithEnv(new() { ["NODEPILOT_MCP_SERVER"] = "http://np.local", ["NODEPILOT_MCP_TOKEN"] = "tok" }, () =>
        {
            var dir = Temp();
            try
            {
                var cfg = new McpServerConfig(new ClientConfigStore(dir), new TokenStore(dir));

                Action act = () => new ApiClientFactory(cfg, new TokenStore(dir)).Create();

                act.Should().Throw<InvalidOperationException>().WithMessage("*HTTPS*");
            }
            finally { TryDelete(dir); }
        });
    }

    [Fact]
    public void Resolve_McpServerEnvironmentOverride_DropsSessionFromDifferentOrigin()
    {
        WithEnv(new() { ["NODEPILOT_MCP_SERVER"] = "https://attacker.example" }, () =>
        {
            var dir = Temp();
            try
            {
                var tokens = new TokenStore(dir);
                tokens.Save("default", StoredSessionFor("https://trusted.example", "origin-bound-token"));

                var session = new McpServerConfig(new ClientConfigStore(dir), tokens).Resolve();

                session.Server.Should().Be("https://attacker.example");
                session.HasToken.Should().BeFalse();
                session.UsesRefreshableSession.Should().BeFalse();
            }
            finally { TryDelete(dir); }
        });
    }

    [Theory]
    [InlineData("https://trusted.example", "https://TRUSTED.EXAMPLE:443/api/")]
    [InlineData("http://localhost", "http://LOCALHOST:80/development/")]
    public void Resolve_EquivalentDefaultPortOrigin_KeepsRefreshableSession(
        string environmentServer,
        string storedServer)
    {
        WithEnv(new() { ["NODEPILOT_MCP_SERVER"] = environmentServer }, () =>
        {
            var dir = Temp();
            try
            {
                var tokens = new TokenStore(dir);
                tokens.Save("default", StoredSessionFor(storedServer, "tok"));

                var session = new McpServerConfig(new ClientConfigStore(dir), tokens).Resolve();

                session.Token.Should().Be("tok");
                session.UsesRefreshableSession.Should().BeTrue();
            }
            finally { TryDelete(dir); }
        });
    }

    [Fact]
    public async Task TokenRefreshHandler_StoreChangedAfterClientCreation_DoesNotUseForeignOriginToken()
    {
        var dir = Temp();
        using var server = WireMockServer.Start();
        try
        {
            server.Given(Request.Create().WithPath("/api/auth/me").UsingGet())
                .RespondWith(Response.Create().WithStatusCode(401));
            var tokens = new TokenStore(dir);
            tokens.Save("default", StoredSessionFor(server.Url!, "stale-token"));

            var handler = new TokenRefreshHandler(tokens, "default")
            {
                InnerHandler = new HttpClientHandler(),
            };
            var http = new HttpClient(handler) { BaseAddress = new Uri(server.Url + "/") };
            var client = new NodePilotApiClient(http) { BearerToken = "stale-token" };

            tokens.Save("default", StoredSessionFor(
                "https://attacker.example", "foreign-origin-token"));

            Func<Task> act = () => client.MeAsync(CancellationToken.None);

            await act.Should().ThrowAsync<ApiException>();
            server.LogEntries.Should().NotContain(entry =>
                entry.RequestMessage!.AbsolutePath == "/api/auth/refresh");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task TokenRefreshHandler_ConcurrentExpiringToolCalls_ShareOneRefresh()
    {
        var dir = Temp();
        using var server = WireMockServer.Start();
        try
        {
            var tokens = new TokenStore(dir);
            tokens.Save("default", new StoredSession
            {
                Server = server.Url!,
                Token = "mcp-expiring-token",
                Username = "admin",
                UserId = Guid.NewGuid(),
                Role = "Admin",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            });
            server.Given(Request.Create().WithPath("/api/auth/refresh").UsingPost()
                    .WithHeader("Authorization", "Bearer mcp-expiring-token"))
                .RespondWith(Response.Create()
                    .WithDelay(TimeSpan.FromMilliseconds(150))
                    .WithStatusCode(200)
                    .WithBodyAsJson(new
                    {
                        token = "mcp-fresh-token",
                        userId = Guid.NewGuid(),
                        username = "admin",
                        role = "Admin",
                        expiresAt = DateTimeOffset.UtcNow.AddHours(8),
                    }));
            server.Given(Request.Create().WithPath("/api/auth/me").UsingGet()
                    .WithHeader("Authorization", "Bearer mcp-fresh-token"))
                .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
                {
                    id = Guid.NewGuid(), username = "admin", role = "Admin",
                }));

            var handler = new TokenRefreshHandler(tokens, "default")
            {
                InnerHandler = new HttpClientHandler(),
            };
            var http = new HttpClient(handler) { BaseAddress = new Uri(server.Url + "/") };
            var client = new NodePilotApiClient(http) { BearerToken = "mcp-expiring-token" };

            var callers = await Task.WhenAll(
                client.MeAsync(CancellationToken.None),
                client.MeAsync(CancellationToken.None));

            callers.Should().OnlyContain(me => me.Username == "admin");
            tokens.Load("default")!.Token.Should().Be("mcp-fresh-token");
            server.LogEntries.Count(entry =>
                entry.RequestMessage!.AbsolutePath == "/api/auth/refresh").Should().Be(1);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task TokenRefreshHandler_ConcurrentTransientRefreshFailures_UseBoundedCooldown()
    {
        var dir = Temp();
        using var server = WireMockServer.Start();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var clock = new ManualTimeProvider(now);
            var tokens = new TokenStore(dir);
            tokens.Save("default", new StoredSession
            {
                Server = server.Url!,
                Token = "mcp-transient-failure",
                Username = "admin",
                UserId = Guid.NewGuid(),
                Role = "Admin",
                ExpiresAt = now.AddMinutes(1),
            });
            server.Given(Request.Create().WithPath("/api/auth/refresh").UsingPost()
                    .WithHeader("Authorization", "Bearer mcp-transient-failure"))
                .RespondWith(Response.Create().WithStatusCode(503));
            server.Given(Request.Create().WithPath("/api/auth/me").UsingGet()
                    .WithHeader("Authorization", "Bearer mcp-transient-failure"))
                .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
                {
                    id = Guid.NewGuid(), username = "admin", role = "Admin",
                }));

            var handler = new TokenRefreshHandler(tokens, "default", timeProvider: clock)
            {
                InnerHandler = new HttpClientHandler(),
            };
            using var http = new HttpClient(handler) { BaseAddress = new Uri(server.Url + "/") };
            var client = new NodePilotApiClient(http) { BearerToken = "mcp-transient-failure" };

            var callers = await Task.WhenAll(Enumerable.Range(0, 100)
                .Select(_ => client.MeAsync(CancellationToken.None)));

            callers.Should().OnlyContain(me => me.Username == "admin");
            server.LogEntries.Count(entry =>
                entry.RequestMessage!.AbsolutePath == "/api/auth/refresh").Should().Be(1);
            server.LogEntries.Count(entry =>
                entry.RequestMessage!.AbsolutePath == "/api/auth/me").Should().Be(100);
            tokens.Load("default")!.Token.Should().Be("mcp-transient-failure");

            clock.Advance(ClientSessionSecurity.TransientRefreshFailureCooldown + TimeSpan.FromSeconds(1));
            (await client.MeAsync(CancellationToken.None)).Username.Should().Be("admin");
            server.LogEntries.Count(entry =>
                entry.RequestMessage!.AbsolutePath == "/api/auth/refresh").Should().Be(2);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task TokenRefreshHandler_IndependentProcessesSharingProfile_UseOneRefresh()
    {
        var dir = Temp();
        using var server = WireMockServer.Start();
        try
        {
            var firstStore = new TokenStore(dir);
            var secondStore = new TokenStore(dir);
            firstStore.Save("default", new StoredSession
            {
                Server = server.Url!,
                Token = "mcp-cross-process-expiring",
                Username = "admin",
                UserId = Guid.NewGuid(),
                Role = "Admin",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            });
            server.Given(Request.Create().WithPath("/api/auth/refresh").UsingPost()
                    .WithHeader("Authorization", "Bearer mcp-cross-process-expiring"))
                .RespondWith(Response.Create()
                    .WithDelay(TimeSpan.FromMilliseconds(250))
                    .WithStatusCode(200)
                    .WithBodyAsJson(new
                    {
                        token = "mcp-cross-process-fresh",
                        userId = Guid.NewGuid(),
                        username = "admin",
                        role = "Admin",
                        expiresAt = DateTimeOffset.UtcNow.AddHours(8),
                    }));
            server.Given(Request.Create().WithPath("/api/auth/me").UsingGet()
                    .WithHeader("Authorization", "Bearer mcp-cross-process-fresh"))
                .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
                {
                    id = Guid.NewGuid(), username = "admin", role = "Admin",
                }));

            var firstHandler = new TokenRefreshHandler(firstStore, "default")
            {
                InnerHandler = new HttpClientHandler(),
            };
            var secondHandler = new TokenRefreshHandler(secondStore, "default")
            {
                InnerHandler = new HttpClientHandler(),
            };
            using var firstHttp = new HttpClient(firstHandler) { BaseAddress = new Uri(server.Url + "/") };
            using var secondHttp = new HttpClient(secondHandler) { BaseAddress = new Uri(server.Url + "/") };
            var firstClient = new NodePilotApiClient(firstHttp) { BearerToken = "mcp-cross-process-expiring" };
            var secondClient = new NodePilotApiClient(secondHttp) { BearerToken = "mcp-cross-process-expiring" };

            var callers = await Task.WhenAll(
                firstClient.MeAsync(CancellationToken.None),
                secondClient.MeAsync(CancellationToken.None));

            callers.Should().OnlyContain(me => me.Username == "admin");
            server.LogEntries.Count(entry =>
                entry.RequestMessage!.AbsolutePath == "/api/auth/refresh").Should().Be(1);
            firstStore.Load("default")!.Token.Should().Be("mcp-cross-process-fresh");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task TokenRefreshHandler_OlderResponseUsesJwtExpiry_AndNewProcessesRespectCooldown()
    {
        var dir = Temp();
        using var server = WireMockServer.Start();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var absoluteExpiry = now.AddMinutes(4);
            var clock = new ManualTimeProvider(now);
            const string initialToken = "mcp-rolling-upgrade-token";
            var firstRotatedToken = Jwt(now, absoluteExpiry);
            var secondIssuedAt = now
                + ClientSessionSecurity.SuccessfulRefreshDeduplicationWindow
                + TimeSpan.FromSeconds(1);
            var secondRotatedToken = Jwt(secondIssuedAt, absoluteExpiry);
            var tokens = new TokenStore(dir);
            tokens.Save("default", new StoredSession
            {
                Server = server.Url!,
                Token = initialToken,
                Username = "admin",
                UserId = Guid.NewGuid(),
                Role = "Admin",
                ExpiresAt = absoluteExpiry,
            });

            server.Given(Request.Create().WithPath("/api/auth/refresh").UsingPost()
                    .WithHeader("Authorization", $"Bearer {initialToken}"))
                .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
                {
                    token = firstRotatedToken,
                    userId = Guid.NewGuid(),
                    username = "admin",
                    role = "Admin",
                }));
            server.Given(Request.Create().WithPath("/api/auth/refresh").UsingPost()
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
                server.Given(Request.Create().WithPath("/api/auth/me").UsingGet()
                        .WithHeader("Authorization", $"Bearer {token}"))
                    .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
                    {
                        id = Guid.NewGuid(), username = "admin", role = "Admin",
                    }));
            }

            async Task CallFromNewProcessAsync()
            {
                var processStore = new TokenStore(dir);
                var current = processStore.Load("default")!;
                var handler = new TokenRefreshHandler(
                    processStore, "default", timeProvider: clock)
                {
                    InnerHandler = new HttpClientHandler(),
                };
                using var http = new HttpClient(handler) { BaseAddress = new Uri(server.Url + "/") };
                var client = new NodePilotApiClient(http) { BearerToken = current.Token };
                (await client.MeAsync(CancellationToken.None)).Username.Should().Be("admin");
            }

            await CallFromNewProcessAsync();
            for (var i = 0; i < 5; i++)
                await CallFromNewProcessAsync();

            server.LogEntries.Count(entry =>
                entry.RequestMessage!.AbsolutePath == "/api/auth/refresh").Should().Be(1);
            tokens.Load("default")!.ExpiresAt.Should()
                .BeCloseTo(absoluteExpiry, TimeSpan.FromSeconds(1));

            clock.Advance(
                ClientSessionSecurity.SuccessfulRefreshDeduplicationWindow
                + TimeSpan.FromSeconds(1));
            await CallFromNewProcessAsync();

            server.LogEntries.Count(entry =>
                entry.RequestMessage!.AbsolutePath == "/api/auth/refresh").Should().Be(2);
            tokens.Load("default")!.Token.Should().Be(secondRotatedToken);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task TokenRefreshHandler_ExpiredSession_DoesNotCallServerAndRequiresLogin()
    {
        var dir = Temp();
        using var server = WireMockServer.Start();
        try
        {
            var tokens = new TokenStore(dir);
            tokens.Save("default", new StoredSession
            {
                Server = server.Url!,
                Token = "mcp-expired-token",
                Username = "admin",
                UserId = Guid.NewGuid(),
                Role = "Admin",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            });
            server.Given(Request.Create().WithPath("/api/auth/refresh").UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
            server.Given(Request.Create().WithPath("/api/auth/me").UsingGet())
                .RespondWith(Response.Create().WithStatusCode(200));

            var handler = new TokenRefreshHandler(tokens, "default")
            {
                InnerHandler = new HttpClientHandler(),
            };
            var http = new HttpClient(handler) { BaseAddress = new Uri(server.Url + "/") };
            var client = new NodePilotApiClient(http) { BearerToken = "mcp-expired-token" };

            var act = () => client.MeAsync(CancellationToken.None);

            var ex = await act.Should().ThrowAsync<ApiException>();
            ex.Which.IsUnauthorized.Should().BeTrue();
            tokens.Load("default").Should().BeNull();
            server.LogEntries.Should().BeEmpty();
        }
        finally { TryDelete(dir); }
    }

    // ---- helpers ------------------------------------------------------------

    private static string Temp() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "np-mcp-cfg-" + Guid.NewGuid().ToString("N"))).FullName;

    private static StoredSession StoredSessionFor(string server, string token) => new()
    {
        Server = server,
        Token = token,
        Username = "admin",
        UserId = Guid.NewGuid(),
        Role = "Admin",
        ExpiresAt = DateTime.UtcNow.AddHours(12),
    };

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

    private static void TryDelete(string dir) { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }

    private static readonly string[] ConfigEnvVars =
        ["NODEPILOT_MCP_SERVER", "NODEPILOT_SERVER", "NODEPILOT_MCP_PROFILE", "NODEPILOT_PROFILE", "NODEPILOT_MCP_TOKEN"];

    private static Dictionary<string, string?> ClearedConfigEnv() => ConfigEnvVars.ToDictionary(v => v, _ => (string?)null);

    // Set the given env vars (and clear the other config vars), run, then restore everything.
    private static void WithEnv(Dictionary<string, string?> vars, Action body)
    {
        var toTouch = ConfigEnvVars.Concat(vars.Keys).Distinct().ToArray();
        var prev = toTouch.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var v in ConfigEnvVars) Environment.SetEnvironmentVariable(v, null);
            foreach (var (k, val) in vars) Environment.SetEnvironmentVariable(k, val);
            body();
        }
        finally
        {
            foreach (var (k, val) in prev) Environment.SetEnvironmentVariable(k, val);
        }
    }
}
