#requires -Version 5.1
<#
.SYNOPSIS
    Rejects environment-local settings from a final publish/package stage.
.DESCRIPTION
    dotnet publish output is only the first input to a NodePilot release artifact: the server
    build adds a source snapshot and the desktop build adds payload trees afterwards. Both call
    this guard against the complete stage directory immediately before packaging.
#>

function Assert-NodePilotPublishSettingsHygiene {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RootPath,
        [string]$RequiredBaseSettingsPath = 'appsettings.json'
    )

    if (-not (Test-Path -LiteralPath $RootPath -PathType Container)) {
        throw "Publish settings hygiene root does not exist: $RootPath"
    }

    $resolvedRoot = (Resolve-Path -LiteralPath $RootPath -ErrorAction Stop).Path
    $requiredBase = [IO.Path]::GetFullPath((Join-Path $resolvedRoot $RequiredBaseSettingsPath))
    $rootPrefix = $resolvedRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if (-not $requiredBase.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Required base-settings path escapes the publish root: $RequiredBaseSettingsPath"
    }
    if (-not (Test-Path -LiteralPath $requiredBase -PathType Leaf)) {
        throw "Publish output is missing the required appsettings.json base configuration at '$RequiredBaseSettingsPath'."
    }

    $forbiddenNames = @('appsettings.Development.json', 'appsettings.runtime.json')
    $offenders = @(Get-ChildItem -LiteralPath $resolvedRoot -Recurse -Force -File | Where-Object {
        $candidateName = $_.Name
        @($forbiddenNames | Where-Object {
            [string]::Equals($_, $candidateName, [StringComparison]::OrdinalIgnoreCase)
        }).Count -gt 0
    } | Sort-Object -Property FullName)

    if ($offenders.Count -gt 0) {
        $relativePaths = @($offenders | ForEach-Object {
            $_.FullName.Substring($rootPrefix.Length)
        })
        throw ("Publish output contains forbidden environment-local settings: " + ($relativePaths -join ', '))
    }
}
