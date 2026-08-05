#requires -Version 5.1
<#
.SYNOPSIS
    Verifies security-critical contracts in the deployment templates.
.DESCRIPTION
    This is a fast, dependency-free repository check. It does not replace
    `haproxy -c` against the fully rendered production configuration.
#>

[CmdletBinding()]
param(
    [string]$HaproxyTemplatePath,
    [string]$AppSettingsTemplatePath,
    [string]$InstallerPath,
    [string]$SsoDocumentationPath,
    [string]$BuildScriptPath,
    [string]$BuildPropsPath,
    [string]$UpdateScriptPath,
    [string]$PreflightScriptPath,
    [string]$UninstallScriptPath,
    [string]$SetupAdapterPath,
    [string]$SetupContractPath,
    [string]$ServerIssPath,
    [string]$RuntimePayloadScriptPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

# Windows PowerShell 5.1 evaluates parameter default expressions before $PSScriptRoot is
# populated when a script is launched via -File. Resolve defaults after parameter binding
# so this repository check works in both powershell.exe and pwsh.
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($HaproxyTemplatePath)) {
    $HaproxyTemplatePath = Join-Path $scriptDirectory 'templates\haproxy.cfg.template'
}
if ([string]::IsNullOrWhiteSpace($AppSettingsTemplatePath)) {
    $AppSettingsTemplatePath = Join-Path $scriptDirectory 'templates\appsettings.Production.json.template'
}
if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $InstallerPath = Join-Path $scriptDirectory 'Install-NodePilot.ps1'
}
if ([string]::IsNullOrWhiteSpace($SsoDocumentationPath)) {
    $SsoDocumentationPath = Join-Path $scriptDirectory '..\docs\ldap-windows-sso.md'
}
if ([string]::IsNullOrWhiteSpace($BuildScriptPath)) {
    $BuildScriptPath = Join-Path $scriptDirectory 'Build-Artifact.ps1'
}
if ([string]::IsNullOrWhiteSpace($BuildPropsPath)) {
    $BuildPropsPath = Join-Path $scriptDirectory '..\Directory.Build.props'
}
if ([string]::IsNullOrWhiteSpace($UpdateScriptPath)) {
    $UpdateScriptPath = Join-Path $scriptDirectory 'Update-NodePilot.ps1'
}
if ([string]::IsNullOrWhiteSpace($PreflightScriptPath)) {
    $PreflightScriptPath = Join-Path $scriptDirectory 'Preflight.ps1'
}
if ([string]::IsNullOrWhiteSpace($UninstallScriptPath)) {
    $UninstallScriptPath = Join-Path $scriptDirectory 'Uninstall-NodePilot.ps1'
}
if ([string]::IsNullOrWhiteSpace($SetupAdapterPath)) {
    $SetupAdapterPath = Join-Path $scriptDirectory 'Invoke-NodePilotSetup.ps1'
}
if ([string]::IsNullOrWhiteSpace($SetupContractPath)) {
    $SetupContractPath = Join-Path $scriptDirectory 'SetupContract.ps1'
}
if ([string]::IsNullOrWhiteSpace($ServerIssPath)) {
    $ServerIssPath = Join-Path $scriptDirectory 'server\NodePilotServer.iss'
}
if ([string]::IsNullOrWhiteSpace($RuntimePayloadScriptPath)) {
    $RuntimePayloadScriptPath = Join-Path $scriptDirectory 'Get-DotnetRuntimePayload.ps1'
}

function Assert-TextMatches {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Pattern
    )

    if (-not [regex]::IsMatch($Text, $Pattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
        throw "Deployment template check failed: $Name"
    }
}

function Assert-TextDoesNotMatch {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Pattern
    )

    if ([regex]::IsMatch($Text, $Pattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
        throw "Deployment template check failed: $Name"
    }
}

function Remove-CommentLines {
    <#
      Strips whole-line comments before a contract looks at the source.

      This is not a nicety. Four separate checks in this file have been written against text that
      also appears in the comment explaining the rule - the build-script signing order, the update
      success path, the uninstall ordering, and the adapter's HttpsPort rule - and every one of
      them passed or failed for the wrong reason until the comments were removed. A contract that
      matches its own explanation measures nothing.
    #>
    param([Parameter(Mandatory)][string]$Text, [string]$CommentPrefix = '#')
    # Block comments first - the comment-based help at the top of these scripts states the rules
    # in prose, using the very identifiers the contracts search for.
    $withoutBlocks = [regex]::Replace($Text, '(?s)<#.*?#>', '')
    return (($withoutBlocks -split "`r?`n" | Where-Object {
        $_.TrimStart() -notlike "$CommentPrefix*"
    }) -join "`n")
}

foreach ($path in @($HaproxyTemplatePath, $AppSettingsTemplatePath, $InstallerPath, $SsoDocumentationPath, $BuildScriptPath, $BuildPropsPath, $UpdateScriptPath, $PreflightScriptPath, $UninstallScriptPath, $SetupAdapterPath, $SetupContractPath, $ServerIssPath, $RuntimePayloadScriptPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Deployment template check failed: missing file '$path'."
    }
}

$haproxy = Get-Content -LiteralPath $HaproxyTemplatePath -Raw
$requiredHaproxyContracts = [ordered]@{
    'HTTP/1.1 keep-alive is enabled' = '(?m)^\s*option\s+http-keep-alive\s*$'
    'backend connections are private per frontend session' = '(?m)^\s*http-reuse\s+never\s*$'
    'frontend ALPN is pinned to HTTP/1.1' = '(?m)^\s*bind\s+.*\balpn\s+http/1\.1\s*$'
    'backend ALPN is pinned to HTTP/1.1' = '(?m)^\s*default-server\s+.*\balpn\s+http/1\.1\s*$'
    'backend certificate verification is mandatory' = '(?m)^\s*default-server\s+.*\bverify\s+required\b.*\bca-file\s+\{\{BACKEND_CA_FILE\}\}'
    'node A has explicit SNI and hostname verification' = '(?m)^\s*server\s+node-a\s+.*\bsni\s+str\(\{\{BACKEND_TLS_SERVER_NAME\}\}\).*\bcheck-sni\s+\{\{BACKEND_TLS_SERVER_NAME\}\}.*\bverifyhost\s+\{\{BACKEND_TLS_SERVER_NAME\}\}'
    'node B has explicit SNI and hostname verification' = '(?m)^\s*server\s+node-b\s+.*\bsni\s+str\(\{\{BACKEND_TLS_SERVER_NAME\}\}\).*\bcheck-sni\s+\{\{BACKEND_TLS_SERVER_NAME\}\}.*\bverifyhost\s+\{\{BACKEND_TLS_SERVER_NAME\}\}'
    'client Forwarded header is removed' = '(?m)^\s*http-request\s+del-header\s+Forwarded\s*$'
    'client X-Forwarded-For is removed' = '(?m)^\s*http-request\s+del-header\s+X-Forwarded-For\s*$'
    'client X-Forwarded-Proto is removed' = '(?m)^\s*http-request\s+del-header\s+X-Forwarded-Proto\s*$'
    'HAProxy generates X-Forwarded-For' = '(?m)^\s*option\s+forwardfor\s+header\s+X-Forwarded-For\s*$'
    'HAProxy sets the trusted scheme' = '(?m)^\s*http-request\s+set-header\s+X-Forwarded-Proto\s+https\s*$'
}

foreach ($contract in $requiredHaproxyContracts.GetEnumerator()) {
    Assert-TextMatches -Name $contract.Key -Text $haproxy -Pattern $contract.Value
}

Assert-TextDoesNotMatch -Name 'http-server-close must not break the Negotiate handshake' `
    -Text $haproxy -Pattern '(?m)^\s*option\s+http-server-close\b'
Assert-TextDoesNotMatch -Name 'backend TLS verification must never be disabled' `
    -Text $haproxy -Pattern '\bverify\s+none\b'

$appSettingsTemplate = Get-Content -LiteralPath $AppSettingsTemplatePath -Raw
Assert-TextMatches -Name 'appsettings exposes the trusted-proxy placeholder' `
    -Text $appSettingsTemplate -Pattern '"KnownProxies"\s*:\s*\{\{KNOWN_PROXIES_JSON\}\}'

function Render-AppSettingsTemplate {
    param([Parameter(Mandatory)][string]$KnownProxiesJson)

    $rendered = $appSettingsTemplate
    $replacements = [ordered]@{
        '{{DB_PROVIDER}}' = 'sqlserver'
        '{{SQLSERVER_CONNECTION_STRING}}' = 'Server=db;Database=NodePilot'
        '{{POSTGRES_CONNECTION_STRING}}' = ''
        '{{JWT_ISSUER}}' = 'nodepilot:test'
        '{{JWT_AUDIENCE}}' = 'nodepilot:test'
        '{{CERT_THUMBPRINT}}' = '0000000000000000000000000000000000000000'
        '{{HTTPS_PORT}}' = '443'
        '{{HTTP_PORT}}' = '80'
        '{{BIND_HTTP_JSON}}' = 'true'
        '{{DATA_PATH_ESCAPED}}' = 'C:\\ProgramData\\NodePilot'
        '{{EXTERNAL_TRIGGER_API_KEY}}' = 'test-key'
        '{{ALLOWED_HOSTS}}' = 'nodepilot.example.test'
        '{{KNOWN_PROXIES_JSON}}' = $KnownProxiesJson
    }

    foreach ($replacement in $replacements.GetEnumerator()) {
        $rendered = $rendered.Replace($replacement.Key, $replacement.Value)
    }

    Assert-TextDoesNotMatch -Name 'rendered appsettings has no unresolved placeholders' `
        -Text $rendered -Pattern '\{\{[^}]+\}\}'
    return $rendered | ConvertFrom-Json
}

