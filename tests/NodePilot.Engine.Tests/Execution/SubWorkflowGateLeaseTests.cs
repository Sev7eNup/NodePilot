using FluentAssertions;
using NodePilot.Engine.Activities;
using NodePilot.Engine.Execution;
using Xunit;

namespace NodePilot.Engine.Tests.Execution;

/// <summary>Active participant and cancellation invariants for ADR 0018.</summary>
public sealed class SubWorkflowGateLeaseTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task ParallelSiblingRetainsSlot_UntilLastActiveStepSuspendsOrFinishes()
    {
        using var gate = new InMemorySubWorkflowGate(1);
        var siblingDone = Signal();
        var childStarted = Signal();
        var childDone = Signal();
        var ct = TestContext.Current.CancellationToken;
        var run = SubWorkflowGateLease.RunAsync(gate, async () =>
        {
            await SubWorkflowGateLease.RunSchedulerAsync(async () =>
            {
                var sibling = SubWorkflowGateLease.RunStepAsync(async () =>
                {
                    await siblingDone.Task.WaitAsync(ct);
                    return true;
                }, ct);
                var nested = SubWorkflowGateLease.RunStepAsync(() =>
                    SubWorkflowGateLease.RunWithCurrentSlotReleasedAsync(() =>
                        SubWorkflowGateLease.RunAsync(gate, async () =>
                        {
                            childStarted.SetResult();
                            await childDone.Task.WaitAsync(ct);
                            return true;
                        }, ct), ct), ct);
                childStarted.Task.IsCompleted.Should().BeFalse();
                gate.Available.Should().Be(0);
                siblingDone.SetResult();
                await childStarted.Task.WaitAsync(ct);
                gate.Available.Should().Be(0);
                childDone.SetResult();
                await Task.WhenAll(sibling, nested);
            }, ct);
            gate.Available.Should().Be(0, "the coordinator reacquires before engine finalization");
            return true;
        }, ct);

        await run;
        gate.Available.Should().Be(1);
        await SubWorkflowGateLease.RunStepAsync(() =>
        {
            gate.Available.Should().Be(1, "the caller must not inherit a finished child's ambient lease");
            return Task.FromResult(true);
        }, ct);
    }

    [Fact]
    public async Task MultipleSuspendedBranches_ReacquireOnceAndReleaseWithoutLeaks()
    {
        using var gate = new InMemorySubWorkflowGate(1);
        var ct = TestContext.Current.CancellationToken;
        await SubWorkflowGateLease.RunAsync(gate, async () =>
        {
            await SubWorkflowGateLease.RunSchedulerAsync(async () =>
            {
                var allEntered = Signal();
                var suspended = 0;
                var steps = Enumerable.Range(0, 8).Select(_ => SubWorkflowGateLease.RunStepAsync(() =>
                    SubWorkflowGateLease.RunWithCurrentSlotReleasedAsync(async () =>
                    {
                        if (Interlocked.Increment(ref suspended) == 8) allEntered.SetResult();
                        await allEntered.Task.WaitAsync(ct);
                        return await SubWorkflowGateLease.RunAsync(gate,
                            () => Task.FromResult(true), ct);
                    }, ct), ct)).ToArray();
                await Task.WhenAll(steps);
            }, ct);
            return true;
        }, ct);
        gate.Available.Should().Be(1);
    }

    [Fact]
    public async Task CancelledResume_DoesNotReleaseAnotherInvocationSlot()
    {
        using var gate = new InMemorySubWorkflowGate(1);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var acquiredByOther = Signal();
        var run = SubWorkflowGateLease.RunAsync(gate, async () =>
        {
            await SubWorkflowGateLease.RunSchedulerAsync(() => SubWorkflowGateLease.RunStepAsync(() =>
                SubWorkflowGateLease.RunWithCurrentSlotReleasedAsync(async () =>
                {
                    await gate.WaitAsync(cancellation.Token);
                    acquiredByOther.SetResult();
                    return true;
                }, cancellation.Token), cancellation.Token), cancellation.Token);
            return true;
        }, cancellation.Token);
        await acquiredByOther.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();
        gate.Available.Should().Be(0);
        gate.Release();
        gate.Available.Should().Be(1);
    }

    [Fact]
    public async Task FlatInvocations_KeepPhysicalCapacityBound()
    {
        using var gate = new InMemorySubWorkflowGate(2);
        var finish = Signal();
        var started = 0;
        var ct = TestContext.Current.CancellationToken;
        async Task<bool> Work()
        {
            Interlocked.Increment(ref started);
            await finish.Task.WaitAsync(ct);
            return true;
        }
        var runs = Enumerable.Range(0, 3).Select(_ => SubWorkflowGateLease.RunAsync(gate, Work, ct)).ToArray();
        started.Should().Be(2);
        gate.Available.Should().Be(0);
        finish.SetResult();
        await Task.WhenAll(runs);
        started.Should().Be(3);
        gate.Available.Should().Be(2);
    }
}
