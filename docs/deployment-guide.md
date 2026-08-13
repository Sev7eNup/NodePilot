# NodePilot Production Deployment Guide

End-to-end walkthrough for installing NodePilot on a Windows server — from building the
signed artifact to the first admin login. Written for operators deploying NodePilot for the
first time. Every step has been validated on a domain-joined Windows Server co-installed
next to an SCCM site server, against SQL Server 2022, without an enterprise PKI
(self-signed certificates throughout).

Parameter-by-parameter reference and HA/cluster setup live in
[`deploy/README.md`](../deploy/README.md); the desktop installer has its own guide in
[`deploy/desktop/README.md`](../deploy/desktop/README.md).

## What gets installed

- A Windows service (`NodePilot`) hosting the ASP.NET Core API **and** the web UI on one
  HTTPS port — no IIS involved, Kestrel serves everything.
- Binaries in `C:\Program Files\NodePilot` (read-only for the service), runtime data in
  `C:\ProgramData\NodePilot` (config, logs, generated secrets).
- A PostgreSQL or SQL Server database (schema is created/migrated automatically at first
  start).

Everything ships as a ZIP artifact with a detached CMS signature. The installer refuses
unsigned or tampered artifacts, so a code-signing certificate is part of the setup.

## Prerequisites

- **Windows Server** (domain-joined recommended), elevated **Windows PowerShell 5.1** for
  both scripts.
- **ASP.NET Core Runtime 10 (x64)** — the plain runtime, **not** the Hosting Bundle. The
  bundle rewires IIS and restarts W3SVC, which you do not want on a shared host (e.g. an
  SCCM site server). The `x64` is a requirement, not a preference: NodePilot ships as
  `win-x64`, a 32-bit runtime cannot start the service, and the pre-flight rejects one by
  name instead of waving it through.
- **SQL Server 2022 CU1 or later** (build ≥ `16.0.4003.1`) or PostgreSQL. NodePilot
  connects with `Encrypt=Strict` (TDS 8.0) and refuses to boot with anything weaker:
  SQL Server 2019 and older cannot speak TDS 8.0 at all, and 2022 **RTM** has a TDS 8.0
  bug that corrupts parameterized statements (error 8005) — patched in CU1. Check first:

  ```sql
  SELECT SERVERPROPERTY('ProductVersion') AS Version, SERVERPROPERTY('ProductUpdateLevel') AS CU;
  -- 16.0.1000.x = 2022 RTM (unpatched) → install the latest 2022 CU before proceeding.
  ```

