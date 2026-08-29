# Windows Server deployment

This operating mode installs NodePilot as a Windows service for production network use. The supplied scripts are under `deploy\`. The complete parameter reference is additionally in `deploy\README.md`.

## Target state

```text
Browser / API client
        |
        | HTTPS
        v
Kestrel inside the Windows service "NodePilot"
        |
        +--> SQL Server 2022
        |    or
        +--> PostgreSQL 16+
        |
        +--> Windows target systems over WinRM
```

Single-node installations use Kestrel directly. Active/passive installations use the supplied HAProxy template.

## Supported variants

### Service identity

| Variant | Use |
|---|---|
| **LocalSystem** | A simple single server; network access happens as the computer account `DOMAIN\HOST$` |
| **gMSA** | Least privilege, a shared identity, and the recommended HA path |

### Database

| Provider | Authentication | Production TLS |
|---|---|---|
| SQL Server 2022 **CU1 or later** (build ≥ 16.0.4003.1) | Windows integrated security | `Encrypt=Strict;TrustServerCertificate=False` |
| PostgreSQL 16+ | User name and password | `SSL Mode=VerifyFull` with a root CA |

`Encrypt=Strict` is TDS 8.0: SQL Server 2019 and older cannot do it, and SQL Server 2022 **RTM**
contains a TDS 8.0 defect that aborts parameterized queries with error 8005 — fixed from CU1. The
installer checks the patch level in the pre-flight; manually:
`SELECT SERVERPROPERTY('ProductVersion')`.

## Prerequisites

### Target server

- Windows Server 2022 or 2025
- Domain membership
- PowerShell 5.1 or PowerShell 7
- ASP.NET Core Runtime 10.0.11 or newer in the 10.x line (x64) — the plain runtime is enough, Kestrel hosts itself; the Hosting Bundle only with deliberate IIS use (it reconfigures IIS and restarts W3SVC). The `(x64)` is binding: NodePilot ships as `win-x64`; the pre-flight rejects 32-bit and older vulnerable 10.x runtimes, naming the path and version
- Network access to the database
- A TLS certificate with its private key in `LocalMachine\My`
- Local administrator rights for the installation

### The network path to the clients

The interface's live view (running steps, execution status, dashboard counters) holds a SignalR
connection on `/hubs/execution`. The client negotiates the transport, working down a ladder:
**WebSockets → server-sent events → long polling**.

A proxy or TLS inspection that discards the `Upgrade: websocket` handshake therefore does **not**
break the live view — it falls back to server-sent events on its own. It only becomes visible as a
recurring connection error in the browser console, plus somewhat more load per connection. To keep
the console clean, have the host bypassed in the proxy or PAC file, or allow WebSocket traffic for it.

For completeness: Kestrel advertises HTTP/2 through ALPN. A TLS-terminating appliance therefore has
to forward WebSockets as *Extended CONNECT* (RFC 8441) — not all of them can, while ordinary requests
pass unremarkably.

### Build host

- .NET 10 SDK
- Node.js LTS and npm
- A code-signing certificate for the artifact manifest and distribution

### Database

The database has to exist before the installation. The NodePilot identity needs DDL permissions so that EF migrations can be applied at startup.

## 1. Prepare the service identity

### Variant A: LocalSystem

No service account has to be created. For SQL Server, the computer account of the NodePilot host has to exist as a login:

```sql
USE master;
CREATE LOGIN [CONTOSO\NPSRV01$] FROM WINDOWS;

