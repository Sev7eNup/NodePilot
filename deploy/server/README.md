# NodePilot Server Setup

The server installer packages a signed artifact and the Microsoft runtime installers.
It supports an interactive wizard and unattended deployment through an answer file.
The [production walkthrough](../../src/nodepilot-docs-ui/content/en/deployment/production.md)
covers identity, database and certificate preparation.

The installer also adds a Start-menu shortcut for the [NodePilot Switcher](../../docs/switcher.md).
It controls the validated local NodePilot service and the SCOrch services `omanagement`,
`oremoting`, `omonitor` and `orunbook`. It never controls database services.

## Quick start

1. Download the setup from the release and verify its checksum and publisher thumbprint
   using the [deployment guide](../../docs/deployment-guide.md).
2. Run it as administrator. Choose the service identity, database, public hostname,
   ports and HTTPS certificate.
3. Review readiness checks and explicitly select any prerequisite actions. Setup can
   install the bundled runtimes, prepare an existing gMSA locally, create database
   access and import or generate a certificate when requested and authorized.
4. Complete installation and retain the finish summary securely. It can contain the
   first-login token and external-trigger key. Saved summaries also contain these secrets.

On an IIS host, ports 80/443 may be reserved even without a visible listener.
Choose available ports, for example HTTPS 8443 and HTTP 0. An empty certificate
thumbprint requires requested PFX import or self-signed generation. Expired or
hostname-mismatched certificates produce warnings and may be rejected by clients.

**Check again** only reads machine state. **Apply selected fixes** performs the
selected actions and then repeats the checks. **Next** only navigates and remains
disabled while required checks fail; while a fix is still ticked it refuses with a message, so
apply the ticked fixes or clear them first. Already installed prerequisites remain in place
if the wizard is cancelled. Setup does not install a database server or create AD accounts.

The gMSA must already exist in AD with password-retrieval permission for the host.
Setup can install RSAT and prepare the account locally. If Windows requires a restart,
setup reports it without restarting the machine. Publisher trust is optional and
unselected by default.

For PFX import, use a file containing one end-entity certificate and its private key.
The password may be empty. Probe inspects the file without persisting the key.
Applying the fix imports into `LocalMachine\My`, without export permission or added
root trust, selects the resulting thumbprint and clears the password from the wizard.
An existing certificate needs a persisted machine private key. Existing certificates
are never replaced automatically. Self-signed generation must be explicitly selected.

### PostgreSQL preparation

When built with `-PgBinariesPath`, setup bundles `psql` and tests login as the NodePilot
role with `sslmode=verify-full`. Without that payload, readiness explicitly reports
that sign-in was not verified. The console installer performs only a TCP check.

Provisioning needs `provisioning.postgresSuperUser` and `provisioning.postgresSuperPassword`.
The account needs `rolsuper`, or both `rolcreaterole` and `rolcreatedb`. Existing role
passwords and database owners are not changed. Correct mismatches yourself and retry.
The catalog distinguishes missing roles and databases without parsing localized errors.
Without administrative credentials, setup reports the server error without guessing.

Certificate revocation is checked separately because `psql` does not check it.
The service requires `Check Certificate Revocation=true`. Ensure the CRL distribution
point is reachable or install a current CRL with `certutil -addstore CA <file>.crl`.
An uncheckable or revoked certificate blocks installation.

### Optional source snapshot

The review page includes the product source snapshot under `knowledge\source`
(about 27 MB). Unticking it leaves that AI knowledge source empty. Source-code access
is separately disabled by default through `AiKnowledge:SourceCodeEnabled`.
The complete artifact is verified before the snapshot is removed.
Updates preserve the choice based on the installed directory.
Unattended, use `includeSourceSnapshot: false`; omission means include.

## Unattended deployment

```text
NodePilot-Server-Setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /ANSWERFILE=C:\prod\answers.json
```

The answer file is the configuration surface. Unknown keys are rejected before
installation. For a custom `installPath`, also pass matching `/DIR="D:\Apps\NodePilot"`
so the Inno uninstaller and application use the same directory.

| Switch | Effect |
|---|---|
| `/ANSWERFILE=<path>` | Replaces the wizard configuration. |
| `/FULLREINSTALL` | Performs a fresh installation instead of updating and generates a new external-trigger key. |
| `/PURGEDATA=1` | Uninstall only: also remove the data directory. |
| `/LOG=<path>` | Inno log. The adapter also writes `%TEMP%\nodepilot-server-setup.log`. |
| `/VERYSILENT /SUPPRESSMSGBOXES` | Unattended display behavior. |
| `/DIR=<path>` | Inno installation directory, which must match a custom answer-file path. |

### Answer file

