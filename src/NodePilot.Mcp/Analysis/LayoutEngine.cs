using System.Text.Json;
using System.Text.Json.Nodes;
using NodePilot.Core.WorkflowDefinitions;

namespace NodePilot.Mcp.Analysis;

/// <summary>
/// Simple left-to-right layered auto-layout: triggers/roots go in the leftmost column, each node
/// sits one column right of its deepest predecessor, and nodes stack vertically within a column.
/// Only node.position is rewritten — every other field is preserved verbatim.
/// </summary>
public static class LayoutEngine
{
    private const double ColumnWidth = 280;
    private const double RowHeight = 120;
    private const double Margin = 60;

    public static JsonObject Reflow(JsonElement definition)
    {
        var doc = WorkflowDefinitionDocument.FromJsonElement(definition);

        // Layer = longest distance from any root over active edges; unreached nodes get the next layer.
        // The cap at node count bounds the layer value so a CYCLE reachable from a trigger
        // (t→a→b→a) terminates instead of relaxing the distance forever.
        var layer = new Dictionary<string, int>(StringComparer.Ordinal);
        var cap = doc.Nodes.Count;
        var queue = new Queue<string>();
        foreach (var r in doc.RootNodes) { layer[r.Id] = 0; queue.Enqueue(r.Id); }
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!doc.Adjacency.TryGetValue(cur, out var next)) continue;
            var cand = layer[cur] + 1;
            if (cand > cap) continue; // longest path in an N-node DAG is ≤ N-1; beyond that we're in a cycle
            foreach (var t in next)
            {
                if (!layer.TryGetValue(t, out var existing) || cand > existing)
                {
                    layer[t] = cand;
                    queue.Enqueue(t);
                }
            }
        }
        var maxLayer = layer.Count > 0 ? layer.Values.Max() : 0;
        foreach (var n in doc.Nodes)
            if (!layer.ContainsKey(n.Id)) layer[n.Id] = maxLayer + 1; // orphans to the far right

        var rowInLayer = new Dictionary<int, int>();
        var posById = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        foreach (var n in doc.Nodes)
        {
            var l = layer[n.Id];
            var row = rowInLayer.GetValueOrDefault(l);
            rowInLayer[l] = row + 1;
            posById[n.Id] = (Margin + l * ColumnWidth, Margin + row * RowHeight);
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
}
