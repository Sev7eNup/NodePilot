#requires -Version 5.1
<#
.SYNOPSIS
    Checks the test-only entry points and the existing CMS contract with a temporary signer.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
. (Join-Path $PSScriptRoot '..\ArtifactSecurity.ps1')

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('nodepilot-signpath-test-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $testRoot)
$certificate = $null
$oldRunnerEnvironment = $env:RUNNER_ENVIRONMENT
$checks = 0

function Assert-Rejected {
    param([string]$Name, [scriptblock]$Action, [string]$MessagePattern)
    $rejected = $false
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notmatch $MessagePattern) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "Expected rejection: $Name" }
    Write-Output "PASS: $Name"
    $script:checks++
}

try {
    $env:RUNNER_ENVIRONMENT = 'local'
    Assert-Rejected -Name 'Build refuses a local machine before installing tools or creating keys' `
        -Action { & (Join-Path $PSScriptRoot 'Build-InstallerTest.ps1') } `
        -MessagePattern 'disposable GitHub-hosted runner'
    Assert-Rejected -Name 'Signature verification refuses local test-certificate trust' -Action {
        & (Join-Path $PSScriptRoot 'Test-SignedInstaller.ps1') -Target Server -Version '1.2.3' `
            -SignedDirectory $testRoot -UnsignedDirectory $testRoot `
            -RequestUrl 'https://app.signpath.io/' -CmsThumbprint ('0' * 40)
    } -MessagePattern 'disposable GitHub-hosted runner'
    $env:RUNNER_ENVIRONMENT = $oldRunnerEnvironment

    $certificate = New-SelfSignedCertificate -Type CodeSigningCert `
        -Subject 'CN=NodePilot installer signing contract test' -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyAlgorithm RSA -KeyLength 2048 -KeyExportPolicy NonExportable `
        -NotAfter ([DateTime]::Now.AddDays(1))
    $payload = Join-Path $testRoot 'payload.txt'
    [IO.File]::WriteAllText($payload, 'test payload')
    $artifact = Join-Path $testRoot 'NodePilot-1.2.3.zip'
    Compress-Archive -LiteralPath $payload -DestinationPath $artifact
    $signed = New-NodePilotSignedArtifactManifest -ArtifactPath $artifact -Version '1.2.3' `
        -SigningCertificateThumbprint $certificate.Thumbprint
    [void](Assert-NodePilotSignedArtifact -ArtifactPath $artifact -TrustedSignerThumbprint $certificate.Thumbprint)
    Write-Output 'PASS: Runner-style CMS signer preserves the existing pinned-publisher contract'
    $checks++

    Assert-Rejected -Name 'Different CMS publisher is rejected' -Action {
        [void](Assert-NodePilotSignedArtifact -ArtifactPath $artifact -TrustedSignerThumbprint ('0' * 40))
    } -MessagePattern 'untrusted certificate'

    $originalManifest = [IO.File]::ReadAllBytes($signed.ManifestPath)
    [IO.File]::AppendAllText($signed.ManifestPath, ' ')
    Assert-Rejected -Name 'Edited CMS manifest is rejected' -Action {
        [void](Assert-NodePilotSignedArtifact -ArtifactPath $artifact -TrustedSignerThumbprint $certificate.Thumbprint)
    } -MessagePattern 'signature|hash|invalid|verification'
    [IO.File]::WriteAllBytes($signed.ManifestPath, $originalManifest)

    [IO.File]::AppendAllText($artifact, 'tampered')
    Assert-Rejected -Name 'Edited payload is rejected by its signed manifest' -Action {
        [void](Assert-NodePilotSignedArtifact -ArtifactPath $artifact -TrustedSignerThumbprint $certificate.Thumbprint)
    } -MessagePattern 'length|hash|mismatch'
    Write-Output "Installer signing contract: $checks checks passed."
} finally {
    $env:RUNNER_ENVIRONMENT = $oldRunnerEnvironment
    if ($null -ne $certificate) {
        Remove-Item -LiteralPath ('Cert:\CurrentUser\My\' + $certificate.Thumbprint) -Force
    }
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedTestRoot.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedTestRoot) -notlike 'nodepilot-signpath-test-*') {
        throw 'Refusing to remove a test directory outside the temporary test location.'
    }
    Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
}
