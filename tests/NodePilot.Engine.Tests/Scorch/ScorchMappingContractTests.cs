using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using NodePilot.Core.Activities;
using NodePilot.Engine.Scorch;
using Xunit;

namespace NodePilot.Engine.Tests.Scorch;

/// <summary>
/// Mechanical guard over everything the SCOrch mapper emits.
///
/// <para>Three separate importer bugs shared one shape: a config key that no executor reads.
/// <c>emailNotification</c> got <c>from</c>/<c>smtpServer</c>/<c>smtpPort</c>/<c>smtpUseSsl</c>, so
/// imported mail silently used a different relay than the runbook named. Checking every emitted key
/// against the shipped schema catches that class outright, and keeps catching it as the schema
/// evolves — which no hand-written per-activity assertion does.</para>
///
/// <para>The second rule is the one that stops the opposite failure: a node that looks configured
/// and does nothing. If a mapping cannot fill a REQUIRED key, it must have degraded to a
/// placeholder rather than shipped an empty node — which is exactly what <c>Run Program</c> did
/// while the mapper probed property names SCOrch does not use.</para>
/// </summary>
public class ScorchMappingContractTests
{
    /// <summary>
    /// Union of every property name the mapper probes, so one bag exercises all builders. Values are
    /// deliberately plausible: a builder that reads a key must find something usable in it.
    /// </summary>
    private static Dictionary<string, string> KitchenSinkProps() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["ScriptType"] = "PowerShell",
        ["ScriptBody"] = "Get-Date",
        ["Program"] = @"C:\Windows\System32\where.exe",
        ["Parameters"] = "/R C:\\ *.log",
        ["StartupDir"] = @"C:\Windows",
        ["ComputerName"] = "SRV01",
        ["WaitForCompletion"] = "TRUE",
        ["To"] = "ops@example.test",
        ["Subject"] = "Subject",
        ["MessageContent"] = "Body",
        ["MailFormat"] = "1",
        ["OutgoingServer"] = "smtp.example.test",
        ["SenderAddress"] = "noreply@example.test",
        ["EveryMinuteValue"] = "15",
        ["Path"] = @"C:\Intake",
        ["Filters"] = "<ItemRoot><Entry><FilterValue>*.csv</FilterValue></Entry></ItemRoot>",
        ["IncludeSubFolders"] = "False",
        ["NotifyIfCreated"] = "True",
        ["SourcePath"] = @"C:\Intake\a.txt",
        ["SourceFileName"] = @"C:\Intake\a.txt",
        ["OriginFolder"] = @"C:\Intake\a.txt",
        ["Folder"] = @"C:\Intake",
        ["DestinationFolder"] = @"D:\Archive",
        ["NewName"] = "b.txt",
        ["ArchiveName"] = @"D:\Archive\a.zip",
        ["XmlTag"] = "//Manifest/Status",
        ["XmlFile"] = @"C:\Intake\a.xml",
        ["InputXmlFile"] = "True",
        ["StringLength"] = "8",
        ["UseUpperCase"] = "True",
        ["UseLowerCase"] = "True",
        ["UseNumbers"] = "True",
        ["StringToCompare"] = "a",
        ["StringTestOption"] = "2",
        ["StringToCompareTo"] = "b",
        ["PolicyPath"] = @"Policies\Shared\Child Runbook",
        ["WaitToComplete"] = "TRUE",
        ["ConnectionName"] = "OpsDb",
        ["Query"] = "SELECT 1",
        ["URL"] = "https://api.example.test/v1",
        ["Method"] = "post",
        ["ServiceName"] = "Spooler",
        ["Action"] = "restart",
        ["WaitForAll"] = "TRUE",
        ["LogName"] = "Application",
        ["Source"] = "MyApp",
        ["EntryType"] = "Error",
        ["Text"] = "a line",
        ["LineNumber"] = "3",
        ["SearchText"] = "old",
        ["ReplaceText"] = "new",
        ["Namespace"] = "root/cimv2",
        ["Force"] = "TRUE",
    };

    private static XElement Obj(string typeName) =>
        new("Object",
            new XElement("Name", $"A {typeName}"),
            new XElement("ObjectTypeName", typeName),
            // Runbook Control activities declare their inputs/outputs here, not as flat properties.
            new XElement("PublishedData",
                "<ItemRoot><Entry><Name>Alpha</Name><Variable>Alpha</Variable></Entry></ItemRoot>"),
            new XElement("TRIGGER_POLICY_PARAMETERS",
                new XElement("Entry",
                    new XElement("ParameterName", "Alpha"),
                    new XElement("Value", "1"))));

    public static TheoryData<string> SupportedTypes()
    {
        var data = new TheoryData<string>();
        foreach (var typeName in ScorchActivityMapper.SupportedTypeNames) data.Add(typeName);
        return data;
    }

    [Theory]
    [MemberData(nameof(SupportedTypes))]
    public void EveryEmittedConfigKey_IsDocumentedForItsActivity(string typeName)
    {
        var mapping = ScorchActivityMapper.Map(Obj(typeName), KitchenSinkProps());

        ActivityCatalog.ByType.Should().ContainKey(mapping.ActivityType,
            "the importer must not emit an activity type the engine has no executor for");

        var entry = ActivityConfigReference.TryGet(mapping.ActivityType);
        entry.Should().NotBeNull();

        var documented = entry!.ConfigKeys.Select(k => k.Key).ToHashSet(StringComparer.Ordinal);
        var undocumented = mapping.Config.Keys
            .Where(k => k != ScorchActivityMapper.RawPropertiesConfigKey)
            .Where(k => !documented.Contains(k))
            .ToList();

        undocumented.Should().BeEmpty(
            "SCOrch '{0}' maps to {1}, and a key its executor never reads makes the imported node "
            + "look configured while doing nothing: {2}",
            typeName, mapping.ActivityType, string.Join(", ", undocumented));
    }

    [Theory]
    [MemberData(nameof(SupportedTypes))]
    public void EveryRequiredConfigKey_IsEitherFilledOrTheMappingDegraded(string typeName)
    {
        var mapping = ScorchActivityMapper.Map(Obj(typeName), KitchenSinkProps());
        var entry = ActivityConfigReference.TryGet(mapping.ActivityType)!;

        foreach (var key in entry.ConfigKeys.Where(k => k.Required))
        {
            mapping.Config.Should().ContainKey(key.Key,
                "SCOrch '{0}' claims to map to {1}", typeName, mapping.ActivityType);
            mapping.Config[key.Key].Should().NotBeNull();
            mapping.Config[key.Key]!.ToString().Should().NotBeNullOrWhiteSpace(
                "a {0} node without '{1}' cannot run — the mapping should have degraded to a placeholder",
                mapping.ActivityType, key.Key);
        }
    }

    /// <summary>
    /// The degradation itself: with nothing to read, no supported type may produce a runnable-looking
    /// node. Every one of them has to end up as a disabled placeholder that says what was lost.
    /// </summary>
    [Theory]
    [MemberData(nameof(SupportedTypes))]
    public void WithNoPropertiesAtAll_EveryMappingDegradesOrFillsItsRequiredKeys(string typeName)
    {
        var mapping = ScorchActivityMapper.Map(
            new XElement("Object",
                new XElement("Name", "Empty"),
                new XElement("ObjectTypeName", typeName)),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        var entry = ActivityConfigReference.TryGet(mapping.ActivityType)!;
        var unfilled = entry.ConfigKeys
            .Where(k => k.Required)
            .Where(k => !mapping.Config.TryGetValue(k.Key, out var v)
                        || string.IsNullOrWhiteSpace(v?.ToString()))
            .Select(k => k.Key)
            .ToList();

        unfilled.Should().BeEmpty(
            "an export carrying nothing for SCOrch '{0}' must not produce a {1} node with an empty {2}",
            typeName, mapping.ActivityType, string.Join("/", unfilled));
    }

    /// <summary>
    /// The same two rules applied to every node the realistic fixture produces — the pass that runs
    /// against real-shaped input rather than a synthetic bag.
    /// </summary>
    [Fact]
    public void EveryNodeFromTheRealisticFixture_SatisfiesTheConfigContract()
    {
        using var stream = File.OpenRead(Path.Combine(
            AppContext.BaseDirectory, "Scorch", "Fixtures", "realistic-runbook.ois_export"));
        var result = new ScorchImporter().Parse(stream);

        var violations = new List<string>();
        foreach (var workflow in result.Workflows)
        {
            var def = JsonSerializer.Deserialize<JsonElement>(workflow.DefinitionJson);
            foreach (var node in def.GetProperty("nodes").EnumerateArray())
            {
                var data = node.GetProperty("data");
                var type = data.GetProperty("activityType").GetString()!;
                var entry = ActivityConfigReference.TryGet(type);
                if (entry is null) { violations.Add($"{type}: no config reference"); continue; }

                var documented = entry.ConfigKeys.Select(k => k.Key).ToHashSet(StringComparer.Ordinal);
                foreach (var key in data.GetProperty("config").EnumerateObject())
                {
                    if (key.Name == ScorchActivityMapper.RawPropertiesConfigKey) continue;
                    if (!documented.Contains(key.Name))
                        violations.Add($"{type}.{key.Name} (node '{data.GetProperty("label").GetString()}')");
                }
            }
        }

        violations.Should().BeEmpty(
            "these imported nodes carry config keys their executor never reads: {0}",
            string.Join(", ", violations));
    }
}
