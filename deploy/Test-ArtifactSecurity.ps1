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

    # Zip-slip. The extractor relies on ZipFile::ExtractToDirectory refusing an entry that resolves
    # outside the destination. Asserted rather than assumed, because a hand-rolled extraction loop
    # would give that property up without any other signal.
    $slipZip = Join-Path $testRoot 'zip-slip.zip'
    $slipSource = Join-Path $testRoot 'slip-src'
    New-Item -ItemType Directory -Path $slipSource -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $slipSource 'harmless.txt'), 'ok', (New-Object Text.UTF8Encoding($false)))
    Compress-Archive -Path (Join-Path $slipSource '*') -DestinationPath $slipZip -Force
    # Compress-Archive cannot author a traversing entry name, so rewrite one in directly.
    Import-NodePilotZipTypes
    $slipArchive = [IO.Compression.ZipFile]::Open($slipZip, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $slipArchive.CreateEntry('../escaped.txt')
        $writer = New-Object IO.StreamWriter($entry.Open())
        try { $writer.Write('escaped') } finally { $writer.Dispose() }
    }
    finally { $slipArchive.Dispose() }

    $slipBlocked = $false
    try { [void](Expand-NodePilotArtifactToStaging -ArtifactPath $slipZip -ParentPath $testRoot) }
    catch { $slipBlocked = $true }
    if (-not $slipBlocked) {
        throw 'A zip entry escaping the staging directory was extracted instead of rejected.'
    }
    if (Test-Path -LiteralPath (Join-Path $testRoot 'escaped.txt')) {
        throw 'Zip-slip wrote a file outside the staging directory.'
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

    # --- signed artifact verification -----------------------------------------------------------
    # Assert-NodePilotSignedArtifact does not validate the certificate chain, so it has to enforce
    # the key usage and the validity window itself. Each of those gets a rejection case below.
    #
    # Certificates are built in memory; this suite writes to no certificate store.
    Import-NodePilotPkcsTypes

    function New-TestSigningCertificate {
        param(
            [int]$ValidFromDays = -1,
            [int]$ValidToDays = 365,
            [string]$Subject = 'CN=NodePilot Test Signing',
            [switch]$WithoutCodeSigningEku,
            [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]$KeyUsage = 'DigitalSignature'
        )
        $rsa = [System.Security.Cryptography.RSA]::Create(2048)
        $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
            $Subject, $rsa,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        if (-not $WithoutCodeSigningEku) {
            $oids = New-Object System.Security.Cryptography.OidCollection
            [void]$oids.Add((New-Object System.Security.Cryptography.Oid '1.3.6.1.5.5.7.3.3'))
            $request.CertificateExtensions.Add(
                (New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension $oids, $true))
        }
        $request.CertificateExtensions.Add(
            (New-Object System.Security.Cryptography.X509Certificates.X509KeyUsageExtension $KeyUsage, $true))
        $certificate = $request.CreateSelfSigned(
            [DateTimeOffset]::UtcNow.AddDays($ValidFromDays), [DateTimeOffset]::UtcNow.AddDays($ValidToDays))
        # Round-tripped through a PFX because CmsSigner on .NET Framework cannot use the ephemeral
        # key CreateSelfSigned returns. No certificate store is involved; a PFX is just bytes.
        $password = [Guid]::NewGuid().ToString('N')
        $pfx = $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password)
        return [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $pfx, $password,
            [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable)
    }

    function New-TestSignedArtifact {
        param(
            [Parameter(Mandatory)]$Certificate,
            [Parameter(Mandatory)][string]$Directory,
            [string]$ArtifactName = 'NodePilot-9.9.9.zip',
            [string]$Content = 'artifact-bytes',
            [switch]$OmitSignerCertificate
        )
        New-Item -ItemType Directory -Path $Directory -Force | Out-Null
        $artifactPath = Join-Path $Directory $ArtifactName
        [IO.File]::WriteAllText($artifactPath, $Content, (New-Object Text.UTF8Encoding($false)))
        $manifestPath = "$artifactPath.manifest.json"
        $manifest = [ordered]@{
            schemaVersion  = 1
            artifactFile   = (Split-Path -Leaf $artifactPath)
            artifactSha256 = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
            artifactLength = [long](Get-Item -LiteralPath $artifactPath).Length
            version        = '9.9.9'
            createdAtUtc   = [DateTime]::UtcNow.ToString('o')
        } | ConvertTo-Json -Compress
        [IO.File]::WriteAllText($manifestPath, $manifest, (New-Object Text.UTF8Encoding($false)))

        $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
        $contentInfo = New-Object System.Security.Cryptography.Pkcs.ContentInfo -ArgumentList (, $manifestBytes)
        $cms = New-Object System.Security.Cryptography.Pkcs.SignedCms -ArgumentList $contentInfo, $true
        $signer = New-Object System.Security.Cryptography.Pkcs.CmsSigner -ArgumentList $Certificate
        $signer.IncludeOption = if ($OmitSignerCertificate) {
            [System.Security.Cryptography.X509Certificates.X509IncludeOption]::None
        } else {
            [System.Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
        }
        $signer.DigestAlgorithm = New-Object System.Security.Cryptography.Oid '2.16.840.1.101.3.4.2.1'
        $cms.ComputeSignature($signer)
        [IO.File]::WriteAllBytes("$manifestPath.p7s", $cms.Encode())
        return $artifactPath
    }

    function Assert-ArtifactRejected {
        param(
            [Parameter(Mandatory)][string]$Name,
            [Parameter(Mandatory)][string]$ArtifactPath,
            [Parameter(Mandatory)][string]$Thumbprint,
            [Parameter(Mandatory)][string]$MessagePattern
        )
        $message = $null
        try { [void](Assert-NodePilotSignedArtifact -ArtifactPath $ArtifactPath -TrustedSignerThumbprint $Thumbprint) }
        catch { $message = $_.Exception.Message }
        if ($null -eq $message) { throw "Artifact security check failed: $Name (it was accepted)" }
        if ($message -notmatch $MessagePattern) {
            throw "Artifact security check failed: $Name (message '$message' does not match '$MessagePattern')"
        }
    }

    $signingRoot = Join-Path $testRoot 'signing'
    $good = New-TestSigningCertificate
    $goodArtifact = New-TestSignedArtifact -Certificate $good -Directory (Join-Path $signingRoot 'good')

    # The central assertion: a correctly signed artifact verifies even though its publisher is in
    # no trust store on this machine.
    [void](Assert-NodePilotSignedArtifact -ArtifactPath $goodArtifact -TrustedSignerThumbprint $good.Thumbprint)

    # The verification is not blanket-permissive.
    Assert-ArtifactRejected -Name 'a different signer than the pinned one is rejected' `
        -ArtifactPath $goodArtifact -Thumbprint ('A' * 40) -MessagePattern 'untrusted certificate'

    $tamperedDir = Join-Path $signingRoot 'tampered'
    $tamperedArtifact = New-TestSignedArtifact -Certificate $good -Directory $tamperedDir
    [IO.File]::WriteAllText("$tamperedArtifact.manifest.json", '{"schemaVersion":1}',
        (New-Object Text.UTF8Encoding($false)))
    Assert-ArtifactRejected -Name 'a manifest edited after signing is rejected' `
        -ArtifactPath $tamperedArtifact -Thumbprint $good.Thumbprint -MessagePattern '.'

    $swappedDir = Join-Path $signingRoot 'swapped'
    $swappedArtifact = New-TestSignedArtifact -Certificate $good -Directory $swappedDir
    [IO.File]::WriteAllText($swappedArtifact, 'artifact-bytes-but-longer', (New-Object Text.UTF8Encoding($false)))
    Assert-ArtifactRejected -Name 'an artifact replaced after signing is rejected' `
        -ArtifactPath $swappedArtifact -Thumbprint $good.Thumbprint -MessagePattern 'length does not match|hash'

    $renamedDir = Join-Path $signingRoot 'renamed'
    $renamedArtifact = New-TestSignedArtifact -Certificate $good -Directory $renamedDir
    Rename-Item -LiteralPath $renamedArtifact -NewName 'NodePilot-8.8.8.zip'
    Rename-Item -LiteralPath "$renamedArtifact.manifest.json" -NewName 'NodePilot-8.8.8.zip.manifest.json'
    Rename-Item -LiteralPath "$renamedArtifact.manifest.json.p7s" -NewName 'NodePilot-8.8.8.zip.manifest.json.p7s'
    Assert-ArtifactRejected -Name 'an artifact renamed after signing is rejected' `
        -ArtifactPath (Join-Path $renamedDir 'NodePilot-8.8.8.zip') -Thumbprint $good.Thumbprint `
        -MessagePattern 'filename does not match'

    # The two validity-window cases a certificate chain check would otherwise cover.
    $expired = New-TestSigningCertificate -ValidFromDays -400 -ValidToDays -1
    $expiredArtifact = New-TestSignedArtifact -Certificate $expired -Directory (Join-Path $signingRoot 'expired')
    Assert-ArtifactRejected -Name 'an expired signer certificate is rejected' `
        -ArtifactPath $expiredArtifact -Thumbprint $expired.Thumbprint -MessagePattern 'expired on'

    $notYet = New-TestSigningCertificate -ValidFromDays 10 -ValidToDays 400
    $notYetArtifact = New-TestSignedArtifact -Certificate $notYet -Directory (Join-Path $signingRoot 'not-yet')
    Assert-ArtifactRejected -Name 'a signer certificate that is not valid yet is rejected' `
        -ArtifactPath $notYetArtifact -Thumbprint $notYet.Thumbprint -MessagePattern 'not valid until'

    # The EKU says what the certificate is for; KeyUsage says what the key may do. Only both
    # together answer whether the key may sign code, and CheckSignature($true) checks neither.
    $noEku = New-TestSigningCertificate -WithoutCodeSigningEku
    $noEkuArtifact = New-TestSignedArtifact -Certificate $noEku -Directory (Join-Path $signingRoot 'no-eku')
    Assert-ArtifactRejected -Name 'a signer certificate without the code-signing purpose is rejected' `
        -ArtifactPath $noEkuArtifact -Thumbprint $noEku.Thumbprint -MessagePattern 'not valid for Code Signing'

    $wrongUsage = New-TestSigningCertificate -KeyUsage (
        [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment)
    $wrongUsageArtifact = New-TestSignedArtifact -Certificate $wrongUsage `
        -Directory (Join-Path $signingRoot 'wrong-usage')
    Assert-ArtifactRejected -Name 'a signer whose KeyUsage forbids signing is rejected' `
        -ArtifactPath $wrongUsageArtifact -Thumbprint $wrongUsage.Thumbprint `
        -MessagePattern 'neither DigitalSignature nor NonRepudiation'

    # The function documents this case, so it gets a test. CheckSignature rejects it first because
    # it cannot find the signer at all, which is the same verdict.
    $noCertArtifact = New-TestSignedArtifact -Certificate $good `
        -Directory (Join-Path $signingRoot 'no-signer-cert') -OmitSignerCertificate
    Assert-ArtifactRejected -Name 'a signature without the signer certificate is rejected' `
        -ArtifactPath $noCertArtifact -Thumbprint $good.Thumbprint -MessagePattern '.'

    Write-Host 'Artifact security checks passed (manifest, tamper detection, staging ACL, atomic file ACL and signature verification).' -ForegroundColor Green
}
finally {
    if ($stagingPath -and (Test-Path -LiteralPath $stagingPath)) {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
