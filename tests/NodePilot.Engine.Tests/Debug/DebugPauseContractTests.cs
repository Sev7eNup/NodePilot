using FluentAssertions;
using NodePilot.Engine.Debug;
using Xunit;

namespace NodePilot.Engine.Tests.Debug;

public sealed class DebugPauseContractTests
{
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
