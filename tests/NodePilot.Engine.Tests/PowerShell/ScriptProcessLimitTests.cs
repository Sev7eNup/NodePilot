using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Configuration;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

/// <summary>
/// The process engines share a fixed number of slots, so a burst of local script steps cannot
/// start more PowerShell processes than the machine has memory for.
/// </summary>
public class ScriptProcessLimitTests
{
    private const string StartTicks = "[Console]::Out.WriteLine('start=' + [DateTime]::UtcNow.Ticks)";
    private const string EndTicks = "[Console]::Out.WriteLine('end=' + [DateTime]::UtcNow.Ticks)";

    private static long Ticks(PowerShellExecutionResult result, string name)
        => long.Parse(Regex.Match(result.Output, name + @"=(\d+)").Groups[1].Value);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScriptWithoutASlot_WaitsForOne_AndTheWaitIsNotItsTimeout(bool isolated)
    {
        using var slots = new SemaphoreSlim(1);
        var engine = ProcessExecutionEngine.CreateWindowsPowerShell(NullLogger.Instance, processSlots: slots);

        var first = engine.ExecuteAsync(
            new PowerShellExecutionRequest { ScriptText = "Start-Sleep -Seconds 6; " + EndTicks },
            TestContext.Current.CancellationToken);
        slots.CurrentCount.Should().Be(0, "the first script took the only slot before its first await");

        // Its timeout is shorter than the time it has to wait for the slot.
        var second = await engine.ExecuteAsync(
            new PowerShellExecutionRequest { ScriptText = StartTicks, Isolated = isolated, Timeout = TimeSpan.FromSeconds(5) },
            TestContext.Current.CancellationToken);
        var firstResult = await first;

        firstResult.Success.Should().BeTrue(firstResult.Error);
        second.Success.Should().BeTrue(second.Error);
        second.TimedOut.Should().BeFalse();
        firstResult.ProcessWait.Should().Be(TimeSpan.Zero);
        second.ProcessWait.Should().BeGreaterThan(TimeSpan.FromSeconds(1), "the engine reports how long the script waited");
        Ticks(second, "start").Should().BeGreaterThan(Ticks(firstResult, "end"),
            "the second script started after the first had ended");
        slots.CurrentCount.Should().Be(1);
    }

    [WindowsFact]
    public async Task CancelWhileWaitingForASlot_ThrowsTheCancelOfTheEngines_AndTakesNoSlot()
    {
        using var slots = new SemaphoreSlim(0, 1);
        var engine = ProcessExecutionEngine.CreateWindowsPowerShell(NullLogger.Instance, processSlots: slots);
        using var cancel = new CancellationTokenSource();

        var waiting = engine.ExecuteAsync(new PowerShellExecutionRequest { ScriptText = StartTicks }, cancel.Token);
        cancel.Cancel();

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        thrown.Message.Should().Be(IPowerShellExecutionEngine.CancelledMessage);
        slots.CurrentCount.Should().Be(0);
    }

    [WindowsFact]
    public async Task TwoHosts_ShareTheSlots()
    {
        // The factory hands the same slots to both hosts; two engine objects stand in for them.
        using var slots = new SemaphoreSlim(1);
        var oneHost = ProcessExecutionEngine.CreateWindowsPowerShell(NullLogger.Instance, processSlots: slots);
        var otherHost = ProcessExecutionEngine.CreateWindowsPowerShell(NullLogger.Instance, processSlots: slots);

        var first = oneHost.ExecuteAsync(
            new PowerShellExecutionRequest { ScriptText = "Start-Sleep -Seconds 3; " + EndTicks },
            TestContext.Current.CancellationToken);
        var second = await otherHost.ExecuteAsync(
            new PowerShellExecutionRequest { ScriptText = StartTicks }, TestContext.Current.CancellationToken);

        Ticks(second, "start").Should().BeGreaterThan(Ticks(await first, "end"));
    }

