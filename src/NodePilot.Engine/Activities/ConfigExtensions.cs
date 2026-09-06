using System.Text.Json;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Extension helpers for reading values out of the <see cref="JsonElement"/> config blob every
/// activity receives. <see cref="GetStringOrNull"/> throws if the property exists but holds a
/// non-string value. <see cref="GetBool"/> reads a real JSON boolean, and also the quoted form a
/// resolved template produces; anything else falls back to the caller's default.
/// Int extraction is not wrapped here because activities mix strict (<c>GetInt32()</c>, throws on
/// non-int) and lenient (<c>TryGetInt32</c>, falls back) reads, and a single helper would have to
/// pick one.
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
        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            // A templated value can only sit inside a JSON string, so `"force": "{{manual.force}}"`
            // always resolves to text. Deciding by ValueKind alone made a resolved "false" read as
            // TRUE for every knob whose default is true — force, waitForExit, encrypt,
            // waitForCompletion — i.e. the dangerous direction.
            JsonValueKind.String when bool.TryParse(p.GetString(), out var parsed) => parsed,
            _ => defaultValue,
        };
    }

    /// <summary>
    /// Reads a positive integer property and returns null if the key is missing, not an int,
    /// or non-positive. Used for timeout fields that follow the convention "missing or ≤0
    /// means no enforcement / unbounded".
    /// </summary>
    public static int? GetOptionalPositiveInt(this JsonElement config, string key)
        => config.TryGetProperty(key, out var p) && p.TryGetInt32(out var v) && v > 0 ? v : null;

    /// <summary>
    /// Reads an integer that may have arrived as a JSON string. The engine's variable resolver
    /// substitutes <c>{{...}}</c> textually inside the raw config JSON, so a templated numeric
    /// field always comes back quoted — <c>"port": "{{manual.probePort}}"</c> resolves to
    /// <c>"5000"</c>, not <c>5000</c>. Without this, numeric fields silently opt out of the
    /// templating the typed sub-modes otherwise promise.
    /// </summary>
    public static bool TryGetIntOrNumericString(this JsonElement config, string key, out int value)
    {
        value = 0;
        if (!config.TryGetProperty(key, out var p)) return false;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(
                p.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }
}
