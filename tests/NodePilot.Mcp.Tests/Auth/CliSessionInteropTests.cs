using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace NodePilot.Mcp.Tests.Auth;

/// <summary>
/// The MCP server deliberately reuses the session that <c>np auth login</c> wrote, so the
/// operator authenticates once (see <c>docs/mcp-server.md</c>). That interop rests on three
/// values matching bit-for-bit across two independently maintained copies of
/// <c>Auth/TokenStore.cs</c>: the DPAPI entropy, the DPAPI scope and the session file name.
///
/// <para>Until now the contract existed only as a code comment ("Must match the CLI's entropy")
/// — a grep for <c>Entropy</c> across the whole test tree returned nothing. Change the CLI's
/// constant and the MCP server silently stops finding the session, with a failure that looks
/// like "not logged in" rather than like a broken contract.</para>
/// </summary>
public sealed class CliSessionInteropTests
{
    private static readonly string CliTokenStore =
        Path.Combine("src", "NodePilot.Cli", "Auth", "TokenStore.cs");

    private static readonly string McpTokenStore =
        Path.Combine("src", "NodePilot.Mcp", "Auth", "TokenStore.cs");

    [Fact]
    public void DpapiEntropy_ComesFromTheSharedCoreConstant_InBothStores()
    {
        // Since the coherence-audit fix the entropy lives ONCE in Core
        // (ClientSessionSecurity.DpapiSessionEntropy). Both stores must reference that
        // constant — a hand-restored literal would reintroduce the silent-breakage coupling.
        ExtractEntropyExpression(CliTokenStore).Should().Contain("ClientSessionSecurity.DpapiSessionEntropy",
            "die CLI muss die geteilte Core-Konstante nutzen, kein eigenes Literal");
        ExtractEntropyExpression(McpTokenStore).Should().Contain("ClientSessionSecurity.DpapiSessionEntropy",
            "der MCP-Server muss die geteilte Core-Konstante nutzen, kein eigenes Literal");

        // And the constant itself is part of the ON-DISK format: changing it orphans every
        // existing `np auth login` session. Pin the value.
        NodePilot.Core.Clients.ClientSessionSecurity.DpapiSessionEntropy.Should().Be("NodePilot.Cli/v1",
            "die Entropie ist Teil des Session-Blob-Formats — eine Änderung macht alle " +
            "bestehenden Sessions unlesbar (Symptom: irreführendes 'nicht angemeldet')");
    }

    [Fact]
    public void DpapiScope_IsIdenticalInBothStores()
    {
        var cli = ExtractScope(CliTokenStore);
        var mcp = ExtractScope(McpTokenStore);

        cli.Should().NotBeEmpty();
        mcp.Should().Be(cli, "ein abweichender DataProtectionScope macht den Blob unlesbar");
    }

    [Fact]
    public void SessionFileName_IsIdenticalInBothStores()
    {
        var cli = ExtractPathPattern(CliTokenStore);
        var mcp = ExtractPathPattern(McpTokenStore);

        mcp.Should().Be(cli,
            "beide Seiten müssen dieselbe Datei adressieren, sonst schreibt die CLI eine " +
            "Session, die der MCP-Server nie findet");
    }

    [Fact]
    public void McpStoredSession_ReadsLegacyCliUtcDateTimeJson()
    {
        const string legacyJson =
            """
            {"server":"https://np.example","token":"legacy","username":"admin","userId":"00000000-0000-0000-0000-000000000001","role":"Admin","expiresAt":"2026-08-15T12:34:56Z"}
            """;

        var session = System.Text.Json.JsonSerializer.Deserialize<NodePilot.Mcp.Auth.StoredSession>(
            legacyJson,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        session.Should().NotBeNull();
        session!.ExpiresAt.Should().Be(new DateTimeOffset(2026, 8, 15, 12, 34, 56, TimeSpan.Zero));
    }

    private static string ExtractEntropyExpression(string relativePath)
    {
        var source = ReadRepoFile(relativePath);
        var match = Regex.Match(source, @"Entropy\s*=\s*Encoding\.UTF8\.GetBytes\((?<expr>[^)]+)\)");
        match.Success.Should().BeTrue($"{relativePath} must declare the DPAPI entropy via Encoding.UTF8.GetBytes(…)");
        return match.Groups["expr"].Value;
    }

    private static string ExtractScope(string relativePath)
    {
        var source = ReadRepoFile(relativePath);
        var match = Regex.Match(source, @"DataProtectionScope\.(?<value>\w+)");
        match.Success.Should().BeTrue($"{relativePath} must name a DataProtectionScope");
        return match.Groups["value"].Value;
    }

    private static string ExtractPathPattern(string relativePath)
    {
        var source = ReadRepoFile(relativePath);
        var match = Regex.Match(source, @"PathFor\([^)]*\)\s*=>\s*Path\.Combine\([^,]+,\s*\$""(?<value>[^""]+)""\)");
        match.Success.Should().BeTrue($"{relativePath} must build the session path from a single interpolated literal");
        return match.Groups["value"].Value;
    }

    private static string ReadRepoFile(string relativePath)
    {
        var path = Path.Combine(FindRepoRoot(), relativePath);
        File.Exists(path).Should().BeTrue($"{path} must exist");
        return File.ReadAllText(path);
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
