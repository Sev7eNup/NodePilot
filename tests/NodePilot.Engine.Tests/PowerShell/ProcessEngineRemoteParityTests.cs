using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Activities;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

/// <summary>
/// A local runScript in a Windows PowerShell process must end like the same script over WinRM:
/// any error record fails the step, output is each object's ToString(), host and warning output
/// are not output, and the error text is the plain message.
/// </summary>
public class ProcessEngineRemoteParityTests
{
    private readonly RunScriptActivity _activity = new(
        new PowerShellEngineFactory(NullLoggerFactory.Instance),
        NullLogger<RunScriptActivity>.Instance);

    private async Task<ActivityResult> RunAsync(string script, bool isolated, string? successExitCodes = null)
    {
        var context = new StepExecutionContext
        {
            WorkflowExecutionId = Guid.NewGuid(),
            StepId = "step-1",
            Variables = new Dictionary<string, string>(),
        };
        var config = JsonDocument.Parse(JsonSerializer.Serialize(
            new { script, engine = "powershell", isolated, successExitCodes })).RootElement;
        return await _activity.ExecuteAsync(context, config, TestContext.Current.CancellationToken);
    }

    private static string[] Lines(string? text)
        => (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonTerminatingError_FailsTheStep_AndKeepsTheRestOfTheScript(bool isolated)
    {
        var result = await RunAsync("$ErrorActionPreference = 'Continue'; Get-Item C:\\nope-np-parity; $after = 'y'", isolated);

        result.Success.Should().BeFalse();
        result.OutputParameters!["after"].Should().Be("y");
        result.ErrorOutput.Should().Contain("nope-np-parity");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeStderr_StopsTheScriptAndFailsTheStep(bool isolated)
    {
        // Over WinRM, Windows PowerShell turns native stderr into an error record, and the
        // wrapper's ErrorActionPreference 'Stop' ends the script there.
        var result = await RunAsync("cmd /c \"echo native-warn 1>&2\"; $after = 'y'", isolated);

        result.Success.Should().BeFalse();
        result.OutputParameters.Should().NotContainKey("after");
        result.ErrorOutput.Should().Contain("native-warn");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Output_IsEachObjectsToString_WithoutHostOrWarningLines(bool isolated)
    {
        var result = await RunAsync(
            "[pscustomobject]@{ a = 1 }; Write-Host 'host-line'; Write-Warning 'warn-line'; Write-Information 'info-line'; 'plain'; $null; 42",
            isolated);

        result.Success.Should().BeTrue(result.ErrorOutput);
        Lines(result.Output).Should().Equal("@{a=1}", "plain", "42");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Throw_ReportsOnlyTheMessage(bool isolated)
    {
        var result = await RunAsync("$x = 'a'\nthrow 'boom-text'", isolated);

        result.Success.Should().BeFalse();
        result.ErrorOutput!.Trim().Should().Be("boom-text");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParseError_FailsBeforeAnyStatementRuns(bool isolated)
    {
        var result = await RunAsync("$x = 'ran'\nif (", isolated);

        result.Success.Should().BeFalse();
        result.OutputParameters.Should().NotContainKey("x");
        result.ErrorOutput.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExitN_EndsTheStepGreenWithThatCode(bool isolated)
    {
        var result = await RunAsync("$x = 'a'; exit 7", isolated);

        result.Success.Should().BeTrue(result.ErrorOutput);
        result.OutputParameters!["x"].Should().Be("a");
        result.OutputParameters["exitCode"].Should().Be("7");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TopLevelReturn_AfterAFailedNativeCommand_IsGatedBySuccessExitCodes(bool isolated)
    {
        var result = await RunAsync("cmd /c exit 3; return", isolated, successExitCodes: "0");

        result.Success.Should().BeFalse();
        result.OutputParameters!["exitCode"].Should().Be("3");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgressTurnedBackOn_DoesNotLeakIntoTheErrorText(bool isolated)
    {
        var result = await RunAsync(
            "$ProgressPreference = 'Continue'; Write-Progress -Activity 'p' -Status 's'; $g = (New-Guid).Guid",
            isolated);

        result.Success.Should().BeTrue(result.ErrorOutput);
        (result.ErrorOutput ?? "").Should().NotContain("CLIXML").And.NotContain("<Objs");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkingDirectory_IsTheHostsDirectory(bool isolated)
    {
        var result = await RunAsync("$cwd = (Get-Location).Path", isolated);

        result.OutputParameters!["cwd"].TrimEnd('\\').Should().BeEquivalentTo(Environment.CurrentDirectory.TrimEnd('\\'));
    }

    [Fact]
    public void StripSerializedStreams_RemovesProgressBlocksAndKeepsPlainErrors()
    {
        const string stderr = "#< CLIXML\r\nreal error\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\"><Obj S=\"progress\" RefId=\"0\"></Obj></Objs>";

        ProcessExecutionEngine.StripSerializedStreams(stderr).Trim().Should().Be("real error");
        ProcessExecutionEngine.StripSerializedStreams("plain <Objs> text").Should().Be("plain <Objs> text");
    }

    [Fact]
    public void DeleteOrphanedTempScripts_DeletesOnlyOldNodePilotScripts()
    {
        var dir = Directory.CreateTempSubdirectory("np-sweep-").FullName;
        try
        {
            var old = Path.Combine(dir, "nodepilot_old.ps1");
            var fresh = Path.Combine(dir, "nodepilot_fresh.ps1");
            var foreign = Path.Combine(dir, "other_old.ps1");
            foreach (var file in new[] { old, fresh, foreign }) File.WriteAllText(file, "x");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddHours(-2));

            ProcessExecutionEngine.DeleteOrphanedTempScripts(NullLogger.Instance, dir);

            File.Exists(old).Should().BeFalse();
            File.Exists(fresh).Should().BeTrue();
            File.Exists(foreign).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
