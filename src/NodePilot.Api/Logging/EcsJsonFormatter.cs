using System.Text.Json;
using Serilog.Events;
using Serilog.Formatting;

namespace NodePilot.Api.Logging;

/// <summary>
/// ECS-1.x compatible JSON formatter that emits one log event per line. Maps the standard
/// Serilog fields (timestamp, level, message, exception) to ECS reserved names and then
/// hoists every NodePilot-domain property into a <c>nodepilot.*</c> custom-namespace
/// object so SIEM ingest pipelines (Elastic, Splunk via HEC, Sentinel) can index by
/// workflow / execution / step without parsing the message text.
/// <para>
/// The mapping is deliberate (not generic): the property names that map to first-class
/// ECS fields (<c>http.*</c>, <c>user.*</c>, etc.) are not used by NodePilot today, so
/// every property gets pushed under the <c>nodepilot</c> tree by default. If a future
/// feature adds an HTTP-shaped property, add an explicit mapping here rather than
/// pattern-matching field names.
/// </para>
/// </summary>
public sealed class EcsJsonFormatter : ITextFormatter
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    // ECS prefixes that should land at the JSON root in their proper nested ECS shape
    // rather than under nodepilot.*. Two categories:
    //   1) Host/service identity (service.*, host.*, deployment.*, …) — enriched once
    //      per process and indexed by the SIEM as the source of every event.
    //   2) Per-event ECS fields (event.*, user.*, source.*, trace.*, span.*, error.*) —
    //      bound to log calls by the audit forward and enricher pipeline. Standard SIEM
    //      detection rules (Sigma, Sentinel analytics, Elastic detection rules) bind to
    //      these names; surfacing them under nodepilot.user_id would make every rule
    //      out-of-the-box silent for NodePilot.
    private static readonly string[] EcsRootPrefixes =
    {
        "service.",
        "host.",
        "deployment.",
        "agent.",
        "cloud.",
        "container.",
        "event.",
        "user.",
        "source.",
        "trace.",
        "span.",
        "error.",
        "client.",
        "network.",
        "url.",
        "http.",
    };

    public void Format(LogEvent logEvent, TextWriter output)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, WriterOptions))
        {
            writer.WriteStartObject();

            // ECS reserved fields.
            writer.WriteString("@timestamp", logEvent.Timestamp.UtcDateTime.ToString("o"));
            writer.WriteString("log.level", MapLevel(logEvent.Level));
            writer.WriteString("message", logEvent.RenderMessage());
            writer.WriteString("ecs.version", "1.12.0");

            if (logEvent.Exception is not null)
            {
                writer.WriteStartObject("error");
                writer.WriteString("type", logEvent.Exception.GetType().FullName);
                writer.WriteString("message", logEvent.Exception.Message);
                writer.WriteString("stack_trace", logEvent.Exception.ToString());
                writer.WriteEndObject();
            }

            // Split the property bag into ECS-root candidates and nodepilot.* domain props.
            // Group root candidates by their first segment so we can write one nested object
            // per ECS namespace instead of duplicating writeStartObject calls.
            var rootGroups = new Dictionary<string, List<(string sub, LogEventPropertyValue val)>>(
                StringComparer.OrdinalIgnoreCase);
            var nodepilotProps = new List<KeyValuePair<string, LogEventPropertyValue>>(logEvent.Properties.Count);
            foreach (var kv in logEvent.Properties)
            {
                var matched = false;
                foreach (var prefix in EcsRootPrefixes)
                {
                    if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var ns = prefix.TrimEnd('.');
                        var sub = kv.Key.Substring(prefix.Length);
                        if (!rootGroups.TryGetValue(ns, out var list))
                            rootGroups[ns] = list = new();
                        list.Add((sub, kv.Value));
                        matched = true;
                        break;
                    }
                }
                if (!matched) nodepilotProps.Add(new(kv.Key, kv.Value));
            }

            foreach (var (ns, list) in rootGroups)
            {
                writer.WriteStartObject(ns);
                // Edge case: NormalizeKey can map two different source names to the same target
                // (e.g. WorkflowId and workflow_id both → workflow_id). Last-wins via
                // dictionary. Without this, Utf8JsonWriter writes both keys and several
                // SIEM ingest pipelines reject duplicates outright; others silently
                // last-wins. Pinning the behavior explicitly keeps every SIEM consistent.
                var deduped = DedupByNormalizedName(
                    list.Select(p => (p.sub, p.val)));
                foreach (var (name, val) in deduped)
                    WriteProperty(writer, name, val);
                writer.WriteEndObject();
            }

            // NodePilot-domain properties under the custom namespace.
            if (nodepilotProps.Count > 0)
            {
                writer.WriteStartObject("nodepilot");
                var deduped = DedupByNormalizedName(
                    nodepilotProps.Select(kv => (kv.Key, kv.Value)));
                foreach (var (name, val) in deduped)
                    WriteProperty(writer, name, val);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        // Serilog's text writer takes the whole line + newline. Decode utf8 → string just
        // once at the end; the inner writer is bytes-only so the JSON encoding stays correct
        // for non-ASCII payloads (workflow names with umlauts, German error messages, etc.).
        output.Write(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
        output.WriteLine();
    }

    private static string MapLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "trace",
        LogEventLevel.Debug => "debug",
        LogEventLevel.Information => "info",
        LogEventLevel.Warning => "warn",
        LogEventLevel.Error => "error",
        LogEventLevel.Fatal => "fatal",
        _ => "info",
    };

    /// <summary>
    /// Apply <see cref="NormalizeKey"/> to each input source-name and keep only the last
    /// value per normalized key. Preserves insertion order so callers can rely on a
    /// stable last-wins ordering: if a Serilog enricher and a per-call argument both
    /// emit a property that normalizes to the same name, the later one in the property
    /// bag wins and only it lands in the JSON output.
    /// </summary>
    private static IEnumerable<(string Name, LogEventPropertyValue Value)> DedupByNormalizedName(
        IEnumerable<(string Source, LogEventPropertyValue Value)> input)
    {
        // Keep insertion order with a list + an index dictionary so the last write at any
        // normalized name overwrites earlier entries without re-shuffling unrelated keys.
        var order = new List<(string Name, LogEventPropertyValue Value)>();
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (source, val) in input)
        {
            var name = NormalizeKey(source);
            if (indexByName.TryGetValue(name, out var i))
            {
                order[i] = (name, val);
            }
            else
            {
                indexByName[name] = order.Count;
                order.Add((name, val));
            }
        }
        return order;
    }

    /// <summary>
    /// PascalCase → snake_case-ish: split on capital letters, lowercase. Stable across
    /// SIEM ingest pipelines and matches the ECS field-naming convention. <c>WorkflowId</c>
    /// becomes <c>workflow_id</c>; <c>StepStartedAt</c> becomes <c>step_started_at</c>.
    /// </summary>
    private static string NormalizeKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return key;
        var sb = new System.Text.StringBuilder(key.Length + 4);
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static void WriteProperty(Utf8JsonWriter writer, string name, LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue s:
                WriteScalar(writer, name, s.Value);
                break;
            case SequenceValue seq:
                writer.WritePropertyName(name);
                writer.WriteStartArray();
                foreach (var item in seq.Elements)
                    WriteScalarOrFallback(writer, item);
                writer.WriteEndArray();
                break;
            case StructureValue st:
                writer.WriteStartObject(name);
                foreach (var (n, v) in DedupByNormalizedName(
                    st.Properties.Select(p => (p.Name, p.Value))))
                    WriteProperty(writer, n, v);
                writer.WriteEndObject();
                break;
            case DictionaryValue dict:
                writer.WriteStartObject(name);
                foreach (var (n, v) in DedupByNormalizedName(
                    dict.Elements.Select(e => (e.Key.Value?.ToString() ?? string.Empty, e.Value))))
                    WriteProperty(writer, n, v);
                writer.WriteEndObject();
                break;
            default:
                writer.WriteString(name, value.ToString());
                break;
        }
    }

    private static void WriteScalar(Utf8JsonWriter writer, string name, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNull(name); break;
            case string s: writer.WriteString(name, s); break;
            case bool b: writer.WriteBoolean(name, b); break;
            case int i: writer.WriteNumber(name, i); break;
            case long l: writer.WriteNumber(name, l); break;
            case double d: writer.WriteNumber(name, d); break;
            case decimal dec: writer.WriteNumber(name, dec); break;
            case DateTime dt: writer.WriteString(name, dt.ToUniversalTime().ToString("o")); break;
            case DateTimeOffset dto: writer.WriteString(name, dto.UtcDateTime.ToString("o")); break;
            case Guid g: writer.WriteString(name, g.ToString()); break;
            default: writer.WriteString(name, value.ToString()); break;
        }
    }

    private static void WriteScalarOrFallback(Utf8JsonWriter writer, LogEventPropertyValue value)
    {
        if (value is ScalarValue s)
        {
            switch (s.Value)
            {
                case null: writer.WriteNullValue(); return;
                case string str: writer.WriteStringValue(str); return;
                case bool b: writer.WriteBooleanValue(b); return;
                case int i: writer.WriteNumberValue(i); return;
                case long l: writer.WriteNumberValue(l); return;
                case double d: writer.WriteNumberValue(d); return;
                default: writer.WriteStringValue(s.Value.ToString()); return;
            }
        }
        writer.WriteStringValue(value.ToString());
    }
}
