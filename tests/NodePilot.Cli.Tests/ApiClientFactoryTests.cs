using FluentAssertions;
using NodePilot.Cli.Api;
using NodePilot.Cli.Auth;
using Xunit;

namespace NodePilot.Cli.Tests;

public sealed class ApiClientFactoryTests : IDisposable
{
    private readonly string _dir;
    private readonly ApiClientFactory _factory;

    public ApiClientFactoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "np-factory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _factory = new ApiClientFactory(new TokenStore(_dir));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Create_WithoutServer_Throws()
    {
        var ctx = new SessionContext { Profile = "default", Server = null, Session = null };
        Action act = () => _factory.Create(ctx);
        act.Should().Throw<InvalidOperationException>().WithMessage("*server*");
    }

    [Fact]
    public void Create_RequireAuthAndNoSession_ThrowsNotAuthenticated()
    {
        var ctx = new SessionContext { Profile = "default", Server = "https://np.local", Session = null };
        Action act = () => _factory.Create(ctx);
        act.Should().Throw<NotAuthenticatedException>().WithMessage("*default*");
    }

    [Fact]
    public void Create_RequireAuthFalse_NoSession_StillReturnsClient()
    {
        var ctx = new SessionContext { Profile = "default", Server = "https://np.local", Session = null };
        var client = _factory.Create(ctx, requireAuth: false);
        client.BaseAddress!.AbsoluteUri.Should().StartWith("https://np.local");
        client.BearerToken.Should().BeNull();
    }

    [Fact]
    public void Create_WithSession_AttachesBearerToken()
    {
        var session = new StoredSession
        {
            Server = "https://np.local",
            Token = "abc",
            Username = "admin",
            Role = "Admin",
            UserId = Guid.NewGuid(),
            ExpiresAt = DateTime.UtcNow.AddHours(12),
        };
        var ctx = new SessionContext { Profile = "default", Server = "https://np.local", Session = session };

        var client = _factory.Create(ctx);
        client.BearerToken.Should().Be("abc");
    }

    [Fact]
    public void Create_NormalizesBaseUriWithTrailingSlash()
    {
        var ctx = new SessionContext { Profile = "default", Server = "https://np.local", Session = null };
        var client = _factory.Create(ctx, requireAuth: false);
        client.BaseAddress!.AbsoluteUri.Should().EndWith("/");
    }

    [Fact]
    public void CreateAnonymous_BuildsClientWithBaseAddress()
    {
        var client = _factory.CreateAnonymous("https://np.local");
        client.BaseAddress!.AbsoluteUri.Should().Be("https://np.local/");
        client.BearerToken.Should().BeNull();
    }

    [Theory]
    [InlineData("http://np.local")]
    [InlineData("ftp://np.local")]
    [InlineData("not-a-url")]
    public void CreateAnonymous_RejectsAnythingExceptAbsoluteHttps(string serverUrl)
    {
        Action act = () => _factory.CreateAnonymous(serverUrl);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*HTTPS*");
    }

    [Fact]
    public void CreateAnonymous_ExplicitOptIn_AllowsOnlyHttpLoopback()
    {
        _factory.CreateAnonymous("http://127.0.0.1:5000", allowInsecureLoopback: true)
            .BaseAddress!.AbsoluteUri.Should().Be("http://127.0.0.1:5000/");

        Action remote = () => _factory.CreateAnonymous(
            "http://nodepilot.internal", allowInsecureLoopback: true);
        remote.Should().Throw<InvalidOperationException>().WithMessage("*HTTPS*");
    }

    [Fact]
    public void Create_SessionOptIn_AllowsOnlyHttpLoopback()
    {
        var context = new SessionContext
        {
            Profile = "default",
            Server = "http://localhost:5000",
            Session = null,
            AllowInsecureLoopback = true,
        };

        _factory.Create(context, requireAuth: false).BaseAddress!.AbsoluteUri
            .Should().Be("http://localhost:5000/");
    }

    [Fact]
    public void Create_SessionWithoutOptIn_RejectsHttpLoopbackWithFlagHint()
    {
        var context = new SessionContext
        {
            Profile = "default",
            Server = "http://localhost:5000",
            Session = null,
        };

        Action act = () => _factory.Create(context, requireAuth: false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*--allow-insecure*");
    }
}
