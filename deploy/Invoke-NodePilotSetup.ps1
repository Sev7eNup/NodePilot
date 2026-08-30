#requires -Version 5.1

<#
.SYNOPSIS
    Adapter between the GUI setup wizard and the existing NodePilot deployment scripts.
.DESCRIPTION
    The wizard collects answers, writes them into an ACL-protected JSON answer file, and calls
    this script. No installation logic is reimplemented here: every mode ends in a splat into
    Install-NodePilot.ps1, Update-NodePilot.ps1 or one of the provisioning helpers. The answer
    file contract lives in SetupContract.ps1 so it can be tested.

    The answer file is used instead of a command line because:

      * -PostgresPassword on Install-NodePilot.ps1 is a [SecureString] and cannot cross a
        `powershell.exe -File` boundary.
      * `Setup.exe /SILENT /ANSWERFILE=prod.json` gives unattended SCCM/GPO deployment.
      * Inno Setup's Pascal script cannot be unit-tested; the logic here can, via
        Test-SetupAdapter.ps1.
.PARAMETER Mode
    InitSession - create the ACL-protected session directory and report its path.
    Probe       - run the readiness checks and report them. Never mutates anything.
    Certificates- list Cert:\LocalMachine\My for the wizard's picker. Reads nothing else and
                  needs no answer file; the thumbprint can still be typed by hand.
    Provision   - carry out the opt-in fixes the operator ticked on the readiness page.
    Apply       - install or upgrade, whichever the answer file declares.
    Cleanup     - shred the answer file and remove the session directory.

    There is no separate Install and Update mode: the answer file declares its own mode, and a
    second source for the same fact could disagree with it.
.PARAMETER SessionPath
    The session directory from InitSession. Required for Cleanup.
.PARAMETER HandoffPath
    InitSession writes the session directory path here for the caller to read back.
.PARAMETER AnswerFile
    The JSON answer file. Required for Probe, Provision, Install and Update.
.PARAMETER ArtifactPath
    The signed NodePilot zip shipped inside the installer. Required for Install and Update.
.PARAMETER TrustedArtifactSignerThumbprint
    Publisher pin for the artifact signature. Required for Install and Update.
.PARAMETER OutFile
    INI file receiving the result. Written in every mode, including on failure.
.PARAMETER LogPath
    Transcript destination. Defaults to %TEMP%\nodepilot-server-setup.log.
.PARAMETER ProgressFile
    Appended one 'step|text' line per phase so the wizard can show live progress instead of
    appearing frozen during a long phase.
.NOTES
    Exit codes, which the wizard reads:
      0 success (Probe: every required check passed)
      1 the adapter itself crashed
      2 Probe ran and at least one required check failed
      3 the answer file is invalid
      4 install or update failed; the underlying script has already rolled back
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('InitSession', 'Probe', 'Certificates', 'Provision', 'Apply', 'Cleanup')]
    [string]$Mode,
    [string]$SessionPath,
    [string]$HandoffPath,
    [string]$AnswerFile,
    [string]$ArtifactPath,
    [string]$TrustedArtifactSignerThumbprint,
    [string]$OutFile,
    [string]$LogPath,
    [string]$ProgressFile,
    [string]$PayloadRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

# Windows PowerShell 5.1 evaluates parameter defaults before $PSScriptRoot exists when a script
# is launched with -File, so paths are resolved after binding.
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path $env:TEMP 'nodepilot-server-setup.log'
}
# The wizard runs this script from a temporary extraction directory, because the readiness page
# runs before anything is installed. The runtime installer and the publisher certificate live in
# the payload, so their location is passed in rather than derived from this script's path.
if ([string]::IsNullOrWhiteSpace($PayloadRoot)) {
    $PayloadRoot = Split-Path -Parent $scriptDirectory
}

. (Join-Path $scriptDirectory 'SetupContract.ps1')

$result = New-NodePilotResultBuffer
# Set when Apply begins; read by the crash lookup so it cannot attribute an older exception to
# this run. Declared here so the failure handler can read it even if Apply threw before reaching it.
$script:ApplyStartedAt = $null

function Write-NodePilotProgress {
    param([Parameter(Mandatory)][string]$Step, [string]$Text = '')
    if ([string]::IsNullOrWhiteSpace($ProgressFile)) { return }
    try { Add-Content -LiteralPath $ProgressFile -Value "$Step|$Text" -Encoding UTF8 -ErrorAction Stop }
    catch { }  # Progress is cosmetic; it must never break an installation.
}

function Write-NodePilotPhaseProgress {
    <#
      Turns the installer's own output into progress the wizard can draw. Install-NodePilot.ps1
      and Update-NodePilot.ps1 announce every phase they enter, and those lines pass through this
      process on their way to the log.
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Line)

    $phase = Get-NodePilotPhaseProgress -Line $Line
    if (-not $phase) { return }
    Write-NodePilotProgress -Step ([string]$phase.Percent) -Text $phase.Text
}

