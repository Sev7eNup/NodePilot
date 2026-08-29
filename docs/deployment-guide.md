# NodePilot Production Deployment Guide

Everything you need to do *before* and *after* the installation itself: obtaining the signed
artifact and verifying it against the release checksums and the publisher's code-signing
certificate; building the artifact from source; and a troubleshooting table for what
actually goes wrong in production. The installation walkthrough — service identity, both
database providers, certificates, first admin account — is on the
[documentation site](https://sev7enup.github.io/NodePilot/#/en/deployment/production).

Validated on a domain-joined Windows Server co-installed next to an SCCM site server, with
SQL Server 2022, without an enterprise PKI (self-signed certificates throughout).

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
- **ASP.NET Core Runtime 10.0.11 or newer in the 10.x line (x64)** — the plain runtime, **not** the Hosting Bundle. The
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
  [av-exclusions.md](av-exclusions.md).

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

- `NodePilot-<version>.zip` — the artifact the service is installed from
- `NodePilot-<version>.zip.manifest.json`
- `NodePilot-<version>.zip.manifest.json.p7s`
- `NodePilot-Deploy-Scripts-<version>.zip` — **the installer scripts you run below**
- `NodePilot-<version>.SHA256SUMS.txt`
- `nodepilot-release-signing.cer` — the public signing certificate

Unpack the scripts next to the artifact — this is where `deploy\Install-NodePilot.ps1` comes from,
and every step after this one runs out of it:

```powershell
Expand-Archive .\NodePilot-Deploy-Scripts-<version>.zip -DestinationPath .
# -> .\deploy\Install-NodePilot.ps1 (+ Update-, Uninstall-, the helpers they need, and templates\)
```

> The scripts ship as their own archive on purpose. They also travel inside the artifact, but only
> under `knowledge\source\` for the AI assistant — and taking them from there would mean extracting
> the **unverified** archive to obtain the very script whose job is to verify it. Verify this small
> zip against `SHA256SUMS`, then let the script it contains verify the artifact.

Verify the download — every file, including the certificate and the script archive, has a line in
`NodePilot-<version>.SHA256SUMS.txt`:

```powershell
# 1. Checksums for everything you downloaded, compared against NodePilot-<version>.SHA256SUMS.txt
Get-ChildItem NodePilot-*, nodepilot-release-signing.cer -File |
    Get-FileHash -Algorithm SHA256 | Format-Table Hash, Path

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

### First run: the SmartScreen prompt

Starting the downloaded installer raises a blue **"Windows protected your PC"** window
(Microsoft Defender SmartScreen), offering only *Don't run* until you expand *More info*. That is
expected, and it is not a sign that anything is wrong with the file.

Two things have to be true for it to appear, and both are true for a downloaded release:

- **The file carries a Mark of the Web.** Anything a browser writes to disk gets an alternate data
  stream marking it as Internet-zone content, and SmartScreen only evaluates files that carry it.
  A locally built installer out of `out\` has no such stream and starts silently — which is why the
  prompt shows up the first time you run a *published* release even though earlier builds of the
  same product never triggered it.
- **The publisher carries no reputation.** NodePilot is signed with a self-signed certificate
  (`CN=NodePilot Release Signing`), so SmartScreen has nothing to weigh and reports an unrecognised
  app.

You can see the mark for yourself:

```powershell
Get-Content -LiteralPath .\NodePilot-Server-Setup-<version>.exe -Stream Zone.Identifier
# ZoneId=3   (3 = Internet)
```

**Verify before you dismiss it, not after.** The prompt is not the trust decision — the checksum
and the publisher thumbprint are:

```powershell
# 1. The file is the one that was published
Get-FileHash .\NodePilot-Server-Setup-<version>.exe -Algorithm SHA256 | Format-List
#    compare against NodePilot-<version>.SHA256SUMS.txt

# 2. It carries the publisher named in the release notes
(Get-AuthenticodeSignature .\NodePilot-Server-Setup-<version>.exe).SignerCertificate.Thumbprint
```

`Get-AuthenticodeSignature` reports `UnknownError` here, and that is the expected result rather
than a failure: the signature itself is intact, but its root is not in any trust store. What is
meaningful is the thumbprint and the checksum. Once both match, choose *More info → Run anyway*.

Clearing the mark instead of clicking through it has the same effect and is easier to script:

```powershell
Unblock-File -Path .\NodePilot-Server-Setup-<version>.exe
```

**The ZIP route needs the same treatment.** `Install-NodePilot.ps1` unpacks a downloaded archive
and runs PowerShell out of it; files extracted from a marked ZIP inherit the mark, which makes
Windows treat those scripts as downloaded content. Unblock the archive before extracting:

```powershell
Unblock-File -Path .\NodePilot-<version>.zip
Expand-Archive -Path .\NodePilot-<version>.zip -DestinationPath .\NodePilot-<version>
```

Antivirus exclusions do not change any of this. SmartScreen is a separate reputation service and
ignores scanner exclusion lists — see [av-exclusions.md](av-exclusions.md). The only thing that
removes the prompt for good is signing with a reputation-carrying certificate; a self-signed
publisher keeps it no matter what is imported into a trust store.

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

Copy these to the target server (e.g. `C:\Temp`):

- `out\NodePilot-<version>.zip`
- `out\NodePilot-<version>.zip.manifest.json`
- `out\NodePilot-<version>.zip.manifest.json.p7s`
- `out\nodepilot-release-signing.cer` — exported by the build from the signer you passed
- `out\NodePilot-Deploy-Scripts-<version>.zip` — or, since you have the checkout, the `deploy\`
  folder directly

`out\NodePilot-<version>.SHA256SUMS.txt` covers everything the run produced, if you want to verify
the transfer.

Whichever way you bring the scripts across, `Install-NodePilot.ps1` needs the helpers it
dot-sources next to it — `ArtifactSecurity.ps1`, `Preflight.ps1`, `ServiceControl.ps1`,
`MachinePath.ps1` — plus `templates\`. The deploy-scripts archive contains exactly that set;
copying the single `.ps1` on its own fails at the first dot-source.

## Step 2 — Install

Once the artifact is verified, the installation itself is documented on the documentation site
rather than repeated here, so there is one walkthrough to keep correct instead of two:

**[Windows Server deployment →](https://sev7enup.github.io/NodePilot/#/en/deployment/production)**

It covers what this guide deliberately no longer does: preparing the service identity
(`LocalSystem` or gMSA), preparing the database (**both** SQL Server and PostgreSQL, including the
SQL Server certificate trap where a CNG key stays invisible to the instance), importing the HTTPS
certificate, running either the GUI setup or the scripts, creating the first admin account,
verifying the result, and upgrading or uninstalling later. Signing in with Active Directory
accounts is on the [AD SSO page](https://sev7enup.github.io/NodePilot/#/en/enterprise/ldap-windows-sso).

The short version, for orientation while you read this page:

```powershell
# GUI: run NodePilot-Server-Setup-<version>.exe as administrator and follow the wizard.
# Scripts: extract NodePilot-Deploy-Scripts-<version>.zip next to the artifact, then
.\deploy\Install-NodePilot.ps1 `
    -ArtifactPath .\NodePilot-<version>.zip `
    -TrustedArtifactSignerThumbprint <thumbprint-from-the-release-notes> `
    -PublicHostname nodepilot.corp.example.com `
    -CertificateThumbprint <kestrel-cert-thumbprint>
```

Every parameter, and what the installer deliberately does *not* do, is in
[deploy/README.md](../deploy/README.md) — which also ships inside the deploy-scripts archive, so it
is available on a machine with no browser.

---

## Troubleshooting

Three files carry the evidence, and the rows below refer to them by name:

| Referred to as | File |
|---|---|
| the log, the Application log, the boot log | `C:\ProgramData\NodePilot\logs\nodepilot-<date>.log` — full diagnostics. Keeps **7 files**, rolling daily and again at 100 MB, so it reaches back fewer than seven days on a talkative system |
| the support log | `C:\ProgramData\NodePilot\logs\nodepilot-support-<date>.log` — a curated extract, 90 files. Also readable in the browser at `/support-log` without a file share |
| the installer log | `%TEMP%\nodepilot-server-setup.log` — a transcript of the install/update run, appended across runs |

Two access notes: `C:\ProgramData\NodePilot` is readable by administrators only, so use an elevated
shell; and `%TEMP%` belongs to the account that *elevated* the installer, which is not necessarily
the one that started it. The full inventory — including what NodePilot deliberately does not log —
is at [Logs & diagnostics](https://sev7enup.github.io/NodePilot/#/en/deployment/logs).

| Symptom | Cause | Fix |
|---|---|---|
| Build: `npm ci failed with exit code N` | real npm failure; commonly an `EPERM` file lock in `node_modules` | close the Vite dev server / editor / AV scan and retry, or `-SkipNpmCi` to reuse warm `node_modules` |
| Install preflight: `No such host is known` | `-SqlServer` / `-PublicHostname` not resolvable | use full FQDNs and verify DNS |
| SQL preflight: SSL handshake error / `The wait operation timed out` | no TLS certificate assigned to SQL Server, or the certificate's key is CNG instead of `KeySpec=KeyExchange`, or the cert isn't trusted on the NodePilot server | redo [preparing the database](https://sev7enup.github.io/NodePilot/#/en/deployment/production) |
| Preflight: `SQL version pre-flight FAILED` — or, on older installer versions, the service boot-loops with TDS **error 8005** (`The parameter name is invalid`) | SQL Server 2022 RTM (or 2019 and older) cannot serve `Encrypt=Strict` | install the latest SQL Server 2022 CU (≥ 16.0.4003.1) |
| Service starts, `/healthz/ready` stays 503, log shows `Login failed for user 'DOMAIN\...$'` | service identity has no SQL login / no DB user | grant it as in [preparing the database](https://sev7enup.github.io/NodePilot/#/en/deployment/production) |
| Install waits out the full 180 s health probe and rolls back; Application log shows `SocketException (10013)` from `AnyIPListenOptions.BindAsync` | Kestrel cannot bind a configured port. **10013 is not "in use"** — Windows returns it for an HTTP.SYS reservation, and on any host running IIS (a ConfigMgr site server, for example) ports 80 and 443 are reserved with no listener to find | set `-HttpPort 0` to drop the redirect, or move the ports. `netsh interface ipv4 show excludedportrange protocol=tcp` lists every reservation. The GUI setup checks this on its Prerequisites page before installing |
| After a reboot the service is still stopped, then comes up on its own | artifacts built before 2026-08-03 registered the service as *Automatic (Delayed Start)*, which idles ~120 s after boot before starting anything | expected on those builds — nothing is broken. Current builds start immediately and wait for the database instead; the boot log names what it is waiting for |
| Boot log repeats `Waiting for the database to accept connections (n/120s)` | the database is not answering yet — a remote SQL Server still recovering, a DC not yet reachable for Kerberos, or a wrong host | let it finish; it proceeds either way and then reports the real connection error. Raise `Database:StartupWaitSeconds` (max 600) if the database routinely needs longer |
| Event log 7000 *the service did not start due to a logon failure*, gMSA identity, only on boot | the service tried to log on before Netlogon could fetch the gMSA password from a DC | current builds set `depend= Netlogon` for gMSA services; on older ones `sc.exe config NodePilot depend= Netlogon` fixes it in place |
| `admin-setup.token` → *Access to the path is denied* | intentional owner-only ACL for the service account | read via `robocopy /B` as shown in [creating the first admin account](https://sev7enup.github.io/NodePilot/#/en/deployment/production) instead of editing the ACL |
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

## Next steps

- **Onboard target machines:** UI → *Machines* → add by FQDN. Start with WinRM over HTTP
  (5985) plus a credential; for WinRM-HTTPS set port 5986 **and** `UseSsl` on the machine
  and trust the target's listener certificate on the NodePilot server.
- **Alerting, AI features, backups:** see `docs/alerting.md`, `docs/ai-features.md` and
  the System-Backup section in `docs/claude-reference.md`.
