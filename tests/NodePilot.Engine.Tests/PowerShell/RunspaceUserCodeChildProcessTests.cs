using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

/// <summary>
/// User code in the in-process pool (engine "runspace") that calls Windows PowerShell directly.
/// The pool has rewritten this process's PSModulePath, but PowerShell restores the Windows
/// PowerShell module path for a direct native call to powershell.exe. The docs rely on this.
/// Children started through cmd, Start-Process or System.Diagnostics.Process inherit the rewritten
/// path; that is a documented limitation of the in-process engine.
/// </summary>
public sealed class RunspaceUserCodeChildProcessTests : IDisposable
{
    private readonly RunspaceExecutionEngine _engine = new(NullLogger.Instance, 1, 1);

    public void Dispose() => _engine.Dispose();

    [Fact]
    public async Task DirectNativeCallToWindowsPowerShell_LoadsItsOwnCoreModules()
    {
        var powerShell = Path.Join(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("(New-Guid).Guid"));

        var result = await _engine.ExecuteAsync(
            new PowerShellExecutionRequest
            {
                ScriptText = $"& '{powerShell}' -NoProfile -NonInteractive -EncodedCommand {encoded} 2>&1 | Out-String",
                Timeout = TimeSpan.FromSeconds(60),
            },
            TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue(result.Error);
        var (clean, _, _) = PowerShellActivitySupport.ExtractMarkers(result.Output, "step-1", NullLogger.Instance);
        clean.Trim().Should().MatchRegex("^[0-9a-f-]{36}$");
    }
}
