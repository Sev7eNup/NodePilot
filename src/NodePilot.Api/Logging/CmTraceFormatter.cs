using Serilog.Events;
using Serilog.Formatting;

namespace NodePilot.Api.Logging;

/// <summary>
/// Formats Serilog events in CMTrace/OneTrace format (used by Microsoft Configuration Manager).
/// Each event is a single line; CMTrace parses the structured fields into Time, Date, Component,
/// and Type columns. Open the resulting .log file with CMTrace.exe or OneTrace.exe.
///
/// type mapping: 1 = Info/Debug/Verbose, 2 = Warning, 3 = Error/Fatal
/// </summary>
public sealed class CmTraceFormatter : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        // Render the structured message template into a plain string.
        var msgBuf = new System.Text.StringBuilder(256);
        using (var sw = new StringWriter(msgBuf))
            logEvent.RenderMessage(sw);

        // Append exception inline.
        if (logEvent.Exception is not null)
        {
            msgBuf.Append(" | Exception: ");
            msgBuf.Append(logEvent.Exception.ToString());
        }

        // Append all structured properties except SourceContext (→ component field).
        foreach (var (k, v) in logEvent.Properties)
        {
            if (k == "SourceContext") continue;
            msgBuf.Append(" | ").Append(k).Append('=');
            using var psw = new StringWriter(msgBuf);
            v.Render(psw);
        }

        // CMTrace requires ONE physical line per entry — any CR/LF inside the rendered
        // message, exception stack trace, or property value (e.g. multi-line EF Core SQL
        // in the "Executed DbCommand" log) would split the entry across lines and break
        // the viewer's parse. Collapse newlines + tabs to single spaces on the assembled
        // buffer, then compact runs of whitespace so the result stays readable.
        Flatten(msgBuf);

        // A user-supplied string that happens to contain the literal `]LOG]!>` terminator
        // (e.g. a copied log excerpt as script output) would truncate the message at the
        // terminator and dump the rest of the payload outside the SMS wrapper. Replace the
        // `!` with `_` to keep the substring recognisable while breaking the match.
        ReplaceAll(msgBuf, "]LOG]!>", "]LOG]_>");

        // CMTrace's parser gives up at ~4096 chars per line — past that point the viewer
        // falls back to "raw line" rendering (no column extraction, all meta fields blank).
        // Cap the message body well below that so the trailing <time=… date=… component=…
        // context=… type=… thread=… file=""> meta (typ. 200–400 chars) still fits inside the
        // parser window.
        const int MessageCap = 3800;
        if (msgBuf.Length > MessageCap)
        {
            var dropped = msgBuf.Length - MessageCap;
            msgBuf.Length = MessageCap;
            msgBuf.Append(" …[truncated +").Append(dropped).Append(" chars]");
        }

        // CMTrace type: 1=normal, 2=warning, 3=error
        var type = logEvent.Level switch
        {
            LogEventLevel.Warning => 2,
            LogEventLevel.Error or LogEventLevel.Fatal => 3,
            _ => 1
        };

        // component = short class name from SourceContext, falls back to "NodePilot".
        // Cast to ScalarValue to get the raw string — sc.ToString() JSON-escapes inner
        // quotes which would leak a literal backslash into the component attribute.
        // A `"` inside the value would also close the CMTrace attribute early and smear
        // the remaining meta fields across one ghost attribute — swap for `'`.
        string component = "NodePilot";
        if (logEvent.Properties.TryGetValue("SourceContext", out var sc))
        {
            var raw = sc is ScalarValue { Value: string s } ? s : sc.ToString().Trim('"');
            component = raw.Replace('"', '\'');
        }

        // CMTrace time field: HH:mm:ss.fff+offsetMinutes (e.g. "14:23:01.456+120")
        var ts = logEvent.Timestamp;
        var offsetMin = (int)ts.Offset.TotalMinutes;
        var sign = offsetMin >= 0 ? "+" : "-";
        var timeField = $"{ts:HH:mm:ss.fff}{sign}{Math.Abs(offsetMin):000}";

        output.WriteLine(
            $"<![LOG[{msgBuf}]LOG]!>" +
            $"<time=\"{timeField}\" date=\"{ts:MM-dd-yyyy}\" " +
            $"component=\"{component}\" context=\"\" type=\"{type}\" " +
            $"thread=\"{Environment.CurrentManagedThreadId}\" file=\"\">");
    }

    /// <summary>
    /// In-place replace of every occurrence of <paramref name="needle"/> with
    /// <paramref name="replacement"/> (lengths may differ).
    /// </summary>
    private static void ReplaceAll(System.Text.StringBuilder sb, string needle, string replacement)
    {
        if (needle.Length == 0 || sb.Length < needle.Length) return;
        var idx = 0;
        while (idx <= sb.Length - needle.Length)
        {
            var hit = true;
            for (var k = 0; k < needle.Length; k++)
            {
                if (sb[idx + k] != needle[k]) { hit = false; break; }
            }
            if (hit)
            {
                sb.Remove(idx, needle.Length);
                sb.Insert(idx, replacement);
                idx += replacement.Length;
            }
            else
            {
                idx++;
            }
        }
    }

    /// <summary>
    /// Replaces CR/LF/TAB with spaces and collapses consecutive whitespace into one
    /// space. Operates in-place on the buffer.
    /// </summary>
    private static void Flatten(System.Text.StringBuilder sb)
    {
        var write = 0;
        var prevSpace = false;
        for (var read = 0; read < sb.Length; read++)
        {
            var c = sb[read];
            var isWs = c == '\r' || c == '\n' || c == '\t' || c == ' ';
            if (isWs)
            {
                if (prevSpace) continue;
                sb[write++] = ' ';
                prevSpace = true;
            }
            else
            {
                sb[write++] = c;
                prevSpace = false;
            }
        }
        sb.Length = write;
    }
}
