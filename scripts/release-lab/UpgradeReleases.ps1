# Shared preflight for the five direct upgrade paths. No VM operations.
function Get-UpgradeReleases {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][ValidateSet('server', 'desktop')][string]$SetupKind
    )
    Set-StrictMode -Version 3.0
    $ErrorActionPreference = 'Stop'
    if (-not $Config.PSObject.Properties['upgradeReleases'] -or @($Config.upgradeReleases).Count -ne 5) {
        throw 'Configure upgradeReleases with exactly the five latest published stable releases before the candidate.'
    }
    if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Invalid candidate version: $Version" }
    $candidate = [version]($Version -split '-')[0]
    $versions = @{}
    $installers = @{}
    $setupProperty = "${SetupKind}Setup"
    foreach ($release in $Config.upgradeReleases) {
        if ($null -eq $release -or -not $release.PSObject.Properties['version'] -or
            $release.version -notmatch '^\d+\.\d+\.\d+$') {
            throw 'Each upgradeReleases entry needs a stable version (major.minor.patch).'
        }
        $source = [version]$release.version
        if ($source -ge $candidate) { throw "Upgrade source $source must be older than $Version." }
        if ($versions.ContainsKey($source.ToString())) { throw "Duplicate upgrade source: $source" }
        $versions[$source.ToString()] = $true
        if (-not $release.PSObject.Properties[$setupProperty] -or
            [string]::IsNullOrWhiteSpace($release.$setupProperty) -or
            -not (Test-Path -LiteralPath $release.$setupProperty -PathType Leaf)) {
            throw "Missing $setupProperty installer for $source. All five upgrade paths are required."
        }
        $path = (Resolve-Path -LiteralPath $release.$setupProperty).ProviderPath
        if ($installers.ContainsKey($path)) { throw "Installer reused for multiple upgrade sources: $path" }
        $installers[$path] = $true
        if ($SetupKind -eq 'server') {
            foreach ($flag in 'preinstallRuntimes', 'preGrantSystemForLocalSql', 'canInstallPostgres') {
                if (-not $release.PSObject.Properties[$flag] -or $release.$flag -isnot [bool]) {
                    throw "Upgrade source $source needs a boolean $flag."
                }
            }
        }
    }
    $Config.upgradeReleases | Sort-Object { [version]$_.version } -Descending
}
