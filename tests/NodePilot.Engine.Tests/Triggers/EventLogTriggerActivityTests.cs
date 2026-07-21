using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Triggers;
using Xunit;

namespace NodePilot.Engine.Tests.Triggers;

/// <summary>
/// Coverage for <see cref="EventLogTrigger"/> activity. Most tests focus on the
/// allow-list guard (L-11) and the orchestrator-fired pass-through — the real
/// EventLog query is OS-dependent and exercised via integration runs.
/// </summary>
public class EventLogTriggerActivityTests
{
    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    [Fact]
    public async Task Execute_OrchestratorFired_PassesThroughEventMetadata()
    {
        var trigger = new EventLogTrigger(EmptyConfig());
        var ctx = new StepExecutionContext
        {
            WorkflowExecutionId = Guid.NewGuid(),
            StepId = "trg",
            Variables = new Dictionary<string, string>
            {
                ["manual.eventId"] = "1001",
                ["manual.eventMessage"] = "Application Error: foo.exe crashed",
                ["manual.source"] = "ApplicationError",
            },
        };

        var result = await trigger.ExecuteAsync(ctx, Cfg("""{}"""), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("1001");
        result.Output.Should().Contain("Application Error: foo.exe crashed");
        result.OutputParameters.Should().ContainKey("eventId").WhoseValue.Should().Be("1001");
        result.OutputParameters.Should().ContainKey("eventMessage");
    }

    [Fact]
    public async Task Execute_ManualRun_LogNotInAllowList_ReturnsValidationFailure()
    {
        // L-11 guard: opening the Security log unprivileged is itself a recon signal,
        // so the trigger must reject any log not in the allow-list before touching the OS.
        var trigger = new EventLogTrigger(EmptyConfig());
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "trg" };

        var result = await trigger.ExecuteAsync(
            ctx, Cfg("""{"logName":"Security"}"""), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("not in the allow-list");
        result.ErrorOutput.Should().Contain("Security");
    }

    [Fact]
    public async Task Execute_ManualRun_AllowListExtendedViaConfig_PermitsLog()
    {
        // Operator extends the allow-list to include Setup; the trigger must accept it
        // (we can't assert a specific row count because Setup may be empty, but reaching
        // the EventLog read path means the allow-list passed).
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Trigger:EventLog:AllowedLogs:0"] = "Setup",
            ["Trigger:EventLog:AllowedLogs:1"] = "Application",
        }).Build();
        var trigger = new EventLogTrigger(config);
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "trg" };

        var result = await trigger.ExecuteAsync(
            ctx, Cfg("""{"logName":"Setup"}"""), CancellationToken.None);

        // Either successfully read (empty result) or a graceful "Event Log error:" — the
        // critical assertion is that the allow-list rejection isn't the failure cause.
        if (!result.Success)
        {
            result.ErrorOutput.Should().NotContain("not in the allow-list");
        }
    }

    [Fact]
    public async Task Execute_ManualRun_ApplicationLog_DefaultAllowListAccepts()
    {
        // Application is in the default allow-list — should not be rejected by the L-11 guard.
        var trigger = new EventLogTrigger(EmptyConfig());
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "trg" };

        var result = await trigger.ExecuteAsync(
            ctx,
            Cfg("""{"logName":"Application","level":"error","lookbackMinutes":1}"""),
            CancellationToken.None);

        if (!result.Success)
        {
            result.ErrorOutput.Should().NotContain("not in the allow-list");
        }
        else
        {
            // The output schema must contain the metadata header even when there are no matches.
            result.Output.Should().Contain("Event Log: Application");
        }
    }

    [Fact]
    public void ActivityType_IsEventLogTrigger() =>
        new EventLogTrigger(EmptyConfig()).ActivityType.Should().Be("eventLogTrigger");
}
