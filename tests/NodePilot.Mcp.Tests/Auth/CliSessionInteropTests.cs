using FluentAssertions;
using NodePilot.Core.Clients;
using NodePilot.Mcp.Config;
using Xunit;

namespace NodePilot.Mcp.Tests.Auth;

/// <summary>
/// The MCP server reuses the session that <c>np auth login</c> wrote, so the operator
/// authenticates once (see <c>docs/mcp-server.md</c>). The session store and the refresh
/// handler live once, in <c>NodePilot.Core.Clients</c>; this guard keeps a client from growing
/// its own copy again and pins the on-disk facts a change would orphan existing sessions with.
/// </summary>
public sealed class CliSessionInteropTests
{
    [Theory]
    [InlineData("src/NodePilot.Cli/Auth/TokenStore.cs")]
    [InlineData("src/NodePilot.Cli/Api/TokenRefreshHandler.cs")]
    [InlineData("src/NodePilot.Mcp/Auth/TokenStore.cs")]
    [InlineData("src/NodePilot.Mcp/Api/TokenRefreshHandler.cs")]
    public void SessionStoreAndRefreshHandler_HaveNoPerClientCopy(string relativePath)
    {
        File.Exists(Path.Combine(FindRepoRoot(), relativePath)).Should().BeFalse(
            "the session store and the refresh handler are shared through NodePilot.Core.Clients; " +
            "a per-client copy would drift from the other client without any visible error");
    }

    [Fact]
    public void McpAssembly_DefinesNoSessionTypesOfItsOwn()
    {
        typeof(McpServerConfig).Assembly.GetTypes()
            .Select(t => t.Name)
            .Should().NotContain(["TokenStore", "TokenRefreshHandler", "StoredSession"]);
    }

    [Fact]
    public void DpapiEntropy_IsPartOfTheOnDiskFormat()
    {
        // Changing it orphans every existing `np auth login` session, with a failure that looks
        // like "not logged in" rather than like a broken contract.
        ClientSessionSecurity.DpapiSessionEntropy.Should().Be("NodePilot.Cli/v1");
    }

    [Fact]
    public void TokenStore_AddressesTheSessionFileTheCliWrites()
    {
        var dir = Path.Combine(Path.GetTempPath(), "np-mcp-interop-" + Guid.NewGuid().ToString("N"));
        try
        {
            new TokenStore(dir).PathFor("prod").Should().Be(Path.Combine(dir, "session-prod.dat"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void StoredSession_ReadsLegacyCliUtcDateTimeJson()
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

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NodePilot.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException($"Could not locate NodePilot.slnx walking up from {AppContext.BaseDirectory}");
    }
}
