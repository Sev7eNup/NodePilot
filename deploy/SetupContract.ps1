#requires -Version 5.1

<#
  The answer-file contract between the setup wizard and the deployment scripts.

  Split out of Invoke-NodePilotSetup.ps1 so Test-SetupAdapter.ps1 can dot-source it without
  running the adapter's entry point, like ArtifactSecurity.ps1 and Preflight.ps1.

  Contains no top-level executable code.
#>

Set-StrictMode -Version 3.0

# Dotted paths, so a nested typo is named precisely instead of ignored. Stricter than PowerShell's
# own parameter binding: an unattended answer file with a misspelled key is rejected before the
# installation starts rather than in the middle of it.
$script:NodePilotAnswerFileKeys = @{
    install = @{
        Required = @(
            'schemaVersion', 'mode', 'installPath', 'dataPath', 'serviceName',
            'identity.type', 'database.provider',
            'network.publicHostname', 'network.httpsPort', 'network.httpPort',
            'certificate.thumbprint'
        )
        Optional = @(
            'serviceDisplayName',
            # Absent means include, so an answer file written before this option existed keeps the
            # behaviour it had. Only an explicit false drops knowledge\source.
            'includeSourceSnapshot',
            'identity.account',
            'database.sqlServer', 'database.sqlDatabase', 'database.sqlCertificateHostName',
            'database.postgresHost', 'database.postgresPort', 'database.postgresDatabase',
            'database.postgresUser', 'database.postgresPassword', 'database.postgresRootCertificate',
            'network.allowedHosts', 'network.knownProxyIps',
            'certificate.source',
            'provisioning.installDotnetRuntime', 'provisioning.createDatabaseAndLogin',
            'provisioning.generateSelfSignedCertificate', 'provisioning.trustArtifactSigner',
            # PostgreSQL has no equivalent of Trusted_Connection, so createDatabaseAndLogin needs
            # explicit credentials there, while the SQL Server path uses the installing admin's
            # Windows identity. Provisioning only: the service never sees them.
            'provisioning.postgresSuperUser', 'provisioning.postgresSuperPassword',
            'bootstrap.adminUsername', 'bootstrap.credentialOutputPath',
            'seed.backupPath', 'seed.passphrase',
            'skips.databaseCheck', 'skips.gmsaCheck'
        )
    }
    update = @{
        Required = @('schemaVersion', 'mode', 'installPath', 'serviceName')
        Optional = @('dataPath')
    }
}

function Get-NodePilotAnswerFileKeys {
    # Exposed so the tests and the documentation generator read the same table the parser uses.
    param([Parameter(Mandatory)][string]$AnswerMode)
    if (-not $script:NodePilotAnswerFileKeys.ContainsKey($AnswerMode)) {
        throw "Unknown answer file mode '$AnswerMode'."
    }
    return $script:NodePilotAnswerFileKeys[$AnswerMode]
}

function ConvertTo-NodePilotFlatMap {
    <#
      Flattens the parsed answer file to dotted paths. Arrays stay leaf values: knownProxyIps is a
      list, not a nested object.
    #>
    param(
        [Parameter(Mandatory)][AllowNull()]$InputObject,
        [string]$Prefix = ''
    )
    $map = [ordered]@{}
    if ($null -eq $InputObject) { return $map }
    foreach ($property in $InputObject.PSObject.Properties) {
        $key = if ($Prefix) { "$Prefix.$($property.Name)" } else { $property.Name }
        $value = $property.Value
        if ($value -is [System.Management.Automation.PSCustomObject]) {
            foreach ($entry in (ConvertTo-NodePilotFlatMap -InputObject $value -Prefix $key).GetEnumerator()) {
                $map[$entry.Key] = $entry.Value
            }
        }
        else {
            $map[$key] = $value
        }
    }
    return $map
}