function Add-NodePilotCheckResults {
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Results)
    foreach ($check in $Results) {
        $section = "check.$($check.Id)"
        Set-NodePilotResult -Buffer $result -Section $section -Name 'title' -Value $check.Title
        Set-NodePilotResult -Buffer $result -Section $section -Name 'status' -Value $check.Status
        Set-NodePilotResult -Buffer $result -Section $section -Name 'detail' -Value $check.Detail
        Set-NodePilotResult -Buffer $result -Section $section -Name 'hint' -Value $check.RemediationHint
        Set-NodePilotResult -Buffer $result -Section $section -Name 'remediation' -Value $check.Remediation
        Set-NodePilotResult -Buffer $result -Section $section -Name 'required' -Value $(if ($check.Required) { 1 } else { 0 })
        Set-NodePilotResult -Buffer $result -Section $section -Name 'canAutoFix' -Value $(if ($check.CanAutoFix) { 1 } else { 0 })
        Set-NodePilotResult -Buffer $result -Section $section -Name 'autoFixLabel' -Value $check.AutoFixLabel
        Set-NodePilotResult -Buffer $result -Section $section -Name 'autoFixDefault' `
            -Value $(if ($check.AutoFixDefault) { 1 } else { 0 })
    }
}

function Invoke-NodePilotFirstLogin {
    <#
      Redeems the one-shot setup token for a first admin account, so an unattended installation
      ends with usable credentials instead of a token nobody is there to type.

      Called against localhost: the loopback listener is up once the installer's health probe has
      passed, and the request never leaves the machine.
    #>
    # PSAvoidUsingPlainTextForPassword is suppressed: the value is generated here, serialised into
    # a JSON request body and written to a credential file, so it is plaintext at every step and a
    # SecureString would only hide that.
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        'PSAvoidUsingPlainTextForPassword', 'Password',
        Justification = 'Sent as a JSON body and written to the credential file; plaintext throughout by design.')]
    param(
        [Parameter(Mandatory)][int]$HttpsPort,
        [Parameter(Mandatory)][string]$Username,
        [Parameter(Mandatory)][string]$Password,
        [Parameter(Mandatory)][string]$Token
    )

    $uri = "https://localhost:$HttpsPort/api/auth/login"
    $body = @{ username = $Username; password = $Password } | ConvertTo-Json -Compress
    $headers = @{ 'X-Setup-Token' = $Token }

    # The certificate on a loopback call names the public hostname, not localhost, so validation
    # is relaxed for this request. The previous policy is restored in the finally block so nothing
    # else in this process inherits it.
    $previousPolicy = $null
    $policyChanged = $false
    try {
        if ($PSVersionTable.PSVersion.Major -lt 6) {
            if (-not ('TrustAllCertsBootstrap' -as [type])) {
                Add-Type @"
using System.Net; using System.Security.Cryptography.X509Certificates;
public class TrustAllCertsBootstrap : ICertificatePolicy {
  public bool CheckValidationResult(ServicePoint s, X509Certificate c, WebRequest r, int p) { return true; }
}
"@
            }
            $previousPolicy = [System.Net.ServicePointManager]::CertificatePolicy
            [System.Net.ServicePointManager]::CertificatePolicy = New-Object TrustAllCertsBootstrap
            [System.Net.ServicePointManager]::SecurityProtocol = 'Tls12, Tls13'
            $policyChanged = $true
            $response = Invoke-WebRequest -Uri $uri -Method POST -Body $body -Headers $headers `
                -ContentType 'application/json' -UseBasicParsing -TimeoutSec 30
        }
        else {
            $response = Invoke-WebRequest -Uri $uri -Method POST -Body $body -Headers $headers `
                -ContentType 'application/json' -UseBasicParsing -TimeoutSec 30 -SkipCertificateCheck
        }
        return [pscustomobject]@{ Status = 'Created'; Detail = "HTTP $($response.StatusCode)" }
    }
    catch {
        # Report the server's own message: a password-policy rejection or a username mismatch is
        # actionable, a generic failure is not.
        $detail = $_.Exception.Message
        try {
            $errorResponse = $_.Exception.Response
            if ($errorResponse) {
                $reader = New-Object IO.StreamReader($errorResponse.GetResponseStream())
                $payload = $reader.ReadToEnd()
                if ($payload) { $detail = $payload }
            }
        } catch { }
        return [pscustomobject]@{ Status = 'Failed'; Detail = $detail }
    }
    finally {
        if ($policyChanged) { [System.Net.ServicePointManager]::CertificatePolicy = $previousPolicy }
    }
}

function Get-NodePilotServiceCrashReason {
    <#
      Returns one sentence explaining why the service was installed but never reported ready. The
      wizard shows only the message it gets back, and a health-probe timeout names a symptom
      rather than a cause; the cause sits in the Application log, for example:

          SocketException (10013): An attempt was made to access a socket in a way forbidden ...

      Best effort: a diagnostic that throws would replace the original failure with its own.
    #>
    # Untyped on purpose: a [datetime] parameter turns an unset caller value into 01-01-0001 rather
    # than staying empty, and Get-WinEvent then scans the whole log.
    param($Since)

    try {
        if ($Since -isnot [datetime]) { $Since = (Get-Date).AddMinutes(-15) }
        $crash = Get-WinEvent -FilterHashtable @{
            LogName = 'Application'; ProviderName = '.NET Runtime'; StartTime = $Since
        } -MaxEvents 10 -ErrorAction Stop |
            Where-Object { $_.Message -like '*NodePilot.Api*' } |
            Select-Object -First 1
        if (-not $crash) { return '' }

        $info = @($crash.Message -split "`r?`n" |
            Where-Object { $_ -like 'Exception Info:*' }) | Select-Object -First 1
        if (-not $info) { return '' }
        # One line only: the caller shows this in a message box, where a stack trace would bury
        # the message.
        return ($info -replace '^Exception Info:\s*', '').Trim()
    }
    catch { return '' }
}

function Add-NodePilotCertificateInventory {
    <#
      Publishes the machine's certificate store for the wizard's picker. Probe and the standalone
      Certificates mode share this one emitter, so both produce the same field order for the
      Pascal reader.
    #>
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Buffer)

    $index = 0
    foreach ($certificate in Get-NodePilotCertificateInventory) {
        Set-NodePilotResult -Buffer $Buffer -Section 'certificates' -Name "$index" `
            -Value (Format-NodePilotCertificateLine -Certificate $certificate)
        $index++
    }
    # Always written, including as 0: the wizard reads the count to decide what to show, and an
    # absent key would make an unreadable store look empty.
    Set-NodePilotResult -Buffer $Buffer -Section 'certificates' -Name 'count' -Value $index
}

