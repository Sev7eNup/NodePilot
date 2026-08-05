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
    [string]$PreflightPath,
    [string]$ServiceControlPath,
    [string]$SetupAdapterPath,
    [string]$ArtifactSecurityPath
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
if ([string]::IsNullOrWhiteSpace($ServiceControlPath)) {
    $ServiceControlPath = Join-Path $scriptDirectory 'ServiceControl.ps1'
}
# Run as a process rather than dot-sourced: it takes a mandatory -Mode and ends in `exit`.
if ([string]::IsNullOrWhiteSpace($SetupAdapterPath)) {
    $SetupAdapterPath = Join-Path $scriptDirectory 'Invoke-NodePilotSetup.ps1'
}
# Loaded because the adapter loads it: the CSPRNG behind the generated bootstrap password and the
# ACL-protected credential writer both live there. Testing the contract without it would test a
# composition that does not exist in production.
if ([string]::IsNullOrWhiteSpace($ArtifactSecurityPath)) {
    $ArtifactSecurityPath = Join-Path $scriptDirectory 'ArtifactSecurity.ps1'
}
foreach ($path in @($SetupContractPath, $PreflightPath, $ServiceControlPath, $SetupAdapterPath, $ArtifactSecurityPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Setup adapter check failed: missing file '$path'."
    }
}

. $SetupContractPath
. $PreflightPath
. $ServiceControlPath
. $ArtifactSecurityPath

