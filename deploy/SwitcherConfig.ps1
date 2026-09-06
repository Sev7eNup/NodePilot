#requires -Version 5.1
<#
.SYNOPSIS
    Reads and writes the Switcher's server URL, shared by install and update.
.DESCRIPTION
    The Switcher drives NodePilot through np.exe. Without a server URL it falls back to the
    CLI's own configuration, which is per-user and DPAPI-protected - the setup account is not the
    account that later runs the switcher, so only the shipped configuration can carry the value.

    Install seeds it from the hostname and port it just configured. Update wipes and repopulates the
    install directory, so it has to carry the previous value across or the switch to NodePilot breaks
    again on every upgrade. Both go through here: two copies of the same string surgery is how one of
    them ends up subtly different.
#>

Set-StrictMode -Version 3.0

<#
.SYNOPSIS
    Returns the configured serverUrl, or $null when the file is absent, unreadable or unset.
#>
function Get-NodePilotSwitcherServerUrl {
    [OutputType([string])]
    param([Parameter(Mandatory)][string]$ConfigPath)

    if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { return $null }
    try {
        $value = ((Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8) | ConvertFrom-Json).nodePilot.serverUrl
    } catch {
        return $null
    }
    if ([string]::IsNullOrWhiteSpace($value)) { return $null }
    return [string]$value
}

<#
.SYNOPSIS
    Writes $ServerUrl into the configuration and returns $true when the file changed.
.DESCRIPTION
    Rewritten in place rather than round-tripped through ConvertTo-Json: the file is documented as
    hand-editable, and re-serialising it would escape '&' and quotes in activeJobsPath and reflow
    every line. The result is parsed before it is written, so a pattern that ever stops matching
    cannot leave a configuration the switcher refuses to load.
#>
function Set-NodePilotSwitcherServerUrl {
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)][string]$ConfigPath,
        [Parameter(Mandatory)][string]$ServerUrl)

    if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { return $false }
    $raw = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8
    $pattern = '"serverUrl"\s*:\s*(?:null|"[^"]*")'
    if ([regex]::Matches($raw, $pattern).Count -ne 1) { return $false }

    $updated = [regex]::Replace($raw, $pattern, '"serverUrl": "' + $ServerUrl + '"')
    $null = $updated | ConvertFrom-Json
    [System.IO.File]::WriteAllText($ConfigPath, $updated, (New-Object System.Text.UTF8Encoding $false))
    return $true
}

<#
.SYNOPSIS
    Builds the switcher's server URL from a hostname and the Kestrel HTTPS port.
#>
function Get-NodePilotSwitcherServerUrlFor {
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string]$Hostname,
        [Parameter(Mandatory)][int]$HttpsPort)

    if ($HttpsPort -eq 443) { return "https://$Hostname" }
    return "https://${Hostname}:$HttpsPort"
}
