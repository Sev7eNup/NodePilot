#requires -Version 5.1
# Runs the scripts of the shipped example workflows against synthetic evidence: the temp
# cleanup keeps every run's archive, the host health check alerts on faults and missing
# evidence, and the Windows Update check applies freshnessDays.
[CmdletBinding()]
param()
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$script:passed = 0
function Assert-Example {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
    $script:passed++
}

function Read-Example {
    param([string]$Name)
    $bundle = Get-Content -LiteralPath (Join-Path $PSScriptRoot $Name) -Raw -Encoding UTF8 | ConvertFrom-Json
    $nodes = @{}
    foreach ($node in $bundle.workflows[0].definition.nodes) { $nodes[$node.id] = $node }
    return $nodes
}

# The engine inserts a resolved value into runScript as a single-quoted literal.
function ConvertTo-QuotedLiteral { param([string]$Value) "'" + $Value.Replace("'", "''") + "'" }
function Resolve-Script {
    param([string]$Script, [hashtable]$Values)
    foreach ($template in $Values.Keys) { $Script = $Script.Replace($template, (ConvertTo-QuotedLiteral $Values[$template])) }
    return $Script
}
# Runs a step script in its own scope, without strict mode like the engine, and returns the
# named variables it assigned.
function Invoke-StepScript {
    param([string]$Script, [string[]]$Capture)
    $probe = ($Capture | ForEach-Object { "'$_' = `$$_" }) -join '; '
    $result = @(& ([scriptblock]::Create("Set-StrictMode -Off`n" + $Script + "`n[pscustomobject]@{ $probe }")))
    return $result[-1]
}

