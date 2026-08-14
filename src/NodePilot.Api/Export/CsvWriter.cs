using System.Text;

namespace NodePilot.Api.Export;

/// <summary>
/// CSV escaping shared by every controller that streams a table export (audit log, support
/// events). Kept in one place so the two exports cannot drift into different quoting rules —
/// a consumer that parses both would otherwise see the same value escaped two ways.
/// </summary>
public static class CsvWriter
{
    /// <summary>
    /// RFC 4180 minimal CSV escaping: only quote when the value contains a comma, quote,
    /// or newline; double internal quotes. NULL and empty render as empty (no quotes).
    /// </summary>
    public static void Field(StringBuilder sb, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        var needsQuoting = value.IndexOfAny([',', '"', '\n', '\r']) >= 0;
        if (!needsQuoting) { sb.Append(value); return; }
        sb.Append('"');
        foreach (var c in value)
        {
            if (c == '"') sb.Append("\"\"");
            else sb.Append(c);
        }
        sb.Append('"');
    }
}
