using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Activities;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

/// <summary>
/// The unified, error-based success model for <c>runScript</c>: a step fails only on a
/// terminating PowerShell error (throw / Write-Error under Stop), NOT on an explicit <c>exit N</c>
/// — consistently across the in-process runspace and the out-of-process engine. Plus the opt-in
/// <c>successExitCodes</c> gate and the always-present <c>param.exitCode</c>.
/// </summary>
public class RunScriptSuccessSemanticsTests
{
    private readonly RunScriptActivity _activity = new(
        new PowerShellEngineFactory(NullLoggerFactory.Instance),
        NullLogger<RunScriptActivity>.Instance);

    private static StepExecutionContext Ctx()
        => new() { WorkflowExecutionId = Guid.NewGuid(), StepId = "step-1", Variables = new Dictionary<string, string>() };

    private static JsonElement Config(string script, string engine = "runspace", string? successExitCodes = null)
    {
        var sec = successExitCodes is null ? "" : $", \"successExitCodes\": \"{successExitCodes}\"";
        return JsonDocument.Parse(
            $"{{\"script\": {JsonSerializer.Serialize(script)}, \"engine\": \"{engine}\"{sec}}}").RootElement;
    }

    [Theory]
    [InlineData("runspace")]
    [InlineData("powershell")]
    public async Task ExitNonZero_DefaultErrorBased_StepSucceeds(string engine)
    {
        // The headline fix: `exit N` is NOT a failure by default — consistent across engines.
        var result = await _activity.ExecuteAsync(Ctx(), Config("Write-Output 'hi'; exit 1", engine), CancellationToken.None);
        result.Success.Should().BeTrue($"exit 1 must not fail the step on the {engine} engine");
    }

    [Theory]
    [InlineData("runspace")]
    [InlineData("powershell")]
    public async Task Throw_FailsAndStripsErrorMarker(string engine)
    {
        var result = await _activity.ExecuteAsync(Ctx(), Config("throw 'boom'", engine), CancellationToken.None);
        result.Success.Should().BeFalse($"a terminating error must fail the step on the {engine} engine");
        result.ErrorOutput.Should().NotBeNullOrEmpty();
        (result.Output ?? "").Should().NotContain(PowerShellScriptWrapper.ErrorMarker, "the control marker must be stripped from Output");
    }

    [Theory]
    [InlineData("runspace")]
    [InlineData("powershell")]
    public async Task WriteError_UnderStop_Fails(string engine)
    {
        // The wrapper sets $ErrorActionPreference='Stop', so Write-Error is terminating in both engines.
        var result = await _activity.ExecuteAsync(Ctx(), Config("Write-Error 'nope'", engine), CancellationToken.None);
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task SuccessExitCodes_Zero_ExitOne_FailsOnProcess()
    {
        var result = await _activity.ExecuteAsync(Ctx(), Config("exit 1", "powershell", successExitCodes: "0"), CancellationToken.None);
        result.Success.Should().BeFalse("successExitCodes:\"0\" re-enables exit-based failure on the process engine");
    }

    [Fact]
    public async Task SuccessExitCodes_Zero_ExitZero_Succeeds()
    {
        var result = await _activity.ExecuteAsync(Ctx(), Config("exit 0", "powershell", successExitCodes: "0"), CancellationToken.None);
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task SuccessExitCodes_ZeroOne_ExitOne_Succeeds()
    {
        var result = await _activity.ExecuteAsync(Ctx(), Config("exit 1", "powershell", successExitCodes: "0,1"), CancellationToken.None);
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task SuccessExitCodes_Unset_ExitSeven_Succeeds()
    {
        // Guard against accidentally defaulting to {0} like StartProgram (unset = no gating).
        var result = await _activity.ExecuteAsync(Ctx(), Config("exit 7", "powershell"), CancellationToken.None);
        result.Success.Should().BeTrue();
    }

    [Theory]
    [InlineData("runspace")]
    [InlineData("powershell")]
    public async Task NativeCommandExitCode_CapturedConsistently(string engine)
    {
        // $LASTEXITCODE of the last native command is captured by the wrapper → consistent across
        // engines. Disable PS7's native-command error preference so the non-zero native exit is a
        // value, not a terminating error (no-op on Windows PowerShell 5.1).
        var result = await _activity.ExecuteAsync(
            Ctx(),
            Config("$PSNativeCommandUseErrorActionPreference = $false; cmd /c exit 3", engine),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters.Should().ContainKey("exitCode").WhoseValue.Should().Be("3");
    }

    [Fact]
    public async Task ScriptLevelExit_ExitCodeParam_ProcessSeesValue_RunspaceSeesZero()
    {
        // A script-level `exit 5` is only observable as a real exit code on the process engine.
        var proc = await _activity.ExecuteAsync(Ctx(), Config("exit 5", "powershell"), CancellationToken.None);
        proc.OutputParameters.Should().ContainKey("exitCode").WhoseValue.Should().Be("5");

        var run = await _activity.ExecuteAsync(Ctx(), Config("exit 5", "runspace"), CancellationToken.None);
        run.OutputParameters.Should().ContainKey("exitCode").WhoseValue.Should().Be("0",
            "the in-process runspace cannot observe a script-level exit code");
    }

    [Fact]
    public async Task Throw_WithTranscript_FailsAndOutputHasNoMarkers()
    {
        var config = JsonDocument.Parse(
            $"{{\"script\": {JsonSerializer.Serialize("Write-Output 'before'; throw 'boom'")}, \"engine\": \"runspace\", \"transcript\": true}}").RootElement;

        var result = await _activity.ExecuteAsync(Ctx(), config, CancellationToken.None);

        result.Success.Should().BeFalse();
        (result.Output ?? "").Should().NotContain("###NODEPILOT_", "all control markers must be stripped even on a throw-with-transcript");
    }
}
