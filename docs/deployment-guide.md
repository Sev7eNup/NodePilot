# Production deployment guide

This guide covers artifact verification, local builds and deployment failures.
Installation and first access are described in the
[production walkthrough](../src/nodepilot-docs-ui/content/en/deployment/production.md).
The [server setup](../deploy/server/README.md) and [script reference](../deploy/README.md)
document their respective configuration surfaces.

The server package installs the API and web UI as one Windows service. Binaries and
`appsettings.Production.json` are in `C:\Program Files\NodePilot`, while logs and
generated keys are in `C:\ProgramData\NodePilot`. The database schema is migrated
on startup. The [desktop installer](../deploy/desktop/README.md) is a separate,
loopback-only option without inbound webhooks, SSO or HA.

## Verify a release

Download the installer or ZIP artifacts from the
[release page](https://github.com/Sev7eNup/NodePilot/releases/latest), together with
`NodePilot-<version>.SHA256SUMS.txt` and `nodepilot-release-signing.cer`.
For scripted installation, also obtain the artifact's `.manifest.json` and
`.manifest.json.p7s` files and `NodePilot-Deploy-Scripts-<version>.zip`.

Compare the SHA-256 of every downloaded file with the published checksum list.
Then compare the certificate thumbprint with the publisher thumbprint in the release notes:

```powershell
Get-FileHash .\NodePilot-Server-Setup-<version>.exe -Algorithm SHA256
(Get-PfxCertificate .\nodepilot-release-signing.cer).Thumbprint
(Get-AuthenticodeSignature .\NodePilot-Server-Setup-<version>.exe).SignerCertificate.Thumbprint
```

The GUI setup pins the artifact signer at build time. Scripts require the verified
value as `-TrustedArtifactSignerThumbprint`. Artifact verification checks the detached
signature, expected signer, certificate validity and code-signing usage. It does not
require that publisher to be trusted in the machine certificate store.

The publisher pin is supplied by the release notes, not accepted just because it
appears on a downloaded certificate. A matching hash alone checks the file against
the checksum list; it does not identify the publisher.

Use the verified deploy-scripts archive to obtain the installer and its helpers.
Do not extract the unverified application archive merely to obtain its verifier.

### First run: the SmartScreen prompt

Published installers use a self-signed publisher certificate. Windows may report an
untrusted root or show SmartScreen's unrecognized-app warning. These messages do not
replace checking the file's checksum and expected publisher before execution.
An `UnknownError` signature status alone is not proof that a file is safe.

After verification, the downloaded file can be allowed through the prompt or
unblocked with `Unblock-File`. Verify downloaded ZIPs before unblocking and extracting
them as well, since extracted scripts can inherit the Internet-zone mark.
Importing the publisher certificate is optional for artifact verification and does
not remove SmartScreen's reputation check.

For a ZIP, unblock before extraction so its scripts do not inherit the Internet-zone mark:

```powershell
# Run after comparing the checksum and publisher with the release notes.
Unblock-File .\NodePilot-Deploy-Scripts-<version>.zip
Expand-Archive .\NodePilot-Deploy-Scripts-<version>.zip -DestinationPath C:\Packages
```

Release notes must explain the expected SmartScreen prompt and verification steps.
A local build may not show the prompt because it has no downloaded-file zone marker.

## Build from source

Use the SDK and Node versions declared in `global.json` and the package manifests.
The signing certificate needs a private key, code-signing usage and current validity.
Keep it for subsequent releases.

For a local build without an existing code-signing certificate:

```powershell
$signer = New-SelfSignedCertificate -Type CodeSigningCert `
    -Subject 'CN=NodePilot local publisher' -CertStoreLocation Cert:\CurrentUser\My
```

When using an existing certificate, load that certificate into `$signer` instead.

```powershell
.\deploy\Build-Artifact.ps1 -SigningCertificateThumbprint $signer.Thumbprint

.\deploy\Build-Artifact.ps1 -SigningCertificateThumbprint $signer.Thumbprint `
    -IncludeServerInstaller -IncludeDesktopInstaller -PgBinariesPath 'C:\Packages\pgsql' `
    -InstallerSigningCertificateThumbprint $signer.Thumbprint
```

Installer builds require Inno Setup 6. The desktop bundle also needs PostgreSQL binaries.
Sign during the build so the resulting checksum list covers the signed installers.
Transfer the ZIP, manifest, detached signature and deploy-scripts archive together.

`-Version` defaults to the product version in `Directory.Build.props`. Missing Inno
Setup or desktop PostgreSQL binaries cause the affected installer step to be skipped
with a warning, while the server ZIP is still produced. Check the actual output files.
The build exports `nodepilot-release-signing.cer` and creates the checksum list for
its outputs. The server installer can also use `-PgBinariesPath` to bundle `psql`.
For an npm file-lock failure, close the locking process and retry. `-SkipNpmCi`
reuses existing `node_modules` when those dependencies are already prepared.

## Certificates and host requirements

The script route requires both x64 .NET and ASP.NET Core runtimes, at least 10.0.11
within the 10.x line and satisfying the artifact's declared framework versions.
The GUI setup can install its bundled runtimes. Kestrel does not require IIS.
SQL Server requires 2022 CU1 or later for `Encrypt=Strict`, or use PostgreSQL 16+.

Use the standalone runtimes. The Hosting Bundle changes IIS and restarts W3SVC,
which affects other applications on shared hosts. Install and update check every
framework required by the artifact before replacing the installation.

| Certificate | Purpose and location |
|---|---|
| Publisher signing certificate | Verifies the artifact against the expected publisher pin. |
| Kestrel HTTPS certificate | In `LocalMachine\My` on the application host, with private-key access for the service and hostname coverage. Clients must trust its issuer. |
| SQL Server TLS certificate | On the SQL host, with a suitable RSA KeyExchange key and matching hostname. The application host must trust its issuer. |
| PostgreSQL TLS certificate | Server certificate/key on the database host, trusted root PEM and current CRL available to the application host. |

Certificate preparation is covered in
[Database TLS](../src/nodepilot-docs-ui/content/en/deployment/database-tls.md).
Replacing an HTTPS certificate also requires granting the service identity access to
the new private key. Trusting the issuer alone does not repair a hostname mismatch.

SQL Server's certificate requires `KeySpec=KeyExchange`. A CNG key can leave it
unavailable for selection by SQL Server. PostgreSQL additionally requires a reachable
CRL distribution point or a current CRL in `LocalMachine\CA` on the application host.

## Troubleshooting

Start with `%TEMP%\nodepilot-server-setup.log`, the Windows Application log and the
service logs under `C:\ProgramData\NodePilot\logs`. Correlate timestamps with the
current attempt, since old installation messages may remain in the same log.

The [detailed troubleshooting guide](deployment-troubleshooting.md) provides commands,
log retention, bootstrap checks, database and ACL repairs, and failures on older builds.

| Symptom | Check |
|---|---|
| Missing or outdated runtime | Check both `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App`, architecture and the artifact's required patch. |
| Service cannot log on | Verify the gMSA is installed and available, and that Netlogon/DC access works at boot. |
| SQL login failure or `/healthz/ready` returns 503 | Check the service identity's database access, TLS and `/healthz/database`. Correct connection configuration requires a restart. |
| SQL error 8005 | Verify SQL Server 2022 has CU1 or later. |
| Socket error 10013 or unavailable ports | Check HTTP.SYS reservations as well as listeners. Change the HTTPS port or set HTTP to 0. |
| Certificate missing or private key unavailable | Check `LocalMachine\My`, the private key and the service account's read permission. |
| Invalid hostname response | Check `AllowedHosts`, the requested URL and certificate names. A literal `*` is rejected under strict host policy. |
| CLI certificate trust failure | Use a trusted issuer or the expected SHA-256 certificate pin with `--tls-thumbprint`. The URL should match the certificate's names. |
| Setup token access denied | The file is intentionally owner-only. Use the documented administrator `robocopy /B` procedure. |
| JWT key directory rejected | Inspect the data-directory ACL for untrusted write grants. Rotating only the key file does not fix the parent directory. |
| Update cannot replace DLLs | Identify the process holding the files. Current updates wait and stop remaining installation processes before swapping files. |
| Database outage during operation | `/api` returns `503 DATABASE_UNAVAILABLE` until successful probes restore access. See [ADR 0011](adr/0011-database-availability-breaker.md). |
| LDAP/SSO sign-in failure | Check the audit reason, directory health, UPN, BaseDn and allowed groups using the [authentication guide](ldap-windows-sso.md). |
| PowerShell or update blocked by endpoint protection | Review the [AV exclusion guide](av-exclusions.md) with the responsible administrators. |

### Is the live connection healthy?

SignalR uses `/hubs/execution` and can fall back from WebSockets to Server-Sent Events
or long polling. A WebSocket error with continuing live updates therefore indicates
a failed transport attempt, not necessarily a lost application connection.

Inspect the browser network requests for negotiation, upgrade status and the active
fallback transport. Compare direct access with the configured proxy path. A failed
upgrade can result from a proxy or TLS inspection, while server-side 503 responses
can come from database unavailability or a follower node in an HA deployment.
Check `/healthz/database` and `/healthz/leader` before attributing the error to the network.

HA traffic must reach the leader with connection affinity. The
[HA guide](ha-active-passive.md) describes the routing requirements.
For browser-only failures, retest without extensions and inspect the actual failed request.
The [transport diagnosis](deployment-troubleshooting.md#live-connection) shows how to
distinguish WebSockets, SSE and polling and compare direct access with a proxy.

## After installation

Follow the [installation verification](../src/nodepilot-docs-ui/content/en/deployment/production.md#7-verify-the-installation)
and [first-admin steps](../src/nodepilot-docs-ui/content/en/deployment/production.md#8-create-the-first-admin-account).
Add target machines by FQDN. WinRM uses HTTP 5985, or HTTPS 5986 with `UseSsl` and a
listener certificate trusted by NodePilot. Grant the chosen credential or service
identity access to the target's WinRM endpoint, through local Administrators or
Remote Management Users as appropriate. Credential-free delegation requires the
[target-machine setup](../src/nodepilot-docs-ui/content/en/deployment/production.md#what-the-service-identity-means-for-target-machines).

Configure [alerting](alerting.md), [AI features](ai-features.md) and
[configuration backups](../deploy/README.md#backup-and-operational-responsibilities)
as needed. Native database backups remain required for execution history and full recovery.
