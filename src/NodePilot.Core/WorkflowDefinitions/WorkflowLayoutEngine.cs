using System.Text.Json;
using System.Text.Json.Nodes;

namespace NodePilot.Core.WorkflowDefinitions;

/// <summary>
/// Spacing and ordering for <see cref="WorkflowLayoutEngine"/>.
/// </summary>
/// <param name="ColumnWidth">Horizontal distance between two graph layers.</param>
/// <param name="RowHeight">Vertical distance between two nodes in the same layer.</param>
/// <param name="Margin">Offset of the first column/row from the origin.</param>
/// <param name="TriggerHeadroom">
/// Extra horizontal gap after the first column. Trigger nodes render as octagons larger than their
/// bounding box and need more room after them than an ordinary step.
/// </param>
/// <param name="GridSnap">Round every coordinate to a multiple of this. 0 disables snapping.</param>
/// <param name="OrderRowsByExistingY">
/// Order the nodes within a layer by the y they already have instead of by document order. This
/// lets an imported graph keep its vertical arrangement while getting NodePilot's spacing.
/// </param>
public sealed record WorkflowLayoutOptions(
    double ColumnWidth,
    double RowHeight,
    double Margin,
    double TriggerHeadroom = 0,
    double GridSnap = 0,
    bool OrderRowsByExistingY = false)
{
    /// <summary>Tight spacing, and the exact numbers the MCP <c>suggest_layout</c> tool has always used.</summary>
    public static readonly WorkflowLayoutOptions Compact = new(280, 120, 60);

    /// <summary>
    /// Spacing for generated or imported workflows, per <c>docs/workflow-styleguide.md</c>. The
    /// column and row are wider than a node's 220x110 bounding box, so no two nodes can overlap
    /// regardless of graph size, and every coordinate lands on the 20 px grid.
    /// </summary>
    public static readonly WorkflowLayoutOptions Imported =
        new(300, 180, 60, TriggerHeadroom: 100, GridSnap: 20, OrderRowsByExistingY: true);
}

/// <summary>
/// Bounds for reproducing a graph's own geometry instead of re-laying it out.
/// </summary>
/// <param name="NodeWidth">
/// How wide a node occupies on canvas. The defaults describe the designer's DEFAULT rendering —
/// the classic icon view at the default size step, where the label column (108 px) is wider than
/// the glyph and therefore sets the footprint. Sizing against the card view's 280 px instead spaced
/// an imported graph nearly three times wider than it needed to be, and the whole point of keeping
/// the original arrangement is being able to take it in at a glance.
/// </param>
/// <param name="NodeHeight">Icon plus a wrapped label at the default size step.</param>
/// <param name="MinGap">
/// Clear space to leave between two node edges. Must stay above <paramref name="GridSnap"/>, since
/// snapping can move each of a pair by half a step and would otherwise close the gap.
/// </param>
/// <param name="MinForwardGap">
/// How far right of its source a target must sit for the edge between them to render as a curve.
/// The designer draws a rectangular U-loop whenever a right-to-left edge runs backwards by more
/// than <c>BACKWARD_THRESHOLD</c> = 60 px measured between PORTS — i.e. whenever
/// <c>target.x &lt; source.x + nodeWidth - 60</c> (<c>designer/edges/smartEdgePath.ts</c>). The
/// default mirrors that: <c>108 - 60</c>, plus one grid step so snapping cannot eat the margin.
/// <para>Deliberately NOT the node's own width. This constraint is about how an edge draws, not
/// about nodes colliding — a pair that also needs pulling apart is handled by
/// <paramref name="MinGap"/> in the same pass. Demanding a full node width here spread graphs
/// hundreds of pixels wider than anything on screen required.</para>
/// </param>
/// <param name="Margin">Where the top-left of the graph lands.</param>
/// <param name="GridSnap">Round every coordinate to a multiple of this. 0 disables snapping.</param>
/// <param name="MaxScale">
/// Refuse to preserve beyond this factor. A source graph whose nodes sit a few pixels apart would
/// need a scale that turns the canvas into something nobody can navigate; falling back to a layered
/// layout is better than a faithful but unusable one.
/// </param>
public sealed record PreservedLayoutOptions(
    double NodeWidth = 108,
    double NodeHeight = 100,
    double MinGap = 40,
    double MinForwardGap = 68,
    double Margin = 60,
    double GridSnap = 20,
    double MaxScale = 8);

