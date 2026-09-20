<#
.SYNOPSIS
    Builds the public site and uploads it to a web host over SFTP or FTPS.

.DESCRIPTION
    Assembles the same _site/ tree the GitHub Pages workflow publishes -- project website at the
    root, documentation under /docs/, browser demo under /demo/, media under /media/ -- and
    uploads it to the configured host.

    Settings and credentials come from a JSON file that is never committed. The credential is
    handed to curl through an environment variable, so it appears neither in the command line
    (which any local user can read) nor on disk.

    The site itself is built location-independent: relative asset URLs, hash routes. Only a few
    absolute URLs need the target origin -- the canonical and Open Graph tags and the docs' link
    to the demo -- and those are rewritten at build time from the config's "origin".

.PARAMETER ConfigPath
    JSON settings file. Defaults to deploy\site-publish.local.json next to this script.
    See deploy\site-publish.example.json for the shape.

.PARAMETER SkipBuild
    Upload the existing _site/ without rebuilding it.

.PARAMETER DryRun
    Build and list what would be uploaded, but transfer nothing.

.EXAMPLE
    powershell -File deploy\Publish-Site.ps1 -DryRun
    powershell -File deploy\Publish-Site.ps1
#>
[CmdletBinding()]
param(
    [string] $ConfigPath,
    [switch] $SkipBuild,
    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$sitePackage = Join-Path $repoRoot 'src\nodepilot-docs-ui'
$siteDir = Join-Path $sitePackage '_site'
$credentialVariable = 'NP_PUBLISH_CRED'

if (-not $ConfigPath) { $ConfigPath = Join-Path $PSScriptRoot 'site-publish.local.json' }
if (-not (Test-Path -LiteralPath $ConfigPath)) {
    throw "No settings file at $ConfigPath. Copy site-publish.example.json next to it, fill it in, and keep it out of git."
}

$config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$present = $config.PSObject.Properties.Name
function Setting([string] $name) { if ($present -contains $name) { "$($config.$name)".Trim() } else { '' } }

foreach ($field in 'origin', 'protocol', 'host', 'user', 'remotePath') {
    if (-not (Setting $field)) { throw "Settings file is missing '$field'." }
}

$protocol = (Setting 'protocol').ToLowerInvariant()
if ($protocol -ne 'ftps' -and $protocol -ne 'sftp') {
    throw "protocol must be 'sftp' or 'ftps', not '$(Setting 'protocol')'. Plain ftp is refused: it sends the password in the clear."
}

$password = Setting 'password'
$keyFile = Setting 'keyFile'
if (-not $password -and -not $keyFile) { throw "Settings file needs either 'password' or 'keyFile'." }

# Pinning the host key is what makes the first connection trustworthy. Read it once with
#   ssh-keyscan -t rsa <host> | ssh-keygen -lf -
# and compare it against the fingerprint the hosting provider publishes.
$fingerprint = Setting 'hostFingerprint'
if ($protocol -eq 'sftp' -and -not $fingerprint) {
    throw "SFTP needs 'hostFingerprint' (the base64 part of an ssh-keygen SHA256 fingerprint), or the server cannot be told apart from an impostor."
}

# --- curl ------------------------------------------------------------------

# Windows ships its own curl in System32, built without libssh2: it cannot speak SFTP at all.
# Git for Windows ships one that can, so the right binary is picked by capability, not by name.
function Find-Curl([string] $needed) {
    $candidates = @()
    $onPath = Get-Command curl.exe -All -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    $candidates += Join-Path $env:ProgramFiles 'Git\mingw64\bin\curl.exe'
    $candidates += Join-Path ${env:ProgramFiles(x86)} 'Git\mingw64\bin\curl.exe'

    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        $line = & $candidate --version 2>$null | Select-String '^Protocols:'
        if ($line -and ($line.Line -split '\s+') -contains $needed) { return $candidate }
    }
    return $null
}

$curl = Find-Curl $protocol
if (-not $curl) {
    throw "Found no curl.exe that supports $protocol. Git for Windows ships one (mingw64\bin\curl.exe); the copy in System32 does not."
}

# --- build -----------------------------------------------------------------

if ($SkipBuild) {
    if (-not (Test-Path -LiteralPath (Join-Path $siteDir 'index.html'))) {
        throw "-SkipBuild was given but $siteDir holds no index.html."
    }
    Write-Host "Using the existing $siteDir"
} else {
    Write-Host "Building the site for $(Setting 'origin')"
    # Read by both Vite configs; it retargets the absolute URLs the sources write for Pages.
    $env:NP_SITE_ORIGIN = Setting 'origin'
    try {
        Push-Location $sitePackage
        foreach ($script in 'build', 'build:site', 'build:demo', 'assemble:site') {
            Write-Host "  npm run $script"
            & npm.cmd run $script
            if ($LASTEXITCODE -ne 0) { throw "npm run $script failed with exit code $LASTEXITCODE." }
        }
    } finally {
        Pop-Location
        Remove-Item Env:\NP_SITE_ORIGIN -ErrorAction SilentlyContinue
    }
}

