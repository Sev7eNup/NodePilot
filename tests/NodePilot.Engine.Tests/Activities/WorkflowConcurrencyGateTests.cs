using FluentAssertions;
using NodePilot.Engine.Activities;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

public class WorkflowConcurrencyGateTests
{
    private static readonly Guid Workflow = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void TryAcquire_BelowLimit_Succeeds()
    {
        var gate = new InMemoryWorkflowConcurrencyGate();

        gate.TryAcquire(Workflow, 2).Should().BeTrue();
        gate.TryAcquire(Workflow, 2).Should().BeTrue();
        gate.ActiveCount(Workflow).Should().Be(2);
    }

    [Fact]
    public void TryAcquire_AtLimit_ReturnsFalse()
    {
        var gate = new InMemoryWorkflowConcurrencyGate();
        gate.TryAcquire(Workflow, 1).Should().BeTrue();

        gate.TryAcquire(Workflow, 1).Should().BeFalse();
        gate.ActiveCount(Workflow).Should().Be(1);
    }

    [Fact]
    public void TryAcquire_WithNullLimit_AlwaysSucceedsButTracksActive()
    {
        var gate = new InMemoryWorkflowConcurrencyGate();

        for (var i = 0; i < 50; i++)
            gate.TryAcquire(Workflow, null).Should().BeTrue();

        // Tracked even when unlimited: otherwise raising a limit from null to 2 while 50 runs
        // are in flight would admit two more.
        gate.ActiveCount(Workflow).Should().Be(50);
        gate.BlockedWorkflowIds.Should().BeEmpty();
    }

    [Fact]
    public void TryAcquire_WithNonPositiveLimit_TreatsItAsUnlimited()
    {
        // Matches WorkflowEngine.CheckCapacityCaps, where a non-positive configured cap is off.
        var gate = new InMemoryWorkflowConcurrencyGate();

        gate.TryAcquire(Workflow, 0).Should().BeTrue();
        gate.TryAcquire(Workflow, 0).Should().BeTrue();
        gate.BlockedWorkflowIds.Should().BeEmpty();
    }

    [Fact]
    public void Release_FreesTheSlotForTheNextAcquire()
    {
        var gate = new InMemoryWorkflowConcurrencyGate();
        gate.TryAcquire(Workflow, 1);
        gate.TryAcquire(Workflow, 1).Should().BeFalse();

        gate.Release(Workflow);

        gate.TryAcquire(Workflow, 1).Should().BeTrue();
    }

    [Fact]
    public async Task Release_HandsSlotsToWaiters_InFifoOrder()
    {
        var gate = new InMemoryWorkflowConcurrencyGate();
        gate.TryAcquire(Workflow, 1);

        var first = gate.AcquireAsync(Workflow, 1, CancellationToken.None);
        var second = gate.AcquireAsync(Workflow, 1, CancellationToken.None);
        first.IsCompleted.Should().BeFalse();

        gate.Release(Workflow);
        await first;
        second.IsCompleted.Should().BeFalse("the second waiter must not jump the single slot");

        gate.Release(Workflow);
        await second;
    }

    [Fact]
    public async Task LoweredLimit_DoesNotHandOffUntilBelowNewLimit()
    {
        var gate = new InMemoryWorkflowConcurrencyGate();
        for (var i = 0; i < 5; i++) gate.TryAcquire(Workflow, 5);

        var waiter = gate.AcquireAsync(Workflow, 5, CancellationToken.None);
        gate.SetLimit(Workflow, 2);

        // Handing a released slot straight to the waiter would keep Active at 5 forever.
        gate.Release(Workflow);
        gate.Release(Workflow);
        gate.Release(Workflow);
        gate.ActiveCount(Workflow).Should().Be(2);
        waiter.IsCompleted.Should().BeFalse();

        gate.Release(Workflow);
        await waiter;
        gate.ActiveCount(Workflow).Should().Be(2);
    }

    [Fact]
    public async Task RaisedLimit_WakesQueuedWaitersImmediately()
    {
        var gate = new InMemoryWorkflowConcurrencyGate();
        gate.TryAcquire(Workflow, 1);

        var waiters = Enumerable.Range(0, 4)
            .Select(_ => gate.AcquireAsync(Workflow, 1, CancellationToken.None))
            .ToArray();
        waiters.Should().OnlyContain(task => !task.IsCompleted);

        gate.SetLimit(Workflow, 4);

        // Three more fit under the new limit; the fourth keeps waiting.
        await Task.WhenAll(waiters[0], waiters[1], waiters[2]);
        waiters[3].IsCompleted.Should().BeFalse();
        gate.ActiveCount(Workflow).Should().Be(4);
    }

