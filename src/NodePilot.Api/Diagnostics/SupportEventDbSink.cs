using System.Text.Json;
using NodePilot.Core.Models;
using NodePilot.Engine;
using Serilog.Core;
using Serilog.Events;

namespace NodePilot.Api.Diagnostics;

/// <summary>
/// Custom Serilog sink: consumes <see cref="LogEvent"/>s that carry the <c>SupportLog=true</c>
/// property (the filter that selects them is set up before the sink in
/// <see cref="NodePilot.Api.Hosting.LoggingSetup"/>), extracts the structured fields, and pushes a
/// <see cref="SupportEvent"/> row into the shared <see cref="SupportEventChannel"/>. Non-blocking.
///
/// <para>The mapping is conservative: known properties land in dedicated columns, everything
/// else (the long tail) gets redacted, size-capped, and serialized as JSON into
/// <c>PropertiesJson</c>. Values are converted to strings via <see cref="ExtractScalarString"/>
/// so the JSON schema stays stable — Serilog's internal value types
/// (<c>ScalarValue</c>, <c>SequenceValue</c>, ...) shouldn't be the UI client's problem.</para>
/// </summary>
internal sealed class SupportEventDbSink : ILogEventSink
{
    private readonly SupportEventChannel _channel;

    /// <summary>
    /// Known property names that land in dedicated columns. Everything else goes into
    /// <c>PropertiesJson</c>. Case-sensitive (Serilog properties are case-sensitive).
    /// </summary>
    private static readonly HashSet<string> KnownProperties = new(StringComparer.Ordinal)
    {
        "SupportLog",         // the marker property itself — redundant once the filter has run
        "support.event_type",
        "support.message",    // projected into the Message column, not into PropertiesJson
        "workflow_execution_id",
        "workflow_id",
        "workflow_name",
        "step_id",
        "step_label",
        "activity_type",
        "user.id",
        "user.name",
        "trace_id",
        "span_id",
        "TraceId",            // OtelTagEnricher writes PascalCase aliases
        "SpanId",
    };

    private const int MaxMessageChars = 8000;
    private const int MaxPropertiesJsonChars = 8000;

    public SupportEventDbSink(SupportEventChannel channel)
    {
        _channel = channel;
    }

    public void Emit(LogEvent logEvent)
    {
        try
        {
            var ev = BuildEvent(logEvent);
            if (!_channel.TryWrite(ev))
            {
                EngineMetrics.SupportEventsDropped.Add(1,
                    new KeyValuePair<string, object?>("reason", "channel_full"));
            }
        }
        catch
        {
            // A sink failure must never abort the logging pipeline call — otherwise a bug in
            // the property extraction would also take down the main file sink (plain text).
            // Drop and move on.
            EngineMetrics.SupportEventsDropped.Add(1,
                new KeyValuePair<string, object?>("reason", "sink_error"));
        }
    }

    private static SupportEvent BuildEvent(LogEvent logEvent)
    {
        // Clean message: if the source explicitly set support.message, use that (so the Message
        // column only holds the human-relevant wording, not workflow/execution/step values that
        // already have their own columns). Fall back to the rendered template for sources that
        // didn't set support.message (third-party code, or a BeginScope with SupportLog=true
        // but no clean message of its own).
        var supportMessage = ExtractScalarString(logEvent, "support.message");

        var ev = new SupportEvent
        {
            Id = Guid.NewGuid(),
            Timestamp = logEvent.Timestamp.UtcDateTime,
            Level = (int)logEvent.Level,
            EventType = ExtractScalarString(logEvent, "support.event_type") ?? "UNKNOWN",
            Message = Truncate(supportMessage ?? logEvent.RenderMessage(), MaxMessageChars),
            WorkflowId = ExtractGuid(logEvent, "workflow_id"),
            WorkflowName = ExtractScalarString(logEvent, "workflow_name"),
            ExecutionId = ExtractGuid(logEvent, "workflow_execution_id"),
            StepId = ExtractScalarString(logEvent, "step_id"),
            StepLabel = ExtractScalarString(logEvent, "step_label"),
            ActivityType = ExtractScalarString(logEvent, "activity_type"),
            UserName = ExtractScalarString(logEvent, "user.name"),
            UserId = ExtractGuid(logEvent, "user.id"),
            TraceId = ExtractScalarString(logEvent, "trace_id") ?? ExtractScalarString(logEvent, "TraceId"),
            SpanId = ExtractScalarString(logEvent, "span_id") ?? ExtractScalarString(logEvent, "SpanId"),
        };

        // 8-hex-char prefix for UI grouping. Already available inline in the message template
        // as ExecutionShort; denormalized here into its own column for sortable display.
        if (ev.ExecutionId is { } id)
            ev.ExecutionShort = id.ToString("N")[..8];

        ev.PropertiesJson = SerializeLongTail(logEvent);
        return ev;
    }

    private static string? ExtractScalarString(LogEvent logEvent, string name)
    {
        if (!logEvent.Properties.TryGetValue(name, out var prop)) return null;
        if (prop is ScalarValue { Value: { } v }) return v.ToString();
        return null;
    }

    private static Guid? ExtractGuid(LogEvent logEvent, string name)
    {
        if (!logEvent.Properties.TryGetValue(name, out var prop)) return null;
        if (prop is ScalarValue { Value: Guid g }) return g;
        if (prop is ScalarValue { Value: string s } && Guid.TryParse(s, out var parsed)) return parsed;
        return null;
    }

    private static string? SerializeLongTail(LogEvent logEvent)
    {
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in logEvent.Properties)
        {
            if (KnownProperties.Contains(key)) continue;
            bag[key] = FlattenScalar(value);
        }
        if (bag.Count == 0) return null;

        var json = JsonSerializer.Serialize(bag);
        return Truncate(json, MaxPropertiesJsonChars);
    }

    private static object? FlattenScalar(LogEventPropertyValue value) => value switch
    {
        ScalarValue { Value: null } => null,
        ScalarValue scalar => scalar.Value switch
        {
            string s => s,
            bool b => b,
            int i => i,
            long l => l,
            double d => d,
            decimal dec => dec,
            DateTime dt => dt,
            DateTimeOffset dto => dto,
            Guid g => g.ToString(),
            _ => scalar.Value.ToString(),
        },
        // Sequence/Structure/Dictionary values are reduced to a string — we don't want
        // arbitrary-shaped JSON in this column. The operator can see the raw data in the
        // main log or the OTLP stream if more detail is needed.
        _ => value.ToString(),
    };

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Length > max ? s.Substring(0, max - 12) + "…[truncated]" : s;
    }
}
