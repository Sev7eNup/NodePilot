using System.Text.Json;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Extension helpers for reading values out of the <see cref="JsonElement"/> config blob
/// every activity receives. These preserve the exact semantics of the inline patterns
/// they replace — in particular:
/// <list type="bullet">
///   <item><description><see cref="GetStringOrNull"/> still throws if the property exists
///     but is not a JSON string (matches the previous <c>p.GetString()</c> behaviour).</description></item>
///   <item><description><see cref="GetBool"/> preserves both flavours of the bool pattern
///     used across activities: with <c>defaultValue: false</c> it mirrors
///     <c>TryGetProperty &amp;&amp; ValueKind == True</c>, and with <c>defaultValue: true</c> it
///     mirrors <c>!(TryGetProperty &amp;&amp; ValueKind == False)</c>.</description></item>
/// </list>
/// Int extraction is intentionally <b>not</b> wrapped here because activities mix the
/// strict (<c>GetInt32()</c> throws on non-int) and lenient (<c>TryGetInt32</c> falls back)
/// variants; conflating them would silently shift behaviour.
/// </summary>
internal static class ConfigExtensions
{
    public static string? GetStringOrNull(this JsonElement config, string key)
        => config.TryGetProperty(key, out var p) ? p.GetString() : null;

    public static string GetString(this JsonElement config, string key, string defaultValue)
        => (config.TryGetProperty(key, out var p) ? p.GetString() : null) ?? defaultValue;

    public static bool GetBool(this JsonElement config, string key, bool defaultValue)
    {
        if (!config.TryGetProperty(key, out var p)) return defaultValue;
        return defaultValue
            ? p.ValueKind != JsonValueKind.False
            : p.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// Reads a positive integer property and returns null if the key is missing, not an int,
    /// or non-positive. Used for timeout fields that follow the convention "missing or ≤0
    /// means no enforcement / unbounded".
    /// </summary>
    public static int? GetOptionalPositiveInt(this JsonElement config, string key)
        => config.TryGetProperty(key, out var p) && p.TryGetInt32(out var v) && v > 0 ? v : null;
}
