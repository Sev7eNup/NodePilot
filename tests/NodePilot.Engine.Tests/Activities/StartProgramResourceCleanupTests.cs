using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Activities;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

public class StartProgramResourceCleanupTests
{
    private static string CmdPath => Path.Join(Environment.SystemDirectory, "cmd.exe");
    private static string PowerShellPath => Path.Join(Environment.SystemDirectory,
        "WindowsPowerShell", "v1.0", "powershell.exe");

    [Fact]
    public async Task RepeatedCalls_ReleaseOwnJobsAndPreserveOutputAndUnrelatedSubscription()
    {
        using var engine = CreateEngine();
        await Run(engine, "Register-ObjectEvent -InputObject (New-Object System.Timers.Timer) -EventName Elapsed -SourceIdentifier np-unrelated -Action {} | Out-Null");
        try
        {
            for (var i = 0; i < 12; i++)
            {
                var result = await Execute(engine, new {
                    filePath = CmdPath, arguments = "/c echo probe-out & echo probe-err 1>&2",
                    timeoutSeconds = 5
                });
                result.Success.Should().BeTrue(result.ErrorOutput);
                result.OutputParameters["stdout"].Trim().Should().Be("probe-out");
                result.OutputParameters["stderr"].Trim().Should().Be("probe-err");
            }

            await AssertResources(engine, expectedJobs: 1, expectedSubscribers: 1);
            (await Run(engine, "Write-Output ('unrelated=' + @(Get-Job -Name np-unrelated).Count)")).Output
                .Should().Contain("unrelated=1");
        }
        finally
        {
            await Run(engine, "Unregister-Event -SourceIdentifier np-unrelated; Get-Job -Name np-unrelated | Remove-Job -Force");
        }
    }

    [Fact]
    public async Task LaunchFailure_ReleasesJobsAndSubscribers()
    {
        using var engine = CreateEngine();
        var missing = Path.Join(Path.GetTempPath(), $"np-missing-{Guid.NewGuid():N}.exe");
        for (var i = 0; i < 3; i++)
        {
            var result = await Execute(engine, new { filePath = missing, timeoutSeconds = 5 });
            result.Success.Should().BeFalse();
            result.ErrorOutput.Should().Contain("launch failed");
        }
        await AssertResources(engine);
    }

    // Wall-clock budget for the capture tests. They assert what the drain loop collects, not how
    // fast it collects it, so the budget only has to outlast a loaded CI runner: the pipe readers
    // complete on the thread pool, and a starved pool delays them far beyond a local run.
    private const int CaptureBudgetSeconds = 60;

