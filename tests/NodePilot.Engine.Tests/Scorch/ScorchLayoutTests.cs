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
        // Drawn directly BELOW 000c at the same x, and then far LEFT of that again. Both are shapes
        // a SCOrch author produces routinely and both render as an angular U-loop if the import
        // reproduces them literally — see the two tests at the bottom of this file.
        ["22222222-0000-0000-0000-00000000000d"] = (-129, 827),
        ["22222222-0000-0000-0000-00000000000e"] = (-879, 827),
    };

    private const string StackedAbove = "22222222-0000-0000-0000-00000000000c";
    private const string StackedBelow = "22222222-0000-0000-0000-00000000000d";
    private const string DrawnFarLeft = "22222222-0000-0000-0000-00000000000e";

    /// <summary>
    /// The scale the import applied, read back off the vertical span — which the import reproduces
    /// exactly. Reading it off x would be circular: x is deliberately no longer a pure similarity.
    /// </summary>
    private static double DerivedScale(Dictionary<string, (double X, double Y)> imported)
    {
        var srcMinY = SourcePositions.Values.Min(p => p.Y);
        var tallest = SourcePositions.OrderByDescending(kv => kv.Value.Y - srcMinY).First();
        var outMinY = SourcePositions.Keys.Min(id => imported[id].Y);
        return (imported[tallest.Key].Y - outMinY) / (tallest.Value.Y - srcMinY);
    }

    // Grid snapping is the only licence the transform takes, and it bites twice in these
    // comparisons: once on the node under test, once on the reference point the scale was read off.
    private const double SnapTolerance = 25;

    /// <summary>
    /// The vertical arrangement survives untouched — which rows belong together, which branch sits
    /// above which, is most of what makes a runbook recognisable to the person who drew it. Nothing
    /// in the import moves a node vertically.
    /// </summary>
    [Fact]
    public void ImportedGraph_ReproducesTheVerticalArrangementExactly()
    {
        var imported = Positions(ImportFixture(out _));
        var srcMinY = SourcePositions.Values.Min(p => p.Y);
        var outMinY = SourcePositions.Keys.Min(id => imported[id].Y);
        var scale = DerivedScale(imported);

        foreach (var (id, (_, sy)) in SourcePositions)
        {
            imported[id].Y.Should().BeApproximately(outMinY + (sy - srcMinY) * scale, SnapTolerance,
                "node {0} keeps its place vertically", id);
        }
    }

    /// <summary>
    /// Horizontally the import is allowed one liberty, and only one: a node may be pushed RIGHT of
    /// where the scale put it, so the link into it stops rendering as an angular loop. It is never
    /// pulled left, so no activity ends up ahead of a step the author drew before it, and the
    /// leftmost node still lands on the margin (see the canvas-origin test below).
    ///
    /// <para>Note what this deliberately does NOT claim: that the x ORDER survives. A link drawn
    /// right-to-left is exactly the case the push exists for, and resolving it necessarily swaps
    /// those two nodes over. That is the trade — a step that runs later reads later.</para>
    /// </summary>
    [Fact]
    public void ImportedGraph_OnlyEverPushesNodesRightOfTheirScaledPosition()
    {
        var imported = Positions(ImportFixture(out _));
        var srcMinX = SourcePositions.Values.Min(p => p.X);
        var outMinX = SourcePositions.Keys.Min(id => imported[id].X);
        var scale = DerivedScale(imported);

        foreach (var (id, (sx, _)) in SourcePositions)
        {
            imported[id].X.Should().BeGreaterThanOrEqualTo(outMinX + (sx - srcMinX) * scale - SnapTolerance,
                "node {0} is never moved left of where the scale put it", id);
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
    /// Two source nodes on the same point cannot be pulled apart by any scale, so the arrangement
    /// is
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

    // ---------- edges have to be readable, not just nodes ----------

    private readonly record struct ImportedEdge(string Source, string Target, string? SourceHandle, string? TargetHandle);

    private static List<ImportedEdge> Edges(JsonElement definition) =>
        definition.GetProperty("edges").EnumerateArray().Select(e => new ImportedEdge(
            e.GetProperty("source").GetString()!,
            e.GetProperty("target").GetString()!,
            e.TryGetProperty("sourceHandle", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null,
            e.TryGetProperty("targetHandle", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null))
            .ToList();

    /// <summary>
    /// The designer routes a right-to-left edge as a rectangular U-loop below both nodes once the
    /// target's left port sits more than <c>BACKWARD_THRESHOLD</c> = 60 px behind the source's
    /// right
    /// port (<c>designer/edges/smartEdgePath.ts</c>). Measured between PORTS, so a pair sitting in
    /// the same column trips it just by being a node wide — which, on a source that stacks steps
    /// vertically, is most of the graph. Not one imported edge may come out that way.
    /// </summary>
    [Fact]
    public void ImportedEdges_NeverRenderAsTheAngularBackwardLoop()
    {
        var definition = ImportFixture(out _);
        var pos = Positions(definition);
        const double backwardThreshold = 60;

        var angular = Edges(definition)
            .Where(e => pos.ContainsKey(e.Source) && pos.ContainsKey(e.Target))
            // Only the default right/left docking draws the loop; a vertical dock never does.
            .Where(e => e.SourceHandle is null or "right" && e.TargetHandle is null or "left")
            .Where(e => pos[e.Target].X < pos[e.Source].X + NodeWidth - backwardThreshold)
            .Select(e => $"{e.Source}->{e.Target}")
            .ToList();

        angular.Should().BeEmpty("every link must render as a curve: {0}", string.Join(", ", angular));
    }

    /// <summary>
    /// The cheap remedy, and the one that covers most of the real cases: a pair drawn one above the
    /// other docks bottom-to-top instead of right-to-left. The loop is keyed on the port pair, so
    /// this settles it without either node moving a pixel — the arrangement is what we came to
    /// keep.
    /// </summary>
    [Fact]
    public void StackedPair_DocksVerticallyInsteadOfMovingEitherNode()
    {
        var definition = ImportFixture(out _);
        var pos = Positions(definition);

        var edge = Edges(definition).Single(e => e.Source == StackedAbove && e.Target == StackedBelow);
        edge.SourceHandle.Should().Be("bottom");
        edge.TargetHandle.Should().Be("top");

        pos[StackedBelow].X.Should().Be(pos[StackedAbove].X, "neither node had to move");
        pos[StackedBelow].Y.Should().BeGreaterThan(pos[StackedAbove].Y, "and the lower one stays lower");
    }

    /// <summary>
    /// The other remedy, for a link a vertical dock cannot help: an activity drawn far to the LEFT
    /// of the step that leads into it. Docking it vertically would draw a long diagonal across the
    /// canvas, so it is pushed right instead — far enough to clear the loop threshold, and then far
    /// enough again to clear the node it lands next to. Its row is untouched either way.
    /// </summary>
    [Fact]
    public void BackwardLink_IsPushedForwardInsteadOfLoopingBack()
    {
        var definition = ImportFixture(out _);
        var pos = Positions(definition);

        pos[DrawnFarLeft].X.Should().BeGreaterThan(pos[StackedBelow].X,
            "the activity was drawn 750 px to the left of its predecessor and had to come forward");
        pos[DrawnFarLeft].Y.Should().Be(pos[StackedBelow].Y, "but its row is never touched");
    }

    // ---------- the layered fallback, exercised directly ----------

    /// <summary>
    /// A part of the graph no trigger reaches gets its own band below the main flow, laid out by
    /// its
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
    /// The MCP <c>suggest_layout</c> tool shares this engine. Its preset must keep producing
    /// exactly
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