- **A service identity.** Recommended: a **gMSA** — the service then reaches SQL Server
  and WinRM target machines with Kerberos, no stored passwords. Alternative: `LocalSystem`
  (the service authenticates as the computer account `DOMAIN\HOST$`).

  ```powershell
  # On a domain controller (once):
  New-ADServiceAccount -Name svc-nodepilot -DNSHostName svc-nodepilot.corp.example.com `
      -PrincipalsAllowedToRetrieveManagedPassword 'APPHOST$'

  # On the NodePilot server:
  Install-ADServiceAccount svc-nodepilot
  Test-ADServiceAccount svc-nodepilot    # must return True
  ```

- **A free HTTPS port.** Default is 443. On a host where IIS/http.sys owns 80/443 (SCCM,
  WSUS, …), use `-HttpsPort 8443 -HttpPort 0` instead.

- **A network path that passes WebSocket upgrades.** The live view opens a SignalR
  connection to `/hubs/execution`. A proxy or TLS-inspection appliance that drops the
  `Upgrade: websocket` handshake does **not** break the product — SignalR falls back to
  Server-Sent Events on its own — but it fills every user's browser console with connect
  errors and costs efficiency. Worth clearing with your network team up front; see
  [Is the live connection healthy?](#is-the-live-connection-healthy).

- **Antivirus exclusions agreed with your security team.** The service starts PowerShell
  child processes and executes generated scripts out of `%TEMP%`; without exceptions,
  endpoint protection blocks individual steps or the install-directory swap during an
  upgrade. Hand-off list with per-entry rationale and residual risk:
  [av-exclusions.md](av-exclusions.md) (German).

### The three certificates

Without an enterprise CA you create all three self-signed; with a CA, issue them there and
skip the `Root` imports.

| Purpose | Created on | Must end up in | Requirements |
|---|---|---|---|
| **Artifact signing** (code signing) | build host | nowhere — the installer pins the thumbprint. Importing the public `.cer` into `LocalMachine\Root` is optional | code-signing EKU, a KeyUsage that permits signing, currently valid |
| **Kestrel HTTPS** | NodePilot server | `LocalMachine\My` + (self-signed) `LocalMachine\Root` there and on browser clients | CN/SAN = public hostname |
| **SQL Server TLS** | SQL server | `LocalMachine\My` on the SQL server + (self-signed) `LocalMachine\Root` on the NodePilot server | RSA with `KeySpec=KeyExchange`, CN/SAN = SQL host FQDN |

> **Shortcut: the GUI setup.** `NodePilot-Server-Setup-<version>.exe` performs exactly the
> installation described below, driven by a wizard. It bundles the signed artifact and the ASP.NET
> Core runtime, checks every prerequisite before it changes anything, and can create the SQL login
> and database for you if your account may. It does not need the publisher to be trusted here - the
> signature is checked against a thumbprint compiled into the setup - and the readiness page shows
> that thumbprint with an optional offer to import it into `LocalMachine\Root` anyway. For the Kestrel certificate it asks only for the
> thumbprint and offers the machine store's certificates in a list below the field, so a PKI
> certificate from your own CA needs importing and picking, nothing typed. Leaving the field empty
> is allowed and means "I have none yet": the prerequisite page then offers to create a self-signed
> one for lab and pilot use. It refuses to
> install against one that has expired instead of leaving that for the first user to discover in a
> browser, while a certificate issued for a different name warns without blocking. The installation itself
> reports the phase it is in rather than sitting on a blank page for the couple of minutes it takes.
> That collapses Step 1 to "download one file" and Step 3 to "click Next". It also runs unattended:
> `Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /ANSWERFILE=answers.json` — and with a `bootstrap` group
> in that answer file it creates the first administrator itself, with a password generated per
> machine and written to an ACL-protected file, so a rollout does not end waiting for someone to
> type a setup token into a browser.
>
> For the wizard's own step-by-step - what to have ready, which page asks for what, what the finish
> page shows - see the **Quick start** at the top of
> [`deploy/server/README.md`](../deploy/server/README.md#quick-start).
>
> This guide keeps the scripted path as the reference, because it is what the wizard runs and what
> you will want for automation and for troubleshooting. See
> [`deploy/server/README.md`](../deploy/server/README.md) for the wizard, its answer-file schema
> and its switches.

## Step 1 — Get the signed artifact

You can either **download** the published one or **build your own**. The installer does not care
which — it cares that the artifact is signed and that you tell it which publisher to trust.

### Option A — download the published release

Take these from the [latest release](https://github.com/Sev7eNup/NodePilot/releases/latest):

- `NodePilot-<version>.zip`
- `NodePilot-<version>.zip.manifest.json`
- `NodePilot-<version>.zip.manifest.json.p7s`
- `NodePilot-<version>.SHA256SUMS.txt`
- `nodepilot-release-signing.cer` — the public signing certificate

Verify the download:

```powershell
# 1. Checksums (compare against NodePilot-1.2.0.SHA256SUMS.txt)
Get-FileHash .\NodePilot-1.2.0.zip -Algorithm SHA256 | Format-List

# 2. The publisher you are about to pin is the one named in the release notes
(Get-PfxCertificate .\nodepilot-release-signing.cer).Thumbprint
```

The thumbprint printed in step 2 is what you pass as `-TrustedArtifactSignerThumbprint`. **Compare
it against the value published in the release notes** — that comparison is the trust decision, and
it is why the value is published out of band.

**You do not have to import anything.** The installer verifies the signature and requires the
signer to be exactly that thumbprint; it also checks that the certificate is valid for code signing
and currently valid. It does *not* require the publisher to be trusted on the machine, because for
a self-signed publisher the trust anchor is the same certificate — so importing it would only
restate the thumbprint you already compared, at the price of a permanent, machine-wide change.

Optionally, and for a different reason:

```powershell
# Makes Windows validate the Authenticode signature of the NodePilot installers themselves
Import-Certificate -FilePath .\nodepilot-release-signing.cer -CertStoreLocation Cert:\LocalMachine\Root
```

That affects how Windows treats *future* NodePilot binaries on this machine; it does not
retroactively authenticate anything already running, and it does not silence SmartScreen.

> The published artifact is signed with a **self-signed** publisher certificate, not one issued by
> a public CA. That is why the thumbprint is published and why you verify it out-of-band. If your
> organisation wants a chain it already trusts, use Option B and sign with your own enterprise
> code-signing certificate — note that the installer does not validate the chain either way, so
> that choice is about your own policy rather than about what the installer will accept.

### Option B — build it yourself (build host)

Create the signing certificate once and keep it:

```powershell
$signer = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=NodePilot Release Signing' `
    -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5)
Export-Certificate -Cert $signer -FilePath .\nodepilot-signer.cer
```

Build (needs the .NET 10 SDK and Node — versions are pinned in `global.json` and the `engines`
fields). `-Version` defaults to the product version in `Directory.Build.props`, so pass it only
when you want something else:

```powershell
.\deploy\Build-Artifact.ps1 -SigningCertificateThumbprint $signer.Thumbprint

