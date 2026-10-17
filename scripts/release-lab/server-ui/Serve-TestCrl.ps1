#requires -Version 5.1
<# Serves only /issuer.crl over loopback for the isolated PostgreSQL revocation fixture. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CrlPath,
    [ValidateRange(1024, 65535)][int]$Port = 55412
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
$body = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $CrlPath).ProviderPath)
$listener = New-Object Net.Sockets.TcpListener ([Net.IPAddress]::Loopback), $Port
$listener.Server.ExclusiveAddressUse = $true
$listener.Start()
try {
    while ($true) {
        $client = $listener.AcceptTcpClient()
        try {
            $stream = $client.GetStream()
            $stream.ReadTimeout = 3000
            $stream.WriteTimeout = 3000
            $deadline = [DateTime]::UtcNow.AddSeconds(5)
            $requestBytes = New-Object 'Collections.Generic.List[byte]'
            $complete = $false
            while ($requestBytes.Count -lt 8192 -and [DateTime]::UtcNow -lt $deadline) {
                $value = $stream.ReadByte()
                if ($value -lt 0) { break }
                $requestBytes.Add([byte]$value)
                $count = $requestBytes.Count
                if ($count -ge 4 -and $requestBytes[$count - 4] -eq 13 -and $requestBytes[$count - 3] -eq 10 -and
                    $requestBytes[$count - 2] -eq 13 -and $requestBytes[$count - 1] -eq 10) { $complete = $true; break }
            }
            $request = [Text.Encoding]::ASCII.GetString($requestBytes.ToArray())
            if ($complete -and $request -cmatch '^GET /issuer\.crl(?:\?run=[0-9a-f]{32})? HTTP/1\.[01]\r\n') {
                $header = "HTTP/1.1 200 OK`r`nContent-Type: application/pkix-crl`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n"
                $bytes = [Text.Encoding]::ASCII.GetBytes($header)
                $stream.Write($bytes, 0, $bytes.Length)
                $stream.Write($body, 0, $body.Length)
            } else {
                $bytes = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 404 Not Found`r`nContent-Length: 0`r`nConnection: close`r`n`r`n")
                $stream.Write($bytes, 0, $bytes.Length)
            }
        } catch [IO.IOException] {
            Write-Verbose 'Client disconnected or exceeded the request timeout.'
        } finally { $client.Dispose() }
    }
} finally { $listener.Stop() }
