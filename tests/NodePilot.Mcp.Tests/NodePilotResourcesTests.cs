using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.Activities;
using NodePilot.Mcp.Resources;
using Xunit;

namespace NodePilot.Mcp.Tests;

public sealed class NodePilotResourcesTests
{
    [Fact]
    public void AllThreeResources_ReadAndAreWellFormed()
    {
        // activity-catalog: valid JSON listing activity types (incl. a trigger).
        var catalog = NodePilotResources.ActivityCatalog();
        using (var doc = JsonDocument.Parse(catalog))
            doc.RootElement.GetProperty("activityTypes").GetArrayLength().Should().BeGreaterThan(20);
        catalog.Should().Contain("runScript").And.Contain("manualTrigger");

        // activity-config-reference: valid JSON.
        JsonDocument.Parse(NodePilotResources.ActivityConfigReference()).Dispose();

        // styleguide: the embedded markdown is non-empty.
        NodePilotResources.Styleguide().Trim().Should().NotBeEmpty();
    }

    [Fact]
    public void ActivityConfigReference_IsServedFromTheSharedCoreSource()
    {
        // The document moved to NodePilot.Core so NodePilot.Ai can render the AI prompts from the
        // same data. The MCP contract (URI, body) must be unchanged by that move. Coverage and
        // key-correctness are guarded in NodePilot.Engine.Tests/Activities/ActivityConfigReferenceTests.
        NodePilotResources.ActivityConfigReference()
            .Should().Be(NodePilot.Core.Activities.ActivityConfigReference.RawJson);

        using var doc = JsonDocument.Parse(NodePilotResources.ActivityConfigReference());
        var documented = doc.RootElement.GetProperty("activities")
            .EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var missing = ActivityCatalog.All.Select(a => a.Type).Where(t => !documented.Contains(t)).ToList();
        missing.Should().BeEmpty($"these Core activity types lack a config-reference entry: {string.Join(", ", missing)}");
    }
}
