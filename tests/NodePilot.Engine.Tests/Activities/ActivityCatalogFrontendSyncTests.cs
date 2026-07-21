using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using NodePilot.Core.Activities;
using NodePilot.Core.Constants;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

public class ActivityCatalogFrontendSyncTests
{
    [Fact]
    public void FrontendGeneratedCatalog_MatchesBackendActivityCatalog()
    {
        var repoRoot = FindRepoRoot();
        var frontend = ReadFrontendCatalog(repoRoot);
        var backend = ActivityCatalog.All.Select(ComparableActivity.FromBackend).ToList();

        frontend.Select(ComparableActivity.FromFrontend).Should().BeEquivalentTo(
            backend,
            opts => opts.WithStrictOrdering(),
            "src/nodepilot-ui/src/lib/activityCatalog.generated.ts is derived from NodePilot.Core.Activities.ActivityCatalog");
    }

    [Fact]
    public void TriggerActivityTypes_AreDerivedFromCatalog()
    {
        TriggerActivityTypes.All.Should().BeEquivalentTo(
            ActivityCatalog.TriggerTypes,
            "TriggerActivityTypes is a compatibility facade over the backend activity catalog");
    }

    private static List<FrontendActivity> ReadFrontendCatalog(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "src", "nodepilot-ui", "src", "lib", "activityCatalog.generated.ts");
        File.Exists(path).Should().BeTrue($"activityCatalog.generated.ts must exist at {path}");

        var content = File.ReadAllText(path);
        var match = Regex.Match(
            content,
            @"export\s+const\s+ACTIVITY_CATALOG\s*=\s*(?<json>\[[\s\S]*?\])\s+as\s+const;",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue("activityCatalog.generated.ts must export ACTIVITY_CATALOG as a JSON-compatible literal");

        var catalog = JsonSerializer.Deserialize<List<FrontendActivity>>(
            match.Groups["json"].Value,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        catalog.Should().NotBeNull();
        return catalog!;
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

    private sealed record FrontendActivity(
        string Type,
        string Category,
        string LabelKey,
        string Icon,
        bool IsRemote,
        bool IsExternalTrigger,
        string Timeout,
        IReadOnlyList<FrontendOutputParameter> OutputParameters,
        IReadOnlyList<string> TelemetryParameters,
        FrontendPrompt Prompt);

    private sealed record FrontendOutputParameter(string Name, string Type);

    private sealed record FrontendPrompt(bool Included, string? ExclusionReason);

    private sealed record ComparableActivity(
        string Type,
        string Category,
        string LabelKey,
        string Icon,
        bool IsRemote,
        bool IsExternalTrigger,
        string Timeout,
        IReadOnlyList<FrontendOutputParameter> OutputParameters,
        IReadOnlyList<string> TelemetryParameters,
        FrontendPrompt Prompt)
    {
        public static ComparableActivity FromBackend(ActivityDescriptor descriptor) =>
            new(
                descriptor.Type,
                ToFrontendCategory(descriptor.Category),
                descriptor.LabelKey,
                descriptor.Icon,
                descriptor.IsRemote,
                descriptor.IsExternalTrigger,
                ToFrontendTimeout(descriptor.Timeout),
                descriptor.OutputParameters.Select(p => new FrontendOutputParameter(p.Name, p.Type)).ToArray(),
                descriptor.TelemetryParameters.ToArray(),
                new FrontendPrompt(descriptor.Prompt.IsIncluded, descriptor.Prompt.ExclusionReason));

        public static ComparableActivity FromFrontend(FrontendActivity descriptor) =>
            new(
                descriptor.Type,
                descriptor.Category,
                descriptor.LabelKey,
                descriptor.Icon,
                descriptor.IsRemote,
                descriptor.IsExternalTrigger,
                descriptor.Timeout,
                descriptor.OutputParameters,
                descriptor.TelemetryParameters,
                descriptor.Prompt);
    }

    private static string ToFrontendCategory(ActivityCategory category) => category switch
    {
        ActivityCategory.Trigger => "trigger",
        ActivityCategory.Action => "action",
        ActivityCategory.ControlFlow => "controlFlow",
        ActivityCategory.Logic => "logic",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    private static string ToFrontendTimeout(ActivityTimeoutKind timeout) => timeout switch
    {
        ActivityTimeoutKind.None => "none",
        ActivityTimeoutKind.Always => "always",
        ActivityTimeoutKind.WhenWaitForExit => "whenWaitForExit",
        ActivityTimeoutKind.WhenWaitForCompletion => "whenWaitForCompletion",
        _ => throw new ArgumentOutOfRangeException(nameof(timeout), timeout, null),
    };
}