CREATE DATABASE NodePilot;
USE NodePilot;
CREATE USER [CONTOSO\NPSRV01$] FOR LOGIN [CONTOSO\NPSRV01$];
ALTER ROLE db_owner ADD MEMBER [CONTOSO\NPSRV01$];
```

### Variant B: gMSA

The gMSA is created in Active Directory, released for the target server, and then installed on the target server:

```powershell
Install-ADServiceAccount -Identity svc-nodepilot
Test-ADServiceAccount -Identity svc-nodepilot
```

The expected test result is `True`. For SQL Server, the gMSA is created as a login and database user with `db_owner`, just like the computer account.

### The setup can do both for you

If you install with `NodePilot-Server-Setup-<version>.exe`, you can skip the SQL above, provided the executing account is `sysadmin` (or holds `CREATE ANY DATABASE`). The readiness page checks the **service identity's** database access separately from plain reachability — reachability is tested as the *installing admin*, whereas at runtime it is the service that signs in — and creates the login, user and `db_owner` if needed. The row comes pre-ticked; unattended, `"provisioning": { "createDatabaseAndLogin": true }` in the answer file requests the same. It is existence-checked: if everything is already there, nothing happens. Without the permissions, nothing is changed and the instructions for the DBA are shown.

**PostgreSQL likewise, with one difference.** The setup ships `psql`, signs in during the pre-flight as the NodePilot role (`sslmode=verify-full` against the supplied root certificate) and creates the role and database on request. Because PostgreSQL has no counterpart to `Trusted_Connection`, it requires **superuser credentials**: two additional fields on the credentials page, or `provisioning.postgresSuperUser` / `.postgresSuperPassword` in the answer file. Without them the row stays a diagnosis with no button. The password of an existing role is **never** overwritten.

## 2. Prepare the database

### SQL Server

**The TLS certificate.** SQL Server only offers certificates that are RSA with
`KeySpec=KeyExchange`. The default CNG key of `New-SelfSignedCertificate` is invisible to it, so
the two provider flags below are load-bearing rather than decoration:

```powershell
New-SelfSignedCertificate -DnsName 'sql1.corp.example.com' `
    -CertStoreLocation Cert:\LocalMachine\My `
    -KeySpec KeyExchange `
    -Provider 'Microsoft RSA SChannel Cryptographic Provider' `
    -KeyLength 2048 -NotAfter (Get-Date).AddYears(5)
```

1. Give the SQL service account (`NT Service\MSSQLSERVER` by default) read access to the private
   key: `certlm.msc` → Personal → the certificate → *All Tasks → Manage Private Keys*.
2. Assign it in SQL Server Configuration Manager → *Protocols for MSSQLSERVER* → *Certificate*.
   Leave **Force Encryption = No.** NodePilot encrypts its own connection regardless, and forcing
   it instance-wide breaks every other client of a shared instance that does not trust the
   certificate — remote ConfigMgr site systems, for example.
3. Restart the SQL Server service and confirm the ERRORLOG line
   `The certificate ... was successfully loaded for encryption`.
4. Self-signed certificates additionally need their public part imported into `LocalMachine\Root`
   **on the NodePilot server** — the runtime verifies the chain (`TrustServerCertificate=False`).

**Database and login** for the service identity, run as `sysadmin`. With `LocalSystem`, substitute
the computer account (`CORP\APPHOST$`) for the gMSA:

```sql
CREATE LOGIN [CORP\svc-nodepilot$] FROM WINDOWS;
CREATE DATABASE [NodePilot];
GO
USE [NodePilot];
CREATE USER [CORP\svc-nodepilot$] FOR LOGIN [CORP\svc-nodepilot$];
ALTER ROLE db_owner ADD MEMBER [CORP\svc-nodepilot$];
```

Read-committed snapshot isolation should also be enabled:

```sql
ALTER DATABASE [NodePilot]
SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
```

The installer tries to set this automatically. Without sufficient permissions, the pre-flight prints the required SQL statement.

### PostgreSQL

```sql
CREATE ROLE nodepilot WITH LOGIN PASSWORD '<strong-secret>';
CREATE DATABASE nodepilot OWNER nodepilot;
```

The PostgreSQL server has to present a certificate whose host name and trust chain can be verified. The root CA is passed to the installer as a PEM file.

## 3. Import the HTTPS certificate

```powershell
$certificatePassword = Read-Host -AsSecureString "PFX password"
Import-PfxCertificate `
  -FilePath C:\Certs\nodepilot.pfx `
  -CertStoreLocation Cert:\LocalMachine\My `
  -Password $certificatePassword
```

Find the thumbprint:

```powershell
Get-ChildItem Cert:\LocalMachine\My |
  Where-Object Subject -Like "*nodepilot*" |
  Select-Object Subject, Thumbprint, NotAfter
```

The certificate name has to match the public host name.

## 4. Obtain the production artifact

Either download the published release or build it yourself — in both cases the installer requires a
signed artifact and the publisher's thumbprint.

