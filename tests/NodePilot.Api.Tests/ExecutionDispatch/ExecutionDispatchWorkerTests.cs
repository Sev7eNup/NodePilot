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
/// the worker -> queue contract isn't mocked away.
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
            NullLogger<ExecutionDispatchWorker>.Instance);

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
            NullLogger<ExecutionDispatchWorker>.Instance);

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
            NullLogger<ExecutionDispatchWorker>.Instance);

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
        // Direct unit-test for the queue ordering. We pre-enqueue all three items WHILE
        // no worker is running, so the queue accumulates them in arrival order. Then we
        // start the worker — its first dequeue must hit the interactive queue, then the
        // two normals. A previous version of this test awaited firstNormalDone before
        // starting the worker, which deadlocked (no worker → callback never fires).
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
            NullLogger<ExecutionDispatchWorker>.Instance);

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
}
