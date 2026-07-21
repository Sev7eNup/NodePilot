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
    public void ActivityConfigReference_CoversEveryCoreActivityType()
    {
        using var doc = JsonDocument.Parse(NodePilotResources.ActivityConfigReference());
        var documented = doc.RootElement.GetProperty("activities")
            .EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        // Drift guard: every type in the backend catalog must have a curated config reference,
        // otherwise get_activity_config_reference / validate_activity_config silently degrade for it.
        var missing = ActivityCatalog.All.Select(a => a.Type).Where(t => !documented.Contains(t)).ToList();
        missing.Should().BeEmpty($"these Core activity types lack a config-reference entry: {string.Join(", ", missing)}");
    }
}
