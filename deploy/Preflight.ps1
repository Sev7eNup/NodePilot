#requires -Version 5.1

<#
  Side-effect-free readiness checks for a NodePilot server installation.

  Dot-sourced by Install-NodePilot.ps1, which asserts on the results and aborts, and by the
  setup wizard's adapter, which renders them as a traffic-light page with a re-check button.

  Nothing in this file may mutate anything: no ALTER DATABASE, no CREATE, no New-Service, no
  Set-Acl, no New-SelfSignedCertificate, no firewall rules. Every check runs again on each click
  of re-check, so install-time work stays in Install-NodePilot.ps1.
  Test-DeploymentTemplates.ps1 enforces the rule.

  Two layers:
    * Test-NodePilot*        - one probe each, returns a result object and does not throw for a
                               failed check (only for a caller error such as a missing param),
                               which is what makes them callable from a UI button.
    * Invoke-NodePilotPreflight / Assert-NodePilotPreflight
                             - collect the applicable set, then print and abort.
#>

Set-StrictMode -Version 3.0

# Status values used across this file:
#   Pass    - requirement met
#   Fail    - requirement not met; aborts the install when the check is Required
#   Warn    - worth saying out loud, never aborts
#   Skipped - not applicable to this configuration

# One label for the bundled-runtime fix, because two checks offer it and a reader comparing two
# rows should not have to work out whether they do the same thing. It names both packages: the
# setup carries the .NET runtime as well, and saying only "ASP.NET Core" is the half-truth this
# fix exists to correct.
$NodePilotRuntimeFixLabel = 'Install the bundled .NET 10 and ASP.NET Core 10 runtimes now'

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
        # Whether the wizard arrives with this fix already ticked. Reserved for work that is a
        # precondition of the installation the operator has already chosen, and that only adds
        # what NodePilot itself needs: the database named two pages earlier, the service
        # identity's access to it, and the .NET runtime without which the service cannot start.
        # Never pre-ticked: anything that changes what the machine trusts or replaces something
        # already there - the publisher certificate in LocalMachine\Root, or a TLS certificate.
        # The box stays visible and clearable either way.
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
        # Dropped when the caller has already proven the database exists. A CREATE DATABASE for a
        # database the DBA can see makes the rest of the script look wrong too.
        [switch]$SkipCreateDatabase
    )
    $principalIdentifier = $Principal.Replace(']', ']]')
    $principalLiteral = $Principal.Replace("'", "''")
    $databaseIdentifier = $Database.Replace(']', ']]')
    $databaseLiteral = $Database.Replace("'", "''")
    $lines = @(
        '-- Run in SQL Server Management Studio as a SQL Server administrator.'
        'USE [master];'
        'GO'
        "IF SUSER_ID(N'$principalLiteral') IS NULL"
        "    CREATE LOGIN [$principalIdentifier] FROM WINDOWS;"
        'GO'
    )
    if (-not $SkipCreateDatabase) {
        $lines += @(
            "IF DB_ID(N'$databaseLiteral') IS NULL"
            "    CREATE DATABASE [$databaseIdentifier];"
            'GO'
        )
    }
    $lines += @(
        "USE [$databaseIdentifier];"
        'GO'
        "IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE sid = SUSER_SID(N'$principalLiteral'))"
        "    CREATE USER [$principalIdentifier] FOR LOGIN [$principalIdentifier];"
        'GO'
        "DECLARE @user sysname = (SELECT TOP 1 name FROM sys.database_principals WHERE sid = SUSER_SID(N'$principalLiteral'));"
        "IF IS_ROLEMEMBER(N'db_owner', @user) = 0"
        'BEGIN'
        "    DECLARE @grant nvarchar(max) = N'ALTER ROLE db_owner ADD MEMBER ' + QUOTENAME(@user);"
        '    EXEC sys.sp_executesql @grant;'
        'END;'
        'GO'
    )
    $lines -join [Environment]::NewLine
}

function Get-NodePilotCertificateImportInstructions {
    @(
        'In setup, go back to HTTPS certificate and choose Import a PFX file.'
        'Alternatively, run this in an elevated PowerShell window, replacing the example path:'
        'Import-PfxCertificate -FilePath ''C:\path\server.pfx'' -CertStoreLocation Cert:\LocalMachine\My -Password (Read-Host -AsSecureString)'
        'Then select the imported certificate on the HTTPS certificate page and check again.'
    ) -join [Environment]::NewLine
}

function Get-NodePilotPostgresRemediationScript {
    param(
        [Parameter(Mandatory)][string]$User,
        [Parameter(Mandatory)][string]$Database,
        [bool]$CreateRole = $true,
        [bool]$CreateDatabase = $true
    )
    $quotedUser = '"' + $User.Replace('"', '""') + '"'
    $quotedDatabase = '"' + $Database.Replace('"', '""') + '"'
    @(
        '-- In psql, connect as a database administrator to the postgres database.'
        '-- Run only the statements for objects confirmed missing.'
        if ($CreateRole) {
            "CREATE ROLE $quotedUser WITH LOGIN;"
            '-- At the password prompt, enter the same role password configured in setup.'
            "\password $quotedUser"
        }
        if ($CreateDatabase) { "CREATE DATABASE $quotedDatabase OWNER $quotedUser;" }
    ) -join [Environment]::NewLine
}

# ---------------------------------------------------------------------------
# Individual checks
# ---------------------------------------------------------------------------

