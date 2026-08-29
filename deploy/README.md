# NodePilot — Windows Server Deployment

Turnkey installer for NodePilot as a Windows service on a domain-joined Windows Server 2022/2025. TLS is terminated directly by Kestrel (certificate from `LocalMachine\My`), and the database runs on **SQL Server 2022 CU1 or later** (trusted connection, build ≥ `16.0.4003.1` — the runtime connects with `Encrypt=Strict`/TDS 8.0; 2019 cannot speak TDS 8.0, and 2022 RTM has a TDS 8.0 RPC bug, error 8005) or **PostgreSQL 16+** (user/password) — switchable via `-DbProvider`.

> **Step-by-step guide** (lab-validated, including certificate recipes and troubleshooting): [`docs/deployment-guide.md`](../docs/deployment-guide.md).

> **Prefer clicking to typing?** There is a GUI installer for exactly this server installation:
> `NodePilot-Server-Setup-<version>.exe`, see [`server/README.md`](server/README.md). It calls the
> same scripts described here — the ZIP route remains the reference and is still the more direct one
> for automation. What the setup mainly takes off your hands is the trust ceremony (one asset instead
> of five, no manual thumbprint comparison), and it checks the prerequisites before it changes
> anything. `/VERYSILENT /SUPPRESSMSGBOXES /ANSWERFILE=` covers SCCM/GPO.

> **Only one machine, no team access needed?** Then the **desktop app** is considerably faster: an `.exe` installer that brings PostgreSQL and the .NET runtime with it, with no certificate, database or AD preparation — see [`desktop/README.md`](desktop/README.md). In exchange it binds loopback only: **no network access, no inbound webhooks, no SSO, no HA**.

The service runs under one of:

- **LocalSystem** (`-UseLocalSystem`) — no gMSA needed. On the network the service then authenticates as the **computer account** `DOMAIN\<host>$`: SQL Server trusted connections and integrated WinRM use that account. The simplest option for a single server.
- **gMSA** (`-ServiceAccount 'CONTOSO\svc-nodepilot$'`) — a dedicated, AD-managed identity. Least privilege, and the clean choice in HA clusters because all nodes share one identity (with LocalSystem each node has its own computer account, which has to be authorized individually on the SQL Server and on the WinRM targets).

## Files

| File | Purpose |
|---|---|
| [Build-Artifact.ps1](Build-Artifact.ps1) | Builds `out\NodePilot-<version>.zip` from the repository (dotnet publish + PowerShell module staging into `<stage>\Modules` + operator clients into `<stage>\tools\{np,mcp}` + npm build + template) and signs the manifest (detached CMS) |
| [MachinePath.ps1](MachinePath.ps1) | Pure PATH string helpers, shared by install/update/uninstall — idempotently appends `<install>\tools\np` to the machine PATH and removes it again. Tests: `Test-MachinePath.ps1` |
| [Install-NodePilot.ps1](Install-NodePilot.ps1) | The main installer — service, ACLs, firewall, certificate key access |
| [ArtifactSecurity.ps1](ArtifactSecurity.ps1) | Shared signing/verification logic (manifest + `.p7s`); dot-sourced by build/install/update |
| [Preflight.ps1](Preflight.ps1) | Shared **side-effect-free** readiness checks (runtime, certificate, **HTTP/HTTPS ports bindable**, gMSA, database reachability, TDS 8.0 version, service identity, domain membership); dot-sourced by install |
| [ServiceControl.ps1](ServiceControl.ps1) | After stopping the service, waits for the processes in the install directory to exit and terminates stragglers; dot-sourced by install and update |
| [SetupContract.ps1](SetupContract.ps1) | The GUI setup's answer-file contract: schema, splat mapping onto `Install-NodePilot.ps1`, SecureString construction, INI result file |
| [Invoke-NodePilotSetup.ps1](Invoke-NodePilotSetup.ps1) | Adapter between the wizard and the scripts (`InitSession`/`Probe`/`Provision`/`Apply`/`Cleanup`) |
| [Provision-NodePilotDatabase.ps1](Provision-NodePilotDatabase.ps1) | Opt-in: create the SQL login + database. Permission gate **before** any mutation, otherwise DDL output only. SQL Server only |
| [Provision-NodePilotPostgres.ps1](Provision-NodePilotPostgres.ps1) | The same for PostgreSQL: role + database through the bundled `psql`. Needs superuser credentials (Postgres has no `Trusted_Connection`). Does **not** reset the password of an existing role and does **not** change a database owner |
| [New-NodePilotSelfSignedCertificate.ps1](New-NodePilotSelfSignedCertificate.ps1) | Opt-in: self-signed Kestrel certificate, two years, **no** automatic root import |
| [Get-DotnetRuntimePayload.ps1](Get-DotnetRuntimePayload.ps1) | Build time: fetch the ASP.NET Core runtime, verify against the published SHA512 + the checked-in pin + Authenticode |
| [Test-SetupAdapter.ps1](Test-SetupAdapter.ps1) | Behavioural test of the answer-file contract (non-admin, offline, no database) |
| [server/](server/README.md) | GUI installer for the server installation (Inno Setup 6) |
| [desktop/](desktop/README.md) | Desktop app installer (Electron, offline Win 11 x64; everything as boot-start services) — full documentation in the folder README |
| [Test-ArtifactSecurity.ps1](Test-ArtifactSecurity.ps1) | Self-test of the artifact signature chain (tamper detection, signer pinning) |
| [Update-NodePilot.ps1](Update-NodePilot.ps1) | In-place upgrade, preserves appsettings + the SQL database, rolls back on failure |
| [Uninstall-NodePilot.ps1](Uninstall-NodePilot.ps1) | Stops the service, removes binaries, firewall rules and the installation marker. The database is left untouched |
| [Test-Failover.ps1](Test-Failover.ps1) | HA failover smoke test against two nodes (kills the leader, measures RTO until `/healthz/leader` turns green on the standby) |
| [Test-DeploymentTemplates.ps1](Test-DeploymentTemplates.ps1) | Static security/contract check for the HAProxy and appsettings templates |
| [templates/appsettings.Production.json.template](templates/appsettings.Production.json.template) | Production configuration template (single node) |
| [templates/appsettings.Cluster.json.template](templates/appsettings.Cluster.json.template) | Configuration template for active/passive HA (`Cluster:Enabled=true`, `Secrets:Provider=AesGcm`) — see `docs/ha-active-passive.md` |
| [templates/haproxy.cfg.template](templates/haproxy.cfg.template) | Example HAProxy configuration with a `GET /healthz/leader` probe for the HA setup |