    [Fact]
    public async Task FastProcessExit_PreservesAllOutputInOrder()
    {
        using var engine = CreateEngine();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var result = await Execute(engine, new {
                filePath = CmdPath,
                arguments = "/d /c \"(for /l %i in (1,1,128) do @echo out-%i) & (for /l %i in (1,1,128) do @echo err-%i 1>&2)\"",
                timeoutSeconds = CaptureBudgetSeconds
            });
            result.Success.Should().BeTrue(result.ErrorOutput);
            var stdout = result.OutputParameters["stdout"].Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim());
            var stderr = result.OutputParameters["stderr"].Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim());
            stdout.Should().Equal(Enumerable.Range(1, 128).Select(i => $"out-{i}"));
            stderr.Should().Equal(Enumerable.Range(1, 128).Select(i => $"err-{i}"));
        }
        await AssertResources(engine);
    }

    [Fact]
    public async Task OutputBeyondCap_DrainsBothPipesAndReportsTruncation()
    {
        using var engine = CreateEngine();
        var count = StartProgramActivity.MaxOutputBytesPerStream + 4096;
        var result = await Execute(engine, new {
            filePath = PowerShellPath,
            arguments = EncodedCommand($"[Console]::Out.Write(('x' * {count})); [Console]::Error.Write(('y' * {count}))"),
            timeoutSeconds = CaptureBudgetSeconds
        });
        result.Success.Should().BeTrue(result.ErrorOutput);
        result.OutputParameters["stdout"].Length.Should().Be(StartProgramActivity.MaxOutputBytesPerStream);
        result.OutputParameters["stderr"].Length.Should().Be(StartProgramActivity.MaxOutputBytesPerStream);
        result.OutputParameters["stdoutTruncated"].Should().Be("true");
        result.OutputParameters["stderrTruncated"].Should().Be("true");
        await AssertResources(engine);
    }

    [Fact]
    public async Task WindowsPowerShell51_ReleasesJobsAcrossSuccessfulAndFailedCalls()
    {
        var activity = new Accessor();
        var success = activity.Render(JsonSerializer.SerializeToElement(new {
            filePath = CmdPath, arguments = "/c echo legacy-out", timeoutSeconds = 5
        }));
        var failure = activity.Render(JsonSerializer.SerializeToElement(new {
            filePath = Path.Join(Path.GetTempPath(), $"np-missing-{Guid.NewGuid():N}.exe")
        }));
        var script = "1..3 | ForEach-Object { & {\n" + success + "\n}; & {\n" + failure + "\n} }\n"
            + "Write-Output ('resources jobs={0}; subscribers={1}; events={2}' -f @(Get-Job).Count,@(Get-EventSubscriber).Count,@(Get-Event).Count)";
        var engine = ProcessExecutionEngine.CreateWindowsPowerShell(NullLogger.Instance);
        var result = await engine.ExecuteAsync(new PowerShellExecutionRequest {
            ScriptText = script, Timeout = TimeSpan.FromSeconds(20)
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(25), TestContext.Current.CancellationToken);
        result.Success.Should().BeTrue(result.Error);
        result.Output.Should().Contain("legacy-out").And.Contain("resources jobs=0; subscribers=0; events=0");
    }

    [Fact]
    public async Task ProcessTimeout_ReleasesJobsAndStopsProcess()
    {
        using var engine = CreateEngine();
        var result = await Execute(engine, new {
            filePath = PowerShellPath,
            arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 10\"",
            timeoutSeconds = 1
        });
        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("timed out");
        await AssertResources(engine);
        try
        {
            using var process = Process.GetProcessById(int.Parse(result.OutputParameters["processId"]));
            process.HasExited.Should().BeTrue();
        }
        catch (ArgumentException) { /* The process has already left the process table. */ }
    }

    [Fact]
    public async Task CallerCancellation_ReleasesJobsAndLeavesPoolUsable()
    {
        using var engine = CreateEngine();
        using var cts = new CancellationTokenSource();
        var eventName = "np-program-started-" + Guid.NewGuid().ToString("N");
        using var started = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        var childScript = $"$ready=[System.Threading.EventWaitHandle]::OpenExisting('{eventName}'); [void]$ready.Set(); $ready.Dispose(); Start-Sleep -Seconds 2";
        var script = new Accessor().Render(JsonSerializer.SerializeToElement(new {
            filePath = PowerShellPath, arguments = EncodedCommand(childScript), timeoutSeconds = 5
        }));
        var execution = engine.ExecuteAsync(new PowerShellExecutionRequest {
            ScriptText = script, Timeout = TimeSpan.FromSeconds(10)
        }, cts.Token);
        try
        {
            (await Task.Run(() => started.WaitOne(TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken))
                .Should().BeTrue("the real child process must start before cancellation");
            var cancellation = cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => execution.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
            await cancellation;
            await AssertResources(engine);
        }
        finally
        {
            await cts.CancelAsync();
            try { await execution.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None); }
            catch (OperationCanceledException ex) { ex.CancellationToken.Should().Be(cts.Token); }
        }
    }

    [Fact]
    public async Task FireAndForget_ReleasesResourcesAndAllowsChildToFinishWriting()
    {
        using var engine = CreateEngine();
        var suffix = Guid.NewGuid().ToString("N");
        var eventName = "np-program-release-" + suffix;
        var marker = Path.Join(Path.GetTempPath(), $"np-program-{suffix}.txt");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        var childScript = $"$gate=[System.Threading.EventWaitHandle]::OpenExisting('{eventName}'); if (-not $gate.WaitOne(15000)) {{ exit 17 }}; $gate.Dispose(); [Console]::Out.WriteLine('late-out'); [Console]::Error.WriteLine('late-err'); [System.IO.File]::WriteAllText('{marker.Replace("'", "''")}', 'finished')";
        try
        {
            var result = await Execute(engine, new {
                filePath = PowerShellPath, arguments = EncodedCommand(childScript),
                waitForExit = false, timeoutSeconds = 5
            });
            result.Success.Should().BeTrue(result.ErrorOutput);
            result.OutputParameters["waited"].Should().Be("false");
            using var child = Process.GetProcessById(int.Parse(result.OutputParameters["processId"]));
            try
            {
                child.HasExited.Should().BeFalse("fire-and-forget must leave the child running");
                await AssertResources(engine);
                release.Set();
                await child.WaitForExitAsync(TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                child.ExitCode.Should().Be(0);
                File.ReadAllText(marker).Should().Be("finished");
            }
            finally
            {
                release.Set();
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None);
            }
        }
        finally
        {
            release.Set();
            File.Delete(marker);
        }
    }

    private static RunspaceExecutionEngine CreateEngine() => new(NullLogger.Instance, 1, 1);

    private static string EncodedCommand(string script) =>
        "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

    private static async Task<PowerShellExecutionResult> Run(RunspaceExecutionEngine engine, string script)
    {
        var result = await engine.ExecuteAsync(new PowerShellExecutionRequest {
            ScriptText = script, Timeout = TimeSpan.FromSeconds(10)
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15));
        result.Success.Should().BeTrue(result.Error);
        return result;
    }

    private static async Task<ActivityResult> Execute(RunspaceExecutionEngine engine, object settings)
    {
        var config = JsonSerializer.SerializeToElement(settings);
        var activity = new Accessor();
        var result = await Run(engine, activity.Render(config));
        return activity.Process(new ActivityResult {
            Success = result.Success, Output = result.Output, ErrorOutput = result.Error, Duration = result.Duration
        }, config);
    }

    private static async Task AssertResources(RunspaceExecutionEngine engine, int expectedJobs = 0, int expectedSubscribers = 0)
    {
        var result = await Run(engine,
            "Write-Output ('resources jobs={0}; subscribers={1}; events={2}' -f @(Get-Job).Count,@(Get-EventSubscriber).Count,@(Get-Event).Count)");
        result.Output.Should().Contain($"resources jobs={expectedJobs}; subscribers={expectedSubscribers}; events=0");
    }

    private sealed class Accessor() : StartProgramActivity(null!, null!, null!, null!, new ConfigurationBuilder().Build())
    {
        public string Render(JsonElement config) => BuildScript(config,
            new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "program" });
        public ActivityResult Process(ActivityResult result, JsonElement config) => PostProcess(result, config);
    }
}
