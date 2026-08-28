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
        Checks the publish manifest and the version resources of representative host, runtime,
        WebSocket and ASP.NET Core binaries, so a requested RuntimeFrameworkVersion still holds
        when a stale or mixed stage directory is packaged.
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

function Get-NodePilotDesktopPostgresMajorVersion {
    <#
    .SYNOPSIS
        Reads the PostgreSQL major version out of the bundled server binary itself.

    .DESCRIPTION
        The version comes from the binary, not from the directory path: EDB's portable zip unpacks
        to a folder named 'pgsql' with no version in the path, and installer paths carry only a
        label a caller can rename. postgres.exe stamps PG_VERSION into its version resource, and
        the numeric fields of that resource carry the major on their own.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $PgRootPath)

    $postgresExe = Join-Path $PgRootPath 'bin\postgres.exe'
    if (-not (Test-Path -LiteralPath $postgresExe -PathType Leaf)) {
        throw "Bundled PostgreSQL payload is missing its server binary: $postgresExe"
    }

    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($postgresExe)
    foreach ($reported in @($info.ProductVersion, $info.FileVersion)) {
        if ([string]::IsNullOrWhiteSpace($reported)) { continue }
        $match = [regex]::Match("$reported", '(?<!\d)(?<major>\d+)\.\d+')
        if ($match.Success) { return [int]$match.Groups['major'].Value }
    }
    foreach ($numericMajor in @($info.ProductMajorPart, $info.FileMajorPart)) {
        if ($numericMajor -gt 0) { return [int]$numericMajor }
    }

    throw "Bundled PostgreSQL binary '$postgresExe' does not report a usable version."
}

function Assert-DesktopPostgresPayload {
    <#
    .SYNOPSIS
        Fails the desktop build unless the bundled PostgreSQL is the expected major version.

    .DESCRIPTION
        PostgreSQL refuses to start on a data directory written by a different major version, and
        the desktop package upgrades in place over the existing pgdata. A runtime from another major
        would still build and ship, then fail on every installation it lands on. Minor versions may
        differ freely; the major is a contract with the cluster on disk and is checked here.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $PgRootPath,
        [Parameter(Mandatory)] [int] $ExpectedMajorVersion
    )

    $actualMajor = Get-NodePilotDesktopPostgresMajorVersion -PgRootPath $PgRootPath
    if ($actualMajor -ne $ExpectedMajorVersion) {
        throw ("Bundled PostgreSQL is major version $actualMajor, but the desktop package requires " +
               "major version $ExpectedMajorVersion. PostgreSQL cannot start on a data directory " +
               "from another major version, so this installer would fail against every existing " +
               "NodePilot database. Point -PgBinariesPath at a PostgreSQL $ExpectedMajorVersion " +
               "distribution (found: $PgRootPath).")
    }
}
