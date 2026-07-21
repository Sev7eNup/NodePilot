using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Triggers;
using Xunit;

namespace NodePilot.Engine.Tests.Triggers;

public class WebhookTriggerTests
{
    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Execute_ExtractsManualPrefixedVariablesIntoOutputParameters()
    {
        var trigger = new WebhookTrigger();
        var ctx = new StepExecutionContext
        {
            Variables =
            {
                ["manual.webhookBody"] = "{\"hello\":\"world\"}",
                ["manual.webhookMethod"] = "POST",
                ["manual.webhookHeader_X-GitHub-Event"] = "push",
                ["someOther.var"] = "ignored",
            },
        };

        var result = await trigger.ExecuteAsync(ctx, Cfg("""{"path":"hook","method":"POST"}"""), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters.Should().ContainKey("webhookBody")
            .WhoseValue.Should().Be("{\"hello\":\"world\"}");
        result.OutputParameters.Should().ContainKey("webhookMethod");
        result.OutputParameters.Should().ContainKey("webhookHeader_X-GitHub-Event");
        result.OutputParameters.Should().NotContainKey("someOther.var",
            "non-manual-prefixed variables must not be exposed as webhook output parameters");
    }

    [Fact]
    public async Task Execute_NoBodyVariable_FallsBackToPlaceholder()
    {
        var trigger = new WebhookTrigger();
        var ctx = new StepExecutionContext { Variables = { ["manual.webhookMethod"] = "POST" } };

        var result = await trigger.ExecuteAsync(ctx, Cfg("""{"path":"hook"}"""), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("(no body)");
    }

    [Fact]
    public async Task Execute_OutputContainsConfiguredMethodAndPath()
    {
        var trigger = new WebhookTrigger();
        var ctx = new StepExecutionContext { Variables = { ["manual.webhookBody"] = "x" } };

        var result = await trigger.ExecuteAsync(ctx, Cfg("""{"path":"github-push","method":"POST"}"""), CancellationToken.None);

        result.Output.Should().Contain("POST");
        result.Output.Should().Contain("github-push");
        result.Output.Should().Contain("Body: x");
    }

    [Fact]
    public async Task Execute_PathMissing_FallsBackToWorkflowExecutionId()
    {
        var trigger = new WebhookTrigger();
        var execId = Guid.NewGuid();
        var ctx = new StepExecutionContext { WorkflowExecutionId = execId };

        var result = await trigger.ExecuteAsync(ctx, Cfg("""{}"""), CancellationToken.None);

        result.Output.Should().Contain(execId.ToString());
    }

    [Fact]
    public async Task Execute_PrefixIsCaseInsensitive()
    {
        // Source can lowercase or mixed-case the "manual." prefix; both must be picked up.
        var trigger = new WebhookTrigger();
        var ctx = new StepExecutionContext
        {
            Variables =
            {
                ["MANUAL.upperKey"] = "u",
                ["Manual.MixedKey"] = "m",
                ["manual.lowerKey"] = "l",
            },
        };

        var result = await trigger.ExecuteAsync(ctx, Cfg("""{}"""), CancellationToken.None);

        result.OutputParameters.Should().ContainKey("upperKey");
        result.OutputParameters.Should().ContainKey("MixedKey");
        result.OutputParameters.Should().ContainKey("lowerKey");
    }

    [Fact]
    public async Task Execute_ActivityTypeIsWebhookTrigger()
    {
        new WebhookTrigger().ActivityType.Should().Be("webhookTrigger");
    }
}
