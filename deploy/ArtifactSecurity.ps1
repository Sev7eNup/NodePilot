#requires -Version 5.1

Set-StrictMode -Version 3.0

function Normalize-NodePilotThumbprint {
    param([Parameter(Mandatory)][string]$Thumbprint)
    $normalized = ($Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    if ($normalized -notmatch '^[0-9A-F]{40,128}$') {
        throw "Certificate thumbprint must contain 40 to 128 hexadecimal characters."
    }
    return $normalized
}

function New-NodePilotRandomBase64 {
    <#
      One definition, three consumers: the installer's External-Trigger key default, the setup
      adapter (which has to generate that key itself, because the installer prints it to a console
      the wizard does not have), and anything else that needs a CSPRNG secret.

      RNGCryptoServiceProvider works on both Windows PowerShell 5.1 (.NET Framework) and
      PowerShell 7 (.NET 6+). RandomNumberGenerator.Fill() is Core-only.
    #>
    param([int]$ByteCount = 48)
    $buffer = New-Object byte[] $ByteCount
    $rng = [System.Security.Cryptography.RNGCryptoServiceProvider]::new()
    try { $rng.GetBytes($buffer) } finally { $rng.Dispose() }
    return [Convert]::ToBase64String($buffer)
}

function Import-NodePilotPkcsTypes {
    if ('System.Security.Cryptography.Pkcs.SignedCms' -as [type]) { return }
    try { Add-Type -AssemblyName System.Security.Cryptography.Pkcs -ErrorAction Stop }
    catch { Add-Type -AssemblyName System.Security -ErrorAction Stop }
}

function ConvertFrom-NodePilotHex {
    param([Parameter(Mandatory)][string]$Hex)
    if ($Hex.Length % 2 -ne 0 -or $Hex -notmatch '^[0-9A-Fa-f]+$') { throw "Invalid hexadecimal value in artifact manifest." }
    $bytes = New-Object byte[] ($Hex.Length / 2)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $bytes[$i] = [Convert]::ToByte($Hex.Substring($i * 2, 2), 16)
    }
    return $bytes
}

function Test-NodePilotFixedTimeEqual {
    param([Parameter(Mandatory)][byte[]]$Left, [Parameter(Mandatory)][byte[]]$Right)
    if ($Left.Length -ne $Right.Length) { return $false }
    $difference = 0
    for ($i = 0; $i -lt $Left.Length; $i++) { $difference = $difference -bor ($Left[$i] -bxor $Right[$i]) }
    return $difference -eq 0
}

function Get-NodePilotStreamSha256 {
    param([Parameter(Mandatory)][IO.Stream]$Stream)
    if (-not $Stream.CanSeek) { throw "Artifact verification stream must be seekable." }
    $originalPosition = $Stream.Position
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $Stream.Position = 0
        return ([BitConverter]::ToString($sha.ComputeHash($Stream))).Replace('-', '')
    }
    finally {
        $Stream.Position = $originalPosition
        $sha.Dispose()
    }
}

function New-NodePilotRestrictedFileSecurity {
    param(
        [Parameter(Mandatory)][string]$ServiceAccount,
        [switch]$SkipServiceRule
    )

    $systemSid = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $administratorsSid = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $security = New-Object System.Security.AccessControl.FileSecurity
    $security.SetAccessRuleProtection($true, $false)
    # Use well-known SIDs, not localised account names. For example,
    # BUILTIN\Administrators cannot be resolved on a German Windows host.
    foreach ($identity in @($systemSid, $administratorsSid)) {
        $security.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $identity, 'FullControl', 'None', 'None', 'Allow')))
    }
    if (-not $SkipServiceRule) {
        $normalizedServiceAccount = $ServiceAccount.Trim().ToLowerInvariant()
        $serviceIdentity = if ($normalizedServiceAccount -in @(
            'localsystem', '.\localsystem', 'system', 'nt authority\system', 's-1-5-18')) {
            $systemSid
        } elseif ($normalizedServiceAccount -match '^s-\d+(?:-\d+)+$') {
            [System.Security.Principal.SecurityIdentifier]::new($ServiceAccount.Trim())
        } else {
            $ServiceAccount
        }
        $security.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $serviceIdentity, 'Read', 'None', 'None', 'Allow')))
    }
    return $security
}

