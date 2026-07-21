using FluentAssertions;
using NodePilot.Engine.Debug;
using Xunit;

namespace NodePilot.Engine.Tests.Debug;

/// <summary>
/// Unit tests for the DebugHandle TaskCompletionSource machinery (the plumbing behind step
/// breakpoints/resume). Engine end-to-end tests separately cover the pause-insertion point in
/// WorkflowEngine.ExecuteStepAsync.
/// </summary>
public class DebugHandleTests
{
    [Fact]
    public async Task AwaitResume_ResolvedBySubsequentResume_ReturnsSameRequest()
    {
        var handle = new DebugHandle();
        var awaitTask = handle.AwaitResumeAsync("step-1", CancellationToken.None);

        // Resume happens "later" (asynchronously) — awaitTask must return exactly this request object.
        var resumed = handle.Resume("step-1", new ResumeRequest(ResumeCommand.Continue, null));
        resumed.Should().BeTrue();

        var got = await awaitTask.WaitAsync(TimeSpan.FromSeconds(2));
        got.Command.Should().Be(ResumeCommand.Continue);
        got.Overrides.Should().BeNull();
    }

    [Fact]
    public async Task Resume_WithOverrides_PropagatesOverridesDictionary()
    {
        var handle = new DebugHandle();
        var awaitTask = handle.AwaitResumeAsync("step-1", CancellationToken.None);

        var overrides = new Dictionary<string, string> { ["globals.ENV"] = "prod", ["manual.x"] = "42" };
        handle.Resume("step-1", new ResumeRequest(ResumeCommand.StepOver, overrides));

        var got = await awaitTask.WaitAsync(TimeSpan.FromSeconds(2));
        got.Command.Should().Be(ResumeCommand.StepOver);
        got.Overrides.Should().NotBeNull();
        got.Overrides!["globals.ENV"].Should().Be("prod");
        got.Overrides["manual.x"].Should().Be("42");
    }

    [Fact]
    public void Resume_OnUnknownStep_ReturnsFalse()
    {
        var handle = new DebugHandle();
        // Nobody is waiting → Resume has nothing to resolve.
        var ok = handle.Resume("non-existent", new ResumeRequest(ResumeCommand.Continue, null));
        ok.Should().BeFalse();
    }

    [Fact]
    public async Task AwaitResume_CancelledBeforeResume_ThrowsOperationCanceled()
    {
        var handle = new DebugHandle();
        using var cts = new CancellationTokenSource();
        var awaitTask = handle.AwaitResumeAsync("step-1", cts.Token);

        cts.Cancel();

        var act = async () => await awaitTask.WaitAsync(TimeSpan.FromSeconds(2));
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReleaseAllAsStop_ResolvesAllPendingTcsWithStop()
    {
        // Two parallel branches sitting at a breakpoint — the cancel endpoint must release BOTH,
        // otherwise one of the engine tasks stays stuck waiting for the pause guard.
        var handle = new DebugHandle();
        var t1 = handle.AwaitResumeAsync("step-a", CancellationToken.None);
        var t2 = handle.AwaitResumeAsync("step-b", CancellationToken.None);

        handle.ReleaseAllAsStop();

        var r1 = await t1.WaitAsync(TimeSpan.FromSeconds(2));
        var r2 = await t2.WaitAsync(TimeSpan.FromSeconds(2));
        r1.Command.Should().Be(ResumeCommand.Stop);
        r2.Command.Should().Be(ResumeCommand.Stop);
    }

    [Fact]
    public async Task TwoParallelSteps_ResumedIndependently()
    {
        // Parallel branches: Step A gets Continue, Step B gets StepOver — each awaiter must
        // receive its own matching command, never the other one's.
        var handle = new DebugHandle();
        var t1 = handle.AwaitResumeAsync("step-a", CancellationToken.None);
        var t2 = handle.AwaitResumeAsync("step-b", CancellationToken.None);

        handle.Resume("step-b", new ResumeRequest(ResumeCommand.StepOver, null));
        handle.Resume("step-a", new ResumeRequest(ResumeCommand.Continue, null));

        var r1 = await t1.WaitAsync(TimeSpan.FromSeconds(2));
        var r2 = await t2.WaitAsync(TimeSpan.FromSeconds(2));
        r1.Command.Should().Be(ResumeCommand.Continue);
        r2.Command.Should().Be(ResumeCommand.StepOver);
    }

    [Fact]
    public void PendingSteps_TracksRegisteredAwaiters()
    {
        var handle = new DebugHandle();
        handle.PendingSteps.Should().BeEmpty();

        _ = handle.AwaitResumeAsync("a", CancellationToken.None);
        _ = handle.AwaitResumeAsync("b", CancellationToken.None);
        handle.PendingSteps.Should().BeEquivalentTo(new[] { "a", "b" });

        handle.Resume("a", new ResumeRequest(ResumeCommand.Continue, null));
        handle.PendingSteps.Should().BeEquivalentTo(new[] { "b" });
    }
}
