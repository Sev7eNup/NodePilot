using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Engine.Activities;
using NodePilot.Engine.PowerShell;
using NodePilot.Core.Interfaces;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

/// <summary>
/// Tests for RunScriptActivity output-parameter extraction via the ###NODEPILOT_PARAMS### marker.
/// Some tests emit the marker manually to exercise extraction; others run through the real
/// wrapper path to verify auto-capture for both process and runspace engines.
/// </summary>
public class RunScriptOutputParametersTests
{
    private readonly RunScriptActivity _activity;

    public RunScriptOutputParametersTests()
    {
        _activity = new RunScriptActivity(
            new PowerShellEngineFactory(NullLoggerFactory.Instance),
            NullLogger<RunScriptActivity>.Instance);
    }

    private static StepExecutionContext Ctx()
        => new() { WorkflowExecutionId = Guid.NewGuid(), StepId = "step-1", Variables = new Dictionary<string, string>() };

    private static JsonElement Script(string script, string engine = "runspace")
        => JsonDocument.Parse($"{{\"script\": {System.Text.Json.JsonSerializer.Serialize(script)}, \"engine\": \"{engine}\"}}").RootElement;

    private const string Marker = "###NODEPILOT_PARAMS###";

    [Fact]
    public async Task Marker_SingleParam_ExtractedAsOutputParameter()
    {
        var result = await _activity.ExecuteAsync(Ctx(),
            Script($"Write-Output '{Marker}'; Write-Output '{{\"key\":\"value\"}}'"),
            CancellationToken.None);
        result.Success.Should().BeTrue();
        result.OutputParameters.Should().ContainKey("key");
        result.OutputParameters["key"].Should().Be("value");
    }

    [Fact]
    public async Task Marker_MultipleParams_AllExtracted()
    {
        var json = "{\"a\":\"alpha\",\"b\":\"beta\"}";
        var result = await _activity.ExecuteAsync(Ctx(),
            Script($"Write-Output '{Marker}'; Write-Output '{json}'"),
            CancellationToken.None);
        result.OutputParameters.Should().ContainKey("a").And.ContainKey("b");
        result.OutputParameters["a"].Should().Be("alpha");
        result.OutputParameters["b"].Should().Be("beta");
    }

    [Fact]
    public async Task Marker_OutputBeforeMarker_CleanedFromOutput()
    {
        var result = await _activity.ExecuteAsync(Ctx(),
            Script($"Write-Output 'user output'; Write-Output '{Marker}'; Write-Output '{{\"x\":\"1\"}}'"),
            CancellationToken.None);
        result.Output.Should().Contain("user output");
        result.Output.Should().NotContain(Marker);
        result.Output.Should().NotContain("{\"x\"");
    }

    [Fact]
    public async Task Marker_LastIndexOf_UsesLastOccurrence()
    {
        // Two markers in output — only the last one should count (last-write-wins)
        var result = await _activity.ExecuteAsync(Ctx(),
            Script($"Write-Output '{Marker}'; Write-Output '{{\"first\":\"1\"}}'; Write-Output '{Marker}'; Write-Output '{{\"second\":\"2\"}}'"),
            CancellationToken.None);
        result.OutputParameters.Should().ContainKey("second");
        result.OutputParameters.Should().NotContainKey("first");
    }

    [Fact]
    public async Task Marker_MalformedJson_NoUserParams()
    {
        var result = await _activity.ExecuteAsync(Ctx(),
            Script($"Write-Output '{Marker}'; Write-Output 'not-valid-json'"),
            CancellationToken.None);
        // Malformed JSON yields no user params; only the always-present exitCode remains.
        result.OutputParameters.Keys.Should().BeEquivalentTo(new[] { "exitCode" });
    }

    [Fact]
    public async Task Marker_EmptyJsonBlock_NoUserParams()
    {
        var result = await _activity.ExecuteAsync(Ctx(),
            Script($"Write-Output '{Marker}'; Write-Output '{{}}'"),
            CancellationToken.None);
        result.OutputParameters.Keys.Should().BeEquivalentTo(new[] { "exitCode" });
    }

    [Fact]
    public async Task NoMarker_ScriptOutput_NotParsedAsParams()
    {
        var result = await _activity.ExecuteAsync(Ctx(),
            Script("Write-Output 'just some output'"),
            CancellationToken.None);
        result.Output.Should().Contain("just some output");
        // No params marker → no user params; only the always-present exitCode remains.
        result.OutputParameters.Keys.Should().BeEquivalentTo(new[] { "exitCode" });
    }

    [Fact]
    public async Task AutoEngine_CountZero_StillExtractedAsOutputParameter()
    {
        var result = await _activity.ExecuteAsync(
            Ctx(),
            Script("$count = 0; $name = 'foo'", "auto"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters.Should().ContainKey("count").WhoseValue.Should().Be("0");
        result.OutputParameters.Should().ContainKey("name").WhoseValue.Should().Be("foo");
        result.Output.Should().BeEmpty();
    }

    [Fact]
    public async Task RunspaceEngine_CapturesWrapperOutputParameters()
    {
        var result = await _activity.ExecuteAsync(
            Ctx(),
            Script("$count = 0; $name = 'foo'", "runspace"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters.Should().ContainKey("count").WhoseValue.Should().Be("0");
        result.OutputParameters.Should().ContainKey("name").WhoseValue.Should().Be("foo");
        result.Output.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingScript_ReturnsFailure()
    {
        var result = await _activity.ExecuteAsync(Ctx(),
            JsonDocument.Parse("{\"engine\": \"runspace\"}").RootElement,
            CancellationToken.None);
        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("script");
    }
}
