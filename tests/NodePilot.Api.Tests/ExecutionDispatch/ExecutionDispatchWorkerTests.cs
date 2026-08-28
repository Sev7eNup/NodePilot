using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodePilot.Api.ExecutionDispatch;
using NodePilot.Core.Interfaces;
using Xunit;

namespace NodePilot.Api.Tests.ExecutionDispatch;

/// <summary>
/// Coverage for the dispatch worker pool — the loop that pulls items off the queue,
/// runs them, and emits success/failure metrics. Uses a real ExecutionDispatchQueue so
/// the worker-to-queue contract isn't mocked away.
/// </summary>
public class ExecutionDispatchWorkerTests
{
    private static ExecutionDispatchQueue NewQueue(int capacity = 8) =>
        new(Options.Create(new ExecutionDispatchOptions { Capacity = capacity, WorkerCount = 1 }));

    [Fact]
    public async Task Worker_ProcessesQueuedWorkItems()
    {
        var queue = NewQueue();
        var processed = Channel.CreateUnbounded<int>();

        var worker = new ExecutionDispatchWorker(
            queue,
            Options.Create(new ExecutionDispatchOptions { WorkerCount = 1 }),
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            NullLogger<ExecutionDispatchWorker>.Instance,
            NodePilot.TestCommons.TestDatabaseAvailability.Available);

        using var stopCts = new CancellationTokenSource();
        await worker.StartAsync(stopCts.Token);

        for (var i = 0; i < 3; i++)
        {
            var captured = i;
            await queue.EnqueueAsync(_ =>
            {
                processed.Writer.TryWrite(captured);
                return Task.CompletedTask;
            }, CancellationToken.None);
        }

        var seen = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            seen.Add(await processed.Reader.ReadAsync(readCts.Token));
        }
        seen.Should().BeEquivalentTo(new[] { 0, 1, 2 });

