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
/// Extra horizontal gap after the first column. Trigger nodes render as octagons at 1.55x their
/// bounding box, so the styleguide asks for more room right after them than between two ordinary
/// steps.
/// </param>
/// <param name="GridSnap">Round every coordinate to a multiple of this. 0 disables snapping.</param>
/// <param name="OrderRowsByExistingY">
/// Order the nodes within a layer by the y they already have, instead of by their order in the
/// document. This is what lets an imported graph keep the author's vertical arrangement while
/// getting NodePilot's spacing.
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
/// Simple left-to-right layered auto-layout: triggers/roots go in the leftmost column, each node
/// sits one column right of its deepest predecessor, and nodes stack vertically within a column.
/// Only node.position is rewritten — every other field is preserved verbatim.
///
/// <para>Lives in Core because two very different callers need it: the MCP <c>suggest_layout</c>
/// tool, and the SCOrch importer — which cannot reach into NodePilot.Mcp, and whose input needs
/// re-laying-out because SCOrch's canvas puts activities on a 75 px grid while a NodePilot node is
/// a 220x110 card, so copied coordinates overlap almost everywhere.</para>
/// </summary>
public static class WorkflowLayoutEngine
{
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

        // Rebuild nodes preserving all fields, replacing only position.
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

        var edges = definition.TryGetProperty("edges", out var rawEdges) && rawEdges.ValueKind == JsonValueKind.Array
            ? (JsonNode?)JsonNode.Parse(rawEdges.GetRawText())
            : new JsonArray();

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

    private static Dictionary<string, double> ReadNodeY(JsonElement definition)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        if (!definition.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            return map;

        foreach (var node in nodes.EnumerateArray())
        {
            var id = node.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                ? idProp.GetString() : null;
            if (id is null) continue;
            if (node.TryGetProperty("position", out var pos)
                && pos.ValueKind == JsonValueKind.Object
                && pos.TryGetProperty("y", out var y)
                && y.ValueKind == JsonValueKind.Number)
            {
                map[id] = y.GetDouble();
            }
        }
        return map;
    }
}