$emptySettings = Render-AppSettingsTemplate -KnownProxiesJson '[]'
if (@($emptySettings.ForwardedHeaders.KnownProxies).Count -ne 0) {
    throw 'Deployment template check failed: an empty trusted-proxy list did not render as an empty JSON array.'
}
if (-not [string]::IsNullOrEmpty([string]$emptySettings.ConnectionStrings.Postgres)) {
    throw 'Deployment template check failed: the Postgres secret must not be rendered into production JSON.'
}

$proxySettings = Render-AppSettingsTemplate -KnownProxiesJson '["10.0.1.5","2001:db8::5"]'
$renderedProxies = @($proxySettings.ForwardedHeaders.KnownProxies)
if ($renderedProxies.Count -ne 2 -or
    $renderedProxies[0] -ne '10.0.1.5' -or
    $renderedProxies[1] -ne '2001:db8::5') {
    throw 'Deployment template check failed: trusted IPv4/IPv6 proxy addresses did not round-trip through JSON.'
}

$installer = Get-Content -LiteralPath $InstallerPath -Raw
Assert-TextMatches -Name 'installer accepts trusted proxy IPs' `
    -Text $installer -Pattern '\[string\[\]\]\$KnownProxyIps\s*=\s*@\(\)'
Assert-TextMatches -Name 'installer renders the trusted-proxy placeholder' `
    -Text $installer -Pattern "Replace\('\{\{KNOWN_PROXIES_JSON\}\}'"
Assert-TextMatches -Name 'installer validates trusted proxy addresses' `
    -Text $installer -Pattern 'IPAddress\]::TryParse\(\$proxyIp'
Assert-TextMatches -Name 'installer keeps the Postgres secret in the service-scoped environment' `
    -Text $installer -Pattern 'ConnectionStrings__Postgres=\$postgresServiceConnStr'
Assert-TextMatches -Name 'installer protects the service registry key before writing the Postgres secret' `
    -Text $installer -Pattern '(?s)Set-ServiceRegistryAclForSecrets\s+-Path\s+\$envRegPath.*ConnectionStrings__Postgres=\$postgresServiceConnStr'

# --- release build contracts ------------------------------------------------------------------
# The release drop must stay reproducible from one script and one version number. These guard the
# three properties that silently rot: the version source, the SPA being built twice, and the
# desktop step being allowed to abort a server build.
$buildScript = Get-Content -LiteralPath $BuildScriptPath -Raw
$requiredBuildContracts = [ordered]@{
    'build script accepts the desktop-installer switch' = '\[switch\]\$IncludeDesktopInstaller'
    'build script accepts the Postgres binaries path' = '\[string\]\$PgBinariesPath'
    'build script accepts an Inno Setup override' = '\[string\]\$IsccPath'
    'version defaults to Directory.Build.props instead of a timestamp' = "Directory\.Build\.props"
    'version is parsed from the <Version> element' = "'<Version>"
    'the desktop build inherits the same version' = '(?s)\$desktopArgs\s*=\s*@\{[^}]*Version\s*=\s*\$Version'
    'the SPA is not rebuilt for the desktop payload' = '(?s)\$desktopArgs\s*=\s*@\{[^}]*SkipSpaBuild\s*=\s*\$true'
    'the produced installer is copied next to the server zip' = 'NodePilot-Desktop-Setup-\$Version\.exe'
    'a checksum file is written' = 'SHA256SUMS'
    'checksums are SHA256' = "Get-FileHash[^`r`n]*-Algorithm SHA256"
    'missing desktop prerequisites warn instead of failing' = '(?s)\$desktopSkipReasons\.Count -eq 0.*?else\s*\{\s*Write-Warning'
    'the installers can be Authenticode-signed by the build' = '\[string\]\$InstallerSigningCertificateThumbprint'
    'signing is verified rather than trusted to signtool exit code' = 'Get-AuthenticodeSignature'
    'build script accepts the server-setup switch' = '\[switch\]\$IncludeServerInstaller'
    'the produced server setup is copied next to the server zip' = 'NodePilot-Server-Setup-\$Version\.exe'
    # One signing loop covering every installer, not a hand-maintained block per target: a second
    # copy is how the two drift apart, and the ordering check below only pins one place.
    'signing iterates over every installer this run produced' = '(?s)foreach \(\$target in \$installersToSign\)'
}

# Signing rewrites the .exe, so it must happen BEFORE the checksums are computed. Getting this
# backwards produces a SHA256SUMS that declares the shipped installer corrupt - and it is exactly
# the order the script had while signing was still a manual follow-up step.
# Anchored on code, not on prose: the phrase "Authenticode-sign" also appears in the .PARAMETER
# help at the top of the script, and matching that would make this check pass no matter where the
# signing step actually sits.
$signIndex = $buildScript.IndexOf('$signTool.FullName sign')
$checksumIndex = $buildScript.IndexOf('$checksumLines')
if ($signIndex -lt 0 -or $checksumIndex -lt 0) {
    throw 'Deployment template check failed: could not locate the signing and checksum steps in the build script.'
}
if ($signIndex -gt $checksumIndex) {
    throw 'Deployment template check failed: the installer is signed after the checksums are written, which invalidates them.'
}

foreach ($contract in $requiredBuildContracts.GetEnumerator()) {
    Assert-TextMatches -Name $contract.Key -Text $buildScript -Pattern $contract.Value
}

# A missing Inno Setup or Postgres distribution must never abort the server artifact, so the
# pre-flight block that decides this may not contain a throw.
$desktopPreflight = [regex]::Match($buildScript, '(?s)if \(\$IncludeDesktopInstaller\) \{.*?\r?\n\}')
if (-not $desktopPreflight.Success) {
    throw 'Deployment template check failed: could not locate the desktop pre-flight block in the build script.'
}
Assert-TextDoesNotMatch -Name 'desktop pre-flight must not throw on missing prerequisites' `
    -Text $desktopPreflight.Value -Pattern '\bthrow\b'

$serverPreflight = [regex]::Match($buildScript, '(?s)if \(\$IncludeServerInstaller\) \{.*?\r?\n\}')
if (-not $serverPreflight.Success) {
    throw 'Deployment template check failed: could not locate the server-setup pre-flight block in the build script.'
}
Assert-TextDoesNotMatch -Name 'server-setup pre-flight must not throw on missing prerequisites' `
    -Text $serverPreflight.Value -Pattern '\bthrow\b'

# The setup embeds the signed zip and verifies it at install time, so an unsigned development
# artifact would produce an installer that refuses its own payload. Skip, do not attempt.
Assert-TextMatches -Name 'the server setup is not built from an unsigned artifact' `
    -Text $serverPreflight.Value -Pattern '\$AllowUnsignedDevelopmentArtifact'

# Both installers must be in $artifacts before the checksums are computed, or SHA256SUMS ships
# describing a drop it does not cover.
$serverArtifactIndex = $buildScript.IndexOf('$artifacts += $serverInstaller')
if ($serverArtifactIndex -lt 0) {
    throw 'Deployment template check failed: the server setup is never added to the checksum list.'
}
if ($serverArtifactIndex -gt $checksumIndex) {
    throw 'Deployment template check failed: the server setup is added to the checksum list after the checksums are written.'
}

# The version regex in the build script must actually match the props file it reads. A renamed or
# conditioned <Version> element would otherwise only surface when someone cuts a release.
$buildProps = Get-Content -LiteralPath $BuildPropsPath -Raw
$versionMatch = [regex]::Match($buildProps, '<Version>\s*([^<\s]+)\s*</Version>')
if (-not $versionMatch.Success) {
    throw "Deployment template check failed: no <Version> element in '$BuildPropsPath' - Build-Artifact.ps1 could not derive a default version."
}
if ($versionMatch.Groups[1].Value -notmatch '^\d+\.\d+\.\d+') {
    throw "Deployment template check failed: <Version> '$($versionMatch.Groups[1].Value)' is not a three-part product version."
}

# --- update contract -----------------------------------------------------------------------
# A successful update must leave the service RUNNING. The script used to restore the pre-update
# state, which combined badly with its own 30-second stop timeout: operators stop the service by
# hand first, so "stopped" became the recorded state and a successful update ended with a dead
# service. Failure must still restore the prior state.
$updateScript = Get-Content -LiteralPath $UpdateScriptPath -Raw

$successStart = $updateScript.IndexOf("Write-Ok '/healthz/ready returned 200 OK'")
$catchStart = $updateScript.IndexOf("`n    catch {", $(if ($successStart -ge 0) { $successStart } else { 0 }))
if ($successStart -lt 0 -or $catchStart -lt 0) {
    throw 'Deployment template check failed: could not delimit the success path in Update-NodePilot.ps1.'
}
$successPath = $updateScript.Substring($successStart, $catchStart - $successStart)

# Comments are stripped first. The block carries an explanation that names Stop-ServiceAndVerify,
# and matching prose would fail the check no matter what the code does - the same way an earlier
# version of the build-script ordering check passed no matter where the signing step sat.
$successCode = ($successPath -split "`n" | Where-Object { $_.TrimStart() -notmatch '^#' }) -join "`n"

Assert-TextDoesNotMatch -Name 'a successful update must not stop the service again' `
    -Text $successCode -Pattern 'Stop-ServiceAndVerify'

# The rollback path is the one place the prior state still governs.
$rollbackPath = $updateScript.Substring($catchStart)
Assert-TextMatches -Name 'a failed update still restores the pre-update state' `
    -Text $rollbackPath -Pattern '(?s)\$serviceWasRunning\s+-and.*?Start-Service'

