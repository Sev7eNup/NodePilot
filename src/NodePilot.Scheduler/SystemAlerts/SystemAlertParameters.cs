using System.Text.Json;

namespace NodePilot.Scheduler.SystemAlerts;

/// <summary>
/// Serialization helpers for a policy's <c>SourceParametersJson</c> — one place converts between the stored
/// JSON, the API-facing dictionary, and a <see cref="SystemAlertQuery"/> for sampling.
/// </summary>
public static class SystemAlertParameters
{
    /// <summary>Parses the stored JSON to a dictionary for API responses (null/blank → null).</summary>
    public static IReadOnlyDictionary<string, object?>? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            return doc?.ToDictionary(kv => kv.Key, kv => (object?)Unbox(kv.Value));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Builds a sampling query from the stored JSON.</summary>
    public static SystemAlertQuery ToQuery(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return SystemAlertQuery.Empty;
        try
        {
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (doc is null) return SystemAlertQuery.Empty;
            return new SystemAlertQuery(doc.ToDictionary(kv => kv.Key, kv => (object?)Unbox(kv.Value)));
        }
        catch (JsonException) { return SystemAlertQuery.Empty; }
    }

    private static object? Unbox(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => el.GetRawText(),
    };
}