# --- collect ---------------------------------------------------------------

$files = Get-ChildItem -LiteralPath $siteDir -Recurse -File
if ($files.Count -eq 0) { throw "$siteDir is empty." }

$remoteRoot = (Setting 'remotePath').TrimEnd('/')
$port = if (Setting 'port') { ":$(Setting 'port')" } else { '' }
$baseUrl = "${protocol}://$(Setting 'host')$port$remoteRoot"

# Relative POSIX paths: the remote side is not Windows.
#
# HTML goes last. Asset file names carry a content hash, so a changed stylesheet is a new file:
# uploading the HTML first points the live site at an asset that is not there yet, and every
# visitor during the transfer gets the page without its styles.
$relative = @($files | ForEach-Object { $_.FullName.Substring($siteDir.Length).TrimStart('\').Replace('\', '/') } |
    Sort-Object { if ($_ -match '\.html?$') { 1 } else { 0 } }, { $_ })

Write-Host "$($files.Count) file(s) -> $baseUrl/"

if ($DryRun) {
    $relative | Sort-Object | ForEach-Object { Write-Host "  would upload $_" }
    Write-Host 'Dry run: nothing was transferred.'
    return
}

# --- upload ----------------------------------------------------------------

# The credential reaches curl through the environment, expanded by curl itself. Passing it as
# --user would publish it to the process list; writing it into a config file would put it on disk.
Set-Item -Path "Env:\$credentialVariable" -Value $(if ($keyFile) { "$(Setting 'user'):" } else { "$(Setting 'user'):$password" })

try {
    $common = @('--silent', '--show-error', '--variable', "%$credentialVariable", '--expand-user', "{{$credentialVariable}}")
    if ($protocol -eq 'sftp') { $common += @('--hostpubsha256', $fingerprint) }
    if ($keyFile) { $common += @('--key', $keyFile) }

    # SFTP will not create missing directories on upload, so they are made first, shortest path
    # first. The '*' prefix is what makes a failing command non-fatal -- '-' means something else
    # entirely (run it after the transfer), and with it one "directory exists" aborts the whole
    # command list, leaving every deeper directory uncreated.
    if ($protocol -eq 'sftp') {
        $prefixes = [System.Collections.Generic.HashSet[string]]::new()
        foreach ($item in $relative) {
            $dir = (Split-Path $item -Parent)
            if (-not $dir) { continue }
            $parts = $dir.Replace('\', '/').Split('/')
            for ($i = 0; $i -lt $parts.Count; $i++) { [void]$prefixes.Add($parts[0..$i] -join '/') }
        }
        $quote = @()
        foreach ($prefix in ($prefixes | Sort-Object { $_.Split('/').Count }, { $_ })) {
            $quote += '-Q'
            $quote += "*mkdir $remoteRoot/$prefix"
        }
        if ($quote.Count -gt 0) { & $curl @common @quote "$baseUrl/" -o NUL }
    }

    $uploaded = 0
    $failed = @()
    foreach ($item in $relative) {
        $local = Join-Path $siteDir ($item.Replace('/', '\'))
        # Not $args: that is an automatic variable. curl's stderr is left alone -- redirecting a
        # native command's stderr in PowerShell 5.1 wraps each line in an ErrorRecord and flips $?.
        $curlArgs = $common + @('--fail', '--upload-file', $local, "$baseUrl/$item")
        if ($protocol -eq 'ftps') { $curlArgs += @('--ssl-reqd', '--ftp-create-dirs') }

        # Each file is its own SSH session, and a handshake occasionally fails on a busy host.
        # Two extra attempts turn that from a failed deploy into a pause.
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            & $curl @curlArgs
            if ($LASTEXITCODE -eq 0) { break }
            if ($attempt -lt 3) { Start-Sleep -Seconds ($attempt * 2) }
        }
        if ($LASTEXITCODE -ne 0) { $failed += $item } else { $uploaded++ }

        if (($uploaded + $failed.Count) % 25 -eq 0) { Write-Host "  $($uploaded + $failed.Count)/$($files.Count)" }
    }
} finally {
    Remove-Item -Path "Env:\$credentialVariable" -ErrorAction SilentlyContinue
}

Write-Host "Uploaded $uploaded of $($files.Count) file(s)."

if ($failed.Count -gt 0) {
    Write-Host "$($failed.Count) file(s) failed:" -ForegroundColor Red
    $failed | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    throw 'Upload incomplete.'
}

Write-Host "Done. The site should be live at $(Setting 'origin')/"
Write-Host 'Files removed from the build are not deleted on the server; clear the remote folder for a clean slate.'