function Read-NodePilotAnswerFile {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Answer file not found: $Path"
    }
    # Strip the byte-order mark before ConvertFrom-Json sees it. UTF8.GetString turns EF BB BF into
    # U+FEFF, which is neither whitespace nor a JSON token, so the parser rejects the whole
    # document. Inno Setup's SaveStringsToUTF8File and Notepad both write one.
    $text = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($Path))
    $text = $text.TrimStart([char]0xFEFF, [char]0xFFFE)
    try { $parsed = $text | ConvertFrom-Json }
    catch { throw "Answer file is not valid JSON: $($_.Exception.Message)" }
    if ($null -eq $parsed) { throw 'Answer file is empty.' }

    $map = ConvertTo-NodePilotFlatMap -InputObject $parsed
    if (-not $map.Contains('schemaVersion')) { throw "Answer file has no 'schemaVersion'." }
    if ([int]$map['schemaVersion'] -ne 1) {
        throw "Answer file schemaVersion $($map['schemaVersion']) is not supported (expected 1)."
    }
    if (-not $map.Contains('mode')) { throw "Answer file has no 'mode'." }
    $answerMode = [string]$map['mode']
    if (-not $script:NodePilotAnswerFileKeys.ContainsKey($answerMode)) {
        throw "Answer file mode '$answerMode' is not one of: $($script:NodePilotAnswerFileKeys.Keys -join ', ')."
    }

    $keys = $script:NodePilotAnswerFileKeys[$answerMode]
    $known = @($keys.Required) + @($keys.Optional)
    foreach ($key in $map.Keys) {
        if ($known -notcontains $key) {
            throw "Answer file contains unknown key '$key' for mode '$answerMode'."
        }
    }
    # Required means the key has to be present, not that its value has to be non-empty. An empty
    # certificate.thumbprint states that there is no certificate yet and the wizard should offer to
    # create one; the prerequisite page acts on that.
    $mayBeEmpty = @('certificate.thumbprint')
    foreach ($key in $keys.Required) {
        $absent = -not $map.Contains($key) -or $null -eq $map[$key]
        if (-not $absent -and $key -notin $mayBeEmpty) {
            $absent = ($map[$key] -is [string] -and [string]::IsNullOrWhiteSpace($map[$key]))
        }
        if ($absent) {
            throw "Answer file is missing required key '$key' for mode '$answerMode'."
        }
    }

    # Conditional requirements, so the operator learns about them here rather than from
    # Install-NodePilot.ps1 several minutes later.
    if ($answerMode -eq 'install') {
        $identityType = [string]$map['identity.type']
        if ($identityType -notin @('localSystem', 'gmsa')) {
            throw "Answer file 'identity.type' must be 'localSystem' or 'gmsa', got '$identityType'."
        }
        if ($identityType -eq 'gmsa' -and -not $map.Contains('identity.account')) {
            throw "Answer file needs 'identity.account' when 'identity.type' is 'gmsa'."
        }
        # Empty is a valid answer; anything else has to be a thumbprint. Unchecked, a typo reaches
        # Kestrel's configuration and only shows up there as a certificate missing from the store.
        $thumbprint = [string]$map['certificate.thumbprint']
        if (-not [string]::IsNullOrWhiteSpace($thumbprint) -and $thumbprint -notmatch '^[0-9A-Fa-f]{40}$') {
            throw ("Answer file 'certificate.thumbprint' must be 40 hexadecimal characters, or empty to " +
                   "have a self-signed certificate created; got '$thumbprint'.")
        }
        $provider = [string]$map['database.provider']
        switch ($provider) {
            'sqlserver' {
                foreach ($key in 'database.sqlServer', 'database.sqlDatabase') {
                    if (-not $map.Contains($key)) { throw "Answer file needs '$key' for provider 'sqlserver'." }
                }
            }
            'postgres' {
                foreach ($key in 'database.postgresHost', 'database.postgresDatabase',
                                 'database.postgresUser', 'database.postgresPassword',
                                 'database.postgresRootCertificate') {
                    if (-not $map.Contains($key)) { throw "Answer file needs '$key' for provider 'postgres'." }
                }
            }
            default { throw "Answer file 'database.provider' must be 'sqlserver' or 'postgres', got '$provider'." }
        }
    }

    return $map
}

function ConvertTo-NodePilotSecureString {
    <#
      Builds a SecureString character by character and zeroes the source array afterwards.
      -PostgresPassword is a [SecureString] and cannot cross a `powershell.exe -File` boundary as
      one, so the answer file carries it as text and it is converted here.
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string]$PlainText)

    $secure = New-Object System.Security.SecureString
    $characters = $PlainText.ToCharArray()
    try {
        foreach ($character in $characters) { $secure.AppendChar($character) }
    }
    finally {
        for ($i = 0; $i -lt $characters.Length; $i++) { $characters[$i] = [char]0 }
    }
    $secure.MakeReadOnly()
    return $secure
}

