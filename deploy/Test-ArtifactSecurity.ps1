#requires -Version 5.1
<#
.SYNOPSIS
    Exercises the deployment artifact and restricted-file security primitives.
.DESCRIPTION
    Runs without administrator rights and is intentionally compatible with both Windows
    PowerShell 5.1 and PowerShell 7. It verifies manifest enforcement, tamper detection,
    non-inheriting ACLs, localised-Windows-safe SID rules, and atomic restricted-file writes.
#>

[CmdletBinding()]
param([string]$ArtifactSecurityPath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ArtifactSecurityPath)) {
    $ArtifactSecurityPath = Join-Path $scriptDirectory 'ArtifactSecurity.ps1'
}
if (-not (Test-Path -LiteralPath $ArtifactSecurityPath -PathType Leaf)) {
    throw "Artifact security helper not found: $ArtifactSecurityPath"
}
. $ArtifactSecurityPath

function Assert-RestrictedAcl {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string[]]$AllowedSid
    )

    $acl = Get-Acl -LiteralPath $Path
    if (-not $acl.AreAccessRulesProtected) {
        throw "ACL inheritance remains enabled on '$Path'."
    }
    $allowed = @{}
    foreach ($sid in $AllowedSid) { $allowed[$sid] = $true }
    foreach ($rule in @($acl.Access)) {
        $sid = $rule.IdentityReference.Translate(
            [System.Security.Principal.SecurityIdentifier]).Value
        if (-not $allowed.ContainsKey($sid)) {
            throw "Unexpected ACL principal '$sid' on '$Path'."
        }
    }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'nodepilot-artifact-security-test-' + [Guid]::NewGuid().ToString('N'))
$payloadRoot = Join-Path $testRoot 'payload'
$zipPath = Join-Path $testRoot 'artifact.zip'
$stagingPath = $null

try {
    New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $payloadRoot 'NodePilot.Api.exe'),
        'signed-test-payload',
        (New-Object Text.UTF8Encoding($false)))
    [void](New-NodePilotExtractedFileManifest -RootPath $payloadRoot)
    Compress-Archive -Path (Join-Path $payloadRoot '*') -DestinationPath $zipPath

    $stagingPath = Expand-NodePilotArtifactToStaging `
        -ArtifactPath $zipPath `
        -ParentPath $testRoot
    Assert-NodePilotExtractedFiles -RootPath $stagingPath

    $currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $privilegedSids = @('S-1-5-18', 'S-1-5-32-544', $currentSid) | Select-Object -Unique
    Assert-RestrictedAcl -Path $stagingPath -AllowedSid $privilegedSids

    [IO.File]::WriteAllText(
        (Join-Path $stagingPath 'NodePilot.Api.exe'),
        'tampered',
        (New-Object Text.UTF8Encoding($false)))
    $tamperDetected = $false
    try {
        Assert-NodePilotExtractedFiles -RootPath $stagingPath
    }
    catch {
        if ($_.Exception.Message -match 'mismatch') { $tamperDetected = $true }
        else { throw }
    }
    if (-not $tamperDetected) {
        throw 'Extracted artifact tampering was not detected.'
    }

    $secretPath = Join-Path $testRoot 'restricted-settings.json'
    $secretBytes = [Text.Encoding]::UTF8.GetBytes('{"secret":"first"}')
    try {
        Write-NodePilotRestrictedFile `
            -Path $secretPath `
            -Content $secretBytes `
            -ServiceAccount $currentSid
    }
    finally {
        [Array]::Clear($secretBytes, 0, $secretBytes.Length)
    }
    Assert-RestrictedAcl -Path $secretPath -AllowedSid $privilegedSids
    if ([IO.File]::ReadAllText($secretPath) -ne '{"secret":"first"}') {
        throw 'Restricted file content did not round-trip.'
    }

    # Replacing an existing secret must also recreate it with the final DACL in CreateFile.
    $replacementBytes = [Text.Encoding]::UTF8.GetBytes('{"secret":"second"}')
    try {
        Write-NodePilotRestrictedFile `
            -Path $secretPath `
            -Content $replacementBytes `
            -ServiceAccount $currentSid
    }
    finally {
        [Array]::Clear($replacementBytes, 0, $replacementBytes.Length)
    }
    Assert-RestrictedAcl -Path $secretPath -AllowedSid $privilegedSids
    if ([IO.File]::ReadAllText($secretPath) -ne '{"secret":"second"}') {
        throw 'Restricted file replacement did not round-trip.'
    }

    Write-Host 'Artifact security checks passed (manifest, tamper detection, staging ACL and atomic file ACL).' -ForegroundColor Green
}
finally {
    if ($stagingPath -and (Test-Path -LiteralPath $stagingPath)) {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
