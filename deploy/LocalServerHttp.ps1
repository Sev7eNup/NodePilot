#requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

if (-not ('NodePilot.Deployment.LocalServerHttp' -as [type])) {
    # The Framework-compatible HTTP API is obsolete on PowerShell 7, but its per-request
    # certificate callback is supported on both runtimes.
    Add-Type -IgnoreWarnings -TypeDefinition @'
using System;
using System.Net;
using System.Text.RegularExpressions;

namespace NodePilot.Deployment
{
    public static class LocalServerHttp
    {
        public static HttpWebRequest CreateRequest(Uri uri, string certificateThumbprint, int timeoutMilliseconds)
        {
            RequireLoopbackHttps(uri);
            if (certificateThumbprint == null || !Regex.IsMatch(certificateThumbprint, "\\A[0-9a-fA-F]{40}\\z"))
                throw new ArgumentException("The configured server certificate thumbprint is required.", "certificateThumbprint");
            var request = CreateLoopbackRequest(uri, timeoutMilliseconds);
            // Preflight validates the selected certificate. Loopback uses that exact identity,
            // even when the certificate names the public hostname or is explicitly self-signed.
            request.ServerCertificateValidationCallback = (sender, certificate, chain, errors) =>
                certificate != null && String.Equals(certificate.GetCertHashString(), certificateThumbprint, StringComparison.OrdinalIgnoreCase);
            return request;
        }

        // Accepts any certificate for this one request. Only for a probe that sends no credentials
        // when no thumbprint is known.
        public static HttpWebRequest CreateUnpinnedRequest(Uri uri, int timeoutMilliseconds)
        {
            RequireLoopbackHttps(uri);
            var request = CreateLoopbackRequest(uri, timeoutMilliseconds);
            request.ServerCertificateValidationCallback = (sender, certificate, chain, errors) => true;
            return request;
        }

        private static void RequireLoopbackHttps(Uri uri)
        {
            if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsLoopback || uri.UserInfo.Length != 0)
                throw new ArgumentException("Setup requests require a loopback HTTPS URL.", "uri");
        }

        private static HttpWebRequest CreateLoopbackRequest(Uri uri, int timeoutMilliseconds)
        {
            if (timeoutMilliseconds <= 0)
                throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.AllowAutoRedirect = false;
            request.Proxy = null;
            request.Timeout = timeoutMilliseconds;
            request.ReadWriteTimeout = timeoutMilliseconds;
            return request;
        }
    }
}
'@
}

function Get-NodePilotLocalHttpsProtocol {
    # Windows PowerShell without the system-default switch offers only SSL 3 and TLS 1.0, which
    # Kestrel refuses; TLS 1.3 covers hosts with TLS 1.2 disabled. 0 is SystemDefault: the OS
    # already negotiates TLS 1.2 or later.
    param([Parameter(Mandatory)][Net.SecurityProtocolType]$Current)
    if ([int]$Current -eq 0) { return $Current }
    return $Current -bor [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
}

function Invoke-NodePilotLocalHttps {
    [CmdletBinding(DefaultParameterSetName = 'Pinned')]
    param(
        [Parameter(Mandatory)][uri]$Uri,
        [Parameter(Mandatory, ParameterSetName = 'Pinned')][string]$CertificateThumbprint,
        # Health probe without a known thumbprint. GET only, so no credentials go out unpinned.
        [Parameter(Mandatory, ParameterSetName = 'Unpinned')][switch]$SkipCertificateCheck,
        [ValidateSet('GET', 'POST')][string]$Method = 'GET',
        [string]$Body,
        [hashtable]$Headers = @{},
        [ValidateRange(1, 300)][int]$TimeoutSeconds = 30
    )

    $request = if ($SkipCertificateCheck) {
        if ($Method -ne 'GET' -or $Headers.Count -gt 0) {
            throw 'A request without certificate pinning may only be a plain GET.'
        }
        [NodePilot.Deployment.LocalServerHttp]::CreateUnpinnedRequest($Uri, $TimeoutSeconds * 1000)
    } else {
        [NodePilot.Deployment.LocalServerHttp]::CreateRequest($Uri, $CertificateThumbprint, $TimeoutSeconds * 1000)
    }
    $request.Method = $Method
    foreach ($key in $Headers.Keys) { $request.Headers.Add([string]$key, [string]$Headers[$key]) }
    # The protocol setting is process-wide, so it only applies for this request.
    $previousProtocol = [Net.ServicePointManager]::SecurityProtocol
    [Net.ServicePointManager]::SecurityProtocol = Get-NodePilotLocalHttpsProtocol -Current $previousProtocol
    try {
        if ($Method -eq 'POST') {
            $request.ContentType = 'application/json'
            $bytes = [Text.Encoding]::UTF8.GetBytes($Body)
            try {
                $request.ContentLength = $bytes.Length
                $stream = $request.GetRequestStream()
                try { $stream.Write($bytes, 0, $bytes.Length) }
                finally { $stream.Dispose() }
            }
            finally { [Array]::Clear($bytes, 0, $bytes.Length) }
        }
        $response = $request.GetResponse()
    }
    finally { [Net.ServicePointManager]::SecurityProtocol = $previousProtocol }
    try { return [pscustomobject]@{ StatusCode = [int]$response.StatusCode } }
    finally { $response.Dispose() }
}
