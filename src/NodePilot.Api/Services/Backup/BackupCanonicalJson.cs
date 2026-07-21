using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NodePilot.Api.Services.Backup;

/// <summary>
/// Deterministic canonical serialization of the backup envelope, used as the input to the
/// whole-file HMAC that detects a tampered or corrupted backup file (ADR 0001, section K5).
/// Object keys are sorted ordinally and all insignificant whitespace is removed, so two
/// semantically identical envelopes hash identically regardless of property order. Arrays keep
/// their order (it is semantically meaningful).
/// </summary>
public static class BackupCanonicalJson
{
    /// <summary>
    /// Canonical UTF-8 bytes of <paramref name="node"/>, with the top-level <paramref name="excludeKey"/>
    /// (the <c>mac</c> field itself) omitted so the MAC can be computed over everything-but-itself.
    /// </summary>
    public static byte[] Canonicalize(JsonNode? node, string? excludeKey = null)
    {
        var sb = new StringBuilder();
        Write(node, sb, excludeKey);
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void Write(JsonNode? node, StringBuilder sb, string? excludeTopLevelKey)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                sb.Append('{');
                var first = true;
                foreach (var key in obj.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (excludeTopLevelKey is not null && key == excludeTopLevelKey) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(key));
                    sb.Append(':');
                    Write(obj[key], sb, excludeTopLevelKey: null);
                }
                sb.Append('}');
                break;
            case JsonArray arr:
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(arr[i], sb, excludeTopLevelKey: null);
                }
                sb.Append(']');
                break;
            default: // JsonValue
                sb.Append(node.ToJsonString());
                break;
        }
    }
}