**Note: the templates are strict JSON, no comments.** `Test-DeploymentTemplates.ps1` parses them with
`ConvertFrom-Json` (Windows PowerShell 5.1) — a `//` comment breaks the check even though ASP.NET Core
would tolerate it when loading. Explanations belong here or in `docs/`, not in the template file.

### Sizing in the templates

Both templates ship `"Performance": { "ManualTuning": false }` — NodePilot then derives the runspace
pool, the step cap, the ThreadPool floor and the dispatch worker count from the detected CPU and memory. That
is deliberate: `Install-NodePilot.ps1` rolls the production template onto *arbitrary* hardware, and the
numbers stored there are the profile measured for **20 cores / 500 concurrent workflows**. On a smaller
machine they over-provision considerably (768 minimum ThreadPool threads already measured a 28 %
regression on a 20-core box).

The numbers remain as an **inert, immediately activatable preset**: anyone actually running a machine
at that load sets `ManualTuning: true` (configuration or settings UI) and gets exactly the measured
profile. The switch requires a restart. Formulas, limits and measurement evidence:
`docs/performance-improvements.md`.

## Prerequisites (once, by hand, before the first install)

### 1. Server host

- Windows Server 2022 or 2025, domain-joined
- PowerShell ≥ 5.1 (Windows PowerShell) or 7+ (recommended)
- **ASP.NET Core Runtime 10.0.11 or newer in the 10.x line (x64)** — download at <https://dotnet.microsoft.com/download>. The plain runtime is enough (Kestrel hosts itself); the **Hosting Bundle only if IIS is deliberately involved** — it wires up IIS and restarts W3SVC, which is undesirable on shared hosts (SCCM/WSUS). The `(x64)` is not a recommendation: NodePilot is published as `win-x64`, and a 32-bit or older vulnerable 10.x runtime is explicitly rejected by the pre-flight
- The target server can reach the SQL Server on port 1433
- Antivirus exclusions have been agreed with the security team — the list is in [`docs/av-exclusions.md`](../docs/av-exclusions.md)

### 2. Service identity

> **With LocalSystem (`-UseLocalSystem`) only:** this entire section does not apply. No gMSA has to be created or installed — Windows supplies the identity (the computer account) inherently. Continue with section 3 (database); note the computer-account variant of the SQL login there.

#### Group Managed Service Account (gMSA) — for the gMSA path only

On a domain controller (or from a host with RSAT AD PowerShell):

```powershell
# Once per domain:
Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10))

# Security group holding the NodePilot hosts:
New-ADGroup -Name 'NodePilot-Servers' -GroupScope Global -GroupCategory Security
Add-ADGroupMember -Identity 'NodePilot-Servers' -Members (Get-ADComputer -Identity $targetServer)

# Create the gMSA:
New-ADServiceAccount -Name svc-nodepilot `
    -DNSHostName "svc-nodepilot.$((Get-ADDomain).DNSRoot)" `
    -PrincipalsAllowedToRetrieveManagedPassword 'NodePilot-Servers'
```

On the target server:

```powershell
Install-ADServiceAccount -Identity svc-nodepilot
Test-ADServiceAccount -Identity svc-nodepilot    # → True
```

### 3. Database

#### Variant A: SQL Server 2022 CU1 or later (default)

Check the patch level first — the installer aborts in the pre-flight below `16.0.4003.1` (CU1),
because 2022 RTM corrupts `Encrypt=Strict` connections (TDS 8.0) with error 8005:

```sql
SELECT SERVERPROPERTY('ProductVersion') AS Version, SERVERPROPERTY('ProductUpdateLevel') AS CU;
-- 16.0.1000.x = RTM (unpatched) → install a current SQL 2022 CU first.
```

On the SQL Server as `sysadmin`. The Windows login is the service's **network identity**:

- **gMSA path** → the gMSA: `CONTOSO\svc-nodepilot$`
- **LocalSystem path** → the **computer account** of the NodePilot server: `CONTOSO\NPSRV01$` (NetBIOS domain + host name + `$`). With several nodes, create each server individually.

```sql
USE master;
-- gMSA:        CREATE LOGIN [CONTOSO\svc-nodepilot$] FROM WINDOWS;
-- LocalSystem: CREATE LOGIN [CONTOSO\NPSRV01$]       FROM WINDOWS;
CREATE LOGIN [CONTOSO\NPSRV01$] FROM WINDOWS;

CREATE DATABASE NodePilot;
USE NodePilot;
CREATE USER [CONTOSO\NPSRV01$] FOR LOGIN [CONTOSO\NPSRV01$];
ALTER ROLE db_owner ADD MEMBER [CONTOSO\NPSRV01$];
```

`db_owner` is required so the migration bootstrapper can apply the EF migration set on first start.

> **Set `max server memory` if SQL Server shares the machine.** The default is unlimited; on a host
> with little RAM alongside other services this makes queries wait for their memory grant instead of
> computing. Measured in the lab: the workflow list needed 85 ms of CPU and 2,585 logical reads, but
> waited 55 seconds for a 22 MB grant (`RESOURCE_SEMAPHORE`, longest single wait 119 s) — on a 4 GB VM
> next to an SCCM site server. NodePilot now answers that with `503 DATABASE_TIMEOUT` instead of
> hanging, but the cause is in the SQL Server configuration. Check with:
>
> ```sql
> SELECT name, value_in_use FROM sys.configurations WHERE name = 'max server memory (MB)';
> SELECT wait_type, waiting_tasks_count, wait_time_ms, max_wait_time_ms
> FROM sys.dm_os_wait_stats WHERE wait_type = 'RESOURCE_SEMAPHORE';
> ```

