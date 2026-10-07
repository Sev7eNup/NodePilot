# Database certificates

In production NodePilot connects to its database only over TLS and verifies the server certificate: the host name must match, the chain must lead to a trusted root, and for PostgreSQL the revocation status must be checkable. This page shows how to create such a certificate when no internal CA is available.

**With an internal CA (AD CS, for example)** you do not need any of this: request a server certificate for the database host's FQDN and continue with the section for your database at *On the NodePilot server*.

The host name in the certificate has to be exactly the name NodePilot connects to. An IP address or the short name is refused.

## PostgreSQL

You need OpenSSL. Git for Windows ships it (`C:\Program Files\Git\usr\bin\openssl.exe`). Run the steps in PowerShell in an empty folder on any machine.

### 1. CA and server certificate

```powershell
$db = 'pg1.corp.example.com'   # host name NodePilot connects to
openssl req -x509 -newkey rsa:3072 -nodes -days 3650 -keyout ca.key -out ca.crt -subj "/CN=NodePilot DB CA" -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign"
openssl req -newkey rsa:3072 -nodes -keyout server.key -out server.csr -subj "/CN=$db"
Set-Content server.ext "subjectAltName=DNS:$db`r`nextendedKeyUsage=serverAuth" -Encoding ascii
openssl x509 -req -in server.csr -CA ca.crt -CAkey ca.key -CAcreateserial -days 825 -extfile server.ext -out server.crt
openssl verify -CAfile ca.crt server.crt
```

The last command has to print `server.crt: OK`.

### 2. Revocation list (CRL)

NodePilot checks whether the certificate has been revoked. Without a CRL it refuses the connection, even though the certificate is valid.

```powershell
Set-Content ca.cnf "[ca]`r`ndefault_ca=db_ca`r`n[db_ca]`r`ndatabase=index.txt`r`ndefault_md=sha256`r`ndefault_crl_days=365" -Encoding ascii
New-Item index.txt -ItemType File | Out-Null
openssl ca -config ca.cnf -keyfile ca.key -cert ca.crt -gencrl -out ca.crl
```

The CRL is valid for one year. Before it expires, run the last command again and import the new `ca.crl` as in step 4. Keep `ca.key`, `ca.cnf` and `index.txt` in a safe place, never on the database server.

### 3. On the PostgreSQL server

Copy `server.crt` and `server.key` into the data directory and set in `postgresql.conf`:

```ini
ssl = on
ssl_cert_file = 'server.crt'
ssl_key_file = 'server.key'
```

The PostgreSQL service account needs read access to `server.key`. Look up the account and grant it the right (the example uses `NetworkService` and the data directory `C:\PostgreSQL\data`), then restart the service. Under `LocalSystem` no grant is needed:

```powershell
(Get-CimInstance Win32_Service -Filter "Name like 'postgresql%'").StartName
icacls 'C:\PostgreSQL\data\server.key' /grant 'NT AUTHORITY\NetworkService:R'
Restart-Service postgresql*
```

### 4. On the NodePilot server

Import the CRL into the computer store:

```powershell
certutil -addstore CA ca.crl
```

Pass `ca.crt` to the installer as the root certificate (`-PostgresRootCertificate`, or the wizard page for PostgreSQL). It ends up in the connection string as `Root Certificate=…`.

### Check

```powershell
$env:PGSSLMODE = 'verify-full'
$env:PGSSLROOTCERT = 'C:\PKI\ca.crt'
psql -h pg1.corp.example.com -U nodepilot -d nodepilot -c "select ssl, version from pg_stat_ssl where pid = pg_backend_pid()"
```

Expected: `t | TLSv1.3`. Set the root certificate as an environment variable: inside the `psql` connection string, Windows backslashes get lost. `psql` does not check revocation. The installer's pre-flight checks it and aborts if the CRL is missing.

## SQL Server

SQL Server only offers certificates that are RSA with `KeySpec=KeyExchange`. The default CNG key of `New-SelfSignedCertificate` is invisible to it, so both provider flags below are required. Run as administrator on the SQL server:

```powershell
$cert = New-SelfSignedCertificate -DnsName 'sql1.corp.example.com' `
    -CertStoreLocation Cert:\LocalMachine\My `
    -KeySpec KeyExchange `
    -Provider 'Microsoft RSA SChannel Cryptographic Provider' `
    -KeyLength 2048 -NotAfter (Get-Date).AddYears(5)
$key = Join-Path "$env:ProgramData\Microsoft\Crypto\RSA\MachineKeys" $cert.PrivateKey.CspKeyContainerInfo.UniqueKeyContainerName
icacls $key /grant 'NT Service\MSSQLSERVER:R'
Export-Certificate -Cert $cert -FilePath C:\Temp\sql1.cer
```

Replace `NT Service\MSSQLSERVER` with the actual SQL service account if it differs (`(Get-CimInstance Win32_Service -Filter "Name='MSSQLSERVER'").StartName`).

1. Assign the certificate in SQL Server Configuration Manager → *Protocols for MSSQLSERVER* → *Certificate*. Leave **Force Encryption = No.** NodePilot encrypts its own connection regardless, and forcing it instance-wide breaks every other client of a shared instance that does not trust the certificate, remote ConfigMgr site systems for example.
2. Restart the SQL Server service and confirm the ERRORLOG line `The certificate ... was successfully loaded for encryption`.
3. **On the NodePilot server**, import the public part as a trusted root:

```powershell
Import-Certificate -FilePath C:\Temp\sql1.cer -CertStoreLocation Cert:\LocalMachine\Root
```

SQL Server needs no CRL. Pass the FQDN from `-DnsName` to the installer as `-SqlCertificateHostName`.

## Common errors

| Message | Cause |
|---|---|
| `The remote certificate was rejected by the provided RemoteCertificateValidationCallback` (PostgreSQL) | CRL not imported or expired, root certificate not given, or host name does not match |
| `The certificate chain was issued by an authority that is not trusted` (SQL Server) | `.cer` not imported into `LocalMachine\Root` on the NodePilot server |
| `The target principal name is incorrect` (SQL Server) | Connected via IP or short name instead of the name in the certificate |
| `Aborted: Postgres pre-flight failed - certificate revocation could not be checked.` | CRL missing on the NodePilot server, see step 4 |
