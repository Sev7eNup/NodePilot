#requires -Version 5.1
<# Builds an inert copy of the current wizard for navigation, layout or fault inspection. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Wizard', 'Layout', 'Fault')][string]$Kind,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
. (Join-Path $repoRoot 'deploy\desktop\Resolve-IsccPath.ps1')
$compiler = Resolve-NodePilotIsccPath
if (-not $compiler) { throw 'Inno Setup 6 is required.' }
$stage = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
if (Test-Path -LiteralPath $stage) { throw 'OutputPath must be a new directory.' }
$sourcePath = Join-Path $repoRoot 'deploy\server\NodePilotServer.iss'
$source = [IO.File]::ReadAllText($sourcePath).Replace("`r`n", "`n")

function Set-PreviewText([string]$Text, [string]$Needle, [string]$Replacement) {
    $index = $Text.IndexOf($Needle, [StringComparison]::Ordinal)
    if ($index -lt 0 -or $Text.IndexOf($Needle, $index + $Needle.Length, [StringComparison]::Ordinal) -ge 0) {
        throw "Expected exactly one preview seam: $Needle"
    }
    return $Text.Replace($Needle, $Replacement)
}

$source = Set-PreviewText $source 'AppId={{03EAD540-1472-4A1B-9F06-9CB3D358E202}' ('AppId={{' + [guid]::NewGuid() + '}')
$source = Set-PreviewText $source 'AppName=NodePilot Server' "AppName=NodePilot $Kind PREVIEW (no installation)"
$source = Set-PreviewText $source 'PrivilegesRequired=admin' 'PrivilegesRequired=lowest'
$source = Set-PreviewText $source '  DetectExistingInstallation();' '  IsUpgrade := False;'
$guard = "begin`n  if AdapterTimedOut then`n  begin`n    Result := 'A previous operation may still be running. Close setup and inspect the log before retrying.';"
$source = Set-PreviewText $source $guard ("begin`n  Result := 'PREVIEW ONLY. Installation is disabled.';`n  Exit;`n" + $guard.Substring(6))
if ($Kind -eq 'Layout') {
    $next = "function NextButtonClick(CurPageID: Integer): Boolean;`nvar`n  ResultCode: Integer;`n  Thumbprint: String;`n  UninstPath: String;`n  I: Integer;`nbegin"
    $source = Set-PreviewText $source $next ($next + "`n  Result := True; Exit;")
}
if ($Kind -eq 'Fault') {
    $source = Set-PreviewText $source 'AdapterTimeoutMs = 2700000;' 'AdapterTimeoutMs = 6000;'
    $skip = "function ShouldSkipPage(PageID: Integer): Boolean;`nbegin"
    $source = Set-PreviewText $source $skip ($skip + "`n  Result := (PageID <> ReadinessPage.ID) and (PageID <> wpReady);`n  Exit;")
}

$utf8 = New-Object Text.UTF8Encoding($false)
foreach ($folder in @('payload', 'deploy', 'out')) {
    $null = New-Item -ItemType Directory -Path (Join-Path $stage $folder)
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'src\nodepilot-desktop\assets\icon.ico') -Destination (Join-Path $stage 'setup-icon.ico')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PreviewAdapter.ps1') -Destination (Join-Path $stage 'payload\Invoke-NodePilotSetup.ps1')
foreach ($name in @('NodePilot.zip', 'NodePilot.zip.manifest.json', 'NodePilot.zip.manifest.json.p7s',
    'nodepilot-release-signing.cer', 'dotnet-runtime-win-x64.exe', 'aspnetcore-runtime-win-x64.exe')) {
    [IO.File]::WriteAllText((Join-Path $stage "payload\$name"), 'INERT PREVIEW PAYLOAD', $utf8)
}
[IO.File]::WriteAllText((Join-Path $stage 'deploy\fixture.txt'), 'INERT PREVIEW PAYLOAD', $utf8)
[IO.File]::WriteAllText((Join-Path $stage 'LICENSE.txt'), 'PREVIEW ONLY. No installation or system checks. Use fictitious input values.', $utf8)
Add-Type -AssemblyName System.Drawing
foreach ($size in @('164x314', '192x386', '246x459', '328x628', '55x55', '64x68', '83x80', '110x106')) {
    $dimensions = $size.Split('x')
    $bitmap = New-Object Drawing.Bitmap ([int]$dimensions[0]), ([int]$dimensions[1])
    try {
        $prefix = if ([int]$dimensions[0] -ge 164) { 'wizard-image' } else { 'wizard-small' }
        $bitmap.Save((Join-Path $stage "$prefix-$size.bmp"), [Drawing.Imaging.ImageFormat]::Bmp)
    } finally { $bitmap.Dispose() }
}
$previewSource = Join-Path $stage 'NodePilotServer.iss'
[IO.File]::WriteAllText($previewSource, $source, $utf8)
$version = '0.0.0-preview-' + $Kind.ToLowerInvariant()
& $compiler '/Qp' "/DStageDir=$stage" "/DOutputDir=$stage\out" "/DAppVersion=$version" $previewSource
if ($LASTEXITCODE -ne 0) { throw 'Preview compilation failed.' }
$exe = Join-Path $stage "out\NodePilot-Server-Setup-$version.exe"
$manifest = [ordered]@{
    kind = $Kind
    createdUtc = [DateTime]::UtcNow.ToString('o')
    wizardSourceSha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
    adapterSha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'PreviewAdapter.ps1') -Algorithm SHA256).Hash
    previewSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    timeoutMilliseconds = $(if ($Kind -eq 'Fault') { 6000 } else { 2700000 })
    installationDisabled = $true
}
[IO.File]::WriteAllText((Join-Path $stage 'preview-manifest.json'), ($manifest | ConvertTo-Json), $utf8)
Write-Output $exe