function ConvertTo-NodePilotInstallParameters {
    <#
      The single place that decides which parameters Install-NodePilot.ps1 receives. Provider-
      specific keys are added only for the active provider: passing -PostgresHost alongside
      -DbProvider sqlserver binds fine and then fails confusingly much later.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Answers)

    $optional = {
        param([string]$Key, $Default = $null)
        if ($Answers.Contains($Key) -and $null -ne $Answers[$Key]) { return $Answers[$Key] }
        return $Default
    }

    $splat = @{
        InstallPath    = [string]$Answers['installPath']
        DataPath       = [string]$Answers['dataPath']
        ServiceName    = [string]$Answers['serviceName']
        CertThumbprint = [string]$Answers['certificate.thumbprint']
        PublicHostname = [string]$Answers['network.publicHostname']
        HttpsPort      = [int]$Answers['network.httpsPort']
        HttpPort       = [int]$Answers['network.httpPort']
        DbProvider     = [string]$Answers['database.provider']
    }

    $displayName = & $optional 'serviceDisplayName'
    if ($displayName) { $splat['ServiceDisplayName'] = [string]$displayName }
    $allowedHosts = & $optional 'network.allowedHosts'
    if ($allowedHosts) { $splat['AllowedHosts'] = [string]$allowedHosts }
    $knownProxies = & $optional 'network.knownProxyIps'
    if ($knownProxies) { $splat['KnownProxyIps'] = [string[]]@($knownProxies) }

    if ([string]$Answers['identity.type'] -eq 'localSystem') {
        $splat['UseLocalSystem'] = $true
    }
    else {
        $splat['ServiceAccount'] = [string]$Answers['identity.account']
    }

    if ([string]$Answers['database.provider'] -eq 'sqlserver') {
        $splat['SqlServer'] = [string]$Answers['database.sqlServer']
        $splat['SqlDatabase'] = [string]$Answers['database.sqlDatabase']
        # Omitted when blank so the installer's own derivation from -SqlServer wins.
        $certificateHostName = & $optional 'database.sqlCertificateHostName'
        if ($certificateHostName) { $splat['SqlCertificateHostName'] = [string]$certificateHostName }
    }
    else {
        $splat['PostgresHost'] = [string]$Answers['database.postgresHost']
        $splat['PostgresDatabase'] = [string]$Answers['database.postgresDatabase']
        $splat['PostgresUser'] = [string]$Answers['database.postgresUser']
        $splat['PostgresRootCertificate'] = [string]$Answers['database.postgresRootCertificate']
        $splat['PostgresPassword'] = ConvertTo-NodePilotSecureString -PlainText ([string]$Answers['database.postgresPassword'])
        $port = & $optional 'database.postgresPort'
        if ($port) { $splat['PostgresPort'] = [int]$port }
    }

    if ([bool](& $optional 'skips.databaseCheck' $false)) { $splat['SkipSqlConnectivityCheck'] = $true }
    if ([bool](& $optional 'skips.gmsaCheck' $false)) { $splat['SkipGmsaCheck'] = $true }

    # Pins which username may consume the one-shot setup token. An unattended install knows the
    # name in advance, so a token intercepted between service start and the adapter's login cannot
    # be spent on an account of the interceptor's choosing.
    $bootstrapAdmin = & $optional 'bootstrap.adminUsername'
    if ($bootstrapAdmin) { $splat['BootstrapAdminUsername'] = [string]$bootstrapAdmin }

    # A configuration backup to restore on first start. The passphrase travels as a SecureString
    # like -PostgresPassword; it unlocks a file holding every credential of the reference machine.
    $seedPath = & $optional 'seed.backupPath'
    if ($seedPath) {
        $splat['SeedBackupPath'] = [string]$seedPath
        $splat['SeedBackupPassphrase'] = ConvertTo-NodePilotSecureString `
            -PlainText ([string]$Answers['seed.passphrase'])
    }

    return $splat
}

function New-NodePilotBootstrapPassword {
    <#
      The first admin's password, random per machine, so no installation ships a known default.

      24 bytes of CSPRNG, which is 32 base64 characters. The server's password policy is
      length-only (MinPasswordLength 8, MaxPasswordBytes 72), so this fits between both bounds.
      The upper bound exists because BCrypt truncates anything past 72 bytes.
    #>
    return New-NodePilotRandomBase64 -ByteCount 24
}

function Get-NodePilotBootstrapCredentialPath {
    <#
      Where the generated credentials are left for the automation to collect. Inside DataPath by
      default: the installer has already restricted that directory to SYSTEM and Administrators,
      and it is a location the caller of a silent installation can predict.
    #>
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary]$Answers
    )
    $configured = [string]$Answers['bootstrap.credentialOutputPath']
    if (-not [string]::IsNullOrWhiteSpace($configured)) { return $configured }
    return (Join-Path ([string]$Answers['dataPath']) 'bootstrap-admin.json')
}

function Remove-NodePilotAnswerFile {
    <#
      Overwrites the file before deleting it. It carries secrets that have no reason to survive
      the installation run.
    #>
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    try {
        $length = (Get-Item -LiteralPath $Path).Length
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $zeros = New-Object byte[] $length
            $stream.Write($zeros, 0, $zeros.Length)
            $stream.Flush($true)
        }
        finally { $stream.Dispose() }
    }
    catch {
        # Fall through to the delete: a file that could not be overwritten must still not remain.
    }
    Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
# Result file (INI)
# ---------------------------------------------------------------------------
# INI rather than JSON because Inno Setup has GetIniString built in and no JSON support, and a
# Pascal JSON parser would be untested code.

function ConvertTo-NodePilotIniValue {
    # INI values cannot span lines; the wizard expands the escapes again before display.
    param([AllowNull()]$Value)
    if ($null -eq $Value) { return '' }
    return ([string]$Value) -replace "`r`n", '\n' -replace "`n", '\n' -replace "`r", '\n'
}

# The installer's phases in the order it prints them, with the percentage each one starts at.
# Mirrors the Write-Step calls in Install-NodePilot.ps1; Test-DeploymentTemplates.ps1 checks that
# every entry here still exists there, because a renamed step stalls the progress bar.
#
# The percentages are not equal slices: extracting the artifact and starting the service take the
# most wall-clock time, so they get the widest ranges.
$script:NodePilotInstallPhases = @(
    [pscustomobject]@{ Step = 'NodePilot installer';                     Percent = 2;  Text = 'Starting the installer' }
    [pscustomobject]@{ Step = 'Pre-flight checks';                       Percent = 8;  Text = 'Checking prerequisites' }
    [pscustomobject]@{ Step = 'Preparing directories';                   Percent = 15; Text = 'Preparing directories' }
    [pscustomobject]@{ Step = 'Extracting artifact';                     Percent = 25; Text = 'Extracting and verifying the signed artifact' }
    [pscustomobject]@{ Step = 'Generating appsettings.Production.json';  Percent = 55; Text = 'Writing the configuration' }
    [pscustomobject]@{ Step = 'Applying ACLs';                           Percent = 62; Text = 'Applying permissions' }
    [pscustomobject]@{ Step = 'Firewall rules';                          Percent = 68; Text = 'Adding firewall rules' }
    [pscustomobject]@{ Step = 'Registering Windows Service';             Percent = 74; Text = 'Registering the Windows service' }
    [pscustomobject]@{ Step = "Granting 'Log on as a service' to";       Percent = 77; Text = 'Granting the service logon right' }
    [pscustomobject]@{ Step = 'Starting service';                        Percent = 80; Text = 'Starting the service - this can take up to three minutes' }
)

# The updater's five phases. Its health probe has a shorter timeout than the installer's, so the
# last caption promises less.
$script:NodePilotUpdatePhases = @(
    # Ahead of the backup on purpose: expanding the files to staging and hashing each one against
    # the signed manifest is the longest part of an update, so it gets a phase of its own.
    [pscustomobject]@{ Step = 'Extracting artifact';          Percent = 10; Text = 'Extracting and verifying the signed artifact - this can take a few minutes' }
    [pscustomobject]@{ Step = 'Backing up current install';   Percent = 20; Text = 'Backing up the current installation' }
    [pscustomobject]@{ Step = 'Stopping service';             Percent = 40; Text = 'Stopping the service' }
    [pscustomobject]@{ Step = 'Installing verified artifact'; Percent = 55; Text = 'Installing the verified artifact' }
    [pscustomobject]@{ Step = 'Starting service';             Percent = 75; Text = 'Starting the service - this can take up to a minute' }
)

function Get-NodePilotInstallPhases {
    # Exposed so the tests and the drift contract read the same tables the translation uses.
    return $script:NodePilotInstallPhases
}

function Get-NodePilotUpdatePhases {
    return $script:NodePilotUpdatePhases
}

function Get-NodePilotPhaseProgress {
    <#
      Translates one line of installer or updater output into a progress position, or $null when
      the line is not a phase heading.

      Matching is by prefix, because several headings interpolate a value into themselves
      ("Stopping service 'NodePilot'"), which an exact comparison cannot express. A prefix is
      unambiguous because Write-Step prints its heading flush while Write-Info indents every
      detail line, so a detail line can never be the prefix of a phase name.

      Returning $null for every other line leaves the bar where it is instead of resetting it.
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Line)

    if ($Line -cmatch '^\[install\]\s(.*)$') {
        return Find-NodePilotPhase -Text $matches[1] -Phases $script:NodePilotInstallPhases
    }
    if ($Line -cmatch '^\[update\]\s(.*)$') {
        return Find-NodePilotPhase -Text $matches[1] -Phases $script:NodePilotUpdatePhases
    }
    return $null
}

function Find-NodePilotPhase {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory)][object[]]$Phases
    )
    # StartsWith, not -clike: the wildcard operator would read '[', ']' and '*' in a phase name as
    # pattern syntax and stop matching without reporting anything.
    foreach ($phase in $Phases) {
        if ($Text.StartsWith($phase.Step, [System.StringComparison]::Ordinal)) {
            return [pscustomobject]@{ Percent = $phase.Percent; Text = $phase.Text }
        }
    }
    return $null
}

function Get-NodePilotBootstrapToken {
    <#
      Reads admin-setup.token for display on the wizard's finish page.

      The service writes that file with a single ACE for its own identity, so the installing admin
      is denied a plain read whenever the service runs as LocalSystem or a gMSA. Test-Path still
      returns true, because Administrators own the directory.

      robocopy /B reads through the backup semantics an elevated administrator already holds. The
      copy lands in the caller's ACL-protected session directory and is shredded after reading.
    #>
    param(
        [Parameter(Mandatory)][string]$DataPath,
        [Parameter(Mandatory)][string]$StagingDirectory
    )

    $tokenPath = Join-Path $DataPath 'admin-setup.token'
    if (-not (Test-Path -LiteralPath $tokenPath)) { return '' }

    # Direct read first: it succeeds when the installer and the service share an identity, and it
    # avoids putting a second copy of the secret on disk when it does.
    try { return (Get-Content -LiteralPath $tokenPath -Raw -ErrorAction Stop).Trim() } catch { }

    $scratch = Join-Path $StagingDirectory ([Guid]::NewGuid().ToString('N'))
    try {
        [void](New-Item -ItemType Directory -Path $scratch -ErrorAction Stop)
        # /B uses backup semantics; /NJH /NJS /NP keep robocopy's banner out of the transcript.
        & robocopy.exe $DataPath $scratch 'admin-setup.token' /B /NJH /NJS /NP | Out-Null
        $copy = Join-Path $scratch 'admin-setup.token'
        if (-not (Test-Path -LiteralPath $copy)) { return '' }
        return (Get-Content -LiteralPath $copy -Raw -ErrorAction Stop).Trim()
    }
    catch { return '' }
    finally {
        # The copy is a live credential; it must not outlive this call.
        if (Test-Path -LiteralPath $scratch) {
            foreach ($file in @(Get-ChildItem -LiteralPath $scratch -File -ErrorAction SilentlyContinue)) {
                Remove-NodePilotAnswerFile -Path $file.FullName
            }
            Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Format-NodePilotCertificateLine {
    <#
      One certificate from the machine store as a single INI value, for the wizard's picker:

          <thumbprint>|<subject>|<hasPrivateKey>|<yyyy-MM-dd>

      The thumbprint comes first because it is the only field the wizard uses; the rest is label
      text for the operator. An X.500 attribute value may legally contain a pipe, which would
      shift the remaining fields, so pipes in the subject are folded to a slash.

      The date is formatted against the invariant culture: 'yyyy' resolves against the culture's
      default calendar, so a machine set to a Hijri locale would otherwise report a Hijri year.
    #>
    param([Parameter(Mandatory)]$Certificate)

    $subject = [string]$Certificate.Subject
    if ([string]::IsNullOrWhiteSpace($subject)) { $subject = '(no subject)' }
    return '{0}|{1}|{2}|{3}' -f `
        $Certificate.Thumbprint,
        ($subject -replace '\|', '/'),
        $(if ($Certificate.HasKey) { '1' } else { '0' }),
        $Certificate.NotAfter.ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
}

function New-NodePilotResultBuffer {
    return [ordered]@{}
}

function Set-NodePilotResult {
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary]$Buffer,
        [Parameter(Mandatory)][string]$Section,
        [Parameter(Mandatory)][string]$Name,
        [AllowNull()]$Value
    )
    if (-not $Buffer.Contains($Section)) { $Buffer[$Section] = [ordered]@{} }
    $Buffer[$Section][$Name] = ConvertTo-NodePilotIniValue -Value $Value
}

function Write-NodePilotResultFile {
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary]$Buffer,
        [string]$Path
    )
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($section in $Buffer.GetEnumerator()) {
        $lines.Add("[$($section.Key)]")
        foreach ($entry in $section.Value.GetEnumerator()) {
            $lines.Add("$($entry.Key)=$($entry.Value)")
        }
        $lines.Add('')
    }
    [IO.File]::WriteAllLines($Path, $lines, [Text.UTF8Encoding]::new($false))
}
