# Windows Server deployment scripts

Install NodePilot as a Windows service with Kestrel HTTPS and an external database.
Use the [server wizard](server/README.md) for interactive or answer-file installation,
or the scripts below for automation. For a single local machine, see the
[desktop installer](desktop/README.md).

- [Installation walkthrough](../src/nodepilot-docs-ui/content/en/deployment/production.md): service identity, database, certificates and first login.
- [Artifact verification and local builds](../docs/deployment-guide.md).
- [Troubleshooting](../docs/deployment-troubleshooting.md): logs, startup failures, permissions and recovery.

## Prerequisites

| Requirement | Details |
|---|---|
| Host | Windows Server 2022/2025, PowerShell 5.1+, administrator rights. Domain membership is required for gMSA. |
| Runtime | Both x64 .NET and ASP.NET Core runtimes, 10.0.11+ within the 10.x line, satisfying the artifact's declared versions. Use standalone runtimes; the Hosting Bundle changes IIS. The GUI setup can install its bundled runtimes. |
| Identity | An installed gMSA, or `-UseLocalSystem`. For network access, LocalSystem uses `DOMAIN\HOST$`; same-host SQL Server sees `NT AUTHORITY\SYSTEM`. Grant database and target-machine access to the actual identity. |
| Database | SQL Server 2022 CU1+ (build `16.0.4003.1`) with a login, database user and `db_owner`; or PostgreSQL 16+ with a role and owned database. |
| HTTPS | Certificate and private key in `Cert:\LocalMachine\My`, covering the public hostname and trusted by clients. The installer grants the service access to the key. |

Database TLS is required: SQL Server uses `Encrypt=Strict;TrustServerCertificate=False`;
PostgreSQL uses `SSL Mode=VerifyFull` with `-PostgresRootCertificate`. Follow the
[database certificate guide](../src/nodepilot-docs-ui/content/en/deployment/database-tls.md).
On SQL Server, setup enables read-committed snapshot isolation when permitted; otherwise
it prints the statement for the DBA. The console PostgreSQL pre-flight checks reachability;
authentication failures appear during the readiness check. The wizard also checks with `psql`.

An expired, not-yet-valid or hostname-mismatched **HTTPS** certificate produces a
pre-flight warning; clients may still reject it. Publisher-signature validation is a
separate check and rejects an unacceptable artifact.

Prepare WinRM access and review [antivirus guidance](../docs/av-exclusions.md) before rollout.
The integrated (credential-less) WinRM path authenticates as the NodePilot process identity:
the gMSA, or the host computer account under LocalSystem. Grant that identity permission on
each target's WinRM endpoint; this first hop needs no constrained delegation.
For Windows SSO, configure the service's HTTP SPN, browser intranet policy and NTLM denial
as described in the [authentication guide](../docs/ldap-windows-sso.md).

## Install