# Comment-stripped, because the explanation of the rule below necessarily names the things the
# rule forbids. Anchored on the whole script rather than on $successCode: the start-type fix runs
# before the health probe, and $successCode covers only what happens after it returned 200.
$updateCode = Remove-CommentLines -Text $updateScript
# Without this the delayed-auto fix reaches fresh installations only, and every upgraded host
# keeps idling roughly two minutes past each boot waiting for something the new binaries now do
# themselves. An upgrade that leaves the old start type behind looks exactly like no fix at all.
Assert-TextMatches -Name 'an update normalises the service start type' `
    -Text $updateCode -Pattern 'sc\.exe\s+config\s+\$ServiceName\s+start=\s+auto\b'
# The update's contract is binaries only. Identity, dependencies and recovery actions belong to
# the installer; reconfiguring them from here would change a service the operator asked us only
# to update, and would do it without ever saying so.
Assert-TextDoesNotMatch -Name 'an update must not reconfigure identity, dependencies or recovery' `
    -Text $updateCode -Pattern 'sc\.exe\s+(failure|managedaccount)|sc\.exe\s+config[^\r\n]*\b(obj|depend)='

# --- pre-flight extraction contracts ----------------------------------------------------------
# The readiness checks live in Preflight.ps1 so the setup wizard can run the same set behind a
# "re-check" button. That shared use is the entire reason for the split, and it only holds while
# the file stays free of side effects.
$preflightScript = Get-Content -LiteralPath $PreflightScriptPath -Raw

# The near-miss this guards against, concretely: Enable-SqlReadCommittedSnapshot used to sit
# INSIDE the SQL reachability try/catch. Moving the pre-flight block wholesale would have carried
# its ALTER DATABASE ... WITH ROLLBACK IMMEDIATE along - and that drops every open session on the
# target database, once per click of a re-check button, against production.
#
# Checked over the parsed AST, not with a regex over the source. A regex cannot tell "executes
# New-NetFirewallRule" from "prints New-NetFirewallRule as the fix for a red row" - and this file
# is full of the latter on purpose, including CREATE LOGIN / CREATE DATABASE / sc.exe. In the AST
# a remediation string is a StringConstantExpression and simply is not a command, so the check
# becomes exact instead of heuristic.
$preflightParseErrors = $null
$preflightAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $PreflightScriptPath, [ref]$null, [ref]$preflightParseErrors)
if ($preflightParseErrors -and $preflightParseErrors.Count -gt 0) {
    throw "Deployment template check failed: '$PreflightScriptPath' does not parse: $($preflightParseErrors[0].Message)"
}

