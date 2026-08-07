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
        [string]$AutoFixLabel = '',
        # Whether the wizard should arrive with this fix already ticked. Reserved for work that is
        # part of installing rather than a decision about someone else's server: granting the
        # service identity access to a database that already exists is the former, CREATE DATABASE
        # on a production instance is the latter. The box stays visible either way, so a default of
        # $true is "one fewer click", never "done behind your back".
        [bool]$AutoFixDefault = $false
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
        AutoFixDefault  = $AutoFixDefault
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
        [Parameter(Mandatory)][string]$Database,
        # Dropped when the caller has already proven the database is there. Handing a DBA a
        # CREATE DATABASE for a database they can see invites them to read the rest of the script
        # as equally wrong.
        [switch]$SkipCreateDatabase
    )
    $lines = @("CREATE LOGIN [$Principal] FROM WINDOWS;")
    if (-not $SkipCreateDatabase) { $lines += "CREATE DATABASE [$Database];" }
    $lines += @(
        "USE [$Database];"
        "CREATE USER [$Principal] FOR LOGIN [$Principal];"
        "ALTER ROLE db_owner ADD MEMBER [$Principal];"
    )
    $lines -join [Environment]::NewLine
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

function Get-NodePilotPeArchitecture {
    <#
      The machine type out of a PE file's COFF header: 'x64', 'x86', 'arm64', or $null when the
      file cannot be read or is not a PE image at all.

      Deliberately not 'dotnet --info', which also reports the host architecture: its labels are
      localised, so on a German server it prints "Architektur:" and any parse of the English text
      quietly finds nothing. 'dotnet --list-runtimes' is not localised, which is why that one stays
      for the version question - but it says nothing about architecture, so this reads the bytes.

      Opened with FileShare ReadWrite: dotnet.exe may be running while we look at it.
    #>
    param([Parameter(Mandatory)][string]$Path)

    try {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    }
    catch { return $null }

    try {
        # A file too short, or a header offset pointing past the end, runs into the reader and is
        # answered by the catch below. No separate length guard: it would be a branch no test could
        # tell apart from the catch, which is how dead code gets in.
        $reader = New-Object IO.BinaryReader($stream)
        $stream.Position = 0x3C
        $stream.Position = $reader.ReadInt32()
        # This one is not redundant. Without it, anything at all would be read as a machine type and
        # some junk file would classify as a perfectly good x64 host.
        if ($reader.ReadUInt32() -ne 0x00004550) { return $null }   # 'PE\0\0'
        switch ($reader.ReadUInt16()) {
            0x8664 { return 'x64' }
            0x014C { return 'x86' }
            0xAA64 { return 'arm64' }
            default { return 'unknown' }
        }
    }
    catch { return $null }
    finally { $stream.Dispose() }
}

function Get-NodePilotDotNetHostCandidates {
    <#
      Every dotnet.exe this machine might offer, most-likely first.

      PATH alone is not enough on two counts. A clean Windows Server has no dotnet on PATH at all,
      and - the case that actually bites - a process that installed the runtime itself still carries
      the PATH it was started with, so PATH stays stale until it restarts. Hence the well-known
      machine-wide locations as a fallback.

      All PATH hits, not the first: a machine with both runtimes installed can easily have the x86
      one earlier in PATH, and taking only the first hit would answer for a dotnet that is not the
      one NodePilot's service will use.

      The registry (HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\InstallLocation) is what the
      apphost itself consults and is deliberately not read here: over PATH plus ProgramW6432 it only
      adds installations in unusual places, which PATH already covers, and a registry view redirected
      under WOW64 would be a new way to be wrong.
    #>
    $candidates = New-Object System.Collections.Generic.List[string]
    foreach ($command in @(Get-Command dotnet -CommandType Application -All -ErrorAction SilentlyContinue)) {
        $candidates.Add($command.Source)
    }
    foreach ($root in @("$env:ProgramW6432", $env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        $candidates.Add((Join-Path $root 'dotnet\dotnet.exe'))
    }

    $seen = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)
    $result = @()
    foreach ($candidate in $candidates) {
        if (-not $seen.Add($candidate)) { continue }
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $result += $candidate }
    }
    $result
}

function Get-NodePilotDotNetHostState {
    <#
      What is actually installed, as data: the 64-bit host (if any) with the runtimes it reports,
      plus one host of a different architecture to name in the message. The verdict is a separate,
      side-effect-free function so it can be exercised without installing runtimes.
    #>
    $x64Path = $null
    $otherPath = $null
    $otherArchitecture = ''

    foreach ($candidate in @(Get-NodePilotDotNetHostCandidates)) {
        $architecture = Get-NodePilotPeArchitecture -Path $candidate
        if ($architecture -eq 'x64') { $x64Path = $candidate; break }
        # Only architectures worth naming. An unreadable or exotic image says nothing the operator
        # could act on, so it falls through to the plain "nothing found" verdict.
        if (-not $otherPath -and @('x86', 'arm64') -contains $architecture) {
            $otherPath = $candidate
            $otherArchitecture = $architecture
        }
    }

    $runtimes = @()
    if ($x64Path) {
        try { $runtimes = @(& $x64Path --list-runtimes 2>$null) } catch { $runtimes = @() }
    }

    [pscustomobject]@{
        X64Path           = $x64Path
        Runtimes          = $runtimes
        OtherPath         = $otherPath
        OtherArchitecture = $otherArchitecture
    }
}

