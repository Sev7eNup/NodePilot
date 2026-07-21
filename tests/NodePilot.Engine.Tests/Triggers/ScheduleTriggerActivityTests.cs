using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Triggers;
using Xunit;

namespace NodePilot.Engine.Tests.Triggers;

/// <summary>
/// IActivityExecutor side of <see cref="ScheduleTrigger"/> — i.e. how the trigger node
/// behaves when the engine actually runs it (vs. how the Quartz source schedules it).
/// </summary>
public class ScheduleTriggerActivityTests
{
    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Execute_FailsFast_WhenCronExpressionMissing()
    {
        // Defensive: the scheduler source already validates this, but a hand-crafted
        // execution that bypasses the source (e.g. test runner) must still fail visibly.
        var trigger = new ScheduleTrigger();

        var result = await trigger.ExecuteAsync(new StepExecutionContext(), Cfg("""{}"""), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("cron");
    }

    [Fact]
    public async Task Execute_HappyPath_ReturnsCronAndDescriptionInOutput()
    {
        var trigger = new ScheduleTrigger();
        var config = Cfg("""{"cronExpression":"0 0 * * * ?","description":"Hourly housekeeping"}""");

        var result = await trigger.ExecuteAsync(new StepExecutionContext(), config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("0 0 * * * ?");
        result.Output.Should().Contain("Hourly housekeeping");
    }

    [Fact]
    public async Task Execute_ExtractsManualPrefixedVariablesIntoOutputParameters()
    {
        // The scheduler source pushes "firedAt" / "nextFireAt" as manual.* into the
        // execution variables. The activity must surface them under bare names so the
        // workflow can use {{step.param.firedAt}} downstream.
        var trigger = new ScheduleTrigger();
        var ctx = new StepExecutionContext
        {
            Variables =
            {
                ["manual.firedAt"] = "2026-04-26T10:00:00Z",
                ["manual.nextFireAt"] = "2026-04-26T11:00:00Z",
                ["other.unrelated"] = "ignored",
            },
        };

        var result = await trigger.ExecuteAsync(ctx, Cfg("""{"cronExpression":"0 0 * * * ?"}"""), CancellationToken.None);

        result.OutputParameters.Should().ContainKey("firedAt").WhoseValue.Should().Be("2026-04-26T10:00:00Z");
        result.OutputParameters.Should().ContainKey("nextFireAt");
        result.OutputParameters.Should().NotContainKey("other.unrelated");
    }

    [Fact]
    public void ActivityType_IsScheduleTrigger() =>
        new ScheduleTrigger().ActivityType.Should().Be("scheduleTrigger");
}
