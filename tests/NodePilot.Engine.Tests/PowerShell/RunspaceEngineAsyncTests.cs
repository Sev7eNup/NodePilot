using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

/// <summary>
/// Verifies cancellation, timeout, and concurrency behavior of the BeginInvoke/EndInvoke path.
/// Caller cancellation must stop the script and surface as OperationCanceledException. Script
/// timeout remains a failed result, and concurrent calls must keep their output isolated.
/// </summary>
public class RunspaceEngineAsyncTests
{
    [Theory]
    [InlineData("throw 'Collection was modified; enumeration operation may not execute.'")]
    [InlineData("Write-Error 'Collection was modified; enumeration operation may not execute.'")]
    public async Task Execute_CollectionErrorAfterSideEffect_DoesNotReplayScript(string failure)
    {
        using var engine = new RunspaceExecutionEngine(NullLogger.Instance);
        var path = Path.GetTempFileName();
        try
        {
            var result = await engine.ExecuteAsync(new PowerShellExecutionRequest
            {
                ScriptText = $"[IO.File]::AppendAllText({PowerShellOperation.Literal(path)}, 'x')\n{failure}",
            }, TestContext.Current.CancellationToken);

            result.Success.Should().BeFalse();
            result.Error.Should().Contain("Collection was modified");
            File.ReadAllText(path).Should().Be("x", "the transport must not repeat an already executed side effect");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Execute_CallerCancellation_ThrowsInsteadOfReturningAFailedResult()
    {
        // waitAny and waitNofM cancel losing branches. The exception must reach StepRunner so it
        // records those branches as Cancelled instead of Failed.
        using var engine = new RunspaceExecutionEngine(
            NullLogger<RunspaceExecutionEngine>.Instance,
            minRunspaces: 1,
            maxRunspaces: 4);

        using var cts = new CancellationTokenSource();

        var releaseName = $"NodePilot-test-{Guid.NewGuid():N}";
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        using var started = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName + "-started");
        var task = engine.ExecuteAsync(
            new PowerShellExecutionRequest
            {
                ScriptText = $$"""
                    $started = [System.Threading.EventWaitHandle]::OpenExisting('{{releaseName}}-started')
                    try { [void]$started.Set() } finally { $started.Dispose() }
                    {{WaitForRelease(releaseName)}}
                    """,
                Timeout = TimeSpan.FromMinutes(5),
            },
            cts.Token);

        OperationCanceledException thrown;
        try
        {
            (await Task.Run(() => started.WaitOne(TimeSpan.FromSeconds(30)),
                TestContext.Current.CancellationToken)).Should().BeTrue("the script must be running before cancellation");
            var cancellation = cts.CancelAsync();
            // Longer than the engine's default StopGracePeriod, so cancellation surfaces even if
            // the SDK never completes Stop().
            thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => task.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken));
            await cancellation.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        }
        finally { release.Set(); }