```json
{
  "schemaVersion": 1,
  "mode": "install",
  "installPath": "C:\\Program Files\\NodePilot",
  "dataPath": "C:\\ProgramData\\NodePilot",
  "serviceName": "NodePilot",
  "identity": { "type": "gmsa", "account": "CONTOSO\\svc-nodepilot$" },
  "database": {
    "provider": "sqlserver",
    "sqlServer": "sql01.contoso.local",
    "sqlDatabase": "NodePilot",
    "sqlCertificateHostName": ""
  },
  "network": {
    "publicHostname": "nodepilot.contoso.local",
    "httpsPort": 443,
    "httpPort": 80,
    "allowedHosts": "nodepilot.contoso.local",
    "knownProxyIps": []
  },
  "certificate": {
    "thumbprint": "<40-hex-character-https-thumbprint>",
    "source": "existing"
  },
  "bootstrap": { "adminUsername": "npadmin" }
}
```

Replace the example values before use. `identity.type` accepts `localSystem` or
`gmsa`, with `account` required for gMSA. `database.provider` accepts `sqlserver`
or `postgres`. PostgreSQL requires `postgresHost`, `postgresDatabase`, `postgresUser`,
`postgresPassword` and `postgresRootCertificate` instead of the SQL Server fields.

A nonempty `certificate.thumbprint` must contain 40 hex characters. An empty value
requires self-signed generation or a requested PFX import. For `mode: update`,
`installPath` and `serviceName` suffice alongside the schema and mode. `dataPath` is
optional, and installation-only fields are rejected.

The eleven always-required install keys are `schemaVersion`, `mode`, `installPath`,
`dataPath`, `serviceName`, `identity.type`, `database.provider`, `network.publicHostname`,
`network.httpsPort`, `network.httpPort` and `certificate.thumbprint`.
The provider and identity fields described above are required conditionally.
There are no per-setting command-line switches. `/DIR` and `installPath` must agree;
setup does not enforce that equality.

| Optional key | Purpose |
|---|---|
| `serviceDisplayName` | Service display name. |
| `includeSourceSnapshot` | `true` when omitted. Set `false` to omit the source snapshot. |
| `database.sqlCertificateHostName` | Certificate hostname, otherwise derived from `sqlServer`. |
| `database.postgresPort` | PostgreSQL port, default `5432`. |
| `network.allowedHosts`, `network.knownProxyIps` | Host restrictions and direct trusted proxies. Explicit host lists retain their restrictions with localhost added for health checks. |
| `certificate.source` | `existing` when omitted, or `pfx`. |
| `certificate.pfxPath`, `certificate.pfxPassword` | Required for `pfx`, with an empty thumbprint. The password may be empty. |
| `provisioning.importPfxCertificate` | Import the PFX, mutually exclusive with self-signed generation or an existing thumbprint. |
| `provisioning.installGmsa`, `provisioning.installAdTools` | Prepare an existing gMSA locally. AD tools require `installGmsa`. |
| `provisioning.installDotnetRuntime` | Install the bundled runtimes. |
| `provisioning.createDatabaseAndLogin` | Create the database and grant the service identity access, using the installer's permissions. |
| `provisioning.postgresSuperUser`, `provisioning.postgresSuperPassword` | Required administrative connection for PostgreSQL provisioning. These credentials are not given to the service. |
| `provisioning.generateSelfSignedCertificate` | Create a lab/pilot HTTPS certificate. |
| `provisioning.trustArtifactSigner` | Import the publisher certificate into the machine trust store. |
| `bootstrap.adminUsername` | Create the first administrator with a generated password. |
| `bootstrap.credentialOutputPath` | Credential output file, default `<dataPath>\bootstrap-admin.json`. |
| `seed.backupPath`, `seed.passphrase` | Restore a `.npbackup` into an empty instance on first start. |
| `skips.databaseCheck`, `skips.gmsaCheck` | Skip the corresponding prerequisite check. |

For blank host lists, defaults include the public hostname, short computer name and
localhost. Certificate SANs do not expand an explicit host policy. Prerequisite
actions also work silently when selected in the answer file. On a fresh database,
request `createDatabaseAndLogin` and provide the required administrative access.

The supplied answer file remains untouched. Setup uses a protected temporary copy
and removes it after the run. Passwords in the original file remain plaintext,
so its storage and permissions must protect them.

## Turnkey rollout

`bootstrap.adminUsername` avoids a manual first-login step. Setup redeems the token
and writes a generated password to the protected credential output file.
For an existing configuration, `seed.backupPath` and `seed.passphrase` provide the
first-start restore. The passphrase is stored in the service environment rather
than `appsettings.Production.json`.

Passwords are random per installation. There is no `adminPassword` answer-file key.
The credential file contains username, password, address and timestamp, with a
SYSTEM/Administrators-only ACL applied before writing. It is not deleted automatically.
Collect it, remove the collected file when no longer needed and rotate the password.
`bootstrap.adminUsername` also pins which name may redeem the token.

