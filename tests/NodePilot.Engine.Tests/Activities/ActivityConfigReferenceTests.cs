using System.Text.RegularExpressions;
using FluentAssertions;
using NodePilot.Core.Activities;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

/// <summary>
/// Guards the curated activity config reference — the single source behind the MCP config tools and
/// the rendered AI-prompt catalog.
///
/// <para>The previous guard only checked that an activity HAD an entry, never that its keys were
/// right. Under that guard the reference accumulated keys the engine never reads
/// (<c>startWorkflow.workflowId</c> instead of <c>workflowNameOrId</c>,
/// <c>registryOperation.valueData</c> instead of <c>value</c>,
/// <c>waitForCondition.pollIntervalSeconds</c> instead of <c>intervalSeconds</c>, plus phantom keys
/// like <c>emailNotification.cc</c>). An agent authoring a node from those produces a node that
/// looks correct and silently does nothing, so the keys are verified against the executor sources
/// here.</para>
/// </summary>
public class ActivityConfigReferenceTests
{
    /// <summary>Keys read by shared infrastructure rather than the activity's own source file.</summary>
    private static readonly Dictionary<string, string[]> ReadElsewhere = new(StringComparer.Ordinal)
    {
        // WorkflowScheduler evaluates the junction mode, not JunctionActivity.
        ["junction"] = ["requiredCount"],
        // RestApiHttpClientProvider resolves the per-step proxy override.
        ["restApi"] = ["proxyMode"],
        // WebhooksController / WebhookHmacSecurity verify the request before the trigger runs.
        ["webhookTrigger"] = ["secret", "signatureMode", "fieldMappings"],
    };

    [Fact]
    public void EveryCoreActivityType_HasAnEntryWithAPurpose()
    {
        foreach (var activity in ActivityCatalog.All)
        {
            var entry = ActivityConfigReference.TryGet(activity.Type);
            entry.Should().NotBeNull($"'{activity.Type}' has no curated config reference");
            entry!.Description.Should().NotBeNullOrWhiteSpace($"'{activity.Type}' has no description");
        }
    }

    [Fact]
    public void EveryDocumentedKey_IsActuallyReadByItsExecutor()
    {
        var sources = LoadExecutorSources();
        var phantom = new List<string>();

        foreach (var activity in ActivityCatalog.All)
        {
            var entry = ActivityConfigReference.TryGet(activity.Type);
            if (entry is null || !sources.TryGetValue(activity.Type, out var source)) continue;

            var exempt = ReadElsewhere.TryGetValue(activity.Type, out var e) ? e : [];

            foreach (var key in entry.ConfigKeys)
            {
                if (exempt.Contains(key.Key, StringComparer.Ordinal)) continue;
                if (!source.Contains($"\"{key.Key}\"", StringComparison.Ordinal))
                    phantom.Add($"{activity.Type}.{key.Key}");
            }
        }

        phantom.Should().BeEmpty(
            "these documented config keys are never read by their executor, so a node authored from "
            + "them silently does nothing: {0}",
            string.Join(", ", phantom));
    }

    [Fact]
    public void DocumentedKeys_HaveATypeAndADescription()
    {
        foreach (var (type, entry) in ActivityConfigReference.ByType)
        {
            foreach (var key in entry.ConfigKeys)
            {
                key.Key.Should().NotBeNullOrWhiteSpace($"{type} has a config key without a name");
                key.Type.Should().NotBeNullOrWhiteSpace($"{type}.{key.Key} has no type");
                key.Description.Should().NotBeNullOrWhiteSpace($"{type}.{key.Key} has no description");
            }
        }
    }

    [Fact]
    public void SchemaVersion_IsCurrent() => ActivityConfigReference.SchemaVersion.Should().Be(2);

    /// <summary>Maps activity/trigger type → the source text of the class that implements it.</summary>
    private static Dictionary<string, string> LoadExecutorSources()
    {
        var root = FindRepoRoot();
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var dir in new[] { "Activities", "Triggers" })
        {
            var path = Path.Combine(root, "src", "NodePilot.Engine", dir);
            if (!Directory.Exists(path)) continue;

            foreach (var file in Directory.EnumerateFiles(path, "*.cs"))
            {
                var text = File.ReadAllText(file);
                var match = Regex.Match(text, @"(?:ActivityType|TriggerType)\s*=>\s*""([^""]+)""");
                if (match.Success) sources[match.Groups[1].Value] = text;
            }
        }

        sources.Should().NotBeEmpty("the executor sources must be discoverable for this guard to mean anything");
        return sources;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NodePilot.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException($"Could not locate NodePilot.slnx from {AppContext.BaseDirectory}");
    }
}
