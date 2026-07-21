using FluentAssertions;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Activities;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

/// <summary>
/// Tests the in-memory default for <see cref="ISubWorkflowGate"/> — the back-pressure
/// pool shared by <c>startWorkflow</c> and <c>forEach</c>. Each test creates its own
/// gate instance (no shared static state) so we keep the suite parallelizable, unlike
/// the previous static-semaphore design.
/// </summary>
public class InMemorySubWorkflowGateTests
{
    [Fact]
    public void DefaultCapacity_IsCalibratedTo128()
    {
        var gate = new InMemorySubWorkflowGate();
        gate.Capacity.Should().Be(128);
        gate.Available.Should().Be(128);
    }

    [Fact]
    public void Constructor_RejectsZeroOrNegativeCapacity()
    {
        FluentActions.Invoking(() => new InMemorySubWorkflowGate(0))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new InMemorySubWorkflowGate(-1))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task WaitAsync_AcquireAndRelease_IsObservableInAvailable()
    {
        using var gate = new InMemorySubWorkflowGate(4);
        var initial = gate.Available;

        await gate.WaitAsync(CancellationToken.None);
        try
        {
            gate.Available.Should().Be(initial - 1);
        }
        finally
        {
            gate.Release();
        }

        gate.Available.Should().Be(initial);
    }

    [Fact]
    public async Task WaitAsyncWithTimeout_ReturnsFalse_WhenSaturated()
    {
        using var gate = new InMemorySubWorkflowGate(2);
        await gate.WaitAsync(CancellationToken.None);
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            // gate is empty — the third waiter must time out cleanly with `false`,
            // not block or throw. This is the contract StartWorkflowActivity relies on
            // for its 5-second admission probe.
            var acquired = await gate.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);
            acquired.Should().BeFalse();
        }
        finally
        {
            gate.Release();
            gate.Release();
        }
    }

    [Fact]
    public async Task WaitAsync_HonoursCancellation_WhenSaturated()
    {
        using var gate = new InMemorySubWorkflowGate(1);
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            var act = async () => await gate.WaitAsync(cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public void Release_OverRelease_Throws()
    {
        // SemaphoreSlim throws when Release() pushes count past initialCount.
        // We rely on this to surface symmetry bugs in caller code instead of
        // silently corrupting the cap.
        using var gate = new InMemorySubWorkflowGate(1);
        FluentActions.Invoking(() => gate.Release())
            .Should().Throw<SemaphoreFullException>();
    }
}
