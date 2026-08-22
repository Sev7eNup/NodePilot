using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.WorkflowDefinitions;
using NodePilot.Engine.Scorch;
using Xunit;

namespace NodePilot.Engine.Tests.Scorch;

/// <summary>
/// Layout guarantees for imported runbooks.
///
/// <para>The importer used to copy SCOrch's PositionX/PositionY verbatim. SCOrch draws activities as
/// small icons on a 75 px grid and its x is routinely negative (the reference export spans
/// x −1479…246), while a NodePilot node is a 220x110 card — so an imported graph opened as a pile of
/// overlapping nodes somewhere off-canvas, and the first thing anyone did was drag them apart.</para>
///
/// <para>Non-overlap here is a property, not a hope: with a column of 300 and a row of 180 against a
/// 220x110 card, bounding boxes are disjoint for any node count.</para>
/// </summary>
public class ScorchLayoutTests
{
    private const double NodeWidth = 220;
    private const double NodeHeight = 110;

    private static JsonElement ImportFixture(out ScorchImportResult result)
    {
        using var stream = File.OpenRead(Path.Combine(
            AppContext.BaseDirectory, "Scorch", "Fixtures", "realistic-runbook.ois_export"));
        result = new ScorchImporter().Parse(stream);
        return JsonSerializer.Deserialize<JsonElement>(
            result.Workflows.Single(w => w.Name == "Sample Package Intake").DefinitionJson);
    }

    private static List<(string Id, double X, double Y)> Positions(JsonElement definition) =>
        definition.GetProperty("nodes").EnumerateArray()
            .Select(n => (
                Id: n.GetProperty("id").GetString()!,
                X: n.GetProperty("position").GetProperty("x").GetDouble(),
                Y: n.GetProperty("position").GetProperty("y").GetDouble()))
            .ToList();

    [Fact]
    public void ImportedNodes_NeverOverlap()
    {
        var positions = Positions(ImportFixture(out _));

        var overlaps = new List<string>();
        for (var i = 0; i < positions.Count; i++)
        {
            for (var j = i + 1; j < positions.Count; j++)
            {
                var a = positions[i];
                var b = positions[j];
                if (Math.Abs(a.X - b.X) < NodeWidth && Math.Abs(a.Y - b.Y) < NodeHeight)
                    overlaps.Add($"{a.Id} vs {b.Id}");
            }
        }

        overlaps.Should().BeEmpty("imported nodes must not need dragging apart: {0}", string.Join(", ", overlaps));
    }

    [Fact]
    public void ImportedCoordinates_AreOnTheTwentyPixelGrid()
    {
        foreach (var (id, x, y) in Positions(ImportFixture(out _)))
        {
            (x % 20).Should().Be(0, "node {0} x={1}", id, x);
            (y % 20).Should().Be(0, "node {0} y={1}", id, y);
        }
    }

    [Fact]
    public void ImportedGraph_StartsAtTheCanvasOrigin_NotAtScorchNegativeCoordinates()
    {
        var positions = Positions(ImportFixture(out _));

        positions.Min(p => p.X).Should().Be(60);
        positions.Min(p => p.Y).Should().Be(60);
    }

    /// <summary>
    /// Within a column, nodes keep the order the SCOrch author gave them vertically. That is the
    /// difference between "my runbook, tidied up" and "a graph I have to re-read".
    /// </summary>
    [Fact]
    public void NodesInTheSameColumn_KeepTheirOriginalVerticalOrder()
    {
        var definition = ImportFixture(out _);
        var positions = Positions(definition).ToDictionary(p => p.Id, p => p);

        // In the fixture these two share a column: both are reached in the same number of steps
        // from the trigger, and 'Run Robocopy Sync' sits above 'Clear File Attributes' (y 527 < 677).
        var robocopy = positions["22222222-0000-0000-0000-00000000000b"];
        var attrib = positions["22222222-0000-0000-0000-00000000000c"];

        robocopy.X.Should().Be(attrib.X, "both are one step behind 'Read First Line'");
        robocopy.Y.Should().BeLessThan(attrib.Y, "SCOrch had them in that order");
    }

    /// <summary>
    /// A part of the graph no trigger reaches gets its own band below the main flow, laid out by its
    /// own depth. Parking it in one extra column instead produced a column as tall as the node
    /// count: in the reference export a single disabled activity cut 44 of 47 nodes loose, and they
    /// came out stacked nearly 8000 px deep — technically non-overlapping, practically unreadable.
    /// </summary>
    [Fact]
    public void DetachedPartOfTheGraph_IsLaidOutBelowTheMainFlow_NotInOneTallColumn()
    {
        var definition = ImportFixture(out _);
        var positions = Positions(definition).ToDictionary(p => p.Id, p => p);

        // The fixture's 006 → 007 link is disabled, so everything from 007 on is unreachable. 007
        // itself is the age-filtered Delete File, which imports as a disabled placeholder — so the
        // component that still has live links starts at 008.
        var lastReachable = positions["22222222-0000-0000-0000-000000000006"];
        var detachedHead = positions["22222222-0000-0000-0000-000000000008"];
        var detachedNext = positions["22222222-0000-0000-0000-000000000009"];

        detachedHead.Y.Should().BeGreaterThan(lastReachable.Y, "the detached part starts its own band");
        detachedNext.X.Should().BeGreaterThan(detachedHead.X, "and is laid out by its own depth");
    }

    /// <summary>
    /// The MCP <c>suggest_layout</c> tool shares this engine now. Its preset must keep producing
    /// exactly what it always did — the move into Core was a relocation, not a behaviour change.
    /// </summary>
    [Fact]
    public void CompactPreset_KeepsTheSpacingSuggestLayoutHasAlwaysUsed()
    {
        var definition = JsonSerializer.Deserialize<JsonElement>("""
            {"nodes":[
              {"id":"t","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"manualTrigger"}},
              {"id":"a","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"log"}},
              {"id":"b","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"log"}}],
             "edges":[
              {"id":"e1","source":"t","target":"a"},
              {"id":"e2","source":"t","target":"b"}]}
            """);

        var reflowed = WorkflowLayoutEngine.Reflow(definition, WorkflowLayoutOptions.Compact);
        var nodes = reflowed["nodes"]!.AsArray();

        // Margin 60, column 280, row 120, no trigger headroom, no grid snapping.
        nodes[0]!["position"]!["x"]!.GetValue<double>().Should().Be(60);
        nodes[1]!["position"]!["x"]!.GetValue<double>().Should().Be(340);
        nodes[1]!["position"]!["y"]!.GetValue<double>().Should().Be(60);
        nodes[2]!["position"]!["y"]!.GetValue<double>().Should().Be(180);
    }
}