function Get-NodePilotPsqlPath {
    <#
      The bundled PostgreSQL client, or empty when this build carries none.

      -PgBinariesPath is optional on the build script, so both cases are normal: without the
      client the Postgres check reports reachability only and marks the login as untested,
      instead of offering a fix that cannot run.
    #>
    $candidate = Join-Path $PayloadRoot 'psql.exe'
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    return ''
}

function ConvertTo-NodePilotPreflightParameters {
    # Mirrors the answer file onto Invoke-NodePilotPreflight, which takes the installer's own
    # variable names rather than the answer file's nesting.
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Answers)

    $splat = @{
        CertificateThumbprint = [string]$Answers['certificate.thumbprint']
        DbProvider            = [string]$Answers['database.provider']
        IsLocalSystem         = ([string]$Answers['identity.type'] -eq 'localSystem')
        ServiceName           = [string]$Answers['serviceName']
        # Only the certificate check reads this, to say "issued for X, you are installing Y".
        PublicHostname        = [string]$Answers['network.publicHostname']
        # Carried into the probe so the readiness page can report a port that cannot be bound
        # before the installer fails on it during its health probe.
        HttpsPort             = [int]$Answers['network.httpsPort']
        HttpPort              = [int]$Answers['network.httpPort']
    }

    # The publisher of the artifact this setup carries. Only the setup knows it, because it holds
    # both the certificate and the thumbprint it was built against, so the check belongs on the
    # readiness page rather than in the installation itself.
    if ($PayloadRoot) {
        $splat['ArtifactSignerCertificatePath'] = Get-NodePilotSignerCertificatePath
        $splat['ExpectedSignerThumbprint'] = [string]$TrustedArtifactSignerThumbprint
    }
    if ($splat['IsLocalSystem']) {
        $splat['ComputerAccount'] = "$env:USERDOMAIN\$env:COMPUTERNAME`$"
        $splat['SqlPrincipal'] = $splat['ComputerAccount']
    }
    else {
        $splat['ServiceAccount'] = [string]$Answers['identity.account']
        $splat['SqlPrincipal'] = [string]$Answers['identity.account']
    }
    if ($splat['DbProvider'] -eq 'sqlserver') {
        $splat['SqlServer'] = [string]$Answers['database.sqlServer']
        $splat['SqlDatabase'] = [string]$Answers['database.sqlDatabase']
        $splat['SqlCertificateHostName'] =
            if ($Answers.Contains('database.sqlCertificateHostName') -and
                -not [string]::IsNullOrWhiteSpace([string]$Answers['database.sqlCertificateHostName'])) {
                [string]$Answers['database.sqlCertificateHostName']
            }
            else {
                # Same derivation Install-NodePilot.ps1 performs, so the probe validates the
                # certificate identity the runtime will actually pin.
                (([string]$Answers['database.sqlServer'] -replace '^tcp:', '') -split '[\\,]')[0]
            }
    }
    else {
        $splat['PostgresHost'] = [string]$Answers['database.postgresHost']
        $splat['PostgresUser'] = [string]$Answers['database.postgresUser']
        $splat['PostgresDatabase'] = [string]$Answers['database.postgresDatabase']
        if ($Answers.Contains('database.postgresPort') -and $Answers['database.postgresPort']) {
            $splat['PostgresPort'] = [int]$Answers['database.postgresPort']
        }
        # Lets the check test whether the service can log in rather than only whether the port
        # answers. Without the client it falls back to the TCP probe on its own.
        if ($Answers.Contains('database.postgresPassword')) {
            $splat['PostgresPassword'] = ConvertTo-NodePilotSecureString `
                -PlainText ([string]$Answers['database.postgresPassword'])
        }
        $splat['PostgresRootCertificate'] = [string]$Answers['database.postgresRootCertificate']
        $splat['PsqlPath'] = Get-NodePilotPsqlPath
        # The superuser lets the check query the catalogue for what is missing and offer to create
        # it, instead of parsing a localised error message.
        if ($Answers.Contains('provisioning.postgresSuperUser')) {
            $splat['PostgresSuperUser'] = [string]$Answers['provisioning.postgresSuperUser']
        }
        if ($Answers.Contains('provisioning.postgresSuperPassword')) {
            $splat['PostgresSuperPassword'] = ConvertTo-NodePilotSecureString `
                -PlainText ([string]$Answers['provisioning.postgresSuperPassword'])
        }
        $splat['CanProvisionPostgres'] =
            -not [string]::IsNullOrWhiteSpace($splat['PsqlPath']) -and
            $splat.Contains('PostgresSuperUser') -and
            -not [string]::IsNullOrWhiteSpace($splat['PostgresSuperUser'])
    }
    if ($Answers.Contains('skips.databaseCheck') -and [bool]$Answers['skips.databaseCheck']) {
        $splat['SkipDatabaseCheck'] = $true
    }
    if ($Answers.Contains('skips.gmsaCheck') -and [bool]$Answers['skips.gmsaCheck']) {
        $splat['SkipGmsaCheck'] = $true
    }
    return $splat
}

