using System.Text.Json;

namespace NodePilot.Core.Audit;

/// <summary>
/// Builds the compact JSON detail strings passed to <see cref="IAuditWriter.LogAsync"/>.
/// Keeps detail serialization in one place instead of hand-written JSON at every call site,
/// so invariants of the audit-row schema can be applied centrally.
/// </summary>
public static class AuditDetails
{
    /// <summary>
    /// Serializes the supplied key/value pairs as a single-line JSON object. Insertion order
    /// of the fields is preserved.
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
