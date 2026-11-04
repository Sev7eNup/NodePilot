using System.Globalization;
using System.Text;
using System.Text.Json;
using NodePilot.Cli.Api;

namespace NodePilot.Cli.Output;

/// <summary>
/// Minimal YAML emitter for CLI output: a human-readable, line-oriented rendering of API
/// responses whose values read back unchanged with a YAML parser. Kept in-house to avoid
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

    private static readonly char[] IndicatorChars =
        [':', '#', '"', '\'', '{', '}', '[', ']', ',', '&', '*', '!', '|', '>', '%', '@', '`'];

    // Plain words that YAML 1.1 or 1.2 resolves to null or a boolean instead of a string.
    private static readonly HashSet<string> NonStringWords = new(StringComparer.OrdinalIgnoreCase)
        { "null", "true", "false", "yes", "no", "on", "off", "y", "n" };

    private static void Write(JsonElement el, StringBuilder sb, int indent, bool isListItem)
    {
        var pad = new string(' ', indent);
        switch (el.ValueKind)
        {
            case JsonValueKind.Object when el.EnumerateObject().Any():
                bool first = true;
                foreach (var p in el.EnumerateObject())
                {
                    var prefix = first && isListItem ? "" : pad;
                    if (IsInline(p.Value))
                    {
                        sb.Append(prefix).Append(Quote(p.Name)).Append(": ").AppendLine(Inline(p.Value));
                    }
                    else
                    {
                        sb.Append(prefix).Append(Quote(p.Name)).AppendLine(":");
                        Write(p.Value, sb, indent + 2, isListItem: false);
                    }
                    first = false;
                }
                break;
            case JsonValueKind.Array when el.GetArrayLength() > 0:
                bool firstItem = true;
                foreach (var item in el.EnumerateArray())
                {
                    var prefix = firstItem && isListItem ? "" : pad;
                    if (IsInline(item))
                    {
                        sb.Append(prefix).Append("- ").AppendLine(Inline(item));
                    }
                    else
                    {
                        sb.Append(prefix).Append("- ");
                        Write(item, sb, indent + 2, isListItem: true);
                    }
                    firstItem = false;
                }
                break;
            default:
                // Only the root gets here: nested scalars and empty collections are written inline.
                sb.AppendLine(Inline(el));
                break;
        }
    }

    /// <summary>Scalars and empty collections fit on the line of their key or list dash.</summary>
    private static bool IsInline(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => !el.EnumerateObject().Any(),
        JsonValueKind.Array => el.GetArrayLength() == 0,
        _ => true,
    };

    private static string Inline(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => "{}",
        JsonValueKind.Array => "[]",
        JsonValueKind.String => Quote(el.GetString() ?? ""),
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => el.GetRawText(),
    };

    private static string Quote(string s)
    {
        if (CanStayPlain(s)) return s;
        // Double-quoted with escapes: a physical line break inside quotes would fold into a space.
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (NeedsEscape(c))
                        sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>
    /// A string stays unquoted only if YAML cannot read it as anything else: it starts with a
    /// letter (so no number, date, <c>~</c>, <c>-</c> or indicator), has no indicator, control
    /// character or trailing space, and is not a null or boolean word.
    /// </summary>
    private static bool CanStayPlain(string s) =>
        s.Length > 0 && char.IsLetter(s[0]) && !char.IsWhiteSpace(s[^1])
        && s.IndexOfAny(IndicatorChars) < 0 && !s.Any(NeedsEscape)
        && !NonStringWords.Contains(s);

    // Control characters, the Unicode line separators YAML treats as breaks, and a BOM.
    private static bool NeedsEscape(char c) => char.IsControl(c) || c is (char)0x2028 or (char)0x2029 or (char)0xFEFF;
}
