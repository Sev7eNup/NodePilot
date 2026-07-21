using System.Globalization;
using System.Text;
using System.Text.Json;
using NodePilot.Cli.Api;

namespace NodePilot.Cli.Output;

/// <summary>
/// Minimal YAML emitter for CLI output. We only use it as a human-readable, line-oriented
/// rendering of API responses — there is no round-trip parsing. Keeping it in-house avoids
/// pulling in YamlDotNet just for `--output yaml`.
/// </summary>
public static class YamlEmitter
{
    public static string Emit<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, NodePilotApiClient.JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        Write(doc.RootElement, sb, indent: 0, isListItem: false);
        return sb.ToString();
    }

    private static void Write(JsonElement el, StringBuilder sb, int indent, bool isListItem)
    {
        var pad = new string(' ', indent);
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                bool first = true;
                foreach (var p in el.EnumerateObject())
                {
                    var prefix = first && isListItem ? "" : pad;
                    if (IsScalar(p.Value))
                    {
                        sb.Append(prefix).Append(p.Name).Append(": ").AppendLine(Scalar(p.Value));
                    }
                    else
                    {
                        sb.Append(prefix).Append(p.Name).AppendLine(":");
                        Write(p.Value, sb, indent + 2, isListItem: false);
                    }
                    first = false;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    if (IsScalar(item))
                    {
                        sb.Append(pad).Append("- ").AppendLine(Scalar(item));
                    }
                    else
                    {
                        sb.Append(pad).Append("- ");
                        Write(item, sb, indent + 2, isListItem: true);
                    }
                }
                break;
            default:
                sb.Append(pad).AppendLine(Scalar(el));
                break;
        }
    }

    private static bool IsScalar(JsonElement el) => el.ValueKind
        is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True
        or JsonValueKind.False or JsonValueKind.Null;

    private static string Scalar(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => Quote(el.GetString() ?? ""),
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => el.GetRawText(),
    };

    private static string Quote(string s)
    {
        if (s.Length == 0) return "''";
        bool needsQuote = s.IndexOfAny(new[] { ':', '#', '\n', '\r', '"', '\'', '{', '}', '[', ']', ',', '&', '*', '!', '|', '>', '%', '@', '`' }) >= 0
            || char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1])
            || bool.TryParse(s, out _) || double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        if (!needsQuote) return s;
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
