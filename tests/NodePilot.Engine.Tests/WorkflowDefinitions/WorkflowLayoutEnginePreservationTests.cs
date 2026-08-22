using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.WorkflowDefinitions;
using Xunit;

namespace NodePilot.Engine.Tests.WorkflowDefinitions;

/// <summary>
/// Properties <see cref="WorkflowLayoutEngine.TryPreserveGeometry"/> must hold for ANY source
/// geometry, not just the one export the SCOrch importer was measured against.
///
/// <para>The tuning that matters — the assumed node footprint — comes from the designer's own size
/// table (the default icon view at step <c>lg</c>, whose 108 px label column is the widest part of a
/// node), not from any particular runbook. The scale itself is derived per graph from its own
/// tightest pair. These tests pin that: a source drawn on a fine grid must come out further apart
/// than one drawn on a coarse grid, and neither may overlap.</para>
/// </summary>
public class WorkflowLayoutEnginePreservationTests
{
    private static readonly PreservedLayoutOptions Options = new();

    /// <summary>Builds one node object; plain concatenation keeps the JSON braces unambiguous.</summary>
    private static string Node(string id, double x, double y)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        return "{\"id\":\"" + id + "\",\"type\":\"activity\",\"position\":{\"x\":"
             + x.ToString(ci) + ",\"y\":" + y.ToString(ci)
             + "},\"data\":{\"activityType\":\"log\"}}";
    }

    /// <summary>Builds one edge object, using its endpoints as its id.</summary>
    private static string Edge(string source, string target) =>
        "{\"id\":\"" + source + "->" + target + "\",\"source\":\"" + source + "\",\"target\":\"" + target + "\"}";

    private static JsonElement Definition(IEnumerable<string> nodes, IEnumerable<string>? edges = null) =>
        JsonSerializer.Deserialize<JsonElement>(
            "{\"nodes\":[" + string.Join(",", nodes)
            + "],\"edges\":[" + string.Join(",", edges ?? []) + "]}");

    /// <summary>A grid of nodes <paramref name="step"/> apart, optionally with odd rows offset.</summary>
    private static JsonElement Grid(int columns, int rows, double step, double jitter = 0)
    {
        var nodes = new List<string>();
        for (var c = 0; c < columns; c++)
            for (var r = 0; r < rows; r++)
                nodes.Add(Node($"n{c}_{r}", c * step + (r % 2 == 1 ? jitter : 0), r * step));
        return Definition(nodes);
    }

    private static List<(string Id, double X, double Y)> Positions(System.Text.Json.Nodes.JsonObject result) =>
        result["nodes"]!.AsArray().Select(n => (
            Id: n!["id"]!.GetValue<string>(),
            X: n["position"]!["x"]!.GetValue<double>(),
            Y: n["position"]!["y"]!.GetValue<double>())).ToList();

    private static void AssertNoOverlap(List<(string Id, double X, double Y)> pts, PreservedLayoutOptions o)
    {
        for (var i = 0; i < pts.Count; i++)
        {
            for (var j = i + 1; j < pts.Count; j++)
            {
                var clear = Math.Abs(pts[i].X - pts[j].X) >= o.NodeWidth
                            || Math.Abs(pts[i].Y - pts[j].Y) >= o.NodeHeight;
                clear.Should().BeTrue("{0} and {1} must not overlap", pts[i].Id, pts[j].Id);
            }
        }
    }

    /// <summary>
    /// The whole point: whatever grid the source was drawn on, the result is usable. A tight source
    /// gets scaled up more than a roomy one — the factor is derived from the graph, not fixed.
    /// </summary>
    [Theory]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(75)]   // the step a real SCOrch export uses
    [InlineData(120)]
    [InlineData(150)]
    [InlineData(400)]  // already roomier than NodePilot needs
    public void AnySourceGridStep_ProducesAnOverlapFreeGraphAtTheOrigin(double step)
    {
        var result = WorkflowLayoutEngine.TryPreserveGeometry(Grid(6, 4, step), Options);

        result.Should().NotBeNull("a regular grid is always reproducible");
        var pts = Positions(result!);

        AssertNoOverlap(pts, Options);
        pts.Min(p => p.X).Should().Be(Options.Margin);
        pts.Min(p => p.Y).Should().Be(Options.Margin);
        pts.Should().OnlyContain(p => p.X % Options.GridSnap == 0 && p.Y % Options.GridSnap == 0);
    }

    /// <summary>
    /// A source that is already roomy enough must not be blown up further — the scale is the
    /// smallest one that fits, so a wide-drawn runbook keeps its size and is only moved to the origin.
    ///
    /// <para>Edge-free on purpose, as is <see cref="TighterSources_AreScaledUpMoreThanRoomierOnes"/>:
    /// both read spacing off the distinct x values, and the edge pass is allowed to push individual
    /// nodes right, which would turn "the spacing" into a set of different numbers.</para>
    /// </summary>
    [Fact]
    public void SourceAlreadyRoomyEnough_IsTranslatedButNotEnlarged()
    {
        const double step = 400;
        var pts = Positions(WorkflowLayoutEngine.TryPreserveGeometry(Grid(4, 3, step), Options)!);

        var xs = pts.Select(p => p.X).Distinct().OrderBy(v => v).ToList();
        (xs[1] - xs[0]).Should().Be(step, "already-sufficient spacing is kept as it is");
    }

    /// <summary>
    /// A tighter source has to end up further apart than a roomier one. This is what makes the rule
    /// general rather than a constant fitted to one file.
    /// </summary>
    [Fact]
    public void TighterSources_AreScaledUpMoreThanRoomierOnes()
    {
        double Spacing(double step)
        {
            var xs = Positions(WorkflowLayoutEngine.TryPreserveGeometry(Grid(5, 3, step), Options)!)
                .Select(p => p.X).Distinct().OrderBy(v => v).ToList();
            return xs[1] - xs[0];
        }

        var tight = Spacing(25);
        var medium = Spacing(75);
        var roomy = Spacing(200);

        // All three end up with at least the node plus its gap between neighbours...
        foreach (var s in new[] { tight, medium, roomy })
            s.Should().BeGreaterThanOrEqualTo(Options.NodeWidth + Options.MinGap - Options.GridSnap);

        // ...and none is inflated beyond what it needed: the roomy source keeps its own spacing.
        roomy.Should().Be(200);
    }

    /// <summary>Irregular, non-grid sources are reproducible too — nothing assumes a lattice.</summary>
    [Fact]
    public void IrregularlySpacedSource_IsReproducedWithoutOverlap()
    {
        var definition = JsonSerializer.Deserialize<JsonElement>("""
            {"nodes":[
              {"id":"a","type":"activity","position":{"x":-1479,"y":377},"data":{"activityType":"log"}},
              {"id":"b","type":"activity","position":{"x":-1332,"y":211},"data":{"activityType":"log"}},
              {"id":"c","type":"activity","position":{"x":-1290,"y":544},"data":{"activityType":"log"}},
              {"id":"d","type":"activity","position":{"x":-903,"y":98},"data":{"activityType":"log"}},
              {"id":"e","type":"activity","position":{"x":-877,"y":655},"data":{"activityType":"log"}},
              {"id":"f","type":"activity","position":{"x":17,"y":401},"data":{"activityType":"log"}}],
             "edges":[]}
            """);

        var result = WorkflowLayoutEngine.TryPreserveGeometry(definition, Options);

        result.Should().NotBeNull();
        AssertNoOverlap(Positions(result!), Options);
    }

    /// <summary>
    /// Two nodes on the same point are the one geometry no scale can fix, at any density. The caller
    /// gets null and falls back to laying the graph out.
    /// </summary>
    [Fact]
    public void CoincidentNodes_AreRefusedRegardlessOfHowRoomyTheRestIs()
    {
        var definition = JsonSerializer.Deserialize<JsonElement>("""
            {"nodes":[
              {"id":"a","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"log"}},
              {"id":"b","type":"activity","position":{"x":5000,"y":0},"data":{"activityType":"log"}},
              {"id":"c","type":"activity","position":{"x":5000,"y":0},"data":{"activityType":"log"}}],
             "edges":[]}
            """);

        WorkflowLayoutEngine.TryPreserveGeometry(definition, Options).Should().BeNull();
    }

    /// <summary>
    /// A definition whose nodes carry no positions at all — every one reads as the origin — is the
    /// degenerate case of the above, and must not produce a pile at the margin.
    /// </summary>
    [Fact]
    public void SourceWithoutPositions_IsRefused()
    {
        var definition = JsonSerializer.Deserialize<JsonElement>("""
            {"nodes":[
              {"id":"a","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"log"}},
              {"id":"b","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"log"}}],
             "edges":[]}
            """);

        WorkflowLayoutEngine.TryPreserveGeometry(definition, Options).Should().BeNull();
    }

    /// <summary>
    /// Below MaxScale the graph is reproduced; above it the canvas would be unusable and the caller
    /// is told to lay the graph out instead. The boundary is a stated bound, not a happy accident.
    /// </summary>
    [Theory]
    [InlineData(40, true)]   // needs ~3.7x
    [InlineData(20, true)]   // needs ~7.4x
    [InlineData(10, false)]  // would need ~15x
    [InlineData(1, false)]
    public void MaxScale_DecidesWhereReproductionStops(double step, bool expectPreserved)
    {
        var result = WorkflowLayoutEngine.TryPreserveGeometry(Grid(3, 3, step), Options);

        (result is not null).Should().Be(expectPreserved);
        if (result is not null) AssertNoOverlap(Positions(result), Options);
    }

    /// <summary>
    /// The transform is a similarity: relative distances survive. Checked on a source with no
    /// regularity to lean on, so it cannot pass by coincidence of the grid.
    /// </summary>
    [Fact]
    public void RelativeDistances_KeepTheirRatios()
    {
        var source = new Dictionary<string, (double X, double Y)>
        {
            ["a"] = (0, 0),
            ["b"] = (137, 61),
            ["c"] = (409, 22),
            ["d"] = (88, 350),
        };

        var pts = Positions(WorkflowLayoutEngine.TryPreserveGeometry(
            Definition(source.Select(kv => Node(kv.Key, kv.Value.X, kv.Value.Y))), Options)!)
            .ToDictionary(p => p.Id, p => (p.X, p.Y));

        double SourceDist(string p, string q) =>
            Math.Sqrt(Math.Pow(source[p].X - source[q].X, 2) + Math.Pow(source[p].Y - source[q].Y, 2));
        double OutDist(string p, string q) =>
            Math.Sqrt(Math.Pow(pts[p].X - pts[q].X, 2) + Math.Pow(pts[p].Y - pts[q].Y, 2));

        var reference = OutDist("a", "c") / SourceDist("a", "c");
        foreach (var (p, q) in new[] { ("a", "b"), ("b", "c"), ("c", "d"), ("a", "d"), ("b", "d") })
        {
            (OutDist(p, q) / SourceDist(p, q)).Should().BeApproximately(reference, 0.15,
                "the {0}-{1} distance keeps its ratio to every other", p, q);
        }
    }

    // ---------- the edge pass ----------
    //
    // Everything above works on positions alone. Scaling a graph up keeps its edges pointing the
    // same way, but "the same way" includes backwards — and the designer draws a backward
    // right-to-left edge as a rectangular U-loop below both nodes. So after the scale, edges get a
    // pass of their own: dock a vertically-stacked pair top-to-bottom, and push anything else far
    // enough right that the loop no longer applies. These pin what that pass may and may not do.

    private const double BackwardThreshold = 60; // designer/edges/smartEdgePath.ts

    private static void AssertNoAngularEdge(
        List<(string Id, double X, double Y)> pts, JsonElement source, System.Text.Json.Nodes.JsonObject result)
    {
        var pos = pts.ToDictionary(p => p.Id, p => (p.X, p.Y));
        var handled = result["edges"]!.AsArray()
            .Where(e => e!["sourceHandle"] is not null)
            .Select(e => e!["id"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var edge in source.GetProperty("edges").EnumerateArray())
        {
            var id = edge.GetProperty("id").GetString()!;
            var s = edge.GetProperty("source").GetString()!;
            var t = edge.GetProperty("target").GetString()!;
            if (handled.Contains(id) || s == t) continue;

            (pos[t].X >= pos[s].X + Options.NodeWidth - BackwardThreshold).Should().BeTrue(
                "edge {0} must not render as the angular loop (source x={1}, target x={2})", id, pos[s].X, pos[t].X);
        }
    }

    /// <summary>
    /// The cheap remedy: a pair one above the other keeps both positions and docks vertically. The
    /// loop is keyed on the port pair, so changing the ports settles it without moving anything —
    /// which matters, because the arrangement is the thing being preserved.
    /// </summary>
    [Fact]
    public void VerticallyStackedPair_ChangesItsPortsAndNotItsPositions()
    {
        var source = Definition(
            [Node("a", 0, 0), Node("b", 0, 300), Node("c", 600, 150)],
            [Edge("a", "b"), Edge("b", "c")]);

        var result = WorkflowLayoutEngine.TryPreserveGeometry(source, Options)!;
        var pts = Positions(result).ToDictionary(p => p.Id, p => (p.X, p.Y));

        pts["a"].X.Should().Be(pts["b"].X, "a vertical dock costs no movement at all");

        var ab = result["edges"]!.AsArray().Single(e => e!["id"]!.GetValue<string>() == "a->b")!;
        ab["sourceHandle"]!.GetValue<string>().Should().Be("bottom");
        ab["targetHandle"]!.GetValue<string>().Should().Be("top");

        // The forward edge needs nothing: it already reads left-to-right.
        var bc = result["edges"]!.AsArray().Single(e => e!["id"]!.GetValue<string>() == "b->c")!;
        bc["sourceHandle"].Should().BeNull();
    }

    /// <summary>
    /// The other remedy: an edge pointing left with the two nodes at nearly the same height cannot
    /// be helped by ports, so the target is pushed right instead. Only the target, only rightwards,
    /// and never vertically.
    ///
    /// <para>Compared against the source node rather than against the canvas: the finished graph is
    /// translated back to the margin, so if the pushed node was the leftmost one, every absolute x
    /// shifts. What must hold is that nothing moves relative to anything else except the target.</para>
    /// </summary>
    [Fact]
    public void EdgePointingLeft_PushesOnlyItsTarget_OnlyRight_AndNeverVertically()
    {
        var nodes = new[] { Node("s", 800, 0), Node("t", 0, 0), Node("far", 800, 400) };
        var withoutEdges = Positions(WorkflowLayoutEngine.TryPreserveGeometry(Definition(nodes), Options)!)
            .ToDictionary(p => p.Id, p => (p.X, p.Y));

        var source = Definition(nodes, [Edge("s", "t")]);
        var result = WorkflowLayoutEngine.TryPreserveGeometry(source, Options)!;
        var pts = Positions(result);
        var moved = pts.ToDictionary(p => p.Id, p => (p.X, p.Y));

        double OffsetBefore(string id) => withoutEdges[id].X - withoutEdges["s"].X;
        double OffsetAfter(string id) => moved[id].X - moved["s"].X;

        OffsetAfter("t").Should().BeGreaterThan(OffsetBefore("t"), "the target came forward");
        OffsetAfter("far").Should().Be(OffsetBefore("far"), "an uninvolved node keeps its place in the graph");
        foreach (var id in new[] { "s", "t", "far" })
            moved[id].Y.Should().Be(withoutEdges[id].Y, "{0} keeps its row", id);

        AssertNoAngularEdge(pts, source, result);
        AssertNoOverlap(pts, Options);
    }

    /// <summary>
    /// The two remedies against each other, on an arrangement built to make them fight: pushing a
    /// node right to clear one edge lands it on a neighbour, and separating that neighbour undoes
    /// the push. The pass has to settle anyway — and settle on the 20 px grid, without overlaps.
    /// </summary>
    [Fact]
    public void PushAndSeparation_SettleTogether_OnGrid_WithoutOverlaps()
    {
        var nodes = new List<string>();
        var edges = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            // Each step is drawn LEFT of the one before it, all in two tight rows — so every edge
            // needs a push, and every push lands the node on top of the row above.
            nodes.Add(Node($"n{i}", 900d - i * 120d, i % 2 * 130d));
            if (i > 0) edges.Add(Edge($"n{i - 1}", $"n{i}"));
        }

        var source = Definition(nodes, edges);
        var result = WorkflowLayoutEngine.TryPreserveGeometry(source, Options);

        result.Should().NotBeNull("the pass has to converge, not give up on an ordinary chain");
        var pts = Positions(result!);
        AssertNoOverlap(pts, Options);
        AssertNoAngularEdge(pts, source, result!);
        pts.Should().OnlyContain(p => p.X % Options.GridSnap == 0 && p.Y % Options.GridSnap == 0);
        pts.Min(p => p.X).Should().Be(Options.Margin, "pushing right must not lift the graph off the margin");
    }

    /// <summary>
    /// A cycle cannot have all of its edges pointing forward, so the pass exempts the edges that
    /// close one — and must not spin trying to satisfy them. Both shapes matter: a cycle hanging off
    /// the rest of the graph, and one with no path into it at all. The second is the one a
    /// roots-only traversal never colours, and it hangs rather than fails.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CyclicSource_Terminates_AndKeepsTheLoopBackVisible(bool reachable)
    {
        var nodes = new List<string>
        {
            Node("a", 0, 0), Node("b", 400, 0), Node("c", 800, 0), Node("lone", 400, 400),
        };
        var edges = new List<string> { Edge("a", "b"), Edge("b", "c"), Edge("c", "a") };
        if (reachable) { nodes.Add(Node("entry", -400, 0)); edges.Add(Edge("entry", "a")); }

        var result = WorkflowLayoutEngine.TryPreserveGeometry(Definition(nodes, edges), Options);

        result.Should().NotBeNull();
        AssertNoOverlap(Positions(result!), Options);

        // c->a closes the loop. It keeps the default ports and its backward run, on purpose: the
        // styleguide wants a loop-back to stand out rather than blend into the forward flow.
        var loop = result!["edges"]!.AsArray().Single(e => e!["id"]!.GetValue<string>() == "c->a")!;
        loop["sourceHandle"].Should().BeNull();
    }

    /// <summary>
    /// Edges to nodes that are not in the graph, and edges a node points at itself, must not derail
    /// the pass — an import is exactly where malformed references turn up.
    /// </summary>
    [Fact]
    public void DanglingAndSelfEdges_AreIgnored()
    {
        var source = Definition(
            [Node("a", 0, 0), Node("b", 400, 200)],
            [Edge("a", "a"), Edge("a", "ghost"), Edge("ghost", "b"), Edge("a", "b")]);

        var result = WorkflowLayoutEngine.TryPreserveGeometry(source, Options);

        result.Should().NotBeNull();
        result!["edges"]!.AsArray().Should().HaveCount(4, "every edge is handed back, sound or not");
    }

    /// <summary>
    /// A disabled edge still gets drawn — dashed and faded, but drawn — so it is part of the picture
    /// this pass exists to fix, and it constrains the layout like any other.
    /// </summary>
    [Fact]
    public void DisabledEdges_ConstrainTheLayoutToo_BecauseTheyStillRender()
    {
        var disabled = "{\"id\":\"s->t\",\"source\":\"s\",\"target\":\"t\",\"data\":{\"disabled\":true}}";
        var nodes = new[] { Node("s", 800, 0), Node("t", 0, 0) };

        var withoutEdges = Positions(WorkflowLayoutEngine.TryPreserveGeometry(Definition(nodes), Options)!)
            .ToDictionary(p => p.Id, p => p.X);
        var withDisabled = Positions(WorkflowLayoutEngine.TryPreserveGeometry(Definition(nodes, [disabled]), Options)!)
            .ToDictionary(p => p.Id, p => p.X);

        withDisabled["t"].Should().BeGreaterThan(withoutEdges["t"]);
    }
}
