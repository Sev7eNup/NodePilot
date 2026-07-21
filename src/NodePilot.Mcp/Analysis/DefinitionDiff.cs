using System.Text.Json;

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
        var modified = prop.Keys.Where(k => cur.ContainsKey(k) && !JsonEquals(cur[k], prop[k])).OrderBy(k => k).ToList();

        return new ArrayDiff(added, removed, modified);
    }

    private static Dictionary<string, string> IndexById(JsonElement def, string arrayName)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (def.ValueKind == JsonValueKind.Object && def.TryGetProperty(arrayName, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                {
                    var id = idEl.GetString();
                    if (!string.IsNullOrEmpty(id)) map[id] = Canonical(item);
                }
        return map;
    }

    // Order-insensitive comparison of objects by canonicalising property order recursively.
    private static bool JsonEquals(string a, string b) => a == b;

    private static string Canonical(JsonElement el)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonical(el, writer);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(JsonElement el, Utf8JsonWriter writer)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var p in el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(p.Name);
                    WriteCanonical(p.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in el.EnumerateArray()) WriteCanonical(item, writer);
                writer.WriteEndArray();
                break;
            default:
                el.WriteTo(writer);
                break;
        }
    }
}