$forbiddenPreflightCommands = @(
    'New-Item', 'New-ItemProperty', 'Set-ItemProperty', 'Remove-Item', 'Remove-ItemProperty'
    'Set-Acl', 'icacls', 'secedit'
    'New-Service', 'Set-Service', 'Start-Service', 'Stop-Service', 'Restart-Service', 'sc.exe'
    'New-NetFirewallRule', 'Remove-NetFirewallRule', 'Set-NetFirewallRule'
    'New-SelfSignedCertificate', 'Import-PfxCertificate', 'Import-Certificate'
    'Invoke-CimMethod', 'Install-ADServiceAccount'
    'Out-File', 'Set-Content', 'Add-Content', 'Copy-Item', 'Move-Item', 'Expand-Archive'
    'Enable-SqlReadCommittedSnapshot'
)
$invokedCommands = @(
    $preflightAst.FindAll(
        { param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true) |
        ForEach-Object { $_.GetCommandName() } |
        Where-Object { $_ } |
        Sort-Object -Unique
)
$mutatingCommands = @($invokedCommands | Where-Object { $forbiddenPreflightCommands -contains $_ })
if ($mutatingCommands.Count -gt 0) {
    throw ("Deployment template check failed: Preflight.ps1 invokes mutating command(s) " +
           "'$($mutatingCommands -join "', '")'. Readiness checks run behind the setup wizard's " +
           're-check button and must have no side effects; move install-time work to Install-NodePilot.ps1.')
}

# ExecuteNonQuery is the single gate every ADO.NET mutation passes through, and it is a method
# call rather than a command, so it needs its own pass over the AST.
$invokedMethods = @(
    $preflightAst.FindAll(
        { param($node) $node -is [System.Management.Automation.Language.InvokeMemberExpressionAst] }, $true) |
        ForEach-Object { [string]$_.Member.Value } |
        Where-Object { $_ } |
        Sort-Object -Unique
)
if ($invokedMethods -contains 'ExecuteNonQuery') {
    throw ('Deployment template check failed: Preflight.ps1 calls ExecuteNonQuery. ' +
           'Readiness probes may read, never write.')
}

# Every check must report rather than throw, or it cannot be rendered as a traffic-light row.
Assert-TextMatches -Name 'preflight checks return result objects' `
    -Text $preflightScript -Pattern 'function\s+New-NodePilotPreflightResult'
Assert-TextMatches -Name 'preflight exposes a collect-only entry point' `
    -Text $preflightScript -Pattern 'function\s+Invoke-NodePilotPreflight'
Assert-TextMatches -Name 'preflight exposes an asserting entry point' `
    -Text $preflightScript -Pattern 'function\s+Assert-NodePilotPreflight'

# A process that just installed the runtime still carries the PATH it started with, so a
# PATH-only lookup keeps failing after a successful install. Losing this fallback would make the
# wizard's "install the runtime for me" action look broken.
Assert-TextMatches -Name 'the dotnet probe falls back to the machine-wide install location' `
    -Text $preflightScript -Pattern 'dotnet\\dotnet\.exe'

$installerScript = Get-Content -LiteralPath $InstallerPath -Raw
Assert-TextMatches -Name 'installer dot-sources the shared pre-flight helper' `
    -Text $installerScript -Pattern "Join-Path\s+\`$PSScriptRoot\s+'Preflight\.ps1'"
# A re-forked copy inside the installer would drift from the one the wizard runs, silently.
Assert-TextDoesNotMatch -Name 'installer must not re-declare the extracted pre-flight checks' `
    -Text $installerScript `
    -Pattern '(?m)^\s*function\s+(Test-NodePilot(DotNetRuntime|SqlReachable|SqlTds8Support|PostgresReachable|TlsCertificate|Gmsa)|Test-DotNet10Runtime|Test-SqlReachable|Assert-SqlServerTds8Support|Test-PostgresReachable)\b'
# RCSI is install-time work and must stay on the installer side of the split.
Assert-TextMatches -Name 'RCSI stays with the installer, not the pre-flight' `
    -Text $installerScript -Pattern 'function\s+Enable-SqlReadCommittedSnapshot'

# Ordering, anchored on code rather than on the section comment: a pre-flight that ran after the
# artifact was staged would have already mutated the target before it could refuse to.
$assertIndex = $installerScript.IndexOf('Assert-NodePilotPreflight -Results')
$stagingIndex = $installerScript.IndexOf('Expand-NodePilotArtifactToStaging')
if ($assertIndex -lt 0 -or $stagingIndex -lt 0) {
    throw 'Deployment template check failed: could not locate the pre-flight assertion and the staging step in the installer.'
}
if ($assertIndex -gt $stagingIndex) {
    throw 'Deployment template check failed: the installer stages the artifact before asserting the pre-flight results.'
}

# The unattended path never shows the readiness page - with /ANSWERFILE every wizard page is
# skipped, so nothing calls the probe. The port check reaches a silent installation only through
# THIS call, and only if it is handed the ports actually being installed. Dropping these two
# arguments would leave the pre-flight checking the parameter defaults (443 and 80) while the
# install proceeds on 8443, which passes and then fails at service start - the exact failure the
# check exists to prevent, restored in the one mode where nobody is watching.
Assert-TextMatches -Name 'the installer pre-flights the ports it is installing on' `
    -Text $installerScript `
    -Pattern '(?s)Invoke-NodePilotPreflight[\s\S]{0,600}-HttpsPort \$HttpsPort[\s\S]{0,80}-HttpPort \$HttpPort'

# The marker is how any later tool finds this installation. Leaving it behind on uninstall makes
# every subsequent fresh install look like an upgrade.
Assert-TextMatches -Name 'installer records a machine-wide installation marker' `
    -Text $installerScript -Pattern 'HKLM:\\SOFTWARE\\NodePilot\\Server'

# --- process-wait contracts ---------------------------------------------------------------------
# The SCM reports SERVICE_STOPPED while the host is still unwinding, so an immediate snapshot of
# the install directory finds the very process the script just stopped. Both scripts used to abort
# there and tell the operator to kill it by hand (lab 2026-08-03, exit code 4 on a plain update).
# Waiting for it - and ending what is left - is the script's job.
Assert-TextMatches -Name 'the update waits for the stopped service process instead of blaming it' `
    -Text $updateCode -Pattern 'Wait-NodePilotProcessesUnderPath[^\r\n]*-Force'
Assert-TextMatches -Name 'the installer waits for the stopped service process too' `
    -Text $installerScript -Pattern 'Wait-NodePilotProcessesUnderPath[^\r\n]*-Force'
# The snapshot-and-throw is what produced the dead end. Its distinguishing feature is testing
# Path.StartsWith inline instead of going through the shared helper.
Assert-TextDoesNotMatch -Name 'the update must not re-add an unwaited process snapshot' `
    -Text $updateCode -Pattern 'Get-Process[\s\S]{0,120}Path\.StartsWith'
# Deleting the service before its process exits orphans it: nothing can address it through the
# SCM afterwards, and the file replacement then rips DLLs out from under a live process.
$installWaitIndex = $installerScript.IndexOf('Wait-NodePilotProcessesUnderPath')
$installDeleteIndex = $installerScript.IndexOf('& sc.exe delete $Name')
if ($installWaitIndex -lt 0 -or $installDeleteIndex -lt 0) {
    throw 'Deployment template check failed: could not locate the installer process wait and service delete.'
}
if ($installWaitIndex -gt $installDeleteIndex) {
    throw 'Deployment template check failed: the installer deletes the service before waiting for its process.'
}
# Both callers share one implementation; a re-forked copy would drift from the one that was fixed.
$serviceControlScript = Get-Content -LiteralPath (Join-Path $scriptDirectory 'ServiceControl.ps1') -Raw
Assert-TextMatches -Name 'the shared process helper exists' `
    -Text $serviceControlScript -Pattern 'function\s+Wait-NodePilotProcessesUnderPath'
Assert-TextDoesNotMatch -Name 'installer and update do not re-declare the shared process helper' `
    -Text ($installerScript + $updateScript) `
    -Pattern '(?m)^\s*function\s+(Wait|Get)-NodePilotProcessesUnderPath\b'
# It has to ship, or the copy of Update-NodePilot.ps1 the setup lays down cannot dot-source it
# and every update through the wizard dies on a missing helper.
Assert-TextMatches -Name 'the shared process helper ships with the setup' `
    -Text (Get-Content -LiteralPath (Join-Path $scriptDirectory 'server\Build-ServerInstaller.ps1') -Raw) `
    -Pattern "'ServiceControl\.ps1'"

# --- service start-type contracts ---------------------------------------------------------------
# Scoped to the registration section, because Restore-ServiceRollbackSnapshot legitimately still
# writes start= delayed-auto: it restores whatever the service it replaced had. A file-wide check
# would either fail on that correct code or be weakened until it proves nothing.
$registrationStart = $installerScript.IndexOf('Write-Step "Registering Windows Service"')
$registrationEnd = $installerScript.IndexOf('Write-Step ', $registrationStart + 40)
if ($registrationStart -lt 0 -or $registrationEnd -lt 0) {
    throw 'Deployment template check failed: could not delimit the service registration section in the installer.'
}
# Comment-stripped: the block below explains at length why delayed-auto is wrong, and that prose
# quotes the exact token the contract forbids.
$registrationCode = Remove-CommentLines -Text $installerScript.Substring(
    $registrationStart, $registrationEnd - $registrationStart)

Assert-TextMatches -Name 'the service is registered as plain automatic start' `
    -Text $registrationCode -Pattern 'sc\.exe\s+config\s+\$ServiceName\s+start=\s+auto\b'
# Delayed-auto was a two-minute timer standing in for a database wait the API did not have. The
# API waits for connectivity itself now; reintroducing the delay would restore both failure modes
# (idle for two minutes when the database is ready in eight seconds, still too early when it is
# not) and would do so invisibly, because the service does eventually come up either way.
Assert-TextDoesNotMatch -Name 'the service start type must not fall back to a fixed delay' `
    -Text $registrationCode -Pattern 'delayed-auto'
# A gMSA logon needs a DC before the process exists, so no in-process wait can cover it.
Assert-TextMatches -Name 'a gMSA service depends on Netlogon' `
    -Text $registrationCode -Pattern 'sc\.exe\s+config\s+\$ServiceName\s+depend=\s+Netlogon'

Assert-TextMatches -Name 'rollback restores the replaced service dependencies' `
    -Text $installerScript -Pattern '\$Snapshot\.DependOnService\s+-join'
# Comment-stripped, like the adapter and runtime scripts. Every contract below anchors on code,
# and the comments explaining those rules necessarily quote the very things the rules forbid.
$uninstallScript = Remove-CommentLines -Text (Get-Content -LiteralPath $UninstallScriptPath -Raw)
Assert-TextMatches -Name 'uninstaller removes the installation marker' `
    -Text $uninstallScript -Pattern "(?s)Remove-Item[^\r\n]*\`$markerPath"

# Observed on the lab host: the SCM reports 'Stopped' as soon as the service acknowledges the
# control code, but the process kept running for 31 more seconds. Deleting the service in that
# window ORPHANS it - nothing can stop it through the SCM afterwards - and the file deletion then
# rips DLLs out from under a live process, leaving a half-deleted install directory. The wait has
# to sit before sc.exe delete, so the operator still holds a supported way to stop the thing.
#
# Comments are stripped before the indices are taken, and the anchors are code-shaped rather than
# prose-shaped. Both, because the explanatory comment above the wait names 'sc.exe delete' itself -
# and the first version of this check duly failed on correct code.
$uninstallCode = ($uninstallScript -split "`r?`n" | Where-Object { $_.TrimStart() -notmatch '^#' }) -join "`n"
$processWaitIndex = $uninstallCode.IndexOf('$blocking = @(Get-ProcessesUnderPath -Path $InstallPath)')
$serviceDeleteIndex = $uninstallCode.IndexOf('& sc.exe delete $ServiceName')
if ($processWaitIndex -lt 0 -or $serviceDeleteIndex -lt 0) {
    throw 'Deployment template check failed: could not locate the process wait and the service deletion in the uninstaller.'
}
if ($processWaitIndex -gt $serviceDeleteIndex) {
    throw 'Deployment template check failed: the uninstaller deletes the service before waiting for its process to exit, which orphans a running process.'
}
Assert-TextMatches -Name 'uninstaller fails closed while processes still run from the install path' `
    -Text $uninstallScript -Pattern '(?s)Still running: PID.*?\bthrow\b'

# The GUI setup registers its uninstaller as <InstallPath>\unins000.exe and calls this script from
# there, so the uninstaller is itself a process running out of the watched directory - and this
# script's own grandparent. Without excluding its own process tree the guard waits out the full
# timeout and then refuses to uninstall anything, blaming the process doing the uninstalling.
Assert-TextMatches -Name 'the running-process guard ignores the uninstaller that invoked it' `
    -Text $uninstallScript -Pattern '(?s)function Get-ProcessesUnderPath.*?ownTree -contains \$_\.Id'

# sc.exe delete leaves the service key behind while a handle is open, and that key holds the
# Postgres connection string including its password.
Assert-TextMatches -Name 'uninstaller clears the service environment holding the DB secret' `
    -Text $uninstallScript -Pattern "Remove-ItemProperty[^\r\n]*-Name\s+'Environment'"

# The installer writes jwt-secret.key and admin-setup.token owner-only to the SERVICE account, so
# an administrator cannot delete them either. Without taking ownership first, -PurgeData gets
# partway through and throws: measured on the lab host as 12 of 17 entries gone and an aborted run.
$purgeBlockStart = $uninstallScript.IndexOf('if ($PurgeData) {')
if ($purgeBlockStart -lt 0) {
    throw 'Deployment template check failed: could not locate the data purge block in the uninstaller.'
}
$purgeBlock = $uninstallScript.Substring($purgeBlockStart)
Assert-TextMatches -Name 'purging takes ownership before deleting owner-only files' `
    -Text $purgeBlock -Pattern 'takeown\.exe'
# "BUILTIN\Administrators" does not resolve on a non-English Windows.
Assert-TextMatches -Name 'the ownership grant uses the well-known SID, not a localised group name' `
    -Text $purgeBlock -Pattern 'S-1-5-32-544'
Assert-TextDoesNotMatch -Name 'the ownership grant must not use a localised group name' `
    -Text $purgeBlock -Pattern 'BUILTIN\\Administrators'
# (OI) and (CI) are CONTAINER inheritance flags. Applied to a leaf file, icacls drops them, reports
# "Successfully processed 1 files" and adds no ACE whatsoever - so the grant looks like it worked
# and the file stays undeletable. Measured on the lab host against jwt-secret.key.
Assert-TextDoesNotMatch -Name 'the grant must not carry container inheritance flags' `
    -Text $purgeBlock -Pattern 'icacls[^\r\n]*\(OI\)'

# --- server setup wizard contracts -------------------------------------------------------------
# The Pascal side has no unit-test story at all, so what CAN be pinned statically is pinned here
# and the residual gap is written down in deploy/server/README.md rather than left implied.
$serverIss = Get-Content -LiteralPath $ServerIssPath -Raw

# [Run] cannot inspect an exit code. The desktop installer's [Run] entry silently swallows a
# failed provisioning run - it calls exit 1 and Inno still reports success. Forbidding the section
# outright makes that class of defect structurally impossible here rather than merely absent today.
Assert-TextDoesNotMatch -Name 'the server setup must not use a [Run] section' `
    -Text $serverIss -Pattern '(?mi)^\s*\[Run\]'
Assert-TextMatches -Name 'every Exec result is inspected' `
    -Text $serverIss -Pattern '(?m)ResultCode\s*<>\s*0'

# Windows Server 2022 is build 20348. Copying the desktop installer's 22000 would make the SERVER
# setup refuse to run on the only operating system it targets.
Assert-TextMatches -Name 'the server setup runs on Windows Server 2022' `
    -Text $serverIss -Pattern '(?m)^MinVersion=10\.0\.20348\s*$'
Assert-TextDoesNotMatch -Name 'the server setup must not inherit the desktop Windows 11 floor' `
    -Text $serverIss -Pattern '(?m)^MinVersion=10\.0\.22000'
Assert-TextMatches -Name 'a failed setup leaves a log behind' `
    -Text $serverIss -Pattern '(?m)^SetupLogging=yes\s*$'
Assert-TextMatches -Name 'the setup requires elevation' `
    -Text $serverIss -Pattern '(?m)^PrivilegesRequired=admin\s*$'
# Measured on Inno 6.7.3: in ssPostInstall neither RaiseException nor Abort changes the exit code -
# a failed installation still reports 0. Under SCCM that is a deployment claiming success having
# installed nothing, which is the same silent-failure class the [Run] ban above exists to prevent.
# PrepareToInstall returns a message and exits 7.
Assert-TextMatches -Name 'the installation runs where failure can be signalled' `
    -Text $serverIss -Pattern '(?s)function PrepareToInstall.*-Mode Apply'
Assert-TextDoesNotMatch -Name 'the installation must not run in a step that swallows failures' `
    -Text $serverIss -Pattern '(?s)ssPostInstall[^;]*-Mode Apply'
# Everything used at runtime is extracted to {tmp}: the readiness page and PrepareToInstall both
# run before Inno has copied a single file, and 11 MB of redistributable has no business staying
# on the target afterwards either.
Assert-TextMatches -Name 'the runtime payload and artifact are temporary, never installed' `
    -Text $serverIss -Pattern '(?m)^Source:[^\r\n]*payload\\\*"[^\r\n]*dontcopy'
# Inno deduplicates identical source files, so a dontcopy entry and a DestDir entry pointing at
# the same file collapse into one and the dontcopy variant disappears. Keeping the two staging
# trees apart is what prevents that; a dontcopy entry reading from deploy\ would reintroduce it.
Assert-TextDoesNotMatch -Name 'the temporary payload must not share a source tree with the installed scripts' `
    -Text $serverIss -Pattern '(?m)^Source:[^\r\n]*deploy\\[^\r\n]*dontcopy'
# Measured on the lab host: Inno evaluates {code:...} in [UninstallRun] parameters at INSTALL time
# and freezes the result into unins000.dat, so an uninstall-time choice such as /PURGEDATA=1 can
# never reach the script through it - and like [Run] it cannot inspect an exit code either.
Assert-TextDoesNotMatch -Name 'the server setup must not use an [UninstallRun] section' `
    -Text $serverIss -Pattern '(?mi)^\s*\[UninstallRun\]'
Assert-TextMatches -Name 'uninstalling runs the deployment uninstaller from code' `
    -Text $serverIss -Pattern '(?s)usUninstall.*Uninstall-NodePilot\.ps1'
Assert-TextMatches -Name 'the purge switch is built at uninstall time' `
    -Text $serverIss -Pattern '(?s)usUninstall.*UninstallPurgeData then Switches'
# The data directory is ours; the database is not. There is no option to remove it and there must
# not be one: this installer never created it.
Assert-TextDoesNotMatch -Name 'the setup must not offer to drop the database' `
    -Text $serverIss -Pattern 'DROPDATABASE|DropDatabase|DROP DATABASE'
Assert-TextMatches -Name 'the uninstall says the database is left alone' `
    -Text $serverIss -Pattern '(?s)InitializeUninstall[\s\S]*?DATABASE is not affected'
# Removal has to be offered where an operator lands, not only under Apps & Features. The mode
# page is the only screen a second run of the setup reliably shows, so the option lives there.
Assert-TextMatches -Name 'the mode page offers removal' `
    -Text $serverIss -Pattern "ModePage\.Add\('Remove NodePilot from this computer"

# --- readiness page presentation ----------------------------------------------------------------
# Status was carried by text colour alone, which conveys nothing to anyone who cannot tell this
# green from this red, and nothing at all in a greyscale screenshot or a support ticket.
Assert-TextMatches -Name 'a passing check renders a glyph, not just a colour' `
    -Text $serverIss -Pattern 'CheckMarks\[I\]\.Caption := MarkPass'
Assert-TextMatches -Name 'a failing check renders a glyph, not just a colour' `
    -Text $serverIss -Pattern 'CheckMarks\[I\]\.Caption := MarkFail'
# The glyphs are character codes rather than literal characters on purpose: a .iss that only
# compiles when saved in one particular encoding is a trap for whoever edits it next.
Assert-TextDoesNotMatch -Name 'the setup script stays pure ASCII' `
    -Text $serverIss -Pattern '[^\x00-\x7F]'
# Rows are positioned after the captions are set, because a wrapped label only knows its height
# once it has text. Laying out at construction time is what reserved 128 px for checkboxes that
# are almost never shown and squeezed the remediation area down to a single line.
$captionIndex = $serverIss.IndexOf('CheckLabels[I].Caption := Title')
# Anchored on the indented CALL, not on the substring: 'procedure LayoutReadiness();' contains it
# too, sits above the render loop, and made this check fail against a correct file.
$layoutMatch = [regex]::Match($serverIss, '(?m)^[ \t]+LayoutReadiness\(\);')
$layoutIndex = $(if ($layoutMatch.Success) { $layoutMatch.Index } else { -1 })
if ($captionIndex -lt 0 -or $layoutIndex -lt 0) {
    throw 'Deployment template check failed: could not locate the readiness render and layout steps.'
}
if ($layoutIndex -lt $captionIndex) {
    throw 'Deployment template check failed: the readiness page lays rows out before their captions are set.'
}
# A memo sized to the leftovers is what produced a one-line box with a scrollbar that reads as a
# broken edit field. The remediation text is display-only and belongs in a label.
#
# Scoped to the page, not the file: the finish page uses a memo on purpose, because it is
# otherwise empty and a 64-character API key that cannot be selected would have to be retyped.
$readinessStart = $serverIss.IndexOf('procedure CreateReadinessPage()')
$readinessEnd = $serverIss.IndexOf('procedure InitializeWizard', $readinessStart)
if ($readinessStart -lt 0 -or $readinessEnd -lt 0) {
    throw 'Deployment template check failed: could not delimit CreateReadinessPage in the server setup.'
}
$readinessPageCode = $serverIss.Substring($readinessStart, $readinessEnd - $readinessStart)
Assert-TextDoesNotMatch -Name 'the readiness page has no edit control' `
    -Text $readinessPageCode -Pattern 'TNewMemo'

# --- certificate picker ---------------------------------------------------------------------------
# The thumbprint of a certificate already installed on the machine is otherwise only reachable
# through the certificate MMC, whose copy button prepends an invisible U+200E - the reason
# Install-NodePilot.ps1 strips non-hex characters before measuring the length at all. The adapter
# has published the store's contents since the wizard existed and nothing ever read them back.
$compactStart = $serverIss.IndexOf('procedure CompactNetworkPage(')
$certComboStart = $serverIss.IndexOf('procedure CertComboChange(')
$loaderStart = $serverIss.IndexOf('procedure LoadCertificateList(')
$loaderEnd = $serverIss.IndexOf('procedure CreateReadinessPage(')
if ($compactStart -lt 0 -or $certComboStart -le $compactStart -or
    $loaderStart -le $certComboStart -or $loaderEnd -le $loaderStart) {
    throw 'Deployment template check failed: could not delimit the certificate picker in the server setup.'
}
$compactCode = $serverIss.Substring($compactStart, $certComboStart - $compactStart)
$certComboCode = $serverIss.Substring($certComboStart, $loaderStart - $certComboStart)
$loaderCode = $serverIss.Substring($loaderStart, $loaderEnd - $loaderStart)

# The picker shipped drawn as a sliver under the bottom edge of the page. An input page does not
# scroll and gives no indication that anything is below it, so "a few pixels too low" is invisible
# in the source and total on screen. Whatever the reflow computes, the control has to land inside
# the surface.
Assert-TextMatches -Name 'the picker is kept inside the visible surface' `
    -Text $compactCode -Pattern 'CertCombo\.Top \+ CertCombo\.Height > NetworkPage\.SurfaceHeight'
# Reflowed, not split. Five values that answer one question belong on one screen; the space comes
# from Inno's 54 px per label+edit pair, which is about 11 px more than the controls occupy.
Assert-TextMatches -Name 'the page is reflowed from the controls, not from constants' `
    -Text $compactCode -Pattern 'NetworkPage\.PromptLabels\[I\]\.Height'
Assert-TextMatches -Name 'the reflow runs while the wizard is built' `
    -Text $serverIss -Pattern '(?s)procedure InitializeWizard[\s\S]*?CompactNetworkPage\(\);'

# Picking one has to land in the field that the answer file, the validation and the self-signed
# write-back all read. A picker holding its own copy would be a second home for the same value, and
# the wizard would install whichever of the two it happened to read.
Assert-TextMatches -Name 'picking a certificate fills the thumbprint field itself' `
    -Text $certComboCode -Pattern 'NetworkPage\.Values\[4\] := CertThumbprints'
Assert-TextMatches -Name 'the certificate list is loaded when the TLS page is shown' `
    -Text $serverIss `
    -Pattern '(?s)procedure CurPageChanged[\s\S]{0,400}NetworkPage\.ID then[\s\S]{0,80}LoadCertificateList'

# Called from the page handler and nowhere else. A call from InitializeWizard would spawn
# PowerShell during /SILENT, where no page is ever shown - an unattended SCCM run paying for a
# convenience nobody is there to use.
$loaderCalls = @([regex]::Matches($serverIss, '(?m)^[ \t]+LoadCertificateList\(\);'))
if ($loaderCalls.Count -ne 1) {
    throw ("Deployment template check failed: LoadCertificateList is called $($loaderCalls.Count) times; " +
           'it belongs to the TLS page handler alone, so the unattended path never shells out for it.')
}

# The picker is a convenience, never a gate. An unreadable store, a missing adapter or a PowerShell
# that will not start must leave the operator typing the thumbprint by hand exactly as before this
# existed - the prerequisite page verifies it either way, so there is nothing here worth stopping
# an installation over.
Assert-TextDoesNotMatch -Name 'a certificate list that cannot be read must not stop the wizard' `
    -Text $loaderCode -Pattern 'MsgBox|RaiseException'
# Offering a line that failed to parse would put something that is not a thumbprint into the field,
# and the certificate check then reports it as a missing certificate - sending the operator after a
# problem that does not exist.
Assert-TextMatches -Name 'a malformed entry is dropped rather than offered' `
    -Text $loaderCode -Pattern 'Length\(Thumbprint\) <> 40'
# Subject and expiry alone are not unique. On the lab host two certificates share both - "NodePilot
# Lab HTTPS" and "NodePilot Lab SQL TLS", issued 39 seconds apart under the same CN - so the list
# rendered two identical lines, and choosing the wrong one would have handed Kestrel the database's
# certificate in silence. The value that lands in the field has to be on the line.
Assert-TextMatches -Name 'each offered certificate shows the thumbprint it will fill in' `
    -Text $loaderCode -Pattern 'Entry := Subject \+ [^\r\n]*Thumbprint'

# --- finish page ---------------------------------------------------------------------------------
# The adapter has written url / adminSetupToken / externalTriggerApiKey into result.ini since the
# wizard existed, and nothing ever read them back: the finish page showed Inno's stock "Setup has
# finished" and the operator was left without an address, a first-login token, or the API key -
# which is generated by the adapter, omitted from install-report.txt by design, and unrecoverable.
Assert-TextMatches -Name 'the finish page shows the address' `
    -Text $serverIss -Pattern "GetIniString\('result', 'url'"
Assert-TextMatches -Name 'the finish page shows the first-login token' `
    -Text $serverIss -Pattern "GetIniString\('result', 'adminSetupToken'"
Assert-TextMatches -Name 'the finish page shows the external-trigger API key' `
    -Text $serverIss -Pattern "GetIniString\('result', 'externalTriggerApiKey'"
# The summary is assembled while result.ini still exists - DeinitializeSetup wipes the session
# directory, so reading it from the page handler would find nothing. Which FUNCTION each step
# lives in is the invariant; comparing file offsets would only measure declaration order, and
# CurPageChanged is declared above PrepareToInstall regardless of when either runs.
$prepareStart = $serverIss.IndexOf('function PrepareToInstall(')
$prepareEnd = $serverIss.IndexOf('function InitializeUninstall(', $prepareStart)
if ($prepareStart -lt 0 -or $prepareEnd -lt 0) {
    throw 'Deployment template check failed: could not delimit PrepareToInstall in the server setup.'
}
$prepareCode = $serverIss.Substring($prepareStart, $prepareEnd - $prepareStart)

Assert-TextMatches -Name 'the finish summary is built while the session still exists' `
    -Text $prepareCode -Pattern 'BuildFinishSummary\(ResultIni\)'

# A rolled-back run must never present values as if it had succeeded. Inside one function offsets
# do reflect execution order, so: the failure branch comes first, and it leaves. Anchored on the
# branch itself rather than on the first Exit; in the function - PrepareToInstall opens with an
# early Exit; for the session check, which would satisfy a naive ordering test without proving
# anything about the failure path.
$failBranchIndex = $prepareCode.IndexOf('if ResultCode <> 0 then')
$buildIndex = $prepareCode.IndexOf('BuildFinishSummary(ResultIni)')
if ($failBranchIndex -lt 0 -or $buildIndex -lt 0 -or $buildIndex -lt $failBranchIndex) {
    throw 'Deployment template check failed: the finish summary is built before the failure branch runs.'
}
Assert-TextMatches -Name 'a failed run leaves before the finish summary is built' `
    -Text $prepareCode.Substring($failBranchIndex, $buildIndex - $failBranchIndex) -Pattern '(?m)^\s*Exit;\s*$'

$pageChangedStart = $serverIss.IndexOf('procedure CurPageChanged(')
$pageChangedEnd = $serverIss.IndexOf('function ValidatePort(', $pageChangedStart)
if ($pageChangedStart -lt 0 -or $pageChangedEnd -lt 0) {
    throw 'Deployment template check failed: could not delimit CurPageChanged in the server setup.'
}
Assert-TextDoesNotMatch -Name 'the finish page reads no file of its own' `
    -Text $serverIss.Substring($pageChangedStart, $pageChangedEnd - $pageChangedStart) `
    -Pattern 'GetIniString'
# Sliced to the removal branch exactly, rather than bounded by a character count. A window wide
# enough to cover the branch also reaches the /FULLREINSTALL confirmation that follows it, and
# PowerShell's -match is case-insensitive, so a bounded pattern for "must not ask about purging"
# also matched the branch's own help text naming the -PurgeData switch. Both are false positives
# about neighbouring text rather than statements about this branch.
$removalStart = $serverIss.IndexOf('ModePage.SelectedValueIndex = 2')
$removalEnd = $serverIss.IndexOf('ModePage.SelectedValueIndex = 1', $removalStart)
if ($removalStart -lt 0 -or $removalEnd -lt 0) {
    throw 'Deployment template check failed: could not delimit the removal branch in the server setup.'
}
$removalBranch = $serverIss.Substring($removalStart, $removalEnd - $removalStart)

Assert-TextMatches -Name 'choosing removal hands off to the registered uninstaller' `
    -Text $removalBranch -Pattern 'Exec\(UninstPath'
# The uninstaller owns the keep-or-delete-the-data-directory question. A second yes/no here would
# put two prompts behind one decision, which is how operators learn to click prompts away.
Assert-TextDoesNotMatch -Name 'the removal branch must not ask its own yes/no question' `
    -Text $removalBranch -Pattern 'MB_YESNO'
# Without this the handoff pops "are you sure you want to cancel?" for an abort nobody requested.
Assert-TextMatches -Name 'the uninstall handoff suppresses the cancel confirmation' `
    -Text $serverIss -Pattern '(?s)procedure CancelButtonClick[\s\S]{0,400}UninstallHandoff then[\s\S]{0,80}Confirm := False'
# Silently pinning a publisher the operator never saw would be worse than today's explicit
# -TrustedArtifactSignerThumbprint parameter.
Assert-TextMatches -Name 'the pinned signer thumbprint is compiled in' `
    -Text $serverIss -Pattern '\{#SignerThumbprint\}'
# A full re-setup issues a NEW External-Trigger API key and the old one is unrecoverable. That
# needs a confirmation, not a line of body text.
# Bounded, not (?s).* - the file grew a second mbConfirmation for the uninstall question, and an
# unbounded pattern happily spanned to that one, so the check passed even with this confirmation
# downgraded to an OK box. Caught by the mutation harness, which is the point of it.
Assert-TextMatches -Name 'a full re-setup warns that the API key changes' `
    -Text $serverIss -Pattern 'External-Trigger API key[\s\S]{0,400}mbConfirmation, MB_YESNO\) = IDYES'
# The answer file holds the database password; a cancelled wizard must not leave it behind.
Assert-TextMatches -Name 'cancelling the wizard still cleans up the session' `
    -Text $serverIss -Pattern '(?s)procedure DeinitializeSetup.*-Mode Cleanup'
# Unattended deployment (SCCM, GPO) is one of the three reasons the answer file is a file rather
# than a command line - a [SecureString] password cannot be passed as an argument at all.
Assert-TextMatches -Name 'the wizard accepts an externally supplied answer file' `
    -Text $serverIss -Pattern '\{param:ANSWERFILE\|\}'

# The readiness page and PrepareToInstall both run BEFORE [Files] is copied, so an adapter path
# under {app} does not exist yet. Observed on the lab host as a bare "PrepareToInstall failed".
$adapterPathFunction = [regex]::Match($serverIss, '(?s)function AdapterPath\(\).*?\bend;')
if (-not $adapterPathFunction.Success) {
    throw 'Deployment template check failed: could not locate AdapterPath() in the server setup script.'
}
Assert-TextMatches -Name 'the adapter is run from the temporary extraction directory' `
    -Text $adapterPathFunction.Value -Pattern "ExpandConstant\('\{tmp\}"
Assert-TextDoesNotMatch -Name 'the adapter path must not point into the not-yet-installed app directory' `
    -Text $adapterPathFunction.Value -Pattern '\{app\}'
Assert-TextMatches -Name 'the scripts are extracted before the wizard uses them' `
    -Text $serverIss -Pattern "ExtractTemporaryFiles\('\*\.ps1'\)"

# Inno inserts a wizard page directly AFTER the ID it is anchored to, so two pages sharing an
# anchor come out in reverse creation order - and any page anchored to the earlier of the two then
# lands in front of the later one. Anchoring both the SQL and the PostgreSQL page to the provider
# page produced Provider -> Postgres -> Network -> Prerequisites -> Sql: the SQL page sat after the
# page that reads its values, so it was never shown and its fields kept their defaults. Every page
# must therefore be anchored to a DISTINCT predecessor.
$pageAnchors = @([regex]::Matches($serverIss, 'Create(?:InputQuery|InputOption|Custom)Page\(\s*([A-Za-z0-9_]+(?:\.ID)?)') |
    ForEach-Object { $_.Groups[1].Value })
$duplicateAnchors = @($pageAnchors | Group-Object | Where-Object { $_.Count -gt 1 })
if ($duplicateAnchors.Count -gt 0) {
    throw ("Deployment template check failed: wizard pages share an anchor (" +
           ($duplicateAnchors.Name -join ', ') +
           '). Inno inserts each page directly after its anchor, so sharing one silently reorders ' +
           'the wizard - anchor every page to the one created before it.')
}

# An input page offers 309 pixels of surface and each label+edit pair costs 54, so the sixth field
# is laid out at 337 and simply is not drawn. Measured, not estimated. The page does not scroll and
# gives no indication that anything is missing: the PostgreSQL page's root-certificate field was
# invisible, and setup then failed on a value the operator was never shown a box for.
$queryPageNames = @([regex]::Matches($serverIss, '(?m)^\s*([A-Za-z0-9_, ]+):\s*TInputQueryWizardPage;') |
    ForEach-Object { $_.Groups[1].Value -split ',' } |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ })
if ($queryPageNames.Count -eq 0) {
    throw 'Deployment template check failed: could not find any TInputQueryWizardPage declarations in the server setup script.'
}
$fieldCounts = @{}
foreach ($match in [regex]::Matches($serverIss, '(?m)^\s*([A-Za-z0-9_]+)\.Add\(')) {
    $pageName = $match.Groups[1].Value
    if ($queryPageNames -notcontains $pageName) { continue }
    if (-not $fieldCounts.ContainsKey($pageName)) { $fieldCounts[$pageName] = 0 }
    $fieldCounts[$pageName]++
}
$overfullPages = @($fieldCounts.GetEnumerator() | Where-Object { $_.Value -gt 5 })
if ($overfullPages.Count -gt 0) {
    throw ('Deployment template check failed: wizard input page(s) with more than five fields (' +
           (($overfullPages | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', ') +
           '). Only five label+edit pairs fit on an Inno input page; the rest are laid out below ' +
           'the visible surface and the page does not scroll. Split the page.')
}

# --- listen-port pre-flight ------------------------------------------------------------------
# Added after a lab install died on it: Kestrel could not bind port 80, which HTTP.SYS reserves on
# any host running IIS, so the service crashed at startup and the operator watched a 180-second
# health probe expire followed by a rollback. The checks covered .NET, the certificate, the gMSA and
# the database - everything except whether the thing could listen.
$preflightStripped = Remove-CommentLines -Text (Get-Content -LiteralPath $PreflightScriptPath -Raw)
Assert-TextMatches -Name 'the pre-flight checks whether the ports can be bound' `
    -Text $preflightStripped -Pattern '(?s)function Invoke-NodePilotPreflight[\s\S]*?Test-NodePilotListenPorts'
# 10013 is not "in use" - Windows returns it for a reservation with no listener behind it, so a
# message about a busy port would send the operator hunting a process that does not exist.
Assert-TextMatches -Name 'a reserved port is told apart from an occupied one' `
    -Text $preflightStripped -Pattern 'SocketError\]::AccessDenied'
# The other face of the same reservation, and the one the lab host actually shows: HTTP.SYS holds
# the listener in the kernel, so it surfaces as PID 4 rather than as a bind failure. "In use by
# System (PID 4)" is true and sends the operator after a process nobody can stop.
Assert-TextMatches -Name 'a kernel-held port is not reported as an ordinary process' `
    -Text $preflightStripped -Pattern 'OwningProcess -le 4'
# Kestrel binds the wildcard address - the crash came out of AnyIPListenOptions.BindAsync. Probing
# loopback would pass on a port that is reserved on 0.0.0.0.
Assert-TextMatches -Name 'the port probe binds the address Kestrel binds' `
    -Text $preflightStripped -Pattern 'IPAddress\]::Any'
# A probe, not a change: it runs again on every click of "Check again".
Assert-TextMatches -Name 'the port probe releases what it binds' `
    -Text $preflightStripped -Pattern 'try \{ \$probe\.Start\(\) \} finally \{ \$probe\.Stop\(\) \}'
# The wizard reads a fixed list of check ids; a check the adapter reports and the page never asks
# for is invisible, which is indistinguishable from not having written it.
Assert-TextMatches -Name 'the readiness page asks for the port check' `
    -Text $serverIss -Pattern "CheckIds\[\d\] := 'ports';"
$declaredCheckCount = [int]([regex]::Match($serverIss, 'CheckCount\s*=\s*(\d+)').Groups[1].Value)
$assignedCheckIds = @([regex]::Matches($serverIss, "CheckIds\[\d+\] := '")).Count
if ($declaredCheckCount -ne $assignedCheckIds) {
    throw ("Deployment template check failed: CheckCount is $declaredCheckCount but $assignedCheckIds " +
           'check ids are assigned. The array is fixed-size, so a mismatch either drops the last ' +
           'check from the page or reads past the end of the array.')
}

# --- installation progress -------------------------------------------------------------------
# The wizard sat on "Preparing to Install" showing nothing for the whole run - 136 s healthy, 187 s
# when the health probe is lost - long enough for Windows to grey the window out as "Not
# responding", which is how it was read.
#
# Dot-sourced rather than pattern-matched: the phase table IS the contract here, and reading the
# real one is the only way this guard is worth having. SetupContract.ps1 has no top-level code -
# Test-SetupAdapter.ps1 loads it the same way.
. $SetupContractPath
$installPhases = @(Get-NodePilotInstallPhases)
if ($installPhases.Count -eq 0) {
    throw 'Deployment template check failed: the install progress phase table is empty.'
}
# THE drift guard. The bar is driven by matching the installer's own output, so a renamed step does
# not break anything loudly - it silently produces a bar that stops at the phase before it and an
# operator who watches a frozen 25% for two minutes.
function Get-DeclaredStepPrefixes {
    param([Parameter(Mandatory)][string]$Script)
    $prefixes = @()
    foreach ($match in [regex]::Matches($Script, "Write-Step\s+(?:'([^']*)'|`"([^`"]*)`")")) {
        $literal = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
        # Cut at the first interpolation: "Stopping service '$ServiceName'" is only ever knowable
        # up to "Stopping service ", which is exactly what the table matches on.
        $dollar = $literal.IndexOf('$')
        if ($dollar -ge 0) { $literal = $literal.Substring(0, $dollar) }
        $literal = $literal.TrimEnd()
        if ($literal) { $prefixes += $literal }
    }
    return $prefixes
}

$updatePhases = @(Get-NodePilotUpdatePhases)
foreach ($pair in @(
    @{ Name = 'installer'; Script = $installerScript;                                  Phases = $installPhases },
    @{ Name = 'updater';   Script = (Get-Content -LiteralPath $UpdateScriptPath -Raw); Phases = $updatePhases })) {

    $declaredSteps = @(Get-DeclaredStepPrefixes -Script $pair.Script)
    if ($declaredSteps.Count -eq 0) {
        throw "Deployment template check failed: found no Write-Step announcements in the $($pair.Name)."
    }

    # Forward: an entry whose step has been renamed away would stop the bar at the phase before it.
    foreach ($phase in $pair.Phases) {
        $found = @($declaredSteps | Where-Object { $_.StartsWith($phase.Step, [System.StringComparison]::Ordinal) })
        if ($found.Count -eq 0) {
            throw ("Deployment template check failed: progress phase '$($phase.Step)' no longer " +
                   "exists as a Write-Step in the $($pair.Name). The bar would stop at the phase " +
                   'before it and never move again.')
        }
    }

    # Backward, and the direction that was missing. The forward guard only asks whether every table
    # entry still exists in the script; it says nothing about a step in the script that no entry
    # covers. That gap shipped: the updater announces four phases and the table listed two, the
    # installer ten and the table nine, so the bar stood still through half of an update and
    # nothing went red. Both omissions came from the same sloppy search - one pattern found only
    # double-quoted steps, the other was anchored at the start of a line and missed the indented
    # ones.
    foreach ($declared in $declaredSteps) {
        $covered = @($pair.Phases | Where-Object { $declared.StartsWith($_.Step, [System.StringComparison]::Ordinal) })
        if ($covered.Count -eq 0) {
            throw ("Deployment template check failed: the $($pair.Name) announces a phase " +
                   "('$declared') that no progress entry covers. It would run with neither the bar " +
                   'nor the caption moving, which is indistinguishable from being stuck.')
        }
    }
}
# A bar that can go backwards is a bar nobody believes. Ascending percentages are what make the
# wizard's "never retreat" rule hold without keeping state per phase.
$previous = -1
foreach ($phase in $installPhases) {
    if ([int]$phase.Percent -le $previous) {
        throw ("Deployment template check failed: install progress phase '$($phase.Step)' is not " +
               'above the phase before it. The percentages must ascend.')
    }
    $previous = [int]$phase.Percent
}
# The service start waits up to 180 s on a health probe. A bar that stops there without saying why
# is the same silence this replaced, just at 80% instead of 0.
$lastPhase = $installPhases[$installPhases.Count - 1]
if ($lastPhase.Text -notmatch 'minutes') {
    throw ("Deployment template check failed: the last install phase ('$($lastPhase.Step)') must " +
           'say how long it can take - it is the one that waits on the health probe.')
}

# Without the file, every Write-NodePilotProgress call in the adapter goes nowhere. That was the
# state this feature shipped in: the writing side existed and nothing ever asked for it.
Assert-TextMatches -Name 'the wizard asks the adapter for progress' `
    -Text $serverIss -Pattern '(?s)-Mode Apply[\s\S]{0,400}-ProgressFile'
