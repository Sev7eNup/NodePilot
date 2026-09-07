using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

public class RunspaceEngineQueuedCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_StoppedWhileWaitingForRunspace_DoesNotReportSuccess(bool callerCancellation)
    {
        await using var pool = new OccupiedPool();
        using var cts = new CancellationTokenSource();
        pool.WaitUntilOccupied();

        var probe = pool.Engine.ExecuteAsync(new PowerShellExecutionRequest
        {
            ScriptText = pool.ProbeScript,
            Timeout = callerCancellation ? null : TimeSpan.FromMilliseconds(300),
        }, cts.Token);
        if (callerCancellation) cts.Cancel();

        if (callerCancellation)
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => probe.WaitAsync(TimeSpan.FromSeconds(5)));
            error.CancellationToken.Should().Be(cts.Token);
            error.Message.Should().Be(IPowerShellExecutionEngine.CancelledMessage);
        }
        else
        {
            var result = await probe.WaitAsync(TimeSpan.FromSeconds(5));
            result.Success.Should().BeFalse($"the queued script never ran; output='{result.Output}', exitCode={result.ExitCode}, timedOut={result.TimedOut}");
            result.TimedOut.Should().BeTrue();
            result.ExitCode.Should().Be(-1);
            result.Error.Should().Contain("Script timed out after");
        }

        await pool.ReleaseAsync();
        var recovery = await pool.Engine.ExecuteAsync(new PowerShellExecutionRequest
        {
            ScriptText = "Write-Output 'pool-recovered'",
            Timeout = TimeSpan.FromSeconds(5),
        }, CancellationToken.None);
        recovery.Success.Should().BeTrue();
        recovery.Output.Should().Contain("pool-recovered");
        pool.ProbeRan.WaitOne(0).Should().BeFalse("the stopped script must not run when the pool becomes available");
    }

    [Fact]
    public async Task Execute_QueuedCallGetsRunspaceBeforeTimeout_PreservesOutputAndParameters()
    {
        await using var pool = new OccupiedPool();
        pool.WaitUntilOccupied();
        var probe = pool.Engine.ExecuteAsync(new PowerShellExecutionRequest
        {
            ScriptText = pool.ProbeScript,
            Timeout = TimeSpan.FromSeconds(10),
        }, CancellationToken.None);

        probe.IsCompleted.Should().BeFalse("the only runspace is still occupied");
        await pool.ReleaseAsync();
        var result = await probe.WaitAsync(TimeSpan.FromSeconds(5));
        result.Success.Should().BeTrue();
        result.TimedOut.Should().BeFalse();
        result.Output.Should().Contain("init-output-line");
        result.Output.Should().Contain("\"msg\":\"hello-from-init\"");
        result.Output.Should().Contain("\"num\":\"42\"");
        pool.ProbeRan.WaitOne(0).Should().BeTrue();
    }

    [Fact]
    public async Task Execute_AlreadyCancelled_DoesNotInvokeScript()
    {
        using var engine = new RunspaceExecutionEngine(NullLogger.Instance, 1, 1);
        var name = "np-precancel-" + Guid.NewGuid().ToString("N");
        using var ran = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ExecuteAsync(
            new PowerShellExecutionRequest
            {
                ScriptText = $"$signal=[System.Threading.EventWaitHandle]::OpenExisting('{name}'); try {{ [void]$signal.Set() }} finally {{ $signal.Dispose() }}",
            }, cts.Token));

        error.CancellationToken.Should().Be(cts.Token);
        error.Message.Should().Be(IPowerShellExecutionEngine.CancelledMessage);
        ran.WaitOne(0).Should().BeFalse();
    }

    [Fact]
    public async Task Execute_CallerCancelsWhileCompletedOutputIsFormatted_PreservesSuccess()
    {
        using var engine = new RunspaceExecutionEngine(NullLogger.Instance, 1, 1);
        using var cts = new CancellationTokenSource();
        var key = Guid.NewGuid().ToString("N");
        CancelOnFormat.Callbacks[key] = cts.Cancel;
        try
        {
            // ToString runs in the engine after EndInvoke has returned the completed pipeline's output.
            var result = await engine.ExecuteAsync(new PowerShellExecutionRequest
            {
                ScriptText = $"[NodePilot.Engine.Tests.PowerShell.RunspaceEngineQueuedCancellationTests+CancelOnFormat]::new('{key}')",
                Timeout = TimeSpan.FromSeconds(10),
            }, cts.Token);

            cts.IsCancellationRequested.Should().BeTrue("formatting must actually exercise the late cancellation");
            result.Success.Should().BeTrue();
            result.TimedOut.Should().BeFalse();
            result.Output.Should().Contain("completed-before-cancel");
        }
        finally
        {
            CancelOnFormat.Callbacks.TryRemove(key, out _);
        }
    }

    public sealed class CancelOnFormat(string key)
    {
        internal static readonly ConcurrentDictionary<string, Action> Callbacks = new();

        public override string ToString()
        {
            Callbacks[key]();
            return "completed-before-cancel";
        }
    }

    private sealed class OccupiedPool : IAsyncDisposable
    {
        private readonly string _suffix = Guid.NewGuid().ToString("N");
        private readonly EventWaitHandle _entered;
        private readonly EventWaitHandle _release;
        private readonly Task<PowerShellExecutionResult> _blocker;
        public RunspaceExecutionEngine Engine { get; } = new(NullLogger.Instance, 1, 1);
        public EventWaitHandle ProbeRan { get; }

        public string ProbeScript =>
            $"$signal=[System.Threading.EventWaitHandle]::OpenExisting('np-ran-{_suffix}'); try {{ [void]$signal.Set() }} finally {{ $signal.Dispose() }}; $msg='hello-from-init'; $num='42'; Write-Output 'init-output-line'";

        public OccupiedPool()
        {
            _entered = new EventWaitHandle(false, EventResetMode.ManualReset, "np-enter-" + _suffix);
            _release = new EventWaitHandle(false, EventResetMode.ManualReset, "np-release-" + _suffix);
            ProbeRan = new EventWaitHandle(false, EventResetMode.ManualReset, "np-ran-" + _suffix);
            _blocker = Engine.ExecuteAsync(new PowerShellExecutionRequest
            {
                ScriptText = $"$e=[System.Threading.EventWaitHandle]::OpenExisting('np-enter-{_suffix}'); $r=[System.Threading.EventWaitHandle]::OpenExisting('np-release-{_suffix}'); try {{ [void]$e.Set(); [void]$r.WaitOne(15000) }} finally {{ $e.Dispose(); $r.Dispose() }}",
                Timeout = TimeSpan.FromSeconds(20),
            }, CancellationToken.None);
        }

        public void WaitUntilOccupied() => _entered.WaitOne(TimeSpan.FromSeconds(10))
            .Should().BeTrue("the sole runspace must be occupied before the probe queues");

        public async Task ReleaseAsync()
        {
            _release.Set();
            await _blocker.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async ValueTask DisposeAsync()
        {
            try { await ReleaseAsync(); }
            finally
            {
                Engine.Dispose();
                _entered.Dispose();
                _release.Dispose();
                ProbeRan.Dispose();
            }
        }
    }
}
