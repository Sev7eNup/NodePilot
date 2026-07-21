using System.Text.Json;

namespace NodePilot.Core.Audit;

/// <summary>
/// Builds the compact JSON detail strings passed to <see cref="IAuditWriter.LogAsync"/>.
/// Replaces the inline <c>$"{{\"foo\":{JsonSerializer.Serialize(x)}, ...}}"</c> pattern that
/// was repeated across every controller — easier to read, harder to typo, and a single place
/// to add invariants if the audit-row schema ever changes.
/// </summary>
public static class AuditDetails
{
    /// <summary>
    /// Serializes the supplied key/value pairs as a single-line JSON object. Insertion order
    /// is preserved so the resulting JSON matches what callers used to build by hand.
    /// </summary>
    public static string Json(params (string Key, object? Value)[] fields)
    {
        var dict = new Dictionary<string, object?>(fields.Length, StringComparer.Ordinal);
        foreach (var (k, v) in fields)
        {
            dict[k] = v;
        }
        return JsonSerializer.Serialize(dict);
    }
}
