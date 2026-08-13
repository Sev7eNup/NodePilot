#requires -Version 5.1

function Get-NodePilotDesktopPayloadFileVersion {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required desktop runtime payload is missing: $Path"
    }

    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    $reported = if (-not [string]::IsNullOrWhiteSpace($info.ProductVersion)) {
        $info.ProductVersion
    } else {
        $info.FileVersion
    }
    $match = [regex]::Match("$reported", '(?<!\d)(?<version>\d+\.\d+\.\d+)(?:\.\d+)?')
    if (-not $match.Success) {
        throw "Desktop runtime payload '$Path' reports an invalid version."
    }

    return [version]$match.Groups['version'].Value
}

function Assert-DesktopRuntimePayload {
    <#
    .SYNOPSIS
        Fails the desktop build unless its self-contained .NET payload meets the security floor.

    .DESCRIPTION
        Checks both the publish manifest and version resources from representative host, runtime,
        WebSocket and ASP.NET Core binaries. This prevents a requested RuntimeFrameworkVersion
        from becoming a paper-only control when a stale or mixed stage directory is packaged.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $AppPath,
        [Parameter(Mandatory)] [version] $MinimumVersion
    )

    $runtimeConfigPath = Join-Path $AppPath 'NodePilot.Api.runtimeconfig.json'
    if (-not (Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf)) {
        throw "Desktop runtime configuration is missing: $runtimeConfigPath"
    }

    try {
        $runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
        $includedFrameworks = @($runtimeConfig.runtimeOptions.includedFrameworks)
    } catch {
        throw "Desktop runtime configuration is invalid: $($_.Exception.Message)"
    }

    foreach ($frameworkName in @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')) {
        $framework = @($includedFrameworks | Where-Object { $_.name -ceq $frameworkName })
        if ($framework.Count -ne 1) {
            throw "Desktop self-contained payload must declare exactly one $frameworkName framework."
        }

        try { $actualVersion = [version]([string]$framework[0].version) }
        catch { throw "Desktop framework '$frameworkName' reports an invalid version." }
        if ($actualVersion -lt $MinimumVersion) {
            throw "Desktop framework '$frameworkName' is $actualVersion; security floor is $MinimumVersion."
        }
    }

    foreach ($relativePath in @(
        'hostfxr.dll',
        'System.Private.CoreLib.dll',
        'System.Net.WebSockets.dll',
        'System.Net.WebSockets.Client.dll',
        'Microsoft.AspNetCore.Server.Kestrel.Core.dll'
    )) {
        $payloadPath = Join-Path $AppPath $relativePath
        $actualVersion = Get-NodePilotDesktopPayloadFileVersion -Path $payloadPath
        if ($actualVersion -lt $MinimumVersion) {
            throw "Desktop runtime payload '$relativePath' is $actualVersion; security floor is $MinimumVersion."
        }
    }
}