/// <summary>
/// Simple left-to-right layered auto-layout: triggers/roots go in the leftmost column, each node
/// sits one column right of its deepest predecessor, and nodes stack vertically within a column.
/// <see cref="Reflow(JsonElement, WorkflowLayoutOptions)"/> rewrites node.position and nothing else;
/// <see cref="TryPreserveGeometry"/> may additionally set an edge's handles (see there).
///
/// <para>Lives in Core because two very different callers need it: the MCP <c>suggest_layout</c>
/// tool, and the SCOrch importer — which cannot reach into NodePilot.Mcp, and whose input needs
/// re-laying-out because SCOrch's canvas puts activities on a 75 px grid while a NodePilot node is
/// a 220x110 card, so copied coordinates overlap almost everywhere.</para>
/// </summary>
public static class WorkflowLayoutEngine
{
    /// <summary>
    /// Reproduces the graph's own arrangement instead of re-laying it out, by scaling it uniformly
    /// until no two node cards can overlap and translating it to the margin.
    ///
    /// <para>A uniform scale is a similarity transform: every distance and angle keeps its ratio, so
    /// the result is the SAME picture at a different size. That matters for an imported runbook —
    /// the author's arrangement is what makes it recognisable, and re-laying it out means handing
    /// someone a graph they have to read from scratch. The scale is needed because the source draws
    /// activities as small icons while a NodePilot node is a card several times that size.</para>
    ///
    /// <para>The scale alone leaves edges unreadable, so <see cref="MakeEdgesReadable"/> runs after
    /// it. That step keeps y an exact similarity but relaxes x: a node only ever moves RIGHT of
    /// where the scale put it, and the x order of every pair survives.</para>
    ///
    /// <para>Returns null when the arrangement cannot be reproduced: two nodes on the same point
    /// (no scale separates them), fewer than two positions to go on, a required scale beyond
    /// <see cref="PreservedLayoutOptions.MaxScale"/>, or edges that will not settle. The caller then
    /// falls back to <see cref="Reflow(JsonElement, WorkflowLayoutOptions)"/>.</para>
    /// </summary>
    public static JsonObject? TryPreserveGeometry(JsonElement definition, PreservedLayoutOptions options)
    {
        var positions = ReadNodePositions(definition);
        if (positions.Count < 2) return null;

        // Centre-to-centre distance a pair needs: the node itself plus the gap we want to SEE
        // between its edges. The gap doubles as the snapping allowance - rounding can move each of a
        // pair by half a step, and MinGap is required to exceed a full step.
        var width = options.NodeWidth + options.MinGap;
        var height = options.NodeHeight + options.MinGap;

        double required = 0;
        var points = positions.Values.ToList();
        for (var i = 0; i < points.Count; i++)
        {
            for (var j = i + 1; j < points.Count; j++)
            {
                var dx = Math.Abs(points[i].X - points[j].X);
                var dy = Math.Abs(points[i].Y - points[j].Y);
                if (dx == 0 && dy == 0) return null; // coincident: no scale ever separates them

                // The pair is clear as soon as EITHER axis separates it, so the cheaper axis decides.
                var byX = dx > 0 ? width / dx : double.PositiveInfinity;
                var byY = dy > 0 ? height / dy : double.PositiveInfinity;
                required = Math.Max(required, Math.Min(byX, byY));
            }
        }

        // Rounded up to a quarter step rather than to a whole number. Whole numbers looked tidy but
        // cost real estate: a graph needing 2.1x was pushed to 3x, spreading it half again as wide
        // as it had to be, and a graph you cannot take in at a glance defeats keeping its layout.
        var scale = Math.Max(1, Math.Ceiling(required * 4) / 4);
        if (scale > options.MaxScale) return null;

        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);

