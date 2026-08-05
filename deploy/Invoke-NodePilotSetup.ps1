#requires -Version 5.1

<#
.SYNOPSIS
    Adapter between the GUI setup wizard and the existing NodePilot deployment scripts.
.DESCRIPTION
    The wizard collects answers, writes them into an ACL-protected JSON answer file, and calls
    this script. Nothing here reimplements installation logic: every mode ends in a splat into
    Install-NodePilot.ps1, Update-NodePilot.ps1 or one of the provisioning helpers. The answer
    file contract itself lives in SetupContract.ps1 so it can be tested.

    Three reasons the answer file exists rather than a command line:

      * -PostgresPassword on Install-NodePilot.ps1 is a [SecureString] and cannot cross a
        `powershell.exe -File` boundary at all.
      * `Setup.exe /SILENT /ANSWERFILE=prod.json` gives unattended SCCM/GPO deployment for free.
      * Inno Setup's Pascal script has no unit-test story. Keeping the logic here keeps it
        testable - see Test-SetupAdapter.ps1.
.PARAMETER Mode
    InitSession - create the ACL-protected session directory and report its path.
    Probe       - run the readiness checks and report them. Never mutates anything.
    Certificates- list Cert:\LocalMachine\My for the wizard's picker. Reads nothing else, needs
                  no answer file, and never blocks: the thumbprint can still be typed by hand.
    Provision   - carry out the opt-in fixes the operator ticked on the readiness page.
    Apply       - install or upgrade, whichever the answer file declares.
    Cleanup     - shred the answer file and remove the session directory.

    There is deliberately no separate Install and Update mode. The answer file already declares
    its own mode, and a second way to say the same thing is a second way for the two to disagree -
    which matters most on the unattended path, where nobody is watching.
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
    freezing for the duration of a 180-second health probe.
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
# is launched with -File, so paths are resolved after binding. Same trap as in
# Test-DeploymentTemplates.ps1.
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path $env:TEMP 'nodepilot-server-setup.log'
}
# The wizard runs this script from a temporary extraction directory, because the readiness page
# fires before any file has been installed. The runtime installer and the publisher certificate
# live under the installed payload instead, so their location is passed in rather than guessed
# relative to this script.
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
    }
}

function Get-NodePilotServiceCrashReason {
    <#
      The one sentence worth putting on screen when the service was installed and never reported
      ready. Install-NodePilot.ps1 writes a full diagnostics block into its transcript, but the
      wizard shows only the message it gets back - and "did not report /healthz/ready within 180s"
      names a symptom the operator can do nothing with. The cause sits in the Application log:

          SocketException (10013): An attempt was made to access a socket in a way forbidden ...

      Best-effort by construction. A diagnostic that throws would turn a failed installation into
      a crashed adapter and lose the original error along with it.
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
        # One line only: the caller folds this into a message box, and a stack trace there would
        # bury the sentence that matters.
        return ($info -replace '^Exception Info:\s*', '').Trim()
    }
    catch { return '' }
}

