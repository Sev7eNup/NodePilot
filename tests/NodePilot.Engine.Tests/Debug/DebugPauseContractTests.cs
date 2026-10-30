using FluentAssertions;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Debug;
using Xunit;

namespace NodePilot.Engine.Tests.Debug;

public sealed class DebugPauseContractTests
{
    [Theory]
    [InlineData("globals.password")]
    [InlineData("GLOBALS.region")]
    [InlineData("__internal")]
    public async Task InvalidOverride_PreservesPauseAndSnapshot(string key)
    {
        var handle = new DebugHandle();
        var values = new Dictionary<string, string> { ["manual.x"] = "before" };
        var wait = handle.AwaitResumeAsync("step", CancellationToken.None,
            new DebugPausedStepSnapshot("step", "Label", DateTime.UtcNow, "breakpoint", values));
        values["manual.x"] = "later";
        var resume = () => handle.Resume("step", new ResumeRequest(ResumeCommand.Continue,
            new Dictionary<string, string> { [key] = "edited" }));

        resume.Should().Throw<ArgumentException>();
        wait.IsCompleted.Should().BeFalse();
        handle.PausedSnapshots.Should().ContainSingle().Which.Variables["manual.x"].Should().Be("before");
        handle.Resume("step", new ResumeRequest(ResumeCommand.Continue, null)).Should().BeTrue();
        await wait;
        handle.PausedSnapshots.Should().BeEmpty();
    }

    [Fact]
    public async Task ParallelPauses_SuspendTimeoutUntilLastResume_AndCountOverlapOnce()
    {
        var handle = new DebugHandle { OriginalTimeoutSeconds = 1 };
        var startedAt = DateTime.UtcNow;
        using var execution = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        handle.BeginPause(execution);
        handle.BeginPause(execution);
        await Task.Delay(100);
        handle.EndPause(startedAt, execution);
        await Task.Delay(1100);
        execution.IsCancellationRequested.Should().BeFalse();
        handle.EndPause(startedAt, execution);
        var elapsed = DateTime.UtcNow - startedAt;
        handle.TotalPausedDuration.TotalMilliseconds.Should().BeApproximately(elapsed.TotalMilliseconds, 100);
        execution.IsCancellationRequested.Should().BeFalse();
    }
}