# Same run, plus both installers (needs Inno Setup 6, and a PostgreSQL 16 "pgsql" folder for the
# desktop bundle; without them the desktop step is skipped with a warning and the server zip is
# still produced). -InstallerSigningCertificateThumbprint Authenticode-signs both .exe files as
# part of the build - signing them afterwards would invalidate their SHA256SUMS entries.
.\deploy\Build-Artifact.ps1 -SigningCertificateThumbprint $signer.Thumbprint `
    -IncludeServerInstaller -IncludeDesktopInstaller -PgBinariesPath 'C:\Packages\pgsql' `
    -InstallerSigningCertificateThumbprint $signer.Thumbprint
```

Copy **four files** to the target server (e.g. `C:\Temp`):

- `out\NodePilot-1.2.0.zip`
- `out\NodePilot-1.2.0.zip.manifest.json`
- `out\NodePilot-1.2.0.zip.manifest.json.p7s`
- `nodepilot-signer.cer`

`out\NodePilot-1.2.0.SHA256SUMS.txt` covers everything the run produced, if you want to verify the
transfer.

plus the `deploy\` folder itself (`Install-NodePilot.ps1` + `ArtifactSecurity.ps1`).

## Step 2 — Prepare SQL Server

**TLS certificate.** SQL Server only lists certificates that are RSA with
`KeySpec=KeyExchange` — the default CNG key of `New-SelfSignedCertificate` is invisible to
it, so the provider flags below are load-bearing:

```powershell
New-SelfSignedCertificate -DnsName 'sql1.corp.example.com' `
    -CertStoreLocation Cert:\LocalMachine\My `
    -KeySpec KeyExchange `
    -Provider 'Microsoft RSA SChannel Cryptographic Provider' `
    -KeyLength 2048 -NotAfter (Get-Date).AddYears(5)
```

1. Grant the SQL service account (default `NT Service\MSSQLSERVER`) read access to the
   private key: `certlm.msc` → Personal → certificate → *All Tasks → Manage Private Keys*.
2. Assign it: SQL Server Configuration Manager → *Protocols for MSSQLSERVER* →
   *Certificate* tab. Leave **Force Encryption = No** — NodePilot always encrypts on its
   own, and forcing it server-wide breaks other clients of a shared instance (e.g. remote
   ConfigMgr site systems) that don't trust a self-signed certificate.
3. Restart the SQL Server service and confirm the ERRORLOG line
   `The certificate ... was successfully loaded for encryption`.
4. Self-signed: import the certificate's public part into `LocalMachine\Root` **on the
   NodePilot server** — the runtime verifies the chain (`TrustServerCertificate=False`).

**Database and login** for the service identity (SSMS, as sysadmin). For `LocalSystem`,
replace the gMSA with the computer account `CORP\APPHOST$`:

```sql
CREATE LOGIN [CORP\svc-nodepilot$] FROM WINDOWS;
CREATE DATABASE [NodePilot];
GO
USE [NodePilot];
CREATE USER [CORP\svc-nodepilot$] FOR LOGIN [CORP\svc-nodepilot$];
ALTER ROLE db_owner ADD MEMBER [CORP\svc-nodepilot$];
```

The installer enables `READ_COMMITTED_SNAPSHOT` on the database automatically (warning
only if it lacks permission).

The same applies on PostgreSQL, where the setup ships `psql` and can create the role and the
database — but PostgreSQL has no counterpart to `Trusted_Connection`, so that one needs superuser
credentials (`provisioning.postgresSuperUser` / `.postgresSuperPassword`, or the two extra fields
on the credentials page). It never overwrites an existing role's password.

You can skip this step if you install with `NodePilot-Server-Setup-<version>.exe` and the
account running it is `sysadmin` (or holds `CREATE ANY DATABASE`). The readiness page checks
the service identity's login, database user and `db_owner` membership separately from plain
reachability — reachability is tested as *you*, the service connects as itself — and offers
to create whatever is missing, ticked by default. Unattended, ask for it with
`"provisioning": { "createDatabaseAndLogin": true }` in the answer file; on the console path
`deploy\Provision-NodePilotDatabase.ps1` does the same in one call. All three are
existence-guarded and change nothing without the permissions above, in which case they print
the statements for a DBA.

## Step 3 — Install (target server)

Create the Kestrel HTTPS certificate:

```powershell
$tls = New-SelfSignedCertificate -DnsName 'nodepilot.corp.example.com' `
    -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddYears(5)
# Self-signed → trust it locally too (repeat the Root import on every browser client):
Export-Certificate -Cert $tls -FilePath "$env:TEMP\nodepilot-tls.cer" | Out-Null
Import-Certificate -FilePath "$env:TEMP\nodepilot-tls.cer" -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
```