    [Fact]
    public void StalePolicyObservation_DoesNotOverwriteNewerLimit()
    {
        var gate = new InMemoryWorkflowConcurrencyGate();
        gate.TryAcquire(Workflow, 5);
        gate.SetLimit(Workflow, 1);

        // A caller whose workflow row was read before the change must not restore the old cap.
        gate.TryAcquire(Workflow, 5).Should().BeFalse();
        gate.BlockedWorkflowIds.Should().Contain(Workflow);
    }

    [Fact]
    public void SetLimit_OnIdleWorkflow_IsNoOp()
    {
        var gate = new InMemoryWorkflowConcurrencyGate();

        gate.SetLimit(Workflow, 1);

        // Nothing is tracked, so the next acquire seeds from its own fresh row read.
        gate.TrackedWorkflowCount.Should().Be(0);
        gate.TryAcquire(Workflow, 3).Should().BeTrue();
        gate.TryAcquire(Workflow, 3).Should().BeTrue();
    }

    [Fact]
    public async Task AcquireAsync_WhenCancelled_RemovesWaiterAndLeavesSlotForTheNext()
    {
        var gate = new InMemoryWorkflowConcurrencyGate();
        gate.TryAcquire(Workflow, 1);

        using var cts = new CancellationTokenSource();
        var cancelled = gate.AcquireAsync(Workflow, 1, cts.Token);
        var survivor = gate.AcquireAsync(Workflow, 1, CancellationToken.None);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        // The cancelled waiter must not consume the freed slot.
        gate.Release(Workflow);
        await survivor;
        gate.ActiveCount(Workflow).Should().Be(1);
    }

    [Fact]
    public async Task AcquireRelease_Symmetry_LeavesNoResidualState()
    {
        var gate = new InMemoryWorkflowConcurrencyGate();

        for (var i = 0; i < 20; i++)
        {
            await gate.AcquireAsync(Workflow, 2, CancellationToken.None);
            gate.Release(Workflow);
        }

        gate.ActiveCount(Workflow).Should().Be(0);
        gate.TrackedWorkflowCount.Should().Be(0);
        gate.BlockedWorkflowIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ConcurrentAcquire_NeverExceedsLimit()
    {
        var gate = new InMemoryWorkflowConcurrencyGate();
        const int limit = 5;
        var active = 0;
        var peak = 0;
        var peakGate = new object();

        await Parallel.ForEachAsync(Enumerable.Range(0, 200), async (_, ct) =>
        {
            await gate.AcquireAsync(Workflow, limit, ct);
            try
            {
                var current = Interlocked.Increment(ref active);
                lock (peakGate) peak = Math.Max(peak, current);
                await Task.Yield();
                Interlocked.Decrement(ref active);
            }
            finally
            {
                gate.Release(Workflow);
            }
        });

        peak.Should().BeLessThanOrEqualTo(limit);
        gate.TrackedWorkflowCount.Should().Be(0);
    }

    [Fact]
    public void BlockedWorkflowIds_TracksBoundaryCrossingsInBothDirections()
    {
        var gate = new InMemoryWorkflowConcurrencyGate();

        gate.TryAcquire(Workflow, 1);
        gate.BlockedWorkflowIds.Should().BeEquivalentTo([Workflow]);

        gate.Release(Workflow);
        gate.BlockedWorkflowIds.Should().BeEmpty();
    }

    [Fact]
    public void BlockedWorkflowIds_ExcludesUnlimitedWorkflows()
    {
        var gate = new InMemoryWorkflowConcurrencyGate();
        gate.TryAcquire(Workflow, null);
        gate.TryAcquire(Other, 1);

        gate.BlockedWorkflowIds.Should().BeEquivalentTo([Other]);
    }

    [Fact]
    public void BlockedWorkflowIds_ExpireAfterTtl_AndReturnOnTheNextRefusedAcquire()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var gate = new InMemoryWorkflowConcurrencyGate(time);
        gate.TryAcquire(Workflow, 1);
        gate.BlockedWorkflowIds.Should().Contain(Workflow);

        // Expiry lets one claim through, so a limit raised without a SetLimit push is re-read
        // instead of leaving the workflow blocked until its running execution ends.
        time.Advance(InMemoryWorkflowConcurrencyGate.BlockedEntryTtl + TimeSpan.FromSeconds(1));
        gate.BlockedWorkflowIds.Should().BeEmpty();

        gate.TryAcquire(Workflow, 1).Should().BeFalse();
        gate.BlockedWorkflowIds.Should().Contain(Workflow);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
