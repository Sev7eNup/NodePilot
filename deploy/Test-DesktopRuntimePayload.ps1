#requires -Version 5.1
<# Runs the desktop build's runtime payload check against synthetic stage directories. The fixture
   DLLs carry only a version resource. Windows PowerShell compiles them in a child process, so no
   fixture assembly is loaded into this session and the fixture directory can be removed. #>
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'desktop\Assert-DesktopRuntimePayload.ps1')

$script:passed = 0
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:passed++
}
function Assert-Rejected {
    param([Parameter(Mandatory)][scriptblock]$Action, [Parameter(Mandatory)][string]$Pattern, [Parameter(Mandatory)][string]$Message)
    $failure = $null
    try { & $Action } catch { $failure = $_ }
    Assert-True ($null -ne $failure -and $failure.Exception.Message -match $Pattern) "$Message ($failure)"
}

# The pin the build publishes with and validates against. 10.0.13 fixed CVE-2026-88092 and
# CVE-2026-70188 in Kestrel; 10.0.14 adds the out-of-band update that followed.
$floor = [version]'10.0.14'
$buildPath = Join-Path $PSScriptRoot 'desktop\Build-DesktopInstaller.ps1'
$tokens = $null; $parseErrors = $null
$buildAst = [Management.Automation.Language.Parser]::ParseFile($buildPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Desktop build script does not parse.' }
$pinAssignment = @($buildAst.EndBlock.Statements | Where-Object {
    $_ -is [Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -eq '$DesktopRuntimeVersion'
})
Assert-True ($pinAssignment.Count -eq 1) 'The desktop build must pin its runtime in one $DesktopRuntimeVersion assignment.'
$pin = [version]($pinAssignment[0].Right.Extent.Text.Trim("'", '"', ' '))
Assert-True ($pin -ge $floor) "The desktop runtime pin $pin is below the patched floor $floor."
$buildText = Get-Content -LiteralPath $buildPath -Raw
Assert-True (@([regex]::Matches($buildText, '"-p:RuntimeFrameworkVersion=\$DesktopRuntimeVersion"')).Count -eq 2) 'The API and client publishes must both use the pin.'
Assert-True (@([regex]::Matches($buildText, 'Assert-DesktopRuntimePayload -AppPath \$(appStage|clientStage) -MinimumVersion \(\[version\]\$DesktopRuntimeVersion\)')).Count -eq 2) 'The API and client payloads must both be validated against the pin.'

$fixtureRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('nodepilot-desktop-runtime-' + [guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
try {
    $compileScript = Join-Path $fixtureRoot 'compile.ps1'
    [IO.File]::WriteAllText($compileScript, @'
param([string]$Path, [string]$Version)
$ErrorActionPreference = 'Stop'
$source = ('[assembly: System.Reflection.AssemblyFileVersion("{0}.0")] ' +
    '[assembly: System.Reflection.AssemblyInformationalVersion("{0}+fixture")] ' +
    'namespace NodePilotFixture {{ public static class Marker {{ }} }}') -f $Version
Add-Type -TypeDefinition $source -OutputAssembly $Path -OutputType Library
'@)
    $binaries = @{}
    foreach ($version in @('10.0.12', [string]$pin)) {
        $binaries[$version] = Join-Path $fixtureRoot "runtime-$version.dll"
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $compileScript -Path $binaries[$version] -Version $version
        if ($LASTEXITCODE -ne 0) { throw "Could not compile the $version fixture." }
    }
    $current = $binaries[[string]$pin]
    $outdated = $binaries['10.0.12']

    function New-Stage {
        param(
            [Parameter(Mandatory)][string]$Name,
            [string]$RuntimeConfigName = 'NodePilot.Api.runtimeconfig.json',
            [string[]]$Frameworks = @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App'),
            [string]$FrameworkVersion = [string]$pin,
            [string[]]$Binaries = @('hostfxr.dll', 'System.Private.CoreLib.dll', 'System.Net.WebSockets.dll',
                                    'System.Net.WebSockets.Client.dll', 'Microsoft.AspNetCore.Server.Kestrel.Core.dll'),
            [string[]]$OutdatedBinaries = @()
        )
        $stage = Join-Path $fixtureRoot $Name
        New-Item -ItemType Directory -Path $stage | Out-Null
        $declared = @($Frameworks | ForEach-Object { '{{"name":"{0}","version":"{1}"}}' -f $_, $FrameworkVersion }) -join ','
        [IO.File]::WriteAllText((Join-Path $stage $RuntimeConfigName),
            '{"runtimeOptions":{"tfm":"net10.0","includedFrameworks":[' + $declared + ']}}')
        foreach ($binary in $Binaries) {
            $source = if ($OutdatedBinaries -contains $binary) { $outdated } else { $current }
            Copy-Item -LiteralPath $source -Destination (Join-Path $stage $binary)
        }
        return $stage
    }

    # A consistent patched API payload passes.
    Assert-DesktopRuntimePayload -AppPath (New-Stage -Name 'api-current') -MinimumVersion $pin
    $script:passed++

    Assert-Rejected { Assert-DesktopRuntimePayload -AppPath (New-Stage -Name 'api-old-frameworks' -FrameworkVersion '10.0.12') -MinimumVersion $pin } `
        "framework 'Microsoft\.NETCore\.App' is 10\.0\.12" 'An outdated framework declaration was accepted.'
    # Mixed stage directories: current declarations, one binary left over from an older publish.
    Assert-Rejected { Assert-DesktopRuntimePayload -AppPath (New-Stage -Name 'api-old-kestrel' -OutdatedBinaries 'Microsoft.AspNetCore.Server.Kestrel.Core.dll') -MinimumVersion $pin } `
        "'Microsoft\.AspNetCore\.Server\.Kestrel\.Core\.dll' is 10\.0\.12" 'An outdated Kestrel DLL in an otherwise current stage was accepted.'
    Assert-Rejected { Assert-DesktopRuntimePayload -AppPath (New-Stage -Name 'api-old-host' -OutdatedBinaries 'hostfxr.dll') -MinimumVersion $pin } `
        "'hostfxr\.dll' is 10\.0\.12" 'An outdated host in an otherwise current stage was accepted.'
    Assert-Rejected { Assert-DesktopRuntimePayload -AppPath (New-Stage -Name 'api-no-aspnet' -Frameworks 'Microsoft.NETCore.App') -MinimumVersion $pin } `
        'exactly one Microsoft\.AspNetCore\.App' 'An API payload without ASP.NET Core was accepted.'
    Assert-Rejected { Assert-DesktopRuntimePayload -AppPath (New-Stage -Name 'api-no-kestrel' -Binaries 'hostfxr.dll', 'System.Private.CoreLib.dll', 'System.Net.WebSockets.dll', 'System.Net.WebSockets.Client.dll') -MinimumVersion $pin } `
        'missing' 'An API payload without its Kestrel DLL was accepted.'

    # The operator clients: their own runtimeconfig, the base runtime only.
    $clientBinaries = @('hostfxr.dll', 'System.Private.CoreLib.dll', 'System.Net.WebSockets.dll', 'System.Net.WebSockets.Client.dll')
    foreach ($client in @('np', 'nodepilot-mcp')) {
        $stage = New-Stage -Name "$client-current" -RuntimeConfigName "$client.runtimeconfig.json" -Frameworks 'Microsoft.NETCore.App' -Binaries $clientBinaries
        Assert-DesktopRuntimePayload -AppPath $stage -MinimumVersion $pin -RuntimeConfigName "$client.runtimeconfig.json" -BaseRuntimeOnly
        $script:passed++
        Assert-Rejected { Assert-DesktopRuntimePayload -AppPath $stage -MinimumVersion $pin } `
            'NodePilot\.Api\.runtimeconfig\.json' "The $client payload passed as the API payload."
        $stale = New-Stage -Name "$client-old-corelib" -RuntimeConfigName "$client.runtimeconfig.json" -Frameworks 'Microsoft.NETCore.App' `
            -Binaries $clientBinaries -OutdatedBinaries 'System.Private.CoreLib.dll'
        Assert-Rejected { Assert-DesktopRuntimePayload -AppPath $stale -MinimumVersion $pin -RuntimeConfigName "$client.runtimeconfig.json" -BaseRuntimeOnly } `
            "'System\.Private\.CoreLib\.dll' is 10\.0\.12" "An outdated runtime in the $client payload was accepted."
        $oldDeclaration = New-Stage -Name "$client-old-framework" -RuntimeConfigName "$client.runtimeconfig.json" -Frameworks 'Microsoft.NETCore.App' `
            -Binaries $clientBinaries -FrameworkVersion '10.0.12'
        Assert-Rejected { Assert-DesktopRuntimePayload -AppPath $oldDeclaration -MinimumVersion $pin -RuntimeConfigName "$client.runtimeconfig.json" -BaseRuntimeOnly } `
            "framework 'Microsoft\.NETCore\.App' is 10\.0\.12" "An outdated framework declaration in the $client payload was accepted."
    }
}
finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $fixtureRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($fixtureRoot) -notlike 'nodepilot-desktop-runtime-*') { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
}
Write-Host "Desktop runtime payload checks passed: $script:passed (pin $pin, outdated and mixed payloads rejected, API and client payloads accepted)."
