using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog.Events;
using Serilog.Parsing;

namespace NodePilot.Api.Diagnostics;

internal enum AppLogLevel { Verbose, Debug, Information, Warning, Error, Fatal }

/// <summary>One parsed application-log event. <see cref="Template"/> is set for CLEF only.</summary>
internal sealed record AppLogEntry(
    DateTimeOffset Timestamp,
    AppLogLevel Level,
    string Source,
    string Message,
    string? Template,
    string? Details,
    bool Truncated,
    string Format);

internal sealed record AppLogParseResult(
    IReadOnlyList<AppLogEntry> Entries,
    int FragmentsDropped,
    int UnparsedLines);

/// <summary>
/// Parses the lines of the application log into entries. The format is detected per line, because a
/// restart with another <c>Logging:Format</c> mixes formats inside one file. The parser follows what
/// the formatters write: cmtrace folds Error/Fatal and Info/Debug/Verbose together, flattens line breaks
/// and caps messages; ECS carries no message template; the text format has no time zone offset.
/// </summary>
internal static class AppLogParser
{
    public const string FormatCmTrace = "cmtrace";
    public const string FormatText = "text";
    public const string FormatJson = "json";
    public const string FormatEcs = "ecs-json";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex CmTraceLine = new(
        @"^<!\[LOG\[(?<msg>.*)\]LOG\]!><time=""(?<time>[^""]*)"" date=""(?<date>[^""]*)"" component=""(?<comp>[^""]*)"" context=""[^""]*"" type=""(?<type>\d)"" thread=""[^""]*"" file=""[^""]*"">\s*$",
        RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex CmTraceTime = new(
        @"^(?<h>\d\d):(?<m>\d\d):(?<s>\d\d)\.(?<f>\d{3})(?<sign>[+-])(?<off>\d+)$",
        RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex CmTracePropertyBoundary = new(
        @"\s\|\s(?=[A-Za-z_][\w.]*=)", RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex CmTraceTruncationMarker = new(
        @"\s?…\[truncated \+\d+ chars\]$", RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex TextLineStart = new(
        @"^(?<ts>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}) \[(?<lvl>VRB|DBG|INF|WRN|ERR|FTL)\] (?<rest>.*)$",
        RegexOptions.CultureInvariant, RegexTimeout);

    // Properties every event carries from enrichers; they add noise, not information.
    private static readonly HashSet<string> NoisyProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "SourceContext", "source_context", "service.name", "service", "deployment.environment",
        "deployment", "ThreadId", "ecs.version",
    };

    private const int MaxPropertyCandidates = 20;

    public static AppLogParseResult Parse(IEnumerable<string> lines, TimeZoneInfo? textZone = null)
    {
        var zone = textZone ?? TimeZoneInfo.Local;
        var entries = new List<AppLogEntry>();
        TextEntry? open = null;
        var fragments = 0;
        var unparsed = 0;
        var sawEntry = false;

        void CloseOpen()
        {
            if (open is null) return;
            entries.Add(open.ToEntry());
            open = null;
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            if (line.StartsWith("<![LOG[", StringComparison.Ordinal))
            {
                CloseOpen();
                var entry = TryParseCmTrace(line);
                if (entry is null) unparsed++;
                else { entries.Add(entry); sawEntry = true; }
            }
            else if (line[0] == '{')
            {
                CloseOpen();
                var entry = TryParseJson(line);
                if (entry is null) unparsed++;
                else { entries.Add(entry); sawEntry = true; }
            }
            else if (TextLineStart.Match(line) is { Success: true } m)
            {
                CloseOpen();
                open = TextEntry.From(m, zone);
                sawEntry = true;
            }
            else if (open is not null)
            {
                open.Continuation.AppendLine(line);
            }
            else if (sawEntry) unparsed++;
            else fragments++;
        }

        CloseOpen();
        return new AppLogParseResult(entries, fragments, unparsed);
    }

    // ---------------------------------------------------------------- cmtrace

    private static AppLogEntry? TryParseCmTrace(string line)
    {
        var m = CmTraceLine.Match(line);
        if (!m.Success) return null;

        var time = CmTraceTime.Match(m.Groups["time"].Value);
        if (!time.Success
            || !DateTime.TryParseExact(m.Groups["date"].Value, "MM-dd-yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)
            || !int.TryParse(time.Groups["off"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var offsetMinutes))
            return null;

        TimeSpan offset;
        DateTimeOffset timestamp;
        try
        {
            offset = TimeSpan.FromMinutes(time.Groups["sign"].Value == "-" ? -offsetMinutes : offsetMinutes);
            timestamp = new DateTimeOffset(
                date.Year, date.Month, date.Day,
                int.Parse(time.Groups["h"].Value, CultureInfo.InvariantCulture),
                int.Parse(time.Groups["m"].Value, CultureInfo.InvariantCulture),
                int.Parse(time.Groups["s"].Value, CultureInfo.InvariantCulture),
                int.Parse(time.Groups["f"].Value, CultureInfo.InvariantCulture),
                offset);
        }
        catch (ArgumentException) { return null; }

        var level = m.Groups["type"].Value switch
        {
            "2" => AppLogLevel.Warning,
            "3" => AppLogLevel.Error,
            _ => AppLogLevel.Information,
        };

        var body = m.Groups["msg"].Value;
        var truncated = CmTraceTruncationMarker.IsMatch(body);
        if (truncated) body = CmTraceTruncationMarker.Replace(body, "");

        // Order written by the formatter: message, " | Exception: ...", then " | Key=Value" properties.
        var segments = CmTracePropertyBoundary.Split(body);
        var main = segments[0];
        string message = main;
        var details = new List<string>();
        const string exceptionMarker = " | Exception: ";
        var exceptionIndex = main.IndexOf(exceptionMarker, StringComparison.Ordinal);
        if (exceptionIndex >= 0)
        {
            message = main[..exceptionIndex];
            details.Add(main[(exceptionIndex + exceptionMarker.Length)..]);
        }
        foreach (var property in segments.Skip(1))
        {
            var eq = property.IndexOf('=');
            if (eq > 0 && !NoisyProperties.Contains(property[..eq])) details.Add(property);
        }

        var component = m.Groups["comp"].Value;
        return new AppLogEntry(timestamp, level, component.Length == 0 ? "NodePilot" : component,
            message, null, details.Count == 0 ? null : string.Join('\n', details), truncated, FormatCmTrace);
    }

    // ---------------------------------------------------------------- text

    private sealed class TextEntry
    {
        public required DateTimeOffset Timestamp { get; init; }
        public required AppLogLevel Level { get; init; }
        public required string Source { get; init; }
        public required string Message { get; init; }
        public string? PropertyDetails { get; init; }
        public StringBuilder Continuation { get; } = new();

        public static TextEntry From(Match m, TimeZoneInfo zone)
        {
            var local = DateTime.ParseExact(m.Groups["ts"].Value, "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture, DateTimeStyles.None);
            var level = m.Groups["lvl"].Value switch
            {
                "VRB" => AppLogLevel.Verbose,
                "DBG" => AppLogLevel.Debug,
                "WRN" => AppLogLevel.Warning,
                "ERR" => AppLogLevel.Error,
                "FTL" => AppLogLevel.Fatal,
                _ => AppLogLevel.Information,
            };

            var rest = m.Groups["rest"].Value;
            var (message, properties) = SplitTrailingProperties(rest);
            var source = "NodePilot";
            var propertyLines = new List<string>();
            if (properties is { } props)
            {
                foreach (var p in props.EnumerateObject())
                {
                    if (p.NameEquals("SourceContext") && p.Value.ValueKind == JsonValueKind.String)
                        source = p.Value.GetString() ?? source;
                    else if (!NoisyProperties.Contains(p.Name))
                        propertyLines.Add($"{p.Name}={RenderJsonValue(p.Value)}");
                }
            }

            return new TextEntry
            {
                Timestamp = new DateTimeOffset(local, zone.GetUtcOffset(local)),
                Level = level,
                Source = source,
                Message = message,
                PropertyDetails = propertyLines.Count == 0 ? null : string.Join('\n', propertyLines),
            };
        }

        public AppLogEntry ToEntry()
        {
            var details = new List<string>();
            var continuation = Continuation.ToString().TrimEnd('\r', '\n');
            if (continuation.Length > 0) details.Add(continuation);
            if (PropertyDetails is not null) details.Add(PropertyDetails);
            return new AppLogEntry(Timestamp, Level, Source, Message, null,
                details.Count == 0 ? null : string.Join('\n', details), false, FormatText);
        }
    }

    /// <summary>
    /// The text template ends in <c>{Properties:j}</c>, a JSON object after one space. The message may
    /// contain braces itself, so candidates are tried from the right until one parses to the line end.
    /// </summary>
    private static (string Message, JsonElement? Properties) SplitTrailingProperties(string rest)
    {
        var tries = 0;
        var index = rest.Length;
        while (tries < MaxPropertyCandidates && index > 0
            && (index = rest.LastIndexOf(" {", index - 1, StringComparison.Ordinal)) >= 0)
        {
            tries++;
            try
            {
                using var doc = JsonDocument.Parse(rest[(index + 1)..]);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    return (rest[..index], doc.RootElement.Clone());
            }
            catch (JsonException) { /* not the property block, try the next brace to the left */ }
            if (index == 0) break;
        }
        return (rest, null);
    }

    // ---------------------------------------------------------------- json (CLEF / ECS)

    private static AppLogEntry? TryParseJson(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("@timestamp", out _)) return ParseEcs(root);
            if (root.TryGetProperty("@t", out _) || root.TryGetProperty("@mt", out _)) return ParseClef(root);
            return null;
        }
        catch (JsonException) { return null; }
    }

    private static AppLogEntry? ParseClef(JsonElement root)
    {
        if (!root.TryGetProperty("@t", out var t)
            || t.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(t.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var timestamp))
            return null;

        var level = AppLogLevel.Information;
        if (root.TryGetProperty("@l", out var l) && l.ValueKind == JsonValueKind.String
            && Enum.TryParse<AppLogLevel>(l.GetString(), ignoreCase: true, out var parsedLevel))
            level = parsedLevel;

        var template = Str(root, "@mt");
        var message = Str(root, "@m") ?? (template is null ? "" : RenderTemplate(template, root));
        var source = Str(root, "SourceContext") ?? "NodePilot";

        var details = new List<string>();
        if (Str(root, "@x") is { Length: > 0 } exception) details.Add(exception);
        foreach (var p in root.EnumerateObject())
        {
            if (p.Name.StartsWith('@') || NoisyProperties.Contains(p.Name)) continue;
            details.Add($"{p.Name}={RenderJsonValue(p.Value)}");
        }

        return new AppLogEntry(timestamp, level, source, message, template,
            details.Count == 0 ? null : string.Join('\n', details), false, FormatJson);
    }

    private static AppLogEntry? ParseEcs(JsonElement root)
    {
        if (!DateTimeOffset.TryParse(Str(root, "@timestamp"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var timestamp))
            return null;

        var level = Str(root, "log.level")?.ToLowerInvariant() switch
        {
            "trace" => AppLogLevel.Verbose,
            "debug" => AppLogLevel.Debug,
            "warn" => AppLogLevel.Warning,
            "error" => AppLogLevel.Error,
            "fatal" => AppLogLevel.Fatal,
            _ => AppLogLevel.Information,
        };

        var source = "NodePilot";
        var details = new List<string>();
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var stack = Str(error, "stack_trace") ?? Str(error, "message");
            if (!string.IsNullOrEmpty(stack)) details.Add(stack);
        }
        if (root.TryGetProperty("nodepilot", out var np) && np.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in np.EnumerateObject())
            {
                if (p.Name == "source_context" && p.Value.ValueKind == JsonValueKind.String)
                    source = p.Value.GetString() ?? source;
                else if (!NoisyProperties.Contains(p.Name))
                    details.Add($"{p.Name}={RenderJsonValue(p.Value)}");
            }
        }

        return new AppLogEntry(timestamp, level, source, Str(root, "message") ?? "", null,
            details.Count == 0 ? null : string.Join('\n', details), false, FormatEcs);
    }

    /// <summary>Fills a Serilog message template from the event's top-level JSON properties.</summary>
    private static string RenderTemplate(string template, JsonElement root)
    {
        MessageTemplate parsed;
        try { parsed = new MessageTemplateParser().Parse(template); }
        catch (ArgumentException) { return template; }

        var sb = new StringBuilder();
        foreach (var token in parsed.Tokens)
        {
            if (token is PropertyToken property && root.TryGetProperty(property.PropertyName, out var value))
                sb.Append(RenderJsonValue(value));
            else
                sb.Append(token);
        }
        return sb.ToString();
    }

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string RenderJsonValue(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
}
