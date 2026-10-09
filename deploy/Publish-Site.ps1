<#
.SYNOPSIS
    Builds the public site and uploads it to a web host over SFTP or FTPS.

.DESCRIPTION
    Assembles the full _site/ tree for the webspace -- project website at the
    root, documentation under /docs/, browser demo under /demo/, media under /media/ -- and
    uploads it to the configured host.

    Settings and credentials come from a JSON file that is never committed. The credential is
    handed to curl through an environment variable, so it appears neither in the command line
    (which any local user can read) nor on disk.

    The demo uses path routing under /demo/ with the bundled Apache fallback. A few
    absolute URLs need the target origin -- the canonical and Open Graph tags and the docs' link
    to the demo -- and those are rewritten at build time from the config's "origin".

.PARAMETER ConfigPath
    JSON settings file. Defaults to deploy\site-publish.local.json next to this script.
    See deploy\site-publish.example.json for the shape.

.PARAMETER SkipBuild
    Upload the existing _site/ without rebuilding it.

.PARAMETER DryRun
    Build, check which training videos the server already holds, and list what would be
    uploaded, but transfer nothing.

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
            # Windows PowerShell 5.1 turns native stderr into error records. Vite writes
            # non-fatal build warnings there, so judge npm by its exit code instead.
            $previousErrorAction = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Continue'
                & npm.cmd run $script 2>&1 | ForEach-Object { Write-Host $_ }
                $npmExitCode = $LASTEXITCODE
            } finally {
                $ErrorActionPreference = $previousErrorAction
            }
            if ($npmExitCode -ne 0) { throw "npm run $script failed with exit code $npmExitCode." }
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

# Training videos are published in their own step, before everything else: see below.
$videoPrefix = 'media/training/'
$catalog = Get-Content -LiteralPath (Join-Path $sitePackage 'src\site\videos.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$videos = @(foreach ($episode in $catalog.episodes) {
    foreach ($lang in 'de', 'en') {
        [pscustomobject]@{ Path = $episode.text.$lang.video; Bytes = [long]$episode.text.$lang.bytes }
    }
})

