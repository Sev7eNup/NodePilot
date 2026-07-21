using System.Diagnostics;
using FluentAssertions;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

/// <summary>
/// Real-process tests for the Windows Job Object launcher. Marked [WindowsFact] so they are
/// SKIPPED (visibly, not silently passed) off-Windows. They spawn powershell.exe and assert the
/// OS-enforced guarantees of isolated mode, each with a backstop assertion so a green result
/// cannot mean "passed for the wrong reason".
/// </summary>
public class IsolatedProcessLauncherTests
{
    private const string Pwsh = "powershell.exe"; // Windows PowerShell — always present on Windows.

    private static string Command(string inner) =>
        $"-NoLogo -NoProfile -NonInteractive -Command \"{inner}\"";

    [Fact]
    public void EffectiveLimits_WhenIsolationRequestedWithoutCaps_AppliesSafeDefaults()
    {
        var limits = IsolatedProcessLauncher.EffectiveLimits(new ProcessIsolationLimits());

        limits.Should().NotBeNull();
        limits!.MemoryLimitMb.Should().Be(IsolatedProcessLauncher.DefaultMemoryLimitMb);
        limits.MaxProcesses.Should().Be(IsolatedProcessLauncher.DefaultMaxProcesses);
    }

    [Fact]
    public void EffectiveLimits_PreservesExplicitCaps()
    {
        var limits = IsolatedProcessLauncher.EffectiveLimits(new ProcessIsolationLimits
        {
            MemoryLimitMb = 256,
            MaxProcesses = 3,
        });

        limits!.MemoryLimitMb.Should().Be(256);
        limits.MaxProcesses.Should().Be(3);
    }

    [WindowsFact]
    public async Task Launch_CapturesStdoutAndStderr()
    {
        using var p = IsolatedProcessLauncher.Launch(
            Pwsh,
            Command("Write-Output 'hello-iso'; [Console]::Error.WriteLine('err-iso')"),
            Path.GetTempPath(),
            limits: null);

        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(CancellationToken.None);
        p.Terminate();
        var stdout = await outTask;
        var stderr = await errTask;

        p.GetExitCode().Should().Be(0);
        stdout.Should().Contain("hello-iso");
        stderr.Should().Contain("err-iso");
    }

    [WindowsFact]
    public async Task Launch_KillOnClose_TerminatesProcessWhenDisposed()
    {
        var p = IsolatedProcessLauncher.Launch(
            Pwsh,
            Command("Start-Sleep -Seconds 60"),
            Path.GetTempPath(),
            limits: null);

        // Capture the OS handle while alive: a held Process object survives the process exit and is
        // immune to PID reuse, so HasExited is a reliable liveness check (no bare-PID race).
        using var child = Process.GetProcessById(p.ProcessId);
        child.HasExited.Should().BeFalse("the sleeping child must be alive before the job handle closes");

        p.Dispose(); // closing the job handle ⇒ KILL_ON_JOB_CLOSE reaps the tree

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!child.HasExited && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        child.HasExited.Should().BeTrue("KILL_ON_JOB_CLOSE must reap the process when the job handle closes");
    }

    [WindowsFact]
    public async Task Launch_JobMemoryLimit_ScriptAllocationFails()
    {
        using var p = IsolatedProcessLauncher.Launch(
            Pwsh,
            // 1.5 GB allocation under a 512 MB aggregate job cap → the commit fails (OOM); the
            // kernel does NOT terminate — the script sees the failure and exits non-zero.
            Command("$ErrorActionPreference='Stop'; $b=[byte[]]::new(1500MB); [Console]::Out.Write('ALLOCATED ' + $b.Length)"),
            Path.GetTempPath(),
            new ProcessIsolationLimits { MemoryLimitMb = 512 });

        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(CancellationToken.None);
        p.Terminate();
        var stdout = await outTask;
        var stderr = await errTask;

        p.GetExitCode().Should().NotBe(0, "the 1.5 GB allocation must fail under a 512 MB job-memory cap");
        stdout.Should().NotContain("ALLOCATED", "the allocation should have failed before printing success");
        stderr.Should().Contain("OutOfMemory", "the failure cause must be the memory cap (OutOfMemoryException), not an unrelated error");
    }

    [WindowsFact]
    public async Task Launch_MaxProcesses_BlocksChildSpawn()
    {
        using var p = IsolatedProcessLauncher.Launch(
            Pwsh,
            // Active-process limit 1 = just the root; any child spawn fails with ERROR_NOT_ENOUGH_QUOTA.
            Command("$ErrorActionPreference='Stop'; Start-Process cmd -ArgumentList '/c','exit' | Out-Null; Write-Output 'SPAWNED'"),
            Path.GetTempPath(),
            new ProcessIsolationLimits { MaxProcesses = 1 });

        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(CancellationToken.None);
        p.Terminate();
        var stdout = await outTask;
        var stderr = await errTask;

        p.GetExitCode().Should().NotBe(0, "spawning a 2nd process must fail when the job active-process limit is 1");
        stdout.Should().NotContain("SPAWNED", "the child spawn must have failed before reaching the success marker");
        stderr.Should().NotBeNullOrWhiteSpace("a spawn-denied error must surface on stderr (not an exit code from an unrelated cause)");
    }

    [WindowsFact]
    public async Task Launch_RootExitsChildHoldsPipe_DoesNotHangOnEof()
    {
        // Root starts a long-lived child (inheriting stdout via -NoNewWindow) then exits at once.
        // Without "Terminate before awaiting EOF" the stdout read would hang on the surviving child.
        // (Reap itself is proven by Launch_KillOnClose; this test targets the no-hang ordering.)
        using var p = IsolatedProcessLauncher.Launch(
            Pwsh,
            Command("Start-Process powershell -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 30' -NoNewWindow | Out-Null; Write-Output 'root-done'"),
            Path.GetTempPath(),
            limits: null);

        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(CancellationToken.None); // root exits quickly
        p.Terminate();                                     // reap survivors → close inherited pipe ends

        // .WaitAsync turns a regression (EOF hang) into a TimeoutException instead of blocking CI.
        var stdout = await outTask.WaitAsync(TimeSpan.FromSeconds(20));
        await errTask.WaitAsync(TimeSpan.FromSeconds(20));

        stdout.Should().Contain("root-done", "the root's output must surface even though a child held the pipe");
    }
}