# Synchronous Exec blocks Inno's UI thread for the whole installation, which is what produced the
# frozen window. The installation - and only the installation - runs detached.
Assert-TextMatches -Name 'the installation runs without blocking the interface' `
    -Text $serverIss -Pattern '(?s)function RunAdapterWithProgress[\s\S]*?StartPowerShell\(Arguments\)'
# Sliced to the function, not matched across the file. The first version of this check searched
# from "function StartPowerShell" to the next ewNoWait anywhere below it - and the uninstaller
# handoff further down uses ewNoWait too, so the contract passed with the start switched back to
# ewWaitUntilTerminated. It measured the wrong line entirely.
$startStart = $serverIss.IndexOf('function StartPowerShell(')
$startEnd = $serverIss.IndexOf('function StripBom(', $startStart)
if ($startStart -lt 0 -or $startEnd -le $startStart) {
    throw 'Deployment template check failed: could not delimit StartPowerShell in the server setup.'
}
$startCode = $serverIss.Substring($startStart, $startEnd - $startStart)
Assert-TextMatches -Name 'the detached start uses ewNoWait' `
    -Text $startCode -Pattern 'ewNoWait'
Assert-TextDoesNotMatch -Name 'the detached start must not wait' `
    -Text $startCode -Pattern 'ewWaitUntilTerminated'
# With ewNoWait there is no exit code to read from Exec. Trusting it would report every failed
# installation as a success.
Assert-TextMatches -Name 'the outcome is read from the result file, not from Exec' `
    -Text $serverIss -Pattern "(?s)function RunAdapterWithProgress[\s\S]*?GetIniString\('summary', 'exitCode'"
