using FluentAssertions;
using NodePilot.Api.Diagnostics;
using NodePilot.Core.Models;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace NodePilot.Api.Tests.Logging;

/// <summary>
/// Pins the sink's property extraction: every column on <see cref="SupportEvent"/> must
/// arrive from the Serilog event in the expected shape. If these tests break, the web
/// viewer loses the ability to index its filters.
/// </summary>
public class SupportEventDbSinkTests
{
    private static LogEvent BuildEvent(LogEventLevel level, string template, params LogEventProperty[] props)
    {
        var parsed = new MessageTemplateParser().Parse(template);
        return new LogEvent(
            DateTimeOffset.UtcNow,
            level, exception: null, parsed,
            props);
    }

    private static LogEventProperty P(string name, object value) =>
        new(name, new ScalarValue(value));

    [Fact]
    public void Emit_ExtractsKnownProperties_IntoColumns()
    {
        var channel = new SupportEventChannel();
        var sink = new SupportEventDbSink(channel);
        var execId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var ev = BuildEvent(LogEventLevel.Warning, "STEP_FAILED",
            P("support.event_type", "STEP_FAILED"),
            P("workflow_execution_id", execId),
            P("workflow_id", workflowId),
            P("workflow_name", "Daily Report"),
            P("step_id", "fetchData"),
            P("step_label", "Fetch Data"),
            P("activity_type", "runScript"),
            P("user.id", userId.ToString()),
            P("user.name", "alice"),
            P("trace_id", "0123456789abcdef0123456789abcdef"));

        sink.Emit(ev);

        channel.Reader.TryRead(out var row).Should().BeTrue();
        row!.EventType.Should().Be("STEP_FAILED");
        row.Level.Should().Be((int)LogEventLevel.Warning);
        row.ExecutionId.Should().Be(execId);
        row.ExecutionShort.Should().Be(execId.ToString("N")[..8]);
        row.WorkflowId.Should().Be(workflowId);
        row.WorkflowName.Should().Be("Daily Report");
        row.StepId.Should().Be("fetchData");
        row.StepLabel.Should().Be("Fetch Data");
        row.ActivityType.Should().Be("runScript");
        row.UserId.Should().Be(userId);
        row.UserName.Should().Be("alice");
        row.TraceId.Should().Be("0123456789abcdef0123456789abcdef");
    }

    [Fact]
    public void Emit_UnknownProperties_LandInPropertiesJson()
    {
        var channel = new SupportEventChannel();
        var sink = new SupportEventDbSink(channel);

        var ev = BuildEvent(LogEventLevel.Information, "EXECUTION_SUCCEEDED",
            P("support.event_type", "EXECUTION_SUCCEEDED"),
            P("workflow_execution_id", Guid.NewGuid()),
            // Long-tail properties — expected to be serialized into PropertiesJson
            P("duration_sec", 3.7),
            P("steps_ok", 4),
            P("steps_failed", 0));

        sink.Emit(ev);

        channel.Reader.TryRead(out var row).Should().BeTrue();
        row!.PropertiesJson.Should().NotBeNull();
        row.PropertiesJson.Should().Contain("duration_sec");
        row.PropertiesJson.Should().Contain("steps_ok");
        // Known properties must NOT land in PropertiesJson — they have their own dedicated columns
        row.PropertiesJson.Should().NotContain("support.event_type");
        row.PropertiesJson.Should().NotContain("workflow_execution_id");
    }

    [Fact]
    public void Emit_MissingEventType_DefaultsToUnknown()
    {
        var channel = new SupportEventChannel();
        var sink = new SupportEventDbSink(channel);

        var ev = BuildEvent(LogEventLevel.Information, "no event_type set");
        sink.Emit(ev);

        channel.Reader.TryRead(out var row).Should().BeTrue();
        row!.EventType.Should().Be("UNKNOWN");
    }

    [Fact]
    public void Emit_ChannelFull_DropsAndDoesNotThrow()
    {
        var channel = new SupportEventChannel();
        var sink = new SupportEventDbSink(channel);

        // Fill the channel beyond its 1024 capacity. The extra events are allowed to be
        // dropped, but Emit must always return without throwing.
        for (int i = 0; i < 2000; i++)
        {
            var ev = BuildEvent(LogEventLevel.Information, "ev",
                P("support.event_type", "USER_LOG"));
            sink.Emit(ev);
        }

        // The channel can hold at most 1024 events — everything above that was dropped.
        int drained = 0;
        while (channel.Reader.TryRead(out _)) drained++;
        drained.Should().Be(1024);
    }

    [Fact]
    public void Emit_LongMessage_TruncatedTo8KiB()
    {
        var channel = new SupportEventChannel();
        var sink = new SupportEventDbSink(channel);

        var giantTemplate = new string('x', 12_000);
        var ev = BuildEvent(LogEventLevel.Information, giantTemplate,
            P("support.event_type", "USER_LOG"));
        sink.Emit(ev);

        channel.Reader.TryRead(out var row).Should().BeTrue();
        row!.Message.Length.Should().BeLessThanOrEqualTo(8000);
        row.Message.Should().EndWith("…[truncated]");
    }
}
