using System.Text.RegularExpressions;
using FluentAssertions;
using NodePilot.Core.Activities;
using NodePilot.Engine.Tests.Triggers;
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

    /// <summary>
    /// Enum values the engine never spells out as a literal because it hands them to a generic
    /// parser. Listing them here is the same trade-off <see cref="ReadElsewhere"/> makes for keys:
    /// the guard stays meaningful for the hand-written switches, which is where drift actually
    /// happened.
    /// </summary>
    private static readonly Dictionary<string, string[]> ValuesNotLiteralInSource = new(StringComparer.Ordinal)
    {
        // Passed straight to new HttpMethod(...) — no per-verb literal exists.
        ["restApi.method"] = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"],
        // Verified by WebhooksController / WebhookHmacSecurity in NodePilot.Api.
        ["webhookTrigger.signatureMode"] = ["header", "nodepilot-hmac-v2"],
        // Parsed via Enum.TryParse<EventLogEntryType>.
        ["eventLogTrigger.entryType"] = ["Information", "Warning", "Error"],
    };

    /// <summary>
    /// The reference documents enum values as "a | b | c" prose. Nothing verified those against the
    /// engine, and three drifted: <c>scheduledTask.action</c> listed run/query/delete/create when the
    /// executor accepts get/start/stop/enable/disable/register/unregister, its <c>triggerType</c>
    /// said onLogon/onStartup instead of atLogon/atStartup, and xml/jsonQuery <c>resultMode</c> said
    /// "list" where the executors only honour "all". Each one renders into the AI prompt catalog and
    /// the MCP config tools, so an agent authoring from them produces a node that throws "unknown
    /// action" or silently falls back to the default.
    /// </summary>
    [Fact]
    public void DocumentedEnumValues_ExistInTheEngineSources()
    {
        var engineText = LoadAllEngineSources();
        var unknown = new List<string>();

        foreach (var (type, entry) in ActivityConfigReference.ByType)
        {
            foreach (var key in entry.ConfigKeys)
            {
                var exempt = ValuesNotLiteralInSource.TryGetValue($"{type}.{key.Key}", out var e) ? e : [];

                foreach (var value in ExtractEnumValues(key.Description))
                {
                    if (exempt.Contains(value, StringComparer.OrdinalIgnoreCase)) continue;
                    if (!engineText.Contains($"\"{value}\"", StringComparison.OrdinalIgnoreCase))
                        unknown.Add($"{type}.{key.Key}={value}");
                }
            }
        }

        unknown.Should().BeEmpty(
            "these documented enum values appear nowhere in the engine sources, so a node authored "
            + "from them is rejected or silently falls back to the default: {0}",
            string.Join(", ", unknown));
    }

    /// <summary>
    /// Pulls the "a | b | c" alternatives out of a key description. The first alternative is
    /// preceded by prose ("Required for setStartType: Automatic | …"), so it contributes its LAST
    /// word; every later alternative is followed by prose ("file — where the XML comes from"), so it
    /// contributes its FIRST word. Anything that is not identifier-shaped is dropped, which is how
    /// the trailing "…" in an open-ended list stays out.
    /// </summary>
    internal static IEnumerable<string> ExtractEnumValues(string description)
    {
        if (string.IsNullOrWhiteSpace(description) || !description.Contains('|')) yield break;

        // Everything after the first sentence is explanatory prose, not part of the enum.
        var parts = description.Split('|');
        for (var i = 0; i < parts.Length; i++)
        {
            // "(default)" / "(register)" annotate an alternative without being one.
            var cleaned = Regex.Replace(parts[i], @"\([^)]*\)", " ");
            var words = Regex.Matches(cleaned, @"[A-Za-z][A-Za-z0-9._-]*")
                             .Select(m => m.Value.TrimEnd('.'))
                             .Where(w => w.Length > 0)
                             .ToList();
            if (words.Count == 0) continue;

            var candidate = i == 0 ? words[^1] : words[0];
            if (Regex.IsMatch(candidate, @"^[A-Za-z][A-Za-z0-9._-]*$")) yield return candidate;
        }
    }

    /// <summary>
    /// Engine + Scheduler + the shared trigger contract in Core. An enum value is often honoured
    /// outside the activity class: runScript's "pwsh" lives in PowerShellEngineFactory, junction's
    /// modes in the scheduler, and the background triggers are evaluated by their
    /// NodePilot.Scheduler source against settings parsed in NodePilot.Core.Triggers.
    /// </summary>
    private static string LoadAllEngineSources()
    {
        var root = FindRepoRoot();
        var sb = new System.Text.StringBuilder();

        foreach (var project in new[] { "NodePilot.Engine", "NodePilot.Scheduler" })
        {
            var path = Path.Combine(root, "src", project);
            Directory.Exists(path).Should().BeTrue($"{project} sources must be discoverable");
            foreach (var file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
                sb.AppendLine(File.ReadAllText(file));
        }

        sb.AppendLine(TriggerContractSources.LoadSharedContractText());
        return sb.ToString();
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

    /// <summary>
    /// Maps activity/trigger type → the source text that must read its keys. For the background
    /// triggers that is no longer the node executor alone: parsing moved into the shared
    /// NodePilot.Core.Triggers settings, so the shared contract text is appended for every trigger
    /// type. Without that this guard would flag every trigger key as phantom.
    /// </summary>
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

        var shared = TriggerContractSources.LoadSharedContractText();
        foreach (var type in sources.Keys.Where(k => k.EndsWith("Trigger", StringComparison.Ordinal)).ToList())
            sources[type] += shared;

        sources.Should().NotBeEmpty("the executor sources must be discoverable for this guard to mean anything");
        return sources;
    }

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NodePilot.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException($"Could not locate NodePilot.slnx from {AppContext.BaseDirectory}");
    }
}
