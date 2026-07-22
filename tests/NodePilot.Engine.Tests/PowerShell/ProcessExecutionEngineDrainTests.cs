using System.Diagnostics;
using FluentAssertions;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

/// <summary>
/// Deterministic, hermetic coverage for the bounded post-exit pipe drain
/// (<see cref="ProcessExecutionEngine.DrainReadsAsync"/>) — the guarantee that closes the
/// "isolated runScript stuck in Running forever" bug. Root cause: a leaked inherited stdout/stderr
/// pipe handle in an unrelated process keeps the pipe write-end open, so the isolated
/// <c>ReadToEndAsync</c> never reaches EOF and the step Task never completes. After the root process
/// has exited and its job tree has been terminated, no legitimate writer remains, so the drain MUST
/// be bounded. These tests drive the drain with a read that never EOFs (the leak) and prove it
/// returns promptly with whatever was captured.
/// </summary>
public class ProcessExecutionEngineDrainTests
{
    [Fact]
    public async Task DrainReads_BothReadsReachEof_ReturnsOutputsWithNoTimeout()
    {
        var (stdout, stderr, timedOut) = await ProcessExecutionEngine.DrainReadsAsync(
            Task.FromResult("out"), Task.FromResult("err"),
            TimeSpan.FromSeconds(5), CancellationToken.None);

        timedOut.Should().BeFalse();
        stdout.Should().Be("out");
        stderr.Should().Be("err");
    }

    [Fact]
    public async Task DrainReads_ReadNeverReachesEof_ReturnsBoundedWithCapturedOutput()
    {
        // stderr simulates a read blocked forever by a leaked inherited pipe handle.
        var stderrNeverEof = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var sw = Stopwatch.StartNew();
        var (stdout, stderr, timedOut) = await ProcessExecutionEngine.DrainReadsAsync(
            Task.FromResult("captured-stdout"), stderrNeverEof.Task,
            TimeSpan.FromMilliseconds(200), CancellationToken.None);
        sw.Stop();

        timedOut.Should().BeTrue("a read that never EOFs after root-exit + job-terminate signals a leaked handle");
        stdout.Should().Be("captured-stdout", "output that did drain must still be returned");
        stderr.Should().BeEmpty("the abandoned read yields empty rather than hanging");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "the drain is bounded by the grace window — this is the regression guard for the stuck-Running hang");

        // The abandoned read is observed: faulting it later (as reader disposal would) must not raise
        // an UnobservedTaskException.
        stderrNeverEof.SetException(new ObjectDisposedException("reader"));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [Fact]
    public async Task DrainReads_CallerCancels_PropagatesOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var never1 = new TaskCompletionSource<string>();
        var never2 = new TaskCompletionSource<string>();

        var drain = ProcessExecutionEngine.DrainReadsAsync(
            never1.Task, never2.Task, TimeSpan.FromSeconds(30), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await drain);

        // Observe the still-pending tasks so they don't linger as unobserved.
        never1.TrySetCanceled();
        never2.TrySetCanceled();
    }
}