function Test-NodePilotDotNetRuntime {
    <#
      NodePilot publishes with --runtime win-x64 and installs the NodePilot.Api.exe apphost, which a
      32-bit runtime cannot host. A check that accepts any architecture goes green on a machine where
      the service then refuses to start - which is exactly what happened in the field.
    #>
    param([object]$State = (Get-NodePilotDotNetHostState))

    $title = 'ASP.NET Core 10 runtime'
    $hint = 'Install the ASP.NET Core 10 runtime (x64) - the plain runtime, not the Hosting Bundle, which also wires up IIS.'
    $link = 'https://dotnet.microsoft.com/download/dotnet/10.0'
    $fixLabel = 'Install the bundled ASP.NET Core 10 runtime now'

    if (-not $State.X64Path) {
        # Two different stories, and telling them apart is the point: "nothing found" sends the
        # operator looking for an installer, while "found, but 32-bit" tells them why the dotnet they
        # can plainly see on PATH does not count.
        if ($State.OtherPath) {
            $found = switch ($State.OtherArchitecture) {
                'x86' { '32-bit (x86)' }
                'arm64' { 'ARM64' }
                default { $State.OtherArchitecture }
            }
            return New-NodePilotPreflightResult -Id 'dotnet' -Title $title -Status 'Fail' -Required $true `
                -CanAutoFix $true -AutoFixLabel $fixLabel `
                -Detail ("Only a $found .NET host was found ($($State.OtherPath)). " +
                         'NodePilot is a 64-bit application and needs the x64 runtime.') `
                -RemediationHint $hint -Remediation $link `
                -AbortMessage ("Only a $found .NET host was found ($($State.OtherPath)). " +
                               "Install the 64-bit ASP.NET Core 10 runtime from $link.")
        }
        return New-NodePilotPreflightResult -Id 'dotnet' -Title $title -Status 'Fail' -Required $true `
            -CanAutoFix $true -AutoFixLabel $fixLabel `
            -Detail 'dotnet was not found on PATH or under Program Files.' `
            -RemediationHint $hint -Remediation $link `
            -AbortMessage ".NET Runtime not found on PATH. Install the ASP.NET Core 10 runtime from $link."
    }

    if (-not (@($State.Runtimes) -match '^Microsoft\.AspNetCore\.App 10\.')) {
        return New-NodePilotPreflightResult -Id 'dotnet' -Title $title -Status 'Fail' -Required $true `
            -CanAutoFix $true -AutoFixLabel $fixLabel `
            -Detail "No Microsoft.AspNetCore.App 10.x runtime reported by '$($State.X64Path) --list-runtimes'." `
            -RemediationHint $hint -Remediation $link `
            -AbortMessage ".NET 10 ASP.NET Core Runtime not found. Install the ASP.NET Core 10 runtime ($link)."
    }

    New-NodePilotPreflightResult -Id 'dotnet' -Title $title -Status 'Pass' -Required $true `
        -Detail ".NET 10 ASP.NET Core runtime found (x64, $($State.X64Path))."
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
        # PID 4 is the System process, and that is what an HTTP.SYS reservation looks like from
        # here. Reporting "in use by System (PID 4)" is true and useless: it sends the operator
        # after a process that cannot be stopped or moved. Measured on the lab host, where IIS
        # reserves 80 and 443 exactly this way - the kernel driver holds the listener, so this
        # branch is reached instead of the AccessDenied one below.
        if ($listener.OwningProcess -le 4) {
            return [pscustomobject]@{
                Port = $Port; IsBlocked = $true
                Detail = 'reserved by Windows HTTP.SYS (IIS, WinRM or WSUS) - no ordinary process holds it'
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

function Test-NodePilotCertificateNameMatch {
    <#
      Whether a certificate presents the name operators are going to type. Split from the store
      lookup so every branch is reachable from a test host, the same reason
      New-NodePilotSqlServiceLoginResult is separate from its connection.

      Callers pass DnsNameList rather than the raw SAN extension on purpose: X509Extension.Format()
      renders "DNS Name=" in the machine's UI language, so a parser built against it works on an
      English host and silently finds nothing on a German one. PowerShell's certificate provider
      hands over the decoded list instead.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Names,
        [Parameter(Mandatory)][AllowEmptyString()][string]$PublicHostname
    )

    # Nothing to compare against is not a mismatch. The console path can be called without a
    # public hostname, and inventing a complaint there would be noise.
    if ([string]::IsNullOrWhiteSpace($PublicHostname)) { return $true }

    foreach ($name in $Names) {
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        if ($name -eq $PublicHostname) { return $true }
        # A wildcard covers exactly one label (RFC 6125): *.corp.example matches np.corp.example
        # but neither corp.example itself nor a.np.corp.example.
        if ($name.StartsWith('*.')) {
            $suffix = $name.Substring(1)
            if ($PublicHostname.Length -gt $suffix.Length -and
                $PublicHostname.EndsWith($suffix, [StringComparison]::OrdinalIgnoreCase)) {
                $label = $PublicHostname.Substring(0, $PublicHostname.Length - $suffix.Length)
                if (-not $label.Contains('.')) { return $true }
            }
        }
    }
    return $false
}

function Get-NodePilotCertificateNames {
    <#
      Every name a certificate claims, SAN first. The CN fallback is for certificates old enough
      to carry no SAN at all - browsers stopped honouring those years ago, but they still turn up
      in internal PKIs, and without it the check would report a mismatch that is really "no SAN".
    #>
    param([Parameter(Mandatory)]$Certificate)

    $names = @()
    if ($Certificate.PSObject.Properties.Name -contains 'DnsNameList') {
        $names = @($Certificate.DnsNameList | ForEach-Object { [string]$_.Unicode } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }
    if ($names.Count -eq 0) {
        $commonName = [regex]::Match([string]$Certificate.Subject, 'CN=([^,]+)').Groups[1].Value.Trim()
        if ($commonName) { $names = @($commonName) }
    }
    return $names
}

function Test-NodePilotTlsCertificate {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Thumbprint,
        [AllowEmptyString()][string]$PublicHostname = ''
    )

    $title = 'Kestrel TLS certificate'

    # "I have none yet" is a different answer from "the one I named is missing", and it is the
    # answer a fresh host gives. Reported before the store is read, so this branch also states the
    # verdict on a machine where the certificate store cannot be enumerated at all - and so the
    # message stops reading "Certificate  is not present", with the empty thumbprint rendered as
    # the gap it is.
    if ([string]::IsNullOrWhiteSpace($Thumbprint)) {
        return New-NodePilotPreflightResult -Id 'certificate' -Title $title -Status 'Fail' -Required $true `
            -CanAutoFix $true -AutoFixLabel 'Generate a self-signed certificate (lab use only)' `
            -Detail 'No certificate selected. Kestrel terminates TLS itself and will not start without one.' `
            -RemediationHint 'Pick one on the previous page, or tick the box to have a self-signed certificate created.' `
            -Remediation 'Import-PfxCertificate -FilePath <file>.pfx -CertStoreLocation Cert:\LocalMachine\My -Password (Read-Host -AsSecureString)' `
            -AbortMessage 'No certificate thumbprint was given. Kestrel cannot serve HTTPS without one.'
    }

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

    New-NodePilotCertificateVerdict -Certificate $cert -Thumbprint $Thumbprint `
        -PublicHostname $PublicHostname -Now (Get-Date)
}

function Test-NodePilotArtifactSignerTrust {
    <#
      The publisher of the artifact the setup carries.

      Install-NodePilot.ps1 no longer requires the publisher to be trusted on the target: it
      verifies the signature and compares the signer against a pinned thumbprint, so an untrusted
      publisher is a note, not a blocker. What it DOES still reject is a certificate that is
      expired, not yet valid, or not permitted to sign code - and those must not be reported here
      as an optional yellow line, or this row would promise an installation that then fails.

      So the order matters: everything the installer will reject is decided first and blocks; the
      chain is asked last, and only a failure that is exclusively about trust is optional.

      The certificate purpose is checked here as well as in ArtifactSecurity.ps1 rather than shared:
      this file is dot-sourced on its own by both the installer and the setup adapter, and pulling
      the security layer in behind it would be a heavier coupling than two small checks. A contract
      test keeps the pair from drifting.

      Revocation is deliberately not checked: a self-signed publisher has no CRL distribution point,
      so an online check fails for the absence of a CRL rather than the absence of trust, and this
      row would send the operator after a problem that is not theirs.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$CertificatePath,
        [Parameter(Mandatory)][AllowEmptyString()][string]$ExpectedThumbprint
    )

    $title = 'Artifact publisher'
    $brokenSetupHint = 'The setup is incomplete or has been altered. Download it again from the release.'

    # Not required any more: the installation verifies the certificate that travels inside the
    # signature, not this file. Missing, it only costs the convenience of the import offer.
    if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) {
        return New-NodePilotPreflightResult -Id 'signer' -Title $title -Status 'Warn' `
            -Detail "The publisher certificate is missing from the setup payload ($CertificatePath)." `
            -RemediationHint $brokenSetupHint
    }

    try { $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificatePath) }
    catch {
        return New-NodePilotPreflightResult -Id 'signer' -Title $title -Status 'Warn' `
            -Detail "The publisher certificate in the setup payload cannot be read: $($_.Exception.Message)" `
            -RemediationHint $brokenSetupHint
    }

    $actual = (($certificate.Thumbprint -replace '[^0-9A-Fa-f]', '')).ToUpperInvariant()
    $expected = (($ExpectedThumbprint -replace '[^0-9A-Fa-f]', '')).ToUpperInvariant()
    if ($expected -and $actual -ne $expected) {
        # No auto-fix here, on purpose. The box would import a certificate that is NOT the publisher
        # this setup was built against and make it trusted for the entire machine - the one outcome
        # worse than refusing to install.
        return New-NodePilotPreflightResult -Id 'signer' -Title $title -Status 'Fail' -Required $true `
            -Detail ("The certificate in the payload ($actual) is not the publisher this setup was built " +
                     "against ($expected). Nothing will be trusted and nothing will be installed.") `
            -RemediationHint $brokenSetupHint `
            -AbortMessage "Payload publisher $actual does not match the expected $expected."
    }

    # --- the three the installer will reject on, so they block here too ------------------------
    $now = Get-Date
    if ($certificate.NotAfter -lt $now -or $certificate.NotBefore -gt $now) {
        $when = if ($certificate.NotAfter -lt $now) {
            "expired on $($certificate.NotAfter.ToString('yyyy-MM-dd'))"
        } else {
            "is not valid until $($certificate.NotBefore.ToString('yyyy-MM-dd'))"
        }
        return New-NodePilotPreflightResult -Id 'signer' -Title $title -Status 'Fail' -Required $true `
            -Detail "The publisher certificate ($actual) $when, so the artifact signature will be rejected." `
            -RemediationHint 'A release signed with an out-of-date certificate has to be re-signed and re-published.' `
            -AbortMessage "Artifact publisher $actual $when."
    }

    $codeSigningOid = '1.3.6.1.5.5.7.3.3'
    $ekus = @($certificate.Extensions | Where-Object {
        $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]
    })
    if ($ekus.Count -eq 0 -or -not ($ekus.EnhancedKeyUsages.Value -contains $codeSigningOid)) {
        return New-NodePilotPreflightResult -Id 'signer' -Title $title -Status 'Fail' -Required $true `
            -Detail "The publisher certificate ($actual) is not valid for code signing." `
            -RemediationHint $brokenSetupHint `
            -AbortMessage "Artifact publisher $actual is not valid for code signing."
    }

    # Absent means unrestricted in X.509, so only a present extension can reject anything.
    $signingUsages = [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor
                     [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::NonRepudiation
    foreach ($keyUsage in @($certificate.Extensions | Where-Object {
        $_ -is [Security.Cryptography.X509Certificates.X509KeyUsageExtension]
    })) {
        if (($keyUsage.KeyUsages -band $signingUsages) -eq 0) {
            return New-NodePilotPreflightResult -Id 'signer' -Title $title -Status 'Fail' -Required $true `
                -Detail ("The publisher certificate ($actual) carries a KeyUsage that permits neither " +
                         "DigitalSignature nor NonRepudiation, so it may not sign code.") `
                -RemediationHint $brokenSetupHint `
                -AbortMessage "Artifact publisher $actual may not sign code (KeyUsage: $($keyUsage.KeyUsages))."
        }
    }

    # --- and only now the question that is genuinely optional ----------------------------------
    $trustOnly = $false
    $reasons = ''
    $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
    try {
        $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        if ($chain.Build($certificate)) {
            return New-NodePilotPreflightResult -Id 'signer' -Title $title -Status 'Pass' `
                -Detail "Publisher $($certificate.Subject) is trusted on this machine ($actual)."
        }
        $flags = @($chain.ChainStatus | ForEach-Object { $_.Status })
        $reasons = @($chain.ChainStatus | ForEach-Object { $_.StatusInformation.Trim() }) -join ' '
        # Exclusively about the missing trust anchor. Anything else the chain objects to is not
        # something an import fixes, and is reported as a failure rather than waved through.
        $trustOnly = $flags.Count -gt 0 -and -not (@($flags | Where-Object {
            $_ -ne [Security.Cryptography.X509Certificates.X509ChainStatusFlags]::UntrustedRoot -and
            $_ -ne [Security.Cryptography.X509Certificates.X509ChainStatusFlags]::PartialChain
        }).Count)
    }
    finally { $chain.Dispose() }

    if (-not $trustOnly) {
        return New-NodePilotPreflightResult -Id 'signer' -Title $title -Status 'Fail' -Required $true `
            -Detail "The publisher certificate ($actual) is not usable: $reasons" `
            -RemediationHint $brokenSetupHint `
            -AbortMessage "Artifact publisher $actual failed certificate validation: $reasons"
    }

    # Offered, never pre-ticked, and never required: the installation verifies the signature against
    # the pinned thumbprint and does not care whether this machine trusts the publisher. What the
    # import adds is that Windows itself will validate the installers' own Authenticode signature
    # from then on - it does not authenticate the setup that is already running.
    New-NodePilotPreflightResult -Id 'signer' -Title $title -Status 'Warn' `
        -CanAutoFix $true -AutoFixLabel 'Trust the publisher certificate (adds it to LocalMachine\Root)' `
        -Detail ("Publisher $($certificate.Subject) ($actual) is not trusted on this machine. " +
                 'The installation does not need it.') `
        -RemediationHint ('Optional. Trusting it makes Windows validate the signature of this and future ' +
                          'NodePilot installers; it applies to the whole machine, so compare the thumbprint ' +
                          'against the one published with the release first.') `
        -Remediation "Import-Certificate -FilePath '$CertificatePath' -CertStoreLocation Cert:\LocalMachine\Root"
}

function New-NodePilotCertificateVerdict {
    <#
      Everything that can be decided about a certificate once it has been found in the store,
      separated from finding it. -Now is a parameter for the same reason: "expired" and "not yet
      valid" are the two branches that matter here and neither is reachable from a test host that
      may not install certificates.
    #>
    param(
        [Parameter(Mandatory)]$Certificate,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Thumbprint,
        [Parameter(Mandatory)][AllowEmptyString()][string]$PublicHostname,
        [Parameter(Mandatory)][datetime]$Now
    )

    $title = 'Kestrel TLS certificate'
    $importHint = 'Import a current certificate into Cert:\LocalMachine\My (MachineKeySet|PersistKeySet), then re-check.'
    $importCommand = 'Import-PfxCertificate -FilePath <file>.pfx -CertStoreLocation Cert:\LocalMachine\My -Password (Read-Host -AsSecureString)'

    # Validity is a hard stop, not a note in the margin. The expiry used to be rendered into the
    # green line as text with nothing acting on it, so an expired certificate installed cleanly
    # and surfaced as a browser warning to the first user - after the rollout, on someone else's
    # screen. Deliberately NOT auto-fixable: offering the self-signed generator here would answer
    # "your PKI certificate expired" with "here, have a lab certificate instead".
    if ($Certificate.NotAfter -lt $Now) {
        return New-NodePilotPreflightResult -Id 'certificate' -Title $title -Status 'Fail' -Required $true `
            -Detail ("Certificate $($Certificate.Subject) expired on $($Certificate.NotAfter.ToString('yyyy-MM-dd')). " +
                     'Kestrel will serve it and every client will refuse it.') `
            -RemediationHint $importHint -Remediation $importCommand `
            -AbortMessage "Cert $Thumbprint expired on $($Certificate.NotAfter.ToString('yyyy-MM-dd'))."
    }
    if ($Certificate.NotBefore -gt $Now) {
        return New-NodePilotPreflightResult -Id 'certificate' -Title $title -Status 'Fail' -Required $true `
            -Detail ("Certificate $($Certificate.Subject) is not valid until $($Certificate.NotBefore.ToString('yyyy-MM-dd')). " +
                     'Clients will refuse it until then.') `
            -RemediationHint $importHint -Remediation $importCommand `
            -AbortMessage "Cert $Thumbprint is not valid until $($Certificate.NotBefore.ToString('yyyy-MM-dd'))."
    }

    $expiryWarning = ''
    if ($Certificate.NotAfter -lt $Now.AddDays(30)) {
        $expiryWarning = " Expires $($Certificate.NotAfter.ToString('yyyy-MM-dd'))."
    }

    # A warning, never a stop. A certificate whose SAN does not name this host is wrong far more
    # often than it is deliberate - but behind a reverse proxy, or on a host reached under an
    # alias, it is exactly right, and refusing the install would be refusing a valid setup.
    $names = @(Get-NodePilotCertificateNames -Certificate $Certificate)
    if (-not (Test-NodePilotCertificateNameMatch -Names $names -PublicHostname $PublicHostname)) {
        $claimed = if ($names.Count -gt 0) { $names -join ', ' } else { '(no host name at all)' }
        return New-NodePilotPreflightResult -Id 'certificate' -Title $title -Status 'Warn' -Required $true `
            -Detail ("Cert found: $($Certificate.Subject)$expiryWarning It is issued for " +
                     "$claimed - not for $PublicHostname.") `
            -RemediationHint ('Browsers will show a name mismatch unless something in front of NodePilot ' +
                              "terminates TLS under that name. Either use a certificate naming " +
                              "$PublicHostname, or set the public host name to one the certificate covers.") `
            -Remediation $importCommand
    }

    New-NodePilotPreflightResult -Id 'certificate' -Title $title -Status 'Pass' -Required $true `
        -Detail "Cert found: $($Certificate.Subject)$expiryWarning"
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

function New-NodePilotSqlServiceLoginResult {
    <#
      The verdict, separated from the connection that produces it. Split out so every branch is
      reachable from a test host with no SQL Server on it - which is every test host we have.
    #>
    param(
        [Parameter(Mandatory)][string]$Principal,
        [Parameter(Mandatory)][string]$Database,
        [Parameter(Mandatory)][bool]$LoginExists,
        [Parameter(Mandatory)][AllowEmptyString()][string]$UserName,
        [Parameter(Mandatory)][bool]$IsDbOwner,
        [Parameter(Mandatory)][bool]$IsSysadmin
    )

    $title = 'SQL login for the service identity'

    if ($IsSysadmin) {
        return New-NodePilotPreflightResult -Id 'databaseServiceLogin' -Title $title -Status 'Pass' `
            -Detail "$Principal is sysadmin on this instance and needs no grant on [$Database]."
    }
    if ($LoginExists -and $UserName -and $IsDbOwner) {
        # dbo lands here too: a service identity that owns the database is a member of db_owner.
        return New-NodePilotPreflightResult -Id 'databaseServiceLogin' -Title $title -Status 'Pass' `
            -Detail "$Principal has a login and is db_owner on [$Database] (as [$UserName])."
    }

    $missing = if (-not $LoginExists) {
        "$Principal has no SQL login on this instance."
    }
    elseif (-not $UserName) {
        "$Principal has a login but no user in [$Database]."
    }
    else {
        "$Principal maps to [$UserName] in [$Database] but is not a member of db_owner."
    }

    # Not Required. The install works - it is the first request AFTER it that fails - and the
    # console path has always let this through with the statements printed. Failing here instead
    # would turn a repairable gap into a refused install on hosts where a DBA is standing by.
    New-NodePilotPreflightResult -Id 'databaseServiceLogin' -Title $title -Status 'Fail' `
        -Detail "$missing Without it the service starts and /healthz/ready answers 503." `
        -RemediationHint "The service will connect as $Principal. On the SQL Server:" `
        -Remediation (Get-NodePilotSqlRemediationScript -Principal $Principal -Database $Database -SkipCreateDatabase) `
        -CanAutoFix $true -AutoFixDefault $true `
        -AutoFixLabel "Create that login and grant it db_owner on [$Database] now"
}

function Test-NodePilotSqlServiceLogin {
    <#
      Whether the SERVICE identity can use the database - which is not what the reachability
      check above established. That one authenticated as the installing admin; at runtime the
      service authenticates as its own principal (the computer account under LocalSystem, the
      gMSA otherwise), and that grant is separate. Its absence shows up as a 503 on
      /healthz/ready long after "Install complete", which is the worst possible time to learn it.

      This used to be a standing caveat printed unconditionally - correct advice, no information,
      and shown just as loudly on the hosts where the grant was already in place. It is a query
      now, and a red line here is one the wizard can act on.

      Read-only by construction: three lookups and no DDL. The fix lives in
      Provision-NodePilotDatabase.ps1, on the other side of the rule at the top of this file.

      Runs on the connection of the installing admin, so what it can see is bounded by what that
      account may see. A sysadmin - the account that can act on the answer anyway - sees
      everything; a lesser account can be told "no login" about one that exists, and then the fix
      it is offered declines on its own permission gate and prints the DDL. That is the same
      place the old caveat left everyone, so the degradation costs nothing.
    #>
    param(
        [Parameter(Mandatory)][string]$Principal,
        [Parameter(Mandatory)][string]$Server,
        [Parameter(Mandatory)][string]$Database,
        [Parameter(Mandatory)][string]$CertificateHostName
    )

    $title = 'SQL login for the service identity'
    $remediation = Get-NodePilotSqlRemediationScript -Principal $Principal -Database $Database -SkipCreateDatabase
    $hint = "The service will connect as $Principal. On the SQL Server:"

    # Names are parameters, not interpolation: this is the read path and it does not need the
    # bracket-doubling dance that the DDL in Provision-NodePilotDatabase.ps1 does.
    $sql = @'
DECLARE @sid varbinary(85) = (SELECT TOP 1 sid FROM sys.server_principals WHERE name = @principal);
DECLARE @user sysname = (SELECT TOP 1 name FROM sys.database_principals WHERE sid = @sid);
SELECT
    CASE WHEN @sid IS NULL THEN 0 ELSE 1 END,
    ISNULL(@user, N''),
    ISNULL(IS_ROLEMEMBER('db_owner', @user), 0),
    ISNULL(IS_SRVROLEMEMBER('sysadmin', @principal), 0);
'@

    $connectionString = Resolve-NodePilotSqlProbeConnectionString `
        -Server $Server -Database $Database -CertificateHostName $CertificateHostName
    $conn = New-Object System.Data.SqlClient.SqlConnection $connectionString
    try {
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $sql
        $cmd.CommandTimeout = 30
        $parameter = $cmd.Parameters.Add('@principal', [Data.SqlDbType]::NVarChar, 128)
        $parameter.Value = $Principal
        $reader = $cmd.ExecuteReader()
        try {
            [void]$reader.Read()
            $loginExists = [int]$reader.GetValue(0) -eq 1
            $userName = [string]$reader.GetValue(1)
            $isDbOwner = [int]$reader.GetValue(2) -eq 1
            $isSysadmin = [int]$reader.GetValue(3) -eq 1
        }
        finally { $reader.Dispose() }
    }
    catch {
        # Cannot tell either way. Falling back to the caveat is right: claiming the grant is
        # missing would offer a fix for something that may be perfectly in order.
        return New-NodePilotPreflightResult -Id 'databaseServiceLogin' -Title $title -Status 'Warn' `
            -Detail ("Could not verify the service identity's access as your admin account: " +
                     "$($_.Exception.Message) At runtime the service connects as $Principal.") `
            -RemediationHint $hint -Remediation $remediation
    }
    finally { $conn.Dispose() }

    New-NodePilotSqlServiceLoginResult -Principal $Principal -Database $Database `
        -LoginExists $loginExists -UserName $userName -IsDbOwner $isDbOwner -IsSysadmin $isSysadmin
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

function New-NodePilotPostgresResult {
    <#
      The verdict for the Postgres row, separated from the connection that produces it - same
      split as New-NodePilotSqlServiceLoginResult, and for the same reason: no test host has a
      PostgreSQL server on it.

      -PsqlOutcome is what the login attempt produced: $null when no client was available (then
      this degrades to the TCP verdict the check has always given), otherwise an object with
      Succeeded and Error.

      -RoleExists / -DatabaseExists come from a SECOND connection, as the superuser, and are $null
      when there were no superuser credentials to make it with. They exist because psql's messages
      are localised: a German server answers "Rolle »nodepilot« existiert nicht", so matching on
      "role ... does not exist" classifies correctly on an English host and silently falls through
      to "refused" everywhere else. Measured on a de-DE cluster while building this. Asking
      pg_roles and pg_database is the same question in every locale.
    #>
    param(
        [Parameter(Mandatory)][string]$HostName,
        [Parameter(Mandatory)][int]$Port,
        [Parameter(Mandatory)][string]$User,
        [Parameter(Mandatory)][string]$Database,
        [Parameter(Mandatory)][bool]$TcpReachable,
        [AllowEmptyString()][string]$TcpError = '',
        $PsqlOutcome = $null,
        $RoleExists = $null,
        $DatabaseExists = $null,
        [bool]$CanProvision = $false
    )

    $title = 'PostgreSQL reachable'
    $remediation = Get-NodePilotPostgresRemediationScript -User $User -Database $Database

    if (-not $TcpReachable) {
        $suffix = if ($TcpError) { ": $TcpError" } else { '. Check DNS, firewall, and pg_hba.conf.' }
        return New-NodePilotPreflightResult -Id 'database' -Title $title -Status 'Fail' -Required $true `
            -Detail "Postgres reachability FAILED: TCP probe failed to ${HostName}:${Port}$suffix" `
            -RemediationHint "Cannot reach ${HostName}:${Port} from this host. Verify DNS, firewall, and that Postgres is listening on the external interface. Role setup on the DB server:" `
            -Remediation $remediation `
            -AbortMessage 'Aborted: Postgres pre-flight failed.'
    }

    # No client bundled: the port answered and that is all anyone can say. This is what the check
    # did for its whole life, and it is why a missing role or a wrong password used to cost a full
    # install and a 180-second health probe before anybody found out.
    if ($null -eq $PsqlOutcome) {
        return New-NodePilotPreflightResult -Id 'database' -Title $title -Status 'Warn' -Required $true `
            -Detail ("Postgres TCP reachable: ${HostName}:${Port}. This build carries no PostgreSQL " +
                     "client, so whether '$User' can actually log in to [$Database] is untested - a " +
                     'missing role or a wrong password will surface as a failed service start.') `
            -RemediationHint 'Verify on the database server that the role and database exist:' `
            -Remediation $remediation
    }
    if ($PsqlOutcome.Succeeded) {
        return New-NodePilotPreflightResult -Id 'database' -Title $title -Status 'Pass' -Required $true `
            -Detail "Postgres reachable and '$User' can log in to [$Database] on ${HostName}:${Port}."
    }

    # What the server said, verbatim and unparsed. Useful to a human in any language, and the only
    # thing there is to go on when nobody could ask the catalogue.
    # Not $error: that is a PowerShell automatic variable, and writing to it would clobber the
    # session's error history for everything downstream.
    $psqlError = ([string]$PsqlOutcome.Error) -replace '\s+', ' '

    $missing = @()
    if ($RoleExists -eq $false) { $missing += "the role '$User' does not exist" }
    if ($DatabaseExists -eq $false) { $missing += "the database [$Database] does not exist" }

    if ($missing.Count -gt 0) {
        # Only these two are something creating anything would help with, and they are exactly what
        # the fix creates.
        $label = if ($CanProvision) { "Create the role and database on $HostName now" } else { '' }
        return New-NodePilotPreflightResult -Id 'database' -Title $title -Status 'Fail' -Required $true `
            -Detail ("Postgres answered on ${HostName}:${Port} but $($missing -join ' and '). " +
                     'The service would start and fail its first query.') `
            -RemediationHint 'On the database server:' -Remediation $remediation `
            -CanAutoFix $CanProvision -AutoFixLabel $label `
            -AbortMessage "Aborted: Postgres pre-flight failed - $($missing -join ' and ')."
    }

    if ($null -ne $RoleExists -and $null -ne $DatabaseExists) {
        # Both there, still refused. Creating them again would change nothing, and the fix
        # deliberately never rewrites an existing role's password - so no button.
        return New-NodePilotPreflightResult -Id 'database' -Title $title -Status 'Fail' -Required $true `
            -Detail ("Postgres answered on ${HostName}:${Port} and both the role '$User' and the " +
                     "database [$Database] exist, but the login was refused: $psqlError " +
                     'That is the password, pg_hba.conf, or the TLS trust chain - not something ' +
                     'missing that could be created.') `
            -RemediationHint 'Check the password in this answer file, then pg_hba.conf on the server:' `
            -Remediation $remediation `
            -AbortMessage 'Aborted: Postgres pre-flight failed - the login was refused.'
    }

    # Nobody could ask the catalogue: no superuser credentials were given. The message is repeated
    # as-is rather than guessed at.
    New-NodePilotPreflightResult -Id 'database' -Title $title -Status 'Fail' -Required $true `
        -Detail ("Postgres answered on ${HostName}:${Port} but '$User' could not log in to " +
                 "[$Database]: $psqlError The service would start and fail its first query.") `
        -RemediationHint ('Without a PostgreSQL superuser the setup cannot tell a missing role from ' +
                          'a wrong password. On the database server:') `
        -Remediation $remediation `
        -AbortMessage 'Aborted: Postgres pre-flight failed - the login was refused.'
}

function Test-NodePilotPostgresReachable {
    <#
      Two probes, the second only when a client is available.

      The TCP probe has always been here and stays: "cannot even connect" is the common failure
      and it needs no credentials. What it could never answer is whether the SERVICE will get in,
      and on the Postgres path that is the whole question - unlike SQL Server there is no Windows
      identity to fall back on, so a typo in the role password looks exactly like a healthy
      install right up to the moment the service starts and the installer rolls it back 180
      seconds later.

      With psql from the installer payload the check logs in as the NodePilot role itself, in the
      runtime's own TLS shape (sslmode=verify-full against the configured root certificate). When
      that is refused AND superuser credentials were supplied, it asks the catalogue what is
      actually missing - rather than reading psql's message, which is localised.

      Param is named HostName (not Host) because $Host is a reserved PowerShell automatic
      variable (PSAvoidAssignmentToAutomaticVariable).
    #>
    param(
        [Parameter(Mandatory)][string]$HostName,
        [Parameter(Mandatory)][int]$Port,
        [Parameter(Mandatory)][string]$User,
        [Parameter(Mandatory)][string]$Database,
        [System.Security.SecureString]$Password,
        [AllowEmptyString()][string]$RootCertificate = '',
        [AllowEmptyString()][string]$PsqlPath = '',
        [AllowEmptyString()][string]$SuperUser = '',
        [System.Security.SecureString]$SuperPassword,
        [bool]$CanProvision = $false
    )

    $reachable = $false
    $tcpError = ''
    try {
        $tnc = Test-NetConnection -ComputerName $HostName -Port $Port -WarningAction SilentlyContinue
        $reachable = [bool]$tnc.TcpTestSucceeded
    } catch {
        $tcpError = $_.Exception.Message
    }

    $clientUsable = $reachable -and
        -not [string]::IsNullOrWhiteSpace($PsqlPath) -and (Test-Path -LiteralPath $PsqlPath -PathType Leaf) -and
        -not [string]::IsNullOrWhiteSpace($RootCertificate) -and (Test-Path -LiteralPath $RootCertificate -PathType Leaf)

    $outcome = $null
    if ($clientUsable -and $null -ne $Password -and $Password.Length -gt 0) {
        $outcome = Invoke-NodePilotPsqlLogin -PsqlPath $PsqlPath -HostName $HostName -Port $Port `
            -User $User -Password $Password -Database $Database -RootCertificate $RootCertificate
    }

    # Only when the service's own login failed: on the happy path there is nothing to diagnose, and
    # a superuser connection nobody needs is a superuser connection not worth making.
    $roleExists = $null
    $databaseExists = $null
    if ($clientUsable -and $null -ne $outcome -and -not $outcome.Succeeded -and
        -not [string]::IsNullOrWhiteSpace($SuperUser) -and
        $null -ne $SuperPassword -and $SuperPassword.Length -gt 0) {
        $catalogue = Invoke-NodePilotPsqlCatalogue -PsqlPath $PsqlPath -HostName $HostName -Port $Port `
            -SuperUser $SuperUser -SuperPassword $SuperPassword -RootCertificate $RootCertificate `
            -User $User -Database $Database
        if ($null -ne $catalogue) {
            $roleExists = $catalogue.RoleExists
            $databaseExists = $catalogue.DatabaseExists
        }
    }

    New-NodePilotPostgresResult -HostName $HostName -Port $Port -User $User -Database $Database `
        -TcpReachable $reachable -TcpError $tcpError -PsqlOutcome $outcome `
        -RoleExists $roleExists -DatabaseExists $databaseExists -CanProvision $CanProvision
}

function Invoke-NodePilotPsqlCatalogue {
    <#
      Asks pg_roles and pg_database whether the two things the service needs are there. Read-only,
      and the answer is the same in every locale - which reading psql's error message is not.

      Returns $null when the superuser connection itself fails: "I could not find out" is a
      different answer from "they are not there", and offering to create a role because the
      superuser password was wrong would be the worse of the two mistakes.
    #>
    param(
        [Parameter(Mandatory)][string]$PsqlPath,
        [Parameter(Mandatory)][string]$HostName,
        [Parameter(Mandatory)][int]$Port,
        [Parameter(Mandatory)][string]$SuperUser,
        [Parameter(Mandatory)][System.Security.SecureString]$SuperPassword,
        [Parameter(Mandatory)][string]$RootCertificate,
        [Parameter(Mandatory)][string]$User,
        [Parameter(Mandatory)][string]$Database
    )

    $sql = "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$($User.Replace("'", "''"))')," +
           " EXISTS (SELECT 1 FROM pg_database WHERE datname = '$($Database.Replace("'", "''"))');"

    $result = Invoke-NodePilotPsql -PsqlPath $PsqlPath -Arguments @(
            '-w'
            '-h', $HostName
            '-p', "$Port"
            '-U', $SuperUser
            '-d', 'postgres'
            '-v', 'ON_ERROR_STOP=1'
            '-tA'
        ) -Sql $sql -Environment (Get-NodePilotPsqlEnvironment `
            -Secret (ConvertFrom-NodePilotSecureString -Value $SuperPassword) `
            -RootCertificate $RootCertificate)

    if (-not $result.Succeeded) { return $null }
    $fields = ([string]$result.Output).Trim() -split '\|'
    if ($fields.Count -lt 2) { return $null }
    return [pscustomobject]@{
        RoleExists     = ($fields[0] -eq 't')
        DatabaseExists = ($fields[1] -eq 't')
    }
}

function ConvertTo-NodePilotCommandLineArgument {
    <#
      One argument, quoted the way CommandLineToArgvW parses it back.

      Windows PowerShell 5.1 runs on .NET Framework, where ProcessStartInfo has no ArgumentList -
      only a single Arguments string - so the quoting has to be done here rather than by the
      runtime. This is Microsoft's own ArgvQuote algorithm: backslashes are literal EXCEPT when
      they precede a quote, where they double.
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Value)

    if ($Value -ne '' -and $Value -notmatch '[ \t\n\v"]') { return $Value }

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    for ($index = 0; $index -lt $Value.Length; $index++) {
        $backslashes = 0
        while ($index -lt $Value.Length -and $Value[$index] -eq '\') { $index++; $backslashes++ }
        if ($index -eq $Value.Length) {
            # Trailing backslashes would escape the closing quote, so they double.
            [void]$builder.Append('\', $backslashes * 2)
            break
        }
        if ($Value[$index] -eq '"') {
            [void]$builder.Append('\', $backslashes * 2 + 1)
            [void]$builder.Append('"')
        }
        else {
            [void]$builder.Append('\', $backslashes)
            [void]$builder.Append($Value[$index])
        }
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Invoke-NodePilotPsql {
    <#
      Runs the bundled psql client and hands back exit code, stdout and stderr.

      Plumbing only - it decides nothing and issues no SQL of its own; the caller supplies the
      statement, and in THIS file the only caller supplies a SELECT. The provisioning script is
      where statements that change something live.

      The SQL goes in on STDIN, never as -c. A CREATE ROLE carries the new role's password, and an
      argument is visible in the process list to every user on the machine for as long as the call
      runs. psql with neither -c nor -f reads its input from stdin, so this costs nothing.

      System.Diagnostics.Process rather than the call operator, for three reasons that all bite:
        * The connection secrets go into this ONE process's environment block. Setting them on the
          current process would leave PGPASSWORD readable by anything else running in it.
        * psql writes ordinary refusals ("role does not exist") to stderr, which Windows PowerShell
          turns into a terminating NativeCommandError under $ErrorActionPreference = 'Stop'.
        * No temporary file, so a readiness check that must not touch the machine does not have to
          create and delete one to read an error message.
    #>
    param(
        [Parameter(Mandatory)][string]$PsqlPath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Sql,
        [Parameter(Mandatory)][hashtable]$Environment
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $PsqlPath
    $startInfo.Arguments = (($Arguments | ForEach-Object {
        ConvertTo-NodePilotCommandLineArgument -Value $_
    }) -join ' ')
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($name in $Environment.Keys) {
        $startInfo.EnvironmentVariables[$name] = [string]$Environment[$name]
    }

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        [void]$process.Start()
        $process.StandardInput.Write($Sql)
        $process.StandardInput.Close()
        # stdout asynchronously, stderr synchronously: reading both to the end in sequence
        # deadlocks the moment either pipe buffer fills.
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        return [pscustomobject]@{
            Succeeded = ($process.ExitCode -eq 0)
            Output    = ([string]$stdout.Result).Trim()
            Error     = ([string]$stderr).Trim()
        }
    }
    finally { $process.Dispose() }
}

function ConvertFrom-NodePilotSecureString {
    param([Parameter(Mandatory)][System.Security.SecureString]$Value)
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

function Get-NodePilotPsqlEnvironment {
    <#
      The connection settings every psql call gets, in the runtime's own TLS shape. Shared so the
      check and the fix cannot drift into connecting differently - a fix that succeeds over a
      laxer path than the service will use has proven nothing.
    #>
    param(
        [Parameter(Mandatory)][string]$Secret,
        [Parameter(Mandatory)][string]$RootCertificate
    )
    return @{
        PGPASSWORD        = $Secret
        PGSSLMODE         = 'verify-full'
        PGSSLROOTCERT     = $RootCertificate
        PGCONNECT_TIMEOUT = '10'
    }
}

function Invoke-NodePilotPsqlLogin {
    <#
      One login attempt as the NodePilot role. SELECT 1 and nothing else - this file may not
      mutate, and that rule does not stop at PowerShell cmdlets.
    #>
    param(
        [Parameter(Mandatory)][string]$PsqlPath,
        [Parameter(Mandatory)][string]$HostName,
        [Parameter(Mandatory)][int]$Port,
        [Parameter(Mandatory)][string]$User,
        [Parameter(Mandatory)][System.Security.SecureString]$Password,
        [Parameter(Mandatory)][string]$Database,
        [Parameter(Mandatory)][string]$RootCertificate
    )

    # -w on every call so psql fails instead of prompting: there is no console behind a hidden
    # Exec, and a prompt there is a wizard that hangs until its own timeout.
    return Invoke-NodePilotPsql -PsqlPath $PsqlPath -Arguments @(
            '-w'
            '-h', $HostName
            '-p', "$Port"
            '-U', $User
            '-d', $Database
            '-v', 'ON_ERROR_STOP=1'
            '-tA'
        ) -Sql 'SELECT 1;' -Environment (Get-NodePilotPsqlEnvironment `
            -Secret (ConvertFrom-NodePilotSecureString -Value $Password) `
            -RootCertificate $RootCertificate)
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
        # Only used to tell the operator that the certificate names a different host. Optional,
        # because the console path can be invoked without one and a missing name is not a finding.
        [AllowEmptyString()][string]$PublicHostname = '',
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
        # Only the Postgres row uses these, and only to answer the question the TCP probe never
        # could: can the SERVICE log in. Absent, that row degrades to the reachability answer it
        # has always given.
        [System.Security.SecureString]$PostgresPassword,
        [AllowEmptyString()][string]$PostgresRootCertificate = '',
        [AllowEmptyString()][string]$PsqlPath = '',
        [AllowEmptyString()][string]$PostgresSuperUser = '',
        [System.Security.SecureString]$PostgresSuperPassword,
        [bool]$CanProvisionPostgres = $false,
        [string]$ServiceName = 'NodePilot',
        # Only the setup passes these: it carries the publisher certificate in its payload and knows
        # the thumbprint it was built against. Absent - the scripted path, which has no payload - the
        # row is not emitted at all, exactly as the Postgres credentials degrade.
        [AllowEmptyString()][string]$ArtifactSignerCertificatePath = '',
        [AllowEmptyString()][string]$ExpectedSignerThumbprint = '',
        [switch]$SkipDatabaseCheck,
        [switch]$SkipGmsaCheck
    )

    $results = @()
    if ($ArtifactSignerCertificatePath) {
        $results += Test-NodePilotArtifactSignerTrust `
            -CertificatePath $ArtifactSignerCertificatePath -ExpectedThumbprint $ExpectedSignerThumbprint
    }
    $results += Test-NodePilotDotNetRuntime
    $results += Test-NodePilotTlsCertificate -Thumbprint $CertificateThumbprint -PublicHostname $PublicHostname
    $results += Test-NodePilotListenPorts -HttpsPort $HttpsPort -HttpPort $HttpPort -ServiceName $ServiceName

    if ($IsLocalSystem) {
        # The detail must not repeat the title: the wizard renders "<Title>: <Detail>", so a detail
        # that opens with its own title produced "Service identity: Service identity: LocalSystem -
        # ..." on screen and wrapped a line further than it needed to.
        $results += New-NodePilotPreflightResult -Id 'gmsa' -Title 'Service identity' -Status 'Skipped' `
            -Detail "LocalSystem - network identity is the computer account $ComputerAccount."
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
            # Both identities, not just LocalSystem. While this was a printed caveat there was
            # nothing useful to say about a gMSA that the gMSA check had not already said; as a
            # query it answers the same question for both, and the 503 it predicts does not care
            # which kind of principal the service runs as.
            $results += Test-NodePilotSqlServiceLogin -Principal $SqlPrincipal `
                -Server $SqlServer -Database $SqlDatabase -CertificateHostName $SqlCertificateHostName
            $results += Test-NodePilotSqlTds8Support `
                -Server $SqlServer -CertificateHostName $SqlCertificateHostName
        }
    } else {
        $results += Test-NodePilotPostgresReachable `
            -HostName $PostgresHost -Port $PostgresPort `
            -User $PostgresUser -Database $PostgresDatabase `
            -Password $PostgresPassword -RootCertificate $PostgresRootCertificate `
            -PsqlPath $PsqlPath -SuperUser $PostgresSuperUser -SuperPassword $PostgresSuperPassword `
            -CanProvision $CanProvisionPostgres
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
