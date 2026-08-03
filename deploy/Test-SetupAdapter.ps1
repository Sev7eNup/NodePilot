#requires -Version 5.1

<#
.SYNOPSIS
    Behavioural self-test of the setup answer-file contract.
.DESCRIPTION
    Runs without admin rights, without a database, and without touching the machine - the same
    posture as Test-ArtifactSecurity.ps1, so CI can run it under both Windows PowerShell 5.1 and
    PowerShell 7.

    Static text checks cannot cover this surface. A silent mis-splat - a Postgres key leaking into
    a SQL Server install, a password that fails to round-trip, an unknown key accepted instead of
    rejected - would look identical in the source and only surface during a real installation.
.PARAMETER SetupContractPath
    The contract under test. Defaults to deploy/SetupContract.ps1.
.PARAMETER PreflightPath
    Defaults to deploy/Preflight.ps1. Used for the report-do-not-throw case.
#>

[CmdletBinding()]
param(
    [string]$SetupContractPath,
    [string]$PreflightPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($SetupContractPath)) {
    $SetupContractPath = Join-Path $scriptDirectory 'SetupContract.ps1'
}
if ([string]::IsNullOrWhiteSpace($PreflightPath)) {
    $PreflightPath = Join-Path $scriptDirectory 'Preflight.ps1'
}
foreach ($path in @($SetupContractPath, $PreflightPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Setup adapter check failed: missing file '$path'."
    }
}

. $SetupContractPath
. $PreflightPath

$script:Passed = 0

function Assert-True {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][bool]$Condition)
    if (-not $Condition) { throw "Setup adapter check failed: $Name" }
    $script:Passed++
}

function Assert-Throws {
    <#
      Asserts both that it throws AND that the message names the offending key. "It failed" is not
      good enough for an unattended answer file: the operator needs to be told which key.
    #>
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$MessagePattern
    )
    $message = $null
    try { & $Action }
    catch { $message = $_.Exception.Message }
    if ($null -eq $message) { throw "Setup adapter check failed: $Name (nothing was thrown)" }
    if ($message -notmatch $MessagePattern) {
        throw "Setup adapter check failed: $Name (message '$message' does not match '$MessagePattern')"
    }
    $script:Passed++
}

$workingDirectory = Join-Path ([IO.Path]::GetTempPath()) ("nodepilot-setup-test-" + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $workingDirectory)

function New-AnswerFile {
    param([Parameter(Mandatory)][string]$Json, [string]$Name = 'answers.json')
    $path = Join-Path $workingDirectory $Name
    [IO.File]::WriteAllBytes($path, [Text.Encoding]::UTF8.GetBytes($Json))
    return $path
}