Obtain the artifact, adjacent manifest/signature files and the separate deployment-scripts ZIP
from a [release](https://github.com/Sev7eNup/NodePilot/releases/latest).
Verify `NodePilot-<version>.SHA256SUMS.txt` and the publisher thumbprint using the
[verification guide](../docs/deployment-guide.md#verify-a-release). The installer requires
that trusted thumbprint and refuses unsigned or tampered artifacts. Retrieve scripts from
the separate scripts archive, without extracting the unverified application ZIP.

Run examples in **elevated PowerShell from the repository root**. Adjust script paths when
using the separate deployment-scripts archive; replace example hosts, paths and thumbprints.

```powershell
.\deploy\Install-NodePilot.ps1 `
    -ArtifactPath 'C:\Packages\NodePilot-<version>.zip' `
    -TrustedArtifactSignerThumbprint '<publisher thumbprint from release notes>' `
    -UseLocalSystem `
    -SqlServer 'sql01.contoso.local' `
    -SqlDatabase 'NodePilot' `
    -CertThumbprint '<HTTPS certificate thumbprint>' `
    -PublicHostname 'nodepilot.contoso.local'
```

For gMSA, replace `-UseLocalSystem` with `-ServiceAccount 'CONTOSO\svc-nodepilot$'`.
For PostgreSQL, use `-DbProvider postgres` and the parameters below; supply the password
as a `SecureString` from `Read-Host -AsSecureString`. Complete examples are in the
[walkthrough](../src/nodepilot-docs-ui/content/en/deployment/production.md#variant-b-the-scripts).

Setup checks prerequisites, installs the files, configures permissions and a domain-profile
HTTPS firewall rule, registers the service and checks `/healthz/ready`. It prints the URL
and first-login token. Re-running installation preserves the data directory.

### Parameter overview

| Parameter | Required / default |
|---|---|
| `-ArtifactPath` | Required: signed application ZIP. |
| `-TrustedArtifactSignerThumbprint` | Required: expected publisher; no built-in default. |
| `-UseLocalSystem` / `-ServiceAccount` | Choose one. The latter takes a gMSA name including `$`. |
| `-CertThumbprint` | Required: HTTPS certificate in `LocalMachine\My`. |
| `-DbProvider` | `sqlserver` or `postgres`; default `sqlserver`. |
| `-SqlServer` | Required for SQL Server. |
| `-SqlDatabase` | `NodePilot`. |
| `-SqlCertificateHostName` | Host portion of `-SqlServer`. |
| `-PostgresHost` / `-PostgresUser` | Required for PostgreSQL. |
| `-PostgresPort` / `-PostgresDatabase` | `5432` / `nodepilot`. |
| `-PostgresPassword` | Required for PostgreSQL; `SecureString`. |
| `-PostgresRootCertificate` | Required for PostgreSQL; trusted root PEM. |
| `-PublicHostname` | Machine FQDN. |
| `-HttpsPort` / `-HttpPort` | `443` / `80`; HTTP `0` disables the redirect listener. |
| `-InstallPath` | `C:\Program Files\NodePilot`; local absolute path on NTFS/ReFS, with no junctions or symlinks. |
| `-DataPath` | `C:\ProgramData\NodePilot`. |
| `-ServiceName` / `-ServiceDisplayName` | `NodePilot` / `NodePilot Orchestrator`. |
| `-ExternalTriggerApiKey` | Generated legacy key; authorizes nothing until workflow GUIDs are allowed. Prefer scoped `ExternalTrigger:Keys` for new integrations. |
| `-JwtIssuer` / `-JwtAudience` | `nodepilot:prod:<machine>`. |
| `-AllowedHosts` | Public hostname, short computer name and `localhost`. Explicit lists are retained with `localhost` appended for health checks. |
| `-KnownProxyIps` | Empty; only loopback trusted. Specify direct proxy transport IPs. |
| `-SkipSqlConnectivityCheck` / `-SkipGmsaCheck` | Off; skip the respective pre-flight checks. |
| `-BootstrapAdminUsername` | Empty; when set, limits who may redeem the setup token. |
| `-SeedBackupPath` | Optional `.npbackup` applied on first start into an empty instance. |
| `-SeedBackupPassphrase` | Required with a seed; `SecureString`, stored in the protected service environment. |
| `-SuppressSecretOutput` | Off; suppresses the setup token and legacy trigger key in console output. |
| `-OmitSourceSnapshot` | Off; omits the optional `knowledge\source` snapshot after signature verification. |

A seed containing users removes the need for first-run bootstrap. An invalid seed stops
startup. See [turnkey rollout](server/README.md#turnkey-rollout) for unattended setup.

## First access and protected files

Open the reported URL. On the first sign-in attempt, the form reveals the setup-token
field. Redeeming the token creates the first administrator and deletes the token file.
There is no default account or password.

If the console cannot display the owner-protected token, use backup semantics in an
elevated shell, read the copy and delete it immediately:

```powershell
robocopy 'C:\ProgramData\NodePilot' "$env:TEMP" admin-setup.token /B
Get-Content "$env:TEMP\admin-setup.token"
Remove-Item -LiteralPath "$env:TEMP\admin-setup.token"
```

Use the configured DataPath if it differs. Do not loosen the source token's ACL or take
ownership as a personal administrator account; bootstrap validates its owner and permissions.

| Location | Contents and protection |
|---|---|
| InstallPath | Binaries, configuration, `tools\np`, `tools\mcp`, offline docs in `wwwroot\docs`. Service: ReadAndExecute; Administrators/SYSTEM: FullControl. |
| DataPath | Logs, JWT key, setup token and Data Protection keys. Restricted to the service and Administrators/SYSTEM; token/key files have stricter ownership. |
| Service registry environment | PostgreSQL connection string and seed passphrase. Restricted to Administrators/SYSTEM; the DB password is not written to production JSON. |

Reinstall to change LocalSystem/gMSA identity; setup transfers protected JWT/bootstrap
files. Existing `CurrentUser`-encrypted DPAPI credentials may need re-entry; see
[identity troubleshooting](../docs/deployment-troubleshooting.md#certificates-identity-and-file-permissions).

## Reverse proxies and HA

Use the [HAProxy template](templates/haproxy.cfg.template) and
[HA guide](../docs/ha-active-passive.md#haproxy-example). Replace every placeholder:
`TLS_CERT_PATH`, `BACKEND_CA_FILE`, `BACKEND_TLS_SERVER_NAME`, `NODE_A_IP` and `NODE_B_IP`.
Backend TLS must validate the CA and shared SAN/SNI name; include that name in `AllowedHosts`.

`-KnownProxyIps` takes each direct proxy transport IP, not the public VIP or a whole subnet.
Untrusted forwarded headers are ignored. For Negotiate, retain persistent, per-client
HTTP/1.1 connections and `http-reuse never`; never share an authenticated backend connection
between clients. The template removes and regenerates forwarded headers.
Validate the rendered file with `haproxy -c -f /etc/haproxy/haproxy.cfg` before use.

## Updates and removal

Back up the database and production configuration before updating. In elevated PowerShell:

```powershell
.\deploy\Update-NodePilot.ps1 `
    -ArtifactPath 'C:\Packages\NodePilot-<version>.zip' `
    -TrustedArtifactSignerThumbprint '<publisher thumbprint from release notes>'
```

- Update preserves configuration, database and service identity. It stops runtime processes
  before replacing files and rolls binaries back when the health check fails.
- **Rollback restores binaries only.** It does not undo schema migrations or back up the
  secret-bearing production configuration. Restore the database backup if the old binary
  cannot use the migrated schema. Insecure or modified binary backups are rejected.
- A successful update starts the service; a failed update restores its previous running state.
- The health probe reads the installed HTTPS port unless `-HttpsPort` overrides it, and
  accepts only the certificate named by `Kestrel:Https:CertificateThumbprint`.
  Supply custom `-InstallPath`, `-DataPath` and `-ServiceName` where applicable.
- An installation made with the GUI setup keeps its uninstaller: the updater restores the
  setup's `unins*` files and `<InstallPath>\deploy\` from its backup after a hash check.
- Downgrades require `-AllowDowngrade`; this does not downgrade the database schema.
  A missing/unreadable installed version produces a warning and allows the update to continue.
- Binary backups are stored beside InstallPath; `-KeepBackupCount` defaults to `3`.
  Missing configuration or locked files require the [recovery guide](../docs/deployment-troubleshooting.md#updates-rollback-and-removal).

Uninstall from an elevated shell; adjust paths and service name for custom installations:

```powershell
.\deploy\Uninstall-NodePilot.ps1              # keep runtime settings, keys and logs; appsettings.Production.json is removed
.\deploy\Uninstall-NodePilot.ps1 -PurgeData   # also delete the data directory
```

**The external database is always retained.** Uninstall removes the service, binaries,
installation marker, service environment, firewall rules and installation backups.
It waits for process exit (`-ProcessExitTimeoutSeconds`, default `90`) before deleting the
service and returns exit code `1` if removal is incomplete. The gMSA's service-logon right
and certificate private-key read grant remain because other applications may share them.

**A failed uninstall can be repeated.** The installation marker and `<InstallPath>\deploy\`
are removed last. Any non-zero exit makes the GUI and `/SILENT` uninstaller stop before it
removes a file or the "Apps & features" entry; fix the cause named in
`%TEMP%\nodepilot-server-uninstall.log` and uninstall again. With `-PurgeData`, nothing under
the data directory is deleted if taking ownership of any item fails.

## Backup and operational responsibilities

Use the encrypted [system configuration backup](../docs/adr/0001-system-configuration-backup-restore.md)
to move or rebuild an instance. It includes configuration and secrets, **not execution history,
audit history or statistics**. Keep its passphrase separately; without it, recovery is impossible.
Maintain native database backups for full recovery. CLI recipes: [backup commands](../src/nodepilot-docs-ui/content/en/cli.md).

AD identities/delegation, database provisioning, IdP/SCIM configuration, certificate renewal,
monitoring, log forwarding and antivirus policy remain operator responsibilities. The wizard
can provision database accounts/databases when explicitly requested with sufficient rights.
Uninstall never deletes the database. The production template uses automatic hardware sizing;
manual tuning is described in the [performance guide](../docs/performance-improvements.md).

## Script map

| File | Purpose |
|---|---|
| [Build-Artifact.ps1](Build-Artifact.ps1) | Publish, package and sign. Release procedure: [RELEASING.md](../RELEASING.md). |
| [Install-NodePilot.ps1](Install-NodePilot.ps1) / [Update-NodePilot.ps1](Update-NodePilot.ps1) / [Uninstall-NodePilot.ps1](Uninstall-NodePilot.ps1) | Service lifecycle. |
| [ArtifactSecurity.ps1](ArtifactSecurity.ps1) / [Preflight.ps1](Preflight.ps1) / [ServiceControl.ps1](ServiceControl.ps1) | Signature checks, read-only readiness checks and process shutdown. |
| [Invoke-NodePilotSetup.ps1](Invoke-NodePilotSetup.ps1) / [SetupContract.ps1](SetupContract.ps1) | Wizard adapter and answer-file contract. |
| [Provision-NodePilotDatabase.ps1](Provision-NodePilotDatabase.ps1) / [Provision-NodePilotPostgres.ps1](Provision-NodePilotPostgres.ps1) | Opt-in database provisioning. |
| [New-NodePilotSelfSignedCertificate.ps1](New-NodePilotSelfSignedCertificate.ps1) | Lab HTTPS certificate; no automatic root trust. |
| [Production template](templates/appsettings.Production.json.template) / [cluster template](templates/appsettings.Cluster.json.template) | Service configuration. |
| [Test-DeploymentTemplates.ps1](Test-DeploymentTemplates.ps1) | Deployment contract checks. Release testing: [lab matrix](../scripts/release-lab/README.md). |

Public website publishing is separate: [Publish-Site.ps1](Publish-Site.ps1),
[website build and preview](../src/nodepilot-docs-ui/README.md).
