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
    public async Task ParallelPauses_KeepTimeoutSuspendedUntilLastResume()
    {
        // The budget is already spent, so re-arming the timeout cancels the execution.
        var handle = new DebugHandle { OriginalTimeoutSeconds = 1 };
        var startedAt = DateTime.UtcNow.AddHours(-1);
        using var execution = new CancellationTokenSource();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = execution.Token.Register(() => cancelled.TrySetResult());

        handle.BeginPause(execution);
        handle.BeginPause(execution);
        handle.EndPause(startedAt, execution);

        execution.IsCancellationRequested.Should().BeFalse("the second branch is still paused");

        handle.EndPause(startedAt, execution);

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
