using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

/// <summary>
/// Guards the 2026-07-30 WinPSCompat leak fix. The in-process pool runs on the PowerShell SDK,
/// which ships only the core modules; anything found in the Windows PowerShell 5.1 module path
/// used to be loaded through implicit WinCompat — one `powershell.exe -Version 5.1 -s` child and
/// one never-closed "WinPSCompatSession" per pool runspace. The fix is two-part and each part has
/// its own test here:
///   1. Microsoft.PowerShell.Archive is bundled (PSModules\) and imported eagerly, so
///      Compress-Archive runs natively in-process.
///   2. powershell.config.json next to the SDK assembly sets DisableImplicitWinCompat, so
///      desktop-only cmdlets fail loudly instead of silently spawning compat sessions.
/// </summary>
public class RunspaceEngineWinCompatTests
{
    private static readonly RunspaceExecutionEngine Engine =
        new(NullLogger<RunspaceExecutionEngine>.Instance, 1, 2);

    [Fact]
    public async Task Execute_CompressArchive_UsesBundledModuleInProcess()
    {
        // Roundtrip through Compress-Archive + Expand-Archive, then report where the cmdlet
        // came from. The module path is the discriminator: the bundled copy lives under
        // PSModules\; a WinCompat proxy would live in a remoteIpMoProxy temp path and the
        // System32 copy under WindowsPowerShell\v1.0.
        var result = await Engine.ExecuteAsync(
            new PowerShellExecutionRequest
            {
                ScriptText = """
                    $stage = Join-Path $env:TEMP ('np-wincompat-' + [guid]::NewGuid().ToString('N'))
                    New-Item -ItemType Directory -Path $stage | Out-Null
                    try {
                        Set-Content -Path (Join-Path $stage 'payload.txt') -Value 'np-roundtrip'
                        $zip = Join-Path $stage 'payload.zip'
                        Compress-Archive -Path (Join-Path $stage 'payload.txt') -DestinationPath $zip
                        $out = Join-Path $stage 'out'
                        Expand-Archive -Path $zip -DestinationPath $out
                        Write-Output ('roundtrip=' + (Get-Content (Join-Path $out 'payload.txt')))
                        Write-Output ('module=' + (Get-Command Compress-Archive).Module.Path)
                    }
                    finally {
                        Remove-Item -Path $stage -Recurse -Force -ErrorAction SilentlyContinue
                    }
                    """,
                Timeout = TimeSpan.FromSeconds(60),
            },
            CancellationToken.None);

        result.Success.Should().BeTrue($"Compress-Archive must run natively in the pool (error: {result.Error})");
        result.Output.Should().Contain("roundtrip=np-roundtrip");
        result.Output.Should().Contain("PSModules", "the cmdlet must come from the bundled module, not a WinCompat proxy or the System32 copy");
    }

    [Fact]
    public async Task Execute_DesktopOnlyModule_FailsLoudInsteadOfSpawningCompatSession()
    {
        // The System32 copy of Microsoft.PowerShell.Archive (1.0.1.0) declares edition 'Desktop'
        // only — it is the exact module whose implicit WinCompat load caused the leak. (CDXML
        // modules like ScheduledTasks load natively in PS7 and are NOT compat candidates.)
        // With DisableImplicitWinCompat in force the import must fail terminating, without a
        // WinPSCompatSession. If someone removes the powershell.config.json placement
        // (Directory.Build.targets), WinCompat quietly loads the module, the import SUCCEEDS —
        // and this test goes red, which is exactly its job. Explicit opt-in via
        // `Import-Module -UseWindowsPowerShell` stays possible by design.
        var result = await Engine.ExecuteAsync(
            new PowerShellExecutionRequest
            {
                ScriptText = """
                    $sys32 = 'C:\Windows\System32\WindowsPowerShell\v1.0\Modules\Microsoft.PowerShell.Archive\Microsoft.PowerShell.Archive.psd1'
                    if (-not (Test-Path $sys32)) { throw "probe-invalid: $sys32 missing on this host" }
                    Import-Module $sys32 -ErrorAction Stop
                    Write-Output 'import-succeeded'
                    Write-Output ('sessions=' + ((Get-PSSession | ForEach-Object Name) -join ','))
                    """,
                Timeout = TimeSpan.FromSeconds(60),
            },
            CancellationToken.None);

        result.Success.Should().BeFalse("a Desktop-only module must not be silently served by a WinPSCompat session");
        result.Error.Should().Contain("disabled in the settings file",
            "the failure must be the deliberate DisableImplicitWinCompat refusal, not some unrelated import error");
    }
}