function Invoke-ProvisionRuntime {
    # NodePilotServer.iss extracts every dontcopy payload file flat into {tmp} and passes that
    # directory as PayloadRoot, so the runtime installer sits directly in it. More than one match
    # is refused rather than resolved arbitrarily, in case the payload is malformed or tampered
    # with.
    $installers = @(
        Get-ChildItem -LiteralPath $PayloadRoot -Filter 'aspnetcore-runtime-*.exe' -File `
            -ErrorAction SilentlyContinue
    )
    if ($installers.Count -ne 1) {
        Set-NodePilotResult -Buffer $result -Section 'provision.runtime' -Name 'status' -Value 'Fail'
        Set-NodePilotResult -Buffer $result -Section 'provision.runtime' -Name 'detail' `
            -Value "Expected exactly one bundled runtime installer in the payload; found $($installers.Count)."
        return
    }
    $installer = $installers[0]
    $process = Start-Process -FilePath $installer.FullName `
        -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
    # 3010 = installed, reboot pending. 1638 = a newer version is already present.
    $accepted = @(0, 3010, 1638)
    Set-NodePilotResult -Buffer $result -Section 'provision.runtime' -Name 'exitCode' -Value $process.ExitCode
    Set-NodePilotResult -Buffer $result -Section 'provision.runtime' -Name 'status' `
        -Value $(if ($accepted -contains $process.ExitCode) { 'Pass' } else { 'Fail' })
    Set-NodePilotResult -Buffer $result -Section 'provision.runtime' -Name 'detail' -Value $(
        switch ($process.ExitCode) {
            0 { 'Runtime installed.' }
            3010 { 'Runtime installed; a reboot is pending.' }
            1638 { 'A newer runtime is already installed.' }
            default { "Runtime installer failed with exit code $($process.ExitCode). See %TEMP%\dd_*.log." }
        })
}

function Get-NodePilotSignerCertificatePath {
    <#
      Single definition shared by two readers: the readiness check that reports whether this
      machine trusts the publisher, and the fix that imports it. Every payload file is extracted
      flat into {tmp} ([Files] uses dontcopy without recursesubdirs), so the certificate sits
      directly in PayloadRoot.
    #>
    if (-not $PayloadRoot) { return '' }
    return (Join-Path $PayloadRoot 'nodepilot-release-signing.cer')
}

