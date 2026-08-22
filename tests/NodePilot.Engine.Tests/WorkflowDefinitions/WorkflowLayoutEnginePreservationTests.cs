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

    private static JsonElement Definition(IEnumerable<string> nodes) =>
        JsonSerializer.Deserialize<JsonElement>(
            "{\"nodes\":[" + string.Join(",", nodes) + "],\"edges\":[]}");

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
}
