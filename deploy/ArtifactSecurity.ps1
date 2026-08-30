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
      Returns a Base64-encoded CSPRNG secret. Shared by the installer's External-Trigger key
      default, the setup adapter, and anything else that needs a random secret.

      RNGCryptoServiceProvider works on both Windows PowerShell 5.1 and PowerShell 7;
      RandomNumberGenerator.Fill() is Core-only.
    #>
    param([int]$ByteCount = 48)
    $buffer = New-Object byte[] $ByteCount
    $rng = [System.Security.Cryptography.RNGCryptoServiceProvider]::new()
    try { $rng.GetBytes($buffer) } finally { $rng.Dispose() }
    return [Convert]::ToBase64String($buffer)
}

function Import-NodePilotPkcsTypes {
    <#
      Loads the assembly that carries SignedCms. Windows PowerShell 5.1 ships it inside
      System.Security, PowerShell 7 as System.Security.Cryptography.Pkcs.

      The edition is checked instead of trying a load and catching the failure: Add-Type raises a
      terminating error under the setup's Stop preference, and Start-Transcript records it before
      a catch can swallow it, leaving a misleading error in every setup log.
    #>
    if ('System.Security.Cryptography.Pkcs.SignedCms' -as [type]) { return }
    if ($PSVersionTable.PSEdition -eq 'Core') {
        Add-Type -AssemblyName System.Security.Cryptography.Pkcs -ErrorAction Stop
    }
    else {
        Add-Type -AssemblyName System.Security -ErrorAction Stop
    }
}

function Import-NodePilotZipTypes {
    <#
      System.IO.Compression.ZipFile lives in a separate assembly that Windows PowerShell 5.1 does
      not load on its own; PowerShell 7 has it in the default set. The edition is checked instead
      of catching a failed load, for the same reason as Import-NodePilotPkcsTypes.
    #>
    if ('System.IO.Compression.ZipFile' -as [type]) { return }
    if ($PSVersionTable.PSEdition -ne 'Core') {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
    }
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
      Transfers a secret the service wrote for itself to a new service identity.

      RestrictedFileWriter creates jwt-secret.key and admin-setup.token with the service identity
      as owner and a single FullControl ACE for it, and refuses files it cannot verify, so a
      changed service identity leaves both files unusable. This rewrites that descriptor for the
      new identity, owner and ACE, and runs unconditionally because it is idempotent.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ServiceAccount
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }

    # Use well-known SIDs, never localised names: BUILTIN\Administrators and NT AUTHORITY\SYSTEM
    # do not resolve on a German Windows host.
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
      Writes the generated first-admin credentials where an unattended rollout can collect them:
      a silent installation has nobody to show a password to. The ACL is applied in the create
      call, so the file carries SYSTEM plus Administrators only from its first byte. It holds a
      live credential and is not deleted afterwards; collecting it, deleting it and rotating the
      password is an operator step covered by the documentation.
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
      Creates an unpredictable directory whose DACL grants only SYSTEM, Administrators and the
      current user, applied in the create call rather than afterwards. The name prefix is
      configurable because the setup adapter also uses this for its answer file, so a leftover
      directory can be traced back to whatever created it.
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

    # Resolve to an absolute path: ExtractToDirectory resolves relative paths against the process
    # working directory, which is neither the caller's location nor the staging parent.
    $resolvedArtifact = (Resolve-Path -LiteralPath $ArtifactPath -ErrorAction Stop).Path
    $stagingPath = New-NodePilotRestrictedStagingDirectory -ParentPath $ParentPath
    try {
        # ExtractToDirectory rather than Expand-Archive, which pays per-entry pipeline overhead
        # that dominates a tree of mostly small files.
        #
        # Zip-slip protection is kept: .NET refuses an entry whose resolved path leaves the
        # destination, and Assert-NodePilotExtractedFiles below rejects rooted or dot-dot paths in
        # the manifest, requires an exact file count, and hashes every file against the signed
        # manifest.
        Import-NodePilotZipTypes
        [IO.Compression.ZipFile]::ExtractToDirectory($resolvedArtifact, $stagingPath)
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

function Remove-NodePilotSourceSnapshot {
    <#
      Drops knowledge\source from an installation whose operator asked not to keep the product
      source on the machine. The AI assistant's source-code knowledge source reads that directory;
      without it, that one source is empty and the rest of the assistant is unaffected.

      Ordering is load-bearing: this must run AFTER Assert-NodePilotExtractedFiles. That check
      requires the directory to hold exactly the signed artifact, so removing anything first fails
      the install outright. Running it afterwards keeps the trust chain whole - every file was
      verified against the signed manifest, and only then is a declared subtree dropped.

      Returns $true when something was removed, so the caller can report it.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$InstallPath)

    $snapshot = Join-Path $InstallPath 'knowledge\source'
    if (-not (Test-Path -LiteralPath $snapshot)) { return $false }
    Remove-Item -LiteralPath $snapshot -Recurse -Force -ErrorAction Stop
    return $true
}