try {
    # --- torture round-trip -------------------------------------------------------------------
    # UNC paths, embedded quotes, umlauts, dollar signs and template braces all appear in real
    # answers, and every one of them is a plausible way for the Pascal-side JSON writer to be
    # wrong. A 200-character password covers the SecureString path at length.
    $password = ('A1!"$%&' + ('x' * 190) + '{{end}}')
    $torture = @{
        schemaVersion = 1
        mode          = 'install'
        installPath   = 'C:\Program Files\NodePilot "Prod"'
        dataPath      = '\\fileserver\share$\NodePilot'
        serviceName   = 'NodePilot'
        identity      = @{ type = 'gmsa'; account = 'CONTOSO\svc-nodepilot$' }
        database      = @{
            provider                = 'postgres'
            postgresHost            = 'pg.contoso.local'
            postgresPort            = 5433
            postgresDatabase        = 'nodepilot'
            postgresUser            = 'np_user'
            postgresPassword        = $password
            postgresRootCertificate = 'C:\certs\root-ca.pem'
        }
        network       = @{
            publicHostname = 'nodepilot.contoso.local'
            httpsPort      = 8443
            httpPort       = 0
            allowedHosts   = 'nodepilot.contoso.local;localhost'
            knownProxyIps  = @('10.0.1.5', '2001:db8::5')
        }
        certificate   = @{ thumbprint = 'A1B2C3D4E5F600112233445566778899AABBCCDD'; source = 'existing' }
    } | ConvertTo-Json -Depth 6

    $tortureFile = New-AnswerFile -Json $torture
    $answers = Read-NodePilotAnswerFile -Path $tortureFile
    Assert-True -Name 'quoted install path round-trips' `
        -Condition ($answers['installPath'] -eq 'C:\Program Files\NodePilot "Prod"')
    Assert-True -Name 'UNC data path round-trips' `
        -Condition ($answers['dataPath'] -eq '\\fileserver\share$\NodePilot')
    Assert-True -Name 'gMSA account with a trailing dollar round-trips' `
        -Condition ($answers['identity.account'] -eq 'CONTOSO\svc-nodepilot$')
    Assert-True -Name 'password with quotes, dollars and braces round-trips' `
        -Condition ([string]$answers['database.postgresPassword'] -eq $password)
    Assert-True -Name 'proxy list stays a list, not a nested object' `
        -Condition (@($answers['network.knownProxyIps']).Count -eq 2)
    Assert-True -Name 'a zero HTTP port survives as zero rather than being dropped' `
        -Condition ([int]$answers['network.httpPort'] -eq 0)

    # --- schema enforcement -------------------------------------------------------------------
    Assert-Throws -Name 'an unknown key is rejected by name' -MessagePattern "unknown key 'network\.htpsPort'" -Action {
        Read-NodePilotAnswerFile -Path (New-AnswerFile -Name 'unknown.json' -Json (@{
            schemaVersion = 1; mode = 'install'; installPath = 'C:\np'; dataPath = 'C:\npdata'
            serviceName = 'NodePilot'; identity = @{ type = 'localSystem' }
            database = @{ provider = 'sqlserver'; sqlServer = 'db'; sqlDatabase = 'NodePilot' }
            network = @{ publicHostname = 'h'; httpsPort = 443; httpPort = 80; htpsPort = 443 }
            certificate = @{ thumbprint = 'A' * 40 }
        } | ConvertTo-Json -Depth 6))
    }

    Assert-Throws -Name 'a missing required key is named' -MessagePattern "missing required key 'certificate\.thumbprint'" -Action {
        Read-NodePilotAnswerFile -Path (New-AnswerFile -Name 'missing.json' -Json (@{
            schemaVersion = 1; mode = 'install'; installPath = 'C:\np'; dataPath = 'C:\npdata'
            serviceName = 'NodePilot'; identity = @{ type = 'localSystem' }
            database = @{ provider = 'sqlserver'; sqlServer = 'db'; sqlDatabase = 'NodePilot' }
            network = @{ publicHostname = 'h'; httpsPort = 443; httpPort = 80 }
            certificate = @{ source = 'existing' }
        } | ConvertTo-Json -Depth 6))
    }

    Assert-Throws -Name 'an unsupported schemaVersion is rejected' -MessagePattern 'schemaVersion 2 is not supported' -Action {
        Read-NodePilotAnswerFile -Path (New-AnswerFile -Name 'version.json' -Json '{"schemaVersion":2,"mode":"install"}')
    }

    Assert-Throws -Name 'malformed JSON is reported as such' -MessagePattern 'not valid JSON' -Action {
        Read-NodePilotAnswerFile -Path (New-AnswerFile -Name 'broken.json' -Json '{"schemaVersion":1,')
    }

    Assert-Throws -Name 'a gMSA identity without an account is rejected' -MessagePattern "needs 'identity\.account'" -Action {
        Read-NodePilotAnswerFile -Path (New-AnswerFile -Name 'gmsa.json' -Json (@{
            schemaVersion = 1; mode = 'install'; installPath = 'C:\np'; dataPath = 'C:\npdata'
            serviceName = 'NodePilot'; identity = @{ type = 'gmsa' }
            database = @{ provider = 'sqlserver'; sqlServer = 'db'; sqlDatabase = 'NodePilot' }
            network = @{ publicHostname = 'h'; httpsPort = 443; httpPort = 80 }
            certificate = @{ thumbprint = 'A' * 40 }
        } | ConvertTo-Json -Depth 6))
    }

    Assert-Throws -Name 'an install-mode key in an update answer file is rejected' -MessagePattern "unknown key 'certificate\.thumbprint'" -Action {
        Read-NodePilotAnswerFile -Path (New-AnswerFile -Name 'update-extra.json' -Json (@{
            schemaVersion = 1; mode = 'update'; installPath = 'C:\np'; serviceName = 'NodePilot'
            certificate = @{ thumbprint = 'A' * 40 }
        } | ConvertTo-Json -Depth 6))
    }

    # --- splat mapping ------------------------------------------------------------------------
    # The single place provider bleed can happen. Passing -PostgresHost alongside -DbProvider
    # sqlserver binds without complaint and then fails confusingly much later.
    $postgresSplat = ConvertTo-NodePilotInstallParameters -Answers $answers
    Assert-True -Name 'a Postgres install passes no SQL Server parameters' `
        -Condition (-not ($postgresSplat.Keys | Where-Object { $_ -like 'Sql*' }))
    Assert-True -Name 'a Postgres install passes the password as a SecureString' `
        -Condition ($postgresSplat['PostgresPassword'] -is [System.Security.SecureString])
    Assert-True -Name 'a gMSA install passes -ServiceAccount and not -UseLocalSystem' `
        -Condition ($postgresSplat.Contains('ServiceAccount') -and -not $postgresSplat.Contains('UseLocalSystem'))
    Assert-True -Name 'a non-default Postgres port is carried through' `
        -Condition ([int]$postgresSplat['PostgresPort'] -eq 5433)

    $sqlAnswers = Read-NodePilotAnswerFile -Path (New-AnswerFile -Name 'sql.json' -Json (@{
        schemaVersion = 1; mode = 'install'; installPath = 'C:\np'; dataPath = 'C:\npdata'
        serviceName = 'NodePilot'; identity = @{ type = 'localSystem' }
        database = @{ provider = 'sqlserver'; sqlServer = 'tcp:db.contoso.local'; sqlDatabase = 'NodePilot' }
        network = @{ publicHostname = 'h'; httpsPort = 443; httpPort = 80 }
        certificate = @{ thumbprint = 'B' * 40 }
    } | ConvertTo-Json -Depth 6))
    $sqlSplat = ConvertTo-NodePilotInstallParameters -Answers $sqlAnswers
    Assert-True -Name 'a SQL Server install passes no Postgres parameters' `
        -Condition (-not ($sqlSplat.Keys | Where-Object { $_ -like 'Postgres*' }))
    Assert-True -Name 'a LocalSystem install passes -UseLocalSystem and not -ServiceAccount' `
        -Condition ($sqlSplat['UseLocalSystem'] -eq $true -and -not $sqlSplat.Contains('ServiceAccount'))
    Assert-True -Name 'a blank certificate host name is omitted so the installer derives it' `
        -Condition (-not $sqlSplat.Contains('SqlCertificateHostName'))
    Assert-True -Name 'optional skips are absent unless asked for' `
        -Condition (-not $sqlSplat.Contains('SkipGmsaCheck') -and -not $sqlSplat.Contains('SkipSqlConnectivityCheck'))

    # --- SecureString handling ----------------------------------------------------------------
    $plain = 'correct horse battery staple'
    $secure = ConvertTo-NodePilotSecureString -PlainText $plain
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $recovered = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    Assert-True -Name 'the SecureString round-trips exactly' -Condition ($recovered -eq $plain)
    Assert-True -Name 'the SecureString is read-only' -Condition ($secure.IsReadOnly())

    # --- answer file destruction --------------------------------------------------------------
    $doomed = New-AnswerFile -Name 'doomed.json' -Json '{"schemaVersion":1,"mode":"update","installPath":"C:\\np","serviceName":"NodePilot"}'
    Remove-NodePilotAnswerFile -Path $doomed
    Assert-True -Name 'the answer file is gone after shredding' -Condition (-not (Test-Path -LiteralPath $doomed))
    Remove-NodePilotAnswerFile -Path $doomed  # must be idempotent; the finally block may run twice
    Assert-True -Name 'shredding a missing answer file is a no-op' -Condition ($true)

    # --- INI result buffer --------------------------------------------------------------------
    # Inno reads these with GetIniString, which cannot see a value containing a newline.
    $buffer = New-NodePilotResultBuffer
    Set-NodePilotResult -Buffer $buffer -Section 'check.database' -Name 'remediation' `
        -Value ("CREATE LOGIN [x];`r`nCREATE DATABASE [y];")
    $iniPath = Join-Path $workingDirectory 'result.ini'
    Write-NodePilotResultFile -Buffer $buffer -Path $iniPath
    $ini = Get-Content -LiteralPath $iniPath
    Assert-True -Name 'multi-line remediation is escaped onto a single INI line' `
        -Condition (($ini | Where-Object { $_ -like 'remediation=*' }) -eq 'remediation=CREATE LOGIN [x];\nCREATE DATABASE [y];')
    Assert-True -Name 'the INI carries its section header' `
        -Condition ($ini -contains '[check.database]')

    # --- the two-layer pre-flight split -------------------------------------------------------
    # The point of the whole split: collecting must report, only asserting may abort. .invalid is
    # reserved by RFC 2606, so DNS fails immediately instead of burning the connect timeout.
    $checks = @(Invoke-NodePilotPreflight `
        -CertificateThumbprint ('0' * 40) `
        -DbProvider 'sqlserver' `
        -IsLocalSystem $true `
        -ComputerAccount 'CONTOSO\HOST$' `
        -SqlPrincipal 'CONTOSO\HOST$' `
        -SqlServer 'nodepilot-unreachable.invalid' `
        -SqlDatabase 'NodePilot' `
        -SqlCertificateHostName 'nodepilot-unreachable.invalid' `
        -ServiceName 'NodePilotDoesNotExist')
    Assert-True -Name 'collecting pre-flight results never throws' -Condition ($checks.Count -gt 0)
    Assert-True -Name 'an unreachable database is reported as a required failure' `
        -Condition (@($checks | Where-Object { $_.Id -eq 'database' -and $_.Status -eq 'Fail' -and $_.Required }).Count -eq 1)
    Assert-True -Name 'a failed check still carries a remediation snippet' `
        -Condition (@($checks | Where-Object { $_.Id -eq 'database' })[0].Remediation -match 'CREATE LOGIN')
    Assert-Throws -Name 'asserting the same results does abort' -MessagePattern 'Aborted|not found|not present' -Action {
        Assert-NodePilotPreflight -Results $checks | Out-Null
    }
}
finally {
    if (Test-Path -LiteralPath $workingDirectory) {
        Remove-Item -LiteralPath $workingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Setup adapter checks passed ($script:Passed assertions)." -ForegroundColor Green