**Variant A — download the release.** The [latest release](https://github.com/Sev7eNup/NodePilot/releases/latest)
carries the zip, `manifest.json`, `.p7s`, `NodePilot-Deploy-Scripts-<version>.zip`,
`NodePilot-<version>.SHA256SUMS.txt` and the public signing certificate
`nodepilot-release-signing.cer`.

**The installer scripts come from the deploy-scripts zip** — it contains `Install-`, `Update-` and
`Uninstall-NodePilot.ps1` together with every helper they dot-source, and `templates\`. They do also
travel inside the artifact, but only under `knowledge\source\` for the AI assistant; taking them from
there would mean extracting the *unverified* archive to obtain the very script whose job is to verify
it. Every one of these files has a line in `SHA256SUMS`.

Compare the checksums and check the thumbprint against the release notes — that comparison **is** the
trust decision. The installer requires exactly that signer and checks the code-signing purpose,
KeyUsage and validity; whether the machine trusts the publisher is irrelevant. An import into
`Cert:\LocalMachine\Root` is optional and only causes Windows to validate the Authenticode signature
of the installers themselves.

> If you install with the GUI setup (chapter 5, variant A), you need nothing from this chapter:
> `NodePilot-Server-Setup-<version>.exe` is on the same release, carries the signed artifact inside
> it and verifies its signature itself — one file instead of five, and no manual thumbprint comparison.

### First launch: the blue SmartScreen window

A **downloaded** installer triggers "Windows protected your PC" on launch, with *Don't run* as the
only visible button. That is expected and not a sign of a corrupted file.

Two things have to be true for it, and on a download both are:

- **The file carries a Mark of the Web.** Everything a browser saves gets an alternate data stream
  with the "Internet" zone, and SmartScreen only evaluates files with that marking. A self-built
  installer from `out\` does not have it and starts without comment — which is why the dialog appears
  on the first *published* release even though earlier private builds of the same product never
  triggered it.
- **The publisher has no reputation.** NodePilot is signed with a self-signed certificate, so
  SmartScreen has nothing to evaluate and reports an unknown app.

The marking can be made visible and removed:

```powershell
Get-Content -LiteralPath .\NodePilot-Server-Setup-<version>.exe -Stream Zone.Identifier
# ZoneId=3   (3 = Internet)

Unblock-File -Path .\NodePilot-Server-Setup-<version>.exe
```

**Verify first, then dismiss.** The dialog is not the trust decision — the checksum and the thumbprint
are. Compare both, then choose *More info → Run anyway*. `Get-AuthenticodeSignature` reports
`UnknownError` while doing so; with a self-signed publisher that is the expected finding, not an error.

The same applies to the zip route: files extracted from a marked archive inherit the marking, so
release the zip with `Unblock-File` before extracting — otherwise Windows treats the scripts inside it
as downloaded content.

Antivirus exclusions change none of this; SmartScreen is a separate reputation service and ignores
them (see [Antivirus exclusions](./av-exclusions)). The dialog only disappears permanently with a
certificate that carries reputation.

**Variant B — build it yourself.** In the repository on the build host:

```powershell
$releaseSigner = "0123456789ABCDEF0123456789ABCDEF01234567"
.\deploy\Build-Artifact.ps1 -SigningCertificateThumbprint $releaseSigner
```

`-Version` is optional and falls back to the product version from `Directory.Build.props`.

Result:

```text
out\NodePilot-<version>.zip
out\NodePilot-<version>.zip.manifest.json
out\NodePilot-<version>.zip.manifest.json.p7s
out\NodePilot-Deploy-Scripts-<version>.zip
out\nodepilot-release-signing.cer
out\NodePilot-<version>.SHA256SUMS.txt
```

With `-IncludeServerInstaller` the same run additionally produces `NodePilot-Server-Setup-<version>.exe`,
and with `-IncludeDesktopInstaller -PgBinariesPath <pgsql>` also `NodePilot-Desktop-Setup-<version>.exe`
— all under the same version. `-InstallerSigningCertificateThumbprint <tp>` signs both installers as
part of the run, which necessarily has to happen before the checksums are computed.

The installer and updater verify the signature (thumbprint pin; no chain validation), the certificate's code-signing suitability, validity and KeyUsage, as well as the file name, length and SHA-256 hash before any change.

## 5. Install NodePilot

There are two routes to the same installation.

### Variant A: the GUI setup

Download `NodePilot-Server-Setup-<version>.exe` from the release and run it. It brings the signed
artifact and the ASP.NET Core runtime with it, checks every prerequisite from chapters 1 to 4
**before** it changes anything, and shows each as green, amber or red with copyable instructions. On
request it installs the runtime, creates the SQL login and database, or generates a lab certificate.

That includes the trust question from chapter 4 — but as an **amber, optional** row: the setup names
the thumbprint of the publisher `CN=NodePilot Release Signing` and offers the import into
`LocalMachine\Root`, offered and not pre-ticked. The installation does not need it; it verifies the
signature against the compiled-in thumbprint. The import only causes Windows to validate the
Authenticode signature of the installers themselves. The row turns **red** for everything the
installer itself rejects: a wrong thumbprint, an expired or not-yet-valid certificate, missing
code-signing suitability.

For the Kestrel certificate it asks only for the thumbprint — and offers the certificates from
`Cert:\LocalMachine\My` below the input field, sorted by expiry. That applies to a PKI certificate
from your own CA just as much as to a self-signed one: import, select, done. A certificate without a
private key appears in the list with a corresponding marking rather than silently missing.

If you have none at all, leave the field **empty**. The readiness page then reports "No certificate
selected" and offers to generate a self-signed one — offered, not pre-ticked, because a lab
certificate is created on request. It is valid for two years and deliberately **not** imported into
the root store; for production, a certificate from your own PKI remains the way. Unattended, this
corresponds to an empty `certificate.thumbprint` plus
`"provisioning": { "generateSelfSignedCertificate": true }`.

The finish page shows everything needed for first access: the address, the setup token for the first
sign-in, the external-trigger API key, the certificate thumbprint, and the service name and paths. The
API key appears **only there** — it cannot be reconstructed afterwards. The text is selectable, and
"Save this summary…" writes it to a file.

**Turnkey without typing a token.** Two mutually exclusive routes:

- **A `bootstrap` group with `adminUsername`** — the setup creates the first administrator itself.
  The password is generated randomly per machine and stored in an ACL-protected file at
  `<DataPath>\bootstrap-admin.json` (SYSTEM and Administrators only). There are deliberately no fixed
  default credentials: they would be identical across every machine and would be found rather than
  guessed.
- **A `seed` group with `backupPath` and `passphrase`** — set up a reference machine once, run
  `np backup export`, and every further installation applies that state on first start: users,
  workflows, machines, credentials and settings. No token is created at all then. The passphrase ends
  up in the service key, never in the configuration file; the seed file is deleted after it is applied.

The seed wins: if it brings users with it, there is nothing to redeem. It also fills **only** an empty
instance — a machine in production keeps everything it has. And it is fail-closed: a wrong passphrase
makes the service not start, rather than leaving behind an apparently provisioned but actually empty
instance.

Unattended, for SCCM or GPO:

```powershell
NodePilot-Server-Setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /ANSWERFILE=C:\prod\answers.json
```

A repeat run detects an existing installation and offers three routes: an **update** by default,
alternatively a **fresh install** — which, however, generates a **new external-trigger API key**, and
the old one cannot be reconstructed — or **removal**. The third option hands over to the same
uninstaller that "Apps & features" starts; it asks nothing twice, but lets that uninstaller ask its
one question: keep or delete the data directory. The **database is untouched in every case** — this
setup did not create it and does not remove it. `/FULLREINSTALL` skips the choice and reinstalls
directly. The answer-file schema, the switches and the exit codes are in `deploy/server/README.md`.

### Variant B: the scripts

The route the setup takes internally, and the right one for automation. The installation commands run
as a local administrator on the target server.

### SQL Server with LocalSystem

```powershell
$releaseSigner = "0123456789ABCDEF0123456789ABCDEF01234567"
.\deploy\Install-NodePilot.ps1 `
  -ArtifactPath C:\Packages\NodePilot-<version>.zip `
  -TrustedArtifactSignerThumbprint $releaseSigner `
  -UseLocalSystem `
  -SqlServer "sql01.contoso.local" `
  -SqlDatabase "NodePilot" `
  -CertThumbprint "A1B2C3D4E5F6..." `
  -PublicHostname "nodepilot.contoso.local"
```

### SQL Server with a gMSA

```powershell
$releaseSigner = "0123456789ABCDEF0123456789ABCDEF01234567"
.\deploy\Install-NodePilot.ps1 `
  -ArtifactPath C:\Packages\NodePilot-<version>.zip `
  -TrustedArtifactSignerThumbprint $releaseSigner `
  -ServiceAccount "CONTOSO\svc-nodepilot$" `
  -SqlServer "sql01.contoso.local" `
  -SqlDatabase "NodePilot" `
  -CertThumbprint "A1B2C3D4E5F6..." `
  -PublicHostname "nodepilot.contoso.local"
```

### PostgreSQL with a gMSA

```powershell
$releaseSigner = "0123456789ABCDEF0123456789ABCDEF01234567"
$postgresPassword = Read-Host -AsSecureString "PostgreSQL password"

.\deploy\Install-NodePilot.ps1 `
  -ArtifactPath C:\Packages\NodePilot-<version>.zip `
  -TrustedArtifactSignerThumbprint $releaseSigner `
  -ServiceAccount "CONTOSO\svc-nodepilot$" `
  -DbProvider postgres `
  -PostgresHost "pg01.contoso.local" `
  -PostgresDatabase "nodepilot" `
  -PostgresUser "nodepilot" `
  -PostgresPassword $postgresPassword `
  -PostgresRootCertificate C:\PKI\postgres-root-ca.pem `
  -CertThumbprint "A1B2C3D4E5F6..." `
  -PublicHostname "nodepilot.contoso.local"
```

For PostgreSQL with LocalSystem, `-UseLocalSystem` replaces the `-ServiceAccount` parameter.

## 6. What the installation does

The installer performs the following steps:

1. Check the prerequisites, signature, certificate, service account and database access.
2. Stop an existing NodePilot service in a controlled way.
3. Install the binaries into `C:\Program Files\NodePilot`.
4. Create the operational data under `C:\ProgramData\NodePilot`.
5. Render the production configuration.
6. Set the file-system and certificate ACLs. The installation directory gets a protected ACL: SYSTEM and Administrators `FullControl`, the service account only `ReadAndExecute` — it executes the binaries, it never overwrites them. A different `-InstallPath` is checked beforehand (local, NTFS or ReFS, no junctions in the path) and re-verified after the copy; otherwise it would inherit the permissions of its parent directory, which on a dedicated volume can mean write access for all users.
7. Create the HTTPS firewall rule.
8. Register the Windows service with automatic start and recovery actions (with a gMSA, additionally dependent on Netlogon, so that the logon does not fail before contact with a DC). The service therefore starts without a fixed delay and instead waits for the database itself — with `Database:StartupWaitSeconds` as the upper bound, 120 seconds by default.
9. Start the service and check readiness.
10. Print the admin setup token and the external-trigger API key.

The PostgreSQL connection string is not written into the JSON file. It lives in the ACL-protected service environment.

## 7. Verify the installation

```powershell
Get-Service NodePilot
Invoke-WebRequest https://nodepilot.contoso.local/healthz/live
Invoke-WebRequest https://nodepilot.contoso.local/healthz/ready
```

Expected results:

| Check | Expectation |
|---|---|
| Service status | `Running` |
| `/healthz/live` | HTTP 200 |
| `/healthz/ready` | HTTP 200 and a reachable database |
| Browser access | The login or setup page with no certificate warning |

With the directory integration enabled, `/healthz/directory` has to be checked separately. General readiness deliberately stays limited to the database.

### Behaviour during a database outage in operation

During a runtime outage the service stays up; database-dependent HTTP calls answer quickly with the
shared `503 DATABASE_UNAVAILABLE` contract, and SignalR calls with the same error code. The UI stays
reachable, shows a banner and a status indicator, and reconnects automatically after a successful
`SELECT 1` recovery — including the SignalR groups, without a service restart.
`RejectedByServer`, by contrast, means wrong credentials, database selection or TLS configuration
(`retryable: false`): here an administrator has to correct the configuration. If NodePilot's
connection details were changed, a service restart is required afterwards; server-side corrections are
detected by the probe automatically.

| Endpoint | Behaviour during an outage |
|---|---|
| `/healthz/live` | HTTP 200; no process restart |
| `/healthz/ready` | A fast HTTP 503; route traffic away |
| `/healthz/database` | HTTP 200 with `status` and `reason`; a status report, not a gate |

Running workflows wait at the durable step boundary. Only fires observed by active trigger sources are
counted and discarded, and are not caught up. Notifications are at-least-once; recipients should
deduplicate on `eventKey` from the JSON or `X-NodePilot-Event-Key`. Timeout and probe defaults are
under [Database providers](../configuration/database), and the exact HTTP schema under
[API endpoints](../api/endpoints#health).

## 8. Create the first admin account

The installer shows the one-time setup token from `C:\ProgramData\NodePilot\admin-setup.token`. Sign in in the browser with the user name and password you want: on the first attempt the login page reveals a **setup token field** — paste the token and sign in again. The admin account is then created and the token deleted.

If the installer could not display the token: the file is restricted by an owner-only ACL to the service account (not directly readable even for administrators — by design). Read it with backup semantics rather than changing the ACL:

```powershell
robocopy C:\ProgramData\NodePilot $env:TEMP admin-setup.token /B | Out-Null
Get-Content "$env:TEMP\admin-setup.token"
Remove-Item "$env:TEMP\admin-setup.token"   # after the first sign-in
```

The external-trigger API key is shown only once and has to be stored in a secret-management system.

## Directory and file layout

| Path | Contents | Service access |
|---|---|---|
| `C:\Program Files\NodePilot\` | The API, DLLs and `wwwroot` | Read |
| `C:\Program Files\NodePilot\appsettings.Production.json` | The production configuration | Read |
| `C:\ProgramData\NodePilot\` | Keys, the setup token, logs and operational data | Modify |
| `C:\Program Files\NodePilot\wwwroot\docs\` | This documentation, served at `/docs` | Read |

## The documentation on the server

The installer places the same documentation that the public website carries under
`wwwroot\docs`. It is reachable at `https://<host>/docs` — **without signing in**, so that it is
available when signing in is itself the problem, and without internet access. The shipped copy
belongs to the installed version, whereas the website always shows the current development
state. In the interface, the question mark in the header leads there.

The documentation does not depend on the database and therefore stays readable during a database
outage. It is, however, **only available while the service is running** — if the service does not
start at all, the setup transcript under `%TEMP%` and the application log under
`C:\ProgramData\NodePilot\logs` remain the source.

## Update and automatic rollback

```powershell
$releaseSigner = "0123456789ABCDEF0123456789ABCDEF01234567"
.\deploy\Update-NodePilot.ps1 `
  -ArtifactPath C:\Packages\NodePilot-<version>.zip `
  -TrustedArtifactSignerThumbprint $releaseSigner
```

The updater:

- verifies the new artifact,
- backs up the existing binaries,
- waits up to 30 seconds after stopping the service for processes from the installation directory to exit and then terminates any that remain; only if that does not help does the updater abort with the process name and PID **before the first file deletion** (a stopped service is not enough: orphaned workers keep their DLLs mapped),
- preserves the database, the service account and the production configuration,
- restarts the service,
- checks the health endpoint on the port from the installed configuration (`-HttpsPort` is only needed to override it),
- restores the previous binaries on a failed health check and leaves the service in the state it was in before the update.

A **successful** update always leaves the service **running**, whether or not it was stopped before. Only a failed update restores the original state.

Workflow history is deliberately not re-encrypted at startup, so that the immediate health-check
rollback stays safe. During a mixed HA upgrade, pause workflow edits and rollbacks (or, after the
first write by a new node, allow no further failback to old nodes): new nodes write new history
snapshots immediately as `np:wfv:v1:`, and the old binary cannot read those rows. Once the health
check has succeeded and **all HA nodes** run the new version, run
`np secrets reencrypt --yes` after the regular database backup (or the action under Admin settings →
Security). Old `WorkflowVersions` are then protected as `np:wfv:v1:` as well. After the first new
history write or this cutover, you must not roll back to a binary version without support for the
format.

The binary backup contains no secret-bearing `appsettings.Production.json`. It is therefore replaced last during the swap, so that an abort does not destroy it.

## Replacing the HTTPS certificate

The certificate can be changed at any time on a running installation — to replace a self-signed
setup certificate with one from your own PKI, or for a routine renewal. No reinstallation is needed;
this is a configuration change.

```powershell
# 1. Import the new certificate with its private key
$pfxPassword = Read-Host -AsSecureString "PFX password"
$cert = Import-PfxCertificate -FilePath 'C:\PKI\nodepilot-new.pfx' `
  -CertStoreLocation Cert:\LocalMachine\My -Password $pfxPassword
$cert.Thumbprint
```

**2. Grant the service account read access to the private key.** This is the step a manual swap
misses — during installation the installer does it for you. Without it the service does not start
after the restart, and the message does not mention the certificate.

```powershell
$rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
$keyFile = Join-Path $env:ProgramData `
  "Microsoft\Crypto\RSA\MachineKeys\$($rsa.Key.UniqueName)"
icacls.exe $keyFile /grant '<service account>:(R)'
```

With `LocalSystem` as the service identity the step is unnecessary — `SYSTEM` already has read
access to `MachineKeys`. For a gMSA, give `<service account>` as `DOMAIN\gmsa$`. For an ECDSA
certificate use `GetECDsaPrivateKey` instead of `GetRSAPrivateKey` and look for the file under
`Microsoft\Crypto\Keys`.

**3. Set the thumbprint in the configuration.** In `C:\Program Files\NodePilot\appsettings.Production.json`:

```json
"Kestrel": { "Https": { "CertificateThumbprint": "<new thumbprint>" } }
```

**4. Restart the service.** `Kestrel` belongs to the boot-fixed part of the configuration — hot
reload does not apply there.

```powershell
Restart-Service NodePilot
Invoke-WebRequest https://<host>:<port>/healthz/ready -UseBasicParsing
```

Leave the old certificate in the store until the restart has succeeded: if the service fails to
start, the way back is to put the previous thumbprint into that same line. Remove it afterwards.

## Uninstalling

```powershell
.\deploy\Uninstall-NodePilot.ps1
```

This removes the service, the service binaries, the firewall rules, the installation marker and the
registry environment entry (which holds the Postgres password). Logs and configuration are preserved.
To remove the local operational data completely:

```powershell
.\deploy\Uninstall-NodePilot.ps1 -PurgeData
```

After an installation through the GUI setup, the same is possible through "Apps & features" or directly:

```powershell
& 'C:\Program Files\NodePilot\unins000.exe' /VERYSILENT /SUPPRESSMSGBOXES /PURGEDATA=1
```

**The database is never removed, and there is no option for it.** NodePilot does not create it — it
was provisioned separately in chapter 2, often has its own backup and replication regime, and in an
active/passive cluster both nodes share it. For the same reason, the gMSA's "Log on as a service"
right and the read ACE on the TLS certificate's private key are left in place; both may be shared with
another service. The uninstaller names all three at the end of its run.

## Backup and restore

A complete backup requires two backups:

1. **The system configuration backup:** workflows, machines, credentials, users and runtime settings.
2. **The database backup:** execution history, audit log, statistics and the complete data set.

PostgreSQL uses `pg_dump`, for example; SQL Server uses the native SQL Server backup. Details are in [Import, export and backup](../import-export).

## High availability

Active/passive operation requires:

- at least two NodePilot nodes,
- a shared external database,
- identical JWT parameters,
- `Cluster:Enabled=true`,
- AES-GCM as the secret provider,
- HAProxy with a leader probe on `/healthz/leader`,
- with OIDC, a shared, certificate-protected data-protection key ring.

The complete setup is under [High availability](../enterprise/high-availability).

## Enabling enterprise features

Recommended order:

1. Enable SIEM logging.
2. Configure the secret provider and, if applicable, HA.
3. Verify the local break-glass admin account.
4. Test the LDAP or Windows configuration against real domain controllers.
5. Restart the service.
6. Check `/healthz/ready` and `/healthz/directory`.
7. Enable OIDC and SCIM only after the provider and offboarding tests are complete.

LDAP, Windows SSO, OIDC and SCIM should be treated as preview until the field test passes. Details are in [AD SSO Preview](../enterprise/ldap-windows-sso).
