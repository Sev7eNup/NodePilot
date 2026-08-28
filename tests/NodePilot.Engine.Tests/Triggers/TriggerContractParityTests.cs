using System.Text.RegularExpressions;
using FluentAssertions;
using NodePilot.Core.Activities;
using NodePilot.Engine.Tests.Activities;
using Xunit;

namespace NodePilot.Engine.Tests.Triggers;

/// <summary>
/// Locates the source files that together form a trigger's runtime contract: the shared settings in
/// <c>NodePilot.Core/Triggers</c>, the background sources in <c>NodePilot.Scheduler/Sources</c> and
/// the node executors in <c>NodePilot.Engine/Triggers</c>.
/// </summary>
internal static class TriggerContractSources
{
    /// <summary>Concatenated text of the shared trigger settings — the parsing half of the
    /// contract.</summary>
    internal static string LoadSharedContractText()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (_, text) in Read(Path.Combine("src", "NodePilot.Core", "Triggers")))
            sb.AppendLine(text);
        return sb.ToString();
    }

    /// <summary>Every runtime file that participates, tagged with the trigger type it
    /// serves.</summary>
    internal static IEnumerable<(string Type, string File, string Text)> RuntimeSources()
    {
        var dirs = new[]
        {
            Path.Combine("src", "NodePilot.Core", "Triggers"),
            Path.Combine("src", "NodePilot.Scheduler", "Sources"),
            Path.Combine("src", "NodePilot.Engine", "Triggers"),
        };

        foreach (var dir in dirs)
        {
            foreach (var (file, text) in Read(dir))
            {
                var type = TriggerTypeFromFileName(file);
                if (type is not null && ActivityConfigReference.TryGet(type) is not null)
                    yield return (type, file, text);
            }
        }
    }

    /// <summary>
    /// "EventLogTriggerSettings.cs" / "EventLogTriggerSource.cs" / "EventLogTrigger.cs" all serve
    /// the "eventLogTrigger" node type. Files that do not reduce to a known type
    /// (TriggerFireObserver,
    /// ITriggerSource, …) are dropped by the caller's catalog check.
    /// </summary>
    private static string? TriggerTypeFromFileName(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        foreach (var suffix in new[] { "Settings", "Source" })
            if (name.EndsWith(suffix, StringComparison.Ordinal))
                name = name[..^suffix.Length];

        if (!name.EndsWith("Trigger", StringComparison.Ordinal)) return null;
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static IEnumerable<(string File, string Text)> Read(string relativeDir)
    {
        var path = Path.Combine(ActivityConfigReferenceTests.FindRepoRoot(), relativeDir);
        if (!Directory.Exists(path)) yield break;
        foreach (var file in Directory.EnumerateFiles(path, "*.cs"))
            yield return (file, File.ReadAllText(file));
    }
}

/// <summary>
/// Holds the two runtime halves of every background trigger to one config vocabulary.
///
/// <para>A trigger node is read by the node executor in <c>NodePilot.Engine/Triggers</c> for manual
/// sample runs and by the live source in <c>NodePilot.Scheduler/Sources</c>. Both runtimes must
/// read
/// the same documented keys so filters and polling intervals behave consistently.</para>
///
/// <para>These tests reject undocumented runtime keys and config parsing outside the shared
/// settings type.</para>
/// </summary>
public class TriggerContractParityTests
{
    /// <summary>
    /// Config-key reads. Covers the raw <c>JsonElement.TryGetProperty</c> form used by the sources
    /// and the <c>ReadString/ReadInt32/ReadInt64(config, "key")</c> helpers used by the shared
    /// settings types.
    /// </summary>
    private static readonly Regex KeyRead = new(
        @"(?:TryGetProperty|ReadString|ReadInt32|ReadInt64)\s*\(\s*(?:[A-Za-z_][A-Za-z0-9_]*\s*,\s*)?""([^""]+)""",
        RegexOptions.Compiled);

    /// <summary>
    /// Reads that address a nested object rather than the node config itself, so the reference has
    /// nothing to say about them.
    /// </summary>
    private static readonly Dictionary<string, string[]> NestedKeys = new(StringComparer.Ordinal)
    {
        // Fields of one entry inside manualTrigger's `parameters` array.
        ["manualTrigger"] = ["name", "default", "required", "type"],
    };

    [Fact]
    public void NoTriggerRuntime_ReadsAnUndocumentedConfigKey()
    {
        var undocumented = new List<string>();

        foreach (var (type, file, text) in TriggerContractSources.RuntimeSources())
        {
            var documented = ActivityConfigReference.TryGet(type)!.ConfigKeys
                .Select(k => k.Key)
                .ToHashSet(StringComparer.Ordinal);
            var nested = NestedKeys.TryGetValue(type, out var n) ? n : [];

            foreach (Match match in KeyRead.Matches(text))
            {
                var key = match.Groups[1].Value;
                if (documented.Contains(key) || nested.Contains(key, StringComparer.Ordinal)) continue;
                undocumented.Add($"{type}.{key} ({Path.GetFileName(file)})");
            }
        }

        undocumented.Should().BeEmpty(
            "a trigger runtime reads these config keys but no documentation mentions them, so the "
            + "designer, the AI catalog and the MCP config tools cannot know they exist — and the "
            + "other runtime almost certainly ignores them: {0}",
            string.Join(", ", undocumented.Distinct()));
    }

    [Theory]
    [InlineData("eventLogTrigger", "EventLogTriggerSettings")]
    [InlineData("databaseTrigger", "DatabaseTriggerSettings")]
    public void BothRuntimes_ParseThroughTheSharedSettingsType(string type, string settingsType)
    {
        var parsers = TriggerContractSources.RuntimeSources()
            .Where(s => s.Type == type && !s.File.Contains("NodePilot.Core", StringComparison.Ordinal))
            .ToList();

        parsers.Should().HaveCount(2, "{0} has exactly two runtimes: the node executor and the background source", type);

        foreach (var (_, file, text) in parsers)
        {
            text.Should().Contain($"{settingsType}.Parse",
                "{0} must parse its config through the shared {1} — hand-rolled parsing is how the "
                + "two runtimes drifted apart in the first place", Path.GetFileName(file), settingsType);
        }
    }
}
