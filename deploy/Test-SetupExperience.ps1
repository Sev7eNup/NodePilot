#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
. (Join-Path $PSScriptRoot 'DeploymentProgress.ps1')
. (Join-Path $PSScriptRoot 'SetupContract.ps1')
. (Join-Path $PSScriptRoot 'SetupProvisioning.ps1')
$fixture = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('nodepilot-experience-test-' + [guid]::NewGuid().ToString('N'))))
$script:experienceChecks = 0
function Assert-Experience {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "Setup experience check failed: $Message" }
    $script:experienceChecks++
}
function Assert-ExperienceThrows {
    param([scriptblock]$Action, [string]$Pattern)
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_.Exception.Message }
    Assert-Experience ($null -ne $failure -and $failure -match $Pattern) "Expected failure matching '$Pattern'; got '$failure'."
}
try {
    $source = Join-Path $fixture 'source [files]'
    $target = Join-Path $fixture 'target'
    $null = New-Item -ItemType Directory -Path (Join-Path $source 'empty'), (Join-Path $source 'nested') -Force
    [IO.File]::WriteAllBytes((Join-Path $source 'nested\large.bin'), (New-Object byte[] (3MB + 19)))
    [IO.File]::WriteAllText((Join-Path $source 'a [b].txt'), 'literal path')
    [IO.File]::WriteAllBytes((Join-Path $source 'empty.bin'), @())
    [IO.File]::SetAttributes((Join-Path $source 'a [b].txt'), [IO.FileAttributes]::Hidden)
    $events = New-Object 'System.Collections.Generic.List[object]'
    Copy-NodePilotDirectoryWithProgress -Source $source -Destination $target -ProgressCallback { param($p) $events.Add($p) }
    $expected = @(Get-ChildItem -LiteralPath $source -File -Recurse -Force)
    foreach ($file in $expected) {
        $relative = $file.FullName.Substring($source.Length + 1)
        Assert-Experience ((Get-FileHash -LiteralPath $file.FullName).Hash -eq (Get-FileHash -LiteralPath (Join-Path $target $relative)).Hash) "Copied bytes: $relative"
    }
    Assert-Experience (Test-Path -LiteralPath (Join-Path $target 'empty') -PathType Container) 'Empty directories survive copying.'
    Assert-Experience (([IO.File]::GetAttributes((Join-Path $target 'a [b].txt')) -band [IO.FileAttributes]::Hidden) -ne 0) 'Hidden attributes survive copying.'
    $last = $events[$events.Count - 1]
    Assert-Experience ($last.Current -eq (3MB + 19 + 12) -and $last.Total -eq $last.Current) 'Copy totals match the actual bytes.'
    Assert-Experience ($last.Detail -match '3 / 3') 'Completed file count includes empty and hidden files.'
    Copy-NodePilotDirectoryWithProgress -Source $source -Destination (Join-Path $fixture 'callback-fails') -ProgressCallback { throw 'Progress is unavailable' }
    Assert-ExperienceThrows { Copy-NodePilotDirectoryWithProgress -Source $source -Destination (Join-Path $source 'nested-target') } 'inside the source'
    $progressPath = Join-Path $fixture 'progress.txt'
    Send-NodePilotProgress -Callback { param($p) Write-NodePilotProgressFile -Path $progressPath -Progress $p } -Phase 'copy' -Message "Files|with`nnewlines" -Current 6GB -Total 8GB -Detail 'large counters'
    $line = [IO.File]::ReadAllText($progressPath).TrimStart([char]0xFEFF).Trim()
    Assert-Experience ($line -eq 'working|copy|75|1|Files with newlines|large counters|end') 'Large counters and delimiter escaping round-trip.'
    Send-NodePilotProgress -Callback { param($p) Write-NodePilotProgressFile -Path $progressPath -Progress $p } -Phase 'service' -Message 'Waiting for readiness'
    Assert-Experience ((Get-Content -LiteralPath $progressPath | Select-Object -Last 1) -eq 'working|service|0|0|Waiting for readiness||end') 'Unknown duration has no percentage.'
    $portResult = Join-Path $fixture 'ports.ini'
    $portProgress = Join-Path $fixture 'ports-progress.txt'
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Invoke-NodePilotSetup.ps1') `
        -Mode Ports -OutFile $portResult -ProgressFile $portProgress -LogPath (Join-Path $fixture 'ports.log') | Out-Null
    Assert-Experience ($LASTEXITCODE -eq 0 -and (Get-Content -LiteralPath $portResult -Raw) -match 'exitCode=0') 'The port worker runs independently of incomplete form answers.'
    Assert-Experience ((Get-Content -LiteralPath $portProgress -Raw) -match 'working\|ports\|0\|0\|Checking available HTTPS ports\|\|end') 'The real worker emits structured progress.'
    Assert-Experience ((Get-Content -LiteralPath ($portProgress + '.pid') -Raw) -match '^\d+$') 'The worker reports its own process ID for exit detection.'

    $answers = @{
        schemaVersion = 1; mode = 'install'; installPath = 'C:\NodePilot'; dataPath = 'C:\NodePilotData'; serviceName = 'NodePilot'
        identity = @{ type = 'gmsa'; account = 'TEST\np$' }
        database = @{ provider = 'sqlserver'; sqlServer = 'sql.test'; sqlDatabase = 'NodePilot' }
        network = @{ publicHostname = 'np.test'; httpsPort = 443; httpPort = 0 }
        certificate = @{ thumbprint = ''; source = 'pfx'; pfxPath = 'C:\np.pfx'; pfxPassword = '' }
        provisioning = @{ importPfxCertificate = $true; installGmsa = $true; installAdTools = $true }
    }
    $answerPath = Join-Path $fixture 'answers.json'
    function Save-TestAnswers { [IO.File]::WriteAllText($answerPath, ($answers | ConvertTo-Json -Depth 5)) }
    Save-TestAnswers
    $parsed = Read-NodePilotAnswerFile $answerPath
    Assert-Experience ($parsed['provisioning.installGmsa'] -and $parsed.Contains('certificate.pfxPassword')) 'New provisioning choices survive the answer-file contract.'
    $answers.certificate.thumbprint = 'A' * 40
    Save-TestAnswers
    Assert-ExperienceThrows { Read-NodePilotAnswerFile $answerPath } 'only one certificate source'
    $answers.certificate.thumbprint = ''
    $answers.provisioning.installGmsa = 'false'
    Save-TestAnswers
    Assert-ExperienceThrows { Read-NodePilotAnswerFile $answerPath } 'must be a boolean'
    $answers.provisioning.installGmsa = $false
    Save-TestAnswers
    Assert-ExperienceThrows { Read-NodePilotAnswerFile $answerPath } 'must accompany'

    # AD commands are replaced inside this scope; no domain or Windows feature is changed.
    & {
        . (Join-Path $PSScriptRoot 'SetupProvisioning.ps1')
        $script:gmsaInstalled = $false
        $script:gmsaInstallCalls = 0
        function Get-Module { param([switch]$ListAvailable, $Name) return 'present' }
        function Import-Module { param($Name, $ErrorAction) }
        function Test-ADServiceAccount { param($Identity, $ErrorAction) return $script:gmsaInstalled }
        function Install-ADServiceAccount { param($Identity, $ErrorAction) $script:gmsaInstalled = $true; $script:gmsaInstallCalls++ }
        $first = Install-NodePilotLocalGmsa -Account 'TEST\np$'
        $second = Install-NodePilotLocalGmsa -Account 'TEST\np$'
        Assert-Experience ($first.Status -eq 'Pass' -and $second.Status -eq 'Pass' -and $script:gmsaInstallCalls -eq 1) 'gMSA preparation verifies readiness and is idempotent.'
        function Get-Module { param([switch]$ListAvailable, $Name) return $null }
        Assert-ExperienceThrows { Install-NodePilotLocalGmsa -Account 'TEST\np$' } 'tools are missing'
        function Install-WindowsFeature { param($Name, $ErrorAction) return @{ Success = $true; RestartNeeded = 'Yes' } }
        $restart = Install-NodePilotLocalGmsa -Account 'TEST\np$' -InstallAdTools
        Assert-Experience ($restart.RestartRequired -and $restart.Status -eq 'Fail') 'A required restart is reported before gMSA installation.'
        function Get-Module { param([switch]$ListAvailable, $Name) return 'present' }
        function Test-ADServiceAccount { param($Identity, $ErrorAction) return $false }
        Assert-ExperienceThrows { Install-NodePilotLocalGmsa -Account 'TEST\np$' } 'not usable on this host'
    }
    $rsa = [Security.Cryptography.RSA]::Create(2048)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=np.test', $rsa,
            [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $certificate = $request.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddDays(1))
        try {
            $pfxPath = Join-Path $fixture 'certificate.pfx'
            [IO.File]::WriteAllBytes($pfxPath, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, 'fixture-password'))
            $pfx = Get-NodePilotPfxCertificate -Path $pfxPath -Password (ConvertTo-NodePilotSecureString 'fixture-password')
            Assert-Experience ($pfx.Thumbprint -eq $certificate.Thumbprint) 'PFX inspection selects the expected server certificate without importing it.'
            Assert-ExperienceThrows { Get-NodePilotPfxCertificate -Path $pfxPath -Password (ConvertTo-NodePilotSecureString 'wrong-password') } 'PFX could not be opened'
        } finally { $certificate.Dispose() }
    } finally { $rsa.Dispose() }
    & {
        . (Join-Path $PSScriptRoot 'SetupProvisioning.ps1')
        $script:pfxPresent = $false
        $script:pfxImports = 0
        function Get-NodePilotPfxCertificate { param($Path, $Password) return @{ Thumbprint = ('A' * 40) } }
        function Get-Item {
            param($LiteralPath, $ErrorAction)
            if ($script:pfxPresent) { return @{ Thumbprint = ('A' * 40); HasPrivateKey = $true } }
            return $null
        }
        function Import-PfxCertificate {
            param($FilePath, $Password, $CertStoreLocation, $ErrorAction)
            Assert-Experience ($CertStoreLocation -eq 'Cert:\LocalMachine\My') 'PFX import targets the personal machine store, not root trust.'
            $script:pfxPresent = $true
            $script:pfxImports++
        }
        $password = ConvertTo-NodePilotSecureString ''
        $one = Import-NodePilotPfxCertificate -Path 'fixture.pfx' -Password $password
        $two = Import-NodePilotPfxCertificate -Path 'fixture.pfx' -Password $password
        Assert-Experience ($one -eq ('A' * 40) -and $two -eq $one -and $script:pfxImports -eq 1) 'A repeated PFX import reuses the installed certificate and checks its key.'
    }
    & {
        . (Join-Path $PSScriptRoot 'Preflight.ps1')
        $manualSql = Get-NodePilotSqlRemediationScript -Principal "TEST\O'Brien]svc" -Database "NP'archive]"
        Assert-Experience ($manualSql.Contains("SUSER_ID(N'TEST\O''Brien]svc')") -and
            $manualSql.Contains("CREATE LOGIN [TEST\O'Brien]]svc]")) 'Manual SQL escapes literals and identifiers independently.'
        Assert-Experience ($manualSql.Contains("IF DB_ID(N'NP''archive]') IS NULL") -and
            $manualSql.Contains("CREATE DATABASE [NP'archive]]];`r`nGO`r`nUSE [NP'archive]]];`r`nGO")) 'Manual SQL reuses an existing database and separates creation from its use.'
        Assert-Experience ($manualSql.Contains('WHERE sid = SUSER_SID(') -and
            $manualSql.Contains('QUOTENAME(@user)')) 'Manual grants reuse the service login existing database mapping.'
        # Open() exceptions are wrapped by PowerShell before the probe's catch sees them.
        function New-Object {
            param([string]$TypeName, [object[]]$ArgumentList)
            if ($TypeName -eq 'System.Data.SqlClient.SqlConnection') { return $sqlConnection }
            Microsoft.PowerShell.Utility\New-Object -TypeName $TypeName -ArgumentList $ArgumentList
        }
        $flags = [Reflection.BindingFlags]'Instance,NonPublic'
        $errorConstructor = [System.Data.SqlClient.SqlError].GetConstructors($flags) |
            Where-Object { $_.GetParameters().Count -eq 8 } | Select-Object -First 1
        $createException = [System.Data.SqlClient.SqlException].GetMethods([Reflection.BindingFlags]'Static,NonPublic') |
            Where-Object { $_.Name -eq 'CreateException' -and $_.GetParameters().Count -eq 2 } | Select-Object -First 1
        foreach ($number in @(4060, 18456, 53)) {
            $errors = [Activator]::CreateInstance([System.Data.SqlClient.SqlErrorCollection], $true)
            $sqlError = $errorConstructor.Invoke(@($number, [byte]1, [byte]14, 'sql.test', 'SQL fixture failure', '', 0, $null))
            [System.Data.SqlClient.SqlErrorCollection].GetMethod('Add', $flags).Invoke($errors, @($sqlError)) | Out-Null
            $failure = $createException.Invoke($null, @($errors, '16.0'))
            $sqlConnection = [pscustomobject]@{ Failure = $failure; Disposed = $false }
            $sqlConnection | Add-Member ScriptMethod Open { throw $this.Failure }
            $sqlConnection | Add-Member ScriptMethod Dispose { $this.Disposed = $true }
            $result = Test-NodePilotSqlReachable -Server 'sql.test' -Database 'MissingDatabase' `
                -CertificateHostName 'sql.test' -Principal 'TEST\np$'
            Assert-Experience ($result.Status -eq 'Fail' -and $result.Required -and $sqlConnection.Disposed) "SQL error $number remains blocking and disposes the connection."
            Assert-Experience ($result.Detail -notmatch 'Exception calling|MethodInvocation') "SQL error $number does not expose PowerShell wrapper text."
            if ($number -eq 4060) {
                Assert-Experience ($result.Detail -match 'answered' -and $result.Detail -match 'MissingDatabase' -and $result.RemediationHint -notmatch 'certificate|reachable') 'An inaccessible database is distinguished from connection and TLS failures.'
                Assert-Experience ($result.CanAutoFix -and $result.AutoFixDefault -and $result.Remediation -match 'CREATE DATABASE') 'An inaccessible database offers preparation and manual SQL.'
            } else {
                Assert-Experience (-not $result.CanAutoFix -and -not $result.AutoFixDefault -and -not $result.Remediation) "SQL error $number does not offer database creation as a connection fix."
                if ($number -eq 18456) {
                    Assert-Experience ($result.RemediationHint -match 'Windows account' -and $result.RemediationHint -notmatch 'certificate') 'Rejected Windows login asks for account access, not TLS repair.'
                } else {
                    Assert-Experience ($result.RemediationHint -match 'certificate' -and $result.RemediationHint -match 'reachable') 'A connection failure retains connection and TLS guidance.'
                }
            }
        }
    }
    & {
        . (Join-Path $PSScriptRoot 'SetupContract.ps1')
        $tokens = $null
        $parseErrors = $null
        $adapterAst = [Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $PSScriptRoot 'Invoke-NodePilotSetup.ps1'), [ref]$tokens, [ref]$parseErrors)
        $updateFunction = $adapterAst.Find({ param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-SetupUpdate'
        }, $true)
        . ([scriptblock]::Create($updateFunction.Extent.Text))
        function Write-NodePilotProgress { param($Step, $Text) }
        $scriptDirectory = Join-Path $fixture 'update-adapter'
        [void](New-Item -ItemType Directory -Path $scriptDirectory)
        [IO.File]::WriteAllText((Join-Path $scriptDirectory 'Update-NodePilot.ps1'),
            'param($ArtifactPath, $TrustedArtifactSignerThumbprint, $InstallPath, $ServiceName, $ProgressCallback, $DataPath)')
        $settingsPath = Join-Path $scriptDirectory 'appsettings.Production.json'
        $settings = @{AllowedHosts='localhost;*.invalid;np.test';Kestrel=@{Https=@{HttpsPort=8443;CertificateThumbprint=('A' * 40)}}}
        [IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 5))
        $answers = @{schemaVersion=1;mode='update';installPath=$scriptDirectory;serviceName='Fixture';dataPath='D:\NodePilotData'}
        $AnswerFile = Join-Path $scriptDirectory 'answers.json'
        [IO.File]::WriteAllText($AnswerFile, ($answers | ConvertTo-Json))
        $ArtifactPath = 'inert.zip'
        $TrustedArtifactSignerThumbprint = 'A' * 40
        $progressWriter = $null
        $result = New-NodePilotResultBuffer
        $null = Invoke-SetupUpdate
        Assert-Experience ($result['result']['url'] -eq 'https://np.test:8443/') 'Update completion derives a usable address from installed allowed hosts without a nonexistent PublicHostname property.'
        Assert-Experience ($result['result']['certificateThumbprint'] -eq ('A' * 40) -and
            $result['result']['dataPath'] -eq 'D:\NodePilotData') 'Update completion retains the certificate and custom data directory.'
        $settings.AllowedHosts = 'localhost;*'
        [IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 5))
        $result = New-NodePilotResultBuffer
        $null = Invoke-SetupUpdate
        Assert-Experience (-not $result['result'].Contains('url') -and $result['result']['certificateThumbprint'] -eq ('A' * 40)) 'An unknown public address does not hide the certificate or invent a URL.'
    }
    Write-Host "Setup experience checks passed ($script:experienceChecks assertions; no machine provisioning)."
} finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $fixture.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($fixture) -notlike 'nodepilot-experience-test-*') { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