        var placed = positions.ToDictionary(
            kv => kv.Key,
            kv => (
                X: Snap(options.Margin + (kv.Value.X - minX) * scale, options.GridSnap),
                Y: Snap(options.Margin + (kv.Value.Y - minY) * scale, options.GridSnap)));

        var ports = MakeEdgesReadable(definition, placed, options);
        if (ports is null) return null; // the edge pass could not settle; lay the graph out instead
        return RewritePositions(definition, placed, ports);
    }

    /// <summary>Which side of its endpoints an edge should dock to, keyed by edge id.</summary>
    private sealed record EdgePorts(string Source, string Target);

    private readonly record struct GraphEdge(string Id, string Source, string Target);

    /// <summary>
    /// Makes the edges over an otherwise-final arrangement render as curves instead of rectangular
    /// U-loops, changing as little of the arrangement as possible.
    ///
    /// <para>The designer draws that U-loop for a right-to-left edge that runs backwards — which,
    /// because the offset is measured between PORTS, includes every pair sitting in the SAME column.
    /// On a real import that was 13 of 15 kinked edges: the source stacks activities vertically, and
    /// preserving its arrangement preserved the stacking.</para>
    ///
    /// <para>So two remedies, cheapest first. A stacked pair only needs to dock top-to-bottom
    /// instead of right-to-left — no node moves at all. Everything else has to be pulled apart
    /// horizontally, and only the target moves, only ever to the right.</para>
    ///
    /// <para>Returns null if the two remedies cannot settle against each other within a bounded
    /// number of passes — pulling a node right can push it onto a neighbour, and separating that
    /// neighbour can undo a pull. Refusing is the same answer this class gives to a geometry it
    /// cannot reproduce, and it beats handing back an arrangement that still overlaps.</para>
    /// </summary>
    private static Dictionary<string, EdgePorts>? MakeEdgesReadable(
        JsonElement definition,
        Dictionary<string, (double X, double Y)> placed,
        PreservedLayoutOptions options)
    {
        var ports = new Dictionary<string, EdgePorts>(StringComparer.Ordinal);

        var edges = ReadEdges(definition)
            .Where(e => e.Source != e.Target && placed.ContainsKey(e.Source) && placed.ContainsKey(e.Target))
            .ToList();
        if (edges.Count == 0) return ports;

        // A genuine cycle cannot have all of its edges pointing forward, and the styleguide wants a
        // loop-back to stand out rather than blend in. Its back edges keep the U-loop on purpose.
        var backEdges = FindBackEdges(edges);
        var pullable = edges.Where(e => !backEdges.Contains(e.Id)).ToList();

        var x = placed.ToDictionary(kv => kv.Key, kv => kv.Value.X, StringComparer.Ordinal);
        var y = placed.ToDictionary(kv => kv.Key, kv => kv.Value.Y, StringComparer.Ordinal);

        bool Stacked(GraphEdge e) =>
            Math.Abs(x[e.Target] - x[e.Source]) < options.NodeWidth
            && Math.Abs(y[e.Target] - y[e.Source]) >= options.NodeHeight;

        // Pulling a node right can bring it onto a neighbour, and separating that neighbour can undo
        // a pull, so the two run together until nothing moves. Every move rounds UP to the grid, so
        // each one strictly satisfies the constraint that triggered it and cannot be re-triggered:
        // rounding to the NEAREST multiple would land short of the target (148 is not a multiple of
        // 20, and Math.Round is banker's), re-arm the same constraint next pass, and spin.
        var settled = false;
        var cap = placed.Count + 4;
        for (var pass = 0; pass < cap && !settled; pass++)
        {
            var moved = false;

            foreach (var e in pullable)
            {
                if (Stacked(e)) continue; // a vertical dock fixes this one for free
                var needed = x[e.Source] + options.MinForwardGap;
                if (x[e.Target] >= needed - Epsilon) continue;
                x[e.Target] = SnapUp(needed, options.GridSnap);
                moved = true;
            }

            // Ties are broken by id so a pair the pull left on the same x still separates, and always
            // in the same direction — otherwise neither node is "the right-hand one" and this spins.
            var byX = placed.Keys.OrderBy(id => x[id]).ThenBy(id => id, StringComparer.Ordinal).ToList();
            for (var i = 0; i < byX.Count; i++)
            {
                for (var j = i + 1; j < byX.Count; j++)
                {
                    var a = byX[i];
                    var b = byX[j];
                    if (Math.Abs(x[a] - x[b]) >= options.NodeWidth) break; // sorted: nothing closer follows
                    if (Math.Abs(y[a] - y[b]) >= options.NodeHeight) continue;
                    x[b] = SnapUp(x[a] + options.NodeWidth + options.MinGap, options.GridSnap);
                    moved = true;
                }
            }

            settled = !moved;
        }

        if (!settled) return null;

        // Back to where the graph started. The pull only ever moves right, so the leftmost node can
        // drift off the margin — and it does whenever that node is some edge's target.
        var left = x.Values.Min();
        var origin = Snap(options.Margin, options.GridSnap);
        foreach (var id in placed.Keys.ToList()) placed[id] = (x[id] - left + origin, y[id]);
        foreach (var id in x.Keys.ToList()) x[id] = placed[id].X;

        // Ports last, against the settled positions — a node moved by the pull above must not be
        // classified from where it used to be.
        foreach (var e in pullable)
        {
            if (!Stacked(e)) continue;
            ports[e.Id] = y[e.Target] > y[e.Source]
                ? new EdgePorts("bottom", "top")
                : new EdgePorts("top", "bottom");
        }

        return ports;
    }

    private const double Epsilon = 1e-9;

    /// <summary>
    /// Edge ids that close a cycle, by DFS colouring. <c>WorkflowAnalyzer.FindCycle</c> returns one
    /// cycle's node path, which does not tell us which edges to exempt.
    /// </summary>
    private static HashSet<string> FindBackEdges(List<GraphEdge> edges)
    {
        var outgoing = edges
            .GroupBy(e => e.Source, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = on stack, 2 = done
        var back = new HashSet<string>(StringComparer.Ordinal);

        // Explicit stack: a deep import is a long chain, and recursion here would depend on the
        // runbook's size.
        foreach (var root in edges.Select(e => e.Source).Concat(edges.Select(e => e.Target)).Distinct(StringComparer.Ordinal))
        {
            if (state.ContainsKey(root)) continue;

            var stack = new Stack<(string Node, int Index)>();
            stack.Push((root, 0));
            state[root] = 1;

            while (stack.Count > 0)
            {
                var (node, index) = stack.Pop();
                var next = outgoing.TryGetValue(node, out var list) ? list : [];

                if (index >= next.Count)
                {
                    state[node] = 2;
                    continue;
                }

                stack.Push((node, index + 1));
                var edge = next[index];
                var target = edge.Target;

                if (state.TryGetValue(target, out var seen))
                {
                    if (seen == 1) back.Add(edge.Id); // points back at something still on the stack
                    continue;
                }

                state[target] = 1;
                stack.Push((target, 0));
            }
        }

        return back;
    }

    private static List<GraphEdge> ReadEdges(JsonElement definition)
    {
        var result = new List<GraphEdge>();
        if (!definition.TryGetProperty("edges", out var edges) || edges.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var edge in edges.EnumerateArray())
        {
            if (edge.ValueKind != JsonValueKind.Object) continue;
            if (!TryGetNonEmptyString(edge, "id", out var id)) continue;
            if (!TryGetNonEmptyString(edge, "source", out var source)) continue;
            if (!TryGetNonEmptyString(edge, "target", out var target)) continue;
            result.Add(new GraphEdge(id, source, target));
        }
        return result;
    }

    private static bool TryGetNonEmptyString(JsonElement obj, string name, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String) return false;
        value = prop.GetString() ?? "";
        return value.Length > 0;
    }

    public static JsonObject Reflow(JsonElement definition) => Reflow(definition, WorkflowLayoutOptions.Compact);

    public static JsonObject Reflow(JsonElement definition, WorkflowLayoutOptions options)
    {
        var doc = WorkflowDefinitionDocument.FromJsonElement(definition);

        // Layer = longest distance from any root over active edges; unreached nodes get the next layer.
        // The cap at node count bounds the layer value so a CYCLE reachable from a trigger
        // (t→a→b→a) terminates instead of relaxing the distance forever.
        var layer = new Dictionary<string, int>(StringComparer.Ordinal);
        Relax(doc, doc.RootNodes.Select(r => r.Id), layer);

        // Everything the roots cannot reach gets its own band BELOW the main graph, laid out by its
        // own depth. Parking it all in one extra column instead produced a single column as tall as
        // the node count: one disabled activity part-way through a real 47-node runbook cut 44 nodes
        // loose, and they came out stacked nearly 8000 px deep. Unreached does not mean unstructured.
        var band = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in layer.Keys) band[id] = 0;

        var unreached = doc.Nodes.Where(n => !layer.ContainsKey(n.Id)).Select(n => n.Id).ToList();
        if (unreached.Count > 0)
        {
            var unreachedSet = unreached.ToHashSet(StringComparer.Ordinal);
            // Entry points of the detached part: no predecessor that is itself detached. A component
            // that is a pure cycle has none, so fall back to seeding it whole.
            var seeds = unreached
                .Where(id => !doc.ReverseAdjacency.TryGetValue(id, out var preds)
                             || !preds.Any(unreachedSet.Contains))
                .ToList();
            Relax(doc, seeds.Count > 0 ? seeds : unreached, layer, unreachedSet);

            foreach (var id in unreached)
            {
                layer.TryAdd(id, 0);
                band[id] = 1;
            }
        }

        var existingY = options.OrderRowsByExistingY ? ReadNodeY(definition) : null;
        var order = doc.Nodes.Select((n, index) => (n.Id, Index: index));
        if (existingY is not null)
        {
            // Stable within a layer: the y the node already had, then document order. Keeping the
            // author's vertical arrangement is the difference between "my runbook, tidied up" and
            // "a graph I have to re-read".
            order = order.OrderBy(x => existingY.GetValueOrDefault(x.Id, 0d)).ThenBy(x => x.Index);
        }

        var rowInColumn = new Dictionary<(int Band, int Layer), int>();
        var rowsInBand = new Dictionary<int, int>();
        var placed = new List<(string Id, int Band, int Layer, int Row)>();
        foreach (var (id, _) in order)
        {
            var b = band.GetValueOrDefault(id);
            var l = layer[id];
            var row = rowInColumn.GetValueOrDefault((b, l));
            rowInColumn[(b, l)] = row + 1;
            rowsInBand[b] = Math.Max(rowsInBand.GetValueOrDefault(b), row + 1);
            placed.Add((id, b, l, row));
        }

        // One blank row between bands so the detached part reads as separate rather than as a
        // continuation of the last column.
        var bandOffset = new Dictionary<int, double> { [0] = 0 };
        bandOffset[1] = (rowsInBand.GetValueOrDefault(0) + 1) * options.RowHeight;

        var posById = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        foreach (var (id, b, l, row) in placed)
        {
            var x = options.Margin + l * options.ColumnWidth + (l > 0 ? options.TriggerHeadroom : 0);
            var y = options.Margin + bandOffset.GetValueOrDefault(b) + row * options.RowHeight;
            posById[id] = (Snap(x, options.GridSnap), Snap(y, options.GridSnap));
        }

        return RewritePositions(definition, posById);
    }

    /// <summary>
    /// Rebuilds the definition preserving every field, replacing node.position and — when
    /// <paramref name="ports"/> is given — an edge's sourceHandle/targetHandle.
    ///
    /// <para>Rewriting a handle stretches this method's "positions only" remit, deliberately: which
    /// side an edge leaves and enters is part of laying a graph out, and the styleguide sequences it
    /// that way too ("plan the layout first, then choose handles"). Only the geometry-preserving
    /// path passes them; <see cref="Reflow(JsonElement, WorkflowLayoutOptions)"/> does not, so the
    /// MCP layout tool's output is unaffected.</para>
    /// </summary>
    private static JsonObject RewritePositions(
        JsonElement definition,
        IReadOnlyDictionary<string, (double X, double Y)> posById,
        IReadOnlyDictionary<string, EdgePorts>? ports = null)
    {
        var nodes = new JsonArray();
        if (definition.TryGetProperty("nodes", out var rawNodes) && rawNodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var raw in rawNodes.EnumerateArray())
            {
                if (JsonNode.Parse(raw.GetRawText()) is not JsonObject obj) continue;
                var id = obj["id"]?.GetValue<string>();
                if (id is not null && posById.TryGetValue(id, out var p))
                    obj["position"] = new JsonObject { ["x"] = p.X, ["y"] = p.Y };
                nodes.Add(obj);
            }
        }

        JsonNode? edges;
        if (definition.TryGetProperty("edges", out var rawEdges) && rawEdges.ValueKind == JsonValueKind.Array)
        {
            if (ports is null || ports.Count == 0)
            {
                edges = JsonNode.Parse(rawEdges.GetRawText());
            }
            else
            {
                var rebuilt = new JsonArray();
                foreach (var raw in rawEdges.EnumerateArray())
                {
                    if (JsonNode.Parse(raw.GetRawText()) is not JsonObject obj) continue;
                    var id = obj["id"]?.GetValue<string>();
                    if (id is not null && ports.TryGetValue(id, out var p))
                    {
                        obj["sourceHandle"] = p.Source;
                        obj["targetHandle"] = p.Target;
                    }
                    rebuilt.Add(obj);
                }
                edges = rebuilt;
            }
        }
        else
        {
            edges = new JsonArray();
        }

        return new JsonObject { ["nodes"] = nodes, ["edges"] = edges };
    }

    /// <summary>
    /// Longest-path relaxation from the given seeds over active edges. The cap at node count bounds
    /// the layer value so a CYCLE reachable from a seed (t→a→b→a) terminates instead of relaxing the
    /// distance forever.
    /// </summary>
    private static void Relax(
        WorkflowDefinitionDocument doc,
        IEnumerable<string> seeds,
        Dictionary<string, int> layer,
        HashSet<string>? only = null)
    {
        var queue = new Queue<string>();
        foreach (var seed in seeds)
        {
            layer[seed] = 0;
            queue.Enqueue(seed);
        }

        var cap = doc.Nodes.Count;
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!doc.Adjacency.TryGetValue(cur, out var next)) continue;
            var cand = layer[cur] + 1;
            if (cand > cap) continue;
            foreach (var t in next)
            {
                // A detached component may point INTO the reachable graph. Its pass must not move
                // the nodes the roots already placed, or the main graph drifts right for no reason.
                if (only is not null && !only.Contains(t)) continue;
                if (!layer.TryGetValue(t, out var existing) || cand > existing)
                {
                    layer[t] = cand;
                    queue.Enqueue(t);
                }
            }
        }
    }

    private static double Snap(double value, double grid)
        => grid > 0 ? Math.Round(value / grid) * grid : value;

    /// <summary>
    /// Snaps to the grid without ever going below <paramref name="value"/>. Used where the value IS
    /// a minimum — a gap that has to be cleared — and rounding down would leave it unmet.
    /// </summary>
    private static double SnapUp(double value, double grid)
        => grid > 0 ? Math.Ceiling(value / grid - Epsilon) * grid : value;

    private static Dictionary<string, double> ReadNodeY(JsonElement definition)
        => ReadNodePositions(definition).ToDictionary(kv => kv.Key, kv => kv.Value.Y, StringComparer.Ordinal);

    private static Dictionary<string, (double X, double Y)> ReadNodePositions(JsonElement definition)
    {
        var map = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        if (!definition.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            return map;

        foreach (var node in nodes.EnumerateArray())
        {
            var id = node.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                ? idProp.GetString() : null;
            if (id is null) continue;
            if (node.TryGetProperty("position", out var pos)
                && pos.ValueKind == JsonValueKind.Object
                && pos.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.Number
                && pos.TryGetProperty("y", out var y) && y.ValueKind == JsonValueKind.Number)
            {
                map[id] = (x.GetDouble(), y.GetDouble());
            }
        }
        return map;
    }
}