function Get-NodePilotPeArchitecture {
    <#
      The machine type from a PE file's COFF header: 'x64', 'x86', 'arm64', or $null when the
      file cannot be read or is not a PE image.

      Not 'dotnet --info', which also reports the host architecture: its labels are localised, so
      a parse of the English text finds nothing on a non-English server. 'dotnet --list-runtimes'
      is not localised and stays for the version question, but it says nothing about architecture,
      so this reads the bytes.

      Opened with FileShare ReadWrite because dotnet.exe may be running.
    #>
    param([Parameter(Mandatory)][string]$Path)

    try {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    }
    catch { return $null }

    try {
        # A file that is too short, or a header offset pointing past the end, fails in the reader
        # and is handled by the catch below. A separate length guard would add an untestable branch.
        $reader = New-Object IO.BinaryReader($stream)
        $stream.Position = 0x3C
        $stream.Position = $reader.ReadInt32()
        # Without the PE signature check any file would yield a machine type, so a junk file
        # would classify as a valid x64 host.
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

      PATH alone is not enough: a clean Windows Server has no dotnet on PATH, and a process that
      installed the runtime itself keeps the PATH it started with. The well-known machine-wide
      locations are the fallback.

      All PATH hits are collected, not just the first, because a machine with both runtimes
      installed can have the x86 one earlier in PATH.

      The registry (HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\InstallLocation) is what the
      apphost consults but is not read here: it only adds installations in unusual places that
      PATH already covers, and a registry view redirected under WOW64 could answer wrongly.
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
        # Only architectures worth naming. An unreadable or exotic image falls through to the
        # plain "nothing found" verdict.
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

function Get-NodePilotInstalledFrameworks {
    <#
      Every shared framework 'dotnet --list-runtimes' reported, as Name/Version objects. Shared by
      the runtime floor check and the artifact requirement check so the two cannot read the same
      output differently.
    #>
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Runtimes)

    foreach ($runtime in @($Runtimes)) {
        if ("$runtime" -match '^(?<Name>[\w\.]+) (?<Version>\d+\.\d+\.\d+)(?:\s|$)') {
            $parsed = $null
            # Indexed, not dotted: $Matches is a hashtable, and a capture named like one of its
            # own members would resolve to the member instead of the capture.
            if ([version]::TryParse($Matches['Version'], [ref]$parsed)) {
                [pscustomobject]@{ Name = $Matches['Name']; Version = $parsed }
            }
        }
    }
}

function Test-NodePilotDotNetRuntime {
    <#
      NodePilot publishes with --runtime win-x64 and installs the NodePilot.Api.exe apphost, which
      a 32-bit runtime cannot host. A check that accepts any architecture would pass on a machine
      where the service then refuses to start.
    #>
    param([object]$State = (Get-NodePilotDotNetHostState))

    $minimumRuntime = [version]'10.0.11'
    $title = 'ASP.NET Core 10.0.11+ runtime'
    # Names both downloads. The ASP.NET Core Runtime alone carries only Microsoft.AspNetCore.App -
    # no dotnet.exe - so on a machine without .NET it leaves this row red however cleanly it
    # installs. "the plain runtime" used to stand here and read as ".NET Runtime", which is the
    # other half of the same trap.
    $hint = 'Install BOTH, x64, in this order: the .NET Runtime 10.0.11 or newer (it carries dotnet.exe and Microsoft.NETCore.App), then the ASP.NET Core Runtime of the same version (it carries Microsoft.AspNetCore.App). They are separate downloads on that page, as is the .NET SDK, and neither runtime alone is enough. Not the Hosting Bundle - it also wires up IIS.'
    $link = 'https://dotnet.microsoft.com/download/dotnet/10.0'
    $fixLabel = $NodePilotRuntimeFixLabel

    if (-not $State.X64Path) {
        # Two different messages: "nothing found" sends the operator looking for an installer,
        # while "found, but 32-bit" explains why the dotnet already on PATH does not count.
        if ($State.OtherPath) {
            $found = switch ($State.OtherArchitecture) {
                'x86' { '32-bit (x86)' }
                'arm64' { 'ARM64' }
                default { $State.OtherArchitecture }
            }
            return New-NodePilotPreflightResult -Id 'dotnet' -Title $title -Status 'Fail' -Required $true `
                -CanAutoFix $true -AutoFixDefault $true -AutoFixLabel $fixLabel `
                -Detail ("Only a $found .NET host was found ($($State.OtherPath)). " +
                         'NodePilot is a 64-bit application and needs the x64 runtime.') `
                -RemediationHint $hint -Remediation $link `
                -AbortMessage ("Only a $found .NET host was found ($($State.OtherPath)). " +
                               "Install the 64-bit ASP.NET Core 10 runtime from $link.")
        }
        return New-NodePilotPreflightResult -Id 'dotnet' -Title $title -Status 'Fail' -Required $true `
            -CanAutoFix $true -AutoFixDefault $true -AutoFixLabel $fixLabel `
            -Detail 'dotnet was not found on PATH or under Program Files.' `
            -RemediationHint $hint -Remediation $link `
            -AbortMessage ".NET Runtime not found on PATH. Install the ASP.NET Core 10 runtime from $link."
    }

    $aspNetTenVersions = @(
        foreach ($runtime in @($State.Runtimes)) {
            if ("$runtime" -match '^Microsoft\.AspNetCore\.App (?<Version>10\.\d+\.\d+)(?:\s|$)') {
                try { [version]$Matches.Version } catch { }
            }
        }
    )
    $patchedRuntime = @($aspNetTenVersions | Where-Object { $_ -ge $minimumRuntime } | Sort-Object -Descending | Select-Object -First 1)
    if ($patchedRuntime.Count -eq 0) {
        # Always say what WAS found. An empty tail here left an operator with .NET 10 installed
        # reading a version-shaped complaint and concluding the version was too old.
        $frameworks = @(Get-NodePilotInstalledFrameworks -Runtimes $State.Runtimes)
        $aspNetAny = @($frameworks | Where-Object { $_.Name -eq 'Microsoft.AspNetCore.App' })
        $netCoreTen = @($frameworks | Where-Object {
            $_.Name -eq 'Microsoft.NETCore.App' -and $_.Version.Major -eq 10
        } | Sort-Object -Property Version -Descending)
        $foundDetail = if ($aspNetTenVersions.Count -gt 0) {
            " Found vulnerable/unsupported version(s): $($aspNetTenVersions -join ', ')."
        } elseif ($aspNetAny.Count -eq 0 -and $netCoreTen.Count -gt 0) {
            # What a host looks like when the .NET Runtime was installed instead of the ASP.NET Core
            # Runtime: it carries Microsoft.NETCore.App and nothing else.
            " Microsoft.NETCore.App $($netCoreTen[0].Version) is installed but Microsoft.AspNetCore.App is not," +
            ' so this is the .NET Runtime rather than the ASP.NET Core Runtime.'
        } elseif ($frameworks.Count -gt 0) {
            " Installed: $((($frameworks | ForEach-Object { "$($_.Name) $($_.Version)" }) | Sort-Object) -join ', ')."
        } else { ' It reported no shared frameworks at all.' }
        return New-NodePilotPreflightResult -Id 'dotnet' -Title $title -Status 'Fail' -Required $true `
            -CanAutoFix $true -AutoFixDefault $true -AutoFixLabel $fixLabel `
            -Detail ("No patched Microsoft.AspNetCore.App 10 runtime (minimum 10.0.11) was reported by " +
                     "'$($State.X64Path) --list-runtimes'.$foundDetail") `
            -RemediationHint $hint -Remediation $link `
            -AbortMessage ".NET 10 ASP.NET Core Runtime 10.0.11 or newer not found. Install the patched ASP.NET Core 10 runtime ($link)."
    }

    New-NodePilotPreflightResult -Id 'dotnet' -Title $title -Status 'Pass' -Required $true `
        -Detail ".NET 10 ASP.NET Core runtime $($patchedRuntime[0]) found (x64, $($State.X64Path))."
}

function Get-NodePilotArtifactRuntimeRequirement {
    <#
      Every shared framework an extracted artifact asks its host for, read from
      NodePilot.Api.runtimeconfig.json, as objects with Name and Version.

      All of them, not just Microsoft.AspNetCore.App: the apphost resolves each one and refuses to
      start if any is missing. A host can carry a newer Microsoft.AspNetCore.App than
      Microsoft.NETCore.App, and a check that looks at the web framework alone passes there and
      still leaves a service that will not start.

      Empty when the artifact carries its own runtime or names no shared framework, so a caller
      degrades to the fixed floor instead of refusing an artifact it cannot read.
    #>
    param([Parameter(Mandatory)][string]$RootPath)

    $configPath = Join-Path $RootPath 'NodePilot.Api.runtimeconfig.json'
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) { return @() }

    $options = $null
    try {
        $document = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($document -and ($document.PSObject.Properties.Name -contains 'runtimeOptions')) {
            $options = $document.runtimeOptions
        }
    } catch { return @() }
    if (-not $options) { return @() }

    $sections = $options.PSObject.Properties.Name
    # Self-contained: the frameworks ship inside the artifact and nothing is asked of the machine.
    if ($sections -contains 'includedFrameworks') { return @() }

    $frameworks = @()
    if ($sections -contains 'framework') { $frameworks += $options.framework }
    if ($sections -contains 'frameworks') { $frameworks += @($options.frameworks) }

    $required = @()
    foreach ($framework in $frameworks) {
        if (-not $framework) { continue }
        $fields = $framework.PSObject.Properties.Name
        if (($fields -notcontains 'name') -or ($fields -notcontains 'version')) { continue }
        $parsed = $null
        if (-not [version]::TryParse([string]$framework.version, [ref]$parsed)) { continue }
        $required += [pscustomobject]@{ Name = [string]$framework.name; Version = $parsed }
    }
    return @($required)
}

function Test-NodePilotArtifactRuntime {
    <#
      Whether this machine can host the artifact that is about to be installed.

      Test-NodePilotDotNetRuntime checks a fixed floor; this checks the exact versions the artifact
      in hand was built against, which is what the apphost resolves at start. Roll-forward is
      minor-only by default: a higher patch or minor of the same major serves, a lower one does
      not, and there is no roll-back to an older patch. A build made against a newer runtime than
      the target host carries therefore dies the moment the SCM starts it, with no managed
      exception and nothing in the event log to name the cause.
    #>
    param(
        [object[]]$Requirement = @(),
        [object]$State = (Get-NodePilotDotNetHostState)
    )

    $title = 'Runtime serves the new build'
    $link = 'https://dotnet.microsoft.com/download/dotnet/10.0'
    $required = @($Requirement | Where-Object { $_ })

    if ($required.Count -eq 0) {
        return New-NodePilotPreflightResult -Id 'artifactRuntime' -Title $title -Status 'Skipped' `
            -Detail 'The artifact names no shared framework, so there is nothing to match.'
    }

    $installed = @(Get-NodePilotInstalledFrameworks -Runtimes $State.Runtimes)

    $unmet = @()
    $served = @()
    foreach ($entry in $required) {
        $candidates = @($installed |
            Where-Object { $_.Name -eq $entry.Name -and
                           $_.Version.Major -eq $entry.Version.Major -and
                           $_.Version -ge $entry.Version } |
            Sort-Object Version -Descending | Select-Object -First 1)
        if ($candidates.Count -gt 0) {
            $served += "$($entry.Name) $($entry.Version) (served by $($candidates[0].Version))"
            continue
        }
        $sameName = @($installed | Where-Object { $_.Name -eq $entry.Name } |
            ForEach-Object { $_.Version } | Sort-Object)
        $found = if ($sameName.Count -gt 0) { $sameName -join ', ' } else { 'none' }
        $unmet += "$($entry.Name) $($entry.Version) or a higher $($entry.Version.Major).x (installed: $found)"
    }

    if ($unmet.Count -eq 0) {
        return New-NodePilotPreflightResult -Id 'artifactRuntime' -Title $title -Status 'Pass' -Required $true `
            -Detail "The installed runtimes serve this build: $($served -join '; ')."
    }

    $detail = "This build needs $($unmet -join '; '). All of them are x64."
    New-NodePilotPreflightResult -Id 'artifactRuntime' -Title $title -Status 'Fail' -Required $true `
        -CanAutoFix $true -AutoFixDefault $true -AutoFixLabel $NodePilotRuntimeFixLabel `
        -Detail $detail `
        -RemediationHint ('Install the matching ASP.NET Core runtime (x64) - it carries the base ' +
                          '.NET runtime of the same version - then re-run.') `
        -Remediation $link `
        -AbortMessage "$detail Install the matching ASP.NET Core runtime (x64) from $link, then re-run."
}

function Get-NodePilotCertificateInventory {
    <#
      What is available in LocalMachine\My. The installer prints this when a thumbprint does not
      normalize; the wizard fills its certificate picker from it.

      Sorted by expiry, latest first, and sorted here rather than in either caller: a renewed
      certificate sits in the store beside the one it replaces, under the same subject, and only
      that date separates them. Newest first puts the renewal at the top and expired ones last.
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
        # 10013 is the one that matters here, and it does NOT mean "in use". Windows returns it for
        # a
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
        -RemediationHint 'Choose a free port, or set the HTTP port to 0 to disable the redirect. In setup, go back to Network and use Find available ports.' `
        -Remediation ("See what Windows has reserved:`r`n" +
                      "netsh interface ipv4 show excludedportrange protocol=tcp`r`n`r`n" +
                      "See who is listening:`r`n" +
                      "Get-NetTCPConnection -State Listen | Sort-Object LocalPort`r`n`r`n" +
                      'IIS and other Windows services can occupy ports through HTTP.SYS. NodePilot needs ' +
                      'ports that are free on this computer. For a blocked HTTPS port, choose another port. ' +
                      'For a blocked HTTP port, choose another port or set it to 0 to disable the redirect.') `
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

function Get-NodePilotDefaultPublicHostname {
    [CmdletBinding()]
    param([string]$ComputerName = $env:COMPUTERNAME)
    try {
        $resolved = [Net.Dns]::GetHostEntry($ComputerName).HostName
        if (-not [string]::IsNullOrWhiteSpace($resolved)) { return $resolved }
    } catch { Write-Verbose 'DNS lookup unavailable; using the computer name as the suggestion.' }
    return $ComputerName
}

function Get-NodePilotServerNames {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PublicHostname, [string]$ComputerName = $env:COMPUTERNAME)
    $names = @()
    foreach ($name in @($PublicHostname, $ComputerName, 'localhost')) {
        if ($name -and $names -notcontains $name.Trim()) { $names += $name.Trim() }
    }
    return $names
}

function Resolve-NodePilotAllowedHosts {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PublicHostname, [string]$ComputerName = $env:COMPUTERNAME,
        [string]$AllowedHosts = '')
    [string[]]$names = if ([string]::IsNullOrWhiteSpace($AllowedHosts)) {
        @(Get-NodePilotServerNames -PublicHostname $PublicHostname -ComputerName $ComputerName)
    } else { @($AllowedHosts -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
    if ($names -notcontains 'localhost') { $names += 'localhost' }
    return ($names -join ';')
}

function Get-NodePilotTlsLoginCommand {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PublicHostname,
        [ValidateRange(1, 65535)][int]$HttpsPort = 443, [string]$Sha256 = '')
    $url = (New-Object UriBuilder('https', $PublicHostname, $HttpsPort)).Uri.AbsoluteUri
    $command = 'np auth login --server "{0}"' -f $url
    if ($Sha256) { $command += " --tls-thumbprint $Sha256" }
    return $command
}

function Format-NodePilotCertificateSummary {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Certificate, [AllowEmptyString()][string]$ServerUrl = '')
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $pin = [BitConverter]::ToString($sha.ComputeHash($Certificate.RawData)).Replace('-', '') }
    finally { $sha.Dispose() }
    $names = @(Get-NodePilotCertificateNames -Certificate $Certificate)
    $selfSigned = $Certificate.Subject -eq $Certificate.Issuer
    $lines = @(
        "Names         $($names -join ', ')"
        "Valid from    $($Certificate.NotBefore.ToString('yyyy-MM-dd'))"
        "Expires       $($Certificate.NotAfter.ToString('yyyy-MM-dd'))"
        'Windows store ID (SHA-1)'
        "  $($Certificate.Thumbprint)"
        'Certificate pin (SHA-256)'
        "  $pin"
        ''
    )
    if ($selfSigned) {
        $lines += @('This certificate is self-signed (lab/pilot use).',
            'Browsers and API clients need its public certificate trusted on each client.',
            'Setup does not add client trust. For np, use the certificate pin below instead.')
    } else {
        $lines += 'Clients must trust the issuing CA; trust on this server does not establish client trust.'
    }
    if ($ServerUrl) {
        $address = [Uri]$ServerUrl
        $command = Get-NodePilotTlsLoginCommand -PublicHostname $address.DnsSafeHost -HttpsPort $address.Port
        if (-not $selfSigned) { $lines += @('', 'CLI login:', $command, '', 'CLI certificate pinning (optional):') }
        else { $lines += @('', 'CLI login with this certificate:') }
        $lines += Get-NodePilotTlsLoginCommand -PublicHostname $address.DnsSafeHost -HttpsPort $address.Port -Sha256 $pin
        $lines += 'After certificate replacement, verify and update any saved certificate pin.'
    }
    return ($lines -join "`r`n")
}

