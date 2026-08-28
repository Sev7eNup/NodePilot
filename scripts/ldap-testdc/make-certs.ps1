# Creates the throwaway PKI for the LDAPS test DC, valid for 30 days: an own CA plus a
# server certificate with SAN DNS:localhost and IP:127.0.0.1.
# The files land in scripts/ldap-testdc/certs/ (gitignored) and are copied into the image
# by `docker build`. For the test, ca.crt has to go into the Windows trust store
# (LocalMachine\Root, see README) and should be removed again afterwards.
$ErrorActionPreference = 'Stop'

$dir = Join-Path $PSScriptRoot 'certs'
New-Item -ItemType Directory -Force $dir | Out-Null

$openssl = $null
$cmd = Get-Command openssl -ErrorAction SilentlyContinue
if ($cmd) { $openssl = $cmd.Source }
elseif (Test-Path 'C:\Program Files\Git\usr\bin\openssl.exe') { $openssl = 'C:\Program Files\Git\usr\bin\openssl.exe' }
else { throw 'openssl nicht gefunden (Git fuer Windows installiert?)' }

Push-Location $dir
try {
    @"
subjectAltName = DNS:localhost, DNS:dc1.nodepilot.test, IP:127.0.0.1
extendedKeyUsage = serverAuth
keyUsage = digitalSignature, keyEncipherment
basicConstraints = CA:FALSE
"@ | Set-Content ext.cnf -Encoding Ascii

    & $openssl req -x509 -newkey rsa:2048 -keyout ca.key -out ca.crt -days 30 -nodes -subj '/CN=NodePilot LDAP Test CA'
    & $openssl req -newkey rsa:2048 -keyout server.key -out server.csr -nodes -subj '/CN=localhost'
    & $openssl x509 -req -in server.csr -CA ca.crt -CAkey ca.key -CAcreateserial -out server.crt -days 30 -extfile ext.cnf
}
finally { Pop-Location }

"Zertifikate erzeugt in $dir"
"Naechster Schritt: ca.crt in LocalMachine\Root importieren (elevated), siehe README."
