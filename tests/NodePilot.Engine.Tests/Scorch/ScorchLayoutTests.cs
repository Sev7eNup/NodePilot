using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.WorkflowDefinitions;
using NodePilot.Engine.Scorch;
using Xunit;

namespace NodePilot.Engine.Tests.Scorch;

/// <summary>
/// Layout guarantees for imported runbooks.
///
/// <para>The arrangement of a runbook carries information — which branch is the happy path, what
/// belongs together — so an import reproduces it rather than re-deriving one. It cannot be copied
/// verbatim: SCOrch draws activities as small icons on a 75 px grid (the reference export spans
/// x −1479…246) while a NodePilot node is a card several times that size, so copied coordinates
/// overlapped nearly everywhere and started off-canvas. Scaling the whole graph uniformly is a
/// similarity transform — every distance keeps its ratio, so it is the same picture, just bigger.
/// </para>
/// </summary>
public class ScorchLayoutTests
{
    // The designer's DEFAULT rendering: classic icon view at the default size step, where the label
    // column is wider than the glyph and sets the footprint. Sizing an import against the card
    // view's 280 px instead spread the graph nearly three times wider than it needed to be.
    private const double NodeWidth = 108;
    private const double NodeHeight = 100;

    private static JsonElement ImportFixture(out ScorchImportResult result)
    {
        using var stream = File.OpenRead(Path.Combine(
            AppContext.BaseDirectory, "Scorch", "Fixtures", "realistic-runbook.ois_export"));
        result = new ScorchImporter().Parse(stream);
        return JsonSerializer.Deserialize<JsonElement>(
            result.Workflows.Single(w => w.Name == "Sample Package Intake").DefinitionJson);
    }

    private static Dictionary<string, (double X, double Y)> Positions(JsonElement definition) =>
        definition.GetProperty("nodes").EnumerateArray().ToDictionary(
            n => n.GetProperty("id").GetString()!,
            n => (n.GetProperty("position").GetProperty("x").GetDouble(),
                  n.GetProperty("position").GetProperty("y").GetDouble()));

    // The fixture's own coordinates, straight out of the .ois_export.
    private static readonly Dictionary<string, (double X, double Y)> SourcePositions = new()
    {
        ["22222222-0000-0000-0000-000000000001"] = (-1479, 377),
        ["22222222-0000-0000-0000-000000000002"] = (-1329, 377),
        ["22222222-0000-0000-0000-000000000003"] = (-1179, 377),
        ["22222222-0000-0000-0000-000000000004"] = (-1029, 377),
        ["22222222-0000-0000-0000-000000000005"] = (-879, 152),
        ["22222222-0000-0000-0000-000000000006"] = (-879, 602),
        ["22222222-0000-0000-0000-000000000007"] = (-729, 602),
        ["22222222-0000-0000-0000-000000000008"] = (-579, 602),
        ["22222222-0000-0000-0000-000000000009"] = (-429, 602),
        ["22222222-0000-0000-0000-00000000000a"] = (-279, 602),
        ["22222222-0000-0000-0000-00000000000b"] = (-129, 527),
        ["22222222-0000-0000-0000-00000000000c"] = (-129, 677),
    };

    /// <summary>
    /// The heart of it: the imported graph must be the SOURCE graph, scaled. Every distance keeps
    /// its ratio to every other, which is what makes the result recognisable as the runbook its
    /// author drew rather than a fresh arrangement of the same nodes.
    /// </summary>
    [Fact]
    public void ImportedGraph_IsTheOriginalArrangementScaledUniformly()
    {
        var imported = Positions(ImportFixture(out _));

        var srcMinX = SourcePositions.Values.Min(p => p.X);
        var srcMinY = SourcePositions.Values.Min(p => p.Y);
        var outMinX = SourcePositions.Keys.Min(id => imported[id].X);
        var outMinY = SourcePositions.Keys.Min(id => imported[id].Y);

        // Derive the scale from the widest span, where the grid rounding matters least.
        var widest = SourcePositions.OrderByDescending(kv => kv.Value.X - srcMinX).First();
        var scale = (imported[widest.Key].X - outMinX) / (widest.Value.X - srcMinX);
        // A source already spaced widely enough is only translated, never shrunk — the scale is the
        // smallest one that fits the nodes, so 1 is a legitimate answer. The derived value sits a
        // hair under it because it is read back off snapped coordinates.
        scale.Should().BeGreaterThan(1 - 20.0 / (SourcePositions.Values.Max(p => p.X) - srcMinX));

        // Grid snapping is the only licence the transform takes, and it can bite twice here: once on
        // the node under test and once on the reference point the scale was read off. Beyond that
        // budget it would be a different arrangement, not a rounding.
        const double tolerance = 25;
        foreach (var (id, (sx, sy)) in SourcePositions)
        {
            imported[id].X.Should().BeApproximately(outMinX + (sx - srcMinX) * scale, tolerance,
                "node {0} keeps its place horizontally", id);
            imported[id].Y.Should().BeApproximately(outMinY + (sy - srcMinY) * scale, tolerance,
                "node {0} keeps its place vertically", id);
        }
    }