$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('np-examples-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox | Out-Null
try {
    # --- temp cleanup: one archive per source and run, never replaced ---------------------
    $cleanup = Read-Example 'example-temp-file-cleanup-workflow.json'
    Assert-Example ($cleanup['archive'].data.config.destination -eq '{{archivePath.param.archiveFile}}') 'The archive does not use the per-run path.'
    Assert-Example ($cleanup['archive'].data.config.force -eq $false) 'The archive may replace an existing file.'
    Assert-Example ($cleanup['checksum'].data.config.path -eq '{{archive.param.destination}}') 'The checksum verifies a different file than the archive wrote.'
    Assert-Example ($cleanup['ret'].data.config.data.archive -eq '{{archive.param.destination}}') 'The returned archive path is not the written one.'

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $source = Join-Path $sandbox 'Temp Folder'
    $archiveRoot = Join-Path $sandbox 'archive'
    New-Item -ItemType Directory -Path $source, $archiveRoot | Out-Null
    $values = @{ '{{trg.param.path}}' = $source; '{{trg.param.archiveRoot}}' = $archiveRoot; '{{trg.param.olderThanDays}}' = '30' }
    $pathScript = Resolve-Script $cleanup['archivePath'].data.config.script $values
    $purgeScript = Resolve-Script $cleanup['purge'].data.config.script $values
    $archives = @()
    foreach ($run in 1, 2) {
        $aged = Join-Path $source "aged-$run.log"
        Set-Content -LiteralPath $aged -Value "run $run"
        (Get-Item -LiteralPath $aged).LastWriteTime = (Get-Date).AddDays(-40)
        Set-Content -LiteralPath (Join-Path $source 'current.log') -Value "run $run"
        $archive = (Invoke-StepScript -Script $pathScript -Capture 'archiveFile').archiveFile
        Assert-Example ([IO.Path]::GetDirectoryName($archive) -eq $archiveRoot) "Run $run archives outside archiveRoot: $archive"
        Assert-Example (-not (Test-Path -LiteralPath $archive)) "Run $run reuses an existing archive path."
        # Stand-in for zipOperation with force=false: compress fails if the destination exists.
        [IO.Compression.ZipFile]::CreateFromDirectory($source, $archive)
        $null = & ([scriptblock]::Create($purgeScript))
        Assert-Example (-not (Test-Path -LiteralPath $aged)) "Run $run did not purge its aged file."
        $archives += $archive
    }
    Assert-Example ($archives[0] -ne $archives[1]) 'Two runs produced the same archive path.'
    $first = [IO.Compression.ZipFile]::OpenRead($archives[0])
    try { $entries = @($first.Entries | ForEach-Object { $_.FullName }) } finally { $first.Dispose() }
    Assert-Example ($entries -contains 'aged-1.log') "The second run lost the first run's archived original."

    # --- host health check: faults and missing evidence take the alert path --------------
    $health = Read-Example 'readme-showcase-workflow.json'
    Assert-Example ($health['issues'].data.config.defaultCaseName -eq 'issues-found') 'An undecided verdict defaults to all-green.'
    $rawHealth = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'readme-showcase-workflow.json') -Raw
    Assert-Example (-not ($rawHealth -match 'SRV-|issuesFound = ''false''\\n')) 'The example still reports remote targets or a hardcoded verdict.'

    function Get-PSDrive { [CmdletBinding()] param($Name, $PSProvider) if ($null -eq $script:freeBytes) { throw 'drive not ready' }; [pscustomobject]@{ Free = $script:freeBytes } }
    function Get-Service { [CmdletBinding()] param($Name) if ($script:services.ContainsKey($Name)) { [pscustomobject]@{ Name = $Name; Status = $script:services[$Name] } } }
    function Get-WinEvent {
        [CmdletBinding()] param($FilterHashtable)
        if ($script:eventMode -eq 'none') { $PSCmdlet.ThrowTerminatingError([Management.Automation.ErrorRecord]::new([Exception]::new('No events were found.'), 'NoMatchingEventsFound', 'ObjectNotFound', $null)) }
        if ($script:eventMode -eq 'denied') { throw 'Access is denied.' }
        [pscustomobject]@{ Id = 41; TimeCreated = (Get-Date).AddHours(-2) }
    }
    $diskCases = @(@{ Free = 50GB; State = 'ok' }, @{ Free = 2GB; State = 'fault' }, @{ Free = $null; State = 'unavailable' })
    foreach ($case in $diskCases) {
        $script:freeBytes = $case.Free
        $state = (Invoke-StepScript -Script $health['disk'].data.config.script -Capture 'diskState').diskState
        Assert-Example ($state -eq $case.State) "Disk probe with $($case.Free) bytes free reported '$state'."
    }
    $running = @{ EventLog = 'Running'; Schedule = 'Running'; WinRM = 'Running' }
    foreach ($case in @(@{ Services = $running; State = 'ok' },
                        @{ Services = @{ EventLog = 'Running'; Schedule = 'Stopped'; WinRM = 'Running' }; State = 'fault' },
                        @{ Services = @{ EventLog = 'Running'; Schedule = 'Running' }; State = 'unavailable' })) {
        $script:services = $case.Services
        $state = (Invoke-StepScript -Script $health['svc'].data.config.script -Capture 'svcState').svcState
        Assert-Example ($state -eq $case.State) "Service probe reported '$state' instead of '$($case.State)'."
    }
    foreach ($case in @(@{ Mode = 'none'; State = 'ok' }, @{ Mode = 'critical'; State = 'fault' }, @{ Mode = 'denied'; State = 'unavailable' })) {
        $script:eventMode = $case.Mode
        $state = (Invoke-StepScript -Script $health['evt'].data.config.script -Capture 'evtState').evtState
        Assert-Example ($state -eq $case.State) "Event probe in mode '$($case.Mode)' reported '$state'."
    }

    $healthy = @{
        '{{disk.success}}' = 'true'; '{{svc.success}}' = 'true'; '{{evt.success}}' = 'true'
        '{{disk.param.diskState}}' = 'ok'; '{{svc.param.svcState}}' = 'ok'; '{{evt.param.evtState}}' = 'ok'
        '{{disk.param.diskDetail}}' = 'd'; '{{svc.param.svcDetail}}' = 's'; '{{evt.param.evtDetail}}' = 'e'
    }
    $aggregate = $health['agg'].data.config.script
    Assert-Example ((Invoke-StepScript -Script (Resolve-Script $aggregate $healthy) -Capture 'issuesFound').issuesFound -eq 'false') 'Three healthy probes raised an issue.'
    foreach ($fault in @(@{ '{{disk.param.diskState}}' = 'fault' }, @{ '{{svc.param.svcState}}' = 'unavailable' }, @{ '{{evt.param.evtState}}' = 'fault' })) {
        $values = $healthy.Clone()
        foreach ($key in $fault.Keys) { $values[$key] = $fault[$key] }
        Assert-Example ((Invoke-StepScript -Script (Resolve-Script $aggregate $values) -Capture 'issuesFound').issuesFound -eq 'true') "A probe result '$($fault.Values)' produced a healthy verdict."
    }
    # A failed probe publishes no state, so its templates stay unresolved in the script.
    $values = $healthy.Clone()
    $values['{{svc.success}}'] = 'false'
    $values.Remove('{{svc.param.svcState}}')
    $values.Remove('{{svc.param.svcDetail}}')
    Assert-Example ((Invoke-StepScript -Script (Resolve-Script $aggregate $values) -Capture 'issuesFound').issuesFound -eq 'true') 'A probe without evidence produced a healthy verdict.'

    # --- Windows Update health: healthy requires an install within freshnessDays ---------
    $wu = Read-Example 'example-windows-update-health-workflow.json'
    $decision = $wu['decision_state'].data.config
    $healthyCase = @($decision.cases | Where-Object { $_.name -eq 'healthy' })[0]
    $freshGate = @($healthyCase.condition.children | Where-Object {
            $_.left.stepId -eq 'read_tails' -and $_.left.paramName -eq 'updateFresh' -and $_.op -eq '==' -and $_.right.value -eq 'true' })
    Assert-Example ($healthyCase.condition.op -eq 'AND' -and $freshGate.Count -eq 1) 'The healthy case does not require a fresh install.'
    Assert-Example ($decision.defaultCaseName -ne 'healthy') 'Unclassified evidence defaults to healthy.'

    $wuLog = Join-Path $sandbox 'WindowsUpdate.log'
    $cbsLog = Join-Path $sandbox 'CBS.log'
    Set-Content -LiteralPath $wuLog -Value 'scan completed'
    Set-Content -LiteralPath $cbsLog -Value 'session completed'
    $tailScript = Resolve-Script $wu['read_tails'].data.config.script @{
        '{{gen_log.param.wuLogPath}}' = $wuLog; '{{gen_log.param.cbsLogPath}}' = $cbsLog
        '{{trig.param.tailLines}}' = '200'; '{{trig.param.freshnessDays}}' = '14'
    }
    function Get-HotFix {
        [CmdletBinding()] param()
        if ($script:hotfixMode -eq 'throw') { throw 'RPC server unavailable' }
        foreach ($age in $script:hotfixAges) {
            [pscustomobject]@{ HotFixID = "KB$age"; InstalledOn = $(if ($null -eq $age) { $null } else { (Get-Date).Date.AddDays(-$age) }) }
        }
    }
    foreach ($case in @(@{ Ages = @(40, 3); Fresh = 'true' }, @{ Ages = @(14); Fresh = 'true' }, @{ Ages = @(15, 90); Fresh = 'false' },
                        @{ Ages = @(); Fresh = 'unknown' }, @{ Ages = @($null); Fresh = 'unknown' }, @{ Ages = @(1); Mode = 'throw'; Fresh = 'unknown' })) {
        $script:hotfixAges = $case.Ages
        $script:hotfixMode = $(if ($case.ContainsKey('Mode')) { $case.Mode } else { '' })
        $fresh = (Invoke-StepScript -Script $tailScript -Capture 'updateFresh').updateFresh
        Assert-Example ($fresh -eq $case.Fresh) "Hotfix ages '$($case.Ages -join ',')' ($($script:hotfixMode)) gave updateFresh '$fresh' instead of '$($case.Fresh)'."
    }
} finally {
    Remove-Item -LiteralPath $sandbox -Recurse -Force
}

Write-Host "Example workflow scripts: $script:passed assertions passed." -ForegroundColor Green
