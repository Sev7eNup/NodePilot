using System.Management.Automation.Runspaces;
using FluentAssertions;
using NodePilot.Remote;
using Xunit;

namespace NodePilot.Engine.Tests.Remote;

/// <summary>
/// F-1 regression: <see cref="WinRmSession"/> must mark itself "poisoned" when a script
/// times out, so the pool discards it instead of handing it to the next step (where
/// PowerShell.Invoke()'s still-running pipeline would corrupt the runspace).
///
/// We exercise the timeout path with a real local runspace — no WinRM server needed —
/// because the bug lives in the Task.Run / cancellation interaction, not in the WinRM
/// transport. A long Start-Sleep + a very short timeout is enough to trip it.
/// </summary>
public class WinRmSessionPoisonTests
{
    [Fact]
    public async Task ExecuteScriptAsync_OnTimeout_MarksSessionAsNotAlive()
    {
        using var runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();
        var session = new WinRmSession(runspace, targetHostname: "test-local");

        session.IsAlive.Should().BeTrue("a freshly-opened runspace must be reusable before any execution");

        // 30s sleep, 1s timeout — the cts inside ExecuteScriptAsync fires, Task.Run sees
        // OperationCanceledException, the F-1 catch runs ps.Stop() + sets _poisoned.
        var result = await session.ExecuteScriptAsync(
            "Start-Sleep -Seconds 30",
            timeoutSeconds: 1,
            ct: CancellationToken.None);

        result.Success.Should().BeFalse("the script ran past its timeout");
        result.ErrorOutput.Should().Contain("timed out", "the timeout branch returned its dedicated error message");

        session.IsAlive.Should().BeFalse(
            "F-1: the session was poisoned by the timeout path, so the pool must discard it on Return " +
            "instead of leaking the still-running pipeline to the next consumer");

        await session.DisposeAsync();
    }

    [Fact]
    public async Task ExecuteScriptAsync_OnSuccess_LeavesSessionAlive()
    {
        // Negative control: a normal completion must NOT poison the session.
        using var runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();
        var session = new WinRmSession(runspace, targetHostname: "test-local");

        var result = await session.ExecuteScriptAsync(
            "Write-Output 'ok'",
            timeoutSeconds: 30,
            ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        session.IsAlive.Should().BeTrue("a successful execution must keep the session reusable");

        await session.DisposeAsync();
    }
}
