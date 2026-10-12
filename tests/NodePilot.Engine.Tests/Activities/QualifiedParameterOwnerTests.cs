using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Activities;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
using NodePilot.Engine.Activities;
using NodePilot.Engine.Execution;
using NodePilot.Engine.PowerShell;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

public class QualifiedParameterOwnerTests
{
    private static StepExecutionContext Context()
    {
        var previous = new Dictionary<string, ActivityResult>
        {
            ["first"] = new() { Success = true, OutputParameters = new() { ["hostName"] = "ServerA" } },
            ["lookup"] = new() { Success = true, Output = "[]" },
        };
        return new StepExecutionContext
        {
            WorkflowExecutionId = Guid.NewGuid(),
            StepId = "consumer",
            Variables = VariableResolver.BuildStepVariables(null, new Dictionary<string, string>(), previous,
                new Dictionary<string, string> { ["first"] = "source", ["lookup"] = "target" }),
        };
    }

    [Theory]
    [InlineData("lookup")]
    [InlineData("target")]
    public async Task RunScript_MissingQualifiedParameter_DoesNotBorrowAnotherAncestorsValue(string owner)
    {
        var context = Context();
        context.Variables["hostName"].Should().Be("ServerA");
        var placeholder = "{{" + owner + ".param.hostName}}";
        var activity = new RunScriptActivity(new PowerShellEngineFactory(NullLoggerFactory.Instance),
            NullLogger<RunScriptActivity>.Instance);

        var result = await activity.ExecuteAsync(context, JsonSerializer.SerializeToElement(new
        {
            script = "Write-Output '" + placeholder + "'; Write-Output $hostName; Write-Output {{source.param.hostName}}",
            engine = "runspace",
        }), CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorOutput);
        result.Output!.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Should().Equal(placeholder, "ServerA", "ServerA");
    }

    [Theory]
    [InlineData("lookup")]
    [InlineData("target")]
    public async Task CustomInput_MissingQualifiedParameter_DoesNotBorrowAnotherAncestorsValue(string owner)
    {
        await using var db = TestDbFactory.Create();
        var store = new CustomActivityDefinitionStore(db);
        var definition = await store.CreateAsync(new CustomActivityDefinitionInput
        {
            Key = "echo_target",
            Name = "Echo target",
            Engine = "runspace",
            ScriptTemplate = "Write-Output $destination; Write-Output $hostName",
            InputParametersJson = CustomActivityParameters.Serialize(
                new[] { new CustomActivityInputParameter("destination", "Destination", "string", Required: true) }),
        }, "test", CancellationToken.None);
        await store.SetEnabledAsync(definition.Id, true, definition.ConcurrencyToken, "test", CancellationToken.None);
        var activity = new CustomActivityExecutor(store, new PowerShellEngineFactory(NullLoggerFactory.Instance),
            NullLogger<CustomActivityExecutor>.Instance);
        var placeholder = "{{" + owner + ".param.hostName}}";

        var result = await activity.ExecuteAsync(Context(), JsonSerializer.SerializeToElement(new
        {
            __customDefinitionId = definition.Id,
            __customKey = definition.Key,
            destination = placeholder,
        }), CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorOutput);
        result.Output!.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Should().Equal(placeholder, "ServerA");
    }
}
