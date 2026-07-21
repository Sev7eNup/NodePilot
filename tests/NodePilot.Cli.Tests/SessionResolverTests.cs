using FluentAssertions;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Settings;
using NodePilot.Cli.Tests.Infra;
using Xunit;

namespace NodePilot.Cli.Tests;

[Collection(CommandTestCollection.Name)]
public sealed class SessionResolverTests : IDisposable
{
    private readonly string _dir;
    private readonly ConfigStore _config;
    private readonly TokenStore _tokens;
    private readonly SessionResolver _resolver;

    public SessionResolverTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "np-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigStore(_dir);
        _tokens = new TokenStore(_dir);
        _resolver = new SessionResolver(_config, _tokens);

        var cfg = new CliConfig { DefaultProfile = "default" };
        cfg.Profiles["default"] = new ProfileEntry { Server = "https://np.local" };
        cfg.Profiles["prod"] = new ProfileEntry { Server = "https://np.prod" };
        _config.Save(cfg);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Resolve_NoFlags_FallsBackToDefaultProfile()
    {
        var ctx = _resolver.Resolve(new GlobalSettings());
        ctx.Profile.Should().Be("default");
        ctx.Server.Should().Be("https://np.local");
        ctx.HasSession.Should().BeFalse();
    }

    [Fact]
    public void Resolve_ProfileFlag_PicksProfileServer()
    {
        var ctx = _resolver.Resolve(new GlobalSettings { Profile = "prod" });
        ctx.Profile.Should().Be("prod");
        ctx.Server.Should().Be("https://np.prod");
    }

    [Fact]
    public void Resolve_ServerFlag_OverridesProfileAndDropsMismatchedSession()
    {
        // Stored session points at the prod server; caller forces a different server URL.
        _tokens.Save("prod", new StoredSession
        {
            Server = "https://np.prod",
            Token = "tok",
            Username = "admin",
            Role = "Admin",
            UserId = Guid.NewGuid(),
            ExpiresAt = DateTime.UtcNow.AddHours(12),
        });

        var ctx = _resolver.Resolve(new GlobalSettings { Profile = "prod", Server = "https://other-server" });
        ctx.Server.Should().Be("https://other-server");
        ctx.HasSession.Should().BeFalse();
    }

    [Fact]
    public void Resolve_ServerFlagMatchesSession_KeepsSession()
    {
        _tokens.Save("default", new StoredSession
        {
            Server = "https://np.local",
            Token = "tok",
            Username = "admin",
            Role = "Admin",
            UserId = Guid.NewGuid(),
            ExpiresAt = DateTime.UtcNow.AddHours(12),
        });

        var ctx = _resolver.Resolve(new GlobalSettings { Server = "https://np.local" });
        ctx.HasSession.Should().BeTrue();
        ctx.Session!.Token.Should().Be("tok");
    }

    [Fact]
    public void Resolve_NodePilotServerEnvironmentOverride_DropsSessionFromDifferentOrigin()
    {
        _tokens.Save("default", SessionFor("https://np.local", "origin-bound-token"));
        var previous = Environment.GetEnvironmentVariable("NODEPILOT_SERVER");
        try
        {
            Environment.SetEnvironmentVariable("NODEPILOT_SERVER", "https://attacker.example");

            var ctx = _resolver.Resolve(new GlobalSettings());

            ctx.Server.Should().Be("https://attacker.example");
            ctx.HasSession.Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NODEPILOT_SERVER", previous);
        }
    }

    [Theory]
    [InlineData("https://np.local", "https://NP.LOCAL:443/api/")]
    [InlineData("http://localhost", "http://LOCALHOST:80/development/")]
    public void Resolve_EquivalentDefaultPortOrigin_KeepsSession(
        string configuredServer,
        string sessionServer)
    {
        var cfg = _config.Load();
        cfg.Profiles["default"].Server = configuredServer;
        _config.Save(cfg);
        _tokens.Save("default", SessionFor(sessionServer, "tok"));

        var ctx = _resolver.Resolve(new GlobalSettings());

        ctx.HasSession.Should().BeTrue();
        ctx.Session!.Token.Should().Be("tok");
    }

    private static StoredSession SessionFor(string server, string token) => new()
    {
        Server = server,
        Token = token,
        Username = "admin",
        Role = "Admin",
        UserId = Guid.NewGuid(),
        ExpiresAt = DateTime.UtcNow.AddHours(12),
    };
}
