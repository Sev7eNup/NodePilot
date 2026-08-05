#requires -Version 5.1

<#
  Side-effect-free readiness checks for a NodePilot server installation.

  Dot-sourced by Install-NodePilot.ps1 (which asserts on the results and aborts) and, later,
  by the setup wizard's adapter (which renders them as a traffic-light page with a "re-check"
  button). That second consumer is the whole reason this file exists as a separate unit, and
  it dictates the one rule everything here obeys:

      NOTHING IN THIS FILE MAY MUTATE ANYTHING.

  No ALTER DATABASE, no CREATE, no New-Service, no Set-Acl, no New-SelfSignedCertificate, no
  firewall rules. A check that mutates would fire again on every click of a "re-check" button.
  The concrete near-miss: Enable-SqlReadCommittedSnapshot used to live inside the SQL
  reachability try/catch, and it runs
      ALTER DATABASE [x] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE
  which drops every open session on the target database. That is correct install-time work and
  it stayed in Install-NodePilot.ps1. Test-DeploymentTemplates.ps1 enforces this rule so it
  cannot rot back in.

  Two layers:
    * Test-NodePilot*        - one probe each, returns a result object, NEVER throws for a
                               failed check (only for a caller error such as a missing param).
                               This is what makes them callable from a UI button.
    * Invoke-NodePilotPreflight / Assert-NodePilotPreflight
                             - collect the applicable set, then print and abort exactly the
                               way the installer always has.
#>

Set-StrictMode -Version 3.0

# Status values used across this file:
#   Pass    - requirement met
#   Fail    - requirement not met; aborts the install when the check is Required
#   Warn    - worth saying out loud, never aborts
#   Skipped - not applicable to this configuration

function New-NodePilotPreflightResult {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Title,
        [Parameter(Mandatory)][ValidateSet('Pass', 'Fail', 'Warn', 'Skipped')][string]$Status,
        [string]$Detail = '',
        [string]$RemediationHint = '',
        [string]$Remediation = '',
        [string]$AbortMessage = '',
        [bool]$Required = $false,
        [bool]$CanAutoFix = $false,
        [string]$AutoFixLabel = ''
    )
    [pscustomobject]@{
        Id              = $Id
        Title           = $Title
        Status          = $Status
        Detail          = $Detail
        RemediationHint = $RemediationHint
        Remediation     = $Remediation
        AbortMessage    = $AbortMessage
        Required        = $Required
        CanAutoFix      = $CanAutoFix
        AutoFixLabel    = $AutoFixLabel
    }
}

# ---------------------------------------------------------------------------
# Shared SQL plumbing
# ---------------------------------------------------------------------------