function Test-NodePilotTlsCertificate {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Thumbprint,
        [AllowEmptyString()][string]$PublicHostname = ''
    )

    $title = 'HTTPS certificate'

    # "I have none yet" is a different answer from "the one I named is missing", and it is the
    # answer a fresh host gives. Reported before the store is read, so this branch also states the
    # verdict on a machine where the certificate store cannot be enumerated at all - and so the
    # message stops reading "Certificate  is not present", with the empty thumbprint rendered as
    # the gap it is.
    if ([string]::IsNullOrWhiteSpace($Thumbprint)) {
        return New-NodePilotPreflightResult -Id 'certificate' -Title $title -Status 'Fail' -Required $true `
            -CanAutoFix $true -AutoFixLabel 'Generate a self-signed certificate (lab use only)' `
            -Detail 'No certificate selected. Kestrel terminates TLS itself and will not start without one.' `
            -RemediationHint 'Go back to HTTPS certificate and select or import a certificate, or select the available fix to generate a self-signed certificate for lab use.' `
            -Remediation (Get-NodePilotCertificateImportInstructions) `
            -AbortMessage 'No certificate thumbprint was given. Kestrel cannot serve HTTPS without one.'
    }

    $cert = Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $Thumbprint }

    if (-not $cert) {
        return New-NodePilotPreflightResult -Id 'certificate' -Title $title -Status 'Fail' -Required $true `
            -CanAutoFix $true -AutoFixLabel 'Generate a self-signed certificate (lab use only)' `
            -Detail "Certificate $Thumbprint is not present in Cert:\LocalMachine\My." `
            -RemediationHint 'Go back to HTTPS certificate and select or import a certificate, then check again.' `
            -Remediation (Get-NodePilotCertificateImportInstructions) `
            -AbortMessage "Cert $Thumbprint not found in Cert:\LocalMachine\My. Import the PFX (MachineKeySet|PersistKeySet) and retry."
    }
    if (-not $cert.HasPrivateKey) {
        return New-NodePilotPreflightResult -Id 'certificate' -Title $title -Status 'Fail' -Required $true `
            -Detail "Certificate $Thumbprint is present but carries no private key." `
            -RemediationHint 'Re-import the PFX so the private key is persisted for the machine.' `
            -Remediation (Get-NodePilotCertificateImportInstructions) `
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
                -Detail "Publisher $($certificate.Subject) is trusted on this computer ($actual)."
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
        -Detail ("Publisher $($certificate.Subject) ($actual) is not trusted on this computer. " +
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

    $title = 'HTTPS certificate'
    $importHint = 'Go back to HTTPS certificate and select or import a current certificate, then check again.'
    $importCommand = Get-NodePilotCertificateImportInstructions

    # Loud, but not a stop. Validity is a property of the certificate, not of the installation:
    # Kestrel binds by thumbprint and never reads NotAfter, so the service starts either way, and
    # swapping the certificate afterwards is a store import plus a restart. Refusing to install
    # would block a host whose certificate is renewed the same afternoon.
    $replaceHint = "$importHint The installation is not blocked - the service will start and serve it, " +
                   'but clients normally refuse the connection until it is replaced.'
    $names = @(Get-NodePilotCertificateNames -Certificate $Certificate)
    $claimed = if ($names.Count -gt 0) { $names -join ', ' } else { '(no host name at all)' }
    $metadata = " Names: $claimed. Valid from $($Certificate.NotBefore.ToString('yyyy-MM-dd')); " +
                "expires $($Certificate.NotAfter.ToString('yyyy-MM-dd'))."
    # -Required stays, as on every other branch of this check: only Fail plus Required aborts, so on
    # a Warn the flag says "this requirement is checked", not "this stops here".
    if ($Certificate.NotAfter -lt $Now) {
        return New-NodePilotPreflightResult -Id 'certificate' -Title $title -Status 'Warn' -Required $true `
            -Detail ("Certificate $($Certificate.Subject) expired on $($Certificate.NotAfter.ToString('yyyy-MM-dd')). " +
                     "Kestrel will serve it and clients normally refuse it.$metadata") `
            -RemediationHint $replaceHint -Remediation $importCommand
    }
    if ($Certificate.NotBefore -gt $Now) {
        return New-NodePilotPreflightResult -Id 'certificate' -Title $title -Status 'Warn' -Required $true `
            -Detail ("Certificate $($Certificate.Subject) is not valid until $($Certificate.NotBefore.ToString('yyyy-MM-dd')). " +
                     "Clients normally refuse it until then.$metadata") `
            -RemediationHint $replaceHint -Remediation $importCommand
    }

    # A warning, never a stop. A certificate whose SAN does not name this host is wrong far more
    # often than it is deliberate - but behind a reverse proxy, or on a host reached under an
    # alias, it is exactly right, and refusing the install would be refusing a valid setup.
    if (-not (Test-NodePilotCertificateNameMatch -Names $names -PublicHostname $PublicHostname)) {
        return New-NodePilotPreflightResult -Id 'certificate' -Title $title -Status 'Warn' -Required $true `
            -Detail "Certificate $($Certificate.Subject) does not cover $PublicHostname.$metadata" `
            -RemediationHint ('Browsers will show a name mismatch unless something in front of NodePilot ' +
                              "terminates TLS under that name. Either use a certificate naming " +
                              "$PublicHostname, or set the public host name to one the certificate covers.") `
            -Remediation $importCommand
    }

    New-NodePilotPreflightResult -Id 'certificate' -Title $title -Status 'Pass' -Required $true `
        -Detail "Certificate $($Certificate.Subject) is available.$metadata"
}

function Get-NodePilotGmsaRetrievalState {
    <#
      May THIS host retrieve the gMSA's managed password? Answered over plain LDAP, so it works on
      a server without RSAT - which is most of them.

      The permission lives in the account's msDS-GroupMSAMembership, a binary security descriptor.
      Whoever is allowed is named by SID, usually a group, so the comparison is against this
      computer's own SID plus its transitive group SIDs from the constructed tokenGroups attribute.

      Read-only, and never throws: an unreachable directory is "could not tell", not a failure.
    #>
    param([Parameter(Mandatory)][string]$Sam)

    $unknown = { param([string]$Why) [pscustomobject]@{ Determined = $false; Allowed = $false; Detail = $Why } }
    try {
        $rootDse = New-Object System.DirectoryServices.DirectoryEntry('LDAP://RootDSE')
        $namingContext = [string]$rootDse.Properties['defaultNamingContext'].Value
        if ([string]::IsNullOrWhiteSpace($namingContext)) { return & $unknown 'no directory could be reached' }
        $root = New-Object System.DirectoryServices.DirectoryEntry("LDAP://$namingContext")

        $accountSearch = New-Object System.DirectoryServices.DirectorySearcher($root,
            "(&(objectClass=msDS-GroupManagedServiceAccount)(sAMAccountName=$Sam`$))")
        [void]$accountSearch.PropertiesToLoad.Add('msDS-GroupMSAMembership')
        $account = $accountSearch.FindOne()
        if (-not $account) { return & $unknown "no gMSA named '$Sam' was found in the directory" }
        if (-not $account.Properties['msds-groupmsamembership'].Count) {
            return [pscustomobject]@{ Determined = $true; Allowed = $false
                                      Detail = 'the account names nobody who may retrieve its password' }
        }

        $descriptor = New-Object System.DirectoryServices.ActiveDirectorySecurity
        $descriptor.SetSecurityDescriptorBinaryForm([byte[]]$account.Properties['msds-groupmsamembership'][0])
        $allowed = @($descriptor.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]) |
            Where-Object { $_.AccessControlType -eq 'Allow' } |
            ForEach-Object { [string]$_.IdentityReference.Value })
        if ($allowed.Count -eq 0) {
            return [pscustomobject]@{ Determined = $true; Allowed = $false
                                      Detail = 'the account names nobody who may retrieve its password' }
        }

        $computerSearch = New-Object System.DirectoryServices.DirectorySearcher($root,
            "(&(objectClass=computer)(sAMAccountName=$env:COMPUTERNAME`$))")
        $computer = $computerSearch.FindOne()
        if (-not $computer) { return & $unknown "this computer account was not found in the directory" }
        $mine = @([System.Security.Principal.SecurityIdentifier]::new(
            [byte[]]$computer.Properties['objectsid'][0], 0).Value)
        # tokenGroups is constructed: it has to be asked for on the object itself, and it resolves
        # nested groups, which a memberOf read would not.
        $entry = $computer.GetDirectoryEntry()
        $entry.RefreshCache(@('tokenGroups'))
        foreach ($sidBytes in $entry.Properties['tokenGroups']) {
            $mine += (New-Object System.Security.Principal.SecurityIdentifier([byte[]]$sidBytes, 0)).Value
        }

        $match = @($mine | Where-Object { $allowed -contains $_ })
        return [pscustomobject]@{ Determined = $true; Allowed = ($match.Count -gt 0); Detail = '' }
    }
    catch { return & $unknown $_.Exception.Message }
}