**Nothing needs to be imported for the artifact check.** As Step 1 says, the installer pins the
signer thumbprint you pass on the command line and builds no certificate chain, so the publisher
certificate does not have to be trusted on this machine. Importing it is optional and does one
unrelated thing — it makes Windows validate the Authenticode signature of the NodePilot installers
themselves:

```powershell
# Optional. Not required by -TrustedArtifactSignerThumbprint; does not silence SmartScreen.
Import-Certificate -FilePath C:\Temp\nodepilot-signer.cer -CertStoreLocation Cert:\LocalMachine\Root
```

Run the installer (elevated Windows PowerShell 5.1):

```powershell
.\Install-NodePilot.ps1 `
    -ArtifactPath C:\Temp\NodePilot-1.0.0.zip `
    -TrustedArtifactSignerThumbprint '<signer thumbprint>' `
    -ServiceAccount 'CORP\svc-nodepilot$' `
    -SqlServer 'sql1.corp.example.com' `
    -SqlDatabase 'NodePilot' `
    -CertThumbprint $tls.Thumbprint `
    -PublicHostname 'nodepilot.corp.example.com' `
    -AllowedHosts 'nodepilot.corp.example.com;nodepilot;localhost' `
    -HttpsPort 8443 -HttpPort 0
```

- `-UseLocalSystem` replaces `-ServiceAccount` for the computer-account variant.
- `-AllowedHosts` is fail-closed: list **every** name users will type into the browser;
  wildcards are rejected at boot. `localhost` is appended for you whether you list it or
  not — the installer's own health probe requests `https://localhost:<port>/healthz/ready`,
  and host filtering would answer that 400 and roll back a finished installation.
- Postgres instead of SQL Server: `-DbProvider postgres -PostgresHost ... -PostgresUser ...`
  (see `deploy/README.md`).

What the installer does, in order: verify the artifact signature → preflight (runtime
version, SQL reachability, **SQL version gate ≥ 2022 CU1**, gMSA retrievability) →
snapshot any existing installation → extract → render `appsettings.Production.json` →
register the service, grant *Log on as a service* + private-key read → firewall rule
(Domain profile) → start and poll `https://localhost:<port>/healthz/ready` for up to
180 s → print the External-Trigger API key (**shown once — store it**) and the first-login
setup token. **Any failure after mutation starts triggers an automatic rollback** to the
snapshotted state.

## Step 4 — First login

The very first login creates the admin account, gated by a one-shot **setup token** so
that whoever races to the login endpoint first cannot make themselves admin.

1. Browse to `https://nodepilot.corp.example.com:8443/`.
2. Enter your desired admin username and password (min. 8 characters) and sign in. The
   page answers with *"First-time setup"* and reveals a **Setup token** field.
3. Paste the token printed at the end of the install output, sign in again — done. The
   server deletes the token file and the bootstrap window closes permanently.

If you installed with the GUI setup, its final page carries all of this — address, setup
token, External-Trigger API key, certificate thumbprint, service name and paths — as
selectable text, with a button to save it to a file. The API key appears there and
nowhere else: it is not recoverable afterwards, and `install-report.txt` omits it by
design.

If the installer could not print the token: it lives in
`C:\ProgramData\NodePilot\admin-setup.token`, which is ACL-restricted to the **service
account** — by design even administrators cannot open it directly. Read it via backup
semantics, which sidesteps the ACL entirely:

```powershell
robocopy C:\ProgramData\NodePilot $env:TEMP admin-setup.token /B | Out-Null
Get-Content "$env:TEMP\admin-setup.token"
Remove-Item "$env:TEMP\admin-setup.token"   # after the first login
```

(Prefer `robocopy /B` over editing the ACL. The server validates the token file
fail-closed and trusts only the service identity, `SYSTEM` and the built-in
*Administrators* **group** — `takeown /a` plus a group-level `icacls` grant survives that
check, but taking ownership as your personal admin account invalidates the file.)

Note: the bootstrap admin is a **break-glass** account. The production default
`Authentication:LocalLoginMode = BreakGlassOnly` blocks additional plain local accounts —
connect LDAP / Windows SSO / OIDC for regular users (see `docs/ldap-windows-sso.md`), or
switch the mode in the admin settings (requires a service restart).

## Verify

```powershell
Get-Service NodePilot                                          # Running
Invoke-RestMethod https://nodepilot.corp.example.com:8443/healthz/ready   # Healthy
Get-Content C:\ProgramData\NodePilot\install-report.txt        # what was installed, no secrets
```

