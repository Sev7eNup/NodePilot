using FluentAssertions;
using NodePilot.Cli.Auth;
using Xunit;

namespace NodePilot.Cli.Tests;

public sealed class TokenStoreTests : IDisposable
{
    private readonly string _dir;

    public TokenStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "np-cli-tokens-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void SaveAndLoad_DpapiRoundtrips()
    {
        var store = new TokenStore(_dir);
        var session = new StoredSession
        {
            Server = "https://np.example",
            Token = "deadbeef.jwt.token",
            Username = "admin",
            UserId = Guid.NewGuid(),
            Role = "Admin",
            ExpiresAt = DateTime.UtcNow.AddHours(12),
        };
        store.Save("default", session);

        // File on disk must NOT contain the plaintext token.
        var bytes = File.ReadAllBytes(store.PathFor("default"));
        var asString = System.Text.Encoding.UTF8.GetString(bytes);
        asString.Should().NotContain(session.Token);

        var loaded = store.Load("default");
        loaded.Should().NotBeNull();
        loaded!.Token.Should().Be(session.Token);
        loaded.Username.Should().Be("admin");
        loaded.Role.Should().Be("Admin");
    }

    [Fact]
    public void Load_NoFile_ReturnsNull()
    {
        var store = new TokenStore(_dir);
        store.Load("missing").Should().BeNull();
    }

    [Fact]
    public void Load_CorruptFile_ReturnsNull()
    {
        var store = new TokenStore(_dir);
        File.WriteAllBytes(store.PathFor("broken"), new byte[] { 0x01, 0x02, 0x03, 0x04 });
        store.Load("broken").Should().BeNull();
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        var store = new TokenStore(_dir);
        store.Save("dev", new StoredSession { Server = "x", Token = "y", Username = "u", Role = "Viewer", ExpiresAt = DateTime.UtcNow });
        File.Exists(store.PathFor("dev")).Should().BeTrue();
        store.Delete("dev");
        File.Exists(store.PathFor("dev")).Should().BeFalse();
    }

    [Fact]
    public void IsExpired_RespectsSkew()
    {
        var session = new StoredSession { ExpiresAt = DateTime.UtcNow.AddSeconds(30) };
        session.IsExpired(skew: TimeSpan.FromSeconds(10)).Should().BeFalse();
        session.IsExpired(skew: TimeSpan.FromSeconds(60)).Should().BeTrue();
    }
}