function Set-NodePilotServiceOwnedFileAcl {
    <#
      Hands a secret the SERVICE wrote for itself over to a new service identity.

      RestrictedFileWriter creates jwt-secret.key and admin-setup.token owned by whoever the
      service was at the time, protected, with exactly one ACE: that identity, FullControl. It
      then refuses to use a file it cannot verify - which is correct, and which is why an
      installation that changes the service identity leaves both files behind as rubble: the new
      identity cannot open them, and the old one no longer runs.

      This writes the same descriptor the service itself would have written, for the identity it
      is about to become. Owner AND the single ACE, because the validator checks both.

      Applied unconditionally rather than only when the identity changed: it is idempotent, and a
      machine that is already stuck in that state is repaired by installing over it.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ServiceAccount
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }

    # Well-known SIDs, never localised names: BUILTIN\Administrators and NT AUTHORITY\SYSTEM do
    # not resolve on a German Windows, which is the rule the rest of this file already follows.
    $normalized = $ServiceAccount.Trim().ToLowerInvariant()
    $identity = if ($normalized -in @('localsystem', '.\localsystem', 'system', 'nt authority\system', 's-1-5-18')) {
        [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    }
    elseif ($normalized -match '^s-\d+(?:-\d+)+$') {
        [System.Security.Principal.SecurityIdentifier]::new($ServiceAccount.Trim())
    }
    else {
        ([System.Security.Principal.NTAccount]::new($ServiceAccount.Trim())).Translate(
            [System.Security.Principal.SecurityIdentifier])
    }

    $security = New-Object System.Security.AccessControl.FileSecurity
    $security.SetOwner($identity)
    $security.SetAccessRuleProtection($true, $false)
    $security.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        $identity, 'FullControl', 'None', 'None', 'Allow')))
    Set-Acl -LiteralPath $Path -AclObject $security
}

function Write-NodePilotBootstrapCredentialFile {
    <#
      Leaves the generated first-admin credentials where an unattended rollout can collect them.

      This is the whole point of the random-password path: a silent installation has nobody to show
      a password to, so it has to be written down somewhere predictable. ACL-before-content through
      the same primitive the signed-artifact staging uses - the file exists with SYSTEM +
      Administrators and nothing else from its very first byte, never briefly inheriting from
      DataPath and never existing unprotected with content in it.

      It is a live credential and is deliberately NOT deleted afterwards: a rollout that has not
      collected it yet would otherwise be left with an account nobody can use. Retrieving it,
      removing it and rotating the password is the operator's step, and the documentation says so.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        'PSAvoidUsingPlainTextForPassword', 'Password',
        Justification = 'The file exists precisely so the automation can read this value.')]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Username,
        [Parameter(Mandatory)][string]$Password,
        [Parameter(Mandatory)][string]$Url
    )

    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        [void](New-Item -ItemType Directory -Path $directory -Force)
    }
    # CreateNew below refuses an existing file; a re-run must replace, not fail.
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }

    $payload = [ordered]@{
        username   = $Username
        password   = $Password
        url        = $Url
        createdUtc = (Get-Date).ToUniversalTime().ToString('o')
        note       = 'Live credential. Collect it, delete this file, then rotate the password.'
    } | ConvertTo-Json

    $security = New-NodePilotRestrictedFileSecurity -ServiceAccount 'NT AUTHORITY\SYSTEM' -SkipServiceRule
    $stream = New-NodePilotAclProtectedFileStream -Path $Path -Security $security
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($payload)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush()
    }
    finally { $stream.Dispose() }
}