Logs: `C:\ProgramData\NodePilot\logs\` (CMTrace-formatted). Firewall rule:
`NodePilot NodePilot HTTPS` (Domain profile only — add rules yourself for other profiles).

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Build: `npm ci failed with exit code N` | real npm failure; commonly an `EPERM` file lock in `node_modules` | close the Vite dev server / editor / AV scan and retry, or `-SkipNpmCi` to reuse warm `node_modules` |
| Install preflight: `No such host is known` | `-SqlServer` / `-PublicHostname` not resolvable | use full FQDNs and verify DNS |
| SQL preflight: SSL handshake error / `The wait operation timed out` | no TLS certificate assigned to SQL Server, or the certificate's key is CNG instead of `KeySpec=KeyExchange`, or the cert isn't trusted on the NodePilot server | redo [Step 2](#step-2--prepare-sql-server) |
| Preflight: `SQL version pre-flight FAILED` — or, on older installer versions, the service boot-loops with TDS **error 8005** (`The parameter name is invalid`) | SQL Server 2022 RTM (or 2019 and older) cannot serve `Encrypt=Strict` | install the latest SQL Server 2022 CU (≥ 16.0.4003.1) |
| Service starts, `/healthz/ready` stays 503, log shows `Login failed for user 'DOMAIN\...$'` | service identity has no SQL login / no DB user | grant it as in [Step 2](#step-2--prepare-sql-server) |
| Install waits out the full 180 s health probe and rolls back; Application log shows `SocketException (10013)` from `AnyIPListenOptions.BindAsync` | Kestrel cannot bind a configured port. **10013 is not "in use"** — Windows returns it for an HTTP.SYS reservation, and on any host running IIS (a ConfigMgr site server, for example) ports 80 and 443 are reserved with no listener to find | set `-HttpPort 0` to drop the redirect, or move the ports. `netsh interface ipv4 show excludedportrange protocol=tcp` lists every reservation. The GUI setup checks this on its Prerequisites page before installing |
| After a reboot the service is still stopped, then comes up on its own | artifacts built before 2026-08-03 registered the service as *Automatic (Delayed Start)*, which idles ~120 s after boot before starting anything | expected on those builds — nothing is broken. Current builds start immediately and wait for the database instead; the boot log names what it is waiting for |
| Boot log repeats `Waiting for the database to accept connections (n/120s)` | the database is not answering yet — a remote SQL Server still recovering, a DC not yet reachable for Kerberos, or a wrong host | let it finish; it proceeds either way and then reports the real connection error. Raise `Database:StartupWaitSeconds` (max 600) if the database routinely needs longer |
| Event log 7000 *the service did not start due to a logon failure*, gMSA identity, only on boot | the service tried to log on before Netlogon could fetch the gMSA password from a DC | current builds set `depend= Netlogon` for gMSA services; on older ones `sc.exe config NodePilot depend= Netlogon` fixes it in place |
| `admin-setup.token` → *Access to the path is denied* | intentional owner-only ACL for the service account | read via `robocopy /B` as shown in [Step 4](#step-4--first-login) instead of editing the ACL |
| Install fails with `JWT signing-key file security validation failed: parent directory 'C:\ProgramData\NodePilot' grants mutation rights to an untrusted principal`, then rolls back | an ACE on the data directory belongs to a principal the service does not trust — in practice the service account of an **earlier** installation, because an ACE is only trusted while the service actually runs as that account. Not a version problem; the check has existed since 1.0.0 | installers from 2026-08-12 on verify the directory with the service's own rule after applying the ACL, repair it, and only then start the service — so this no longer reaches the service. On older builds: `icacls C:\ProgramData\NodePilot` names the stranger, `icacls C:\ProgramData\NodePilot /remove:g "<account>"` removes it. **`Jwt:RotateInsecureKeyFile=true` does not help here** — it replaces the key file, and the directory is what was rejected |
| Every `runScript` step fails with `The term 'Write-Output' is not recognized` | artifact built with a pre-2026-08 `Build-Artifact.ps1` that did not stage the PowerShell built-in modules — `$PSHOME\Modules` is missing in the install dir | rebuild with the current build script; or hot-fix in place: `Copy-Item 'C:\Program Files\NodePilot\runtimes\win\lib\net10.0\Modules' 'C:\Program Files\NodePilot\Modules' -Recurse` and restart the service |
| Installer prints `FAILED: ... Restoring the previous installation` | any error after mutation began rolls back to the previous state | fix the reported cause and re-run; note the diagnostics tail the shared log file, so lines from the *previous* installation can appear — check timestamps |
| Browser shows *Not secure* / `Invoke-RestMethod` trust error | self-signed Kestrel certificate not trusted on the client | import it into `LocalMachine\Root` on that machine |
| Upgrade fails with *Access to the path '…\<some>.dll' is denied* | a process is still running from the install directory and keeps DLLs mapped, even though the service is stopped | `tasklist /m <dll>` names the holder; `Get-Process \| Where-Object { $_.Path -like 'C:\Program Files\NodePilot\*' } \| Stop-Process -Force`, then re-run. Current builds abort before deleting anything and print the PID |
| Upgrade fails with *Processes are still running from … and could not be ended* | something under the install directory survived the 30-second grace period **and** could not be stopped — in practice a permissions problem or a hung kernel call | the message names the PID. Nothing was deleted and the service is untouched, so end it yourself or reboot, then re-run. Artifacts before 2026-08-03 reported this immediately after stopping the service, naming the very process they had just stopped; that was a missing wait, not a stuck process |
| Browser shows `{"message":"Token is no longer valid"}` instead of the app | session cookie outlived `Authentication:SessionAbsoluteLifetimeHours` (8 h); artifacts built before 2026-08-02 answered SPA navigations with that 401 | clear the site's cookies to get back in; upgrade to a current artifact to stop it recurring |
| Browser console repeats `WebSocket connection to 'wss://…/hubs/execution?id=…' failed` and `Failed to start the transport 'WebSockets'` — **but the UI still updates live** | The `?id=` proves the `negotiate` call already succeeded, so only the `Upgrade: websocket` handshake is being dropped — in practice by a proxy or a TLS-inspection appliance. SignalR then falls back to Server-Sent Events, which is why live updates keep working. Note that Kestrel advertises HTTP/2 via ALPN, so a TLS-terminating middlebox has to relay WebSockets as RFC 8441 *Extended CONNECT* — many cannot, while ordinary requests pass through unnoticed | have the host bypassed in the proxy/PAC file, or WebSocket passthrough enabled for it. Confirm the cause first with [Is the live connection healthy?](#is-the-live-connection-healthy): a server-side 503 produces the very same console message |
| AD login fails with correct credentials; audit shows `ldap_user_object_not_found`, log says *bind succeeded but no user object found* | the account's `userPrincipalName` attribute is unset or uses another suffix — AD still binds via the implicit `samAccountName@domain`, but the lookup searches that attribute | `Set-ADUser <user> -UserPrincipalName '<user>@corp.example.com'`; also verify `BaseDn` covers the account's OU |
| AD login fails, audit shows `local_login_policy` although LDAP is configured | LDAP was not active for that request — the settings were saved but not yet in effect | re-check that the LDAP section is enabled and saved (HTTP 200), then retry |
| AD login fails, audit shows `no_allowed_directory_group` (`USER_DIRECTORY_ACCESS_REFUSED`) | credentials are fine, but the user is in none of the configured `AllowedGroupSids` groups | `Get-ADGroupMember 'NodePilot-Users'` and compare the SID with the configured one |
| AD login refused with no audit row at all, HTTP 503 | directory unavailable (all endpoints failed). Artifacts before 2026-08-02 wrote no audit entry here | check `/healthz/directory` and the DC; current builds audit this case |

### Is the live connection healthy?

The SPA's live view (running steps, execution status, dashboard counters) rides on a SignalR
connection to `/hubs/execution`. The client negotiates a transport and walks down a ladder —
**WebSockets → Server-Sent Events → long polling** — so a blocked WebSocket upgrade degrades
efficiency rather than function. The console error it prints on the way down looks alarming and
is easy to misread as an outage.

**1. Find out which transport is actually carrying the connection.** DevTools → *Network*, filter
for `hubs/execution`, and look at what follows the `negotiate` request:

| What you see after `negotiate` (200) | Meaning |
|---|---|
| One request answered `101 Switching Protocols` | WebSockets fine, nothing to do |
| One long-pending `GET …?id=…` of type `text/event-stream` | Server-Sent Events fallback — the live view works, the console noise is cosmetic |
| `GET`/`POST` pairs repeating every few seconds | long polling, the most expensive fallback |

**2. Prove whether the network path is to blame.** On Windows, `curl.exe` ignores the WinINET
proxy settings unless told otherwise — which makes it a clean A/B against the browser:

```powershell
# (a) direct, no proxy
curl.exe -sSik --http1.1 -H "Connection: Upgrade" -H "Upgrade: websocket" `
    -H "Sec-WebSocket-Version: 13" -H "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==" `
    "https://nodepilot.corp.example.com:8443/hubs/execution?id=x"

