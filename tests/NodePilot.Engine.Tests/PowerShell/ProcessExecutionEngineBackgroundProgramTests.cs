using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

/// <summary>
/// A script that starts a background program without a new window hands it the engine's output
/// pipes. The step must end when the script ends (plus the drain grace), not when the program
/// does, and must keep the script's own output.
/// </summary>
public class ProcessExecutionEngineBackgroundProgramTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(1);

    private static readonly string Ping = Path.Join(Environment.SystemDirectory, "PING.EXE");

    private static ProcessExecutionEngine WindowsPowerShell() => new(
        "powershell",
        Path.Join(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
        available: true,
        NullLogger.Instance,
        Grace);

    public static TheoryData<string> Launches => new()
    {
        $"$p = Start-Process -FilePath '{Ping}' -ArgumentList '-n 60 127.0.0.1' -NoNewWindow -PassThru; $programId = [string]$p.Id",
        $"$psi = New-Object System.Diagnostics.ProcessStartInfo '{Ping}', '-n 60 127.0.0.1'; $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $programId = [string][System.Diagnostics.Process]::Start($psi).Id",
    };

    [Theory]
    [MemberData(nameof(Launches))]
    public async Task BackgroundProgramHoldingThePipes_DoesNotHoldTheStep(string launch)
    {
        var sw = Stopwatch.StartNew();
        var result = await WindowsPowerShell().ExecuteAsync(
            new PowerShellExecutionRequest
            {
                ScriptText = launch + "\nWrite-Output 'launched'",
                Engine = "powershell",
                Timeout = TimeSpan.FromSeconds(50),
            },
            TestContext.Current.CancellationToken);
        sw.Stop();

        var (clean, _, parameters) = PowerShellActivitySupport.ExtractMarkers(result.Output, "step-1", NullLogger.Instance);
        try
        {
            result.Success.Should().BeTrue(result.Error);
            result.TimedOut.Should().BeFalse();
            clean.Should().Contain("launched");
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20),
                "the step ends with the script, not with the 60-second background program");
        }
        finally
        {
            if (parameters.TryGetValue("programId", out var id) && int.TryParse(id, out var pid))
            {
                try { using var program = Process.GetProcessById(pid); program.Kill(); }
                catch (ArgumentException) { /* already gone */ }
            }
        }
    }
}