        await stopCts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Worker_FailingWorkItem_LogsAndContinuesProcessing()
    {
        // A throwing work item must not stop the worker; the next item should run.
        var queue = NewQueue();
        var second = new TaskCompletionSource();

        var worker = new ExecutionDispatchWorker(
            queue,
            Options.Create(new ExecutionDispatchOptions { WorkerCount = 1 }),
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            NullLogger<ExecutionDispatchWorker>.Instance,
            NodePilot.TestCommons.TestDatabaseAvailability.Available);

        using var stopCts = new CancellationTokenSource();
        await worker.StartAsync(stopCts.Token);

        await queue.EnqueueAsync(_ => throw new InvalidOperationException("boom"), CancellationToken.None);
        await queue.EnqueueAsync(_ => { second.TrySetResult(); return Task.CompletedTask; }, CancellationToken.None);

        var done = await Task.WhenAny(second.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        done.Should().Be(second.Task, "worker must keep draining the queue after a failure");

        await stopCts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Worker_HostShutdown_StopsWorkersWithoutThrowing()
    {
        var queue = NewQueue();
        var worker = new ExecutionDispatchWorker(
            queue,
            Options.Create(new ExecutionDispatchOptions { WorkerCount = 2 }),
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            NullLogger<ExecutionDispatchWorker>.Instance,
            NodePilot.TestCommons.TestDatabaseAvailability.Available);

        using var stopCts = new CancellationTokenSource();
        await worker.StartAsync(stopCts.Token);

        await stopCts.CancelAsync();

        // Stopping must not surface OperationCanceledException to the caller of StopAsync.
        var stopAct = async () => await worker.StopAsync(CancellationToken.None);
        await stopAct.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ExecutionDispatchQueue_InteractivePriority_DequeuesBeforeNormal()
    {
        // Direct unit-test for the queue ordering. All three items are enqueued while no
        // worker is running, so the queue accumulates them in arrival order. The worker is
        // started only after that setup — its first dequeue must hit the interactive queue,
        // then the two normals.
        var queue = NewQueue(capacity: 16);
        var processed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var allDone = new TaskCompletionSource();
        const int total = 3;
        var seenCount = 0;

        Func<CancellationToken, Task> Make(string label) => _ =>
        {
            processed.Enqueue(label);
            if (Interlocked.Increment(ref seenCount) == total) allDone.TrySetResult();
            return Task.CompletedTask;
        };

        await queue.EnqueueAsync(Make("normal-1"), CancellationToken.None);
        await queue.EnqueueAsync(Make("normal-2"), CancellationToken.None);
        await queue.EnqueueAsync(Make("interactive"), CancellationToken.None, ExecutionDispatchPriority.Interactive);

        var worker = new ExecutionDispatchWorker(
            queue,
            Options.Create(new ExecutionDispatchOptions { WorkerCount = 1 }),
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            NullLogger<ExecutionDispatchWorker>.Instance,
            NodePilot.TestCommons.TestDatabaseAvailability.Available);

        using var stopCts = new CancellationTokenSource();
        await worker.StartAsync(stopCts.Token);

        var done = await Task.WhenAny(allDone.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        done.Should().Be(allDone.Task, "worker must drain all three items within 5s");

        await stopCts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        var ordered = processed.ToArray();
        ordered.Should().HaveCount(3);
        // Interactive is enqueued LAST in arrival order but must be processed FIRST.
        ordered[0].Should().Be("interactive");
        ordered.Skip(1).Should().BeEquivalentTo(new[] { "normal-1", "normal-2" });
    }

    [Fact]
    public async Task ExecutionDispatchQueue_EnqueueNullWorkItem_Throws()
    {
        var queue = NewQueue();
        var act = async () => await queue.EnqueueAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Worker_WhileDatabaseUnavailable_ParksInsteadOfDequeuing()
    {
        // A work item pulled during an outage burns its one chance against a dead server and
        // leaves its execution row stuck in Pending until the next restart - so the gate must sit
        // BEFORE the dequeue, and the item must run untouched once the probe reports recovery.
        var queue = NewQueue();
        var processed = Channel.CreateUnbounded<int>();

        var tracker = new NodePilot.Data.Availability.DatabaseAvailabilityTracker(
            NullLogger<NodePilot.Data.Availability.DatabaseAvailabilityTracker>.Instance,
            probeSuccessesToRecover: 1);
        tracker.MarkBootComplete();
        tracker.ReportUnreachable(NodePilot.Data.Availability.DatabaseOutageReason.Unreachable);

        var worker = new ExecutionDispatchWorker(
            queue,
            Options.Create(new ExecutionDispatchOptions { WorkerCount = 1 }),
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            NullLogger<ExecutionDispatchWorker>.Instance,
            tracker);

        using var stopCts = new CancellationTokenSource();
        await worker.StartAsync(stopCts.Token);

        await queue.EnqueueAsync(_ =>
        {
            processed.Writer.TryWrite(1);
            return Task.CompletedTask;
        }, CancellationToken.None);

        // Parked: nothing may be processed while the breaker is open. A short real-time window is
        // the only way to assert "did not happen"; 300 ms is far above the worker's hot path.
        await Task.Delay(300);
        processed.Reader.TryRead(out _).Should().BeFalse("the worker must not dequeue during an outage");

        // Recovery: the probe closes the breaker, the parked worker wakes and the item runs.
        tracker.ReportProbeSucceeded();

        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        (await processed.Reader.ReadAsync(readCts.Token)).Should().Be(1);

        await stopCts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Worker_RetryBeforeStartOutcome_RequeuesUntilDatabaseRecovery()
    {
        var queue = NewQueue();
        var tracker = new NodePilot.Data.Availability.DatabaseAvailabilityTracker(
            NullLogger<NodePilot.Data.Availability.DatabaseAvailabilityTracker>.Instance,
            probeSuccessesToRecover: 1);
        tracker.MarkBootComplete();
        var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;

        await queue.EnqueueOutcomeAsync(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                tracker.ReportUnreachable(NodePilot.Data.Availability.DatabaseOutageReason.Unreachable);
                firstAttempt.TrySetResult();
                return Task.FromResult(ExecutionDispatchOutcome.RetryBeforeStart);
            }

            completed.TrySetResult();
            return Task.FromResult(ExecutionDispatchOutcome.Completed);
        }, CancellationToken.None, ExecutionDispatchPriority.Interactive);

        var worker = new ExecutionDispatchWorker(
            queue,
            Options.Create(new ExecutionDispatchOptions { WorkerCount = 1 }),
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            NullLogger<ExecutionDispatchWorker>.Instance,
            tracker);
        using var stopCts = new CancellationTokenSource();
        await worker.StartAsync(stopCts.Token);

        await firstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(150);
        attempts.Should().Be(1, "the requeued item must park behind the shared outage gate");

        tracker.ReportProbeSucceeded();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        attempts.Should().Be(2, "the same before-start work item must run again after recovery");

        await stopCts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Requeue_DoesNotDeadlockWhenProducerClaimsReleasedCapacity()
    {
        var queue = new ExecutionDispatchQueue(
            Options.Create(new ExecutionDispatchOptions { Capacity = 1, WorkerCount = 1 }));
        await queue.EnqueueOutcomeAsync(
            _ => Task.FromResult(ExecutionDispatchOutcome.RetryBeforeStart),
            CancellationToken.None);
        var retryItem = await queue.DequeueWorkItemAsync(CancellationToken.None);

        // A producer can claim the slot released by dequeue before the worker observes the retry
        // outcome. Requeue is a durability operation and must not wait for the sole worker to
        // dequeue that producer's item, because that worker is the one currently requeueing.
        await queue.EnqueueAsync(_ => Task.CompletedTask, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        var act = async () => await queue.RequeueAsync(retryItem, timeout.Token);

        await act.Should().NotThrowAsync(
            "a retry item must not be lost or deadlock behind newly enqueued work");

        _ = await queue.DequeueWorkItemAsync(CancellationToken.None);
        _ = await queue.DequeueWorkItemAsync(CancellationToken.None);
        await queue.EnqueueAsync(_ => Task.CompletedTask, CancellationToken.None);
    }
}
