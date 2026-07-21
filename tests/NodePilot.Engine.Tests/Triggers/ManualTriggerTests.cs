using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Triggers;
using Xunit;

namespace NodePilot.Engine.Tests.Triggers;

public class ManualTriggerTests
{
    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Execute_ResolvesParameterValues_FromManualPrefix()
    {
        // ManualTrigger reads the schema from config.parameters and pulls the actual values
        // from context.Variables under "manual.<name>". Each parameter ends up in
        // OutputParameters under its bare name for {{varName.param.X}} access downstream.
        var trigger = new ManualTrigger();
        var ctx = new StepExecutionContext
        {
            Variables =
            {
                ["manual.serverName"] = "web01",
                ["manual.action"] = "restart",
            },
        };
        var config = Cfg("""
        {
          "parameters": [
            { "name": "serverName", "type": "string", "required": true },
            { "name": "action", "type": "string", "required": true }
          ]
        }
        """);

        var result = await trigger.ExecuteAsync(ctx, config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters["serverName"].Should().Be("web01");
        result.OutputParameters["action"].Should().Be("restart");
    }

    [Fact]
    public async Task Execute_OutputIsJsonObjectOfParameters()
    {
        // The Output stream surfaces all parameter values as a JSON object — downstream
        // steps that need the bag whole (instead of named picks) read it via
        // {{varName.output}}. Pin the JSON shape so a refactor doesn't silently break that.
        var trigger = new ManualTrigger();
        var ctx = new StepExecutionContext { Variables = { ["manual.foo"] = "bar" } };
        var config = Cfg("""
        {
          "parameters": [{ "name": "foo", "type": "string", "required": false }]
        }
        """);

        var result = await trigger.ExecuteAsync(ctx, config, CancellationToken.None);

        result.Output.Should().NotBeNull();
        var parsed = JsonDocument.Parse(result.Output!);
        parsed.RootElement.GetProperty("foo").GetString().Should().Be("bar");
    }

    [Fact]
    public async Task Execute_FillsDefaultsForOptionalParameters()
    {
        var trigger = new ManualTrigger();
        var ctx = new StepExecutionContext(); // no manual.* variables at all
        var config = Cfg("""
        {
          "parameters": [
            { "name": "env", "type": "string", "required": false, "default": "production" }
          ]
        }
        """);

        var result = await trigger.ExecuteAsync(ctx, config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters["env"].Should().Be("production");
    }

    [Fact]
    public async Task Execute_FailsFast_WhenRequiredParameterMissing()
    {
        // Missing AND empty required parameters both fail — protects downstream activities
        // that would template the value as a single-quoted PowerShell string and silently
        // execute with the empty placeholder.
        var trigger = new ManualTrigger();
        var ctx = new StepExecutionContext(); // missing the required "userId"
        var config = Cfg("""
        {
          "parameters": [{ "name": "userId", "type": "string", "required": true }]
        }
        """);

        var result = await trigger.ExecuteAsync(ctx, config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("userId");
        result.ErrorOutput.Should().Contain("missing");
    }

    [Fact]
    public async Task Execute_NoParametersInConfig_StillSucceeds_WithEmptyJson()
    {
        var trigger = new ManualTrigger();
        var ctx = new StepExecutionContext();

        var result = await trigger.ExecuteAsync(ctx, Cfg("""{"title":"My Workflow"}"""), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().NotBeNull();
        var parsed = JsonDocument.Parse(result.Output!);
        parsed.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        parsed.RootElement.EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_RequiredEmptyString_FailsValidation()
    {
        // CLAUDE.md call-out: manualTrigger params must be String types with String defaults.
        // An explicitly-empty value supplied at fire time should be treated as missing,
        // because string templating downstream cannot tell the difference and would render
        // a bare quote-pair on the right-hand side of an assignment.
        var trigger = new ManualTrigger();
        var ctx = new StepExecutionContext { Variables = { ["manual.token"] = "" } };
        var config = Cfg("""
        {
          "parameters": [{ "name": "token", "type": "string", "required": true }]
        }
        """);

        var result = await trigger.ExecuteAsync(ctx, config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("token");
    }

    [Fact]
    public void ActivityType_IsManualTrigger() =>
        new ManualTrigger().ActivityType.Should().Be("manualTrigger");
}