    [WindowsFact]
    public async Task ScriptThatRunsIntoItsTimeout_GivesItsSlotBack()
    {
        using var slots = new SemaphoreSlim(1);
        var engine = ProcessExecutionEngine.CreateWindowsPowerShell(NullLogger.Instance, processSlots: slots);

        var result = await engine.ExecuteAsync(
            new PowerShellExecutionRequest { ScriptText = "Start-Sleep -Seconds 60", Timeout = TimeSpan.FromSeconds(1) },
            TestContext.Current.CancellationToken);

        result.TimedOut.Should().BeTrue();
        slots.CurrentCount.Should().Be(1);
    }

    [WindowsFact]
    public async Task WaitForAProcess_LongerThanTheRequestAllows_Throws_AndTakesNoSlot()
    {
        using var slots = new SemaphoreSlim(0, 1);
        var engine = ProcessExecutionEngine.CreateWindowsPowerShell(NullLogger.Instance, processSlots: slots);

        var act = () => engine.ExecuteAsync(
            new PowerShellExecutionRequest { ScriptText = StartTicks, ProcessWaitTimeout = TimeSpan.FromMilliseconds(200) },
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ScriptProcessWaitTimeoutException>();
        slots.CurrentCount.Should().Be(0);
    }

    [WindowsFact]
    public async Task Factory_RunsNoMoreScriptsAtOnceThanThePlanAllows()
    {
        // Two cores give the floor of eight script processes.
        var plan = PerformanceSizing.Create(
            new DetectedResources(2, 8L * 1024 * 1024 * 1024, IsDesktop: false),
            manualTuning: false,
            new Dictionary<string, int?>()) with { ExperimentalMode = true };
        plan.MaxScriptProcesses.Value.Should().Be(8);
        var engine = new PowerShellEngineFactory(NullLoggerFactory.Instance, configuration: null, plan).GetEngine("powershell");

        var results = await Task.WhenAll(Enumerable.Range(0, 14).Select(_ => engine.ExecuteAsync(
            new PowerShellExecutionRequest { ScriptText = StartTicks + "; Start-Sleep -Milliseconds 300; " + EndTicks },
            TestContext.Current.CancellationToken)));

        results.Should().OnlyContain(r => r.Success);
        // Sweep over the start and end times: +1 at a start, -1 at an end.
        var running = 0;
        var mostAtOnce = 0;
        foreach (var (_, change) in results
                     .SelectMany(r => new[] { (At: Ticks(r, "start"), Change: 1), (At: Ticks(r, "end"), Change: -1) })
                     .OrderBy(e => e.At).ThenBy(e => e.Change))
        {
            running += change;
            mostAtOnce = Math.Max(mostAtOnce, running);
        }
        mostAtOnce.Should().BeLessThanOrEqualTo(8);
    }

    [WindowsFact]
    public async Task Factory_OutsideExperimentalMode_LetsNoScriptWaitForAProcess()
    {
        // Same plan, experimental mode off: twelve scripts against a limit of eight, and none of
        // them waits, because the limit is not in force.
        var engine = EngineFactories.Create(experimentalMode: false, processorCount: 2).GetEngine("powershell");

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => engine.ExecuteAsync(
            new PowerShellExecutionRequest { ScriptText = "Start-Sleep -Milliseconds 1500" },
            TestContext.Current.CancellationToken)));

        results.Should().OnlyContain(r => r.Success);
        results.Should().OnlyContain(r => r.ProcessWait == TimeSpan.Zero);
    }

    [Fact]
    public void StartArguments_CarryTheScriptPath_OnlyOutsideExperimentalMode()
    {
        static string Decode(string arguments)
            => System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(arguments[(arguments.LastIndexOf(' ') + 1)..]));

        var perScript = Decode(ProcessExecutionEngine.BootstrapArgumentsFor(@"C:\Temp\nodepilot_abc.ps1"));
        perScript.Should().Contain("nodepilot_abc.ps1").And.NotContain(ProcessExecutionEngine.ScriptPathVariable);

        var constant = Decode(ProcessExecutionEngine.ConstantBootstrapArguments);
        constant.Should().Contain("$env:" + ProcessExecutionEngine.ScriptPathVariable).And.NotContain(".ps1");
    }
}
