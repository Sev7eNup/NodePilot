using System.Management.Automation.Runspaces;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Engine.PowerShell;
using NodePilot.Remote;
using Xunit;

namespace NodePilot.Engine.Tests.Remote;

/// <summary>
/// A remote script that throws still publishes what it assigned: the wrapper writes its markers
/// before the throw leaves the pipeline, and the session must not drop that output.
/// </summary>
public class WinRmSessionThrowTests
{
    [Theory]
    [InlineData("throw 'Collection was modified; enumeration operation may not execute.'")]
    [InlineData("Write-Error 'Collection was modified; enumeration operation may not execute.'")]
    public async Task ExecuteScriptAsync_CollectionErrorAfterSideEffect_DoesNotReplayScript(string failure)
    {
        using var runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();
        await using var session = new WinRmSession(runspace, "test-local");
        var path = Path.GetTempFileName();
        try
        {
            var result = await session.ExecuteScriptAsync(
                $"[IO.File]::AppendAllText({PowerShellOperation.Literal(path)}, 'x')\n{failure}",
                timeoutSeconds: 30, ct: TestContext.Current.CancellationToken);

            result.Success.Should().BeFalse();
            result.ErrorOutput.Should().Contain("Collection was modified");
            File.ReadAllText(path).Should().Be("x", "the transport must not repeat an already executed side effect");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ExecuteScriptAsync_ScriptThrows_KeepsTheOutputWrittenBefore()
    {
        using var runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();
        var session = new WinRmSession(runspace, targetHostname: "test-local");
        var wrapped = PowerShellScriptWrapper.Wrap(
            "$x = 'a'\nWrite-Output 'before'\nthrow 'boom-text'", new Dictionary<string, string>(), NullLogger.Instance);

        var result = await session.ExecuteScriptAsync(wrapped, timeoutSeconds: 30, ct: TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Be("boom-text");
        var (clean, _, parameters) = PowerShellActivitySupport.ExtractMarkers(result.Output, "step-1", NullLogger.Instance);
        clean.Should().Contain("before");
        parameters.Should().ContainKey("x").WhoseValue.Should().Be("a");

        await session.DisposeAsync();
    }
}
