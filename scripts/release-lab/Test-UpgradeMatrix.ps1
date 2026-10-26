#requires -Version 5.1
<# Offline regression checks for release selection, scenario expansion and result gates.
   Runs the matrix scripts' planning/summary code with VM operations replaced. #>
[CmdletBinding()]
param()
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UpgradeReleases.ps1')

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Rejected([scriptblock]$Action, [string]$Expected) {
    $message = $null
    try { & $Action | Out-Null } catch { $message = $_.Exception.Message }
    Assert-True ($null -ne $message -and $message -like "*$Expected*") "Expected rejection '$Expected', got '$message'."
}

function Read-ScriptAst([string]$RelativePath) {
    $tokens = $null; $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $RelativePath), [ref]$tokens, [ref]$parseErrors)
    Assert-True ($parseErrors.Count -eq 0) "$RelativePath does not parse: $parseErrors"
    return $ast
}

$serverAst = Read-ScriptAst 'server/Invoke-ServerMatrix.ps1'
$desktopAst = Read-ScriptAst 'desktop/Invoke-DesktopMatrix.ps1'
$planEnd = @($serverAst.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -eq 'Write-Json' })[0].Extent.StartOffset
$planStatements = @($serverAst.EndBlock.Statements | Where-Object {
    $_.Extent.EndOffset -lt $planEnd -and (
        ($_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -in '$scenarios', '$Only') -or
        ($_ -is [System.Management.Automation.Language.IfStatementAst] -and $_.Clauses[0].Item1.Extent.Text -in '$Only', '-not $scenarios'))
})
$serverPlan = [scriptblock]::Create(($planStatements.Extent.Text -join "`n"))
$serverGate = [scriptblock]::Create($serverAst.EndBlock.Statements[-1].Clauses[0].Item1.Extent.Text)
$desktopGate = [scriptblock]::Create($desktopAst.EndBlock.Statements[-1].Clauses[0].Item1.Extent.Text)
$desktopPhase = @($desktopAst.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -eq 'Invoke-Phase' })[0]
$desktopLoop = @($desktopAst.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.ForEachStatementAst] -and $_.Variable.Extent.Text -eq '$release' })[0]

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('nodepilot-upgrade-matrix-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
try {
    # Deliberately unsorted, with a minor boundary and a two-digit patch.
    $releases = @(foreach ($number in '1.4.8', '1.5.0', '1.4.11', '1.4.9', '1.4.10') {
        $serverSetup = Join-Path $fixtureRoot "server-$number.exe"
        $desktopSetup = Join-Path $fixtureRoot "desktop-$number.exe"
        [IO.File]::WriteAllText($serverSetup, $number)
        [IO.File]::WriteAllText($desktopSetup, $number)
        [pscustomobject]@{
            version = $number; serverSetup = $serverSetup; desktopSetup = $desktopSetup
            preinstallRuntimes = $number -eq '1.4.8'; preGrantSystemForLocalSql = $false; canInstallPostgres = $true
        }
    })
    $config = [pscustomobject]@{ upgradeReleases = $releases }
    foreach ($kind in 'server', 'desktop') {
        $selected = @(Get-UpgradeReleases -Config $config -Version '1.5.1' -SetupKind $kind)
        Assert-True (($selected.version -join ',') -eq '1.5.0,1.4.11,1.4.10,1.4.9,1.4.8') 'Sources must be sorted numerically, across version boundaries.'
        Assert-Rejected { Get-UpgradeReleases -Config ([pscustomobject]@{ previousRelease = $releases[0] }) -Version '1.5.1' -SetupKind $kind } 'exactly'
        foreach ($count in 4, 6) {
            $bad = [pscustomobject]@{ upgradeReleases = @($releases + $releases[0])[0..($count - 1)] }
            Assert-Rejected { Get-UpgradeReleases -Config $bad -Version '1.5.1' -SetupKind $kind } 'exactly'
        }
        foreach ($mutation in @(
            @{ property = 'version'; value = '1.4.8'; error = 'Duplicate' },
            @{ property = 'version'; value = '1.5.1'; error = 'older' },
            @{ property = 'version'; value = '1.6.0'; error = 'older' },
            @{ property = 'version'; value = '1.4.7-dev'; error = 'stable version' },
            @{ property = "${kind}Setup"; value = (Join-Path $fixtureRoot 'missing.exe'); error = 'Missing' },
            @{ property = "${kind}Setup"; value = $releases[0]."${kind}Setup"; error = 'reused' }
        )) {
            $bad = $config | ConvertTo-Json -Depth 6 | ConvertFrom-Json
            $bad.upgradeReleases[1].($mutation.property) = $mutation.value
            Assert-Rejected { Get-UpgradeReleases -Config $bad -Version '1.5.1' -SetupKind $kind } $mutation.error
        }
    }
    $bad = $config | ConvertTo-Json -Depth 6 | ConvertFrom-Json
    $bad.upgradeReleases[0].canInstallPostgres = 'false'
    Assert-Rejected { Get-UpgradeReleases -Config $bad -Version '1.5.1' -SetupKind server } 'boolean'
    $bad.upgradeReleases[0].PSObject.Properties.Remove('canInstallPostgres')
    Assert-Rejected { Get-UpgradeReleases -Config $bad -Version '1.5.1' -SetupKind server } 'boolean'

    $upgradeReleases = @(Get-UpgradeReleases -Config $config -Version '1.5.1' -SetupKind server)
    $Only = @()
    . $serverPlan
    Assert-True ($scenarios.Count -eq 30) 'Full server matrix must contain 30 scenarios.'
    Assert-True (@($scenarios | Where-Object { -not $_.upgrade }).Count -eq 10) 'Fresh/non-upgrade scenarios must run only once.'
    Assert-True (@($scenarios.id | Select-Object -Unique).Count -eq 30) 'Server evidence IDs must be unique.'
    foreach ($release in $upgradeReleases) {
        $paths = @($scenarios | Where-Object { $_.upgrade -and $_.previousRelease.version -eq $release.version })
        Assert-True ($paths.Count -eq 4) "Missing source coverage for $($release.version)."
        $combinations = @($paths | ForEach-Object { "$($_.identity)/$($_.database)" } | Sort-Object)
        Assert-True (($combinations -join ',') -eq 'gmsa/postgres,gmsa/sqlserver,localSystem/postgres,localSystem/sqlserver') 'Every source needs all four server combinations.'
        Assert-True (@($paths | Where-Object { $_.previousRelease.serverSetup -ne $release.serverSetup }).Count -eq 0) 'Source installers must stay attached to their version.'
    }
    $Only = @('U1,F1')
    . $serverPlan
    Assert-True ($scenarios.Count -eq 6) 'A diagnostic U1 selection must still cover all five sources.'
    $Only = @('unknown')
    Assert-Rejected { . $serverPlan } 'No server scenarios'

    # Execute the real desktop upgrade loop with remote work replaced.
    $desktopRun = & {
        $script:restores = 0
        $script:copiedVersions = @()
        function Restore-CleanVm { $script:restores++ }
        function Invoke-Phase([string]$Phase, [string[]]$Files, [string]$CaseId) {
            Assert-True ($Phase -eq 'upgrade') 'Expected an upgrade phase.'
            $script:copiedVersions += Get-Content -LiteralPath $Files[-1] -Raw
            [pscustomobject]@{ id = $CaseId; verdict = 'PASS'; summary = 'offline fixture' }
        }
        $ResultsDir = $fixtureRoot
        $common = @()
        $outputs = @()
        . ([scriptblock]::Create($desktopLoop.Extent.Text))
        [pscustomobject]@{ restores = $script:restores; versions = $script:copiedVersions; results = $outputs }
    }
    Assert-True ($desktopRun.restores -eq 5 -and $desktopRun.results.Count -eq 5) 'Desktop must restore and upgrade five times.'
    Assert-True (($desktopRun.versions -join ',') -eq ($upgradeReleases.version -join ',')) 'Desktop must copy the matching source installer for each path.'
    Assert-True (@($desktopRun.results.id | Select-Object -Unique).Count -eq 5) 'Desktop evidence IDs must be unique.'
    Assert-True (($desktopRun.results.sourceVersion -join ',') -eq ($upgradeReleases.version -join ',')) 'Desktop results must identify each source.'

    & {
        $vm = 'offline-fixture'; $cred = $null; $Version = '1.5.1'
        $ResultsDir = $fixtureRoot; $log = Join-Path $fixtureRoot 'desktop.log'
        function New-PSSession { param($VMName, $Credential) 'offline-session' }
        function Remove-PSSession { param($Session) }
        function Invoke-Command {
            param($Session, $ArgumentList, $ScriptBlock)
            if ($ScriptBlock.ToString().Contains('Invoke-GuestDesktopScenarios.ps1')) { $guestOutput }
        }
        . ([scriptblock]::Create($desktopPhase.Extent.Text))
        foreach ($phase in 'first', 'second', 'upgrade') {
            $guestOutput = "[12:00:00] === SUMMARY desktop ${phase}: 12 passed, 0 failed"
            Assert-True ((Invoke-Phase $phase).verdict -eq 'PASS') "Passing $phase was rejected."
        }
        foreach ($guestOutput in @(
            '',
            'guest stopped before producing a summary',
            'SUMMARY desktop upgrade: 0 passed, 0 failed',
            'SUMMARY desktop first: 12 passed, 0 failed',
            'SUMMARY desktop upgrade: 11 passed, 1 failed',
            "SUMMARY desktop upgrade: 12 passed, 0 failed`nSUMMARY desktop upgrade: 12 passed, 0 failed",
            "FAIL lost a check`nSUMMARY desktop upgrade: 12 passed, 0 failed"
        )) {
            Assert-True ((Invoke-Phase 'upgrade').verdict -eq 'FAIL') 'Incomplete or failing desktop output must fail its path.'
        }
    }

    $outputs = @($desktopRun.results) + @([pscustomobject]@{ verdict = 'PASS' }, [pscustomobject]@{ verdict = 'PASS' })
    Assert-True (-not (& $desktopGate)) 'Seven passing desktop phases should pass.'
    $outputs = $outputs[0..5]
    Assert-True ([bool](& $desktopGate)) 'A missing desktop phase must block the release.'
    $outputs += [pscustomobject]@{ verdict = 'FAIL' }
    Assert-True ([bool](& $desktopGate)) 'A failing desktop upgrade must block the release.'

    $Only = @(); . $serverPlan
    $results = @($scenarios | ForEach-Object { [pscustomobject]@{ id = $_.id; verdict = 'PASS' } })
    Assert-True (-not (& $serverGate)) 'Thirty passing server cases should pass.'
    ($results | Where-Object id -eq 'S1').verdict = 'N/A'
    Assert-True (-not (& $serverGate)) 'Optional remote SQL may remain N/A.'
    $upgradeResult = $results | Where-Object id -like 'U1-*' | Select-Object -First 1
    foreach ($verdict in 'N/A', 'HARNESS-ERROR', 'FAIL') {
        $upgradeResult.verdict = $verdict
        Assert-True ([bool](& $serverGate)) "Required server upgrade with $verdict must block the release."
    }
    $upgradeResult.verdict = 'PASS'
    $results = $results[0..28]
    Assert-True ([bool](& $serverGate)) 'A missing server result must block the release.'

    Write-Host 'Upgrade matrix checks passed: five sources, 20 server and five desktop upgrades, distinct evidence, incomplete runs rejected.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixtureRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path $resolved -Leaf) -notlike 'nodepilot-upgrade-matrix-*') { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
