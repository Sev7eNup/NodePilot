#requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
. (Join-Path $PSScriptRoot 'LocalServerHttp.ps1')

$script:passed = 0
function Assert-LocalHttps {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
    $script:passed++
}

function Import-SetupFunction {
    param([string]$FileName, [string]$Name)
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $FileName), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw "Could not parse $FileName" }
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name }, $true)
    if (-not $function) { throw "Missing function $Name" }
    return [scriptblock]::Create($function.Extent.Text)
}

$rsa = [Security.Cryptography.RSA]::Create(2048)
$certificate = $null; $otherCertificate = $null
try {
    $certificateRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=setup.synthetic.invalid', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $certificate = $certificateRequest.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(1))
    $otherCertificate = $certificateRequest.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-2), [DateTimeOffset]::UtcNow.AddDays(2))
    $policyProperty = [Net.ServicePointManager].GetProperty('CertificatePolicy')
    $previousPolicy = if ($policyProperty) { $policyProperty.GetValue($null) } else { $null }
    $previousCallback = [Net.ServicePointManager]::ServerCertificateValidationCallback
    $request = [NodePilot.Deployment.LocalServerHttp]::CreateRequest([uri]'https://localhost:12345/healthz/ready', $certificate.Thumbprint, 1000)
    $errors = [Net.Security.SslPolicyErrors]::RemoteCertificateNameMismatch -bor [Net.Security.SslPolicyErrors]::RemoteCertificateChainErrors
    Assert-LocalHttps ($request.ServerCertificateValidationCallback.Invoke($request, $certificate, $null, $errors)) 'The selected self-signed public-host certificate must identify the loopback server.'
    Assert-LocalHttps (-not $request.ServerCertificateValidationCallback.Invoke($request, $otherCertificate, $null, [Net.Security.SslPolicyErrors]::None)) 'A different certificate must fail even with a valid chain and hostname.'
    Assert-LocalHttps (-not $request.ServerCertificateValidationCallback.Invoke($request, $null, $null, $errors)) 'A missing certificate must fail.'
    Assert-LocalHttps (-not $request.AllowAutoRedirect) 'Bootstrap credentials must not follow redirects.'
    Assert-LocalHttps ($null -eq $request.Proxy) 'Loopback setup must not use a configured proxy.'
    if ($policyProperty) {
        Assert-LocalHttps ([object]::ReferenceEquals($previousPolicy, $policyProperty.GetValue($null))) 'The global certificate policy changed.'
    }
    Assert-LocalHttps ([object]::ReferenceEquals($previousCallback, [Net.ServicePointManager]::ServerCertificateValidationCallback)) 'The global validation callback changed.'
    foreach ($uri in @('http://localhost:12345/', 'https://server.synthetic.invalid/', 'https://user@localhost/')) {
        $rejected = $false
        try { [NodePilot.Deployment.LocalServerHttp]::CreateRequest([uri]$uri, $certificate.Thumbprint, 1000) | Out-Null }
        catch { $rejected = $true }
        Assert-LocalHttps $rejected "Unsafe setup URI accepted: $uri"
    }
    $rejected = $false
    try { [NodePilot.Deployment.LocalServerHttp]::CreateRequest([uri]'https://localhost/', '', 1000) | Out-Null }
    catch { $rejected = $true }
    Assert-LocalHttps $rejected 'Missing certificate pin was accepted.'

    # The unpinned request of the update probe keeps every other loopback rule and stays a plain GET.
    $unpinned = [NodePilot.Deployment.LocalServerHttp]::CreateUnpinnedRequest([uri]'https://localhost:12345/healthz/ready', 1000)
    Assert-LocalHttps ($unpinned.ServerCertificateValidationCallback.Invoke($unpinned, $otherCertificate, $null, $errors)) 'The unpinned probe rejected a certificate.'
    Assert-LocalHttps (-not $unpinned.AllowAutoRedirect -and $null -eq $unpinned.Proxy) 'The unpinned probe follows redirects or uses a proxy.'
    Assert-LocalHttps ([object]::ReferenceEquals($previousCallback, [Net.ServicePointManager]::ServerCertificateValidationCallback)) 'The unpinned probe changed the global validation callback.'
    foreach ($uri in @('http://localhost:12345/', 'https://server.synthetic.invalid/', 'https://user@localhost/')) {
        $rejected = $false
        try { [NodePilot.Deployment.LocalServerHttp]::CreateUnpinnedRequest([uri]$uri, 1000) | Out-Null }
        catch { $rejected = $true }
        Assert-LocalHttps $rejected "Unsafe unpinned URI accepted: $uri"
    }
    foreach ($unsafe in @(@{ Method = 'POST'; Body = '{}' }, @{ Headers = @{ 'X-Setup-Token' = 'synthetic-token' } })) {
        $rejected = $false
        try { Invoke-NodePilotLocalHttps -Uri 'https://localhost:12345/' -SkipCertificateCheck -TimeoutSeconds 1 @unsafe | Out-Null }
        catch { $rejected = $_.Exception.Message -like '*only be a plain GET*' }
        Assert-LocalHttps $rejected "An unpinned request accepted $(@($unsafe.Keys) -join ', ')."
    }

    # Windows PowerShell without the system-default switch would offer only TLS 1.0 to Kestrel.
    $legacy = [Net.SecurityProtocolType]::Tls
    $offered = Get-NodePilotLocalHttpsProtocol -Current $legacy
    Assert-LocalHttps (($offered -band [Net.SecurityProtocolType]::Tls12) -and ($offered -band [Net.SecurityProtocolType]::Tls13) -and
        ($offered -band $legacy)) 'TLS 1.2 and 1.3 must be added to a legacy protocol setting.'
    Assert-LocalHttps ([int](Get-NodePilotLocalHttpsProtocol -Current ([Net.SecurityProtocolType]0)) -eq 0) 'The system default must be left to the OS.'
    $previousProtocol = [Net.ServicePointManager]::SecurityProtocol
    $closedProbe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $closedProbe.Start()
    $closedPort = ([System.Net.IPEndPoint]$closedProbe.LocalEndpoint).Port
    $closedProbe.Stop()
    try {
        [Net.ServicePointManager]::SecurityProtocol = $legacy
        $failed = $false
        try { Invoke-NodePilotLocalHttps -Uri "https://localhost:$closedPort/healthz/ready" -CertificateThumbprint $certificate.Thumbprint -TimeoutSeconds 2 | Out-Null }
        catch { $failed = $true }
        Assert-LocalHttps $failed 'A closed port must fail the request.'
        Assert-LocalHttps ([Net.ServicePointManager]::SecurityProtocol -eq $legacy) 'The process-wide protocol setting must be restored after the request.'
    }
    finally { [Net.ServicePointManager]::SecurityProtocol = $previousProtocol }

    . (Import-SetupFunction -FileName 'Install-NodePilot.ps1' -Name 'Invoke-HealthProbe')
    . (Import-SetupFunction -FileName 'Invoke-NodePilotSetup.ps1' -Name 'Invoke-NodePilotFirstLogin')
    $script:requests = @()
    $script:statusCode = 200
    $script:failWith = $null
    function Invoke-NodePilotLocalHttps {
        param($Uri, $CertificateThumbprint, [switch]$SkipCertificateCheck, $Method = 'GET', $Body, $Headers, $TimeoutSeconds)
        $script:requests += [pscustomobject]@{Uri=$Uri; Pin=$CertificateThumbprint; Unpinned=[bool]$SkipCertificateCheck; Method=$Method; Body=$Body; Headers=$Headers}
        if ($script:failWith) { throw $script:failWith }
        return [pscustomobject]@{StatusCode=$script:statusCode}
    }
    Assert-LocalHttps (Invoke-HealthProbe -Url 'https://localhost:12345/healthz/ready' -CertificateThumbprint $certificate.Thumbprint) 'Pinned readiness response was rejected.'
    $result = Invoke-NodePilotFirstLogin -HttpsPort 12345 -CertificateThumbprint $certificate.Thumbprint -Username 'synthetic-admin' -Password 'synthetic-password' -Token 'synthetic-token'
    Assert-LocalHttps ($result.Status -eq 'Created') 'Pinned bootstrap response was rejected.'
    Assert-LocalHttps (@($script:requests | Where-Object Pin -ne $certificate.Thumbprint).Count -eq 0) 'A caller lost the configured certificate pin.'
    Assert-LocalHttps ($script:requests[1].Headers['X-Setup-Token'] -eq 'synthetic-token') 'Bootstrap token was not sent through the pinned transport.'
    $script:statusCode = 302
    $result = Invoke-NodePilotFirstLogin -HttpsPort 12345 -CertificateThumbprint $certificate.Thumbprint -Username 'synthetic-admin' -Password 'synthetic-password' -Token 'synthetic-token'
    Assert-LocalHttps ($result.Status -eq 'Failed') 'Redirect was treated as successful bootstrap.'

    # A rejected login reaches the caller as a WebException wrapped by the .NET method call; the
    # server's message must still be reported.
    if (-not ('NodePilot.Deployment.Tests.SyntheticErrorResponse' -as [type])) {
        Add-Type -IgnoreWarnings -TypeDefinition @'
namespace NodePilot.Deployment.Tests
{
    public sealed class SyntheticErrorResponse : System.Net.WebResponse
    {
        private readonly byte[] _body;
        public SyntheticErrorResponse(string body) { _body = System.Text.Encoding.UTF8.GetBytes(body); }
        public override System.IO.Stream GetResponseStream() { return new System.IO.MemoryStream(_body); }
    }
}
'@
    }
    $serverMessage = '{"error":"synthetic password policy rejection"}'
    $webError = New-Object Net.WebException('The remote server returned an error: (400) Bad Request.', $null,
        [Net.WebExceptionStatus]::ProtocolError, (New-Object NodePilot.Deployment.Tests.SyntheticErrorResponse($serverMessage)))
    $script:failWith = New-Object Management.Automation.MethodInvocationException('Exception calling "GetResponse" with "0" argument(s)', $webError)
    $result = Invoke-NodePilotFirstLogin -HttpsPort 12345 -CertificateThumbprint $certificate.Thumbprint -Username 'synthetic-admin' -Password 'synthetic-password' -Token 'synthetic-token'
    Assert-LocalHttps ($result.Status -eq 'Failed' -and $result.Detail -eq $serverMessage) 'A rejected bootstrap login must report the server message.'
    $script:failWith = $null

    # The updater reads the pin from the installed configuration: no prompt, no parameter.
    . (Import-SetupFunction -FileName 'Update-NodePilot.ps1' -Name 'Get-NodePilotProbeSettings')
    . (Import-SetupFunction -FileName 'Update-NodePilot.ps1' -Name 'Wait-NodePilotReady')
    function Write-Info { param([string]$Text) }
    # Written by Windows PowerShell with a BOM, the thumbprint pasted from certmgr: lower case,
    # spaced and led by an invisible direction mark. The service accepts that, so the probe must.
    $pasted = [char]0x200E + (($certificate.Thumbprint.ToLowerInvariant() -replace '(..)', '$1 ').Trim())
    $settingsJson = '{"Kestrel":{"Https":{"Enabled":true,"HttpsPort":8443,"HttpPort":80,"BindHttp":true,' +
        '"CertificateLocation":"LocalMachine","CertificateStore":"My","CertificateThumbprint":"' + $pasted + '"}}}'
    $settingsBytes = [byte[]](@(0xEF, 0xBB, 0xBF) + [Text.Encoding]::UTF8.GetBytes($settingsJson))
    $probeSettings = Get-NodePilotProbeSettings -SettingsBytes $settingsBytes
    Assert-LocalHttps ($probeSettings.HttpsPort -eq 8443) 'The update probe ignored the installed HTTPS port.'
    Assert-LocalHttps ($probeSettings.CertificateThumbprint -ceq $certificate.Thumbprint.ToUpperInvariant()) 'The update probe did not normalise the configured thumbprint.'
    $stringEnabled = Get-NodePilotProbeSettings -SettingsBytes ([Text.Encoding]::UTF8.GetBytes(
        '{"Kestrel":{"Https":{"Enabled":"True","CertificateThumbprint":"' + $certificate.Thumbprint + '"}}}'))
    Assert-LocalHttps ($stringEnabled.CertificateThumbprint -eq $certificate.Thumbprint -and $stringEnabled.HttpsPort -eq 0) 'Enabled as a string must count, and an absent port must stay unset.'
    $plain = Get-NodePilotProbeSettings -SettingsBytes ([Text.Encoding]::UTF8.GetBytes(
        '{"Kestrel":{"Https":{"Enabled":false,"HttpsPort":8080,"CertificateThumbprint":null}}}'))
    Assert-LocalHttps ($plain.CertificateThumbprint -eq '' -and $plain.HttpsPort -eq 8080) 'Kestrel:Https off must select the plain-HTTP probe.'
    $noKestrel = Get-NodePilotProbeSettings -SettingsBytes ([Text.Encoding]::UTF8.GetBytes('{"AllowedHosts":"*"}'))
    Assert-LocalHttps ($noKestrel.CertificateThumbprint -eq '' -and $noKestrel.HttpsPort -eq 0) 'A configuration without Kestrel:Https must select the plain-HTTP probe.'
    foreach ($unusable in @('', 'not-a-thumbprint', 'AB12')) {
        $rejected = $false
        try {
            [void](Get-NodePilotProbeSettings -SettingsBytes ([Text.Encoding]::UTF8.GetBytes(
                '{"Kestrel":{"Https":{"Enabled":true,"CertificateThumbprint":"' + $unusable + '"}}}')))
        } catch { $rejected = $true }
        Assert-LocalHttps $rejected "Enabled HTTPS with the unusable thumbprint '$unusable' was accepted."
    }

    $script:requests = @()
    $script:statusCode = 200
    Assert-LocalHttps (Wait-NodePilotReady -Port 8443 -CertificateThumbprint $probeSettings.CertificateThumbprint -TimeoutSeconds 5) 'The update probe rejected a pinned readiness response.'
    Assert-LocalHttps (@($script:requests).Count -eq 1 -and [string]$script:requests[0].Uri -eq 'https://localhost:8443/healthz/ready' -and
        $script:requests[0].Pin -eq $certificate.Thumbprint) 'The update probe did not use the pinned loopback transport.'
    $script:failWith = 'synthetic not ready'
    Assert-LocalHttps (-not (Wait-NodePilotReady -Port 8443 -CertificateThumbprint $certificate.Thumbprint -TimeoutSeconds 1)) 'A failing pinned probe was reported as ready.'
    $script:failWith = $null
    # An unreadable configuration: HTTPS on the given port, through the same transport without a pin.
    $script:requests = @()
    Assert-LocalHttps (Wait-NodePilotReady -Port 443 -SkipCertificateCheck -TimeoutSeconds 5) 'The unpinned update probe rejected a readiness response.'
    Assert-LocalHttps (@($script:requests).Count -eq 1 -and [string]$script:requests[0].Uri -eq 'https://localhost:443/healthz/ready' -and
        $script:requests[0].Unpinned -and -not $script:requests[0].Pin) 'The unpinned update probe did not use the loopback transport without a pin.'
    $script:plainRequests = @()
    function Invoke-WebRequest {
        param($Uri, [switch]$UseBasicParsing, $MaximumRedirection, $TimeoutSec)
        $script:plainRequests += [string]$Uri
        return [pscustomobject]@{StatusCode=200}
    }
    $script:requests = @()
    Assert-LocalHttps (Wait-NodePilotReady -Port 8080 -TimeoutSeconds 5) 'The update probe rejected a plain-HTTP readiness response.'
    Assert-LocalHttps (@($script:plainRequests).Count -eq 1 -and $script:plainRequests[0] -eq 'http://localhost:8080/healthz/ready' -and
        @($script:requests).Count -eq 0) 'Without a configured certificate the update probe must use plain HTTP on loopback.'
}
finally {
    if ($certificate) { $certificate.Dispose() }
    if ($otherCertificate) { $otherCertificate.Dispose() }
    $rsa.Dispose()
}
Write-Host "Local server HTTPS checks passed: $script:passed"
