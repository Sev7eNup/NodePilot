#requires -Version 5.1
<# Compiles the real wizard against inert payloads. The generated EXE must never be installed. #>
[CmdletBinding()]
param([switch]$KeepArtifacts)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
. (Join-Path $PSScriptRoot '..\desktop\Resolve-IsccPath.ps1')
$compiler = Resolve-NodePilotIsccPath
if (-not $compiler) { throw 'Inno Setup 6 is required for the wizard compile check.' }
$fixture = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('nodepilot-wizard-test-' + [guid]::NewGuid().ToString('N'))))
try {
    foreach ($folder in @('payload', 'deploy', 'out')) {
        $null = New-Item -ItemType Directory -Path (Join-Path $fixture $folder) -Force
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\..\src\nodepilot-desktop\assets\icon.ico') -Destination (Join-Path $fixture 'setup-icon.ico')
    Copy-Item -Path (Join-Path $PSScriptRoot '..\*.ps1') -Destination (Join-Path $fixture 'payload')
    Set-Content -LiteralPath (Join-Path $fixture 'deploy\fixture.txt') -Value 'Compile fixture only'
    foreach ($name in @('NodePilot.zip', 'NodePilot.zip.manifest.json', 'NodePilot.zip.manifest.json.p7s',
        'nodepilot-release-signing.cer', 'dotnet-runtime-win-x64.exe', 'aspnetcore-runtime-win-x64.exe')) {
        Set-Content -LiteralPath (Join-Path $fixture "payload\$name") -Value 'INERT COMPILE FIXTURE - NEVER EXECUTE'
    }
    Set-Content -LiteralPath (Join-Path $fixture 'LICENSE.txt') -Value 'INERT COMPILE FIXTURE - NEVER INSTALL'
    Add-Type -AssemblyName System.Drawing
    foreach ($size in @('164x314', '192x386', '246x459', '328x628', '55x55', '64x68', '83x80', '110x106')) {
        $dimensions = $size.Split('x')
        $bitmap = New-Object Drawing.Bitmap ([int]$dimensions[0]), ([int]$dimensions[1])
        try {
            $prefix = if ([int]$dimensions[0] -ge 164) { 'wizard-image' } else { 'wizard-small' }
            $bitmap.Save((Join-Path $fixture "$prefix-$size.bmp"), [Drawing.Imaging.ImageFormat]::Bmp)
        } finally { $bitmap.Dispose() }
    }
    & $compiler '/Qp' "/DStageDir=$fixture" "/DOutputDir=$fixture\out" '/DAppVersion=0.0.0-compile-test' (Join-Path $PSScriptRoot 'NodePilotServer.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Server wizard compilation failed.' }
    Write-Host 'Server wizard compiled successfully against inert payloads.'
} finally {
    if ($KeepArtifacts) { Write-Host "Compile fixture retained: $fixture" }
    else {
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $fixture.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($fixture) -notlike 'nodepilot-wizard-test-*') { throw 'Unsafe fixture cleanup path.' }
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}
