#requires -Version 5.1

<#
  The answer-file contract between the setup wizard and the deployment scripts.

  Split out of Invoke-NodePilotSetup.ps1 so Test-SetupAdapter.ps1 can load it without triggering
  the adapter's entry point - the same reason ArtifactSecurity.ps1 and Preflight.ps1 are separate
  dot-sourceable units. Static text checks cannot cover answer-file behaviour, and a silent
  mis-splat is exactly the kind of defect that would otherwise reach a production install.

  Contains no top-level executable code.
#>

Set-StrictMode -Version 3.0

# Dotted paths, so a nested typo is named precisely instead of silently ignored. Deliberately
# stricter than PowerShell's own parameter binding: an unattended SCCM answer file with a
# misspelled key would otherwise fail in the middle of an installation rather than before it
# starts.
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
            'identity.account',
            'database.sqlServer', 'database.sqlDatabase', 'database.sqlCertificateHostName',
            'database.postgresHost', 'database.postgresPort', 'database.postgresDatabase',
            'database.postgresUser', 'database.postgresPassword', 'database.postgresRootCertificate',
            'network.allowedHosts', 'network.knownProxyIps',
            'certificate.source',
            'provisioning.installDotnetRuntime', 'provisioning.createDatabaseAndLogin',
            'provisioning.generateSelfSignedCertificate', 'provisioning.trustArtifactSigner',
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
    # The leading byte-order mark has to go before ConvertFrom-Json sees it. UTF8.GetString turns
    # EF BB BF into U+FEFF, which is not whitespace and not a JSON token, so the parser rejects the
    # whole document with "Invalid JSON primitive: ." - the dot being how it renders that
    # unprintable character. Two realistic producers write one: Inno Setup's SaveStringsToUTF8File,
    # which the wizard uses, and Notepad, which an operator hand-writing an answer file uses.
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
    foreach ($key in $keys.Required) {
        if (-not $map.Contains($key) -or $null -eq $map[$key] -or
            ($map[$key] -is [string] -and [string]::IsNullOrWhiteSpace($map[$key]))) {
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
      The whole reason the answer file exists: -PostgresPassword is a [SecureString] and cannot
      cross a `powershell.exe -File` boundary. Built character by character and the source array
      is zeroed afterwards.
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
      specific keys are only ever added for the active provider: passing -PostgresHost alongside
      -DbProvider sqlserver binds fine and then produces a confusing failure much later.
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

    return $splat
}

function Remove-NodePilotAnswerFile {
    <#
      Overwrite before delete. A local Administrator can read the file while the install runs -
      the same reader set that can read the secret's permanent home in the service registry key,
      so this introduces no new attacker class - but there is no reason for it to survive the run.
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
        # Fall through to the delete: a file we could not overwrite still must not be left behind.
    }
    Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
# Result file (INI)
# ---------------------------------------------------------------------------
# INI rather than JSON because Inno Setup has GetIniString built in and no JSON support at all;
# parsing JSON in Pascal would add roughly 120 lines that no test can reach.

function ConvertTo-NodePilotIniValue {
    # INI values cannot span lines; the wizard expands the escapes again before display.
    param([AllowNull()]$Value)
    if ($null -eq $Value) { return '' }
    return ([string]$Value) -replace "`r`n", '\n' -replace "`n", '\n' -replace "`r", '\n'
}

# The installer's own phases, in the order it prints them, with the percentage each one STARTS at.
# Mirrors the Write-Step calls in Install-NodePilot.ps1 - Test-DeploymentTemplates.ps1 pins that
# every entry here still exists there, because a renamed step would silently produce a bar that
# never moves past the phase before it.
#
# The percentages are not equal slices. Extracting the artifact and starting the service are where
# the wall-clock goes (the service start alone waits up to 180 s on the health probe), so they get
# the room. A bar that races to 90% and then sits there for two minutes is worse than no bar.
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

# The updater's four phases. The probe here waits 60 s, not the installer's 180, so the last
# caption promises less.
$script:NodePilotUpdatePhases = @(
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

      Matched on a prefix, because several headings interpolate a value into themselves
      ("Stopping service 'NodePilot'", "Granting 'Log on as a service' to CORP\svc$"). An exact
      comparison cannot express those at all - which is how three of them went unrecognised, and
      why the bar stood still through half of an update.

      A prefix is safe here for a reason worth stating: Write-Step prints its heading flush, while
      Write-Info indents every detail line underneath it. A detail line therefore begins with
      whitespace and cannot be the prefix of any phase name.

      Returning $null for everything else is the behaviour that matters most: an unrecognised line
      has to leave the bar where it is rather than reset it.
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
    # pattern syntax and stop matching without saying so - the same silent class of failure this
    # whole table exists to avoid.
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

      The service writes that file with a single ACE for its own identity, so an elevated
      installing admin is DENIED a plain read whenever the service runs as someone else - which is
      always, for both LocalSystem and a gMSA. Test-Path still returns true, because Administrators
      own the directory, so the naive version looked like it worked and silently produced nothing:
      the finish page showed no token, the operator went looking for the file by hand, and granting
      themselves access on the folder is what then broke the bootstrap outright.

      robocopy /B copies through the backup semantics an elevated administrator already holds - the
      same mechanism the installer prints as a hint for the scripted path. The copy lands in the
      caller's ACL-protected session directory and is shredded immediately after reading.
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
        # /B is the whole point; /NJH /NJS /NP keep robocopy's banner out of the transcript.
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

      The thumbprint comes first and unpadded because it is the only field the wizard puts to
      work - everything after it is label text for the operator. A pipe inside the subject would
      shift the remaining fields, so it is folded to a slash here rather than assumed not to
      occur: an X.500 attribute value may legally contain one.

      The date is formatted against the invariant culture, not the machine's. 'yyyy' resolves
      against the culture's default CALENDAR, so on a server set to Arabic (Saudi Arabia) the same
      call returns a Hijri year - a date in the picker that matches nothing the operator can
      compare it against.
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
