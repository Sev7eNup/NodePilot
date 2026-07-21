using System.Diagnostics;
using FluentAssertions;
using NodePilot.Api.Logging;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;
using NodePilot.Core.Telemetry;

namespace NodePilot.Api.Tests.Logging;

/// <summary>
/// Verifies that OtelTagEnricher pulls the correlation IDs off Activity.Current and
/// surfaces them as Serilog event properties — without this, log lines from code paths
/// that don't manually open a BeginScope can't be correlated to the workflow run.
/// </summary>
public class OtelTagEnricherTests
{
    private static readonly ActivitySource _src = new("NodePilot.Tests.OtelTagEnricher");

    private static LogEvent EmptyEvent() =>
        new(DateTimeOffset.UtcNow, LogEventLevel.Information, null,
            new MessageTemplate(Array.Empty<MessageTemplateToken>()),
            Array.Empty<LogEventProperty>());

    static OtelTagEnricherTests()
    {
        // ActivitySource only emits when at least one listener is attached. Without
        // this listener Activity.Current would always be null in tests.
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        });
    }

    [Fact]
    public void Enrich_NoActivity_NoProperties()
    {
        Activity.Current = null;
        var ev = EmptyEvent();
        var factory = new TestPropertyFactory();

        new OtelTagEnricher().Enrich(ev, factory);

        ev.Properties.Should().BeEmpty();
    }

    [Fact]
    public void Enrich_ActivityWithIds_AddsAllExpectedProperties()
    {
        using var activity = _src.StartActivity("test-step");
        activity.Should().NotBeNull("activity listener is attached");
        activity!.SetTag(TelemetryConstants.Attributes.WorkflowId, "wf-123");
        activity.SetTag(TelemetryConstants.Attributes.ExecutionId, "exec-abc");
        activity.SetTag(TelemetryConstants.Attributes.StepId, "step-9");
        activity.SetTag(TelemetryConstants.Attributes.WorkflowCallDepth, 2);

        var ev = EmptyEvent();
        var factory = new TestPropertyFactory();

        new OtelTagEnricher().Enrich(ev, factory);

        ev.Properties.Should().ContainKey("workflow_id");
        ev.Properties["workflow_id"].ToString().Should().Contain("wf-123");
        ev.Properties.Should().ContainKey("execution_id");
        ev.Properties["execution_id"].ToString().Should().Contain("exec-abc");
        ev.Properties.Should().ContainKey("step_id");
        ev.Properties["step_id"].ToString().Should().Contain("step-9");
        ev.Properties.Should().ContainKey("call_depth");
        ev.Properties["call_depth"].ToString().Should().Contain("2");
        ev.Properties.Should().ContainKey("trace_id");
        ev.Properties.Should().ContainKey("span_id");
    }

    [Fact]
    public void Enrich_ActivityWithoutBusinessTags_OnlyTraceAndSpan()
    {
        using var activity = _src.StartActivity("bare-activity");
        activity.Should().NotBeNull();

        var ev = EmptyEvent();
        var factory = new TestPropertyFactory();

        new OtelTagEnricher().Enrich(ev, factory);

        ev.Properties.Should().ContainKey("trace_id");
        ev.Properties.Should().ContainKey("span_id");
        ev.Properties.Should().NotContainKey("workflow_id");
        ev.Properties.Should().NotContainKey("execution_id");
        ev.Properties.Should().NotContainKey("step_id");
        ev.Properties.Should().NotContainKey("call_depth");
    }

    [Fact]
    public void Enrich_AddPropertyIfAbsent_DoesNotOverwrite()
    {
        using var activity = _src.StartActivity("override-test");
        activity.Should().NotBeNull();
        activity!.SetTag(TelemetryConstants.Attributes.WorkflowId, "wf-from-tag");

        var factory = new TestPropertyFactory();
        // Pre-populate workflow_id so the enricher's AddPropertyIfAbsent must respect it
        var ev = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null,
            new MessageTemplate(Array.Empty<MessageTemplateToken>()),
            new[] { new LogEventProperty("workflow_id", new ScalarValue("wf-from-scope")) });

        new OtelTagEnricher().Enrich(ev, factory);

        ev.Properties["workflow_id"].ToString().Should().Contain("wf-from-scope",
            "the enricher must not overwrite values pre-populated by an outer BeginScope");
    }

    private sealed class TestPropertyFactory : Serilog.Core.ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false) =>
            new(name, new ScalarValue(value));
    }
}