function Test-NodePilotSourceSnapshotPresent {
    <#
      Whether an installation currently carries the source snapshot. The updater uses this to
      preserve the operator's choice across an update without depending on the machine-wide
      installation marker, which a second instance on the same host would overwrite.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$InstallPath)

    return (Test-Path -LiteralPath (Join-Path $InstallPath 'knowledge\source'))
}

function Set-DirectoryAclForService {
    <#
      $DataPath must be writable by the service account and readable by Administrators/SYSTEM
      only. Inheritance is disabled so nothing from Program Files or ProgramData parent ACLs
      leaks in.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ServiceAccount,
        [switch]$ReadOnlyForService,
        [switch]$SkipServiceRule
    )

    $acl = Get-Acl $Path
    $acl.SetAccessRuleProtection($true, $false)

    # Set the owner, not just the ACEs. The API refuses to read its bootstrap token when any
    # directory on the way to it has an owner it does not trust, and a reused data directory
    # carries whoever last took ownership of it (the uninstaller's -PurgeData takes ownership to
    # delete owner-only files). A fresh directory is already owned by Administrators.
    $acl.SetOwner([System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))

    # Wipe inherited ACEs that SetAccessRuleProtection preserved-as-explicit.
    $acl.Access | ForEach-Object { $acl.RemoveAccessRule($_) | Out-Null }

    $sysAdmin = @(
        [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
        [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    )
    foreach ($id in $sysAdmin) {
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            $id, 'FullControl',
            'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }

    # LocalSystem is already covered by the SYSTEM FullControl ACE above - adding a second ACE
    # for the same SID is redundant, so the caller passes -SkipServiceRule in that case.
    if (-not $SkipServiceRule) {
        $svcRights = if ($ReadOnlyForService) { 'ReadAndExecute' } else { 'Modify' }
        $svcRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            $ServiceAccount, $svcRights,
            'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($svcRule)
    }

    Set-Acl -Path $Path -AclObject $acl
}

function Assert-NodePilotInstallRootHardened {
    <#
      Checks that only trusted principals can write to the install directory. It is the image path
      of a service running as LocalSystem or a gMSA, so write access there is code execution as
      that account. Shared with the updater, which has to answer the same question.

      -RequireProtectedRules is for the installer, which has just applied a protected DACL. The
      updater omits it, because an older installation under Program Files inherits a safe ACL from
      its parent.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$RequireProtectedRules
    )

    $acl = Get-Acl -Path $Path
    if ($RequireProtectedRules -and -not $acl.AreAccessRulesProtected) {
        throw "Install directory '$Path' still inherits ACEs from its parent. Refusing to register a service whose binaries are governed by an inherited ACL."
    }

    # SYSTEM, Administrators and TrustedInstaller are the principals a machine administrator
    # already trusts with the binaries; any other identity holding a write right can hijack them.
    $trusted = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    $writeMask =
        [System.Security.AccessControl.FileSystemRights]::WriteData -bor
        [System.Security.AccessControl.FileSystemRights]::AppendData -bor
        [System.Security.AccessControl.FileSystemRights]::Delete -bor
        [System.Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [System.Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [System.Security.AccessControl.FileSystemRights]::TakeOwnership

    foreach ($ace in $acl.Access) {
        if ($ace.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) { continue }
        if (($ace.FileSystemRights -band $writeMask) -eq 0) { continue }

        $sid = $null
        try {
            $sid = if ($ace.IdentityReference -is [System.Security.Principal.SecurityIdentifier]) {
                $ace.IdentityReference.Value
            } else {
                $ace.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value
            }
        } catch {
            $sid = $null
        }

        if ($null -eq $sid -or $trusted -notcontains $sid) {
            throw ("Install directory '$Path' grants write access to '$($ace.IdentityReference)'. " +
                   "The binaries there are executed by the NodePilot service, so a non-administrator " +
                   "who can write to them gains code execution as the service account. Re-run " +
                   "Install-NodePilot.ps1, or remove the ACE and restrict the directory to " +
                   "SYSTEM/Administrators FullControl plus read-and-execute for the service account.")
        }
    }
}

function Assert-NodePilotInstallRootHardenedOrRepair {
    <#
      Verify, repair once, verify again, then fail. An untrusted ACE on the install directory is a
      condition an update can fix, and refusing outright would leave an operator with no route to
      the new binaries at all. The repair is Set-DirectoryAclForService, which drops inheritance,
      wipes every explicit ACE and forces the owner back to Administrators, so it clears whatever
      was granted after the installation was laid down.

      The second check is not optional: it is what keeps this a hardening step rather than a
      bypass. If the directory still grants an untrusted principal write access, this throws and
      the caller rolls back.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ServiceAccount
    )

    try {
        Assert-NodePilotInstallRootHardened -Path $Path
        return
    } catch {
        Write-Warning "Install directory is not hardened yet: $($_.Exception.Message)"
    }

    Write-Warning "Repairing the install directory ACL (owner, inheritance and ACEs), then re-checking."
    # The service executes these binaries, it never rewrites them. LocalSystem is already covered
    # by the SYSTEM ACE the repair writes, so it gets no second rule.
    $isLocalSystem = $ServiceAccount -eq 'NT AUTHORITY\SYSTEM'
    Set-DirectoryAclForService -Path $Path -ServiceAccount $ServiceAccount `
        -ReadOnlyForService -SkipServiceRule:$isLocalSystem

    Assert-NodePilotInstallRootHardened -Path $Path
}

function Assert-NodePilotCodeSigningCertificate {
    <#
      Checks both what the certificate is for and what its key may do. The EKU must include code
      signing, and a KeyUsage extension, when present, must permit DigitalSignature or
      NonRepudiation. Only both together answer whether this key may sign code; the CMS signature
      check verifies neither.
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
    # anything. Loop over the extensions that exist instead of requiring one.
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

    # $true verifies the signature only and skips certificate chain validation. The publisher
    # certificate is self-signed, so the chain adds nothing over the thumbprint comparison below
    # while requiring that certificate to be imported into LocalMachine\Root on every host. Key
    # usage and the validity window are checked explicitly instead; trust anchor, constraints and
    # revocation are not, which matters if a CA-issued certificate is ever used here.
    $cms.CheckSignature($true)

    $signerCertificate = $cms.SignerInfos[0].Certificate
    if (-not $signerCertificate) { throw "Artifact signature did not include the signer certificate." }
    Assert-NodePilotCodeSigningCertificate $signerCertificate
    $actualSigner = Normalize-NodePilotThumbprint $signerCertificate.Thumbprint
    $expectedSigner = Normalize-NodePilotThumbprint $TrustedSignerThumbprint
    # The thumbprint is a SHA-1 hash of the certificate, so this identifies the expected publisher
    # rather than proving the exact certificate. A SHA-256 pin over RawData would be stronger, but
    # the pinned value is compiled into the setup and published in the release notes.
    if ($actualSigner -ne $expectedSigner) {
        throw "Artifact was signed by untrusted certificate $actualSigner; expected $expectedSigner."
    }

    # Validity window, checked after the thumbprint pin so a wrong certificate is reported as
    # wrong rather than as expired.
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
