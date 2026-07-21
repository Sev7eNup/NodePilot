using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;
using NodePilot.Core.Telemetry;

namespace NodePilot.Api.Logging;

/// <summary>
/// Pulls a small set of NodePilot-scoped OTel span tags off <see cref="Activity.Current"/>
/// and adds them to every Serilog event as properties. Without this enricher, log lines
/// emitted from code paths that don't manually open a <c>BeginScope({ run_id = … })</c>
/// have no way to be correlated back to the workflow run that triggered them — operators
/// looking at the rolling file see "scary error" but can't link it to an execution.
///
/// We only surface the IDs (workflow / execution / step / call_depth), not free-text
/// values like script bodies or output. The enricher is cheap: a missing
/// <c>Activity.Current</c> short-circuits, and tag lookups are O(1).
/// </summary>
internal sealed class OtelTagEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var activity = Activity.Current;
        if (activity is null) return;

        AddTag(logEvent, propertyFactory, activity, TelemetryConstants.Attributes.WorkflowId, "workflow_id");
        AddTag(logEvent, propertyFactory, activity, TelemetryConstants.Attributes.ExecutionId, "execution_id");
        AddTag(logEvent, propertyFactory, activity, TelemetryConstants.Attributes.StepId, "step_id");
        AddTag(logEvent, propertyFactory, activity, TelemetryConstants.Attributes.WorkflowCallDepth, "call_depth");

        if (!string.IsNullOrEmpty(activity.TraceId.ToString()))
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("trace_id", activity.TraceId.ToString()));
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("span_id", activity.SpanId.ToString()));
        }
    }

    private static void AddTag(LogEvent logEvent, ILogEventPropertyFactory factory,
        Activity activity, string tagName, string propertyName)
    {
        var value = activity.GetTagItem(tagName);
        if (value is null) return;
        logEvent.AddPropertyIfAbsent(factory.CreateProperty(propertyName, value.ToString()));
    }
}
