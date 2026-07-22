using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

/// <summary>
/// Branch coverage for the ISOLATED orchestration in <see cref="ProcessExecutionEngine"/>
/// (ExecuteIsolatedWindowsAsync) — the glue that turns the Job-Object launcher into a step
/// result. These are exactly the abnormal paths the isolation feature exists to make safe
/// (timeout, cancel, limit-attribution, native-launch failure) and the defensive runspace guard.
/// </summary>
public class ProcessIsolationEngineTests
{
    private static IPowerShellExecutionEngine IsolatedPowerShell() =>
        new PowerShellEngineFactory(NullLoggerFactory.Instance).GetEngine("powershell", isolated: true);

    [WindowsFact]
    public async Task ExecuteIsolated_Timeout_ReturnsTimedOutResult()
    {
        var engine = IsolatedPowerShell();

        var result = await engine.ExecuteAsync(
            new PowerShellExecutionRequest
            {
                ScriptText = "Start-Sleep -Seconds 60",
                Engine = "powershell",
                Isolated = true,
                Timeout = TimeSpan.FromMilliseconds(500),
            },
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.TimedOut.Should().BeTrue("the isolated timeout branch must flag TimedOut, not a generic failure");
        result.Error.Should().Contain("timed out");
    }

    [WindowsFact]
    public async Task ExecuteIsolated_CallerCancellation_ReturnsCancelledNotTimedOut()
    {
        var engine = IsolatedPowerShell();
        using var cts = new CancellationTokenSource();

        var sw = Stopwatch.StartNew();
        var task = engine.ExecuteAsync(
            new PowerShellExecutionRequest
            {
                ScriptText = "Start-Sleep -Seconds 60",
                Engine = "powershell",
                Isolated = true,
            },
            cts.Token);

        await Task.Delay(400);
        cts.Cancel();
        var result = await task;
        sw.Stop();

        result.Success.Should().BeFalse();
        result.TimedOut.Should().BeFalse("caller cancellation is distinct from a timeout");
        result.Error.Should().Be("Script execution cancelled");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "cancel must tear the job down promptly, not wait out the 60s sleep");
    }

    [WindowsFact]
    public async Task ExecuteIsolated_AbnormalExitWithMemoryCap_AppendsHedgedLimitHint()
    {
        var engine = IsolatedPowerShell();

        var result = await engine.ExecuteAsync(
            new PowerShellExecutionRequest
            {
                ScriptText = "$ErrorActionPreference='Stop'; $b=[byte[]]::new(1500MB)",
                Engine = "powershell",
                Isolated = true,
                IsolationLimits = new ProcessIsolationLimits { MemoryLimitMb = 512 },
            },
            CancellationToken.None);

        result.Success.Should().BeFalse();
        // The hedged hint must be appended because a cap was set and the script exited non-zero.
        result.Error.Should().Contain("process-isolation memory/process limit was active");
    }

    [WindowsFact]
    public async Task ExecuteIsolated_NativeLaunchFailure_ReturnsCleanFailedResult()
    {
        // An engine pointed at a non-existent executable makes IsolatedProcessLauncher.Launch throw
        // a Win32Exception (CreateProcess fails). The catch must turn that into a clean step failure
        // instead of faulting the whole run.
        var engine = new ProcessExecutionEngine(
            "pwsh", @"Z:\nodepilot-does-not-exist\nope.exe", available: true, NullLogger.Instance);

        var result = await engine.ExecuteAsync(
            new PowerShellExecutionRequest
            {
                ScriptText = "Write-Output 'x'",
                Engine = "pwsh",
                Isolated = true,
            },
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().StartWith("Isolated execution failed:");
    }

    [WindowsFact]
    public async Task ExecuteIsolated_NormalExit_CapturesOutputAndReturnsPromptly()
    {
        // Regression for the bounded drain: a script that exits normally drains stdout/stderr on EOF
        // and returns immediately — it must NOT wait out the drain grace, and output must survive.
        var engine = IsolatedPowerShell();

        var sw = Stopwatch.StartNew();
        var result = await engine.ExecuteAsync(
            new PowerShellExecutionRequest
            {
                ScriptText = "Write-Output 'hello-drain'",
                Engine = "powershell",
                Isolated = true,
            },
            CancellationToken.None);
        sw.Stop();

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("hello-drain");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15),
            "a normal isolated run reaches EOF and drains at once, never waiting out the grace");
    }

    [WindowsFact]
    public async Task ConcurrentIsolatedAndProcessSpawns_AllComplete_NoHang()
    {
        // Part A (ProcessSpawnCoordinator): isolated launches (inheritable pipe handles) run
        // concurrently with the non-isolated Process.Start path (bInheritHandles:true, no HANDLE_LIST
        // — the leak vector). The spawn gate serializes both so no isolated read can hang on a handle
        // leaked into a sibling spawn. Asserts the whole fan-out completes (no wedged step) and every
        // run succeeds. Also proves the gate does not deadlock under contention.
        var factory = new PowerShellEngineFactory(NullLoggerFactory.Instance);
        var isolated = factory.GetEngine("powershell", isolated: true);
        var process = factory.GetEngine("powershell"); // non-isolated → ProcessExecutionEngine.Process.Start

        var tasks = new List<Task<PowerShellExecutionResult>>();
        for (var i = 0; i < 8; i++)
        {
            tasks.Add(isolated.ExecuteAsync(
                new PowerShellExecutionRequest { ScriptText = "Write-Output \"iso-$PID\"", Engine = "powershell", Isolated = true },
                CancellationToken.None));
            tasks.Add(process.ExecuteAsync(
                new PowerShellExecutionRequest { ScriptText = "Write-Output \"proc-$PID\"", Engine = "powershell" },
                CancellationToken.None));
        }

        var all = Task.WhenAll(tasks);
        var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(120)));
        finished.Should().BeSameAs(all, "no isolated read may hang on a leaked pipe handle — the spawn gate prevents cross-inheritance");
        (await all).Should().OnlyContain(r => r.Success, "every concurrent isolated/process run should succeed");
    }

    [Fact]
    public async Task RunspaceEngine_IsolatedRequest_RejectsWithoutRunningInProcess()
    {
        // Defensive guard: the in-process pool can never honor isolation. Host-independent — the
        // guard returns before any runspace work, so no real PowerShell is needed.
        using var engine = new RunspaceExecutionEngine(
            NullLogger<RunspaceExecutionEngine>.Instance, minRunspaces: 1, maxRunspaces: 2);

        var result = await engine.ExecuteAsync(
            new PowerShellExecutionRequest { ScriptText = "Write-Output 'x'", Isolated = true },
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("cannot honor process isolation");
    }
}