function Invoke-ProvisionSigner {
    $certificateFile = Get-NodePilotSignerCertificatePath
    if (-not (Test-Path -LiteralPath $certificateFile -PathType Leaf)) {
        Set-NodePilotResult -Buffer $result -Section 'provision.signer' -Name 'status' -Value 'Fail'
        Set-NodePilotResult -Buffer $result -Section 'provision.signer' -Name 'detail' `
            -Value 'No publisher certificate found in the payload.'
        return
    }
    # X509Store rather than Import-Certificate: the cmdlet demands an interactive confirmation for
    # the machine root store and therefore cannot run from a silent install.
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificateFile)
    $store = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'LocalMachine')
    try {
        $store.Open('ReadWrite')
        $store.Add($certificate)
    }
    finally { $store.Close() }
    Set-NodePilotResult -Buffer $result -Section 'provision.signer' -Name 'status' -Value 'Pass'
    Set-NodePilotResult -Buffer $result -Section 'provision.signer' -Name 'thumbprint' -Value $certificate.Thumbprint
    Set-NodePilotResult -Buffer $result -Section 'provision.signer' -Name 'detail' `
        -Value "Publisher $($certificate.Subject) trusted in LocalMachine\Root."
}

# ---------------------------------------------------------------------------
# Modes
# ---------------------------------------------------------------------------

function Invoke-NodePilotSetupMode {
    [CmdletBinding()]
    param()

    switch ($Mode) {
        'InitSession' {
            . (Join-Path $scriptDirectory 'ArtifactSecurity.ps1')
            # Under ProgramData rather than the user's TEMP: Inno Setup's LoadStringFromFile
            # returns an AnsiString, so the path has to survive a Unicode-to-ANSI round trip.
            # %TEMP% contains the account name and can hold non-ASCII characters, while
            # %ProgramData% is ASCII on every system.
            $session = New-NodePilotRestrictedStagingDirectory `
                -ParentPath $env:ProgramData -Prefix 'nodepilot-setup-'
            # Everything the wizard drops in here inherits the directory's DACL, which is why the
            # Pascal side performs no ACL work of its own.
            # WriteAllText with a BOM-less encoder, not Set-Content -Encoding UTF8: on Windows
            # PowerShell 5.1 that writes a byte-order mark, and Inno Setup reads this file as raw
            # bytes, so the BOM would become three leading characters of the path.
            if ($HandoffPath) {
                [IO.File]::WriteAllText($HandoffPath, $session, [Text.UTF8Encoding]::new($false))
            }
            Set-NodePilotResult -Buffer $result -Section 'summary' -Name 'sessionPath' -Value $session
            Write-Host $session
            return 0
        }

        'Cleanup' {
            if ($AnswerFile) { Remove-NodePilotAnswerFile -Path $AnswerFile }
            if ($SessionPath -and (Test-Path -LiteralPath $SessionPath -PathType Container)) {
                foreach ($file in Get-ChildItem -LiteralPath $SessionPath -File -ErrorAction SilentlyContinue) {
                    Remove-NodePilotAnswerFile -Path $file.FullName
                }
                Remove-Item -LiteralPath $SessionPath -Recurse -Force -ErrorAction SilentlyContinue
            }
            return 0
        }

        'Probe' {
            . (Join-Path $scriptDirectory 'Preflight.ps1')
            $answers = Read-NodePilotAnswerFile -Path $AnswerFile
            Write-NodePilotProgress -Step 'probe' -Text 'Checking prerequisites'

            $preflightSplat = ConvertTo-NodePilotPreflightParameters -Answers $answers
            $checks = @(Invoke-NodePilotPreflight @preflightSplat)
            Add-NodePilotCheckResults -Results $checks

            # Republished so a re-check picks up a certificate that was imported while the wizard
            # was open, without a detour to the TLS page.
            Add-NodePilotCertificateInventory -Buffer $result

            $failed = @($checks | Where-Object { $_.Status -eq 'Fail' -and $_.Required })
            Set-NodePilotResult -Buffer $result -Section 'summary' -Name 'requiredFailures' -Value $failed.Count
            return $(if ($failed.Count -gt 0) { 2 } else { 0 })
        }

        'Certificates' {
            # The certificate list on its own, for the TLS page, which the operator reaches before
            # the probe has run. Kept separate from Probe: Probe opens a database connection and
            # can block on a network timeout, while this mode needs no answer file, no session
            # directory and no elevation. It reads the machine's certificate store and stops.
            . (Join-Path $scriptDirectory 'Preflight.ps1')
            Add-NodePilotCertificateInventory -Buffer $result
            return 0
        }

        'Provision' {
            $answers = Read-NodePilotAnswerFile -Path $AnswerFile
            $performed = 0

            if ($answers.Contains('provisioning.installDotnetRuntime') -and [bool]$answers['provisioning.installDotnetRuntime']) {
                Write-NodePilotProgress -Step 'runtime' -Text 'Installing the ASP.NET Core runtime'
                Invoke-ProvisionRuntime
                $performed++
            }

            if ($answers.Contains('provisioning.generateSelfSignedCertificate') -and [bool]$answers['provisioning.generateSelfSignedCertificate']) {
                Write-NodePilotProgress -Step 'certificate' -Text 'Creating a self-signed certificate'
                $thumbprint = & (Join-Path $scriptDirectory 'New-NodePilotSelfSignedCertificate.ps1') `
                    -PublicHostname ([string]$answers['network.publicHostname'])
                Set-NodePilotResult -Buffer $result -Section 'provision.certificate' -Name 'status' -Value 'Pass'
                Set-NodePilotResult -Buffer $result -Section 'provision.certificate' -Name 'thumbprint' -Value $thumbprint
                Set-NodePilotResult -Buffer $result -Section 'provision.certificate' -Name 'detail' `
                    -Value 'Self-signed certificate created (lab use).'
                $performed++
            }

            # One key for both providers: create whatever database objects the chosen provider
            # needs. Which script runs follows from database.provider, not from a second
            # answer-file flag that could disagree with it.
            if ($answers.Contains('provisioning.createDatabaseAndLogin') -and [bool]$answers['provisioning.createDatabaseAndLogin']) {
                $outcome = $null
                if ([string]$answers['database.provider'] -eq 'postgres') {
                    Write-NodePilotProgress -Step 'database' -Text 'Creating the PostgreSQL role and database'
                    $psql = Get-NodePilotPsqlPath
                    $superUser = [string]$answers['provisioning.postgresSuperUser']
                    if ([string]::IsNullOrWhiteSpace($superUser)) {
                        # Not an error: provisioning was requested without the credentials it
                        # needs, which the wizard prevents and which is a fixable omission in an
                        # unattended answer file.
                        $outcome = [pscustomobject]@{
                            Status = 'Skipped'
                            Detail = ('No PostgreSQL superuser was given (provisioning.postgresSuperUser), ' +
                                      'so the role and database were left alone.')
                            Remediation = ''
                        }
                    }
                    else {
                        $outcome = & (Join-Path $scriptDirectory 'Provision-NodePilotPostgres.ps1') `
                            -PsqlPath $psql `
                            -HostName ([string]$answers['database.postgresHost']) `
                            -Port ([int]$answers['database.postgresPort']) `
                            -Database ([string]$answers['database.postgresDatabase']) `
                            -User ([string]$answers['database.postgresUser']) `
                            -Password (ConvertTo-NodePilotSecureString -PlainText ([string]$answers['database.postgresPassword'])) `
                            -SuperUser $superUser `
                            -SuperPassword (ConvertTo-NodePilotSecureString -PlainText ([string]$answers['provisioning.postgresSuperPassword'])) `
                            -RootCertificate ([string]$answers['database.postgresRootCertificate'])
                    }
                }
                else {
                    Write-NodePilotProgress -Step 'database' -Text 'Creating the SQL login and database'
                    $principal = if ([string]$answers['identity.type'] -eq 'localSystem') {
                        "$env:USERDOMAIN\$env:COMPUTERNAME`$"
                    }
                    else { [string]$answers['identity.account'] }
                    # The certificate host name has to travel with the server name, or TLS
                    # validation rejects the connection before any DDL runs. The runtime
                    # connection string carries it as HostNameInCertificate and
                    # Invoke-NodePilotPreflight is handed it too. An absent key is fine: the
                    # script derives the same fallback itself.
                    $certificateHostName = if ($answers.Contains('database.sqlCertificateHostName')) {
                        [string]$answers['database.sqlCertificateHostName']
                    } else { '' }
                    $outcome = & (Join-Path $scriptDirectory 'Provision-NodePilotDatabase.ps1') `
                        -Server ([string]$answers['database.sqlServer']) `
                        -Database ([string]$answers['database.sqlDatabase']) `
                        -Principal $principal `
                        -CertificateHostName $certificateHostName
                }
                Set-NodePilotResult -Buffer $result -Section 'provision.database' -Name 'status' -Value $outcome.Status
                Set-NodePilotResult -Buffer $result -Section 'provision.database' -Name 'detail' -Value $outcome.Detail
                Set-NodePilotResult -Buffer $result -Section 'provision.database' -Name 'remediation' -Value $outcome.Remediation
                $performed++
            }

            if ($answers.Contains('provisioning.trustArtifactSigner') -and [bool]$answers['provisioning.trustArtifactSigner']) {
                Write-NodePilotProgress -Step 'signer' -Text 'Trusting the publisher certificate'
                Invoke-ProvisionSigner
                $performed++
            }

            Set-NodePilotResult -Buffer $result -Section 'summary' -Name 'actionsPerformed' -Value $performed
            return 0
        }

        'Apply' {
            # Stamped before anything runs so the crash lookup cannot pick up an exception from
            # an earlier attempt and report it as this run's cause.
            $script:ApplyStartedAt = Get-Date
            # The answer file is authoritative about which of the two this is.
            $declared = [string](Read-NodePilotAnswerFile -Path $AnswerFile)['mode']
            if ($declared -eq 'update') { return Invoke-SetupUpdate }
            return Invoke-SetupInstall
        }
    }
}

