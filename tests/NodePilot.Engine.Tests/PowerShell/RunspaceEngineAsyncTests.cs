using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

/// <summary>
/// Verifies the async behavior of RunspaceExecutionEngine after the BeginInvoke/EndInvoke
/// port. Three properties matter under load:
///   1. Caller cancellation tears down the running script promptly (used to be impossible
///      with Task.Run(() => ps.Invoke()) — the token only cancelled scheduling).
///   2. Per-script timeout actually stops the pipeline (same rationale).
///   3. Many concurrent ExecuteAsync calls all complete with correct, non-interleaved output.
/// </summary>
public class RunspaceEngineAsyncTests
{
    [Fact]
    public async Task Execute_CallerCancellation_StopsPromptlyAndIsNotTimedOut()
    {
        using var engine = new RunspaceExecutionEngine(
            NullLogger<RunspaceExecutionEngine>.Instance,
            minRunspaces: 1,
            maxRunspaces: 4);

        using var cts = new CancellationTokenSource();

        var sw = Stopwatch.StartNew();
        var task = engine.ExecuteAsync(
            new PowerShellExecutionRequest
            {
                ScriptText = "Start-Sleep -Seconds 30",
                Timeout = TimeSpan.FromMinutes(5),
            },
            cts.Token);

        // Give the pipeline a moment to actually start executing on the runspace.
        await Task.Delay(150);
        cts.Cancel();
        var result = await task;
        sw.Stop();

        result.Success.Should().BeFalse();
        result.TimedOut.Should().BeFalse("caller cancellation is distinct from timeout-fire");
        result.Error.Should().Be("Script execution cancelled");
        // A 30-second sleep cancelled at 150ms must return well under the original sleep.
        // 5 seconds is a generous bound that won't flake under CI load but still proves the
        // pipeline was actively stopped (not waited out).
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
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
    public async Task Execute_TimeoutShorterThanSleep_TimesOutCloseToTimeout()
    {
        using var engine = new RunspaceExecutionEngine(
            NullLogger<RunspaceExecutionEngine>.Instance,
            minRunspaces: 1,
            maxRunspaces: 4);

        var sw = Stopwatch.StartNew();
        var result = await engine.ExecuteAsync(
            new PowerShellExecutionRequest
            {
                ScriptText = "Start-Sleep -Seconds 30",
                Timeout = TimeSpan.FromMilliseconds(300),
            },
            CancellationToken.None);
        sw.Stop();

        result.TimedOut.Should().BeTrue();
        result.Success.Should().BeFalse();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "the pipeline must be stopped, not waited out for the full 30s sleep");
    }

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