If the seed includes users, no setup token is issued and bootstrap has nothing to
redeem. An invalid seed blocks startup. Without seed users or bootstrap, the first
administrator must use the token shown on the finish page. Owner-only token files
can be read through the documented `robocopy /B` procedure without weakening ACLs.

| Instance after startup | Bootstrap result |
|---|---|
| Users already exist, including users restored from a seed | `AlreadyProvisioned`, no credential file. |
| No users and `bootstrap.adminUsername` set | `Created`, generated credentials written to the output file. |
| No users and no bootstrap request | Manual first sign-in with the setup token. |
| Automatic token redemption fails | `Failed` with the server response. The healthy installation still returns exit code 0. |

The adapter's INI result contains `bootstrap.status` separately from the setup exit
code. For a packaged rollout, verify that the requested credential output file was
created for this run, or that the seeded account can sign in. A missing credential
file after exit code 0 needs bootstrap diagnosis, not an automatic reinstall.
The finish summary shows generated credentials or the token for manual redemption.

The seed is copied to `<DataPath>\seed.npbackup` and deleted after successful restore.
Existing users cause seeding to be skipped, so it never migrates an existing instance.
Seeding restores settings as well as data. Restart after seeding authentication settings.
LDAP/SSO requires a local break-glass administrator first; it cannot replace bootstrap.
Both `seed` and `bootstrap` may be supplied, but a seed containing users takes precedence.

## Progress and interrupted runs

The wizard shows the operation, elapsed time and setup log. Copy percentages describe
the current copy only. Other phases show activity without an estimated total percentage.
Progress-listener failures do not fail installation.

The worker has a 45-minute timeout. If it times out, setup retains its protected
session because the worker may still be changing the machine. Setup does not cancel
an in-flight mutation. Close the wizard and inspect the log and worker state before
retrying. A worker exiting without a result is reported as a failure.

## Updates and uninstall

Existing installations, including those installed from ZIP, default to an update.
Configuration, database and identity are retained. A successful update leaves the
service running. Rollback restores binaries only, so take a database backup before
updating. Details: [deployment scripts](../README.md#updates-and-removal).

Uninstall is available through Apps & features or the setup's mode page. It removes
the service, binaries, firewall rules, installation marker and service environment.
The data directory is retained unless `/PURGEDATA=1` is passed. The database is
always retained. Installations from ZIP use `Uninstall-NodePilot.ps1`.

The installation marker under `HKLM\SOFTWARE\NodePilot\Server` records the actual
service and directory names. Keep it intact so uninstall can find custom paths.
Saved update summaries do not overwrite the original installation credentials.

Fresh-install summaries use `nodepilot-setup-summary.txt`; updates use
`nodepilot-update-summary.txt`. The finish page shows the address, certificate,
service and paths. On a fresh installation it also shows available bootstrap secrets.
`install-report.txt` omits the trigger key. `/FULLREINSTALL` generates a new key,
so existing integrations need the new value and an explicit workflow allowlist.

## Exit codes and diagnosis

| Exit code | Meaning |
|---|---|
| 0 | Completed successfully. |
| 3 | Fatal Inno error while preparing an installation phase. |
| 7 | Validation, prerequisite preparation or installation failed. |
| 8 | Setup could not proceed and Windows requires a restart. |

Any nonzero exit means setup did not complete. Code 7 alone does not establish
whether rollback ran or succeeded. Read the adapter log and its service-start
diagnostics. Common runtime failures are covered by the
[deployment guide](../../docs/deployment-guide.md#troubleshooting).

Adapter-internal codes in the log are 1 for an adapter crash, 2 for failed readiness,
3 for invalid answers and 4 for installation failure. These differ from the setup
process codes above. Automatic bootstrap failure is reported separately.

## Building and testing

Build from the repository root with Inno Setup 6 and a signed artifact:

```powershell
.\deploy\Build-Artifact.ps1 -SigningCertificateThumbprint $tp `
    -IncludeServerInstaller -InstallerSigningCertificateThumbprint $tp `
    -PgBinariesPath 'C:\Packages\pgsql'
```

The first build downloads both runtime installers. Their SHA512, checked-in pins
and Microsoft Authenticode signatures are verified. Pins are stored in
[runtime-payload.lock.json](runtime-payload.lock.json). The Hosting Bundle is not used.

`-PgBinariesPath` bundles the PostgreSQL client for readiness and provisioning.
It may be omitted for SQL-only setups. Standalone builds use
`Build-ServerInstaller.ps1 -ArtifactPath <zip> -TrustedSignerThumbprint <verified-pin>`.
Unsigned development artifacts cannot be packaged as a server installer.

The answer-file adapter is checked by [Test-SetupAdapter.ps1](../Test-SetupAdapter.ps1).
The complete [test coverage and manual acceptance matrix](testing.md) is maintained
separately, including installer constraints and known gaps. Release acceptance follows
[RELEASING.md](../../RELEASING.md) and the [release lab](../../scripts/release-lab/README.md).
