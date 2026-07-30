#requires -Version 5.1
<#
.SYNOPSIS
    Generates the Electron desktop shell's icon set from the SPA's tracked brand assets.

.DESCRIPTION
    Writes into src\nodepilot-desktop\assets (gitignored - the SOURCES are versioned, so a clean
    clone can always rebuild them):

      icon.ico              multi-resolution 16/32/48/256 - exe, installer, Start-Menu, Explorer
      icon.png              256px window icon shown until the SPA reports its skin
      tray.png              32px notification-area icon
      skins\<id>.png        256px window icon per SPA color skin
      skins\<id>-tray.png   32px tray icon per SPA color skin

    The static default set is deliberately BLUE: it is rendered from appicon-<DefaultSkin>.png,
    NOT from the untinted orange source art appicon.png. At runtime the shell swaps to
    skins\<id>.* the moment the SPA reports its favicon (see src\nodepilot-desktop\src\skins.ts),
    so window and tray icon follow the skin the user picked.

    The per-skin set is discovered from public\appicon-*.png instead of a hardcoded list, so a new
    UI skin needs no change here - regenerate its brand asset with scripts\generate-logo-skins.py
    and rerun this script.

    Called by deploy\desktop\Build-DesktopInstaller.ps1. Run it standalone before
    `npm start` in src\nodepilot-desktop to get real icons in the from-source dev loop.

.EXAMPLE
    ./scripts/generate-desktop-icons.ps1
#>
[CmdletBinding()]
param(
    # Skin whose brand asset becomes the static default. Must exist as public\appicon-<id>.png.
    [ValidatePattern('^[a-z][a-z0-9-]{0,31}$')]
    [string] $DefaultSkin = 'dark',
    # Optional extra copy of the generated .ico (Inno Setup's SetupIconFile).
    [string] $SetupIconPath
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$RepoRoot  = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$PublicDir = Join-Path $RepoRoot 'src\nodepilot-ui\public'
$AssetsDir = Join-Path $RepoRoot 'src\nodepilot-desktop\assets'
$SkinsDir  = Join-Path $AssetsDir 'skins'

# Skin ids reach a path join in the Electron main process, so they are held to the same strict
# charset on both sides (see SKIN_ID in src\nodepilot-desktop\src\skins.ts).
$SKIN_ID = '^[a-z][a-z0-9-]{0,31}$'

Add-Type -AssemblyName System.Drawing

function New-ScaledBitmap([System.Drawing.Image] $source, [int] $size) {
    # 32bpp ARGB + HighQualityBicubic keeps the logo's transparency and edges intact when the
    # source is reduced to icon sizes.
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($source, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)))
    $g.Dispose()
    return $bmp
}

function Save-ScaledPng([System.Drawing.Image] $source, [int] $size, [string] $path) {
    $bmp = New-ScaledBitmap $source $size
    try { $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $bmp.Dispose() }
}

# Multi-resolution ICO, written by hand: System.Drawing's Icon.Save(GetHicon()) emits a single
# resolution, which Windows then rescales badly in Explorer and the taskbar. The ICO container is
# a 6-byte header + one 16-byte directory entry per image + the PNG payloads (PNG-compressed
# entries are valid since Vista and keep the file small).
function Write-MultiSizeIco([System.Drawing.Image] $source, [int[]] $sizes, [string] $path) {
    $payloads = foreach ($s in $sizes) {
        $bmp = New-ScaledBitmap $source $s
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        [pscustomobject]@{ Size = $s; Bytes = $ms.ToArray() }
        $ms.Dispose()
    }

    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    try {
        $bw.Write([uint16]0)                    # reserved
        $bw.Write([uint16]1)                    # type: icon
        $bw.Write([uint16]$payloads.Count)
        $offset = 6 + (16 * $payloads.Count)    # header + directory
        foreach ($p in $payloads) {
            # 256 is encoded as 0 in the single-byte width/height fields.
            $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
            $bw.Write([byte]$dim); $bw.Write([byte]$dim)
            $bw.Write([byte]0)                  # palette colours (0 = truecolour)
            $bw.Write([byte]0)                  # reserved
            $bw.Write([uint16]1)                # colour planes
            $bw.Write([uint16]32)               # bits per pixel
            $bw.Write([uint32]$p.Bytes.Length)
            $bw.Write([uint32]$offset)
            $offset += $p.Bytes.Length
        }
        foreach ($p in $payloads) { $bw.Write($p.Bytes) }
    } finally { $bw.Dispose(); $fs.Dispose() }
}

if (-not (Test-Path -LiteralPath $PublicDir)) { throw "SPA public folder not found: $PublicDir" }
New-Item -ItemType Directory -Force -Path $AssetsDir, $SkinsDir | Out-Null

# --- per-skin set ----------------------------------------------------------------------------
# Stale variants of a removed skin would otherwise linger and keep being applied.
Get-ChildItem -LiteralPath $SkinsDir -Filter '*.png' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force

$skinSources = @(Get-ChildItem -LiteralPath $PublicDir -Filter 'appicon-*.png' -File | Sort-Object Name)
if ($skinSources.Count -eq 0) {
    throw "No per-skin brand assets in $PublicDir (expected appicon-<skin>.png). Run scripts\generate-logo-skins.py first."
}

$skins = @()
foreach ($file in $skinSources) {
    $skin = $file.BaseName.Substring('appicon-'.Length)
    if ($skin -notmatch $SKIN_ID) {
        throw "Brand asset $($file.Name) yields the unsupported skin id '$skin' (allowed: $SKIN_ID)."
    }
    $img = [System.Drawing.Image]::FromFile($file.FullName)
    try {
        Save-ScaledPng $img 256 (Join-Path $SkinsDir "$skin.png")
        # Tray sits in the notification area at 16px (Windows picks 20/24 on high DPI, hence 32).
        Save-ScaledPng $img 32  (Join-Path $SkinsDir "$skin-tray.png")
    } finally { $img.Dispose() }
    $skins += $skin
}

# --- static default set (blue) ---------------------------------------------------------------
$defaultSource = Join-Path $PublicDir "appicon-$DefaultSkin.png"
if (-not (Test-Path -LiteralPath $defaultSource)) {
    throw "Default skin '$DefaultSkin' has no brand asset ($defaultSource). Available: $($skins -join ', ')."
}

$appIco = Join-Path $AssetsDir 'icon.ico'
$img = [System.Drawing.Image]::FromFile($defaultSource)
try {
    Save-ScaledPng $img 256 (Join-Path $AssetsDir 'icon.png')
    Save-ScaledPng $img 32  (Join-Path $AssetsDir 'tray.png')
    Write-MultiSizeIco -source $img -sizes @(16, 32, 48, 256) -path $appIco
} finally { $img.Dispose() }

if ($SetupIconPath) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $SetupIconPath) | Out-Null
    Copy-Item -LiteralPath $appIco -Destination $SetupIconPath -Force
}

Write-Host ("    default '{0}': icon.ico {1:N0} KB (16/32/48/256) + icon.png + tray.png" -f $DefaultSkin, ((Get-Item $appIco).Length / 1KB))
Write-Host ("    skins\: {0} variants ({1})" -f $skins.Count, ($skins -join ', '))