function Add-NodePilotCertificateInventory {
    <#
      Publishes the machine's certificate store for the wizard's picker. Both the probe and the
      standalone Certificates mode call this one emitter, so the two cannot drift into different
      field orders behind a Pascal reader that has no way to tell.
    #>
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Buffer)

    $index = 0
    foreach ($certificate in Get-NodePilotCertificateInventory) {
        Set-NodePilotResult -Buffer $Buffer -Section 'certificates' -Name "$index" `
            -Value (Format-NodePilotCertificateLine -Certificate $certificate)
        $index++
    }
    # Always written, including as 0. The wizard reads the count first and decides between "pick
    # one" and "there are none" on it; an absent key would make an unreadable store look empty.
    Set-NodePilotResult -Buffer $Buffer -Section 'certificates' -Name 'count' -Value $index
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
        # Carried into the probe so the readiness page can say "this port cannot be bound" before
        # the installer finds out the hard way, 180 seconds into a health probe it will lose.
        HttpsPort             = [int]$Answers['network.httpsPort']
        HttpPort              = [int]$Answers['network.httpPort']
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
    $runtimeDirectory = Join-Path $PayloadRoot 'runtime'
    $installer = $null
    if (Test-Path -LiteralPath $runtimeDirectory -PathType Container) {
        $installer = Get-ChildItem -LiteralPath $runtimeDirectory -Filter 'aspnetcore-runtime-*.exe' `
            -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    if (-not $installer) {
        Set-NodePilotResult -Buffer $result -Section 'provision.runtime' -Name 'status' -Value 'Fail'
        Set-NodePilotResult -Buffer $result -Section 'provision.runtime' -Name 'detail' `
            -Value 'No bundled runtime installer found in the payload.'
        return
    }
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

function Invoke-ProvisionSigner {
    $certificateFile = Join-Path $PayloadRoot 'signer\nodepilot-release-signing.cer'
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
            # Under ProgramData rather than the user's TEMP, and not only because an elevated
            # machine-wide installer belongs there: Inno Setup's LoadStringFromFile hands back an
            # AnsiString, so the path has to survive a Unicode-to-ANSI round trip. %TEMP% contains
            # the account name and breaks that for an admin called e.g. "Mueller" spelled with an
            # umlaut; %ProgramData% is ASCII on every system.
            $session = New-NodePilotRestrictedStagingDirectory `
                -ParentPath $env:ProgramData -Prefix 'nodepilot-setup-'
            # Everything the wizard drops in here inherits the directory's DACL, which is why the
            # Pascal side performs no ACL work of its own.
            # WriteAllText with a BOM-less encoder, NOT Set-Content -Encoding UTF8: on Windows
            # PowerShell 5.1 that writes a byte-order mark, and Inno Setup reads this file as raw
            # bytes. The BOM then becomes three leading characters of the path - which looks
            # correct in a log and resolves to nothing.
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

            # Republished here so a re-check picks up a certificate imported while the wizard was
            # open, without the operator having to walk back to the TLS page to refresh it.
            Add-NodePilotCertificateInventory -Buffer $result

            $failed = @($checks | Where-Object { $_.Status -eq 'Fail' -and $_.Required })
            Set-NodePilotResult -Buffer $result -Section 'summary' -Name 'requiredFailures' -Value $failed.Count
            return $(if ($failed.Count -gt 0) { 2 } else { 0 })
        }

        'Certificates' {
            # The certificate list on its own, for the TLS page - which the operator reaches long
            # before the probe has run, and which is where the thumbprint is actually typed.
            # Deliberately not a slice of Probe: Probe opens a database connection and can sit on a
            # network timeout for seconds, and nothing here needs an answer file, a session
            # directory or elevation. It reads the machine's own certificate store and stops.
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

            if ($answers.Contains('provisioning.createDatabaseAndLogin') -and [bool]$answers['provisioning.createDatabaseAndLogin']) {
                Write-NodePilotProgress -Step 'database' -Text 'Creating the SQL login and database'
                $principal = if ([string]$answers['identity.type'] -eq 'localSystem') {
                    "$env:USERDOMAIN\$env:COMPUTERNAME`$"
                }
                else { [string]$answers['identity.account'] }
                $outcome = & (Join-Path $scriptDirectory 'Provision-NodePilotDatabase.ps1') `
                    -Server ([string]$answers['database.sqlServer']) `
                    -Database ([string]$answers['database.sqlDatabase']) `
                    -Principal $principal
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
            # Stamped before anything runs so the crash lookup below cannot pick up an exception
            # from an earlier attempt and report it as this one's cause.
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
    $splat['ArtifactPath'] = $ArtifactPath
    $splat['TrustedArtifactSignerThumbprint'] = $TrustedArtifactSignerThumbprint
    # Generated here, not left to the installer: it prints the key exactly once, to a console that
    # does not exist under a hidden Exec, and install-report.txt omits it by design. This is the
    # only way the wizard can show it to the operator.
    $splat['ExternalTriggerApiKey'] = New-NodePilotRandomBase64 -ByteCount 48

    Write-NodePilotProgress -Step 'install' -Text 'Installing NodePilot'
    # 6>&1 because every operator-visible line in the installer is Write-Host, i.e. the
    # information stream.
    & (Join-Path $scriptDirectory 'Install-NodePilot.ps1') @splat 6>&1 |
        ForEach-Object { Write-Host $_ }

    Set-NodePilotResult -Buffer $result -Section 'result' -Name 'url' -Value (
        'https://{0}:{1}/' -f $answers['network.publicHostname'], $answers['network.httpsPort'])
    Set-NodePilotResult -Buffer $result -Section 'result' -Name 'externalTriggerApiKey' -Value $splat['ExternalTriggerApiKey']
    Set-NodePilotResult -Buffer $result -Section 'result' -Name 'certificateThumbprint' -Value $answers['certificate.thumbprint']
    Set-NodePilotResult -Buffer $result -Section 'result' -Name 'installPath' -Value $answers['installPath']
    Set-NodePilotResult -Buffer $result -Section 'result' -Name 'dataPath' -Value $answers['dataPath']
    Set-NodePilotResult -Buffer $result -Section 'result' -Name 'serviceName' -Value $answers['serviceName']

    # Read the bootstrap token here rather than scraping console text for a secret. The file is
    # owner-only for the service account, so failure is expected, not an error.
    $tokenPath = Join-Path ([string]$answers['dataPath']) 'admin-setup.token'
    try {
        if (Test-Path -LiteralPath $tokenPath -PathType Leaf) {
            Set-NodePilotResult -Buffer $result -Section 'result' -Name 'adminSetupToken' `
                -Value ((Get-Content -LiteralPath $tokenPath -Raw).Trim())
        }
    }
    catch {
        Set-NodePilotResult -Buffer $result -Section 'result' -Name 'adminSetupTokenUnreadable' -Value 1
    }
    return 0
}

function Invoke-SetupUpdate {
    $answers = Read-NodePilotAnswerFile -Path $AnswerFile
    Write-NodePilotProgress -Step 'update' -Text 'Updating NodePilot'
    # Deliberately no HTTPS port here. Update-NodePilot.ps1 derives the probe port from the
    # installed Kestrel configuration precisely because passing the 443 default rolled back a
    # healthy 8443 installation in the lab.
    $splat = @{
        ArtifactPath                    = $ArtifactPath
        TrustedArtifactSignerThumbprint = $TrustedArtifactSignerThumbprint
        InstallPath                     = [string]$answers['installPath']
        ServiceName                     = [string]$answers['serviceName']
    }
    if ($answers.Contains('dataPath') -and $answers['dataPath']) {
        $splat['DataPath'] = [string]$answers['dataPath']
    }
    & (Join-Path $scriptDirectory 'Update-NodePilot.ps1') @splat 6>&1 |
        ForEach-Object { Write-Host $_ }

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