# Inno's Pascal exposes no message pump (AppProcessMessages, ProcessMessages and Application are
# all unknown identifiers in 6.7.3), so something in the loop has to touch the window every tick.
Assert-TextMatches -Name 'the wait loop keeps the window alive on every tick' `
    -Text $serverIss -Pattern '(?s)repeat[\s\S]{0,600}ProgressPage\.SetProgress\(Shown, 100\)'
# An adapter killed from Task Manager never writes result.ini. Without a bound, the wizard would
# wait for it forever.
Assert-TextMatches -Name 'the wait loop cannot run forever' `
    -Text $serverIss -Pattern '(?s)repeat[\s\S]*?until Elapsed >= AdapterTimeoutMs'

# --- setup adapter contracts ---------------------------------------------------------------------
$setupAdapter = Remove-CommentLines -Text (Get-Content -LiteralPath $SetupAdapterPath -Raw)

# Update-NodePilot.ps1 derives the probe port from the installed Kestrel configuration. Passing the
# 443 default rolled back a healthy 8443 installation in the lab on 2026-08-01; the adapter must
# not reintroduce that by being helpful.
$updateBranchStart = $setupAdapter.IndexOf('function Invoke-SetupUpdate')
if ($updateBranchStart -lt 0) {
    throw 'Deployment template check failed: could not locate the update path in the setup adapter.'
}
# Bounded at the invocation, not at the end of the file. What matters is the splat handed to
# Update-NodePilot.ps1; after the call the adapter legitimately READS the installed
# Kestrel:Https:HttpsPort to compose the address for the finish page, and a file-wide ban on the
# word could not tell that apart from passing it.
$updateInvokeIndex = $setupAdapter.IndexOf("'Update-NodePilot.ps1'", $updateBranchStart)
if ($updateInvokeIndex -lt 0) {
    throw 'Deployment template check failed: could not locate the updater invocation in the setup adapter.'
}
$updateBranch = $setupAdapter.Substring($updateBranchStart, $updateInvokeIndex - $updateBranchStart)
# Matched without the leading hyphen on purpose: the adapter splats, so the realistic way this
# regresses is a bare `HttpsPort = 443` hashtable key, not a `-HttpsPort` argument. The first
# version of this check only looked for the hyphenated form and a mutation walked straight past it.
Assert-TextDoesNotMatch -Name 'the adapter must not pass a HTTPS port to the updater' `
    -Text $updateBranch -Pattern '\bHttpsPort\b'

# powershell.exe -File returns 0 for a script that merely wrote errors, so an implicit
# fall-through would report a failed installation as success.
Assert-TextMatches -Name 'the adapter exits explicitly' `
    -Text $setupAdapter -Pattern '(?m)^exit \$exitCode\s*$'
