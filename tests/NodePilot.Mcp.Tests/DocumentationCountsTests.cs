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

    // Frontend suite sizes, counted the way the runners discover them: vitest.config.ts includes
    // `src/**/*.test.{ts,tsx}`, and Playwright's testDir is `./e2e` with the default testMatch.
    // The figures justify the scoped-testing rule in CLAUDE.md and had drifted twice before.
    private static int VitestFiles() =>
        CountFiles(RepoPath("src", "nodepilot-ui", "src"), f => f.EndsWith(".test.ts") || f.EndsWith(".test.tsx"));
    private static int E2eSpecs() =>
        CountFiles(RepoPath("src", "nodepilot-ui", "e2e"), f =>
            f.EndsWith(".spec.ts") || f.EndsWith(".spec.tsx")
            || f.EndsWith(".test.ts") || f.EndsWith(".test.tsx"));

    public static IEnumerable<object[]> DocClaims()
    {
        var toolTotal = McpToolTotal();
        var destructive = McpDestructiveTools();
        var defaultTools = toolTotal - destructive;
        var activities = ActivityTypes();
        var skins = Skins();
        var vitestFiles = VitestFiles();
        var e2eSpecs = E2eSpecs();

        // (relative doc path, regex with one capturing group, expected value, what it is)
        yield return Row("CLAUDE.md", @"über (\d+) Tools", toolTotal, "MCP tools (CLAUDE.md overview)");
        // Trailing [,)] rather than a hard \): the parenthetical carries a transport note in
        // some phrasings ("(99 Tools, 3 Resources, stdio)"). Stay anchored to the group without
        // pinning the guard to whatever else is listed inside it.
        yield return Row("CLAUDE.md", @"\((\d+) Tools, \d+ Resources[,)]", toolTotal, "MCP tools (CLAUDE.md MCP section)");
        // Same phrasing, same guard: this file sat at 100 while the root guide said 101 and the code
        // said 102, because nothing read it.
        yield return Row("src/NodePilot.Mcp/CLAUDE.md",
            @"\((\d+) Tools, \d+ Resources[,)]", toolTotal, "MCP tools (Mcp project guide)");
        yield return Row("README.md", @"— (\d+) tools over", toolTotal, "MCP tools (README)");
        yield return Row("README.md", @"with (\d+) activity types", activities, "activity types (README highlights)");
        // The highlights bullet was the only guarded one of the README's three activity counts,
        // and the other two drifted to 29 in #402 while it stayed at 27. Both are pinned now.
        yield return Row("README.md", @"\[All (\d+) activities\]", activities, "activity types (README reference table)");
        yield return Row("README.md", @"WorkflowEngine, (\d+) activities", activities, "activity types (README solution tree)");
        // The scoped-testing rule in CLAUDE.md is argued from these two figures. They drifted in
        // #292 and again in #353/#354 because nothing derived them; now something does. The third
        // number in that sentence — the backend test-case count — needs a real test run and stays
        // a hand-measured snapshot.
        yield return Row("CLAUDE.md", @"(\d+) Vitest-Dateien", vitestFiles, "Vitest files (CLAUDE.md test scope)");
        yield return Row("CLAUDE.md", @"(\d+) E2E-Specs", e2eSpecs, "E2E specs (CLAUDE.md test scope)");
        // The README's "Beyond the N executable Activity types…" annotation-node claim was retired
        // when the README stopped duplicating the documentation site. The count is still guarded
        // twice — in the highlights row above, and in both language versions of the doc site's
        // concepts/workflows page — so no coverage was lost by dropping the row rather than
        // re-inserting a sentence the README no longer needs.
        yield return Row("docs/mcp-server.md", @"(\d+) default tools", defaultTools, "default MCP tools (docs)");
        yield return Row("docs/mcp-server.md", @"(\d+) gated destructive tools", destructive, "destructive MCP tools (docs)");
        yield return Row("docs/mcp-server.md", @"\((\d+) total\)", toolTotal, "total MCP tools (docs)");
        // The doc site carried its own stale counts that no guard covered: "26+ Activity-Typen"
        // (the very number this test was written to retire) and "8 Skins" against a 7-entry
        // registry. Both are now derived from code like every row above.
        //
        // The site ships bilingually (content/de + content/en), so every claim is guarded in
        // BOTH languages: a count corrected in one translation and forgotten in the other is
        // precisely the drift a bilingual corpus adds, and it is invisible to a reader who only
        // ever opens one language.
        yield return Row("src/nodepilot-docs-ui/content/de/mcp-server.md",
            @"(\d+) Tools über", toolTotal, "MCP tools (doc site, de)");
        yield return Row("src/nodepilot-docs-ui/content/en/mcp-server.md",
            @"(\d+) tools\s+across", toolTotal, "MCP tools (doc site, en)");
        yield return Row("src/nodepilot-docs-ui/content/de/concepts/workflows.md",
            @"aller (\d+) Activity-Typen", activities, "activity types (doc site, de)");
        yield return Row("src/nodepilot-docs-ui/content/en/concepts/workflows.md",
            @"all (\d+) activity types", activities, "activity types (doc site, en)");
        // The project website names the same figure on its product page. It is the only count the
        // site carries, and it sits in the two dictionaries rather than in markdown.
        yield return Row("src/nodepilot-docs-ui/src/site/i18n/de.ts",
            @"(\d+) Aktivitätstypen", activities, "activity types (website, de)");
        yield return Row("src/nodepilot-docs-ui/src/site/i18n/en.ts",
            @"(\d+) activity types", activities, "activity types (website, en)");
        yield return Row("src/nodepilot-docs-ui/content/de/designer/overview.md",
            @"Popover mit (\d+) Skins", skins, "colour skins (doc site, de)");
        yield return Row("src/nodepilot-docs-ui/content/en/designer/overview.md",
            @"popover with (\d+) skins", skins, "colour skins (doc site, en)");
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

    // The root guide is an index: behaviour rules, lookup tables and invariants, with the depth
    // in docs/. It had grown past 70 KB because feature PRs appended paragraphs and removed none,
    // so it is capped here. Raising this number is a decision, not a fix.
    [Fact]
    public void ClaudeMd_StaysAnIndex()
    {
        var path = Path.Combine(FindRepoRoot(), "CLAUDE.md");
        new FileInfo(path).Length.Should().BeLessThan(60_000,
            "CLAUDE.md is the index, not the documentation - move depth into docs/ instead.");
    }

    private static object[] Row(string relPath, string pattern, int expected, string what)
        => new object[] { new DocClaim(relPath, pattern, expected, what) };

    private static string[] McpToolsGlob()
        => Directory.EnumerateFiles(RepoPath("src", "NodePilot.Mcp", "Tools"), "*.cs").ToArray();

    private static int CountFiles(string root, Func<string, bool> matches)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count(matches);

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
