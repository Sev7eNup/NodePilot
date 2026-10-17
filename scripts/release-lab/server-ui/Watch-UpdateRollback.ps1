#requires -Version 5.1
<# Blocks the isolated fixture's HTTPS port until the setup adapter enters rollback. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^NPUI[A-Za-z0-9_-]*$')][string]$ServiceName,
    [Parameter(Mandatory)][string]$InstallPath,
    [Parameter(Mandatory)][ValidateRange(1024, 65535)][int]$Port,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
$installRoot = (Resolve-Path -LiteralPath $InstallPath).ProviderPath.TrimEnd('\')
$expectedExe = Join-Path $installRoot 'NodePilot.Api.exe'
$service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
if (-not $service -or $service.State -ne 'Running') { throw 'Fixture service must exist and be running before arming.' }
$pathPattern = '^"?' + [regex]::Escape($expectedExe) + '(?:"(?:\s|$)|$|\s)'
if ($service.PathName -notmatch $pathPattern) { throw 'Service executable does not match InstallPath.' }
if (-not (Test-Path -LiteralPath $expectedExe -PathType Leaf)) { throw 'Fixture executable is absent.' }
$bindings = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
if ($bindings.Count -eq 0 -or @($bindings | Where-Object { $_.OwningProcess -ne $service.ProcessId }).Count -gt 0) {
    throw 'Port must currently be served only by the fixture service process.'
}
$evidence = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EvidenceDirectory)
if (Test-Path -LiteralPath $evidence) { throw 'EvidenceDirectory must be new.' }
$null = New-Item -ItemType Directory -Path $evidence
$utf8 = New-Object Text.UTF8Encoding($false)
$listener = $null
$listener6 = $null
$deadline = [DateTime]::UtcNow.AddMinutes(10)
try {
    [IO.File]::WriteAllText((Join-Path $evidence 'armed.txt'), [DateTime]::UtcNow.ToString('o'), $utf8)
    while ((Get-Service -Name $ServiceName).Status -ne 'Stopped') {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Service-stop deadline expired.' }
        Start-Sleep -Milliseconds 100
    }
    $listener = New-Object Net.Sockets.TcpListener ([Net.IPAddress]::Any), $Port
    $listener.Server.ExclusiveAddressUse = $true
    $listener.Start()
    $listener6 = New-Object Net.Sockets.TcpListener ([Net.IPAddress]::IPv6Any), $Port
    $listener6.Server.DualMode = $false
    $listener6.Server.ExclusiveAddressUse = $true
    $listener6.Start()
    [IO.File]::WriteAllText((Join-Path $evidence 'bound.txt'), [DateTime]::UtcNow.ToString('o'), $utf8)
    $progress = ''
    $rolledBack = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not $progress) {
            $workers = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object {
                $_.CommandLine -match 'Invoke-NodePilotSetup\.ps1.*-Mode\s+Apply(?:\s|$)'
            })
            if ($workers.Count -gt 1) { throw 'Multiple Apply workers; cannot identify this test uniquely.' }
            if ($workers.Count -eq 1) {
                if ($workers[0].CommandLine -notmatch '-ProgressFile\s+"([^"]+)"') { throw 'Apply worker has no recognizable progress file.' }
                $progress = $Matches[1]
                if ($workers[0].CommandLine -notmatch '-AnswerFile\s+"([^"]+)"') { throw 'Apply worker has no recognizable answer file.' }
                $answer = [IO.File]::ReadAllText($Matches[1]) | ConvertFrom-Json
                if ($answer.mode -ne 'update' -or $answer.serviceName -ne $ServiceName -or
                    [IO.Path]::GetFullPath($answer.installPath).TrimEnd('\') -ne $installRoot) {
                    throw 'Apply worker targets another installation or mode.'
                }
            }
        }
        if ($progress -and (Test-Path -LiteralPath $progress)) {
            try { $text = [IO.File]::ReadAllText($progress) } catch [IO.IOException] { $text = '' }
            if ($text -match 'rollback\|rollback\|') {
                [IO.File]::WriteAllText((Join-Path $evidence 'rollback.txt'), $text, $utf8)
                $rolledBack = $true
                break
            }
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not $rolledBack) { throw 'Rollback progress was not observed before the deadline.' }
} catch {
    [IO.File]::WriteAllText((Join-Path $evidence 'error.txt'), $_.Exception.Message, $utf8)
    throw
} finally {
    if ($listener) { $listener.Stop() }
    if ($listener6) { $listener6.Stop() }
    [IO.File]::WriteAllText((Join-Path $evidence 'released.txt'), [DateTime]::UtcNow.ToString('o'), $utf8)
}