        thrown.CancellationToken.Should().Be(cts.Token,
            "StepRunner tells a junction stand-down from a whole-execution cancel by the token");
        thrown.Message.Should().Be(IPowerShellExecutionEngine.CancelledMessage);
    }

    [Fact]
    public async Task Execute_NoTimeoutAndScriptIsLong_RunsToCompletion()
    {
        // An earlier fix made `cts.CancelAfter` actually stop the running pipeline (`ps.Stop()`)
        // instead of just abandoning the wait. A later change made the timeout itself fully
        // optional — when a step sets no timeoutSeconds, request.Timeout is null and the script
        // must be allowed to run as long as it needs, stopping only if the caller cancels.
        using var engine = new RunspaceExecutionEngine(
            NullLogger<RunspaceExecutionEngine>.Instance,
            minRunspaces: 1,
            maxRunspaces: 4);

        var result = await engine.ExecuteAsync(
            new PowerShellExecutionRequest
            {
                ScriptText = "Start-Sleep -Milliseconds 1500; Write-Output 'done'",
                Timeout = null,
            },
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.TimedOut.Should().BeFalse();
        result.Output.Should().Contain("done");
    }

    [Fact]
    public async Task Execute_Timeout_StopsScriptBeforeItIsReleased()
    {
        using var engine = new RunspaceExecutionEngine(
            NullLogger<RunspaceExecutionEngine>.Instance,
            minRunspaces: 1,
            maxRunspaces: 4);

        var releaseName = $"NodePilot-test-{Guid.NewGuid():N}";
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        try
        {
            var result = await engine.ExecuteAsync(
                new PowerShellExecutionRequest
                {
                    ScriptText = WaitForRelease(releaseName),
                    Timeout = TimeSpan.FromMilliseconds(300),
                },
                TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            result.TimedOut.Should().BeTrue();
            result.Success.Should().BeFalse();
        }
        finally { release.Set(); }
    }

    // A blocking .NET call cannot be interrupted by Stop(), so it stands in for a pipeline that
    // ignores cancellation. One runspace: if the abandoned slot were never returned, the
    // follow-up call would queue forever.
    [Fact]
    public async Task Execute_CallerCancellation_PipelineIgnoresStop_ThrowsAfterGracePeriodAndFreesRunspace()
    {
        using var engine = new RunspaceExecutionEngine(
            NullLogger<RunspaceExecutionEngine>.Instance, minRunspaces: 1, maxRunspaces: 1)
        {
            StopGracePeriod = TimeSpan.FromMilliseconds(300),
        };
        using var cts = new CancellationTokenSource();
        var startedName = $"NodePilot-test-{Guid.NewGuid():N}-started";
        using var started = new EventWaitHandle(false, EventResetMode.ManualReset, startedName);
        var releaseName = $"NodePilot-test-{Guid.NewGuid():N}-release";
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);

        var task = engine.ExecuteAsync(
            new PowerShellExecutionRequest { ScriptText = StartThenBlock(startedName, releaseName) },
            cts.Token);
        try
        {
            (await Task.Run(() => started.WaitOne(TimeSpan.FromSeconds(30)),
                TestContext.Current.CancellationToken)).Should().BeTrue();
            await cts.CancelAsync();

            var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

            thrown.CancellationToken.Should().Be(cts.Token);
        }
        finally { release.Set(); }
        var next = await engine.ExecuteAsync(
                new PowerShellExecutionRequest { ScriptText = "'slot back'" },
                TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        next.Success.Should().BeTrue();
        next.Output.Should().Contain("slot back");
    }

    [Fact]
    public async Task Execute_Timeout_PipelineIgnoresStop_ReturnsTimedOutResultAfterGracePeriod()
    {
        using var engine = new RunspaceExecutionEngine(
            NullLogger<RunspaceExecutionEngine>.Instance, minRunspaces: 1, maxRunspaces: 1)
        {
            StopGracePeriod = TimeSpan.FromMilliseconds(300),
        };
        var startedName = $"NodePilot-test-{Guid.NewGuid():N}-started";
        using var started = new EventWaitHandle(false, EventResetMode.ManualReset, startedName);
        var releaseName = $"NodePilot-test-{Guid.NewGuid():N}-release";
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        try
        {
            var result = await engine.ExecuteAsync(
                    new PowerShellExecutionRequest
                    {
                        ScriptText = StartThenBlock(startedName, releaseName),
                        Timeout = TimeSpan.FromSeconds(5),
                    },
                    TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            started.WaitOne(0).Should().BeTrue("the pipeline must reach the blocking .NET call");
            result.TimedOut.Should().BeTrue("the caller returns before cleanup releases the blocked pipeline");
            result.Success.Should().BeFalse();
            result.Error.Should().Be("Script timed out after 5s");
        }
        finally { release.Set(); }
    }

    private static string StartThenBlock(string startedName, string releaseName) => $$"""
        $release = [System.Threading.EventWaitHandle]::OpenExisting('{{releaseName}}')
        $started = [System.Threading.EventWaitHandle]::OpenExisting('{{startedName}}')
        try {
            [void]$started.Set()
            [void]$release.WaitOne()
        } finally { $release.Dispose(); $started.Dispose() }
        """;

    // The script cannot finish naturally until cleanup releases it. The wait limit above
    // only guards a hung test; it does not measure runner speed or module startup.
    private static string WaitForRelease(string name) => $$"""
        $release = [System.Threading.EventWaitHandle]::OpenExisting('{{name}}')
        try { while (-not $release.WaitOne(50)) { } }
        finally { $release.Dispose() }
        """;

    [Fact]
    public async Task Execute_50ConcurrentCalls_AllCompleteWithCorrectOutput()
    {
        // 50 parallel scripts that each emit a unique tag. Verifies BeginInvoke output
        // streams stay isolated under concurrency and that the runspace pool actually
        // services all of them rather than serialising.
        using var engine = new RunspaceExecutionEngine(
            NullLogger<RunspaceExecutionEngine>.Instance,
            minRunspaces: 4,
            maxRunspaces: 64);

        const int parallelism = 50;
        var tasks = Enumerable.Range(0, parallelism)
            .Select(i => engine.ExecuteAsync(
                new PowerShellExecutionRequest
                {
                    ScriptText = $"Write-Output 'tag-{i}'",
                    Timeout = TimeSpan.FromSeconds(15),
                },
                CancellationToken.None))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Should().HaveCount(parallelism);
        for (var i = 0; i < parallelism; i++)
        {
            results[i].Success.Should().BeTrue($"call #{i} should succeed");
            results[i].Output.Should().Contain($"tag-{i}",
                $"call #{i} must see its own output, not another call's");
        }
    }
}