function Invoke-SetupInstall {
    $answers = Read-NodePilotAnswerFile -Path $AnswerFile
    . (Join-Path $scriptDirectory 'ArtifactSecurity.ps1')

    $splat = ConvertTo-NodePilotInstallParameters -Answers $answers

    # A certificate created by an earlier Provision in this session has a thumbprint the answer
    # file cannot contain. The wizard reads it from provision.ini and writes it back onto its TLS
    # page; the unattended path has no page to write to, so the value is picked up here instead.
    #
    # Only when the answer file names no certificate of its own: an empty field means generate
    # one, while a named thumbprint is an explicit choice that must not be overwritten.
    if ($splat['CertThumbprint'] -notmatch '^[0-9A-Fa-f]{40}$') {
        $provisionIni = Join-Path (Split-Path -Parent $AnswerFile) 'provision.ini'
        if (Test-Path -LiteralPath $provisionIni -PathType Leaf) {
            $generated = @(Get-Content -LiteralPath $provisionIni -Encoding UTF8 |
                Select-String -Pattern '^thumbprint=([0-9A-Fa-f]{40})$')
            if ($generated.Count -eq 1) {
                $splat['CertThumbprint'] = $generated[0].Matches[0].Groups[1].Value
            }
        }
    }

    # Still nothing: the answer file left the field empty and asked for no certificate to be
    # created. Failing here can name both halves of the choice, whereas the empty string would
    # otherwise reach a mandatory parameter and produce a parameter-binding error.
    if ($splat['CertThumbprint'] -notmatch '^[0-9A-Fa-f]{40}$') {
        throw ('No TLS certificate to install with. Either name one in certificate.thumbprint, or ' +
               'set provisioning.generateSelfSignedCertificate to have a self-signed one created ' +
               'first. Kestrel terminates TLS itself and will not start without a certificate.')
    }

    $splat['ArtifactPath'] = $ArtifactPath
    $splat['TrustedArtifactSignerThumbprint'] = $TrustedArtifactSignerThumbprint

    # Absent means include, so an older answer file keeps the behaviour it had. Only an explicit
    # false drops the source snapshot.
    if ($answers.Contains('includeSourceSnapshot') -and -not [bool]$answers['includeSourceSnapshot']) {
        $splat['OmitSourceSnapshot'] = $true
    }
    # Generated here, not by the installer: the installer prints the key once to a console that
    # does not exist under a hidden Exec, and install-report.txt omits it. This is the only way
    # the wizard can show it to the operator.
    $splat['ExternalTriggerApiKey'] = New-NodePilotRandomBase64 -ByteCount 48

    Write-NodePilotProgress -Step '0' -Text 'Installing NodePilot'
    # 6>&1 because every operator-visible line in the installer is Write-Host, i.e. the
    # information stream. The same pass that reads a line translates it into progress.
    #
    # The line is not written out again: Write-Host reaches the host, and therefore the
    # transcript, whether or not the information stream is redirected, so re-emitting it would
    # duplicate every line of the log.
    & (Join-Path $scriptDirectory 'Install-NodePilot.ps1') @splat 6>&1 |
        ForEach-Object { Write-NodePilotPhaseProgress -Line $_ }

    Set-NodePilotResult -Buffer $result -Section 'result' -Name 'url' -Value (
        'https://{0}:{1}/' -f $answers['network.publicHostname'], $answers['network.httpsPort'])
    Set-NodePilotResult -Buffer $result -Section 'result' -Name 'externalTriggerApiKey' -Value $splat['ExternalTriggerApiKey']
    Set-NodePilotResult -Buffer $result -Section 'result' -Name 'certificateThumbprint' -Value $answers['certificate.thumbprint']
    Set-NodePilotResult -Buffer $result -Section 'result' -Name 'installPath' -Value $answers['installPath']
    Set-NodePilotResult -Buffer $result -Section 'result' -Name 'dataPath' -Value $answers['dataPath']
    Set-NodePilotResult -Buffer $result -Section 'result' -Name 'serviceName' -Value $answers['serviceName']

    # Read the bootstrap token here rather than scraping console text for a secret.
    #
    # Through Get-NodePilotBootstrapToken, not Get-Content: the service writes that file with a
    # single ACE for its own identity, so a plain read from the installing admin is denied even
    # though Test-Path succeeds, because Administrators own the directory. Widening the ACL by
    # hand makes the server reject every setup token afterwards.
    $dataPath = [string]$answers['dataPath']
    # Existence and readability are different failures and must not be reported as one. The API
    # writes no token when the Users table is already populated and deletes any stale one, so an
    # absent file is normal while a present but unreadable one is a problem worth naming.
    $tokenExists = Test-Path -LiteralPath (Join-Path $dataPath 'admin-setup.token') -PathType Leaf
    $token = if ($tokenExists) {
        Get-NodePilotBootstrapToken -DataPath $dataPath -StagingDirectory (Split-Path -Parent $OutFile)
    } else { '' }

    if (-not $tokenExists) {
        # Users already exist, so there is no token to redeem or report.
        Set-NodePilotResult -Buffer $result -Section 'bootstrap' -Name 'status' -Value 'AlreadyProvisioned'
        Set-NodePilotResult -Buffer $result -Section 'bootstrap' -Name 'detail' `
            -Value 'The instance already has users, so no bootstrap token was issued.'
    }
    elseif (-not $token) {
        Set-NodePilotResult -Buffer $result -Section 'result' -Name 'adminSetupTokenUnreadable' -Value 1
        Set-NodePilotResult -Buffer $result -Section 'bootstrap' -Name 'status' -Value 'Failed'
        Set-NodePilotResult -Buffer $result -Section 'bootstrap' -Name 'detail' `
            -Value 'The bootstrap token exists but could not be read; no admin account was created.'
    }
    elseif ($answers.Contains('bootstrap.adminUsername') -and $answers['bootstrap.adminUsername']) {
        $bootstrapUser = [string]$answers['bootstrap.adminUsername']
        $bootstrapPassword = New-NodePilotBootstrapPassword
        $outcome = Invoke-NodePilotFirstLogin -HttpsPort ([int]$answers['network.httpsPort']) `
            -Username $bootstrapUser -Password $bootstrapPassword -Token $token

        Set-NodePilotResult -Buffer $result -Section 'bootstrap' -Name 'status' -Value $outcome.Status
        Set-NodePilotResult -Buffer $result -Section 'bootstrap' -Name 'detail' -Value $outcome.Detail
        if ($outcome.Status -eq 'Created') {
            $credentialPath = Get-NodePilotBootstrapCredentialPath -Answers $answers
            Write-NodePilotBootstrapCredentialFile -Path $credentialPath `
                -Username $bootstrapUser -Password $bootstrapPassword `
                -Url ('https://{0}:{1}/' -f $answers['network.publicHostname'], $answers['network.httpsPort'])
            Set-NodePilotResult -Buffer $result -Section 'bootstrap' -Name 'credentialPath' -Value $credentialPath
            Set-NodePilotResult -Buffer $result -Section 'bootstrap' -Name 'username' -Value $bootstrapUser
            Set-NodePilotResult -Buffer $result -Section 'bootstrap' -Name 'password' -Value $bootstrapPassword
        }
        else {
            # The installation is healthy; only the account is missing. Reporting a failure here
            # would tell SCCM to retry a deployment that already succeeded.
            Set-NodePilotResult -Buffer $result -Section 'result' -Name 'adminSetupToken' -Value $token
        }
    }
    else {
        # No bootstrap requested: the token goes to the finish page.
        Set-NodePilotResult -Buffer $result -Section 'result' -Name 'adminSetupToken' -Value $token
        Set-NodePilotResult -Buffer $result -Section 'bootstrap' -Name 'status' -Value 'TokenIssued'
    }
    return 0
}

function Invoke-SetupUpdate {
    $answers = Read-NodePilotAnswerFile -Path $AnswerFile
    Write-NodePilotProgress -Step '0' -Text 'Updating NodePilot'
    # No HTTPS port here: Update-NodePilot.ps1 derives the probe port from the installed Kestrel
    # configuration, so passing a default would probe the wrong port and roll back a healthy
    # installation.
    $splat = @{
        ArtifactPath                    = $ArtifactPath
        TrustedArtifactSignerThumbprint = $TrustedArtifactSignerThumbprint
        InstallPath                     = [string]$answers['installPath']
        ServiceName                     = [string]$answers['serviceName']
    }
    if ($answers.Contains('dataPath') -and $answers['dataPath']) {
        $splat['DataPath'] = [string]$answers['dataPath']
    }
    # Not re-emitted, for the same reason as the install path above: Write-Host has already
    # reached the transcript, and writing it again doubles every line of the log.
    & (Join-Path $scriptDirectory 'Update-NodePilot.ps1') @splat 6>&1 |
        ForEach-Object { Write-NodePilotPhaseProgress -Line $_ }

    Set-NodePilotResult -Buffer $result -Section 'result' -Name 'installPath' -Value $splat['InstallPath']
    Set-NodePilotResult -Buffer $result -Section 'result' -Name 'serviceName' -Value $splat['ServiceName']

    # An update collects no network answers, so the address comes from the configuration that is
    # already installed - the same source Update-NodePilot.ps1 derives its health-probe port from.
    # Best effort: a finish page without a URL is a small loss, a failed update is not.
    try {
        $installedSettings = Join-Path ([string]$splat['InstallPath']) 'appsettings.Production.json'
        if (Test-Path -LiteralPath $installedSettings -PathType Leaf) {
            $https = (Get-Content -LiteralPath $installedSettings -Raw | ConvertFrom-Json).Kestrel.Https
            if ($https.PublicHostname -and $https.HttpsPort) {
                Set-NodePilotResult -Buffer $result -Section 'result' -Name 'url' `
                    -Value ('https://{0}:{1}/' -f $https.PublicHostname, $https.HttpsPort)
            }
            if ($https.CertificateThumbprint) {
                Set-NodePilotResult -Buffer $result -Section 'result' -Name 'certificateThumbprint' `
                    -Value $https.CertificateThumbprint
            }
        }
    }
    catch { }
    return 0
}

# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

# Every path exits explicitly. `powershell.exe -File` returns 0 for a script that merely wrote
# errors, so an implicit fall-through would report success for a failed installation.
$exitCode = 1
try { Start-Transcript -Path $LogPath -Append -ErrorAction SilentlyContinue | Out-Null } catch { }
try {
    Set-NodePilotResult -Buffer $result -Section 'summary' -Name 'mode' -Value $Mode
    Set-NodePilotResult -Buffer $result -Section 'summary' -Name 'logPath' -Value $LogPath
    $exitCode = Invoke-NodePilotSetupMode
}
catch {
    $message = $_.Exception.Message
    # A failed Apply almost always means the service would not start, and the installer can only
    # report that it never went ready. Naming the exception here is the difference between an
    # operator who reads "SocketException 10013" and one who stares at a health-probe timeout.
    if ($Mode -eq 'Apply') {
        $crashReason = Get-NodePilotServiceCrashReason -Since $script:ApplyStartedAt
        if ($crashReason) { $message = "$message The service failed to start with: $crashReason" }
    }
    Write-Host "[setup] $message" -ForegroundColor Red
    Set-NodePilotResult -Buffer $result -Section 'summary' -Name 'error' -Value $message
    $exitCode = if ($message -match '^Answer file') { 3 }
                elseif ($Mode -eq 'Apply') { 4 }
                else { 1 }
}
finally {
    Set-NodePilotResult -Buffer $result -Section 'summary' -Name 'exitCode' -Value $exitCode
    # The answer file carries the database password; it must not survive the run, whatever
    # happened during it. Probe and Provision keep it - the wizard re-runs both.
    if ($Mode -in @('Apply', 'Cleanup')) {
        Remove-NodePilotAnswerFile -Path $AnswerFile
    }
    Write-NodePilotResultFile -Buffer $result -Path $OutFile
    try { Stop-Transcript -ErrorAction SilentlyContinue | Out-Null } catch { }
}
exit $exitCode
