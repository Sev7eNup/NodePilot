#requires -Version 5.1

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$helper = Join-Path $PSScriptRoot 'Assert-DotnetSdkPolicy.ps1'
if (-not (Test-Path -LiteralPath $helper -PathType Leaf)) {
    throw "SDK policy helper is missing: $helper"
}
. $helper

Assert-NodePilotDotnetSdkVersion `
    -RequiredVersion '10.0.111' `
    -RollForward 'latestPatch' `
    -ActualVersion '10.0.111'

Write-Host 'PASS: patched SDK floor is accepted.' -ForegroundColor Green

$rejected = $false
try {
    Assert-NodePilotDotnetSdkVersion `
        -RequiredVersion '10.0.111' `
        -RollForward 'latestPatch' `
        -ActualVersion '10.0.110'
} catch {
    $rejected = $_.Exception.Message -match 'below security floor'
}
if (-not $rejected) { throw 'An SDK below 10.0.111 was not rejected.' }

Write-Host 'PASS: vulnerable SDK patch is rejected.' -ForegroundColor Green

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$globalPolicy = Get-Content -LiteralPath (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json
if ([string]$globalPolicy.sdk.version -cne '10.0.111' -or
    [string]$globalPolicy.sdk.rollForward -cne 'latestPatch' -or
    $globalPolicy.sdk.allowPrerelease -ne $false) {
    throw 'global.json does not enforce SDK 10.0.111 with stable latestPatch roll-forward.'
}

foreach ($relativePath in @('.github\workflows\ci.yml', '.github\workflows\codeql.yml')) {
    $workflow = Get-Content -LiteralPath (Join-Path $repoRoot $relativePath) -Raw
    if ($workflow -notmatch 'global-json-file:\s*global\.json' -or
        $workflow -notmatch 'Assert-NodePilotDotnetSdkPolicy') {
        throw "$relativePath does not install and verify the repository SDK policy."
    }
}

foreach ($relativePath in @('deploy\Build-Artifact.ps1', 'deploy\desktop\Build-DesktopInstaller.ps1')) {
    $releaseBuild = Get-Content -LiteralPath (Join-Path $repoRoot $relativePath) -Raw
    if ($releaseBuild -notmatch 'Assert-DotnetSdkPolicy\.ps1' -or
        $releaseBuild -notmatch 'Assert-NodePilotDotnetSdkPolicy') {
        throw "$relativePath does not fail closed on the repository SDK policy."
    }
}

Write-Host 'PASS: global, CI and release entry points enforce the SDK policy.' -ForegroundColor Green
