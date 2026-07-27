using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace NodePilot.Mcp.Tests;

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
    public void DpapiEntropy_IsIdenticalInBothStores()
    {
        var cli = ExtractEntropy(CliTokenStore);
        var mcp = ExtractEntropy(McpTokenStore);

        mcp.Should().Be(cli,
            "die DPAPI-Entropie ist Teil des Schlüssels — weicht sie ab, kann der MCP-Server " +
            "die von `np auth login` geschriebene Session nicht mehr entschlüsseln und meldet " +
            "irreführend 'nicht angemeldet'");
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

    private static string ExtractEntropy(string relativePath)
    {
        var source = ReadRepoFile(relativePath);
        var match = Regex.Match(source, @"Entropy\s*=\s*Encoding\.UTF8\.GetBytes\(""(?<value>[^""]+)""\)");
        match.Success.Should().BeTrue($"{relativePath} must declare the DPAPI entropy as a UTF-8 literal");
        return match.Groups["value"].Value;
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
