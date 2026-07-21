using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using Xunit;

namespace NodePilot.Engine.Tests.Notifications;

/// <summary>
/// Keeps the frontend alerting catalog (src/nodepilot-ui/src/lib/eventFields.ts) + i18n in lock-step
/// with the backend: event-field names must match <see cref="NotificationContext.ToFieldMap"/>, the
/// event-type list must mirror the <see cref="NotificationEventType"/> enum, and the DE/EN i18n key
/// sets must be complete + identical. A drift here breaks the rule editor silently, so fail CI instead.
/// </summary>
public class AlertingCatalogFrontendSyncTests
{
    [Fact]
    public void FrontendEventFieldCatalog_MatchesBackendToFieldMap()
    {
        var ts = ReadEventFieldsTs();
        var frontendNames = ExtractCatalogFieldNames(ts);
        var backendKeys = SampleContext().ToFieldMap().Keys;

        frontendNames.Should().BeEquivalentTo(backendKeys,
            "eventFields.ts EVENT_FIELD_CATALOG names must match NotificationContext.ToFieldMap() keys");
    }

    [Fact]
    public void FrontendEventTypes_MirrorCoreEnum()
    {
        var ts = ReadEventFieldsTs();
        var frontendTypes = ExtractArrayLiterals(ts, "NOTIFICATION_EVENT_TYPES");
        var expected = Enum.GetNames<NotificationEventType>();

        frontendTypes.Should().BeEquivalentTo(expected,
            "NOTIFICATION_EVENT_TYPES must mirror the enum");
    }

    [Fact]
    public void I18n_De_En_KeySetsMatch_AndCoverCatalog()
    {
        var ts = ReadEventFieldsTs();
        var fieldNames = ExtractCatalogFieldNames(ts);
        var eventTypes = ExtractArrayLiterals(ts, "NOTIFICATION_EVENT_TYPES");

        foreach (var lang in new[] { "de", "en" })
        {
            var json = ReadAlertsJson(lang);
            var labels = json.GetProperty("eventTypeLabels").EnumerateObject().Select(p => p.Name).ToHashSet();
            var fields = json.GetProperty("eventFields").EnumerateObject().Select(p => p.Name).ToHashSet();

            labels.Should().Contain(eventTypes, $"{lang}/alerts.json eventTypeLabels must cover every event type");
            fields.Should().Contain(fieldNames, $"{lang}/alerts.json eventFields must cover every catalog field");
        }

        var de = ReadAlertsJson("de");
        var en = ReadAlertsJson("en");
        KeySet(de, "eventTypeLabels").Should().BeEquivalentTo(KeySet(en, "eventTypeLabels"), "DE/EN eventTypeLabels keys must match");
        KeySet(de, "eventFields").Should().BeEquivalentTo(KeySet(en, "eventFields"), "DE/EN eventFields keys must match");
    }

    // ---- helpers ------------------------------------------------------------

    private static NotificationContext SampleContext() => new(
        NotificationEventType.ExecutionFailed, NotificationSeverity.Info, "", null, null, null, null, null,
        null, null, null, DateTime.UtcNow, null, 0, false, null, null, null, null, null);

    private static HashSet<string> KeySet(JsonElement json, string prop)
        => json.GetProperty(prop).EnumerateObject().Select(p => p.Name).ToHashSet();

    private static string ReadEventFieldsTs()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "nodepilot-ui", "src", "lib", "eventFields.ts");
        File.Exists(path).Should().BeTrue($"eventFields.ts must exist at {path}");
        return File.ReadAllText(path);
    }

    private static JsonElement ReadAlertsJson(string lang)
    {
        var path = Path.Combine(FindRepoRoot(), "src", "nodepilot-ui", "src", "i18n", "locales", lang, "alerts.json");
        File.Exists(path).Should().BeTrue($"{lang}/alerts.json must exist at {path}");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    private static List<string> ExtractCatalogFieldNames(string ts)
    {
        var block = Regex.Match(ts, @"EVENT_FIELD_CATALOG[^\[]*\[(?<body>[\s\S]*?)\];");
        block.Success.Should().BeTrue("eventFields.ts must export EVENT_FIELD_CATALOG as an array literal");
        return Regex.Matches(block.Groups["body"].Value, @"name:\s*'([^']+)'").Select(m => m.Groups[1].Value).ToList();
    }

    private static List<string> ExtractArrayLiterals(string ts, string name)
    {
        var block = Regex.Match(ts, name + @"\s*=\s*\[(?<body>[\s\S]*?)\]");
        block.Success.Should().BeTrue($"eventFields.ts must export {name} as an array literal");
        return Regex.Matches(block.Groups["body"].Value, @"'([^']+)'").Select(m => m.Groups[1].Value).ToList();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "NodePilot.slnx")))
                return dir.FullName;
        throw new InvalidOperationException($"Could not locate NodePilot.slnx walking up from {AppContext.BaseDirectory}");
    }
}
