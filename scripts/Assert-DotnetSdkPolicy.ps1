#requires -Version 5.1

function Assert-NodePilotDotnetSdkVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $RequiredVersion,
        [Parameter(Mandatory)] [string] $RollForward,
        [Parameter(Mandatory)] [string] $ActualVersion
    )

    if ($RollForward -cne 'latestPatch') {
        throw "global.json rollForward must be 'latestPatch', not '$RollForward'."
    }
    foreach ($candidate in @($RequiredVersion, $ActualVersion)) {
        if ($candidate -notmatch '^\d+\.\d+\.\d+$') {
            throw "SDK version '$candidate' is not a stable three-part version."
        }
    }

    $required = [version]$RequiredVersion
    $actual = [version]$ActualVersion
    $requiredFeatureBand = $required.Build - ($required.Build % 100)
    $actualFeatureBand = $actual.Build - ($actual.Build % 100)

    if ($actual.Major -ne $required.Major -or
        $actual.Minor -ne $required.Minor -or
        $actualFeatureBand -ne $requiredFeatureBand) {
        throw "Selected SDK $actual is outside required feature band $($required.Major).$($required.Minor).${requiredFeatureBand}xx."
    }
    if ($actual -lt $required) {
        throw "Selected SDK $actual is below security floor $required."
    }
}

function Assert-NodePilotDotnetSdkPolicy {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $RepoRoot,
        [scriptblock] $VersionProbe = { & dotnet --version }
    )

    $globalJsonPath = Join-Path $RepoRoot 'global.json'
    if (-not (Test-Path -LiteralPath $globalJsonPath -PathType Leaf)) {
        throw "SDK policy is missing: $globalJsonPath"
    }
    try { $policy = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json }
    catch { throw "SDK policy is invalid JSON: $($_.Exception.Message)" }

    if ($policy.sdk.allowPrerelease -ne $false) {
        throw "global.json must set sdk.allowPrerelease to false."
    }

    $actualOutput = @(& $VersionProbe)
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet --version failed with exit code $LASTEXITCODE."
    }
    $actualVersion = ($actualOutput | Select-Object -Last 1).ToString().Trim()
    Assert-NodePilotDotnetSdkVersion `
        -RequiredVersion ([string]$policy.sdk.version) `
        -RollForward ([string]$policy.sdk.rollForward) `
        -ActualVersion $actualVersion

    Write-Host "[sdk] Selected $actualVersion; floor $($policy.sdk.version), rollForward latestPatch." -ForegroundColor DarkGray
}