function Resolve-NodePilotSqlProbeConnectionString {
    <#
      Windows PowerShell 5.1's legacy System.Data.SqlClient cannot express
      HostNameInCertificate. Connect through the certificate hostname while preserving an
      explicit instance/port suffix so a probe validates the same identity the .NET 10 runtime
      pins via HostNameInCertificate at runtime.
    #>
    param(
        [Parameter(Mandatory)][string]$Server,
        [Parameter(Mandatory)][string]$Database,
        [Parameter(Mandatory)][string]$CertificateHostName
    )
    $serverWithoutPrefix = $Server -replace '^tcp:', ''
    $suffixIndex = $serverWithoutPrefix.IndexOfAny([char[]]@('\', ','))
    $serverSuffix = if ($suffixIndex -ge 0) { $serverWithoutPrefix.Substring($suffixIndex) } else { '' }

    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $builder['Server'] = "tcp:$CertificateHostName$serverSuffix"
    $builder['Database'] = $Database
    $builder['Integrated Security'] = $true
    $builder['Encrypt'] = $true
    $builder['TrustServerCertificate'] = $false
    $builder['Connect Timeout'] = 10
    $builder['Application Name'] = 'NodePilot-Installer'
    return $builder.ConnectionString
}

# ---------------------------------------------------------------------------
# Remediation snippets - shared so the console path and the wizard cannot drift
# ---------------------------------------------------------------------------

function Get-NodePilotSqlRemediationScript {
    param(
        [Parameter(Mandatory)][string]$Principal,
        [Parameter(Mandatory)][string]$Database
    )
    @(
        "CREATE LOGIN [$Principal] FROM WINDOWS;"
        "CREATE DATABASE [$Database];"
        "USE [$Database];"
        "CREATE USER [$Principal] FOR LOGIN [$Principal];"
        "ALTER ROLE db_owner ADD MEMBER [$Principal];"
    ) -join [Environment]::NewLine
}

function Get-NodePilotPostgresRemediationScript {
    param(
        [Parameter(Mandatory)][string]$User,
        [Parameter(Mandatory)][string]$Database
    )
    @(
        "CREATE ROLE $User WITH LOGIN PASSWORD '<same-as--PostgresPassword>';"
        "CREATE DATABASE $Database OWNER $User;"
    ) -join [Environment]::NewLine
}

# ---------------------------------------------------------------------------
# Individual checks
# ---------------------------------------------------------------------------

function Get-NodePilotDotNetCommand {
    <#
      Resolving 'dotnet' by PATH alone is not enough. A clean Windows Server has no dotnet on
      PATH at all, and - the case that actually bites - a process that installed the runtime
      itself still carries the PATH it was started with, so PATH stays stale until it restarts.
      Fall back to the well-known machine-wide location.
    #>
    $onPath = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($onPath) { return $onPath.Source }
    foreach ($root in @($env:ProgramFiles, "$env:ProgramW6432")) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        $candidate = Join-Path $root 'dotnet\dotnet.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    return $null
}

function Test-NodePilotDotNetRuntime {
    $title = 'ASP.NET Core 10 runtime'
    $hint = 'Install the ASP.NET Core 10 runtime (x64) - the plain runtime, not the Hosting Bundle, which also wires up IIS.'
    $link = 'https://dotnet.microsoft.com/download/dotnet/10.0'

    $dotnet = Get-NodePilotDotNetCommand
    if (-not $dotnet) {
        return New-NodePilotPreflightResult -Id 'dotnet' -Title $title -Status 'Fail' -Required $true `
            -CanAutoFix $true -AutoFixLabel 'Install the bundled ASP.NET Core 10 runtime now' `
            -Detail 'dotnet was not found on PATH or under Program Files.' `
            -RemediationHint $hint -Remediation $link `
            -AbortMessage ".NET Runtime not found on PATH. Install the ASP.NET Core 10 runtime from $link."
    }

    $lines = @()
    try { $lines = @(& $dotnet --list-runtimes 2>$null) } catch { $lines = @() }
    if (-not ($lines -match '^Microsoft\.AspNetCore\.App 10\.')) {
        return New-NodePilotPreflightResult -Id 'dotnet' -Title $title -Status 'Fail' -Required $true `
            -CanAutoFix $true -AutoFixLabel 'Install the bundled ASP.NET Core 10 runtime now' `
            -Detail "No Microsoft.AspNetCore.App 10.x runtime reported by '$dotnet --list-runtimes'." `
            -RemediationHint $hint -Remediation $link `
            -AbortMessage ".NET 10 ASP.NET Core Runtime not found. Install the ASP.NET Core 10 runtime ($link)."
    }

    New-NodePilotPreflightResult -Id 'dotnet' -Title $title -Status 'Pass' -Required $true `
        -Detail '.NET 10 ASP.NET Core runtime found.'
}

function Get-NodePilotCertificateInventory {
    <#
      What is actually available in LocalMachine\My. The installer prints this when a
      thumbprint does not normalize; the wizard fills its certificate picker from it.

      Sorted by expiry, latest first, and sorted HERE rather than in either caller: a renewed
      certificate sits in the store beside the one it replaces, under the same subject, and the
      only thing separating them is that date. Newest-first puts the renewal at the top of the
      picker and sinks anything already expired to the bottom, where it belongs.
    #>
    Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Sort-Object -Property NotAfter -Descending |
        Select-Object Thumbprint, Subject, @{n = 'HasKey'; e = { $_.HasPrivateKey } }, NotAfter
}

function Get-NodePilotPortStatus {
    <#
      Whether Kestrel will be able to bind one port, and if not, why.

      Binds and releases immediately. That is a probe, not a change, so it stays safe behind the
      re-check button - see the rule at the top of this file.

      Bound to IPAddress.Any because that is what Kestrel does: the crash this check exists to
      predict came out of AnyIPListenOptions.BindAsync. Probing 127.0.0.1 instead would pass on a
      port that is reserved on the wildcard address.
    #>
    param(
        [Parameter(Mandatory)][int]$Port,
        [string]$ServiceName = 'NodePilot'
    )

    # An existing listener is the ordinary case when NodePilot is reinstalled over itself: the port
    # is held by the very service about to be replaced. Calling that a conflict would send the
    # operator hunting a problem they created by installing correctly the first time.
    $listener = $null
    try {
        $listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
            Select-Object -First 1
    } catch { }

    if ($listener) {
        $owner = $null
        try { $owner = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue } catch { }
        $service = $null
        try {
            $escaped = $ServiceName.Replace("'", "''")
            $service = Get-CimInstance Win32_Service -Filter "Name='$escaped'" -ErrorAction SilentlyContinue
        } catch { }

        if ($service -and $service.ProcessId -eq $listener.OwningProcess) {
            return [pscustomobject]@{
                Port = $Port; IsBlocked = $false
                Detail = "held by the $ServiceName service being replaced"
            }
        }
        $name = if ($owner) { "$($owner.Name) (PID $($listener.OwningProcess))" } else { "PID $($listener.OwningProcess)" }
        return [pscustomobject]@{ Port = $Port; IsBlocked = $true; Detail = "already in use by $name" }
    }

    try {
        $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Any, $Port)
        try { $probe.Start() } finally { $probe.Stop() }
        return [pscustomobject]@{ Port = $Port; IsBlocked = $false; Detail = 'free' }
    }
    catch {
        $socketError = $null
        $exception = $_.Exception
        while ($exception -and -not $socketError) {
            if ($exception -is [System.Net.Sockets.SocketException]) { $socketError = $exception.SocketErrorCode }
            $exception = $exception.InnerException
        }
        # 10013 is the one that matters here, and it does NOT mean "in use". Windows returns it for a
        # port held by an HTTP.SYS reservation or sitting inside an excluded range - IIS, WinRM and
        # WSUS all create those - and nothing appears in any listener list to explain it.
        if ($socketError -eq [System.Net.Sockets.SocketError]::AccessDenied) {
            return [pscustomobject]@{
                Port = $Port; IsBlocked = $true
                Detail = 'reserved by Windows (an HTTP.SYS reservation or an excluded port range), not held by a listener'
            }
        }
        return [pscustomobject]@{ Port = $Port; IsBlocked = $true; Detail = $_.Exception.Message }
    }
}

function Test-NodePilotListenPorts {
    <#
      The check that turns a three-minute silence into one red line. Without it, a port Kestrel
      cannot bind is discovered only after the installer has copied everything, registered the
      service, waited out a 180-second health probe and rolled the whole thing back - leaving
      "did not report /healthz/ready" on screen and the real reason in a log nobody opens.
    #>
    param(
        [Parameter(Mandatory)][int]$HttpsPort,
        [int]$HttpPort = 0,
        [string]$ServiceName = 'NodePilot'
    )

    $title = 'HTTP/HTTPS ports'
    $blocked = @()
    $fine = @()

    foreach ($candidate in @(
        [pscustomobject]@{ Label = 'HTTPS'; Port = $HttpsPort },
        [pscustomobject]@{ Label = 'HTTP';  Port = $HttpPort })) {

        # 0 is how the wizard says "no HTTP redirect". That is a configuration, not a problem.
        if ($candidate.Port -le 0) {
            $fine += "$($candidate.Label) disabled"
            continue
        }
        $status = Get-NodePilotPortStatus -Port $candidate.Port -ServiceName $ServiceName
        if ($status.IsBlocked) { $blocked += "$($candidate.Label) $($candidate.Port) $($status.Detail)" }
        else { $fine += "$($candidate.Label) $($candidate.Port) $($status.Detail)" }
    }

    if ($blocked.Count -eq 0) {
        return New-NodePilotPreflightResult -Id 'ports' -Title $title -Status 'Pass' -Required $true `
            -Detail ($fine -join ', ')
    }

    New-NodePilotPreflightResult -Id 'ports' -Title $title -Status 'Fail' -Required $true `
        -Detail ($blocked -join '; ') `
        -RemediationHint 'Pick a free port, or set the HTTP port to 0 to drop the redirect.' `
        -Remediation ("See what Windows has reserved:`r`n" +
                      "netsh interface ipv4 show excludedportrange protocol=tcp`r`n`r`n" +
                      "See who is listening:`r`n" +
                      "Get-NetTCPConnection -State Listen | Sort-Object LocalPort`r`n`r`n" +
                      'On a server running IIS - a ConfigMgr site server, for instance - ports 80 and 443 ' +
                      'belong to HTTP.SYS and Kestrel cannot bind them at all. Set the HTTP port to 0 to ' +
                      'drop the redirect, or move both ports somewhere free.') `
        -AbortMessage ('Kestrel cannot bind: ' + ($blocked -join '; ') +
                       '. The service would start and immediately fail with SocketException 10013 or 10048.')
}

function Test-NodePilotTlsCertificate {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Thumbprint)

    $title = 'Kestrel TLS certificate'
    $cert = Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $Thumbprint }

    if (-not $cert) {
        return New-NodePilotPreflightResult -Id 'certificate' -Title $title -Status 'Fail' -Required $true `
            -CanAutoFix $true -AutoFixLabel 'Generate a self-signed certificate (lab use only)' `
            -Detail "Certificate $Thumbprint is not present in Cert:\LocalMachine\My." `
            -RemediationHint 'Import the PFX into the machine store, then re-check.' `
            -Remediation 'Import-PfxCertificate -FilePath <file>.pfx -CertStoreLocation Cert:\LocalMachine\My -Password (Read-Host -AsSecureString)' `
            -AbortMessage "Cert $Thumbprint not found in Cert:\LocalMachine\My. Import the PFX (MachineKeySet|PersistKeySet) and retry."
    }
    if (-not $cert.HasPrivateKey) {
        return New-NodePilotPreflightResult -Id 'certificate' -Title $title -Status 'Fail' -Required $true `
            -Detail "Certificate $Thumbprint is present but carries no private key." `
            -RemediationHint 'Re-import the PFX so the private key is persisted for the machine.' `
            -Remediation 'Import-PfxCertificate -FilePath <file>.pfx -CertStoreLocation Cert:\LocalMachine\My -Exportable' `
            -AbortMessage "Cert $Thumbprint has no private key. Re-import with -KeyStorageFlags MachineKeySet|PersistKeySet|Exportable."
    }

    $expiryWarning = ''
    if ($cert.NotAfter -lt (Get-Date).AddDays(30)) {
        $expiryWarning = " Expires $($cert.NotAfter.ToString('yyyy-MM-dd'))."
    }
    New-NodePilotPreflightResult -Id 'certificate' -Title $title -Status 'Pass' -Required $true `
        -Detail "Cert found: $($cert.Subject)$expiryWarning"
}