Assert-TextMatches -Name 'the answer file is shredded in a finally block' `
    -Text $setupAdapter -Pattern '(?s)finally \{.*Remove-NodePilotAnswerFile'
# The answer file carries the database password in clear text for the duration of the run.
Assert-TextDoesNotMatch -Name 'the adapter must not log the password' `
    -Text $setupAdapter -Pattern 'Write-(Host|Output|Information)[^\r\n]*[Pp]assword'

# The wizard calls this from the TLS page - before an answer file exists, before anything has
# created the session directory, and while the operator is waiting on a page to finish drawing.
# Requiring either input would make the picker fail precisely where it is meant to help, and
# folding it into Probe would make it wait on a database connection to list a local store.
$certificateModeStart = $setupAdapter.IndexOf("'Certificates' {")
if ($certificateModeStart -lt 0) {
    throw 'Deployment template check failed: the setup adapter has no Certificates mode for the wizard to call.'
}
$certificateModeEnd = $setupAdapter.IndexOf("'Provision' {", $certificateModeStart)
if ($certificateModeEnd -lt 0) {
    throw 'Deployment template check failed: could not delimit the Certificates mode in the setup adapter.'
}
Assert-TextDoesNotMatch -Name 'listing certificates needs no answer file' `
    -Text $setupAdapter.Substring($certificateModeStart, $certificateModeEnd - $certificateModeStart) `
    -Pattern 'Read-NodePilotAnswerFile|Invoke-NodePilotPreflight'

