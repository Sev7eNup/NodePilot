using FluentAssertions;
using NodePilot.Cli.Auth;
using Xunit;

namespace NodePilot.Cli.Tests.Auth;

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
    public void StoredSession_OldUtcDateTimeJson_RemainsReadableAsDateTimeOffset()
    {
        const string legacyJson =
            """
            {"server":"https://np.example","token":"legacy","username":"admin","userId":"00000000-0000-0000-0000-000000000001","role":"Admin","expiresAt":"2026-08-15T12:34:56Z"}
            """;

        var session = System.Text.Json.JsonSerializer.Deserialize<StoredSession>(
            legacyJson,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        session.Should().NotBeNull();
        session!.ExpiresAt.Should().Be(new DateTimeOffset(2026, 8, 15, 12, 34, 56, TimeSpan.Zero));
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
    public async Task ConcurrentStoreInstances_SaveAndLoad_NeverExposePartialEncryptedBlob()
    {
        var first = new TokenStore(_dir);
        var second = new TokenStore(_dir);
        var largeTokenA = "a." + new string('A', 64 * 1024) + ".sig";
        var largeTokenB = "b." + new string('B', 64 * 1024) + ".sig";
        StoredSession Session(string token) => new()
        {
            Server = "https://np.example",
            Token = token,
            Username = "admin",
            UserId = Guid.NewGuid(),
            Role = "Admin",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(8),
        };
        first.Save("shared", Session(largeTokenA));

        using var start = new ManualResetEventSlim(false);
        var writers = Enumerable.Range(0, 24).Select(i => Task.Run(() =>
        {
            start.Wait();
            (i % 2 == 0 ? first : second).Save(
                "shared", Session(i % 2 == 0 ? largeTokenA : largeTokenB));
        })).ToArray();
        var readers = Enumerable.Range(0, 80).Select(i => Task.Run(() =>
        {
            start.Wait();
            var loaded = (i % 2 == 0 ? first : second).Load("shared");
            loaded.Should().NotBeNull("atomic replacement must expose either complete generation");
            loaded!.Token.Should().BeOneOf(largeTokenA, largeTokenB);
        })).ToArray();

        start.Set();
        await Task.WhenAll(writers.Concat(readers));

        var final = first.Load("shared");
        final.Should().NotBeNull();
        final!.Token.Should().BeOneOf(largeTokenA, largeTokenB);
        Directory.EnumerateFiles(_dir, "*.tmp").Should().BeEmpty();
    }
}