function New-NodePilotAclProtectedFileStream {
    <#
      Windows PowerShell 5.1 exposes the ACL-aware FileStream constructor directly. Modern
      PowerShell/.NET versions may expose the equivalent operation only through
      FileSystemAclExtensions.Create. Both paths apply the final security descriptor in the
      same native create operation.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][System.Security.AccessControl.FileSecurity]$Security
    )

    $constructorParameterTypes = [Type[]]@(
        [string],
        [IO.FileMode],
        [Security.AccessControl.FileSystemRights],
        [IO.FileShare],
        [int],
        [IO.FileOptions],
        [Security.AccessControl.FileSecurity])
    $constructor = [IO.FileStream].GetConstructor($constructorParameterTypes)
    $arguments = [object[]]@(
        $Path,
        [IO.FileMode]::CreateNew,
        [Security.AccessControl.FileSystemRights]::Write,
        [IO.FileShare]::None,
        4096,
        [IO.FileOptions]::WriteThrough,
        $Security.PSObject.BaseObject)
    if ($constructor) {
        return $constructor.Invoke($arguments)
    }

    $extensionsType = [Type]::GetType(
        'System.IO.FileSystemAclExtensions, System.IO.FileSystem.AccessControl',
        $false)
    if (-not $extensionsType) {
        try {
            Add-Type -AssemblyName System.IO.FileSystem.AccessControl -ErrorAction Stop
            $extensionsType = [Type]::GetType(
                'System.IO.FileSystemAclExtensions, System.IO.FileSystem.AccessControl',
                $false)
        }
        catch {
            throw "This PowerShell runtime has no ACL-aware atomic file creation API: $($_.Exception.Message)"
        }
    }
    $createMethod = $extensionsType.GetMethods() | Where-Object {
        $_.Name -eq 'Create' -and
        $_.IsStatic -and
        $_.GetParameters().Count -eq 7 -and
        $_.GetParameters()[0].ParameterType -eq [IO.FileInfo] -and
        $_.GetParameters()[6].ParameterType -eq [Security.AccessControl.FileSecurity]
    } | Select-Object -First 1
    if (-not $createMethod) {
        throw 'This PowerShell runtime has no ACL-aware atomic file creation API.'
    }
    $arguments[0] = [IO.FileInfo]::new($Path)
    return $createMethod.Invoke($null, $arguments)
}

function Set-NodePilotRestrictedFileAcl {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ServiceAccount,
        [switch]$SkipServiceRule
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Cannot secure missing file '$Path'."
    }
    $security = New-NodePilotRestrictedFileSecurity `
        -ServiceAccount $ServiceAccount `
        -SkipServiceRule:$SkipServiceRule
    Set-Acl -LiteralPath $Path -AclObject $security
}

