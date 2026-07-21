using System.Text.Json;
using System.Text.Json.Nodes;

namespace NodePilot.Mcp.Mapping;

/// <summary>
/// Keeps tool payloads inside an agent's token budget. Large free-text fields
/// (stdout/stderr, return data, audit details) are truncated with an explicit marker so the
/// agent knows content was dropped and can fetch the full record another way if needed.
/// </summary>
public static class PayloadShaping
{
    public const int DefaultMaxChars = 4_000;

    /// <summary>Truncate a possibly-large string, appending a marker noting how many chars were dropped.</summary>
    public static string? Truncate(string? value, int maxChars = DefaultMaxChars)
    {
        if (value is null || value.Length <= maxChars) return value;
        var dropped = value.Length - maxChars;
        return value[..maxChars] + $"\n…[truncated {dropped} more chars]";
    }

    /// <summary>
    /// Deep-copy a JSON value, truncating EVERY string leaf to <paramref name="maxChars"/>. Used for
    /// free-form diagnostics payloads (support log lines, event messages, propertiesJson) where the
    /// shape is open-ended but any single string field could be very large.
    /// </summary>
    public static JsonNode? TruncateStrings(JsonElement element, int maxChars = DefaultMaxChars)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var prop in element.EnumerateObject())
                    obj[prop.Name] = TruncateStrings(prop.Value, maxChars);
                return obj;
            case JsonValueKind.Array:
                var arr = new JsonArray();
                foreach (var item in element.EnumerateArray())
                    arr.Add(TruncateStrings(item, maxChars));
                return arr;
            case JsonValueKind.String:
                return JsonValue.Create(Truncate(element.GetString(), maxChars));
            case JsonValueKind.Number:
                return JsonNode.Parse(element.GetRawText());
            case JsonValueKind.True:
                return JsonValue.Create(true);
            case JsonValueKind.False:
                return JsonValue.Create(false);
            default:
                return null; // Null / Undefined
        }
    }
}
