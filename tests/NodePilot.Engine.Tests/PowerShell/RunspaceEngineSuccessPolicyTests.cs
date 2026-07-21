using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

/// <summary>
/// Verifies the RunspaceExecutionEngine's success policy: any PowerShell error stream entry
/// fails the step, even when stdout was produced.
/// </summary>
public class RunspaceEngineSuccessPolicyTests
{
    private static readonly RunspaceExecutionEngine Engine = new(NullLogger<RunspaceExecutionEngine>.Instance);

    [Fact]
    public async Task Execute_OutputPlusWriteError_Fails()
    {
        var result = await Engine.ExecuteAsync(
            new PowerShellExecutionRequest
            {
                ScriptText = "Write-Output 'partial'; Write-Error 'boom'",
                Timeout = TimeSpan.FromSeconds(5),
            },
            CancellationToken.None);

        result.Success.Should().BeFalse("stdout must not mask PowerShell errors");
        result.Error.Should().Contain("boom");
    }

    [Fact]
    public async Task Execute_PureWriteError_FailsBecauseNoUsableOutput()
    {
        // Write-Error sends to error stream and produces no stdout output → genuine failure.
        var result = await Engine.ExecuteAsync(
            new PowerShellExecutionRequest
            {
                ScriptText = "Write-Error 'something went wrong'",
                Timeout = TimeSpan.FromSeconds(5),
            },
            CancellationToken.None);

        result.Output.Should().BeEmpty();
        result.Success.Should().BeFalse("no output was produced and the error stream got content");
    }

    [Fact]
    public async Task Execute_TerminatingException_FailsCleanly()
    {
        var result = await Engine.ExecuteAsync(
            new PowerShellExecutionRequest
            {
                ScriptText = "throw 'fatal'",
                Timeout = TimeSpan.FromSeconds(5),
            },
            CancellationToken.None);

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Execute_PlainOutput_Succeeds()
    {
        var result = await Engine.ExecuteAsync(
            new PowerShellExecutionRequest
            {
                ScriptText = "Write-Output 'hello'",
                Timeout = TimeSpan.FromSeconds(5),
            },
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("hello");
    }
}
