using System.Text.RegularExpressions;
using FluentAssertions;
using NodePilot.Core.Activities;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

public class ActivityCatalogTests
{
    [Fact]
    public void ActivityTypes_AreUnique()
    {
        ActivityCatalog.All.Select(a => a.Type).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EngineExecutorActivityTypeLiterals_MatchCatalog()
    {
        var repoRoot = FindRepoRoot();
        var engineTypes = ExtractEngineActivityTypes(repoRoot);
        engineTypes.Should().NotBeEmpty();

        var catalogTypes = ActivityCatalog.All.Select(a => a.Type).ToHashSet(StringComparer.Ordinal);
        engineTypes.Should().BeEquivalentTo(
            catalogTypes,
            "every executable activity/trigger must have exactly one descriptor in ActivityCatalog");
    }

    [Fact]
    public void ExternalTriggers_AreSubsetOfTriggerTypes()
    {
        ActivityCatalog.ExternalTriggerTypes.Should().BeSubsetOf(ActivityCatalog.TriggerTypes);
        ActivityCatalog.ExternalTriggerTypes.Should().NotContain("manualTrigger");
    }

    private static HashSet<string> ExtractEngineActivityTypes(string repoRoot)
    {
        var engineRoot = Path.Combine(repoRoot, "src", "NodePilot.Engine");
        var scanDirs = new[]
        {
            Path.Combine(engineRoot, "Activities"),
            Path.Combine(engineRoot, "Triggers"),
        };
        foreach (var dir in scanDirs)
            Directory.Exists(dir).Should().BeTrue($"{dir} must exist");

        var pattern = new Regex(
            @"public\s+(?:override\s+)?string\s+ActivityType\s*=>\s*""(?<type>[A-Za-z][A-Za-z0-9]*)""\s*;",
            RegexOptions.Compiled);

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in scanDirs)
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var content = File.ReadAllText(file);
                foreach (Match match in pattern.Matches(content))
                    found.Add(match.Groups["type"].Value);
            }
        }
        return found;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NodePilot.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException(
            $"Could not locate NodePilot.slnx walking up from {AppContext.BaseDirectory}");
    }
}