# Relative POSIX paths: the remote side is not Windows.
#
# HTML goes last. Asset file names carry a content hash, so a changed stylesheet is a new file:
# uploading the HTML first points the live site at an asset that is not there yet, and every
# visitor during the transfer gets the page without its styles.
$relative = @($files | ForEach-Object { $_.FullName.Substring($siteDir.Length).TrimStart('\').Replace('\', '/') } |
    Where-Object { -not $_.StartsWith($videoPrefix) } |
    Sort-Object { if ($_ -match '\.html?$') { 1 } else { 0 } }, { $_ })

Write-Host "$($relative.Count) file(s) and $($videos.Count) training video(s) -> $baseUrl/"

# --- upload ----------------------------------------------------------------

# The credential reaches curl through the environment, expanded by curl itself. Passing it as
# --user would publish it to the process list; writing it into a config file would put it on disk.
Set-Item -Path "Env:\$credentialVariable" -Value $(if ($keyFile) { "$(Setting 'user'):" } else { "$(Setting 'user'):$password" })

try {
    $common = @('--silent', '--show-error', '--variable', "%$credentialVariable", '--expand-user', "{{$credentialVariable}}")
    if ($protocol -eq 'sftp') { $common += @('--hostpubsha256', $fingerprint) }
    if ($keyFile) { $common += @('--key', $keyFile) }

    function Send-File([string] $item) {
        $local = Join-Path $siteDir ($item.Replace('/', '\'))
        # Not $args: that is an automatic variable. curl's stderr is left alone -- redirecting a
        # native command's stderr in PowerShell 5.1 wraps each line in an ErrorRecord and flips $?.
        $curlArgs = $common + @('--fail', '--upload-file', $local, "$baseUrl/$item")
        if ($protocol -eq 'ftps') { $curlArgs += @('--ssl-reqd', '--ftp-create-dirs') }

        # Each file is its own SSH session, and a handshake occasionally fails on a busy host.
        # Two extra attempts turn that from a failed deploy into a pause.
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            # PowerShell 5.1 promotes native stderr to an ErrorRecord. Keep the curl
            # exit code authoritative so a failed handshake reaches this retry loop.
            $previousErrorAction = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Continue'
                & $curl @curlArgs 2>&1 | ForEach-Object { Write-Host $_ }
                $curlExitCode = $LASTEXITCODE
            } finally {
                $ErrorActionPreference = $previousErrorAction
            }
            if ($curlExitCode -eq 0) { return $true }
            if ($attempt -lt 3) { Start-Sleep -Seconds ($attempt * 2) }
        }
        return $false
    }

    # Name -> size of the files in the remote video folder, or $null when it cannot be listed.
    # SFTP and FTP both list in `ls -l` form: the size is the fifth field, the name the last.
    function Get-RemoteVideoSizes {
        $curlArgs = $common + @("$baseUrl/$videoPrefix")
        if ($protocol -eq 'ftps') { $curlArgs += '--ssl-reqd' }
        $lines = @()
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            $previousErrorAction = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Continue'
                $lines = @(& $curl @curlArgs 2>&1)
                $curlExitCode = $LASTEXITCODE
            } finally {
                $ErrorActionPreference = $previousErrorAction
            }
            if ($curlExitCode -eq 0) { break }
            $lines | ForEach-Object { Write-Host $_ }
            if ($attempt -lt 3) { Start-Sleep -Seconds ($attempt * 2) }
        }
        if ($curlExitCode -ne 0) { return $null }
        $sizes = @{}
        foreach ($line in $lines) {
            $fields = -split $line
            if ($fields.Count -ge 9 -and $fields[0] -notlike 'd*') { $sizes[$fields[-1]] = [long]$fields[4] }
        }
        return $sizes
    }

    # SFTP will not create missing directories on upload, so they are made first, shortest path
    # first. The '*' prefix is what makes a failing command non-fatal -- '-' means something else
    # entirely (run it after the transfer), and with it one "directory exists" aborts the whole
    # command list, leaving every deeper directory uncreated.
    if ($protocol -eq 'sftp' -and -not $DryRun) {
        $prefixes = [System.Collections.Generic.HashSet[string]]::new()
        foreach ($item in @($relative) + @($videos.Path)) {
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
        if ($quote.Count -gt 0) {
            for ($attempt = 1; $attempt -le 3; $attempt++) {
                $previousErrorAction = $ErrorActionPreference
                try {
                    $ErrorActionPreference = 'Continue'
                    & $curl @common @quote "$baseUrl/" -o NUL 2>&1 | ForEach-Object { Write-Host $_ }
                    $curlExitCode = $LASTEXITCODE
                } finally {
                    $ErrorActionPreference = $previousErrorAction
                }
                if ($curlExitCode -eq 0) { break }
                if ($attempt -lt 3) { Start-Sleep -Seconds ($attempt * 2) }
            }
            if ($curlExitCode -ne 0) { throw "Could not prepare $baseUrl/ after three attempts." }
        }
    }

    # Videos first, and only those the server lacks: they are large, and their names carry a
    # content hash, so a file of the catalog's size under that name is the right one. A video
    # that does not reach the server stops the deploy before any page linking to it goes live.
    $remoteSizes = Get-RemoteVideoSizes
    if ($null -eq $remoteSizes) {
        if (-not $DryRun) { throw "Could not list $baseUrl/$videoPrefix." }
        Write-Host "  $videoPrefix cannot be listed; treating every video as missing on the server."
        $remoteSizes = @{}
    }
    $pending = @()
    $unavailable = @()
    foreach ($video in $videos) {
        $local = Join-Path $siteDir ($video.Path.Replace('/', '\'))
        if ($remoteSizes[$video.Path.Substring($videoPrefix.Length)] -eq $video.Bytes) {
            if ($DryRun) { Write-Host "  on server     $($video.Path)" }
        } elseif ((Test-Path -LiteralPath $local) -and (Get-Item -LiteralPath $local).Length -eq $video.Bytes) {
            $pending += $video
            if ($DryRun) { Write-Host "  would upload  $($video.Path)" }
        } else {
            $unavailable += $video.Path
            Write-Host "  missing       $($video.Path)" -ForegroundColor Red
        }
    }
    if ($unavailable.Count -gt 0) {
        throw "$($unavailable.Count) training video(s) are neither on the server nor in $siteDir. Run 'npm run site:videos' and rebuild."
    }

    if ($DryRun) {
        $relative | Sort-Object | ForEach-Object { Write-Host "  would upload $_" }
        Write-Host 'Dry run: nothing was transferred.'
        return
    }

    $failedVideos = @($pending | Where-Object { -not (Send-File $_.Path) } | ForEach-Object { $_.Path })
    if ($pending.Count -gt 0) {
        $remoteSizes = Get-RemoteVideoSizes
        if ($null -eq $remoteSizes) { throw "Could not list $baseUrl/$videoPrefix to verify the uploaded videos." }
        $failedVideos += @($pending | Where-Object { $remoteSizes[$_.Path.Substring($videoPrefix.Length)] -ne $_.Bytes } | ForEach-Object { $_.Path })
    }
    if ($failedVideos.Count -gt 0) {
        $failedVideos | Select-Object -Unique | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        throw 'Training video upload failed; nothing else was published.'
    }
    Write-Host "Training videos: $($pending.Count) uploaded, $($videos.Count - $pending.Count) already on the server."

    $uploaded = 0
    foreach ($item in $relative) {
        if (-not (Send-File $item)) {
            throw "Upload failed: $item. Remaining files were not published."
        }
        $uploaded++
        if ($uploaded % 25 -eq 0) { Write-Host "  $uploaded/$($relative.Count)" }
    }
} finally {
    Remove-Item -Path "Env:\$credentialVariable" -ErrorAction SilentlyContinue
}

Write-Host "Uploaded $uploaded of $($relative.Count) file(s)."


Write-Host "Done. The site should be live at $(Setting 'origin')/"
Write-Host 'Files removed from the build are not deleted on the server; clear the remote folder for a clean slate.'