> The installer's pre-flight checks SQL reachability using the identity of the **installing
> administrator**, not the service identity. Behind it there is therefore a second check that looks up
> exactly the service identity — the computer account under LocalSystem, otherwise the gMSA: login
> present? user in the target database? `db_owner`? If any of those is missing, the service starts and
> fails `/healthz/ready` (503), and the pre-flight says so beforehand instead of afterwards.
>
> The setup program creates it on request (readiness page; the row comes pre-ticked, unattended via
> `provisioning.createDatabaseAndLogin` in the answer file). On the console path,
> `Provision-NodePilotDatabase.ps1` does the same in one call. Both are existence-checked and do
> nothing at all without `sysadmin` or `CREATE ANY DATABASE` — then the SQL above is left for the DBA.

**RCSI (read-committed snapshot isolation)** is enabled automatically by the installer
(`Enable-SqlReadCommittedSnapshot` in the pre-flight). This is the SQL Server counterpart to Postgres
MVCC: without RCSI, long-running readers (stats refresh, retention sweeps) block every concurrent
`INSERT` into `WorkflowExecutions`/`StepExecutions` under the default 2PL locking. If the installer
step fails the permission check (the login has no `ALTER DATABASE`), it prints the T-SQL statement for
the DBA. Manual activation:

```sql
ALTER DATABASE [NodePilot] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
SELECT name, is_read_committed_snapshot_on FROM sys.databases WHERE name = 'NodePilot';  -- → 1
```

#### Variant B: PostgreSQL 16+

On the Postgres server as superuser:

```sql
CREATE ROLE nodepilot WITH LOGIN PASSWORD '<choose-strong-secret>';
CREATE DATABASE nodepilot OWNER nodepilot;
```

The **console path** only polls TCP reachability (port 5432) — it brings no PG client with it.
Authentication problems (wrong password, a `pg_hba` block) only surface there at the
`/healthz/ready` poll after the service starts, and then with the exact Npgsql error message in the
Serilog rolling file under `C:\ProgramData\NodePilot\logs`.

The **setup program** can do more: it ships `psql`, uses it to sign in as the NodePilot role during
the pre-flight already (`sslmode=verify-full`, i.e. exactly as the runtime does), and creates the role
and database on request. For that it needs superuser credentials — on SQL Server the installer's
Windows identity *is* the authorization, whereas PostgreSQL has nothing comparable. See
[`server/README.md`](server/README.md).

> **Database TLS is mandatory in production.** The `DatabaseTlsBootValidator` aborts the boot if the
> database connection does not verify the server — SQL Server is forced to
> `Encrypt=Strict;TrustServerCertificate=False` (the installer sets `-SqlCertificateHostName` if
> needed), Postgres to `SSL Mode=VerifyFull` against the root CA (PEM) supplied via
> `-PostgresRootCertificate`. The database server therefore has to present a server certificate issued
> by that CA for the connect host name. `Database:AllowInsecureTls=true` is a pure development
> loopback escape and is prohibited in production.

### 4. TLS certificate

Import the certificate (PFX) into the **LocalMachine\My** store on the target server — with its
private key:

```powershell
Import-PfxCertificate -FilePath C:\Certs\nodepilot.pfx `
    -CertStoreLocation Cert:\LocalMachine\My `
    -Password (Read-Host -AsSecureString 'PFX password') `
    -Exportable
```

Note the thumbprint:

```powershell
Get-ChildItem Cert:\LocalMachine\My |
    Where-Object { $_.Subject -like '*nodepilot*' } |
    Select-Object Subject, Thumbprint, NotAfter