# The probe can only check the ports it is told about. Without this the readiness page would report
# on the 443 default while the operator installs on 8443.
Assert-TextMatches -Name 'the configured ports reach the pre-flight' `
    -Text $setupAdapter -Pattern "(?s)function ConvertTo-NodePilotPreflightParameters[\s\S]*?HttpsPort\s*=\s*\[int\]\`$Answers\['network\.httpsPort'\]"

# "Service did not report /healthz/ready within 180s" names a symptom. The cause is one line in the
# Application log, and without it the operator is left with a rollback and no reason for it.
Assert-TextMatches -Name 'a failed install reports why the service would not start' `
    -Text $setupAdapter -Pattern '(?s)catch \{[\s\S]{0,400}Get-NodePilotServiceCrashReason'
# Scoped to this run: an exception left in the log by an earlier attempt would otherwise be
# presented as the reason for this one.
Assert-TextMatches -Name 'the crash lookup is bounded to the current run' `
    -Text $setupAdapter -Pattern '(?s)function Get-NodePilotServiceCrashReason[\s\S]*?StartTime = \$Since'
# A diagnostic that throws would replace the real failure with its own.
Assert-TextMatches -Name 'the crash lookup cannot itself fail the run' `
    -Text $setupAdapter -Pattern "(?s)function Get-NodePilotServiceCrashReason[\s\S]*?catch \{ return '' \}"

# One emitter serving both the probe and the standalone mode. Two copies would drift into different
# field orders behind a Pascal reader that splits on position and has no way to notice.
$inventoryReads = @([regex]::Matches($setupAdapter, 'Get-NodePilotCertificateInventory'))
if ($inventoryReads.Count -ne 1) {
    throw ("Deployment template check failed: the setup adapter reads the certificate store in " +
           "$($inventoryReads.Count) places; both callers must go through " +
           'Add-NodePilotCertificateInventory so the line format cannot fork.')
}

# --- runtime payload contracts --------------------------------------------------------------------
$runtimeScript = Remove-CommentLines -Text (Get-Content -LiteralPath $RuntimePayloadScriptPath -Raw)

# deploy/README.md tells operators to install the plain runtime and specifically NOT the Hosting
# Bundle, which rewires IIS and restarts W3SVC on shared hosts. Bundling the wrong one would make
# the installer contradict its own documentation.
Assert-TextMatches -Name 'the payload is the standalone ASP.NET Core runtime' `
    -Text $runtimeScript -Pattern 'aspnetcore-runtime-win-x64\.exe'
Assert-TextDoesNotMatch -Name 'the payload must not be the IIS Hosting Bundle' `
    -Text $runtimeScript -Pattern 'dotnet-hosting-'
# This is a supply-chain boundary, not a convenience download.
Assert-TextMatches -Name 'the download is verified against the published digest' `
    -Text $runtimeScript -Pattern 'Get-Sha512'
Assert-TextMatches -Name 'the download is verified against a committed pin' `
    -Text $runtimeScript -Pattern 'Runtime payload hash mismatch'
Assert-TextMatches -Name 'the download must be signed by Microsoft' `
    -Text $runtimeScript -Pattern "SignerCertificate\.Subject -notmatch 'Microsoft Corporation'"
Assert-TextDoesNotMatch -Name 'certificate validation is never relaxed for the download' `
    -Text $runtimeScript -Pattern 'SkipCertificateCheck|ServerCertificateValidationCallback|TrustAllCerts'

$ssoDocumentation = Get-Content -LiteralPath $SsoDocumentationPath -Raw
Assert-TextMatches -Name 'SPN examples use duplicate-safe registration' `
    -Text $ssoDocumentation -Pattern '(?m)^\s*setspn\s+-S\s+HTTP/'
Assert-TextDoesNotMatch -Name 'SPN examples must not use duplicate-unsafe registration' `
    -Text $ssoDocumentation -Pattern '(?m)^\s*setspn\s+-A\s+HTTP/'

Write-Host ("Deployment template checks passed ({0} HAProxy contracts, appsettings JSON, installer, update, pre-flight, uninstall, server setup, adapter, runtime payload and SPN contracts)." -f `
    $requiredHaproxyContracts.Count) -ForegroundColor Green
