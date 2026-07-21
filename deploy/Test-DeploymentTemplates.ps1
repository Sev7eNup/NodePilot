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
    [string]$SsoDocumentationPath
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

foreach ($path in @($HaproxyTemplatePath, $AppSettingsTemplatePath, $InstallerPath, $SsoDocumentationPath)) {
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

$ssoDocumentation = Get-Content -LiteralPath $SsoDocumentationPath -Raw
Assert-TextMatches -Name 'SPN examples use duplicate-safe registration' `
    -Text $ssoDocumentation -Pattern '(?m)^\s*setspn\s+-S\s+HTTP/'
Assert-TextDoesNotMatch -Name 'SPN examples must not use duplicate-unsafe registration' `
    -Text $ssoDocumentation -Pattern '(?m)^\s*setspn\s+-A\s+HTTP/'

Write-Host ("Deployment template checks passed ({0} HAProxy contracts, appsettings JSON, installer and SPN contracts)." -f `
    $requiredHaproxyContracts.Count) -ForegroundColor Green