function New-NodePilotGmsaRetrievalResult {
    <#
      The verdict for the LDAP answer, separated from the directory call so every branch is
      reachable from a test host with no domain.

      "This host may not retrieve the password" is the one gMSA finding that is worth stopping for:
      the SCM will refuse the logon, the service will not start, and the installation will roll
      back. Everything else stays a warning, because not knowing is not evidence.
    #>
    param(
        [Parameter(Mandatory)][string]$Sam,
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)][string]$Title
    )

    $notBlocking = ' This does not block the installation.'
    if (-not $State.Determined) {
        $why = if ($State.Detail) { " ($($State.Detail))" } else { '' }
        return New-NodePilotPreflightResult -Id 'gmsa' -Title $Title -Status 'Warn' `
            -Detail ("Could not verify '$Sam'$why, so the account was not checked either way." +
                     $notBlocking) `
            -RemediationHint 'Install RSAT-AD-PowerShell to have the setup check the account, or verify it by hand.' `
            -Remediation 'Install-WindowsFeature RSAT-AD-PowerShell'
    }
    if (-not $State.Allowed) {
        return New-NodePilotPreflightResult -Id 'gmsa' -Title $Title -Status 'Fail' -Required $true `
            -Detail ("$env:COMPUTERNAME is not allowed to retrieve the managed password of '$Sam'" +
                     ($(if ($State.Detail) { " - $($State.Detail)" } else { '' })) +
                     '. The service would be registered and then refuse to start with a logon failure.') `
            -RemediationHint ('Allow this host to retrieve the password, then RESTART it - a computer ' +
                              "account's group membership only reaches the machine through a new " +
                              'Kerberos ticket. As Domain Admin:') `
            -Remediation ("Set-ADServiceAccount -Identity $Sam -PrincipalsAllowedToRetrieveManagedPassword " +
                          "(Get-ADComputer '$env:COMPUTERNAME')") `
            -AbortMessage "$env:COMPUTERNAME may not retrieve the managed password of '$Sam'."
    }
    New-NodePilotPreflightResult -Id 'gmsa' -Title $Title -Status 'Pass' `
        -Detail "$env:COMPUTERNAME may retrieve the managed password of '$Sam'."
}
function Test-NodePilotGmsa {
    <#
      Best-effort by design: the ActiveDirectory module may be absent (RSAT not installed) and
      that must not stop an install. Every outcome here is a warning, never an abort.

      Three of them, because they call for three different answers. "RSAT is missing" and "the
      cmdlet could not ask" are not evidence that the account is unusable, and recommending
      Install-ADServiceAccount for either sends the operator to repair something that is not broken.
    #>
    param([Parameter(Mandatory)][string]$ServiceAccount)

    $title = 'Group managed service account'
    # Said in every outcome. The row is amber and cannot hold "Next" back, but an operator reading
    # "not installed" next to a red row assumes it is the red one.
    $notBlocking = ' This does not block the installation.'
    $sam = $ServiceAccount
    if ($sam -like '*\*') { $sam = $sam.Split('\')[-1] }
    $sam = $sam.TrimEnd('$')

    if (-not (Get-Module -ListAvailable -Name ActiveDirectory)) {
        # RSAT is missing on most freshly built servers, and "could not check" let an install run
        # all the way to a service that cannot log on and a rollback. The one question that decides
        # that - may this host fetch the managed password - is answerable over plain LDAP.
        $retrieval = Get-NodePilotGmsaRetrievalState -Sam $sam
        $verdict = New-NodePilotGmsaRetrievalResult -Sam $sam -State $retrieval -Title $title
        if ($verdict.Status -ne 'Fail') {
            $verdict.Status = 'Warn'
            $verdict.Detail += ' AD PowerShell tools are missing; local readiness has not been tested.'
            $verdict.CanAutoFix = $true
            $verdict.AutoFixLabel = 'Prepare this gMSA locally (install AD tools if needed)'
        }
        return $verdict
    }

    try {
        Import-Module ActiveDirectory -ErrorAction Stop
        # Test-ADServiceAccount takes the short SAM name (without domain, without $).
        $installed = Test-ADServiceAccount -Identity $sam -ErrorAction Stop
    } catch {
        return New-NodePilotPreflightResult -Id 'gmsa' -Title $title -Status 'Warn' `
            -Detail "Could not verify '$sam': $($_.Exception.Message)$notBlocking" `
            -RemediationHint 'The account was not checked either way. Confirm it on this host before the service is started:' `
            -Remediation "Test-ADServiceAccount -Identity $sam"
    }

    if (-not $installed) {
        # The one outcome that is evidence, and so the only one allowed to recommend a fix for it.
        # Test-ADServiceAccount asks whether THIS host can use the account now, which is not the
        # same question the SCM answers when it starts a service, so a service already running
        # under the account does not contradict a false here.
        return New-NodePilotPreflightResult -Id 'gmsa' -Title $title -Status 'Warn' `
            -Detail ("Test-ADServiceAccount reports that '$sam' is not installed on this host. If " +
                     'services already run under it, check the account rather than assuming it is ' +
                     "broken - the two do not ask the same question.$notBlocking") `
            -RemediationHint 'If the account really was never installed here, as Domain Admin:' `
            -Remediation "Install-ADServiceAccount -Identity $sam" `
            -CanAutoFix $true -AutoFixLabel 'Prepare this gMSA locally (install AD tools if needed)'
    }

    New-NodePilotPreflightResult -Id 'gmsa' -Title $title -Status 'Pass' `
        -Detail "gMSA '$sam' is installed on this computer."
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
            -Detail 'This computer is not domain-joined; the Domain-profile firewall rules will apply to no active profile.' `
            -RemediationHint 'Open the HTTPS port for the active profile yourself after the install.' `
            -Remediation 'New-NetFirewallRule -DisplayName "NodePilot HTTPS" -Direction Inbound -Protocol TCP -LocalPort <port> -Action Allow -Profile Private'
    }
    New-NodePilotPreflightResult -Id 'domainJoined' -Title $title -Status 'Pass' `
        -Detail 'This computer is domain-joined; the Domain-profile firewall rules will apply.'
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

    $title = 'SQL Server connection'
    $connectionString = Resolve-NodePilotSqlProbeConnectionString `
        -Server $Server -Database $Database -CertificateHostName $CertificateHostName

    $conn = New-Object System.Data.SqlClient.SqlConnection $connectionString
    try {
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = 'SELECT 1'
        [void]$cmd.ExecuteScalar()
    } catch {
        # PowerShell wraps Open() failures. Stop at SqlException to retain its SQL error number.
        $failure = $_.Exception
        while ($failure.InnerException -and $failure.GetType().FullName -ne 'System.Data.SqlClient.SqlException') {
            $failure = $failure.InnerException
        }
        $sqlNumber = if ($failure.GetType().FullName -eq 'System.Data.SqlClient.SqlException') { $failure.Number } else { 0 }
        $canPrepare = $sqlNumber -eq 4060
        $detail = "Could not connect to SQL Server '$Server', database '$Database'."
        $remediation = ''
        $fixLabel = ''
        if ($canPrepare) {
            $detail = "SQL Server answered, but database '$Database' is missing or inaccessible to the current Windows account."
            $hint = 'Check the database name and account access. Database preparation requires SQL Server administrator rights. Alternatively, ask your DBA to run the SQL below.'
            $remediation = Get-NodePilotSqlRemediationScript -Principal $Principal -Database $Database
            $fixLabel = 'Prepare the service login and database (requires SQL Server administrator rights)'
        } elseif ($sqlNumber -eq 18456) {
            $hint = 'Ask your DBA to grant the current Windows account access to SQL Server, then check again.'
        } else {
            $hint = 'Check that the SQL Server instance and port are reachable and that this computer trusts its TLS certificate, then check again.'
        }
        return New-NodePilotPreflightResult -Id 'database' -Title $title -Status 'Fail' -Required $true `
            -Detail "$detail`r`nSQL Server reported: $($failure.Message)" `
            -RemediationHint $hint `
            -Remediation $remediation `
            -CanAutoFix $canPrepare -AutoFixDefault $canPrepare `
            -AutoFixLabel $fixLabel `
            -AbortMessage 'Aborted: SQL pre-flight failed.'
    } finally {
        $conn.Dispose()
    }

    New-NodePilotPreflightResult -Id 'database' -Title $title -Status 'Pass' -Required $true `
        -Detail "Connected to SQL Server '$Server', database '$Database', using the current Windows account."
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

function Test-NodePilotSqlServerIsLocal {
    <#
      True when the SQL Server named in the connection runs on this machine. Compares the host part
      against the local names and against every address of this machine's interfaces.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param([Parameter(Mandatory)][string]$SqlServer)

    $hostPart = (($SqlServer.Trim() -replace '^(tcp|np|lpc):', '') -split '[\\,]')[0].Trim()
    if ($hostPart -in @('.', '(local)', 'localhost', '127.0.0.1', '::1') -or $hostPart -ieq $env:COMPUTERNAME) {
        return $true
    }
    try { $resolved = [System.Net.Dns]::GetHostAddresses($hostPart) } catch { return $false }
    # Compared by bytes: a link-local address from DNS carries no scope id, the interface's does.
    $local = @([System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() |
        ForEach-Object { $_.GetIPProperties().UnicastAddresses } |
        ForEach-Object { [BitConverter]::ToString($_.Address.GetAddressBytes()) })
    foreach ($address in $resolved) {
        if ([System.Net.IPAddress]::IsLoopback($address)) { return $true }
        if ($local -contains [BitConverter]::ToString($address.GetAddressBytes())) { return $true }
    }
    return $false
}

function Get-NodePilotLocalSystemSqlPrincipal {
    <#
      The login a LocalSystem service presents to SQL Server. To another host it is the computer
      account; to a SQL Server on this machine Windows authenticates it as SYSTEM itself.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([string]$SqlServer)

    if ($SqlServer -and (Test-NodePilotSqlServerIsLocal -SqlServer $SqlServer)) {
        # Translated from the SID: the account name is localized on non-English Windows.
        return (New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-18').Translate(
            [System.Security.Principal.NTAccount]).Value
    }
    return "$env:USERDOMAIN\$env:COMPUTERNAME`$"
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
        [bool]$CanProvision = $false,
        [AllowEmptyString()][string]$RevocationProblem = ''
    )

    $title = 'PostgreSQL connection'

    if (-not $TcpReachable) {
        $suffix = if ($TcpError) { ": $TcpError" } else { '.' }
        return New-NodePilotPreflightResult -Id 'database' -Title $title -Status 'Fail' -Required $true `
            -Detail "The PostgreSQL TCP connection to ${HostName}:${Port} failed$suffix" `
            -RemediationHint ('Check the host and port on the PostgreSQL - server page. Verify DNS, firewall rules, ' +
                              'and that PostgreSQL is running and listening on that address and port. Then check again.') `
            -AbortMessage 'Aborted: Postgres pre-flight failed.'
    }

    # The service checks revocation and refuses the server whatever the login check says.
    if ($RevocationProblem) {
        $revocationHint = if ($RevocationProblem -match ' is revoked$') {
            'Replace the revoked PostgreSQL server certificate with a valid certificate issued by your CA, then check again.'
        } else {
            "Ask your CA administrator to publish a current CRL at the certificate's CRL distribution point " +
            'and make it reachable from this host. Alternatively, import that CRL in an elevated PowerShell window: ' +
            "certutil -addstore CA 'C:\path\issuer.crl'. Then check again."
        }
        return New-NodePilotPreflightResult -Id 'database' -Title $title -Status 'Fail' -Required $true `
            -Detail ("PostgreSQL answered on ${HostName}:${Port}, but $RevocationProblem. The service " +
                     'checks certificate revocation and would refuse this server at start.') `
            -RemediationHint $revocationHint `
            -AbortMessage 'Aborted: Postgres pre-flight failed - certificate revocation validation failed.'
    }

    # A TCP response alone does not verify authentication or TLS.
    if ($null -eq $PsqlOutcome) {
        return New-NodePilotPreflightResult -Id 'database' -Title $title -Status 'Warn' -Required $true `
            -Detail ("PostgreSQL responds on ${HostName}:${Port}, but the login for '$User' to [$Database] " +
                     'is untested because setup could not run the authenticated check.') `
            -RemediationHint ('Check the role password and root certificate file on the PostgreSQL - credentials page. ' +
                              'Use a setup package with the bundled PostgreSQL client, then check again. ' +
                              'If a login check is still unavailable, ask your database administrator to verify the connection before continuing.')
    }
    if ($PsqlOutcome.Succeeded) {
        return New-NodePilotPreflightResult -Id 'database' -Title $title -Status 'Pass' -Required $true `
            -Detail "PostgreSQL is reachable and '$User' can log in to [$Database] on ${HostName}:${Port}."
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
        $objects = if ($RoleExists -eq $false -and $DatabaseExists -eq $false) { 'role and database' }
                   elseif ($RoleExists -eq $false) { 'role' } else { 'database' }
        $label = if ($CanProvision) { "Create the missing $objects on $HostName" } else { '' }
        $hint = if ($CanProvision) {
            'Select this fix and choose Apply selected fixes. Alternatively, ask your database administrator to run the instructions below, then check again.'
        } else {
            'Ask your database administrator to run the instructions below, or provide administrator credentials on the PostgreSQL - credentials page. Then check again.'
        }
        $remediation = Get-NodePilotPostgresRemediationScript -User $User -Database $Database `
            -CreateRole ($RoleExists -eq $false) -CreateDatabase ($DatabaseExists -eq $false)
        return New-NodePilotPreflightResult -Id 'database' -Title $title -Status 'Fail' -Required $true `
            -Detail ("PostgreSQL answered on ${HostName}:${Port} but $($missing -join ' and '). " +
                     'NodePilot cannot start with this database configuration.') `
            -RemediationHint $hint -Remediation $remediation `
            -CanAutoFix $CanProvision -AutoFixLabel $label `
            -AbortMessage "Aborted: Postgres pre-flight failed - $($missing -join ' and ')."
    }

    if ($null -ne $RoleExists -and $null -ne $DatabaseExists) {
        # Both there, still refused. Creating them again would change nothing, and the fix
        # deliberately never rewrites an existing role's password - so no button.
        return New-NodePilotPreflightResult -Id 'database' -Title $title -Status 'Fail' -Required $true `
            -Detail ("PostgreSQL answered on ${HostName}:${Port} and both the role '$User' and the " +
                     "database [$Database] exist, but the login was refused: $psqlError " +
                     'Setup will not recreate existing objects or reset their passwords.') `
            -RemediationHint ('Check the role password on the PostgreSQL - credentials page. Ask your database administrator ' +
                              'to check login permissions and pg_hba.conf. For a TLS error, check the host name and root certificate. Then check again.') `
            -AbortMessage 'Aborted: Postgres pre-flight failed - the login was refused.'
    }

    # A missing or failed administrator connection leaves the catalogue state unknown.
    New-NodePilotPreflightResult -Id 'database' -Title $title -Status 'Fail' -Required $true `
        -Detail ("PostgreSQL answered on ${HostName}:${Port} but '$User' could not log in to " +
                 "[$Database]: $psqlError Setup could not verify whether the role and database exist.") `
        -RemediationHint ('Check the role password on the PostgreSQL - credentials page. To verify missing objects, ' +
                          'provide valid administrator credentials there or ask your database administrator. ' +
                          'For a TLS error, check the host name and root certificate; for an access error, check pg_hba.conf and login permissions. Then check again.') `
        -AbortMessage 'Aborted: Postgres pre-flight failed - the login was refused.'
}

function Get-NodePilotCertificateRevocationProblem {
    <#
      Checks a server certificate's revocation status the way the service's Npgsql connection does
      (Check Certificate Revocation=true, online). psql does not check revocation, so without this
      a certificate whose CRL cannot be reached passes the login check and the service still
      refuses the server. Returns '' when revocation is fine, otherwise the reason.
    #>
    param(
        [Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Parameter(Mandatory)][string]$RootCertificatePath
    )

    # X509Chain on .NET Framework has no custom trust store, and CryptoAPI does not use a CRL whose
    # chain ends in an untrusted root. A chain engine with the given root as its exclusive root
    # trusts it for this one check, as Npgsql's custom trust store does, without touching a store.
    if (-not ('NodePilotRevocationProbe' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class NodePilotRevocationProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ChainEngineConfig
    {
        public uint cbSize;
        public IntPtr hRestrictedRoot, hRestrictedTrust, hRestrictedOther;
        public uint cAdditionalStore;
        public IntPtr rghAdditionalStore;
        public uint dwFlags, dwUrlRetrievalTimeout, MaximumCachedCertificates, CycleDetectionModulus;
        public IntPtr hExclusiveRoot, hExclusiveTrustedPeople;
        public uint dwExclusiveFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ChainPara
    {
        public uint cbSize, dwType, cUsageIdentifier;
        public IntPtr rgpszUsageIdentifier;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern IntPtr CertOpenStore(IntPtr provider, uint encoding, IntPtr cryptProv, uint flags, IntPtr para);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CertAddEncodedCertificateToStore(IntPtr store, uint encoding, byte[] encoded, uint length, uint disposition, IntPtr context);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CertCreateCertificateChainEngine(ref ChainEngineConfig config, out IntPtr engine);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CertGetCertificateChain(IntPtr engine, IntPtr certificate, IntPtr time, IntPtr additionalStore, ref ChainPara para, uint flags, IntPtr reserved, out IntPtr chainContext);
    [DllImport("crypt32.dll")] private static extern void CertFreeCertificateChain(IntPtr chainContext);
    [DllImport("crypt32.dll")] private static extern void CertFreeCertificateChainEngine(IntPtr engine);
    [DllImport("crypt32.dll")] private static extern bool CertCloseStore(IntPtr store, uint flags);

    // Returns the chain's CERT_TRUST_STATUS.dwErrorStatus with the root trusted exclusively.
    public static uint ErrorStatus(IntPtr certificate, byte[] root, uint timeoutMs)
    {
        IntPtr store = CertOpenStore(new IntPtr(2), 0, IntPtr.Zero, 0, IntPtr.Zero); // memory store
        if (store == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        IntPtr engine = IntPtr.Zero, context = IntPtr.Zero;
        try
        {
            if (!CertAddEncodedCertificateToStore(store, 0x10001, root, (uint)root.Length, 4, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception();
            var config = new ChainEngineConfig();
            config.cbSize = (uint)Marshal.SizeOf(typeof(ChainEngineConfig));
            config.dwUrlRetrievalTimeout = timeoutMs;
            config.hExclusiveRoot = store;
            if (!CertCreateCertificateChainEngine(ref config, out engine))
                throw new System.ComponentModel.Win32Exception();
            var para = new ChainPara();
            para.cbSize = (uint)Marshal.SizeOf(typeof(ChainPara));
            // CERT_CHAIN_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
            if (!CertGetCertificateChain(engine, certificate, IntPtr.Zero, IntPtr.Zero, ref para, 0x40000000, IntPtr.Zero, out context))
                throw new System.ComponentModel.Win32Exception();
            return (uint)Marshal.ReadInt32(context, 4); // CERT_CHAIN_CONTEXT.TrustStatus.dwErrorStatus
        }
        finally
        {
            if (context != IntPtr.Zero) CertFreeCertificateChain(context);
            if (engine != IntPtr.Zero) CertFreeCertificateChainEngine(engine);
            CertCloseStore(store, 0);
        }
    }
}
'@
    }

    $root = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $RootCertificatePath
    $status = [NodePilotRevocationProbe]::ErrorStatus($Certificate.Handle, $root.RawData, 15000)

    if ($status -band 0x4) {   # CERT_TRUST_IS_REVOKED
        return "the server certificate '$($Certificate.Subject)' is revoked"
    }
    # CERT_TRUST_REVOCATION_STATUS_UNKNOWN, CERT_TRUST_IS_OFFLINE_REVOCATION
    if (($status -band 0x40) -or ($status -band 0x1000000)) {
        return ("the revocation status of the server certificate '$($Certificate.Subject)' could not be " +
                'checked. No CRL for it was reachable from this host')
    }
    return ''
}

function Get-NodePilotPostgresServerCertificate {
    <#
      The certificate a PostgreSQL server presents, or $null. PostgreSQL negotiates TLS inside its
      own protocol: an 8-byte SSLRequest, a single 'S', then the handshake.
    #>
    param(
        [Parameter(Mandatory)][string]$HostName,
        [Parameter(Mandatory)][int]$Port
    )

    $tcp = New-Object System.Net.Sockets.TcpClient
    try {
        $connect = $tcp.BeginConnect($HostName, $Port, $null, $null)
        if (-not $connect.AsyncWaitHandle.WaitOne(5000)) { return $null }
        $tcp.EndConnect($connect)
        $stream = $tcp.GetStream()
        $stream.ReadTimeout = 5000
        $stream.WriteTimeout = 5000
        [byte[]]$sslRequest = 0, 0, 0, 8, 4, 210, 22, 47   # length 8, code 80877103
        $stream.Write($sslRequest, 0, $sslRequest.Length)
        if ($stream.ReadByte() -ne [int][char]'S') { return $null }

        $seen = @{}
        $callback = [System.Net.Security.RemoteCertificateValidationCallback]{
            param($source, $certificate, $chain, $policyErrors)
            # The bytes, not the object: its handle dies with the SslStream.
            $seen['raw'] = $certificate.GetRawCertData()
            return $true
        }
        $ssl = New-Object System.Net.Security.SslStream($stream, $false, $callback)
        try { $ssl.AuthenticateAsClient($HostName) } finally { $ssl.Dispose() }
        if (-not $seen['raw']) { return $null }
        return New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 (, [byte[]]$seen['raw'])
    }
    catch { return $null }
    finally { $tcp.Close() }
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

    $revocationProblem = ''
    if ($reachable -and -not [string]::IsNullOrWhiteSpace($RootCertificate) -and
        (Test-Path -LiteralPath $RootCertificate -PathType Leaf)) {
        # A probe that cannot run says nothing; it must never abort the pre-flight.
        try {
            $serverCertificate = Get-NodePilotPostgresServerCertificate -HostName $HostName -Port $Port
            if ($serverCertificate) {
                $revocationProblem = Get-NodePilotCertificateRevocationProblem `
                    -Certificate $serverCertificate -RootCertificatePath $RootCertificate
            }
        }
        catch { $revocationProblem = '' }
    }

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
        -RoleExists $roleExists -DatabaseExists $databaseExists -CanProvision $CanProvision `
        -RevocationProblem $revocationProblem
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
        # This only reports certificate host-name mismatches. Optional,
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
        # the thumbprint it was built against. Absent - the scripted path, which has no payload -
        # the
        # row is not emitted at all, exactly as the Postgres credentials degrade.
        [AllowEmptyString()][string]$ArtifactSignerCertificatePath = '',
        [AllowEmptyString()][string]$ExpectedSignerThumbprint = '',
        [switch]$SkipDatabaseCheck,
        [switch]$SkipGmsaCheck,
        [scriptblock]$ProgressCallback
    )

    function Write-PreflightStatus {
        param([string]$Message)
        if ($ProgressCallback) {
            try { $null = & $ProgressCallback $Message } catch { Write-Verbose 'The progress listener is unavailable.' }
        }
    }
    $results = @()
    if ($ArtifactSignerCertificatePath) {
        Write-PreflightStatus 'Checking the artifact publisher'
        $results += Test-NodePilotArtifactSignerTrust `
            -CertificatePath $ArtifactSignerCertificatePath -ExpectedThumbprint $ExpectedSignerThumbprint
    }
    Write-PreflightStatus 'Checking the .NET runtimes'
    $results += Test-NodePilotDotNetRuntime
    Write-PreflightStatus 'Checking the HTTPS certificate'
    $results += Test-NodePilotTlsCertificate -Thumbprint $CertificateThumbprint -PublicHostname $PublicHostname
    Write-PreflightStatus 'Checking HTTP and HTTPS ports'
    $results += Test-NodePilotListenPorts -HttpsPort $HttpsPort -HttpPort $HttpPort -ServiceName $ServiceName

    Write-PreflightStatus 'Checking the service identity'
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
    Write-PreflightStatus 'Checking domain membership'
    $results += Test-NodePilotDomainJoined

    if ($SkipDatabaseCheck) {
        $results += New-NodePilotPreflightResult -Id 'database' -Title 'Database reachable' -Status 'Skipped' `
            -Detail 'Database connectivity check skipped by -SkipSqlConnectivityCheck.'
        return $results
    }

    if ($DbProvider -eq 'sqlserver') {
        Write-PreflightStatus 'Connecting to SQL Server'
        $sqlResult = Test-NodePilotSqlReachable `
            -Server $SqlServer -Database $SqlDatabase `
            -CertificateHostName $SqlCertificateHostName -Principal $SqlPrincipal
        $results += $sqlResult
        # Only meaningful once the instance answered; on a failed connection the caller aborts
        # before it could act on either follow-up.
        if ($sqlResult.Status -eq 'Pass') {
            Write-PreflightStatus 'Checking SQL Server permissions and version'
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
        Write-PreflightStatus 'Checking the PostgreSQL login and TLS connection'
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