```

The installer automatically grants the gMSA read access to the private key file.

**Changing the service identity (LocalSystem ⇄ gMSA):** simply reinstall, the data directory is
preserved. `jwt-secret.key` and `admin-setup.token` are written by the **service** for itself — owner
plus a single ACE belonging to the identity that created them. The installer hands both over to the
new identity; without that the service no longer started after an identity change ("the file, its
owner, or its ACL could not be verified"). Nothing is deleted: the JWT key signs live sessions, and
the setup token is the only way into an instance that has not been provisioned yet. The
data-protection key ring needs no special treatment — it inherits from the directory.

The pre-flight checks the validity period and the name: an **expired or not-yet-valid certificate
aborts the installation** (previously it ran through and was only noticed in the browser). If no SAN —
or, without a SAN, the CN — matches `-PublicHostname`, there is a **warning**, not an abort: behind a
reverse proxy or under an alias that is legitimate.

### AD SSO Preview

LDAP, Windows/Kerberos, OIDC and SCIM are opt-in and remain marked **AD SSO Preview** until the real
AD/Kerberos/LDAPS field test passes. For AD paths, LDAPS with full certificate validation, at least one
permitted AD group SID, a service bind for the directory reconciliation, and a sync interval of at most
five minutes are mandatory. Windows SSO additionally requires an effective host/domain policy that
rejects inbound NTLM.

Windows SSO also needs **two client-side prerequisites** that are easily overlooked:

- **An HTTP SPN on the service identity.** If NodePilot runs under a gMSA or a domain account, the computer account's `HOST/` SPN does **not** cover the service — the ticket is encrypted with the computer account's key and unreadable to the service process. Kerberos then fails and SPNEGO falls back to NTLM **silently**. `setspn -S HTTP/<fqdn> <DOMAIN>\<account>$`, then verify with `setspn -L` and `setspn -X`.
- **A browser policy for the NodePilot origin.** Without `AuthServerAllowlist` (Edge/Chrome) or assignment to the intranet zone, the browser does not present the existing Kerberos ticket automatically and opens a sign-in dialog instead. A correctly configured client **never** asks for credentials; a dialog is a configuration error. GPO settings and a ready-made script as a template: [`docs/ldap-windows-sso.md`](../docs/ldap-windows-sso.md) and [`scripts/ad-sso-labtest/Set-BrowserSsoPolicy.ps1`](../scripts/ad-sso-labtest/Set-BrowserSsoPolicy.ps1).

> Careful during acceptance testing: a credential manager entry or an enterprise SSO product that fills passwords into dialogs makes the sign-in look seamless **even though the browser policy is missing**. Server-side the two are indistinguishable. The "works without input" test is therefore only meaningful on a client without such tooling, with an empty credential store, and after a full browser restart.

Changes under `Authentication` are validated on save but only take full effect after a service restart.
The shipped default profile leaves all external providers disabled and sets local sign-in to
`BreakGlassOnly`. SCIM tokens are rotated without interruption by keeping the old value briefly under
`Authentication:Scim:PreviousBearerToken` and deleting it after the IdP switch.

### 5. Kerberos constrained delegation (for WinRM targets)

> This section concerns the **integrated (credential-less) WinRM path only**. If NodePilot uses credentials stored per machine, no delegation is needed — the service's identity is then irrelevant. Under **LocalSystem** the identity to delegate is the **computer account** of the NodePilot server (`Get-ADComputer <NodePilot-Host>`) instead of the gMSA — otherwise the procedure is identical.

For the gMSA to reach target machines via implicit Kerberos, **resource-based constrained delegation**
has to be configured on every target server:

```powershell
# On a DC:
$gmsa = Get-ADServiceAccount -Identity svc-nodepilot
foreach ($target in $targetHosts) {
    $computer = Get-ADComputer -Identity $target
    Set-ADComputer -Identity $target `
        -PrincipalsAllowedToDelegateToAccount (
            (Get-ADComputer $target).PrincipalsAllowedToDelegateToAccount + $gmsa
        )
}
```

WinRM endpoint on the targets (if not already active):

```powershell
Enable-PSRemoting -Force
winrm quickconfig -transport:https   # for Remote:RequireWinRmSsl=true
```

## Obtaining the artifact

Two routes — in both cases the installer requires a signed artifact and the thumbprint of the
publisher to be trusted.

**Download a published release.** The [latest release](https://github.com/Sev7eNup/NodePilot/releases/latest)
carries `NodePilot-<version>.zip`, `.manifest.json`, `.manifest.json.p7s`,
`NodePilot-Deploy-Scripts-<version>.zip`, `NodePilot-<version>.SHA256SUMS.txt` and the public signing
certificate `nodepilot-release-signing.cer`. **The installer scripts are in the deploy-scripts zip** —
they do travel inside the artifact as well, but only under `knowledge\source\` for the AI assistant,
and taking them from there would mean extracting the *unverified* archive to obtain the very script
whose job is to verify it. Compare the checksums and check the thumbprint against the release notes —
**that comparison is the trust decision**; the installer requires exactly that signer and checks the
code-signing purpose and validity, but **not** whether the machine trusts the publisher. An import into
`Cert:\LocalMachine\Root` is optional and only causes Windows to validate the Authenticode signature of
the installers themselves. Step by step:
[`docs/deployment-guide.md`](../docs/deployment-guide.md), step 1 option A. The same release carries
`NodePilot-Server-Setup-<version>.exe` — it already contains this artifact, in which case this section
does not apply at all.

**Build it yourself.** On a build host with the .NET 10 SDK + Node (versions from `global.json` and the
`engines` fields respectively):

```powershell
git clone <repo> NodePilot
cd NodePilot
$releaseSigner = '0123456789ABCDEF0123456789ABCDEF01234567'   # your own code-signing certificate
.\deploy\Build-Artifact.ps1 -SigningCertificateThumbprint $releaseSigner
# → .\out\NodePilot-<version>.zip + .manifest.json + .p7s + .SHA256SUMS.txt
#   + NodePilot-Deploy-Scripts-<version>.zip + nodepilot-release-signing.cer
```

`-Version` is optional and falls back to the product version from `Directory.Build.props`.
With `-IncludeServerInstaller` the same run additionally produces
`NodePilot-Server-Setup-<version>.exe`, and with `-IncludeDesktopInstaller -PgBinariesPath <pgsql>`
also `NodePilot-Desktop-Setup-<version>.exe` — all under the same version.
`-InstallerSigningCertificateThumbprint <tp>` signs both installers as part of the run; signing them
afterwards would invalidate their entries in `NodePilot-<version>.SHA256SUMS.txt`. If Inno Setup 6 or
the PostgreSQL binaries are missing, only the respective installer step is skipped with a warning —
the server zip is produced regardless.

**The release notes need a paragraph on SmartScreen.** Anyone downloading the installer from the
release page gets "Windows protected your PC" on launch — the file then carries a Mark of the Web, and
the release certificate is self-signed and therefore has no reputation. A locally built installer never
triggers it, which is why the warning goes unnoticed in your own testing and comes as a surprise on
the first real download. Signing does **not** remove it. Boilerplate text and procedure (checksum +
thumbprint before dismissing it, `Unblock-File`, the zip special case):
[docs/deployment-guide.md → First run: the SmartScreen prompt](../docs/deployment-guide.md#first-run-the-smartscreen-prompt).

Copy the zip to the target server.

## Installation

As a local administrator on the target server:

**SQL Server + LocalSystem (no gMSA):**

```powershell
$releaseSigner = '0123456789ABCDEF0123456789ABCDEF01234567'
.\deploy\Install-NodePilot.ps1 `
    -ArtifactPath   C:\Packages\NodePilot-<version>.zip `
    -TrustedArtifactSignerThumbprint $releaseSigner `
    -UseLocalSystem `
    -SqlServer      'sql01.contoso.local' `
    -SqlDatabase    'NodePilot' `
    -CertThumbprint 'A1B2C3D4E5F6...' `
    -PublicHostname 'nodepilot.contoso.local'
```

→ The service runs as `LocalSystem`; on the SQL Server the **computer account** `CONTOSO\<host>$` must exist as a `db_owner` login (see section 3).

**SQL Server + gMSA:**

```powershell
$releaseSigner = '0123456789ABCDEF0123456789ABCDEF01234567'
.\deploy\Install-NodePilot.ps1 `
    -ArtifactPath   C:\Packages\NodePilot-<version>.zip `
    -TrustedArtifactSignerThumbprint $releaseSigner `
    -ServiceAccount 'CONTOSO\svc-nodepilot$' `
    -SqlServer      'sql01.contoso.local' `
    -SqlDatabase    'NodePilot' `
    -CertThumbprint 'A1B2C3D4E5F6...' `
    -PublicHostname 'nodepilot.contoso.local'
```

**PostgreSQL (with a gMSA — for LocalSystem simply replace `-ServiceAccount` with `-UseLocalSystem`):**

```powershell
$releaseSigner = '0123456789ABCDEF0123456789ABCDEF01234567'
$pgPw = Read-Host -Prompt 'Postgres password' -AsSecureString

.\deploy\Install-NodePilot.ps1 `
    -ArtifactPath  C:\Packages\NodePilot-<version>.zip `
    -TrustedArtifactSignerThumbprint $releaseSigner `
    -ServiceAccount 'CONTOSO\svc-nodepilot$' `
    -DbProvider       postgres `
    -PostgresHost     'pg01.contoso.local' `
    -PostgresDatabase 'nodepilot' `
    -PostgresUser     'nodepilot' `
    -PostgresPassword $pgPw `
    -PostgresRootCertificate C:\PKI\postgres-root-ca.pem `
    -CertThumbprint   'A1B2C3D4E5F6...' `
    -PublicHostname   'nodepilot.contoso.local'
```

The installer does everything else:

1. Pre-flight (admin, .NET 10, certificate present with a private key, gMSA available, SQL reachable)
2. Stop and remove the old service (if present)
3. Empty and repopulate `C:\Program Files\NodePilot` (including `wwwroot\docs`, the documentation site the API serves at `/docs`)
4. Create `C:\ProgramData\NodePilot\logs`
5. Generate `appsettings.Production.json` from the template
6. Set ACLs (service identity = the gMSA, or `NT AUTHORITY\SYSTEM` under LocalSystem):
   - InstallPath: service = **ReadAndExecute**, Admins/SYSTEM = Full, inheritance off. The service executes the binaries, it never overwrites them — write access there would be code execution as the service account (H-18). The path is validated beforehand (local, NTFS/ReFS, no reparse points) and re-checked after the copy.
   - DataPath: service = Modify, Admins/SYSTEM = Full, nothing else. Under LocalSystem the SYSTEM Full ACE already covers the service — no additional ACE. **It is verified immediately afterwards**, using the very rule the service is about to apply (`Test-ServiceDirectoryAclTrust` mirrors `RestrictedFileWriter.BuildTrustedSids`): owner trusted, no foreign allow ACE with mutation rights. If that triggers, the installer repairs it once and re-checks; only then does it continue. The reason: an ACE for a service account is only harmless as long as the service runs **under exactly that account** — a leftover from an installation with a different identity would otherwise survive until the first service start and kill it there with "grants mutation rights to an untrusted principal", i.e. only on the rollback path. The check deliberately sits **before** the artifact is extracted, so that giving up costs nothing.
   - `appsettings.Production.json`: service = Read, Admins/SYSTEM = Full (under LocalSystem likewise covered by SYSTEM Full)
   - Certificate private key: gMSA = Read; skipped under LocalSystem (SYSTEM has read on MachineKeys by default)
    - PostgreSQL: `ConnectionStrings:Postgres` stays empty in JSON; the fully quoted connection string
      lives only in the service-scoped `ConnectionStrings__Postgres` environment value. Before it is
      written, the service registry key is restricted to SYSTEM/Administrators.
7. Firewall rule `NodePilot <name> HTTPS` (domain profile)
8. Create the service via `Win32_Service.Create` — gMSA (empty password + `sc.exe managedaccount` + a "Log on as a service" grant) or `LocalSystem` (none of those three steps needed), recovery actions, `ASPNETCORE_ENVIRONMENT=Production`
9. Start the service, poll `https://localhost/healthz/ready`
10. Write the installation marker `HKLM\SOFTWARE\NodePilot\Server` (`InstallPath`, `DataPath`, `ServiceName`, `Version`, `DbProvider`, `HttpsPort`) — only on the success path, so a rolled-back run leaves no marker
11. Print the admin bootstrap token + the legacy external-trigger API key to the console. The key is deny-all at first and only takes effect together with explicit workflow GUIDs under `ExternalTrigger:AllowedWorkflowIds`.

Step 1 comes from [`Preflight.ps1`](Preflight.ps1) and is deliberately a separate file: the checks only
collect (`Invoke-NodePilotPreflight`), and aborting is a second step (`Assert-NodePilotPreflight`). That
lets the same check logic run behind a "check again" button later without changing anything on each
click. **Nothing in `Preflight.ps1` may mutate** — `Test-DeploymentTemplates.ps1` enforces that through
the parsed AST, not through a text search, because the file legitimately contains remediation commands
(`CREATE LOGIN`, `sc.exe`, `New-NetFirewallRule`) as **display text**. The concrete trigger:
`Enable-SqlReadCommittedSnapshot` sat in the same `try` as the SQL reachability check, and its
`ALTER DATABASE … WITH ROLLBACK IMMEDIATE` throws every open session out of the target database. RCSI is
installation work and now runs **after** a passed pre-flight instead of in the middle of it — a run that
is about to abort therefore does not touch the database at all.

After a successful install the console shows:

- URL: `https://<public-hostname>/`
- The **admin setup token** (from `C:\ProgramData\NodePilot\admin-setup.token`) → sign in in the browser: on the first attempt the login page reveals a **"Setup token" field**; paste the token there, sign in again → the admin user is created, the token file is deleted, and the bootstrap window closes. If the installer cannot display the token (the file is restricted by an owner-only ACL to the **service account** — by design not directly readable even for admins), read it with backup semantics rather than touching the ACL: `robocopy C:\ProgramData\NodePilot $env:TEMP admin-setup.token /B`, then `Get-Content "$env:TEMP\admin-setup.token"` (delete the temp copy afterwards). Change the ACL only with care: the server validates the file fail-closed; the trusted set contains only the service account, SYSTEM and the **Administrators group** — `takeown /a` + a group grant survives that, whereas taking ownership as your personal admin user invalidates the file.
- The **legacy external-trigger API key** — save it once, it is not shown again. It authorizes no workflow at first; for new integrations, hashed, GUID-scoped entries under `ExternalTrigger:Keys` are recommended.

### Parameter overview

| Parameter | Required | Default |
|---|---|---|
| `-ArtifactPath` | ✓ | |
| `-TrustedArtifactSignerThumbprint` | ✓ | — (**no** default; there is no built-in pinned publisher, the thumbprint always has to be supplied) |
| `-ServiceAccount` | ✓ on the gMSA path (not used with `-UseLocalSystem`) | |
| `-UseLocalSystem` | Alternative to `-ServiceAccount` | off |
| `-CertThumbprint` | ✓ | |
| `-DbProvider` | | `sqlserver` (alternative: `postgres`) |
| `-SqlServer` | ✓ (only with `sqlserver`) | |
| `-SqlDatabase` | | `NodePilot` |
| `-SqlCertificateHostName` | | Host part of `-SqlServer` |
| `-PostgresHost` | ✓ (only with `postgres`) | |
| `-PostgresPort` | | `5432` |
| `-PostgresDatabase` | | `nodepilot` |
| `-PostgresUser` | ✓ (only with `postgres`) | |
| `-PostgresPassword` | ✓ (only with `postgres`, SecureString) | |
| `-PostgresRootCertificate` | ✓ (only with `postgres`, PEM) | |
| `-PublicHostname` | | Machine FQDN |
| `-HttpsPort` | | `443` |
| `-HttpPort` | | `80` (0 = no HTTP binding) |
| `-InstallPath` | | `C:\Program Files\NodePilot`. Must be a local, absolute path on **NTFS or ReFS**, without a junction or symlink anywhere in the path (UNC and FAT/exFAT are rejected). The installer applies a protected ACL to it — SYSTEM + Administrators `FullControl`, the service account only `ReadAndExecute` — and verifies after the copy that no other principal has write access. The reason: the binaries there are executed by the service as LocalSystem/gMSA, and a different path would otherwise inherit e.g. `BUILTIN\Users:(M)` from the volume root (H-18) |
| `-DataPath` | | `C:\ProgramData\NodePilot` |
| `-ServiceName` | | `NodePilot` |
| `-ServiceDisplayName` | | `NodePilot Orchestrator` |
| `-ExternalTriggerApiKey` | | Auto-generated legacy key (48 bytes, Base64); deny-all at first with an empty `AllowedWorkflowIds` list |
| `-JwtIssuer` | | `nodepilot:prod:<machine>` |
| `-JwtAudience` | | `nodepilot:prod:<machine>` |
| `-AllowedHosts` | | PublicHostname. `localhost` is always appended — the installer's health probe goes to `https://localhost:<port>/healthz/ready`, and `UseHostFiltering` would otherwise reject it with 400 and roll back a finished installation |
| `-KnownProxyIps` | | Empty (only loopback is trusted); with HAProxy, list every direct transport IP |
| `-SkipSqlConnectivityCheck` | | off |
| `-SkipGmsaCheck` | | off |
| `-BootstrapAdminUsername` | | Empty. When set, **only** this account may redeem the one-time setup token (`NodePilot:BootstrapAdminUsername`). For unattended rollouts that know the name in advance. |
| `-SeedBackupPath` | | Empty. A `.npbackup` applied on **first start** into an empty instance — users, workflows, machines, credentials, settings. The file is copied to `<DataPath>\seed.npbackup` and deleted after being applied. |
| `-SeedBackupPassphrase` | ✓ if `-SeedBackupPath` is set (SecureString) | Ends up in the `Environment` value of the service key, **never** in `appsettings.Production.json` |

> **Turnkey without typing a token.** `-BootstrapAdminUsername` and `-SeedBackupPath` are the two ways
> to end an unattended installation in a usable state — otherwise someone would have to type the setup
> token into the sign-in form by hand. The seed wins: if it brings users with it, no token is issued at
> all. A wrong seed makes the service **not** start, instead of leaving behind an apparently
> provisioned but actually empty instance. In full in
> [`server/README.md`](server/README.md#turnkey-rollout-unattended-without-typing-a-token).

## HAProxy in front of NodePilot

For active/passive HA and Windows Negotiate, use the supplied
[`haproxy.cfg.template`](templates/haproxy.cfg.template). Before deployment, all `{{...}}` placeholders
have to be replaced, in particular:

| Placeholder | Meaning |
|---|---|
| `TLS_CERT_PATH` | PEM containing the public HAProxy certificate and private key |
| `BACKEND_CA_FILE` | PEM CA chain HAProxy uses to validate the Kestrel certificates |
| `BACKEND_TLS_SERVER_NAME` | Shared SAN/SNI name on both backend certificates and in NodePilot's `AllowedHosts` (usually the public service host name) |
| `NODE_A_IP`, `NODE_B_IP` | Direct backend addresses; no unvalidated host names |

The backend connection stays persistent and exclusive per client session for Negotiate
(`option http-keep-alive`, `http-reuse never`). Backend TLS is fail-closed; `verify none` is not
supported. Forwarded headers injected by clients are deleted and re-set by HAProxy.

In return, NodePilot must trust only the direct HAProxy senders:

```powershell
.\deploy\Install-NodePilot.ps1 `
    <further parameters> `
    -KnownProxyIps '10.0.1.5','10.0.1.6'
```

Do not enter the public VIP, and do not enter a private network wholesale. Without an explicit proxy
IP, NodePilot safely ignores forwarded headers; rate limiters then only see the HAProxy IP. After
rendering, run both checks:

```powershell
.\deploy\Test-DeploymentTemplates.ps1
```

```bash
haproxy -c -f /etc/haproxy/haproxy.cfg
```

## Update

For a new artifact:

```powershell
$releaseSigner = '0123456789ABCDEF0123456789ABCDEF01234567'
.\deploy\Update-NodePilot.ps1 `
    -ArtifactPath C:\Packages\NodePilot-<version>.zip `
    -TrustedArtifactSignerThumbprint $releaseSigner
```

Preserves `appsettings.Production.json`, the database (SQL Server or Postgres) and the service account.
The backup under `C:\Program Files\NodePilot.backup.<timestamp>` contains binaries only and never the
secret-bearing production configuration; automatic rollback on a health-check failure.

- **`-HttpsPort` does not have to be repeated**: the health probe takes `Kestrel:Https:HttpsPort` from the installed configuration (an explicit `-HttpsPort` still wins). Without that derivation, updating an 8443 installation probed against 443 and rolled back a healthy upgrade.
- **Process guard before the swap:** a stopped service is not enough — an orphaned worker keeps its DLLs image-mapped, and Windows reports that as a plain "Access denied" in the middle of the wipe. But the SCM reports `SERVICE_STOPPED` **before** the process has actually exited (host shutdown, log flush). After the stop it therefore waits up to 30 s, then terminates the remaining processes from the install directory — they are NodePilot binaries whose files are about to be replaced anyway. Only if that does not help either does the updater abort **before the first deletion**, naming the PID and process. Until 2026-08-03 the wait was missing and the run failed on exactly the process it had stopped itself.
- During the swap, `appsettings.Production.json` deliberately goes **last**, so that an abort does not take the configuration down with it (by design it is not in the backup). If it is missing after all, the updater refuses — then run `Install-NodePilot.ps1`, which re-renders the configuration from its parameters (the database, DataPath and accounts are preserved; the newly generated legacy external-trigger key stays deny-all until GUIDs are permitted again).
- **A successful update leaves the service RUNNING**, whether or not it was stopped beforehand. Only a failed update restores the original state (a rollback does not start anything that was deliberately stopped before).

## Uninstall

```powershell
.\deploy\Uninstall-NodePilot.ps1              # logs + configuration preserved
.\deploy\Uninstall-NodePilot.ps1 -PurgeData   # everything except the database removed
```

**The SQL database is never deleted automatically** — drop it with DBA tooling if required.

The script additionally removes the installation marker `HKLM\SOFTWARE\NodePilot\Server` and — if
`sc.exe delete` only marked the service key `DELETE_PENDING` because of an open SCM handle — its
`Environment` value, which holds the Postgres connection string **including the password**.

**The uninstaller waits before deleting the service.** The SCM reports `Stopped` as soon as the service
acknowledges the control code; the process keeps running afterwards while ASP.NET Core drains (measured
in the lab: 31 seconds). If the service is deleted in that window, the process is **orphaned** — no
longer reachable through the SCM — and the subsequent deletion pulls the DLLs out from under a running
application. The script therefore waits up to `-ProcessExitTimeoutSeconds` (default 90) for processes
from `InstallPath` and otherwise aborts **with the service still registered**, so that a supported way
to stop it remains. If files are left behind afterwards (virus scanner, search indexer), the script
still runs to completion, names the leftovers together with the holding process, and exits with code 1.

Two things are **deliberately** left in place, because both can be shared with something else on the
host and blindly revoking them would break exactly that: the gMSA's "Log on as a service" right and the
read ACE on the TLS certificate's private key. The script **names** both explicitly at the end of its
run rather than leaving them silently — as it does the database, so that nobody has to guess after the
uninstall whether it is still there.

**`-PurgeData` takes ownership first.** The installer writes `jwt-secret.key` and `admin-setup.token`
owner-only to the *service account*; otherwise not even an administrator can delete them and the run
aborts halfway. The script therefore calls `takeown` and `icacls` with the well-known SID
`S-1-5-32-544` — **without** `(OI)(CI)`: those are container inheritance flags that are silently
discarded on a leaf file while `icacls` still reports success.

## Backup & disaster recovery

NodePilot ships a **system configuration backup** (admin only, UI under `/backup` or CLI `np backup`).
It backs up the *configuration* portably and passphrase-encrypted: workflows, folders/shares, machines,
credentials, global variables, users and runtime settings.

> **Not a substitute for a database backup.** Execution history, audit log and statistics are **not**
> included. For full protection, keep running the database's own backup (Postgres `pg_dump` / SQL Server
> backup). The configuration backup is for "rebuild the instance / move it to another host".

**Properties.** The `.npbackup` file is encrypted with a **passphrase** (PBKDF2→AES-GCM) and protected
against tampering with a whole-file MAC. **Without the passphrase the backup is unrecoverable** — keep
it safely and separately from the file. Restore is portable (different host, different secret provider
DPAPI↔AES-GCM), because secrets are re-encrypted with the target provider during the restore.

**Scheduled DR backup (headless, e.g. a scheduled task).** Never pass the passphrase as an argument —
use an environment variable or a file:

```powershell
$env:NP_BACKUP_PASS = '<from-secret-store>'
np backup export --out "D:\backups\nodepilot-$(Get-Date -Format yyyyMMdd).npbackup" --passphrase-env NP_BACKUP_PASS
# Limit the sections: --sections workflows,credentials,machines
```

**Restoring onto a fresh instance.**

```powershell
np backup preview  .\nodepilot-20260529.npbackup --passphrase-env NP_BACKUP_PASS    # dry run: what would land where
np backup restore  .\nodepilot-20260529.npbackup --passphrase-env NP_BACKUP_PASS --yes
# Conflict policy when restoring into an existing database: --policy skip|rename|overwrite, or section=policy
```

The restore runs transactionally in dependency order, validates references up front (aborting on ones
it cannot resolve), protects the last active admin, and invalidates existing sessions when a user is
overwritten. Settings are written last (a service restart may be needed for them to take effect).

## Troubleshooting

| Symptom | Check |
|---|---|
| Service starts and stops immediately | Event Viewer → Windows Logs → Application, source `<ServiceName>`. Usually a configuration or ACL problem. |
| Update aborts with "Access to the path '…\*.dll' is denied" | A process is still running from the install directory (DLLs image-mapped), despite the service being stopped. `tasklist /m <dll>` names the holder; `Get-Process \| Where-Object { $_.Path -like 'C:\Program Files\NodePilot\*' } \| Stop-Process -Force`, then retry. Current builds abort with the PID before anything is deleted. |
| "Processes are still running from … and could not be ended" | A process from the install directory survived the 30 s grace period **and** could not be terminated — in practice always missing permissions or a hung kernel call. The PID and name are in the message; the service and the files are untouched. Terminate the process or reboot, then retry. |
| Browser shows `{"message":"Token is no longer valid"}` instead of the app | The session cookie has exceeded the absolute lifetime (`Authentication:SessionAbsoluteLifetimeHours`, 8 h). Artifacts before 2026-08-02 answered SPA navigations including `/login` with that too. Clear the site's cookies; permanently: deploy a current artifact. |
| Certificate not found / no private key | `Get-ChildItem Cert:\LocalMachine\My\<thumb>` — must show HasPrivateKey=True. On re-import use `-KeyStorageFlags MachineKeySet,PersistKeySet,Exportable`. |
| Service stops right after a certificate swap | The service account has no read ACE on the new key file. The installer grants it (`Grant-CertPrivateKeyAccess`), a manual swap does not. Steps: [Replacing the HTTPS certificate](../src/nodepilot-docs-ui/content/en/deployment/production.md#replacing-the-https-certificate). Under `LocalSystem` this cannot be the cause — SYSTEM already reads `MachineKeys`. |
| 503 on `/healthz/ready` | The database is not ready. State and reason are available without signing in at `/healthz/database` (always HTTP 200) or via `np health`. On SQL Server also check as the gMSA with `sqlcmd -S sql01 -E -d NodePilot -Q "SELECT 1"`. |
| Database outage **during operation** | Expected: the service stays up, `/api` answers `503 DATABASE_UNAVAILABLE`, the UI shows the outage, and NodePilot resumes automatically after successful probes. Workflows wait at the durability boundary; triggers observed during the outage are counted and discarded without catch-up. Details: [ADR 0011](../docs/adr/0011-database-availability-breaker.md). |
| `/healthz/database`: `RejectedByServer` | Fix the credentials, the database/catalog or TLS; waiting or a restart alone does not clear this state. After a server-side correction the probe picks it up automatically; changed NodePilot connection details need a service restart. |
| WinRM remote calls fail with "Access denied" | The gMSA has no Kerberos delegation to the target. See section 5 (resource-based constrained delegation). |
| SPA loads, API calls return 401 | Sign-in via the setup token has not happened yet — the login page reveals the field on the first attempt; read the token via `robocopy <DataPath> $env:TEMP admin-setup.token /B` (the file is owner-only for the service account). |
| Service boot loop, log shows TDS error 8005 "The parameter name is invalid" | SQL Server 2022 RTM without a CU — the TDS 8.0 RPC bug, fixed from CU1 (`16.0.4003.1`). Install a current 2022 CU. Newer installers catch this in the pre-flight. |
| DPAPI decrypt failed for credentials after a service-account change | The template sets `Credentials:DpapiScope=LocalMachine`. For existing `CurrentUser`-encrypted credentials: re-enter the credentials (there is no migration helper). |
| After an update the service does not see the configuration | ACL on `appsettings.Production.json` — the update script re-applies Read for the current service account. After manual intervention: `icacls "<Install>\appsettings.Production.json" /grant "<gMSA$>:(R)"`. |
| Port 443 already in use | `Get-NetTCPConnection -LocalPort 443` shows the PID. Often the IIS default site or the WinRM HTTPS listener. The installer binds through a Kestrel socket, not http.sys — but a conflict is still a conflict. |
| A ticket was raised, where do I look? | The **support log** — two sub-sinks from the same filter: (1) the plain-text file `C:\ProgramData\NodePilot\logs\nodepilot-support-*.log` (90-day retention) for RDP/tail diagnosis, (2) the structured database table `SupportEvents` (90-day retention via `Retention:SupportEvents`) for the web viewer with filtering, cursor and export. In the browser: Admin settings → "Support log" tab → toggle "Table (DB) \| Plain text (file)". Full diagnostics remain in `nodepilot-*.log` alongside. |
| Where is the manual on a machine with no internet? | The installation serves it at `https://<host>/docs` — the same site as sev7enup.github.io, staged into `wwwroot\docs`, at the version actually installed. No login required, and it stays readable during a database outage. It is gone only if the service itself will not start. |

## What the installer does NOT do

- It does not install the ASP.NET Core runtime — that must be present beforehand. **Exception:** the
  GUI setup (`server/`) ships the official Microsoft runtime installer and offers it on the readiness
  page; the ZIP route described here does not.
- **It does not delete the database on uninstall — not even optionally.** It was provisioned
  separately, often has its own backup and replication regime, and in an active/passive cluster both
  nodes share it. What the installer never created, it does not remove.
- It does not create the gMSA, the SQL login or the Kerberos delegation — an AD/DBA task.
- It does not register the NodePilot HTTP SPN, apply an NTLM blocking policy or set a **browser intranet policy** (`AuthServerAllowlist` / site-to-zone) — all three have to be rolled out and verified by the AD/security team before Windows SSO is enabled. Without the browser policy the sign-in works, but prompts every user for credentials instead of running silently on a ticket.
- It does not configure an OIDC IdP or a SCIM client — redirect URI, claims, group allow-list and the provisioning bearer token remain an IdP/IAM task.
- It does not back up the SQL database — set that up separately via SQL Agent/Ola Hallengren/etc.
- It does not integrate log forwarding or monitoring — logs land under `C:\ProgramData\NodePilot\logs`, collection via Winlogbeat/OTel collector/etc. is your choice.
- It does not migrate data across providers between SQL Server and Postgres. If needed: export/import via `GET /api/workflows/export` → `POST /api/workflows/import`.
- It does not set antivirus exclusions. The service starts PowerShell child processes and runs generated scripts out of `%TEMP%` — without appropriate exceptions, endpoint security blocks individual steps or the install-directory swap during an update. A hand-off-ready list including residual risks: [`docs/av-exclusions.md`](../docs/av-exclusions.md).