function Test-NodePilotGmsa {
    <#
      Best-effort by design: the ActiveDirectory module may be absent (RSAT not installed) and
      that must not stop an install. A failure here is a warning, never an abort - which is why
      this check reports Warn rather than Fail.
    #>
    param([Parameter(Mandatory)][string]$ServiceAccount)

    $title = 'Group managed service account'
    $sam = $ServiceAccount
    if ($sam -like '*\*') { $sam = $sam.Split('\')[-1] }
    $sam = $sam.TrimEnd('$')

    try {
        Import-Module ActiveDirectory -ErrorAction Stop
        # Test-ADServiceAccount takes the short SAM name (without domain, without $).
        if (-not (Test-ADServiceAccount -Identity $sam)) {
            throw "Test-ADServiceAccount returned false for '$sam'. Run Install-ADServiceAccount -Identity $sam as Domain Admin."
        }
    } catch {
        return New-NodePilotPreflightResult -Id 'gmsa' -Title $title -Status 'Warn' `
            -Detail "gMSA check skipped: $($_.Exception.Message)" `
            -RemediationHint 'Install the RSAT-AD-PowerShell feature, or re-run with -SkipGmsaCheck once verified manually.' `
            -Remediation "Install-ADServiceAccount -Identity $sam"
    }

    New-NodePilotPreflightResult -Id 'gmsa' -Title $title -Status 'Pass' `
        -Detail "gMSA '$sam' is installed on this host."
}

function Test-NodePilotServiceIdentityRestorable {
    <#
      Mirrors the rule Get-ServiceRollbackSnapshot enforces in Install-NodePilot.ps1: an
      existing service can only be transactionally restored when it runs as LocalSystem or as
      a machine/managed account (name ending in '$'), because no other account's password is
      recoverable. Surfacing it here turns a mid-install throw into a red row before anyone
      commits to the install.
    #>
    param([Parameter(Mandatory)][string]$ServiceName)

    $title = 'Existing service can be rolled back'
    if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
        return New-NodePilotPreflightResult -Id 'serviceIdentity' -Title $title -Status 'Skipped' `
            -Detail "No existing service named '$ServiceName' - nothing to preserve."
    }

    $escapedName = $ServiceName.Replace("'", "''")
    try {
        $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$escapedName'" -ErrorAction Stop
    } catch {
        return New-NodePilotPreflightResult -Id 'serviceIdentity' -Title $title -Status 'Fail' -Required $true `
            -Detail "Could not read the configuration of the existing service '$ServiceName': $($_.Exception.Message)" `
            -RemediationHint 'The installer refuses to mutate a service it cannot snapshot. Investigate or remove it first.' `
            -Remediation "sc.exe qc $ServiceName"
    }
    if (-not $service) {
        return New-NodePilotPreflightResult -Id 'serviceIdentity' -Title $title -Status 'Fail' -Required $true `
            -Detail "Service '$ServiceName' exists but its configuration could not be read." `
            -RemediationHint 'The installer refuses to mutate a service it cannot snapshot.' `
            -Remediation "sc.exe qc $ServiceName"
    }

    $normalizedStartName = $service.StartName.Trim().ToLowerInvariant()
    $isRestorableSystemAccount = $normalizedStartName -in @(
        'localsystem', '.\localsystem', 'system', 'nt authority\system')
    if (-not $isRestorableSystemAccount -and -not $service.StartName.TrimEnd().EndsWith('$')) {
        return New-NodePilotPreflightResult -Id 'serviceIdentity' -Title $title -Status 'Fail' -Required $true `
            -Detail "Existing service '$ServiceName' runs as '$($service.StartName)'." `
            -RemediationHint 'Only LocalSystem and gMSA services can be transactionally restored, because other account passwords are not recoverable. Uninstall the existing service first.' `
            -Remediation ".\Uninstall-NodePilot.ps1 -ServiceName $ServiceName"
    }

    New-NodePilotPreflightResult -Id 'serviceIdentity' -Title $title -Status 'Pass' `
        -Detail "Existing service '$ServiceName' runs as '$($service.StartName)' and can be rolled back."
}

function Test-NodePilotDomainJoined {
    <#
      The installer's firewall rules target the Domain profile only. On a workgroup host they
      therefore apply to no active profile: the service runs, localhost works, and nothing on
      the network can reach it. Warn, never fail - a loopback-only install is legitimate.
    #>
    $title = 'Domain membership (firewall scope)'
    try {
        $partOfDomain = [bool](Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop).PartOfDomain
    } catch {
        return New-NodePilotPreflightResult -Id 'domainJoined' -Title $title -Status 'Warn' `
            -Detail "Could not determine domain membership: $($_.Exception.Message)"
    }
    if (-not $partOfDomain) {
        return New-NodePilotPreflightResult -Id 'domainJoined' -Title $title -Status 'Warn' `
            -Detail 'This host is not domain-joined; the Domain-profile firewall rules will apply to no active profile.' `
            -RemediationHint 'Open the HTTPS port for the active profile yourself after the install.' `
            -Remediation 'New-NetFirewallRule -DisplayName "NodePilot HTTPS" -Direction Inbound -Protocol TCP -LocalPort <port> -Action Allow -Profile Private'
    }
    New-NodePilotPreflightResult -Id 'domainJoined' -Title $title -Status 'Pass' `
        -Detail 'Host is domain-joined; the Domain-profile firewall rules will apply.'
}

function Test-NodePilotSqlReachable {
    <#
      Opens a connection using the INSTALLER's current Windows identity. The service will run
      as a different principal, so a green result proves the instance and database exist and
      are reachable over TLS - not that the runtime login works. Test-NodePilotSqlServiceLogin
      covers the LocalSystem half of that gap.
    #>
    param(
        [Parameter(Mandatory)][string]$Server,
        [Parameter(Mandatory)][string]$Database,
        [Parameter(Mandatory)][string]$CertificateHostName,
        [Parameter(Mandatory)][string]$Principal
    )

    $title = 'SQL Server reachable'
    $connectionString = Resolve-NodePilotSqlProbeConnectionString `
        -Server $Server -Database $Database -CertificateHostName $CertificateHostName

    $conn = New-Object System.Data.SqlClient.SqlConnection $connectionString
    try {
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = 'SELECT 1'
        [void]$cmd.ExecuteScalar()
    } catch {
        return New-NodePilotPreflightResult -Id 'database' -Title $title -Status 'Fail' -Required $true `
            -Detail "SQL reachability FAILED: $($_.Exception.Message)" `
            -RemediationHint 'The installer could not open a connection to the target DB using the current admin''s Windows identity. Have the DBA run, on the SQL Server:' `
            -Remediation (Get-NodePilotSqlRemediationScript -Principal $Principal -Database $Database) `
            -CanAutoFix $true -AutoFixLabel 'Create the login and database now (needs sysadmin)' `
            -AbortMessage 'Aborted: SQL pre-flight failed.'
    } finally {
        $conn.Dispose()
    }

    New-NodePilotPreflightResult -Id 'database' -Title $title -Status 'Pass' -Required $true `
        -Detail "SQL reachable: $Server/$Database"
}

function Test-NodePilotSqlServiceLogin {
    <#
      Not a probe - a standing caveat, emitted only for LocalSystem. The reachability check
      above authenticated as the installing admin; at runtime the service authenticates as the
      COMPUTER account. That login is a separate grant and its absence shows up as a 503 on
      /healthz/ready long after "Install complete".
    #>
    param(
        [Parameter(Mandatory)][string]$ComputerAccount,
        [Parameter(Mandatory)][string]$Database
    )
    New-NodePilotPreflightResult -Id 'databaseServiceLogin' -Title 'SQL login for the service identity' -Status 'Warn' `
        -Detail "NOTE: reachability was tested with your admin identity. At runtime the service connects as the computer account $ComputerAccount." `
        -RemediationHint "Ensure that login exists with db_owner on [$Database]:" `
        -Remediation (Get-NodePilotSqlRemediationScript -Principal $ComputerAccount -Database $Database)
}

function Test-NodePilotSqlTds8Support {
    <#
      The runtime connection pins Encrypt=Strict (TDS 8.0). Two hard floors follow:
      - TDS 8.0 exists only on SQL Server 2022+ (ProductMajorVersion 16).
      - SQL Server 2022 RTM ships a TDS 8.0 bug that corrupts RPC parameter streams
        (error 8005 "The parameter name is invalid") on the first parameterized statement.
        Plain-text batches (EF migrations) still work, so without this gate the failure
        surfaces only after install, as a service boot loop. Fixed server-side in
        CU1 = 16.0.4003.1 (dotnet/SqlClient#1807).
      This probe connects with Encrypt=$true (TDS 7.4 - System.Data.SqlClient cannot speak
      TDS 8.0), so the version query is the only way to prove the server can handle what the
      .NET 10 runtime will actually send.
    #>
    param(
        [Parameter(Mandatory)][string]$Server,
        [Parameter(Mandatory)][string]$CertificateHostName
    )

    $title = 'SQL Server supports TDS 8.0'
    $hint = 'Check the patch level in SSMS. 16.0.1000.x = 2022 RTM (unpatched). Install the latest SQL Server 2022 cumulative update, then re-check.'
    $snippet = "SELECT SERVERPROPERTY('ProductVersion') AS Version, SERVERPROPERTY('ProductUpdateLevel') AS CU;"

    # master, not the app DB: the version property needs no database and this keeps the gate
    # meaningful even when the app DB is created only after the preflight.
    $connectionString = Resolve-NodePilotSqlProbeConnectionString `
        -Server $Server -Database 'master' -CertificateHostName $CertificateHostName

    $conn = New-Object System.Data.SqlClient.SqlConnection $connectionString
    try {
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128))"
        $productVersion = [string]$cmd.ExecuteScalar()
    } catch {
        return New-NodePilotPreflightResult -Id 'databaseVersion' -Title $title -Status 'Fail' -Required $true `
            -Detail "SQL version pre-flight FAILED: $($_.Exception.Message)" `
            -RemediationHint $hint -Remediation $snippet `
            -AbortMessage 'Aborted: SQL version pre-flight failed.'
    } finally {
        $conn.Dispose()
    }

    $minVersion = [version]'16.0.4003.1'
    $parsed = $null
    if (-not [version]::TryParse($productVersion, [ref]$parsed)) {
        return New-NodePilotPreflightResult -Id 'databaseVersion' -Title $title -Status 'Fail' -Required $true `
            -Detail "SQL version pre-flight FAILED: could not parse SQL Server ProductVersion '$productVersion'." `
            -RemediationHint $hint -Remediation $snippet `
            -AbortMessage 'Aborted: SQL version pre-flight failed.'
    }
    if ($parsed -lt $minVersion) {
        return New-NodePilotPreflightResult -Id 'databaseVersion' -Title $title -Status 'Fail' -Required $true `
            -Detail ("SQL version pre-flight FAILED: SQL Server $productVersion cannot serve NodePilot's " +
                     "Encrypt=Strict (TDS 8.0) connections. Minimum: SQL Server 2022 CU1 ($minVersion) - " +
                     "SQL Server 2019 and older lack TDS 8.0 entirely, and 2022 RTM corrupts TDS 8.0 RPC " +
                     "parameter streams (error 8005).") `
            -RemediationHint $hint -Remediation $snippet `
            -AbortMessage 'Aborted: SQL version pre-flight failed.'
    }

    New-NodePilotPreflightResult -Id 'databaseVersion' -Title $title -Status 'Pass' -Required $true `
        -Detail "SQL Server $productVersion supports TDS 8.0 (>= 2022 CU1)."
}

function Test-NodePilotPostgresReachable {
    <#
      TCP port probe against the Postgres endpoint. We do not attempt a full auth + query
      because Npgsql is not shipped with the installer and pulling it in would bloat the
      bootstrap. A "cannot even connect" is the common failure mode we want to catch before
      starting the service; role/password errors surface in the health-probe step.

      Param is named HostName (not Host) because $Host is a reserved PowerShell automatic
      variable (PSAvoidAssignmentToAutomaticVariable).
    #>
    param(
        [Parameter(Mandatory)][string]$HostName,
        [Parameter(Mandatory)][int]$Port,
        [Parameter(Mandatory)][string]$User,
        [Parameter(Mandatory)][string]$Database
    )

    $title = 'PostgreSQL reachable'
    $reachable = $false
    $detail = ''
    try {
        $tnc = Test-NetConnection -ComputerName $HostName -Port $Port -WarningAction SilentlyContinue
        $reachable = [bool]$tnc.TcpTestSucceeded
    } catch {
        $detail = $_.Exception.Message
    }

    if (-not $reachable) {
        $suffix = if ($detail) { ": $detail" } else { '. Check DNS, firewall, and pg_hba.conf.' }
        return New-NodePilotPreflightResult -Id 'database' -Title $title -Status 'Fail' -Required $true `
            -Detail "Postgres reachability FAILED: TCP probe failed to ${HostName}:${Port}$suffix" `
            -RemediationHint "Cannot reach ${HostName}:${Port} from this host. Verify DNS, firewall, and that Postgres is listening on the external interface. Role setup on the DB server:" `
            -Remediation (Get-NodePilotPostgresRemediationScript -User $User -Database $Database) `
            -AbortMessage 'Aborted: Postgres pre-flight failed.'
    }

    New-NodePilotPreflightResult -Id 'database' -Title $title -Status 'Pass' -Required $true `
        -Detail "Postgres TCP reachable: ${HostName}:${Port}"
}

# ---------------------------------------------------------------------------
# Orchestration
# ---------------------------------------------------------------------------

function Invoke-NodePilotPreflight {
    <#
      Runs the checks applicable to one configuration and returns them in report order.
      Returns results; never throws for a failed check. Assert-NodePilotPreflight decides
      what a failure means.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$CertificateThumbprint,
        [Parameter(Mandatory)][ValidateSet('sqlserver', 'postgres')][string]$DbProvider,
        [Parameter(Mandatory)][bool]$IsLocalSystem,
        [int]$HttpsPort = 443,
        [int]$HttpPort = 0,
        [string]$ServiceAccount,
        [string]$ComputerAccount,
        [string]$SqlPrincipal,
        [string]$SqlServer,
        [string]$SqlDatabase,
        [string]$SqlCertificateHostName,
        [string]$PostgresHost,
        [int]$PostgresPort = 5432,
        [string]$PostgresUser,
        [string]$PostgresDatabase,
        [string]$ServiceName = 'NodePilot',
        [switch]$SkipDatabaseCheck,
        [switch]$SkipGmsaCheck
    )

    $results = @()
    $results += Test-NodePilotDotNetRuntime
    $results += Test-NodePilotTlsCertificate -Thumbprint $CertificateThumbprint
    $results += Test-NodePilotListenPorts -HttpsPort $HttpsPort -HttpPort $HttpPort -ServiceName $ServiceName

    if ($IsLocalSystem) {
        $results += New-NodePilotPreflightResult -Id 'gmsa' -Title 'Service identity' -Status 'Skipped' `
            -Detail "Service identity: LocalSystem - network identity is the computer account $ComputerAccount."
    } elseif ($SkipGmsaCheck) {
        $results += New-NodePilotPreflightResult -Id 'gmsa' -Title 'Group managed service account' -Status 'Skipped' `
            -Detail 'gMSA check skipped by -SkipGmsaCheck.'
    } else {
        $results += Test-NodePilotGmsa -ServiceAccount $ServiceAccount
    }

    $results += Test-NodePilotServiceIdentityRestorable -ServiceName $ServiceName
    $results += Test-NodePilotDomainJoined

    if ($SkipDatabaseCheck) {
        $results += New-NodePilotPreflightResult -Id 'database' -Title 'Database reachable' -Status 'Skipped' `
            -Detail 'Database connectivity check skipped by -SkipSqlConnectivityCheck.'
        return $results
    }

    if ($DbProvider -eq 'sqlserver') {
        $sqlResult = Test-NodePilotSqlReachable `
            -Server $SqlServer -Database $SqlDatabase `
            -CertificateHostName $SqlCertificateHostName -Principal $SqlPrincipal
        $results += $sqlResult
        # Only meaningful once the instance answered; on a failed connection the caller aborts
        # before it could act on either follow-up.
        if ($sqlResult.Status -eq 'Pass') {
            if ($IsLocalSystem) {
                $results += Test-NodePilotSqlServiceLogin -ComputerAccount $ComputerAccount -Database $SqlDatabase
            }
            $results += Test-NodePilotSqlTds8Support `
                -Server $SqlServer -CertificateHostName $SqlCertificateHostName
        }
    } else {
        $results += Test-NodePilotPostgresReachable `
            -HostName $PostgresHost -Port $PostgresPort `
            -User $PostgresUser -Database $PostgresDatabase
    }

    return $results
}

