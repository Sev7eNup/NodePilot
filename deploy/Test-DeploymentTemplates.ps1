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

foreach ($path in @($HaproxyTemplatePath, $AppSettingsTemplatePath, $InstallerPath, $SsoDocumentationPath, $BuildScriptPath, $BuildPropsPath, $UpdateScriptPath, $PreflightScriptPath, $UninstallScriptPath, $SetupAdapterPath, $ServerIssPath, $RuntimePayloadScriptPath)) {
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

# The marker is how any later tool finds this installation. Leaving it behind on uninstall makes
# every subsequent fresh install look like an upgrade.
Assert-TextMatches -Name 'installer records a machine-wide installation marker' `
    -Text $installerScript -Pattern 'HKLM:\\SOFTWARE\\NodePilot\\Server'
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

# --- setup adapter contracts ---------------------------------------------------------------------
$setupAdapter = Remove-CommentLines -Text (Get-Content -LiteralPath $SetupAdapterPath -Raw)

# Update-NodePilot.ps1 derives the probe port from the installed Kestrel configuration. Passing the
# 443 default rolled back a healthy 8443 installation in the lab on 2026-08-01; the adapter must
# not reintroduce that by being helpful.
$updateBranchStart = $setupAdapter.IndexOf('function Invoke-SetupUpdate')
if ($updateBranchStart -lt 0) {
    throw 'Deployment template check failed: could not locate the update path in the setup adapter.'
}
$updateBranch = $setupAdapter.Substring($updateBranchStart)
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