# (b) the same request through the corporate proxy
curl.exe -sSik --http1.1 --proxy http://proxy.corp.example.com:8080 `
    -H "Connection: Upgrade" -H "Upgrade: websocket" `
    -H "Sec-WebSocket-Version: 13" -H "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==" `
    "https://nodepilot.corp.example.com:8443/hubs/execution?id=x"
```

(a) should return a recognisable ASP.NET Core answer — `401` for the missing auth cookie, or
`400`/`404`. Any of those prove the upgrade request reached the application. If (b) instead
returns `200` with `index.html`, a proxy error page, or simply hangs, the proxy is the culprit.
Cross-check by opening the same page from a client with no proxy in the path (the server itself,
for instance): the `wss://` errors disappear there.

**3. Rule out the server-side look-alikes.** Two conditions produce a byte-identical browser
message, because they answer the upgrade request itself rather than dropping it:

- **The database breaker is open.** `/hubs` is sealed with `503` while the database is
  unreachable, deliberately including requests that already carry a connection id. Look for
  `503` / `DATABASE_UNAVAILABLE` on `/hubs/execution` in `C:\ProgramData\NodePilot\logs\`. This
  is ruled out whenever live updates still arrive — the fallback transports would be blocked
  just the same.
- **A follower node answered.** With `Cluster:Enabled`, `/hubs/` is leader-only and followers
  reply `503`. There is no SignalR backplane and hub state is per-process, so any second serving
  instance also breaks the connection-id affinity. Only relevant in an HA setup; check
  `/healthz/leader`.

A Content-Security-Policy problem is *not* a candidate: the browser words that differently
(*"Refused to connect"*), and the page would not have loaded to begin with.

> Unrelated console noise worth knowing about: browser extensions inject their own content
> scripts into the page, and those log under their own file names (a bare `common.js`, for
> example) with no relation to NodePilot. The SPA ships as a single hash-named bundle
> (`assets/index-<hash>.js`) and loads no third-party script — the production CSP is
> `script-src 'self'`. Re-test in a clean browser profile with extensions disabled to separate
> the two.

## Upgrade, reinstall, uninstall

**Upgrade:** build a new artifact, then run
[`Update-NodePilot.ps1`](../deploy/Update-NodePilot.ps1) — in-place, keeps
`appsettings.Production.json` and the database, rolls back on failure:

```powershell
.\Update-NodePilot.ps1 `
    -ArtifactPath C:\Temp\NodePilot-1.2.0.zip `
    -TrustedArtifactSignerThumbprint '<signer thumbprint>'
