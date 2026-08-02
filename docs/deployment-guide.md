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
  SCCM site server).
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
| **Artifact signing** (code signing) | build host | public `.cer` in `LocalMachine\Root` of the NodePilot server | chain must validate on the target |
| **Kestrel HTTPS** | NodePilot server | `LocalMachine\My` + (self-signed) `LocalMachine\Root` there and on browser clients | CN/SAN = public hostname |
| **SQL Server TLS** | SQL server | `LocalMachine\My` on the SQL server + (self-signed) `LocalMachine\Root` on the NodePilot server | RSA with `KeySpec=KeyExchange`, CN/SAN = SQL host FQDN |

## Step 1 — Get the signed artifact

You can either **download** the published one or **build your own**. The installer does not care
which — it cares that the artifact is signed and that you tell it which publisher to trust.

### Option A — download the published release

Take these from the [latest release](https://github.com/Sev7eNup/NodePilot/releases/latest):

- `NodePilot-<version>.zip`
- `NodePilot-<version>.zip.manifest.json`
- `NodePilot-<version>.zip.manifest.json.p7s`
- `SHA256SUMS.txt`
- `nodepilot-release-signing.cer` — the public signing certificate

Verify the download, then trust the publisher on the target server:

```powershell
# 1. Checksums (compare against SHA256SUMS.txt)
Get-FileHash .\NodePilot-1.0.1.zip -Algorithm SHA256 | Format-List

# 2. The certificate you are about to trust is the one named in the release notes
(Get-PfxCertificate .\nodepilot-release-signing.cer).Thumbprint

# 3. Import it so the signature chain validates on this machine (elevated)
Import-Certificate -FilePath .\nodepilot-release-signing.cer -CertStoreLocation Cert:\LocalMachine\Root
```

The thumbprint printed in step 2 is what you pass as `-TrustedArtifactSignerThumbprint`. **Compare
it against the value published in the release notes before importing** — importing a certificate
into `LocalMachine\Root` makes that publisher trusted for the whole machine.

> The published artifact is signed with a **self-signed** publisher certificate, not one issued by
> a public CA. That is why the thumbprint is published and why you verify it out-of-band. If your
> organisation will not trust a self-signed publisher, use Option B and sign with your own
> enterprise code-signing certificate.

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

# Same run, plus the desktop installer (needs Inno Setup 6 and a PostgreSQL 16 "pgsql" folder;
# without them the desktop step is skipped with a warning and the server zip is still produced).
# -DesktopSigningCertificateThumbprint Authenticode-signs the .exe as part of the build - signing
# it afterwards would invalidate its SHA256SUMS entry.
.\deploy\Build-Artifact.ps1 -SigningCertificateThumbprint $signer.Thumbprint `
    -IncludeDesktopInstaller -PgBinariesPath 'C:\Packages\pgsql' `
    -DesktopSigningCertificateThumbprint $signer.Thumbprint
```

Copy **four files** to the target server (e.g. `C:\Temp`):

- `out\NodePilot-1.0.1.zip`
- `out\NodePilot-1.0.1.zip.manifest.json`
- `out\NodePilot-1.0.1.zip.manifest.json.p7s`
- `nodepilot-signer.cer`

`out\NodePilot-1.0.1.SHA256SUMS.txt` covers everything the run produced, if you want to verify the
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

## Step 3 — Install (target server)

Trust the artifact signer and create the Kestrel HTTPS certificate:

```powershell
Import-Certificate -FilePath C:\Temp\nodepilot-signer.cer -CertStoreLocation Cert:\LocalMachine\Root

$tls = New-SelfSignedCertificate -DnsName 'nodepilot.corp.example.com' `
    -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddYears(5)
# Self-signed → trust it locally too (repeat the Root import on every browser client):
Export-Certificate -Cert $tls -FilePath "$env:TEMP\nodepilot-tls.cer" | Out-Null
Import-Certificate -FilePath "$env:TEMP\nodepilot-tls.cer" -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
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
  wildcards are rejected at boot.
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
| `admin-setup.token` → *Access to the path is denied* | intentional owner-only ACL for the service account | read via `robocopy /B` as shown in [Step 4](#step-4--first-login) instead of editing the ACL |
| Every `runScript` step fails with `The term 'Write-Output' is not recognized` | artifact built with a pre-2026-08 `Build-Artifact.ps1` that did not stage the PowerShell built-in modules — `$PSHOME\Modules` is missing in the install dir | rebuild with the current build script; or hot-fix in place: `Copy-Item 'C:\Program Files\NodePilot\runtimes\win\lib\net10.0\Modules' 'C:\Program Files\NodePilot\Modules' -Recurse` and restart the service |
| Installer prints `FAILED: ... Restoring the previous installation` | any error after mutation began rolls back to the previous state | fix the reported cause and re-run; note the diagnostics tail the shared log file, so lines from the *previous* installation can appear — check timestamps |
| Browser shows *Not secure* / `Invoke-RestMethod` trust error | self-signed Kestrel certificate not trusted on the client | import it into `LocalMachine\Root` on that machine |
| Upgrade fails with *Access to the path '…\<some>.dll' is denied* | a process is still running from the install directory and keeps DLLs mapped, even though the service is stopped | `tasklist /m <dll>` names the holder; `Get-Process \| Where-Object { $_.Path -like 'C:\Program Files\NodePilot\*' } \| Stop-Process -Force`, then re-run. Current builds abort before deleting anything and print the PID |
| Browser shows `{"message":"Token is no longer valid"}` instead of the app | session cookie outlived `Authentication:SessionAbsoluteLifetimeHours` (8 h); artifacts built before 2026-08-02 answered SPA navigations with that 401 | clear the site's cookies to get back in; upgrade to a current artifact to stop it recurring |
| AD login fails with correct credentials; audit shows `ldap_user_object_not_found`, log says *bind succeeded but no user object found* | the account's `userPrincipalName` attribute is unset or uses another suffix — AD still binds via the implicit `samAccountName@domain`, but the lookup searches that attribute | `Set-ADUser <user> -UserPrincipalName '<user>@corp.example.com'`; also verify `BaseDn` covers the account's OU |
| AD login fails, audit shows `local_login_policy` although LDAP is configured | LDAP was not active for that request — the settings were saved but not yet in effect | re-check that the LDAP section is enabled and saved (HTTP 200), then retry |
| AD login fails, audit shows `no_allowed_directory_group` (`USER_DIRECTORY_ACCESS_REFUSED`) | credentials are fine, but the user is in none of the configured `AllowedGroupSids` groups | `Get-ADGroupMember 'NodePilot-Users'` and compare the SID with the configured one |
| AD login refused with no audit row at all, HTTP 503 | directory unavailable (all endpoints failed). Artifacts before 2026-08-02 wrote no audit entry here | check `/healthz/directory` and the DC; current builds audit this case |

## Upgrade, reinstall, uninstall

**Upgrade:** build a new artifact, then run
[`Update-NodePilot.ps1`](../deploy/Update-NodePilot.ps1) — in-place, keeps
`appsettings.Production.json` and the database, rolls back on failure:

```powershell
.\Update-NodePilot.ps1 `
    -ArtifactPath C:\Temp\NodePilot-1.1.0.zip `
    -TrustedArtifactSignerThumbprint '<signer thumbprint>'
```

The health probe follows the port in the installed configuration, so a non-default
`-HttpsPort` does not have to be repeated here. Two behaviours worth knowing:

- **Processes still running from the install directory abort the upgrade before anything is
  deleted**, naming the PID. A stopped service is not enough — an orphaned worker keeps its
  DLLs mapped, and Windows reports that as a plain *Access denied* mid-wipe. Stop the named
  process and re-run.
- The binary backup deliberately **excludes** `appsettings.Production.json` (it holds
  secrets). It is the last file removed during the swap, so an aborted upgrade leaves it in
  place — but if it is ever lost, do not re-run the update: it refuses a layout without a
  config. Re-run `Install-NodePilot.ps1` instead, which re-renders the config from its
  parameters. The database, the data directory and the admin accounts are untouched by
  either path; only the External-Trigger API key is regenerated.

**Uninstall:** [`Uninstall-NodePilot.ps1`](../deploy/Uninstall-NodePilot.ps1) stops the
service and removes binaries + firewall rule; the database and
`C:\ProgramData\NodePilot` survive. Add `-PurgeData` to also wipe the data directory
(logs, JWT key, setup token — irreversible), then drop the database
(`DROP DATABASE [NodePilot];`) and remove the SQL login and certificates if desired.

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
