using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace NodePilot.Api.Tests.Architecture;

/// <summary>
/// Enforces the dependency graph declared in CLAUDE.md ("Dep-Graph"). The set of
/// <c>ProjectReference</c>s per production project must equal the declared set exactly:
/// an added edge is a layering violation (for example Core depending on Data, or Engine
/// on Api), and a removed edge means the documentation (and this list) must be updated
/// in the same change.
/// </summary>
public sealed class DependencyDirectionTests
{
    /// <summary>
    /// The declared graph. Mirrors the "Dep-Graph" line in CLAUDE.md — keep the two in sync
    /// (that is the point: this test fails when they drift apart from the code).
    /// </summary>
    private static readonly Dictionary<string, string[]> DeclaredGraph = new(StringComparer.Ordinal)
    {
        ["NodePilot.Core"] = [],
        ["NodePilot.Data"] = ["NodePilot.Core"],
        ["NodePilot.Remote"] = ["NodePilot.Core"],
        ["NodePilot.Telemetry"] = ["NodePilot.Core"],
        ["NodePilot.Ai"] = ["NodePilot.Core"],
        ["NodePilot.Engine"] = ["NodePilot.Ai", "NodePilot.Data", "NodePilot.Remote", "NodePilot.Core", "NodePilot.Telemetry"],
        ["NodePilot.Scheduler"] = ["NodePilot.Engine", "NodePilot.Data", "NodePilot.Core"],
        ["NodePilot.Api"] = ["NodePilot.Ai", "NodePilot.Engine", "NodePilot.Scheduler", "NodePilot.Data", "NodePilot.Remote", "NodePilot.Core", "NodePilot.Telemetry"],
        ["NodePilot.Cli"] = ["NodePilot.Core"],
        ["NodePilot.Mcp"] = ["NodePilot.Core"],
        ["NodePilot.ServiceSwitcher"] = [],
    };

    private static readonly Regex ProjectReferencePattern = new(
        @"ProjectReference\s+Include=""[^""]*[\\/](NodePilot\.[A-Za-z]+)\.csproj""",
        RegexOptions.Compiled);

    [Fact]
    public void EveryProductionProject_ReferencesExactlyItsDeclaredDependencies()
    {
        var offenders = new List<string>();
        var seenProjects = new List<string>();

        foreach (var projectDir in ProductionSources.ProjectDirectories())
        {
            var name = Path.GetFileName(projectDir);
            var csproj = Path.Combine(projectDir, $"{name}.csproj");
            File.Exists(csproj).Should().BeTrue($"expected a project file at {csproj}");
            seenProjects.Add(name);

            if (!DeclaredGraph.TryGetValue(name, out var declared))
            {
                offenders.Add($"{name}: not in the declared graph — add it here AND to CLAUDE.md's Dep-Graph line");
                continue;
            }

            var actual = ProjectReferencePattern.Matches(File.ReadAllText(csproj))
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            var extra = actual.Except(declared, StringComparer.Ordinal).ToList();
            var missing = declared.Except(actual, StringComparer.Ordinal).ToList();
            if (extra.Count > 0)
                offenders.Add($"{name}: UNDECLARED reference(s) {string.Join(", ", extra)} — layering violation, or the graph doc is stale");
            if (missing.Count > 0)
                offenders.Add($"{name}: declared reference(s) {string.Join(", ", missing)} no longer exist — update this list and CLAUDE.md");
        }

        offenders.Should().BeEmpty(
            "the project reference graph is the architecture — an unexpected edge means a layer " +
            "is leaking (Core must stay a leaf, clients stay HTTP-only). Violations:\n" +
            string.Join("\n", offenders));

        // Scanner meta-check: a rename/move that empties the walk must fail loudly instead of
        // green-lighting an unscanned repo (same pattern as the other Architecture guards).
        seenProjects.Should().BeEquivalentTo(DeclaredGraph.Keys,
            "every declared project must exist on disk and every src/NodePilot.* project must be declared");
    }
}
