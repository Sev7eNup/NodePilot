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
            if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsLoopback || uri.UserInfo.Length != 0)
                throw new ArgumentException("Setup requests require a loopback HTTPS URL.", "uri");
            if (certificateThumbprint == null || !Regex.IsMatch(certificateThumbprint, "\\A[0-9a-fA-F]{40}\\z"))
                throw new ArgumentException("The configured server certificate thumbprint is required.", "certificateThumbprint");
            if (timeoutMilliseconds <= 0)
                throw new ArgumentOutOfRangeException("timeoutMilliseconds");

            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.AllowAutoRedirect = false;
            request.Proxy = null;
            request.Timeout = timeoutMilliseconds;
            request.ReadWriteTimeout = timeoutMilliseconds;
            // Preflight validates the selected certificate. Loopback uses that exact identity,
            // even when the certificate names the public hostname or is explicitly self-signed.
            request.ServerCertificateValidationCallback = (sender, certificate, chain, errors) =>
                certificate != null && String.Equals(certificate.GetCertHashString(), certificateThumbprint, StringComparison.OrdinalIgnoreCase);
            return request;
        }
    }
}
'@
}

function Invoke-NodePilotLocalHttps {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][uri]$Uri,
        [Parameter(Mandatory)][string]$CertificateThumbprint,
        [ValidateSet('GET', 'POST')][string]$Method = 'GET',
        [string]$Body,
        [hashtable]$Headers = @{},
        [ValidateRange(1, 300)][int]$TimeoutSeconds = 30
    )

    $request = [NodePilot.Deployment.LocalServerHttp]::CreateRequest($Uri, $CertificateThumbprint, $TimeoutSeconds * 1000)
    $request.Method = $Method
    foreach ($key in $Headers.Keys) { $request.Headers.Add([string]$key, [string]$Headers[$key]) }
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
    try { return [pscustomobject]@{ StatusCode = [int]$response.StatusCode } }
    finally { $response.Dispose() }
}