# ServiceControl.ps1 logs through the host script's writers. The real callers define these; the
# harness has to stand in for them or the -Force path throws on its first warning.
function Write-Info { param([string]$Text) Write-Verbose $Text }
function Write-Warn { param([string]$Text) Write-Verbose $Text }

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

    # --- byte-order mark ----------------------------------------------------------------------
    # The wizard writes its answer file with Inno's SaveStringsToUTF8File, which emits a BOM, and
    # an operator hand-writing one in Notepad gets the same. UTF8.GetString turns those three bytes
    # into U+FEFF, which is neither whitespace nor a JSON token, so the whole document is rejected
    # with "Invalid JSON primitive: ." - and that is exactly how the first interactive run of the
    # wizard died. The unattended path never caught it because it copies a supplied file.
    $bomPath = Join-Path $workingDirectory 'bom.json'
    $bomJson = @{
        schemaVersion = 1; mode = 'install'; installPath = 'C:\np'; dataPath = 'C:\npdata'
        serviceName = 'NodePilot'; identity = @{ type = 'gmsa'; account = 'CORP\svc$' }
        database = @{ provider = 'sqlserver'; sqlServer = 'db'; sqlDatabase = 'NodePilot' }
        network = @{ publicHostname = 'h'; httpsPort = 8443; httpPort = 0 }
        certificate = @{ thumbprint = 'C' * 40 }
    } | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllBytes($bomPath,
        ([byte[]](0xEF, 0xBB, 0xBF)) + [Text.Encoding]::UTF8.GetBytes($bomJson))
    $bomAnswers = Read-NodePilotAnswerFile -Path $bomPath
    Assert-True -Name 'an answer file with a UTF-8 BOM is accepted' `
        -Condition ($bomAnswers['serviceName'] -eq 'NodePilot')
    Assert-True -Name 'the BOM does not leak into the first value' `
        -Condition ([int][char]([string]$bomAnswers['mode'])[0] -eq [int][char]'i')

    # --- the document the WIZARD actually produces ---------------------------------------------
    # Verbatim capture from a real interactive run, BOM and Pascal's own formatting included. The
    # unattended path copies an operator-supplied file and therefore never exercises the wizard's
    # JSON writer at all - which is precisely how a BOM reached production untested. Reproducing
    # the shape by hand would only test my idea of it; this is the bytes it really wrote.
    $wizardJson = @'
{
  "schemaVersion": 1,
  "mode": "install",
  "installPath": "C:\\Program Files\\NodePilot",
  "dataPath": "C:\\ProgramData\\NodePilot",
  "serviceName": "NodePilot",
  "identity": {
    "type": "gmsa",
    "account": "corp\\q-sdvorch2$"
  },
  "database": {
    "provider": "sqlserver",
    "sqlServer": "cm1.corp.contoso.com",
    "sqlDatabase": "NodePilot",
    "sqlCertificateHostName": ""
  },
  "network": {
    "publicHostname": "cm1.corp.contoso.com",
    "httpsPort": 8443,
    "httpPort": 0,
    "allowedHosts": "cm1.corp.contoso.com;cm1;localhost",
    "knownProxyIps": []
  },
  "certificate": {
    "thumbprint": "9457A38A58F741F80236AA8941C4E803ABDD48D1",
    "source": "existing"
  }
}
'@
    $wizardPath = Join-Path $workingDirectory 'wizard.json'
    [IO.File]::WriteAllBytes($wizardPath,
        ([byte[]](0xEF, 0xBB, 0xBF)) + [Text.Encoding]::UTF8.GetBytes($wizardJson))
    $wizardAnswers = Read-NodePilotAnswerFile -Path $wizardPath
    Assert-True -Name 'the wizard-produced answer file parses' `
        -Condition ($wizardAnswers['network.publicHostname'] -eq 'cm1.corp.contoso.com')
    Assert-True -Name 'a gMSA name with a trailing dollar survives the wizard escaping' `
        -Condition ($wizardAnswers['identity.account'] -eq 'corp\q-sdvorch2$')
    Assert-True -Name 'a blank optional field stays blank rather than becoming a literal' `
        -Condition ([string]$wizardAnswers['database.sqlCertificateHostName'] -eq '')
    $wizardSplat = ConvertTo-NodePilotInstallParameters -Answers $wizardAnswers
    Assert-True -Name 'the wizard-produced answers splat into a usable install' `
        -Condition ($wizardSplat['SqlServer'] -eq 'cm1.corp.contoso.com' -and
                    $wizardSplat['HttpPort'] -eq 0 -and
                    -not $wizardSplat.Contains('SqlCertificateHostName'))

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

    # --- certificate picker lines -------------------------------------------------------------
    # Four fields, thumbprint first, and the wizard splits on '|' with no way to notice if a field
    # moved. Everything below is a way that has actually bitten someone in a DN or a locale.
    function New-FakeCertificate {
        param([string]$Subject = 'CN=np.contoso.local', [bool]$HasKey = $true, [string]$NotAfter = '2027-03-01')
        return [pscustomobject]@{
            Thumbprint = 'A' * 40
            Subject    = $Subject
            HasKey     = $HasKey
            NotAfter   = [datetime]::ParseExact($NotAfter, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
        }
    }

    Assert-True -Name 'a certificate line is thumbprint, subject, key flag and expiry' `
        -Condition ((Format-NodePilotCertificateLine -Certificate (New-FakeCertificate)) -eq
                    (('A' * 40) + '|CN=np.contoso.local|1|2027-03-01'))
    Assert-True -Name 'a certificate without a private key is flagged, not dropped' `
        -Condition ((Format-NodePilotCertificateLine -Certificate (New-FakeCertificate -HasKey $false)) -like '*|0|*')
    # A pipe is legal inside an X.500 attribute value, and one would shift every field behind it -
    # turning the key flag into a date and the expiry into nothing.
    Assert-True -Name 'a pipe inside the subject cannot shift the remaining fields' `
        -Condition ((Format-NodePilotCertificateLine -Certificate (New-FakeCertificate -Subject 'CN=a|b, O=c')).Split('|').Count -eq 4)
    Assert-True -Name 'a certificate with no subject still yields four fields' `
        -Condition ((Format-NodePilotCertificateLine -Certificate (New-FakeCertificate -Subject '')).Split('|').Count -eq 4)

    # 'yyyy' resolves against the culture's default calendar. Under ar-SA that is Umm al-Qura, and
    # the same call returns 1448 instead of 2027 - a date the operator cannot compare against
    # anything. Pinned here because the wizard runs on whatever locale the server was installed in.
    $originalCulture = [Threading.Thread]::CurrentThread.CurrentCulture
    try {
        [Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('ar-SA')
        Assert-True -Name 'the expiry date is Gregorian regardless of the machine locale' `
            -Condition ((Format-NodePilotCertificateLine -Certificate (New-FakeCertificate)) -like '*|2027-03-01')
    }
    finally {
        [Threading.Thread]::CurrentThread.CurrentCulture = $originalCulture
    }

    # --- the Certificates mode, as a process --------------------------------------------------
    # Run for real rather than by dot-sourcing: the adapter takes a mandatory -Mode and ends in
    # `exit`, so the only honest way to prove the wizard's call works is to make it. Needs no
    # answer file, no session directory and no elevation - reading the machine store's metadata is
    # allowed to anyone, which is exactly why the picker can run before anything else exists.
    $certificateIni = Join-Path $workingDirectory 'certificates.ini'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $SetupAdapterPath `
        -Mode Certificates -OutFile $certificateIni `
        -LogPath (Join-Path $workingDirectory 'adapter.log') | Out-Null
    Assert-True -Name 'listing certificates succeeds without an answer file' -Condition ($LASTEXITCODE -eq 0)

    $certificateIniLines = @(Get-Content -LiteralPath $certificateIni)
    Assert-True -Name 'the certificate list has its own INI section' `
        -Condition ($certificateIniLines -contains '[certificates]')
    $countLine = @($certificateIniLines | Where-Object { $_ -like 'count=*' })
    Assert-True -Name 'the count is always written, including as zero' -Condition ($countLine.Count -eq 1)

    # The wizard reads count first and sizes its array from it, so a count that does not match the
    # entries would leave it indexing past the end of the list.
    $certificateCount = [int]($countLine[0] -replace '^count=', '')
    $certificateLines = @($certificateIniLines |
        Where-Object { $_ -match '^\d+=' } |
        ForEach-Object { $_ -replace '^\d+=', '' })
    Assert-True -Name 'the count matches the number of entries' `
        -Condition ($certificateLines.Count -eq $certificateCount)

    # This machine's store may legitimately be empty - a fresh Windows install has no personal
    # machine certificates at all - so the shape assertions run only over what is actually there.
    $malformed = @($certificateLines | Where-Object {
        $fields = $_.Split('|')
        ($fields.Count -ne 4) -or
        ($fields[0] -notmatch '^[0-9A-Fa-f]{40}$') -or
        ($fields[2] -notmatch '^[01]$') -or
        ($fields[3] -notmatch '^\d{4}-\d{2}-\d{2}$')
    })
    Assert-True -Name 'every listed certificate parses into the four fields the wizard expects' `
        -Condition ($malformed.Count -eq 0)

    # Newest expiry first: a renewal sits in the store beside the certificate it replaces under the
    # same subject, and that date is the only thing telling them apart in the picker.
    $expiryDates = @($certificateLines | ForEach-Object { $_.Split('|')[3] })
    Assert-True -Name 'certificates are offered newest expiry first' `
        -Condition (($expiryDates -join ',') -eq ((@($expiryDates) | Sort-Object -Descending) -join ','))

    # --- first-admin bootstrap --------------------------------------------------------------
    # An unattended rollout has nobody to type a setup token, so the setup spends it itself and
    # writes down what it created. The password is random per machine: a fixed default would be
    # found by scanning rather than guessing, on a product that runs PowerShell everywhere.
    $bootstrapAnswers = Read-NodePilotAnswerFile -Path (New-AnswerFile -Name 'bootstrap.json' -Json (@{
        schemaVersion = 1; mode = 'install'; installPath = 'C:\np'; dataPath = 'C:\npdata'
        serviceName = 'NodePilot'; identity = @{ type = 'localSystem' }
        database = @{ provider = 'sqlserver'; sqlServer = 'db'; sqlDatabase = 'NodePilot' }
        network = @{ publicHostname = 'h'; httpsPort = 8443; httpPort = 0 }
        certificate = @{ thumbprint = 'D' * 40 }
        bootstrap = @{ adminUsername = 'npadmin' }
    } | ConvertTo-Json -Depth 6))
    Assert-True -Name 'the bootstrap group parses' `
        -Condition ($bootstrapAnswers['bootstrap.adminUsername'] -eq 'npadmin')
    # Without this the token could be spent on a name of an interceptor's choosing.
    Assert-True -Name 'the bootstrap username is pinned in the installer configuration' `
        -Condition ((ConvertTo-NodePilotInstallParameters -Answers $bootstrapAnswers)['BootstrapAdminUsername'] -eq 'npadmin')
    Assert-True -Name 'no bootstrap group means no pinned username' `
        -Condition (-not (ConvertTo-NodePilotInstallParameters -Answers $sqlAnswers).Contains('BootstrapAdminUsername'))
    # Casing is deliberately not the test: the key table is compared case-insensitively, like every
    # other PowerShell hashtable lookup here, so 'adminUserName' is a legitimate spelling. A key
    # that simply does not exist is what has to be caught, and named.
    Assert-Throws -Name 'a mistyped bootstrap key is rejected by name' -MessagePattern "unknown key 'bootstrap\.adminUser'" -Action {
        Read-NodePilotAnswerFile -Path (New-AnswerFile -Name 'bootstrap-typo.json' -Json (@{
            schemaVersion = 1; mode = 'install'; installPath = 'C:\np'; dataPath = 'C:\npdata'
            serviceName = 'NodePilot'; identity = @{ type = 'localSystem' }
            database = @{ provider = 'sqlserver'; sqlServer = 'db'; sqlDatabase = 'NodePilot' }
            network = @{ publicHostname = 'h'; httpsPort = 443; httpPort = 80 }
            certificate = @{ thumbprint = 'A' * 40 }
            bootstrap = @{ adminUser = 'npadmin' }
        } | ConvertTo-Json -Depth 6))
    }

    # Default location, because a silent installation has nowhere else the caller can predict.
    Assert-True -Name 'the credential file defaults into the data directory' `
        -Condition ((Get-NodePilotBootstrapCredentialPath -Answers $bootstrapAnswers) -eq 'C:\npdata\bootstrap-admin.json')
    Assert-True -Name 'an explicit credential path wins' `
        -Condition ((Get-NodePilotBootstrapCredentialPath -Answers @{
            'dataPath' = 'C:\npdata'; 'bootstrap.credentialOutputPath' = 'D:\out\np.json' }) -eq 'D:\out\np.json')

    # Property test rather than one sample: the server rejects anything outside 8..72 bytes, and a
    # generator that occasionally strays would fail one machine in a rollout, not the lab run.
    $weakDraws = 0
    for ($draw = 0; $draw -lt 200; $draw++) {
        $candidate = New-NodePilotBootstrapPassword
        $byteCount = [Text.Encoding]::UTF8.GetByteCount($candidate)
        if ($candidate.Length -lt 8 -or $byteCount -gt 72) { $weakDraws++ }
    }
    Assert-True -Name 'every generated password satisfies the server policy' -Condition ($weakDraws -eq 0)
    Assert-True -Name 'two generated passwords differ' `
        -Condition ((New-NodePilotBootstrapPassword) -ne (New-NodePilotBootstrapPassword))

    # --- provisioning seed ----------------------------------------------------------------------
    $seedAnswers = Read-NodePilotAnswerFile -Path (New-AnswerFile -Name 'seed.json' -Json (@{
        schemaVersion = 1; mode = 'install'; installPath = 'C:\np'; dataPath = 'C:\npdata'
        serviceName = 'NodePilot'; identity = @{ type = 'localSystem' }
        database = @{ provider = 'sqlserver'; sqlServer = 'db'; sqlDatabase = 'NodePilot' }
        network = @{ publicHostname = 'h'; httpsPort = 8443; httpPort = 0 }
        certificate = @{ thumbprint = 'E' * 40 }
        seed = @{ backupPath = '\\share\golden.npbackup'; passphrase = 'seed-pass-phrase' }
    } | ConvertTo-Json -Depth 6))
    $seedSplat = ConvertTo-NodePilotInstallParameters -Answers $seedAnswers
    Assert-True -Name 'the seed path reaches the installer' `
        -Condition ($seedSplat['SeedBackupPath'] -eq '\\share\golden.npbackup')
    # Same reason -PostgresPassword is one: it cannot cross a powershell.exe -File boundary any
    # other way, and it unlocks every credential the reference machine had.
    Assert-True -Name 'the seed passphrase travels as a SecureString' `
        -Condition ($seedSplat['SeedBackupPassphrase'] -is [System.Security.SecureString])
    Assert-True -Name 'no seed group means neither seed parameter' `
        -Condition (-not $sqlSplat.Contains('SeedBackupPath') -and -not $sqlSplat.Contains('SeedBackupPassphrase'))

    # The credential file is the whole point of the silent path: nobody is watching, so the
    # generated password has to be written somewhere the automation can collect it - and nowhere
    # else can read it.
    $credentialFile = Join-Path $workingDirectory 'bootstrap-admin.json'
    Write-NodePilotBootstrapCredentialFile -Path $credentialFile `
        -Username 'npadmin' -Password 'a-generated-secret' -Url 'https://host:8443/'
    $credential = Get-Content -LiteralPath $credentialFile -Raw | ConvertFrom-Json
    Assert-True -Name 'the credential file carries username, password and address' `
        -Condition ($credential.username -eq 'npadmin' -and $credential.password -eq 'a-generated-secret' -and
                    $credential.url -eq 'https://host:8443/')
    Assert-True -Name 'the credential file says it must be collected and rotated' `
        -Condition ($credential.note -match 'rotate')

    # Not $IsWindows: that variable does not exist in Windows PowerShell 5.1, and Set-StrictMode
    # turns reading it into a terminating error rather than a false.
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        $credentialAcl = Get-Acl -LiteralPath $credentialFile
        Assert-True -Name 'the credential file does not inherit' `
            -Condition ($credentialAcl.AreAccessRulesProtected)
        # Anything beyond SYSTEM and Administrators would hand a live admin password to a wider
        # audience than the machine's own operators.
        $untrusted = @($credentialAcl.Access | Where-Object {
            $sid = $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
            $sid -notin @('S-1-5-18', 'S-1-5-32-544')
        })
        Assert-True -Name 'only SYSTEM and Administrators can read the credential file' `
            -Condition ($untrusted.Count -eq 0)
    }

    # A second install onto the same machine must replace it, not fall over an existing file.
    Write-NodePilotBootstrapCredentialFile -Path $credentialFile `
        -Username 'npadmin2' -Password 'second-secret' -Url 'https://host:8443/'
    Assert-True -Name 'writing the credential file twice replaces it' `
        -Condition (((Get-Content -LiteralPath $credentialFile -Raw | ConvertFrom-Json).username) -eq 'npadmin2')

    # --- bootstrap token ------------------------------------------------------------------------
    # The finish page is the only place this token is ever shown, and it was blank on every real
    # installation: the service writes the file with a single ACE for its own identity, so the
    # installing admin is denied a plain read. Test-Path still says true, because Administrators own
    # the directory - which is why the naive version looked correct and produced nothing.
    $tokenData = Join-Path $workingDirectory 'tokendata'
    $tokenStage = Join-Path $workingDirectory 'tokenstage'
    New-Item -ItemType Directory -Path $tokenData, $tokenStage -Force | Out-Null

    Assert-True -Name 'a missing token file yields an empty string, not an error' `
        -Condition ((Get-NodePilotBootstrapToken -DataPath $tokenData -StagingDirectory $tokenStage) -eq '')

    [IO.File]::WriteAllText((Join-Path $tokenData 'admin-setup.token'), "  a-token-value`r`n")
    Assert-True -Name 'a readable token is returned trimmed' `
        -Condition ((Get-NodePilotBootstrapToken -DataPath $tokenData -StagingDirectory $tokenStage) -eq 'a-token-value')
    # A readable file never reaches the robocopy fallback, so there is nothing here to assert about
    # its cleanup - the staging directory stays empty because it was never used. That the fallback
    # shreds its copy is pinned as a contract in Test-DeploymentTemplates.ps1 instead; asserting it
    # here would only look like coverage.
    Assert-True -Name 'a readable token needs no staging copy at all' `
        -Condition (@(Get-ChildItem -LiteralPath $tokenStage -Recurse -Force -ErrorAction SilentlyContinue).Count -eq 0)

    # --- installation progress ------------------------------------------------------------------
    # Drives the wizard's bar. The installer is not touched for it: its own phase headings are
    # translated on the way past, which is why "does this line mean a phase" has to be exact.
    $phase = Get-NodePilotPhaseProgress -Line '[install] Extracting artifact'
    Assert-True -Name 'a phase heading yields a position and a caption' `
        -Condition ($null -ne $phase -and $phase.Percent -gt 0 -and $phase.Text)
    # Write-Info emits its detail lines under the same [install] prefix. Matching loosely would let
    # "  Service acct : ..." register as a phase and drag the bar somewhere arbitrary.
    Assert-True -Name 'an indented detail line is not a phase' `
        -Condition ($null -eq (Get-NodePilotPhaseProgress -Line '[install]   Service acct  : CORP\svc$'))
    Assert-True -Name 'an unrelated line is not a phase' `
        -Condition ($null -eq (Get-NodePilotPhaseProgress -Line 'random output'))
    # Several headings interpolate a value into themselves. An exact comparison cannot express
    # those at all, which is how three of them went unrecognised and the bar stood still through
    # half of an update.
    $updatePhase = Get-NodePilotPhaseProgress -Line "[update] Stopping service 'NodePilot'"
    Assert-True -Name 'an updater heading with an interpolated name still matches' `
        -Condition ($null -ne $updatePhase -and $updatePhase.Percent -gt 0)
    Assert-True -Name 'an installer heading with an interpolated account still matches' `
        -Condition ($null -ne (Get-NodePilotPhaseProgress -Line "[install] Granting 'Log on as a service' to CORP\svc`$"))
    # Every phase either script announces has to be recognised - the reverse of the drift guard,
    # checked here against the real tables rather than against the scripts.
    foreach ($sample in @(
        '[update] Backing up current install',
        "[update] Stopping service 'NodePilot'",
        '[update] Installing verified artifact',
        "[update] Starting service 'NodePilot'")) {
        Assert-True -Name "the updater phase in '$sample' is recognised" `
            -Condition ($null -ne (Get-NodePilotPhaseProgress -Line $sample))
    }
    Assert-True -Name 'an updater detail line is not a phase' `
        -Condition ($null -eq (Get-NodePilotPhaseProgress -Line '[update]   Backup: C:\x'))
    # Ascending percentages are what let the wizard refuse to ever move the bar backwards without
    # tracking state per phase.
    foreach ($table in @(
        @{ Name = 'install'; Percents = @(Get-NodePilotInstallPhases | ForEach-Object { [int]$_.Percent }) },
        @{ Name = 'update';  Percents = @(Get-NodePilotUpdatePhases  | ForEach-Object { [int]$_.Percent }) })) {
        $percents = $table.Percents
        Assert-True -Name "$($table.Name) phases ascend" `
            -Condition (($percents -join ',') -eq ((@($percents) | Sort-Object) -join ',') -and
                        (@($percents | Select-Object -Unique).Count -eq $percents.Count))
    }
    # Progress is cosmetic. It runs inside the pipe that carries the installer's output, so an
    # exception here would take the installation with it.
    Assert-True -Name 'an empty line is handled rather than thrown on' `
        -Condition ($null -eq (Get-NodePilotPhaseProgress -Line ''))

    # --- listen ports -------------------------------------------------------------------------
    # The defect this covers cost three minutes of silence on the lab host: Kestrel could not bind
    # port 80 - reserved by HTTP.SYS because IIS runs there - so the service crashed on startup,
    # the installer waited out its 180-second health probe, rolled everything back, and reported
    # "did not report /healthz/ready". Nothing on screen mentioned a port.
    $freeProbe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $freeProbe.Start()
    $freePort = ([System.Net.IPEndPoint]$freeProbe.LocalEndpoint).Port
    $freeProbe.Stop()

    $freeResult = Test-NodePilotListenPorts -HttpsPort $freePort -HttpPort 0
    Assert-True -Name 'a bindable port passes' -Condition ($freeResult.Status -eq 'Pass')
    # 0 is how the wizard says "no HTTP redirect". Treating it as a port would fail every
    # installation that does not want one.
    Assert-True -Name 'a zero HTTP port reads as disabled, not as a failure' `
        -Condition ($freeResult.Detail -match 'HTTP disabled')

    $busyProbe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $busyProbe.Start()
    $busyPort = ([System.Net.IPEndPoint]$busyProbe.LocalEndpoint).Port
    try {
        $busyResult = Test-NodePilotListenPorts -HttpsPort $busyPort -HttpPort 0
        Assert-True -Name 'an occupied port is reported as a required failure' `
            -Condition ($busyResult.Status -eq 'Fail' -and $busyResult.Required)
        # "Port in use" without a name sends the operator to netstat. The check already knows.
        Assert-True -Name 'the process holding the port is named' `
            -Condition ($busyResult.Detail -match 'already in use by')
        Assert-True -Name 'a blocked port carries an abort message naming the socket error' `
            -Condition ($busyResult.AbortMessage -match '10013 or 10048')
        Assert-True -Name 'a blocked port explains where Windows reservations are listed' `
            -Condition ($busyResult.Remediation -match 'excludedportrange')
        # The unattended path never renders a readiness page - /ANSWERFILE skips every wizard page,
        # so nothing calls the probe. A silent install is stopped by this assert inside
        # Install-NodePilot.ps1 instead, and only because the port result is Required.
        Assert-Throws -Name 'a blocked port aborts an unattended install' -MessagePattern 'Kestrel cannot bind' -Action {
            Assert-NodePilotPreflight -Results @($busyResult) | Out-Null
        }
    }
    finally { $busyProbe.Stop() }

    # Probing must leave nothing behind - it runs again on every click of "Check again".
    $reprobe = Test-NodePilotListenPorts -HttpsPort $freePort -HttpPort 0
    Assert-True -Name 're-checking a free port does not leave it bound' `
        -Condition ($reprobe.Status -eq 'Pass')

    # --- the two-layer pre-flight split -------------------------------------------------------
    # The point of the whole split: collecting must report, only asserting may abort. .invalid is
    # reserved by RFC 2606, so DNS fails immediately instead of burning the connect timeout.
    # Ports passed explicitly rather than left to the 443 default: whether this machine happens to
    # have 443 free is not what this assertion is about.
    $checks = @(Invoke-NodePilotPreflight `
        -CertificateThumbprint ('0' * 40) `
        -DbProvider 'sqlserver' `
        -IsLocalSystem $true `
        -HttpsPort $freePort `
        -HttpPort 0 `
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

    # --- certificate name matching ------------------------------------------------------------
    # The store lookup needs a certificate store; the comparison does not, which is why it is its
    # own function. Wildcards are the part worth having tests for - "it ends with the right thing"
    # accepts a.b.corp.example for *.corp.example, which is precisely what RFC 6125 forbids.
    Assert-True -Name 'an exact SAN entry matches' `
        -Condition (Test-NodePilotCertificateNameMatch -Names @('np.corp.example') -PublicHostname 'np.corp.example')
    Assert-True -Name 'the comparison ignores case' `
        -Condition (Test-NodePilotCertificateNameMatch -Names @('NP.Corp.Example') -PublicHostname 'np.corp.example')
    Assert-True -Name 'a second SAN entry counts too' `
        -Condition (Test-NodePilotCertificateNameMatch -Names @('other.corp.example', 'np.corp.example') -PublicHostname 'np.corp.example')
    Assert-True -Name 'a wildcard covers one label' `
        -Condition (Test-NodePilotCertificateNameMatch -Names @('*.corp.example') -PublicHostname 'np.corp.example')
    Assert-True -Name 'a wildcard does not cover two labels' `
        -Condition (-not (Test-NodePilotCertificateNameMatch -Names @('*.corp.example') -PublicHostname 'a.np.corp.example'))
    Assert-True -Name 'a wildcard does not cover the bare domain' `
        -Condition (-not (Test-NodePilotCertificateNameMatch -Names @('*.corp.example') -PublicHostname 'corp.example'))
    Assert-True -Name 'an unrelated name is a mismatch' `
        -Condition (-not (Test-NodePilotCertificateNameMatch -Names @('np.corp.example') -PublicHostname 'np.other.example'))
    # No hostname to check against is not a finding - the console path can be called without one,
    # and a complaint invented there would train people to ignore the line.
    Assert-True -Name 'no public host name means nothing to complain about' `
        -Condition (Test-NodePilotCertificateNameMatch -Names @('np.corp.example') -PublicHostname '')
    Assert-True -Name 'a certificate claiming no names at all is a mismatch' `
        -Condition (-not (Test-NodePilotCertificateNameMatch -Names @() -PublicHostname 'np.corp.example'))

    # SAN first, CN only when there is no SAN. Certificates without a SAN still come out of
    # internal PKIs, and reporting a mismatch for one would be reporting the wrong problem.
    $sanCert = [pscustomobject]@{
        Subject     = 'CN=fallback.corp.example, O=Contoso'
        DnsNameList = @([pscustomobject]@{ Unicode = 'np.corp.example' })
    }
    Assert-True -Name 'the SAN wins over the common name' `
        -Condition ((Get-NodePilotCertificateNames -Certificate $sanCert) -contains 'np.corp.example')
    Assert-True -Name 'the common name is not consulted when a SAN exists' `
        -Condition ((Get-NodePilotCertificateNames -Certificate $sanCert) -notcontains 'fallback.corp.example')
    $cnOnlyCert = [pscustomobject]@{ Subject = 'CN=legacy.corp.example, OU=IT, O=Contoso' }
    Assert-True -Name 'a certificate without a SAN falls back to its common name' `
        -Condition ((Get-NodePilotCertificateNames -Certificate $cnOnlyCert) -eq 'legacy.corp.example')

    # --- certificate validity -----------------------------------------------------------------
    # -Now is injected so both ends of the validity window are reachable here. The store lookup
    # is not: this suite installs nothing on the machine it runs on.
    function New-FakeStoreCertificate {
        param([string]$Name = 'np.corp.example', [int]$ValidFromDays = -30, [int]$ValidToDays = 365)
        $reference = [datetime]::new(2026, 8, 5, 12, 0, 0, [DateTimeKind]::Local)
        return [pscustomobject]@{
            Subject       = "CN=$Name, O=Contoso"
            HasPrivateKey = $true
            NotBefore     = $reference.AddDays($ValidFromDays)
            NotAfter      = $reference.AddDays($ValidToDays)
            DnsNameList   = @([pscustomobject]@{ Unicode = $Name })
        }
    }
    $referenceNow = [datetime]::new(2026, 8, 5, 12, 0, 0, [DateTimeKind]::Local)
    $verdict = {
        param($Cert, $HostName = 'np.corp.example')
        New-NodePilotCertificateVerdict -Certificate $Cert -Thumbprint ('A' * 40) `
            -PublicHostname $HostName -Now $referenceNow
    }

    $goodCert = & $verdict (New-FakeStoreCertificate)
    Assert-True -Name 'a valid, matching certificate passes' -Condition ($goodCert.Status -eq 'Pass')

    # THE case this was written for: it used to pass, the date was printed into the green line,
    # and the first person to see the problem was a user with a browser warning.
    $expired = & $verdict (New-FakeStoreCertificate -ValidToDays -1)
    Assert-True -Name 'an expired certificate stops the installation' `
        -Condition ($expired.Status -eq 'Fail' -and $expired.Required)
    Assert-True -Name 'the expiry date is in the message, not just the fact' `
        -Condition ($expired.Detail -match '2026-08-04' -and $expired.AbortMessage -match '2026-08-04')
    # Answering "your PKI certificate expired" with "have a lab certificate" would be worse than
    # stopping, so this failure carries no auto-fix even though the generator exists.
    Assert-True -Name 'an expired certificate is not silently replaced by a self-signed one' `
        -Condition (-not $expired.CanAutoFix)

    $notYet = & $verdict (New-FakeStoreCertificate -ValidFromDays 1 -ValidToDays 400)
    Assert-True -Name 'a certificate that is not valid yet stops the installation too' `
        -Condition ($notYet.Status -eq 'Fail' -and $notYet.Required)

    # Still valid, but not for much longer: worth saying, not worth stopping for.
    $soon = & $verdict (New-FakeStoreCertificate -ValidToDays 10)
    Assert-True -Name 'a certificate expiring soon still passes, with the date' `
        -Condition ($soon.Status -eq 'Pass' -and $soon.Detail -match 'Expires 2026-08-15')

    $mismatch = & $verdict (New-FakeStoreCertificate -Name 'other.corp.example')
    Assert-True -Name 'a name mismatch warns rather than blocking' -Condition ($mismatch.Status -eq 'Warn')
    Assert-True -Name 'the mismatch names both sides' `
        -Condition ($mismatch.Detail -match 'other\.corp\.example' -and $mismatch.Detail -match 'np\.corp\.example')
    # Reverse proxies and host aliases are legitimate, so this must never abort an install.
    Assert-True -Name 'a name mismatch never carries an abort message' `
        -Condition ([string]::IsNullOrEmpty($mismatch.AbortMessage))
    Assert-True -Name 'a wildcard certificate passes for a host it covers' `
        -Condition ((& $verdict (New-FakeStoreCertificate -Name '*.corp.example')).Status -eq 'Pass')
    # Expiry is checked before the name: an expired certificate with the right name is still the
    # more urgent finding, and reporting the name instead would bury it.
    $expiredAndMismatched = & $verdict (New-FakeStoreCertificate -Name 'other.corp.example' -ValidToDays -1)
    Assert-True -Name 'an expired certificate reports expiry, not the name' `
        -Condition ($expiredAndMismatched.Status -eq 'Fail' -and $expiredAndMismatched.Detail -match 'expired')

    # --- the service identity's access to the database ----------------------------------------
    # Was a caveat printed on every host whether or not the grant existed. Now a verdict, and the
    # verdict is split from the connection precisely so these branches are reachable here: no test
    # host has a SQL Server, and the failure this predicts (service starts, /healthz/ready 503) is
    # otherwise only observable in a lab.
    $svcSysadmin = New-NodePilotSqlServiceLoginResult -Principal 'CONTOSO\HOST$' -Database 'NodePilot' `
        -LoginExists $true -UserName '' -IsDbOwner $false -IsSysadmin $true
    Assert-True -Name 'a sysadmin service identity needs no grant' `
        -Condition ($svcSysadmin.Status -eq 'Pass')
    Assert-True -Name 'a passing service login offers no fix' `
        -Condition (-not $svcSysadmin.CanAutoFix -and -not $svcSysadmin.AutoFixDefault)

    $svcGranted = New-NodePilotSqlServiceLoginResult -Principal 'CONTOSO\HOST$' -Database 'NodePilot' `
        -LoginExists $true -UserName 'CONTOSO\HOST$' -IsDbOwner $true -IsSysadmin $false
    Assert-True -Name 'a login that is already db_owner passes' -Condition ($svcGranted.Status -eq 'Pass')

    # A service identity that OWNS the database maps to dbo, not to its own name. Reading that as
    # "no user" would offer a CREATE USER that fails with 15063 on a database that was fine.
    $svcDbo = New-NodePilotSqlServiceLoginResult -Principal 'CONTOSO\HOST$' -Database 'NodePilot' `
        -LoginExists $true -UserName 'dbo' -IsDbOwner $true -IsSysadmin $false
    Assert-True -Name 'a service identity mapped to dbo passes' -Condition ($svcDbo.Status -eq 'Pass')

    $svcNoLogin = New-NodePilotSqlServiceLoginResult -Principal 'CONTOSO\HOST$' -Database 'NodePilot' `
        -LoginExists $false -UserName '' -IsDbOwner $false -IsSysadmin $false
    Assert-True -Name 'a missing login is a failure the wizard can fix' `
        -Condition ($svcNoLogin.Status -eq 'Fail' -and $svcNoLogin.CanAutoFix)
    # Pre-ticked: the operator presses Next and it happens. This is the whole point of the change -
    # copying DDL into SSMS was the step that had to go.
    Assert-True -Name 'the service-login fix arrives ticked' -Condition ($svcNoLogin.AutoFixDefault)
    # Never Required. The install itself succeeds without the grant; refusing to install would be a
    # harder stop than the problem deserves, and the console path has always continued here.
    Assert-True -Name 'a missing service login does not refuse the install' `
        -Condition (-not $svcNoLogin.Required)
    Assert-True -Name 'the failure says what a missing grant will cost' `
        -Condition ($svcNoLogin.Detail -match '503')
    # The database is proven to exist - this check only runs after reachability passed. A DBA handed
    # a CREATE DATABASE for a database they can see reads the rest of the script as equally wrong.
    Assert-True -Name 'the service-login remediation omits CREATE DATABASE' `
        -Condition ($svcNoLogin.Remediation -notmatch 'CREATE DATABASE')
    Assert-True -Name 'the service-login remediation still creates the login and the grant' `
        -Condition ($svcNoLogin.Remediation -match 'CREATE LOGIN' -and
                    $svcNoLogin.Remediation -match 'CREATE USER' -and
                    $svcNoLogin.Remediation -match 'ALTER ROLE db_owner')

    # Three distinct gaps, three distinct sentences: "create the login", "create the user" and
    # "add it to db_owner" are different work, and one generic line would send an operator looking
    # for the wrong thing.
    $svcNoUser = New-NodePilotSqlServiceLoginResult -Principal 'CONTOSO\HOST$' -Database 'NodePilot' `
        -LoginExists $true -UserName '' -IsDbOwner $false -IsSysadmin $false
    Assert-True -Name 'a login without a database user says so' `
        -Condition ($svcNoUser.Status -eq 'Fail' -and $svcNoUser.Detail -match 'no user in \[NodePilot\]')
    $svcNoRole = New-NodePilotSqlServiceLoginResult -Principal 'CONTOSO\HOST$' -Database 'NodePilot' `
        -LoginExists $true -UserName 'npsvc' -IsDbOwner $false -IsSysadmin $false
    Assert-True -Name 'a user outside db_owner is named by its mapped user' `
        -Condition ($svcNoRole.Status -eq 'Fail' -and $svcNoRole.Detail -match '\[npsvc\]')

    # The full script keeps CREATE DATABASE - that one runs when nothing exists yet.
    Assert-True -Name 'the unreachable-database remediation still creates the database' `
        -Condition ((Get-NodePilotSqlRemediationScript -Principal 'CONTOSO\HOST$' -Database 'NodePilot') -match 'CREATE DATABASE')

    # An instance that cannot be asked must not be reported as an instance that answered "no": the
    # wizard would offer to create a login that may well be there, against a server it cannot reach.
    $svcUnreachable = Test-NodePilotSqlServiceLogin -Principal 'CONTOSO\HOST$' `
        -Server 'nodepilot-unreachable.invalid' -Database 'NodePilot' `
        -CertificateHostName 'nodepilot-unreachable.invalid'
    Assert-True -Name 'an unverifiable service login warns rather than fails' `
        -Condition ($svcUnreachable.Status -eq 'Warn' -and -not $svcUnreachable.CanAutoFix)
    Assert-True -Name 'the unverifiable case still names the principal and the statements' `
        -Condition ($svcUnreachable.Detail -match 'CONTOSO\\HOST\$' -and $svcUnreachable.Remediation -match 'ALTER ROLE')

    # --- handing an identity-bound secret to a new service identity ---------------------------
    # RestrictedFileWriter creates jwt-secret.key owned by whoever the service was, protected,
    # with one ACE. Change the identity and the new one cannot open it while the old one is gone -
    # which is exactly how a fresh install with a gMSA over a LocalSystem installation died at
    # first start (lab 2026-08-05). The descriptor written here has to match what the service
    # itself would have written, or the service will refuse its own key file.
    $secretPath = Join-Path $workingDirectory 'jwt-secret.key'
    [IO.File]::WriteAllText($secretPath, 'not a real key')
    $me = ([Security.Principal.WindowsIdentity]::GetCurrent()).User
    Set-NodePilotServiceOwnedFileAcl -Path $secretPath -ServiceAccount $me.Value

    $handed = Get-Acl -LiteralPath $secretPath
    Assert-True -Name 'the handed-over secret is owned by the new service identity' `
        -Condition ("$($handed.GetOwner([Security.Principal.SecurityIdentifier]))" -eq $me.Value)
    # Inheritance would pull in ACEs from the data directory, and the validator rejects a secret
    # with any inherited rule at all.
    Assert-True -Name 'the handed-over secret does not inherit' `
        -Condition ($handed.AreAccessRulesProtected)
    $handedRules = @($handed.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    Assert-True -Name 'exactly one principal can reach it' -Condition ($handedRules.Count -eq 1)
    # FullControl exactly, not "at least Read": the service DELETES admin-setup.token after first
    # login and replaces jwt-secret.key when rotating. A read-only handover produces a service that
    # starts and then cannot finish provisioning itself.
    Assert-True -Name 'and that principal is the new service identity, with full control' `
        -Condition ("$($handedRules[0].IdentityReference)" -eq $me.Value -and
                    $handedRules[0].FileSystemRights -eq [Security.AccessControl.FileSystemRights]::FullControl)

    # LocalSystem arrives as a name, and 'NT AUTHORITY\SYSTEM' does not resolve on a German
    # Windows - the same trap the rest of this file avoids with well-known SIDs.
    Set-NodePilotServiceOwnedFileAcl -Path $secretPath -ServiceAccount 'LocalSystem'
    $asSystem = Get-Acl -LiteralPath $secretPath
    Assert-True -Name 'LocalSystem resolves to the well-known SID, not a localised name' `
        -Condition ("$($asSystem.GetOwner([Security.Principal.SecurityIdentifier]))" -eq 'S-1-5-18')

    # A secret that is not there is not a failure: admin-setup.token is deleted after first login,
    # and an install over a provisioned instance must not trip over its absence.
    Set-NodePilotServiceOwnedFileAcl -Path (Join-Path $workingDirectory 'admin-setup.token') -ServiceAccount 'LocalSystem'
    Assert-True -Name 'a missing secret is a no-op, not an error' -Condition $true

    # --- the Postgres row ---------------------------------------------------------------------
    # The TCP probe could only ever say "the port answered". On SQL Server that gap is covered by
    # Windows auth - the pre-flight connects as somebody real; on Postgres there is no such
    # fallback, so a typo in the role password looked exactly like a healthy install until the
    # service started and the installer rolled it back 180 seconds later.
    $pgArgs = @{ HostName = 'pg1.corp.example'; Port = 5432; User = 'nodepilot'; Database = 'nodepilot' }

    $pgDown = New-NodePilotPostgresResult @pgArgs -TcpReachable $false -TcpError 'No route to host'
    Assert-True -Name 'an unreachable Postgres is still a required failure' `
        -Condition ($pgDown.Status -eq 'Fail' -and $pgDown.Required)

    # Built without -PgBinariesPath. Reporting Pass here would be repeating the old lie with extra
    # steps; the row says what it does and does not know.
    $pgNoClient = New-NodePilotPostgresResult @pgArgs -TcpReachable $true
    Assert-True -Name 'without a client the row warns instead of claiming success' `
        -Condition ($pgNoClient.Status -eq 'Warn')
    Assert-True -Name 'the clientless row says the login is untested' `
        -Condition ($pgNoClient.Detail -match 'untested')
    Assert-True -Name 'the clientless row offers no fix it cannot run' `
        -Condition (-not $pgNoClient.CanAutoFix)

    $pgOk = New-NodePilotPostgresResult @pgArgs -TcpReachable $true `
        -PsqlOutcome ([pscustomobject]@{ Succeeded = $true; Error = '' })
    Assert-True -Name 'a role that can log in passes' -Condition ($pgOk.Status -eq 'Pass')
    Assert-True -Name 'the passing row says the login was actually tried' `
        -Condition ($pgOk.Detail -match 'can log in')

    # What is missing comes from pg_roles and pg_database, NOT from psql's message. That message is
    # localised: the de-DE cluster this was built against answers "Rolle »nodepilot« existiert
    # nicht" and "Passwort-Authentifizierung ... fehlgeschlagen", so an English-only matcher
    # classifies correctly on one host and calls everything "refused" on the next. The German
    # strings below are the real ones, kept as the regression they are.
    $germanNoRole = 'psql: Fehler: FATAL: Rolle »nodepilot« existiert nicht'
    $germanBadPassword = 'psql: Fehler: FATAL: Passwort-Authentifizierung für Benutzer »nodepilot« fehlgeschlagen'

    $pgNoRole = New-NodePilotPostgresResult @pgArgs -TcpReachable $true -CanProvision $true `
        -PsqlOutcome ([pscustomobject]@{ Succeeded = $false; Error = $germanNoRole }) `
        -RoleExists $false -DatabaseExists $false
    Assert-True -Name 'a missing role is named and fixable' `
        -Condition ($pgNoRole.Status -eq 'Fail' -and $pgNoRole.CanAutoFix -and $pgNoRole.Detail -match "role 'nodepilot' does not exist")
    Assert-True -Name 'a missing database is named alongside it' `
        -Condition ($pgNoRole.Detail -match 'database \[nodepilot\] does not exist')

    # Role there, database not: half the work, and the fix does only the half that is missing.
    $pgNoDb = New-NodePilotPostgresResult @pgArgs -TcpReachable $true -CanProvision $true `
        -PsqlOutcome ([pscustomobject]@{ Succeeded = $false; Error = 'anything at all' }) `
        -RoleExists $true -DatabaseExists $false
    Assert-True -Name 'a missing database alone is named and fixable' `
        -Condition ($pgNoDb.Status -eq 'Fail' -and $pgNoDb.CanAutoFix -and $pgNoDb.Detail -match 'database \[nodepilot\] does not exist')
    Assert-True -Name 'an existing role is not reported as missing' `
        -Condition ($pgNoDb.Detail -notmatch "role 'nodepilot' does not exist")

    # THE localisation regression: a German "password authentication failed" must not read as a
    # missing role just because the English words are absent.
    $pgBadPassword = New-NodePilotPostgresResult @pgArgs -TcpReachable $true -CanProvision $true `
        -PsqlOutcome ([pscustomobject]@{ Succeeded = $false; Error = $germanBadPassword }) `
        -RoleExists $true -DatabaseExists $true
    Assert-True -Name 'both present but refused is not called a missing role' `
        -Condition ($pgBadPassword.Status -eq 'Fail' -and $pgBadPassword.Detail -notmatch 'does not exist')
    # Creating them again would change nothing, and the fix never rewrites an existing role's
    # password, so a button here would be a button that reports failure.
    Assert-True -Name 'both present but refused offers no fix' -Condition (-not $pgBadPassword.CanAutoFix)
    # Says what it DOES know, so the operator looks at the password and pg_hba.conf rather than at
    # whether the role was ever created. "Could not tell" would send them the wrong way.
    Assert-True -Name 'both present but refused says both are present' `
        -Condition ($pgBadPassword.Detail -match "both the role 'nodepilot' and the database \[nodepilot\] exist")
    Assert-True -Name 'the refusal is quoted verbatim, in whatever language it arrived' `
        -Condition ($pgBadPassword.Detail -match 'Passwort-Authentifizierung')

    # Could not ask: no superuser credentials. "I do not know" is a different answer from "they are
    # not there", and offering to create a role because nobody could look would be the worse guess.
    $pgUnknown = New-NodePilotPostgresResult @pgArgs -TcpReachable $true -CanProvision $false `
        -PsqlOutcome ([pscustomobject]@{ Succeeded = $false; Error = $germanNoRole })
    Assert-True -Name 'without a superuser the cause is not guessed at' `
        -Condition ($pgUnknown.Status -eq 'Fail' -and -not $pgUnknown.CanAutoFix -and $pgUnknown.Detail -notmatch 'does not exist')
    Assert-True -Name 'the unknown case still repeats what the server said' `
        -Condition ($pgUnknown.Detail -match 'existiert nicht')
    Assert-True -Name 'the unfixable row still carries the statements for a DBA' `
        -Condition ($pgUnknown.Remediation -match 'CREATE ROLE' -and $pgUnknown.Remediation -match 'CREATE DATABASE')

    # Windows PowerShell 5.1 has no ProcessStartInfo.ArgumentList, so the quoting is ours to get
    # right. A database name is validated before it reaches DDL, but a PASSWORD is not, and a
    # password with a quote or a trailing backslash in it would otherwise change where the argument
    # ends.
    Assert-True -Name 'a plain argument is passed through untouched' `
        -Condition ((ConvertTo-NodePilotCommandLineArgument -Value 'nodepilot') -eq 'nodepilot')
    Assert-True -Name 'an argument with a space is quoted' `
        -Condition ((ConvertTo-NodePilotCommandLineArgument -Value 'two words') -eq '"two words"')
    Assert-True -Name 'an embedded quote is escaped' `
        -Condition ((ConvertTo-NodePilotCommandLineArgument -Value 'a"b') -eq '"a\"b"')
    # A path with no space needs no quotes at all, and its trailing backslash is then harmless.
    Assert-True -Name 'a path without spaces is left alone, backslash and all' `
        -Condition ((ConvertTo-NodePilotCommandLineArgument -Value 'C:\dir\') -eq 'C:\dir\')
    # Once quoting IS needed, that same trailing backslash would escape the closing quote and
    # swallow the next argument, so it doubles.
    Assert-True -Name 'a trailing backslash cannot escape the closing quote' `
        -Condition ((ConvertTo-NodePilotCommandLineArgument -Value 'C:\program files\') -eq '"C:\program files\\"')
    Assert-True -Name 'backslashes before a quote double' `
        -Condition ((ConvertTo-NodePilotCommandLineArgument -Value 'a\"b') -eq '"a\\\"b"')
    Assert-True -Name 'an empty argument still occupies a slot' `
        -Condition ((ConvertTo-NodePilotCommandLineArgument -Value '') -eq '""')

    # --- ServiceControl.ps1 -------------------------------------------------------------------
    # Static checks can prove the wait is CALLED; only running it proves it works. The defect this
    # covers shipped: the update aborted on the process it had just stopped, because the SCM
    # reports SERVICE_STOPPED before the host has exited.
    $processRoot = Join-Path $workingDirectory 'procdir'
    New-Item -ItemType Directory -Path $processRoot -Force | Out-Null

    Assert-True -Name 'an empty directory reports no processes' `
        -Condition (@(Get-NodePilotProcessesUnderPath -Path $processRoot).Count -eq 0)
    Assert-True -Name 'waiting on an empty directory returns immediately' `
        -Condition (@(Wait-NodePilotProcessesUnderPath -Path $processRoot -TimeoutSeconds 1).Count -eq 0)

    # cmd.exe runs from anywhere and needs no arguments to sit idle on a paused pipe.
    $probeExe = Join-Path $processRoot 'nodepilot-probe.exe'
    Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32\cmd.exe') -Destination $probeExe -Force
    $probe = Start-Process -FilePath $probeExe -ArgumentList '/c', 'pause' -PassThru -WindowStyle Hidden
    try {
        # Start-Process returns before the image is necessarily enumerable; a short settle avoids
        # asserting on a race rather than on the helper.
        $settle = (Get-Date).AddSeconds(10)
        while ((Get-Date) -lt $settle -and @(Get-NodePilotProcessesUnderPath -Path $processRoot).Count -eq 0) {
            Start-Sleep -Milliseconds 200
        }
        Assert-True -Name 'a process under the path is found' `
            -Condition (@(Get-NodePilotProcessesUnderPath -Path $processRoot).Count -ge 1)

        # Without -Force the helper only observes: it must report the straggler, never end it.
        Assert-True -Name 'waiting without -Force reports the straggler' `
            -Condition (@(Wait-NodePilotProcessesUnderPath -Path $processRoot -TimeoutSeconds 1).Count -ge 1)
        Assert-True -Name 'waiting without -Force leaves it running' `
            -Condition (-not (Get-Process -Id $probe.Id -ErrorAction SilentlyContinue).HasExited)

        # With -Force it is the caller's job to clear the directory, not the operator's.
        Assert-True -Name 'waiting with -Force ends the straggler' `
            -Condition (@(Wait-NodePilotProcessesUnderPath -Path $processRoot -TimeoutSeconds 1 -Force).Count -eq 0)
        Assert-True -Name 'the ended process is really gone' `
            -Condition ($null -eq (Get-Process -Id $probe.Id -ErrorAction SilentlyContinue))
    }
    finally {
        Stop-Process -Id $probe.Id -Force -ErrorAction SilentlyContinue
    }

    # A path that never existed must be an empty answer, not an exception: callers use it to
    # decide whether to proceed, and a throw there would abort an otherwise fine installation.
    Assert-True -Name 'a non-existent path reports no processes' `
        -Condition (@(Get-NodePilotProcessesUnderPath -Path (Join-Path $workingDirectory 'nope')).Count -eq 0)
}
finally {
    if (Test-Path -LiteralPath $workingDirectory) {
        Remove-Item -LiteralPath $workingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Setup adapter checks passed ($script:Passed assertions)." -ForegroundColor Green