```

The health probe follows the port in the installed configuration, so a non-default
`-HttpsPort` does not have to be repeated here. Two behaviours worth knowing:

- **Processes still running from the install directory are waited for**, then stopped by
  force if needed. The update waits up to 30 seconds after stopping the service (the SCM
  reports `SERVICE_STOPPED` before the hosting process has actually exited), then kills any
  remaining processes from the install path. Only if a process survives that and cannot be
  ended does the upgrade abort — before any file is touched, naming the PID. A stopped
  service alone is not enough — an orphaned worker keeps its DLLs mapped, and Windows
  reports that as a plain *Access denied* mid-wipe.
- The **only** service setting an update changes is the start type, normalised to plain
  `auto`. Installations made before the API waited for the database on its own carry
  *Automatic (Delayed Start)*, which idles about two minutes past every boot for a wait the
  new binaries now perform themselves. Identity, dependencies and recovery actions stay
  exactly as the installer left them.
- A **successful update always leaves the service running**, regardless of whether it was
  running before. A failed update restores the pre-update state instead — a service that
  was deliberately stopped is not started by a rollback.
- The binary backup deliberately **excludes** `appsettings.Production.json` (it holds
  secrets). It is the last file removed during the swap, so an aborted upgrade leaves it in
  place — but if it is ever lost, do not re-run the update: it refuses a layout without a
  config. Re-run `Install-NodePilot.ps1` instead, which re-renders the config from its
  parameters. The database, the data directory and the admin accounts are untouched by
  either path; only the External-Trigger API key is regenerated.

**Uninstall:** [`Uninstall-NodePilot.ps1`](../deploy/Uninstall-NodePilot.ps1) stops and removes the
service, its registry environment (which holds the Postgres password), the firewall rules, the
installation marker and the binaries. `C:\ProgramData\NodePilot` survives unless you add
`-PurgeData` (logs, JWT key, data-protection keyring — irreversible).

If you installed with the GUI setup, you do not need the script: removal is offered as the third
option on the setup's own start page when it finds an existing installation, and under
*Apps & Features* as usual. Both ask the same single question — keep or delete the data directory —
and both leave the database alone.

**The database is never removed, by either path, and there is no switch for it.** NodePilot did not
create it: you provisioned it in Step 2, it may be replicated or backed up, and in an
active/passive cluster both nodes share one. Drop it yourself once you are sure
(`DROP DATABASE [NodePilot];`), along with the SQL login and any certificates you no longer need.
For the same reason the uninstaller leaves the gMSA's *Log on as a service* right and the read ACE
on the TLS certificate's private key in place — both can be shared with another service. It names
all three at the end of its run rather than leaving you to guess.

If you installed with the GUI setup, uninstall through *Apps & Features* or run
`"C:\Program Files\NodePilot\unins000.exe"`; it asks the same data-directory question and accepts
`/VERYSILENT /SUPPRESSMSGBOXES /PURGEDATA=1` for unattended removal.

## Optional: sign in with Active Directory accounts (LDAP)

Domain users are never imported. They are provisioned on their first successful login,
provided they are a member of an allowed AD group. Full reference — including Windows SSO,
OIDC and SCIM — is in [`docs/ldap-windows-sso.md`](ldap-windows-sso.md); this is the short
path for a single-domain setup.

**Prerequisite: LDAPS.** NodePilot only speaks LDAPS on port 636 with full certificate
validation against the NodePilot server's Windows certificate store. There is no bypass
switch. If the domain has no CA yet, an Enterprise Root CA is the least-effort route — the
DC then enrolls its own certificate automatically, and Group Policy distributes the root to
every domain member, so the NodePilot host trusts it without manual imports:

```powershell
# On the domain controller, once:
Install-WindowsFeature AD-Certificate -IncludeManagementTools
Install-AdcsCertificationAuthority -CAType EnterpriseRootCa -CACommonName 'corp-CA' -Force
```

**Two AD groups, one service account.** Access and role are separate concepts: membership
in an allowed group decides whether someone may sign in at all, a role mapping decides what
they are.

```powershell
New-ADGroup -Name 'NodePilot-Users'  -GroupScope Global -GroupCategory Security
New-ADGroup -Name 'NodePilot-Admins' -GroupScope Global -GroupCategory Security
Add-ADGroupMember -Identity 'NodePilot-Users'  -Members <user>
Add-ADGroupMember -Identity 'NodePilot-Admins' -Members <user>

