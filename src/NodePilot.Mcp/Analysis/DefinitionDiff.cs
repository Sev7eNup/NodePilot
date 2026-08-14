using System.Text.Json;
using System.Text.Json.Nodes;

namespace NodePilot.Mcp.Analysis;

/// <summary>
/// Structural diff of two workflow definitions, by node/edge id. Lets an agent (and the user)
/// see exactly what a proposed change does before applying it — added / removed / modified.
/// </summary>
public static class DefinitionDiff
{
    public sealed record ArrayDiff(IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Modified);
    public sealed record Result(ArrayDiff Nodes, ArrayDiff Edges);

    public static Result Diff(JsonElement current, JsonElement proposed)
        => new(DiffArray(current, proposed, "nodes"), DiffArray(current, proposed, "edges"));

    private static ArrayDiff DiffArray(JsonElement current, JsonElement proposed, string arrayName)
    {
        var cur = IndexById(current, arrayName);
        var prop = IndexById(proposed, arrayName);

        var added = prop.Keys.Where(k => !cur.ContainsKey(k)).OrderBy(k => k).ToList();
        var removed = cur.Keys.Where(k => !prop.ContainsKey(k)).OrderBy(k => k).ToList();
        // Semantic comparison: property order and number representation (1 vs 1.0) are irrelevant,
        // array order is not.
        var modified = prop.Keys.Where(k => cur.ContainsKey(k) && !JsonNode.DeepEquals(cur[k], prop[k])).OrderBy(k => k).ToList();

        return new ArrayDiff(added, removed, modified);
    }

    private static Dictionary<string, JsonNode?> IndexById(JsonElement def, string arrayName)
    {
        var map = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (def.ValueKind == JsonValueKind.Object && def.TryGetProperty(arrayName, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                {
                    var id = idEl.GetString();
                    if (!string.IsNullOrEmpty(id)) map[id] = JsonSerializer.SerializeToNode(item);
                }
        return map;
    }
}
