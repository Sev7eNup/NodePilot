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
    // It has to be given to the engine as well as to the step: the engine timeout is the outer
    // wall clock and cuts a slow drain off before the step budget is ever reached.
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
            }, CaptureBudgetSeconds);
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
        }, CaptureBudgetSeconds);
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
        // Same budget as the capture tests above, and for the same reason: this one starts a real
        // powershell.exe, parses both rendered steps and spawns cmd.exe three times, all of which
        // a loaded CI runner stretches well past a local run.
        var engine = ProcessExecutionEngine.CreateWindowsPowerShell(NullLogger.Instance);
        var result = await engine.ExecuteAsync(new PowerShellExecutionRequest {
            ScriptText = script, Timeout = TimeSpan.FromSeconds(CaptureBudgetSeconds)
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(CaptureBudgetSeconds + 15), TestContext.Current.CancellationToken);
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
    public async Task CallerCancellation_ReleasesJobsAndKillsTheWaitedChild()
    {
        using var engine = CreateEngine();
        using var cts = new CancellationTokenSource();
        var suffix = Guid.NewGuid().ToString("N");
        var eventName = "np-program-started-" + suffix;
        // The child publishes its own PID before signalling, so the kill can be asserted on the
        // process itself: the resource counters say nothing about it, and the script's `finally`
        // only runs if PowerShell.Stop() unwinds the pipeline.
        var pidFile = Path.Join(Path.GetTempPath(), $"np-program-pid-{suffix}.txt");
        var marker = Path.Join(Path.GetTempPath(), $"np-program-survived-{suffix}.txt");
        using var started = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        var childScript =
            $"[System.IO.File]::WriteAllText('{PsLiteral(pidFile)}', [string]$PID); "
            + $"$ready=[System.Threading.EventWaitHandle]::OpenExisting('{eventName}'); [void]$ready.Set(); $ready.Dispose(); "
            + $"Start-Sleep -Seconds 30; [System.IO.File]::WriteAllText('{PsLiteral(marker)}', 'survived')";
        var script = new Accessor().Render(JsonSerializer.SerializeToElement(new {
            filePath = PowerShellPath, arguments = EncodedCommand(childScript), timeoutSeconds = 60
        }));
        var execution = engine.ExecuteAsync(new PowerShellExecutionRequest {
            ScriptText = script, Timeout = TimeSpan.FromSeconds(90)
        }, cts.Token);
        try
        {
            (await Task.Run(() => started.WaitOne(TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken))
                .Should().BeTrue("the real child process must start before cancellation");
            var childPid = int.Parse(File.ReadAllText(pidFile));
            var cancellation = cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => execution.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
            await cancellation;
            await AssertResources(engine);

            (await WaitForExitAsync(childPid, TimeSpan.FromSeconds(15)))
                .Should().BeTrue("a cancelled step must not leave its waited child running for its full lifetime");
            File.Exists(marker).Should().BeFalse("the child was killed long before its 30s sleep elapsed");
        }
        finally
        {
            await cts.CancelAsync();
            try { await execution.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None); }
            catch (OperationCanceledException ex) { ex.CancellationToken.Should().Be(cts.Token); }
            KillIfAlive(pidFile);
            File.Delete(pidFile);
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task PostExitDrain_IsBoundedByTheDrainGraceAndKeepsBufferedOutput()
    {
        using var engine = CreateEngine();
        var suffix = Guid.NewGuid().ToString("N");
        var eventName = "np-program-holder-" + suffix;
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        // `cmd /c start` hands the inherited stdout handle to a detached grandchild, so the pipe
        // never reaches EOF even though the launched program is long gone. Without the grace the
        // loop would burn the whole step timeout and report a timeout failure.
        var holder = $"$gate=[System.Threading.EventWaitHandle]::OpenExisting('{eventName}'); [void]$gate.WaitOne(60000)";
        var arguments = "/d /c \"echo captured-before-exit & start \"\"\"\" /b "
            + PowerShellPath + " " + EncodedCommand(holder) + "\"";
        var clock = Stopwatch.StartNew();
        try
        {
            var result = await Execute(engine, new {
                filePath = CmdPath, arguments, timeoutSeconds = CaptureBudgetSeconds
            }, CaptureBudgetSeconds);
            clock.Stop();

            result.Success.Should().BeTrue(result.ErrorOutput);
            result.OutputParameters["stdout"].Should().Contain("captured-before-exit");
            result.Output.Should().Contain("output capture incomplete");
            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(CaptureBudgetSeconds),
                "the drain after process exit is bounded by the drain grace, not by the step timeout");
        }
        finally
        {
            release.Set();
        }
        await AssertResources(engine);
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

    // --- Module path of the started program ------------------------------------------------

    // The SDK's $PSHOME is where System.Management.Automation.dll was loaded from; opening the pool
    // puts its Modules folder first in this process's PSModulePath.
    private static readonly string SdkModulePath = Path.Combine(
        Path.GetDirectoryName(typeof(System.Management.Automation.PowerShell).Assembly.Location)!, "Modules");

    private static void AssertPoolPollutedTheHostModulePath() =>
        Environment.GetEnvironmentVariable("PSModulePath").Should().ContainEquivalentOf(SdkModulePath,
            "precondition: the open pool has put the SDK modules into the inherited module path");

    // Loading a module makes a redirected powershell.exe write a progress record to stderr; that
    // is not an error, so it is switched off to keep "stderr is empty" meaningful.
    private static string GuidProbe(string tail = "") =>
        "$ProgressPreference = 'SilentlyContinue'; (New-Guid).Guid" + tail;

    [Fact]
    public async Task LocalWindowsPowerShellChild_GetsItsOwnCoreModules()
    {
        using var engine = CreateEngine();
        AssertPoolPollutedTheHostModulePath();

        var result = await Execute(engine, new {
            filePath = PowerShellPath,
            arguments = EncodedCommand(GuidProbe("; $env:PSModulePath")),
            timeoutSeconds = 30
        }, timeoutSeconds: 40);

        result.Success.Should().BeTrue(result.ErrorOutput);
        result.OutputParameters["exitCode"].Should().Be("0");
        result.OutputParameters["stderr"].Should().BeEmpty();
        var lines = result.OutputParameters["stdout"].Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().MatchRegex("^[0-9a-f-]{36}$");
        lines[1].Should().NotContainEquivalentOf(SdkModulePath);
    }

    [Fact]
    public async Task LocalWindowsPowerShellGrandchild_ThroughCmd_GetsItsOwnCoreModules()
    {
        // cmd inherits the corrected environment and passes it on.
        using var engine = CreateEngine();
        AssertPoolPollutedTheHostModulePath();

        var result = await Execute(engine, new {
            filePath = CmdPath,
            arguments = $"/c {PowerShellPath} {EncodedCommand(GuidProbe())}",
            timeoutSeconds = 30
        }, timeoutSeconds: 40);

        result.Success.Should().BeTrue(result.ErrorOutput);
        result.OutputParameters["stderr"].Should().BeEmpty();
        result.OutputParameters["stdout"].Trim().Should().MatchRegex("^[0-9a-f-]{36}$");
    }

    [Fact]
    public async Task GeneratedScript_RunsUnderWindowsPowerShell51_AsOnTheWinRmPath()
    {
        // A remote step runs the same script in Windows PowerShell 5.1 on the target; the pool
        // above only proves it under PowerShell 7.
        var config = JsonSerializer.SerializeToElement(new {
            filePath = PowerShellPath, arguments = EncodedCommand(GuidProbe()), timeoutSeconds = 30
        });
        var activity = new Accessor();
        var engine = new PowerShellEngineFactory(NullLoggerFactory.Instance).GetEngine("powershell");

        var raw = await engine.ExecuteAsync(new PowerShellExecutionRequest {
            ScriptText = activity.Render(config), Engine = "powershell", Timeout = TimeSpan.FromSeconds(40)
        }, TestContext.Current.CancellationToken);
        raw.Output.Should().NotContain("###NODEPILOT_ERROR###", raw.Error);
        var result = activity.Process(new ActivityResult {
            Success = raw.Success, Output = raw.Output, ErrorOutput = raw.Error, Duration = raw.Duration
        }, config);

        result.Success.Should().BeTrue(result.ErrorOutput);
        result.OutputParameters["stderr"].Should().BeEmpty();
        result.OutputParameters["stdout"].Trim().Should().MatchRegex("^[0-9a-f-]{36}$");
    }

    public static TheoryData<bool, string> UmlautPrograms() => new()
    {
        { false, "cmd" }, { false, "powershell" }, { false, "powershell-utf8" },
        { true, "cmd" }, { true, "powershell" }, { true, "powershell-utf8" },
    };

    [Theory]
    [MemberData(nameof(UmlautPrograms))]
    public async Task CapturedOutput_WithUmlauts_IsDecodedWhetherTheProgramWritesOemOrUtf8(bool inWindowsPowerShell, string program)
    {
        // inWindowsPowerShell: the host a remote step gets over WinRM; otherwise the local pool.
        var config = JsonSerializer.SerializeToElement(program switch
        {
            "cmd" => new { filePath = CmdPath, arguments = "/c echo Größe", timeoutSeconds = 30 },
            "powershell" => new { filePath = PowerShellPath, arguments = EncodedCommand("'Größe'"), timeoutSeconds = 30 },
            _ => new { filePath = PowerShellPath, arguments = EncodedCommand("[Console]::OutputEncoding = [Text.Encoding]::UTF8; 'Größe'"), timeoutSeconds = 30 },
        });
        var activity = new Accessor();
        using var pool = CreateEngine();
        IPowerShellExecutionEngine engine = inWindowsPowerShell
            ? ProcessExecutionEngine.CreateWindowsPowerShell(NullLogger.Instance)
            : pool;

        var raw = await engine.ExecuteAsync(new PowerShellExecutionRequest {
            ScriptText = activity.Render(config), Timeout = TimeSpan.FromSeconds(40)
        }, TestContext.Current.CancellationToken);
        var result = activity.Process(new ActivityResult {
            Success = raw.Success, Output = raw.Output, ErrorOutput = raw.Error, Duration = raw.Duration
        }, config);

        result.Success.Should().BeTrue(result.ErrorOutput);
        result.OutputParameters["stdout"].Trim().Should().Be("Größe");
    }

    private static (StartProgramActivity Activity, StepExecutionContext Context) LocalhostStartProgram(
        NodePilot.Data.NodePilotDbContext db, PowerShellEngineFactory factory)
    {
        var machine = new NodePilot.Core.Models.ManagedMachine
        {
            Id = Guid.NewGuid(), Name = "Local", Hostname = "localhost", WinRmPort = 5985, IsReachable = true,
        };
        db.ManagedMachines.Add(machine);
        db.SaveChanges();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StartProgram:DisallowShellExecute"] = "false" })
            .Build();
        return (new StartProgramActivity(null!, null!, db, factory, configuration),
            new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "program", ResolvedMachine = machine });
    }

    [Theory]
    [InlineData(false, "runspace")]
    [InlineData(true, "powershell")]
    public async Task Localhost_ShellExecuteRunsInAWindowsPowerShellProcess_OtherwiseInThePool(bool useShellExecute, string expected)
    {
        var used = new List<string>();
        var factory = new PowerShellEngineFactory(
            new RecordingEngine("pwsh", used), new RecordingEngine("powershell", used), new RecordingEngine("runspace", used));
        using var db = NodePilot.Engine.Tests.Helpers.TestDbContext.Create();
        var (activity, context) = LocalhostStartProgram(db, factory);

        await activity.ExecuteAsync(context,
            JsonSerializer.SerializeToElement(new { filePath = CmdPath, useShellExecute }),
            TestContext.Current.CancellationToken);

        used.Should().Equal(expected);
    }

    [Fact]
    public async Task Localhost_ProgramTimeout_IsReportedByTheScriptWithPartialOutput()
    {
        using var db = NodePilot.Engine.Tests.Helpers.TestDbContext.Create();
        var (activity, context) = LocalhostStartProgram(db, new PowerShellEngineFactory(NullLoggerFactory.Instance));

        var result = await activity.ExecuteAsync(context,
            JsonSerializer.SerializeToElement(new { filePath = Path.Join(Environment.SystemDirectory, "PING.EXE"), arguments = "-n 30 127.0.0.1", timeoutSeconds = 3 }),
            TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().StartWith("Process or output capture timed out");
        result.OutputParameters["processId"].Should().MatchRegex(@"^\d+$");
        result.OutputParameters["stdout"].Should().Contain("127.0.0.1");
    }

    [Fact]
    public async Task LocalShellExecute_ProgramGetsItsOwnCoreModules()
    {
        // ShellExecute cannot take an environment of its own; the launching Windows PowerShell
        // process hands down the machine's module path instead of the pool's.
        var factory = new PowerShellEngineFactory(NullLoggerFactory.Instance);
        AssertPoolPollutedTheHostModulePath();
        using var db = NodePilot.Engine.Tests.Helpers.TestDbContext.Create();
        var (activity, context) = LocalhostStartProgram(db, factory);
        var output = Path.Join(Path.GetTempPath(), $"np-shell-{Guid.NewGuid():N}.txt");
        try
        {
            var result = await activity.ExecuteAsync(context, JsonSerializer.SerializeToElement(new {
                filePath = PowerShellPath,
                arguments = "-WindowStyle Hidden " + EncodedCommand(GuidProbe($" | Set-Content -LiteralPath '{output}'")),
                useShellExecute = true,
                timeoutSeconds = 30,
            }), TestContext.Current.CancellationToken);

            result.Success.Should().BeTrue(result.ErrorOutput);
            result.OutputParameters["exitCode"].Should().Be("0");
            (await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken))
                .Trim().Should().MatchRegex("^[0-9a-f-]{36}$");
        }
        finally
        {
            File.Delete(output);
        }
    }

    private sealed class RecordingEngine(string engineType, List<string> used) : IPowerShellExecutionEngine
    {
        public string EngineType => engineType;
        public bool IsAvailable => true;

        public Task<PowerShellExecutionResult> ExecuteAsync(PowerShellExecutionRequest request, CancellationToken ct)
        {
            used.Add(engineType);
            return Task.FromResult(new PowerShellExecutionResult { Success = true, Output = "" });
        }
    }

    private static RunspaceExecutionEngine CreateEngine() => new(NullLogger.Instance, 1, 1);

    private static string PsLiteral(string value) => value.Replace("'", "''");

    // Polls instead of WaitForExitAsync: the process is not a child of the test host, so no exit
    // handle is available. An empty process table entry means it is gone.
    private static async Task<bool> WaitForExitAsync(int pid, TimeSpan budget)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < budget)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited) return true;
            }
            catch (ArgumentException) { return true; }
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        return false;
    }

    private static void KillIfAlive(string pidFile)
    {
        if (!File.Exists(pidFile) || !int.TryParse(File.ReadAllText(pidFile), out var pid)) return;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException) { /* Already gone. */ }
        catch (InvalidOperationException) { /* Raced with its own exit. */ }
    }

    private static string EncodedCommand(string script) =>
        "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

    // The engine timeout is the outer wall clock over the whole script, so it has to be at least
    // the step budget the caller asked for. Defaults suit the short probes; the capture tests pass
    // their own.
    private static async Task<PowerShellExecutionResult> Run(
        RunspaceExecutionEngine engine, string script, int timeoutSeconds = 10)
    {
        var result = await engine.ExecuteAsync(new PowerShellExecutionRequest {
            ScriptText = script, Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(timeoutSeconds + 5));
        result.Success.Should().BeTrue(result.Error);
        return result;
    }

    private static async Task<ActivityResult> Execute(
        RunspaceExecutionEngine engine, object settings, int timeoutSeconds = 10)
    {
        var config = JsonSerializer.SerializeToElement(settings);
        var activity = new Accessor();
        var result = await Run(engine, activity.Render(config), timeoutSeconds);
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
