using System.Diagnostics;
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

    [Theory]
    [InlineData("Collection was modified; enumeration operation may not execute.", true)]
    [InlineData("collection WAS modified during enumeration", true)]                     // case-insensitive
    [InlineData("Some completely different error", false)]
    [InlineData("Script timed out after 30s", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsModuleLoadRace_DetectsTransientRaceSignatureOnly(string? error, bool expected)
    {
        RunspaceExecutionEngine.IsModuleLoadRace(error).Should().Be(expected);
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

        var task = engine.ExecuteAsync(
            new PowerShellExecutionRequest { ScriptText = StartThenBlock(startedName, blockMs: 4000) },
            cts.Token);
        (await Task.Run(() => started.WaitOne(TimeSpan.FromSeconds(30)),
            TestContext.Current.CancellationToken)).Should().BeTrue();
        var sw = Stopwatch.StartNew();
        await cts.CancelAsync();

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3), "the caller is released after the grace period, not after the blocking call");
        thrown.CancellationToken.Should().Be(cts.Token);
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
        var sw = Stopwatch.StartNew();

        var result = await engine.ExecuteAsync(
                new PowerShellExecutionRequest { ScriptText = StartThenBlock(startedName, blockMs: 5000), Timeout = TimeSpan.FromSeconds(1) },
                TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4), "the caller is released after timeout plus grace period, not after the blocking call");
        result.TimedOut.Should().BeTrue("an internal timeout stays a failed result, never a cancellation");
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Script timed out after 1s");
    }

    private static string StartThenBlock(string startedName, int blockMs) => $$"""
        $started = [System.Threading.EventWaitHandle]::OpenExisting('{{startedName}}')
        try { [void]$started.Set() } finally { $started.Dispose() }
        [System.Threading.Thread]::Sleep({{blockMs}})
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