function Assert-NodePilotPreflight {
    <#
      Prints the collected results the way the installer always has, then aborts on the first
      required failure. Non-required failures and warnings are reported and survived.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Results,
        [string]$Prefix = 'install'
    )

    foreach ($result in $Results) {
        switch ($result.Status) {
            'Pass' { Write-Host "[$Prefix]   $($result.Detail)" -ForegroundColor Gray }
            'Skipped' { Write-Host "[$Prefix]   $($result.Detail)" -ForegroundColor Gray }
            'Warn' {
                Write-Host "[$Prefix]   $($result.Detail)" -ForegroundColor Yellow
                if ($result.RemediationHint) {
                    Write-Host "[$Prefix]   $($result.RemediationHint)" -ForegroundColor Yellow
                }
                foreach ($line in ($result.Remediation -split "`r?`n")) {
                    if ($line) { Write-Host "[$Prefix]     $line" -ForegroundColor Yellow }
                }
            }
            'Fail' {
                Write-Host "[$Prefix]   $($result.Detail)" -ForegroundColor Yellow
                if ($result.RemediationHint -or $result.Remediation) {
                    Write-Host ""
                    if ($result.RemediationHint) {
                        Write-Host "  $($result.RemediationHint)" -ForegroundColor Yellow
                        Write-Host ""
                    }
                    foreach ($line in ($result.Remediation -split "`r?`n")) {
                        if ($line) { Write-Host "    $line" -ForegroundColor Gray }
                    }
                    Write-Host ""
                }
                if ($result.Required) {
                    $message = if ($result.AbortMessage) { $result.AbortMessage } else { $result.Detail }
                    throw $message
                }
            }
        }
    }
}
