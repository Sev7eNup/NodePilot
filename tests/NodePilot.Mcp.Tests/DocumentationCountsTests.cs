using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace NodePilot.Mcp.Tests;

/// <summary>
/// Keeps the count-bearing claims in the docs honest — the "N MCP tools" / "N activity types"
/// figures that the audit found stale (README once said 26 activity types; a stale 81/90 tool
/// count was chased). Same spirit as the catalog frontend-sync guards: derive the number from
/// the code and fail CI when a doc drifts. If a phrasing changes, update the matching pattern.
/// </summary>
public class DocumentationCountsTests
{
    // Code-derived ground truth, counted from source (no assembly coupling).
    private static int McpToolTotal() => CountMatches(McpToolsGlob(), @"\[McpServerTool\(");
    private static int McpDestructiveTools() =>
        CountMatches(new[] { RepoPath("src", "NodePilot.Mcp", "Tools", "DestructiveTools.cs") }, @"\[McpServerTool\(");
    private static int ActivityTypes() =>
        CountMatches(new[] { RepoPath("src", "NodePilot.Core", "Activities", "ActivityCatalog.cs") },
            @"(?:Action|Logic|ControlFlow)\(""");
    // Colour skins offered by the theme switcher, counted from the THEMES registry. `system`
    // is not a skin — it resolves to one — so the doc phrasing says "N Skins + system".
    private static int Skins() =>
        CountMatches(new[] { RepoPath("src", "nodepilot-ui", "src", "stores", "themeStore.ts") },
            @"id: '[a-z-]+'");

    public static IEnumerable<object[]> DocClaims()
    {
        var toolTotal = McpToolTotal();
        var destructive = McpDestructiveTools();
        var defaultTools = toolTotal - destructive;
        var activities = ActivityTypes();
        var skins = Skins();

        // (relative doc path, regex with one capturing group, expected value, what it is)
        yield return Row("CLAUDE.md", @"über (\d+) Tools", toolTotal, "MCP tools (CLAUDE.md overview)");
        // Trailing [,)] rather than a hard \): the parenthetical carries a transport note in
        // some phrasings ("(99 Tools, 3 Resources, stdio)"). Stay anchored to the group without
        // pinning the guard to whatever else is listed inside it.
        yield return Row("CLAUDE.md", @"\((\d+) Tools, \d+ Resources[,)]", toolTotal, "MCP tools (CLAUDE.md MCP section)");
        yield return Row("README.md", @"— (\d+) tools over", toolTotal, "MCP tools (README)");
        yield return Row("README.md", @"with (\d+) activity types", activities, "activity types (README highlights)");
        yield return Row("README.md", @"Beyond the (\d+) executable Activity", activities, "activity types (README annotation nodes)");
        yield return Row("docs/mcp-server.md", @"(\d+) default tools", defaultTools, "default MCP tools (docs)");
        yield return Row("docs/mcp-server.md", @"(\d+) gated destructive tools", destructive, "destructive MCP tools (docs)");
        yield return Row("docs/mcp-server.md", @"\((\d+) total\)", toolTotal, "total MCP tools (docs)");
        yield return Row("src/nodepilot-docs-ui/content/mcp-server.md", @"(\d+) Tools über", toolTotal, "MCP tools (doc site)");
        // The doc site carried its own stale counts that no guard covered: "26+ Activity-Typen"
        // (the very number this test was written to retire) and "8 Skins" against a 7-entry
        // registry. Both are now derived from code like every row above.
        yield return Row("src/nodepilot-docs-ui/content/concepts/workflows.md",
            @"aller (\d+) Activity-Typen", activities, "activity types (doc site)");
        yield return Row("src/nodepilot-docs-ui/content/designer/overview.md",
            @"Popover mit (\d+) Skins", skins, "colour skins (doc site)");
    }

    [Theory]
    [MemberData(nameof(DocClaims))]
    public void DocumentedCount_MatchesCode(DocClaim claim)
    {
        var path = RepoPath(claim.RelativePath.Split('/'));
        File.Exists(path).Should().BeTrue($"{claim.RelativePath} must exist");
        var text = File.ReadAllText(path);

        var m = Regex.Match(text, claim.Pattern);
        m.Success.Should().BeTrue(
            $"{claim.RelativePath} must still contain the '{claim.What}' claim matched by /{claim.Pattern}/ " +
            "— if the phrasing changed, update this guard's pattern.");

        int.Parse(m.Groups[1].Value).Should().Be(claim.Expected,
            $"the documented '{claim.What}' in {claim.RelativePath} must match the code count ({claim.Expected}).");
    }

    private static object[] Row(string relPath, string pattern, int expected, string what)
        => new object[] { new DocClaim(relPath, pattern, expected, what) };

    private static string[] McpToolsGlob()
        => Directory.EnumerateFiles(RepoPath("src", "NodePilot.Mcp", "Tools"), "*.cs").ToArray();

    private static int CountMatches(IEnumerable<string> files, string pattern)
    {
        var rx = new Regex(pattern);
        return files.Sum(f => rx.Matches(File.ReadAllText(f)).Count);
    }

    private static string RepoPath(params string[] parts)
        => Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray());

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "NodePilot.slnx")))
                return dir.FullName;
        throw new InvalidOperationException($"Could not locate NodePilot.slnx walking up from {AppContext.BaseDirectory}");
    }

    public sealed record DocClaim(string RelativePath, string Pattern, int Expected, string What)
    {
        // Shown in the test explorer per theory case.
        public override string ToString() => $"{RelativePath}: {What}";
    }
}