function Write-NodePilotRestrictedFile {
    <#
      Writes a same-directory temporary file with its final security descriptor in CreateFile,
      flushes it to disk, then atomically renames/replaces the destination. Creating an
      inherited-ACL placeholder and applying Set-Acl later leaves a handle-race in which a
      low-privilege reader can open the empty file before the ACL change and observe the later
      secret write through that already-authorised handle.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][byte[]]$Content,
        [Parameter(Mandatory)][string]$ServiceAccount,
        [switch]$SkipServiceRule
    )

    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        throw "Parent directory for restricted file does not exist: $parent"
    }
    $destinationExisted = Test-Path -LiteralPath $Path
    if ($destinationExisted) {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
            throw "Restricted file destination is not a file: $Path"
        }
        # Restrict an existing destination before replacement. ReplaceFile preserves selected
        # destination metadata on Windows; hardening first guarantees that even that behaviour
        # cannot carry a permissive DACL onto the new content.
        Set-NodePilotRestrictedFileAcl `
            -Path $Path `
            -ServiceAccount $ServiceAccount `
            -SkipServiceRule:$SkipServiceRule
    }

    $security = New-NodePilotRestrictedFileSecurity `
        -ServiceAccount $ServiceAccount `
        -SkipServiceRule:$SkipServiceRule
    $temporaryPath = Join-Path $parent ('.nodepilot-secure-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $replaceBackupPath = Join-Path $parent ('.nodepilot-replaced-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $stream = $null
    try {
        $stream = New-NodePilotAclProtectedFileStream -Path $temporaryPath -Security $security
        $stream.Write($Content, 0, $Content.Length)
        $stream.Flush($true)
        $stream.Dispose()
        $stream = $null

        if ($destinationExisted) {
            [IO.File]::Replace($temporaryPath, $Path, $replaceBackupPath, $true)
        }
        else {
            [IO.File]::Move($temporaryPath, $Path)
        }
        Set-NodePilotRestrictedFileAcl `
            -Path $Path `
            -ServiceAccount $ServiceAccount `
            -SkipServiceRule:$SkipServiceRule
    }
    finally {
        if ($stream) {
            try { $stream.Dispose() } catch {}
        }
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $replaceBackupPath) {
            Remove-Item -LiteralPath $replaceBackupPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function New-NodePilotRestrictedStagingDirectory {
    <#
      An unpredictable directory whose DACL is SYSTEM + Administrators + the current user only,
      applied atomically in the create call rather than afterwards. Also used by the setup
      adapter for its answer file, which is why the name is configurable: an operator who finds
      one of these lying around should be able to tell what put it there.
    #>
    [CmdletBinding()]
    param(
        [string]$ParentPath = [IO.Path]::GetTempPath(),
        [string]$Prefix = 'nodepilot-artifact-'
    )

    if (-not (Test-Path -LiteralPath $ParentPath -PathType Container)) {
        throw "Artifact staging parent does not exist: $ParentPath"
    }
    $path = Join-Path $ParentPath ($Prefix + [Guid]::NewGuid().ToString('N'))
    try {
        $acl = New-Object System.Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true, $false)
        $identities = @{}
        foreach ($identity in @(
            [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
            [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'),
            [System.Security.Principal.WindowsIdentity]::GetCurrent().User)) {
            $identities[$identity.Value] = $identity
        }
        foreach ($identity in $identities.Values) {
            $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
                $identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
        }

        $directory = [IO.DirectoryInfo]::new($path)
        $createMethod = [IO.DirectoryInfo].GetMethod(
            'Create',
            [Type[]]@([Security.AccessControl.DirectorySecurity]))
        if ($createMethod) {
            [void]$createMethod.Invoke($directory, [object[]]@($acl.PSObject.BaseObject))
        }
        else {
            $extensionsType = [Type]::GetType(
                'System.IO.FileSystemAclExtensions, System.IO.FileSystem.AccessControl',
                $false)
            if (-not $extensionsType) {
                Add-Type -AssemblyName System.IO.FileSystem.AccessControl -ErrorAction Stop
                $extensionsType = [Type]::GetType(
                    'System.IO.FileSystemAclExtensions, System.IO.FileSystem.AccessControl',
                    $false)
            }
            $extensionCreate = $extensionsType.GetMethods() | Where-Object {
                $_.Name -eq 'Create' -and
                $_.IsStatic -and
                $_.GetParameters().Count -eq 2 -and
                $_.GetParameters()[0].ParameterType -eq [IO.DirectoryInfo] -and
                $_.GetParameters()[1].ParameterType -eq [Security.AccessControl.DirectorySecurity]
            } | Select-Object -First 1
            if (-not $extensionCreate) {
                throw 'This PowerShell runtime has no ACL-aware atomic directory creation API.'
            }
            [void]$extensionCreate.Invoke(
                $null,
                [object[]]@($directory, $acl.PSObject.BaseObject))
        }
        return $path
    }
    catch {
        Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        throw
    }
}

function Expand-NodePilotArtifactToStaging {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ArtifactPath,
        [string]$ParentPath = [IO.Path]::GetTempPath()
    )

    $stagingPath = New-NodePilotRestrictedStagingDirectory -ParentPath $ParentPath
    try {
        Expand-Archive -LiteralPath $ArtifactPath -DestinationPath $stagingPath -Force
        Assert-NodePilotExtractedFiles -RootPath $stagingPath
        return $stagingPath
    }
    catch {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force -ErrorAction SilentlyContinue
        throw
    }
}

function New-NodePilotExtractedFileManifest {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RootPath)
    $root = (Resolve-Path -LiteralPath $RootPath).Path.TrimEnd('\', '/')
    $manifestPath = Join-Path $root 'ARTIFACT-FILES.sha256.json'
    $files = @(Get-ChildItem -LiteralPath $root -File -Recurse |
        Where-Object { $_.FullName -ne $manifestPath } |
        Sort-Object FullName |
        ForEach-Object {
            [ordered]@{
                path = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
                length = [long]$_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        })
    $json = [ordered]@{ schemaVersion = 1; files = $files } | ConvertTo-Json -Depth 5 -Compress
    [IO.File]::WriteAllText($manifestPath, $json, (New-Object Text.UTF8Encoding($false)))
    return $manifestPath
}

function Assert-NodePilotExtractedFiles {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RootPath)
    $root = (Resolve-Path -LiteralPath $RootPath).Path.TrimEnd('\', '/')
    $manifestPath = Join-Path $root 'ARTIFACT-FILES.sha256.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Extracted artifact file manifest is missing." }
    try { $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "Extracted artifact file manifest is invalid: $($_.Exception.Message)" }
    if ([int]$manifest.schemaVersion -ne 1) { throw "Unsupported extracted-file manifest schema version." }

    $expected = @{}
    foreach ($entry in @($manifest.files)) {
        $relative = [string]$entry.path
        if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or
            $relative.Split('/') -contains '..') { throw "Unsafe path '$relative' in extracted-file manifest." }
        if ($expected.ContainsKey($relative)) { throw "Duplicate path '$relative' in extracted-file manifest." }
        $expected[$relative] = $entry
    }

    $actual = @(Get-ChildItem -LiteralPath $root -File -Recurse |
        Where-Object { $_.FullName -ne $manifestPath })
    if ($actual.Count -ne $expected.Count) { throw "Extracted artifact file count does not match the signed ZIP contents." }
    foreach ($file in $actual) {
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        if (-not $expected.ContainsKey($relative)) { throw "Unexpected extracted artifact file '$relative'." }
        $entry = $expected[$relative]
        if ([long]$entry.length -ne [long]$file.Length) { throw "Extracted artifact length mismatch for '$relative'." }
        $actualHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        if (-not (Test-NodePilotFixedTimeEqual `
            (ConvertFrom-NodePilotHex $actualHash) `
            (ConvertFrom-NodePilotHex ([string]$entry.sha256)))) {
            throw "Extracted artifact hash mismatch for '$relative'."
        }
    }
}

function Assert-NodePilotCodeSigningCertificate {
    <#
      What the certificate is for, and what its key is allowed to do. Two different questions, and
      until Assert-NodePilotSignedArtifact stopped validating the chain, only the first one was
      asked here - the chain engine's default policy answered the second on our behalf: where a
      KeyUsage extension is present it must permit DigitalSignature or NonRepudiation.

      CheckSignature($true) verifies the signature and nothing else. Without the check below, a
      certificate carrying the code-signing EKU while its KeyUsage forbade signing would have
      passed: the EKU says what the certificate is FOR, KeyUsage says what the key MAY DO, and only
      both together answer "may this key sign code".
    #>
    param([Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)

    $codeSigningOid = '1.3.6.1.5.5.7.3.3'
    $ekuExtensions = @($Certificate.Extensions | Where-Object {
        $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]
    })
    if ($ekuExtensions.Count -eq 0 -or -not ($ekuExtensions.EnhancedKeyUsages.Value -contains $codeSigningOid)) {
        throw "Artifact signer certificate $($Certificate.Thumbprint) is not valid for Code Signing."
    }

    # An absent KeyUsage extension means unrestricted in X.509, so only a present one can reject
    # anything - looping over what is there rather than demanding it be there.
    $signingUsages = [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor
                     [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::NonRepudiation
    foreach ($keyUsage in @($Certificate.Extensions | Where-Object {
        $_ -is [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]
    })) {
        if (($keyUsage.KeyUsages -band $signingUsages) -eq 0) {
            throw ("Artifact signer certificate $($Certificate.Thumbprint) has a KeyUsage extension that " +
                   "permits neither DigitalSignature nor NonRepudiation (KeyUsage: $($keyUsage.KeyUsages)).")
        }
    }
}

function Get-NodePilotSigningCertificate {
    param([Parameter(Mandatory)][string]$Thumbprint)
    $normalized = Normalize-NodePilotThumbprint $Thumbprint
    $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Where-Object { (($_.Thumbprint -replace '\s', '').ToUpperInvariant()) -eq $normalized } |
        Select-Object -First 1
    if (-not $cert) { throw "Signing certificate $normalized was not found in CurrentUser/My or LocalMachine/My." }
    if (-not $cert.HasPrivateKey) { throw "Signing certificate $normalized has no accessible private key." }
    if ($cert.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow -or $cert.NotAfter.ToUniversalTime() -lt [DateTime]::UtcNow) {
        throw "Signing certificate $normalized is not currently valid."
    }
    Assert-NodePilotCodeSigningCertificate $cert
    return $cert
}

function New-NodePilotSignedArtifactManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ArtifactPath,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$SigningCertificateThumbprint
    )

    Import-NodePilotPkcsTypes
    $artifact = Get-Item -LiteralPath $ArtifactPath -ErrorAction Stop
    $manifestPath = "$($artifact.FullName).manifest.json"
    $signaturePath = "$manifestPath.p7s"
    $hash = (Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest = [ordered]@{
        schemaVersion = 1
        artifactFile = $artifact.Name
        artifactSha256 = $hash
        artifactLength = [long]$artifact.Length
        version = $Version
        createdAtUtc = [DateTime]::UtcNow.ToString('o')
    } | ConvertTo-Json -Compress
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($manifestPath, $manifest, $utf8)

    $certificate = Get-NodePilotSigningCertificate $SigningCertificateThumbprint
    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    $content = New-Object System.Security.Cryptography.Pkcs.ContentInfo -ArgumentList (, $manifestBytes)
    $cms = New-Object System.Security.Cryptography.Pkcs.SignedCms -ArgumentList $content, $true
    $signer = New-Object System.Security.Cryptography.Pkcs.CmsSigner -ArgumentList $certificate
    $signer.IncludeOption = [System.Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
    $signer.DigestAlgorithm = New-Object System.Security.Cryptography.Oid -ArgumentList '2.16.840.1.101.3.4.2.1' # SHA-256
    $cms.ComputeSignature($signer)
    [IO.File]::WriteAllBytes($signaturePath, $cms.Encode())

    return [pscustomobject]@{ ManifestPath = $manifestPath; SignaturePath = $signaturePath; Sha256 = $hash }
}

function Assert-NodePilotSignedArtifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ArtifactPath,
        [Parameter(Mandatory)][string]$TrustedSignerThumbprint,
        [IO.Stream]$ArtifactStream
    )

    Import-NodePilotPkcsTypes
    $artifact = Get-Item -LiteralPath $ArtifactPath -ErrorAction Stop
    $manifestPath = "$($artifact.FullName).manifest.json"
    $signaturePath = "$manifestPath.p7s"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Signed artifact manifest not found: $manifestPath"
    }
    if (-not (Test-Path -LiteralPath $signaturePath -PathType Leaf)) {
        throw "Detached artifact signature not found: $signaturePath"
    }

    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    $content = New-Object System.Security.Cryptography.Pkcs.ContentInfo -ArgumentList (, $manifestBytes)
    $cms = New-Object System.Security.Cryptography.Pkcs.SignedCms -ArgumentList $content, $true
    $cms.Decode([IO.File]::ReadAllBytes($signaturePath))
    if ($cms.SignerInfos.Count -ne 1) { throw "Artifact manifest must contain exactly one signer." }

    # $true = verify the signature, do NOT validate the certificate chain.
    #
    # The publisher is self-signed, so the trust anchor of its chain IS the signing certificate:
    # validating the chain only confirms what the thumbprint comparison below already establishes,
    # and it does so by demanding that an administrator first import that certificate into
    # LocalMachine\Root - a permanent, machine-wide change on every target, for no gain in
    # authenticity. Requiring it made the first installation on any untouched host fail.
    #
    # What the chain policy did enforce beyond trust is now enforced here, explicitly and in one
    # place: the key usage (Assert-NodePilotCodeSigningCertificate) and the validity window below.
    #
    # Deliberately given up, and worth knowing before anyone signs with a CA-issued certificate
    # instead: the trust anchor, time nesting across a chain, basic/name/policy constraints, and
    # revocation. For a self-signed end-entity certificate with no CRL distribution point only the
    # first of those was ever real - and that one is the point of this change.
    $cms.CheckSignature($true)

    $signerCertificate = $cms.SignerInfos[0].Certificate
    if (-not $signerCertificate) { throw "Artifact signature did not include the signer certificate." }
    Assert-NodePilotCodeSigningCertificate $signerCertificate
    $actualSigner = Normalize-NodePilotThumbprint $signerCertificate.Thumbprint
    $expectedSigner = Normalize-NodePilotThumbprint $TrustedSignerThumbprint
    # The thumbprint is a SHA-1 hash of the certificate - "the expected publisher", not "provably
    # this exact certificate and no other". Good enough against a fixed, published value; a SHA-256
    # pin over RawData would be the stronger form and is a change of its own, because the pinned
    # value is compiled into the setup and printed in every release note.
    if ($actualSigner -ne $expectedSigner) {
        throw "Artifact was signed by untrusted certificate $actualSigner; expected $expectedSigner."
    }

    # Was implicit in the chain validation. Checked after the pin so that the wrong certificate is
    # reported as the wrong certificate rather than as an expired one.
    $now = [DateTime]::UtcNow
    if ($signerCertificate.NotBefore.ToUniversalTime() -gt $now) {
        throw ("Artifact signer certificate $actualSigner is not valid until " +
               "$($signerCertificate.NotBefore.ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss')) UTC.")
    }
    if ($signerCertificate.NotAfter.ToUniversalTime() -lt $now) {
        throw ("Artifact signer certificate $actualSigner expired on " +
               "$($signerCertificate.NotAfter.ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss')) UTC.")
    }

    try { $manifest = [Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "Signed artifact manifest is not valid JSON: $($_.Exception.Message)" }
    if ([int]$manifest.schemaVersion -ne 1) { throw "Unsupported artifact manifest schema version '$($manifest.schemaVersion)'." }
    if ($manifest.artifactFile -cne $artifact.Name) { throw "Manifest artifact filename does not match '$($artifact.Name)'." }
    $artifactLength = if ($ArtifactStream) { $ArtifactStream.Length } else { $artifact.Length }
    if ([long]$manifest.artifactLength -ne [long]$artifactLength) { throw "Artifact length does not match the signed manifest." }

    $actualHash = if ($ArtifactStream) {
        Get-NodePilotStreamSha256 $ArtifactStream
    } else {
        (Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256).Hash
    }
    if (-not (Test-NodePilotFixedTimeEqual `
        (ConvertFrom-NodePilotHex $actualHash) `
        (ConvertFrom-NodePilotHex ([string]$manifest.artifactSha256)))) {
        throw "Artifact SHA-256 does not match the signed manifest."
    }

    return [pscustomobject]@{ Version = [string]$manifest.version; SignerThumbprint = $actualSigner; Sha256 = $actualHash }
}