    [Fact]
    public void ImportedNodes_NeverOverlap()
    {
        var positions = Positions(ImportFixture(out _)).ToList();

        var overlaps = new List<string>();
        for (var i = 0; i < positions.Count; i++)
        {
            for (var j = i + 1; j < positions.Count; j++)
            {
                var a = positions[i];
                var b = positions[j];
                if (Math.Abs(a.Value.X - b.Value.X) < NodeWidth && Math.Abs(a.Value.Y - b.Value.Y) < NodeHeight)
                    overlaps.Add($"{a.Key} vs {b.Key}");
            }
        }

        overlaps.Should().BeEmpty("imported nodes must not need dragging apart: {0}", string.Join(", ", overlaps));
    }

    [Fact]
    public void ImportedCoordinates_AreOnTheTwentyPixelGrid()
    {
        foreach (var (id, (x, y)) in Positions(ImportFixture(out _)))
        {
            (x % 20).Should().Be(0, "node {0} x={1}", id, x);
            (y % 20).Should().Be(0, "node {0} y={1}", id, y);
        }
    }

    [Fact]
    public void ImportedGraph_StartsAtTheCanvasOrigin_NotAtScorchNegativeCoordinates()
    {
        var positions = Positions(ImportFixture(out _)).Values.ToList();

        positions.Min(p => p.X).Should().Be(60);
        positions.Min(p => p.Y).Should().Be(60);
    }

    /// <summary>
    /// Two source nodes on the same point cannot be pulled apart by any scale, so the arrangement is
    /// not reproducible and the import falls back to laying the graph out — visibly, not silently.
    /// </summary>
    [Fact]
    public void SourceWithCoincidentPositions_FallsBackToALayeredLayout()
    {
        var definition = JsonSerializer.Deserialize<JsonElement>("""
            {"nodes":[
              {"id":"a","type":"activity","position":{"x":10,"y":10},"data":{"activityType":"log"}},
              {"id":"b","type":"activity","position":{"x":10,"y":10},"data":{"activityType":"log"}}],
             "edges":[]}
            """);

        WorkflowLayoutEngine.TryPreserveGeometry(definition, new PreservedLayoutOptions()).Should().BeNull();
    }

    /// <summary>
    /// A source drawn so tightly that fitting node cards between its activities would need an
    /// unusable canvas is refused too — a faithful graph nobody can navigate is not an improvement.
    /// </summary>
    [Fact]
    public void SourceSpacedTooTightly_IsRefusedRatherThanScaledIntoAnUnusableCanvas()
    {
        var definition = JsonSerializer.Deserialize<JsonElement>("""
            {"nodes":[
              {"id":"a","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"log"}},
              {"id":"b","type":"activity","position":{"x":2,"y":0},"data":{"activityType":"log"}}],
             "edges":[]}
            """);

        WorkflowLayoutEngine.TryPreserveGeometry(definition, new PreservedLayoutOptions()).Should().BeNull();
    }

    // ---------- the layered fallback, exercised directly ----------

    /// <summary>
    /// A part of the graph no trigger reaches gets its own band below the main flow, laid out by its
    /// own depth. Parking it in one extra column instead produced a column as tall as the node
    /// count: in the reference export a single disabled activity cut 44 of 47 nodes loose, and they
    /// came out stacked nearly 8000 px deep — technically non-overlapping, practically unreadable.
    /// </summary>
    [Fact]
    public void Reflow_DetachedComponent_IsLaidOutBelowTheMainFlow_NotInOneTallColumn()
    {
        var definition = JsonSerializer.Deserialize<JsonElement>("""
            {"nodes":[
              {"id":"t","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"manualTrigger"}},
              {"id":"m","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"log"}},
              {"id":"d1","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"log"}},
              {"id":"d2","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"log"}}],
             "edges":[
              {"id":"e1","source":"t","target":"m"},
              {"id":"e2","source":"d1","target":"d2"}]}
            """);

        var positions = Positions(JsonSerializer.SerializeToElement(
            WorkflowLayoutEngine.Reflow(definition, WorkflowLayoutOptions.Imported)));

        positions["d1"].Y.Should().BeGreaterThan(positions["m"].Y, "the detached part starts its own band");
        positions["d2"].X.Should().BeGreaterThan(positions["d1"].X, "and is laid out by its own depth");
    }

    /// <summary>
    /// The MCP <c>suggest_layout</c> tool shares this engine. Its preset must keep producing exactly
    /// what it always did — the move into Core was a relocation, not a behaviour change.
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