# Note the SIDs — Get-ADGroup takes ONE identity, so pipe them:
'NodePilot-Users','NodePilot-Admins' | Get-ADGroup |
    Select-Object Name, @{n='SID';e={$_.SID.Value}}

# Read-only service account for the authoritative lookups:
New-ADUser -Name 'svc-nodepilot-dir' -AccountPassword (Read-Host -AsSecureString) `
    -Enabled $true -PasswordNeverExpires $true
Get-ADUser 'svc-nodepilot-dir' | Select-Object -ExpandProperty DistinguishedName
```

**Every account that signs in needs a `userPrincipalName`.** This is the single most common
stumbling block: Active Directory happily accepts a bind as `user@dns-domain` even when the
account's `userPrincipalName` attribute is empty, but NodePilot looks the user up *by that
attribute*. The result is a login that fails with correct credentials. Check and set it:

```powershell
Get-ADUser <user> -Properties UserPrincipalName | Select-Object SamAccountName, UserPrincipalName
Set-ADUser <user> -UserPrincipalName '<user>@corp.example.com'
```

**Configure** under Admin settings → *Authentication*: enable LDAP, one endpoint
(`dc1.corp.example.com:636`), `BaseDn` (`DC=corp,DC=example,DC=com`), `UpnSuffix`
(`corp.example.com`), the service-bind DN and password, the `NodePilot-Users` SID under
allowed groups, and the `NodePilot-Admins` SID mapped to role `Admin`. Use the built-in
readiness/bind test before saving — it checks TLS trust, service bind, search base and group
resolution against the unsaved draft.

Configure **exactly one** endpoint unless every listed DC is always reachable: directory
access is all-DC consensus, not failover, so a second, occasionally-offline DC blocks all AD
logins.

Leave *JIT-user default root role* **empty** unless you want every AD user who passes the
access gate to receive that folder permission on Root — it is granted independently of the
role mapping.

**Sign in** with the bare username (`alice`), not `DOMAIN\alice` and never with a forward
slash. NodePilot appends the UPN suffix itself. A missing role mapping is not an error: the
user is created as Viewer.

Local logins keep working throughout — the bootstrap admin is a break-glass account, and
NodePilot refuses to start with external authentication enabled unless one exists.

## Next steps

- **Onboard target machines:** UI → *Machines* → add by FQDN. Start with WinRM over HTTP
  (5985) plus a credential; for WinRM-HTTPS set port 5986 **and** `UseSsl` on the machine
  and trust the target's listener certificate on the NodePilot server.
- **Alerting, AI features, backups:** see `docs/alerting.md`, `docs/ai-features.md` and
  the System-Backup section in `docs/claude-reference.md`.
