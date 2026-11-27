# Desktop implementation reference

For build commands and installation, see the [desktop README](../deploy/desktop/README.md).
This reference covers the contracts to preserve when changing the installer or shell.

## Runtime and security

| Component | Location or identity |
|---|---|
| API, SPA and PowerShell modules | `C:\Program Files\NodePilot\app` |
| Electron / PostgreSQL binaries | `C:\Program Files\NodePilot\desktop` / `pgsql` |
| CLI / MCP clients | `C:\Program Files\NodePilot\tools\np` / `tools\mcp` |
| Database, logs, keys, configuration and backups | `%ProgramData%\NodePilot` |
| `NodePilot` service | LocalSystem; starts at boot, depends on `NodePilotDb` |
| `NodePilotDb` service | NetworkService; PostgreSQL bound to `127.0.0.1` |

The API serves the SPA; Electron only displays it. Production hardening remains enabled.
`Deployment:Mode=Desktop` makes Kestrel bind to loopback and permits
`Database:AllowInsecureTls=true` only for a loopback database. All HTTP routes share
that local-only listener. No firewall rule is created. Local script execution uses
the API service's SYSTEM identity.

The installer writes `%ProgramData%\NodePilot\desktop.json`:

```json
{
  "schemaVersion": 1,
  "origin": "https://localhost:47000",
  "certificateSha256": "<uppercase-hex>",
  "serviceName": "NodePilot"
}
```

Read the actual origin from this file; ports are allocated during provisioning.
The database password is stored in the ACL-restricted service environment, never here.
Electron pins the self-signed localhost certificate by SHA-256. No root CA is installed;
an ordinary browser may warn about the certificate.

The SPA window enables context isolation, sandboxing and web security, with no Node
integration, preload or IPC. Off-origin navigation, popups and permission requests are
blocked. Downloads must remain on the app origin through redirects and use the native
Save As dialog. The documentation window is confined to `/docs`; external HTTPS links
open in the system browser. Implementation: [shell security](../src/nodepilot-desktop/src/security.ts).

API, `np` and `nodepilot-mcp` are self-contained and carry their own .NET runtime; a
machine-wide .NET or Windows update does not replace it. Only a rebuilt desktop package
moves an installation to a patched runtime (10.0.14 since 1.5.4). The build publishes all
three at `$DesktopRuntimeVersion` and refuses a stage whose framework declarations or
runtime binaries are older; [Test-DesktopRuntimePayload.ps1](../deploy/Test-DesktopRuntimePayload.ps1)
covers that check.

Database startup waits and runtime outages follow the shared
[database availability model](adr/0011-database-availability-breaker.md).
For paths, health checks and recovery, see [troubleshooting](desktop-troubleshooting.md).

## First-run administrator handoff

1. With no users, the API creates a SYSTEM-owned, one-shot `admin-setup.token`.
2. The elevated provisioner takes ownership for `BUILTIN\Administrators`, grants read
   access and writes `%LOCALAPPDATA%\NodePilot\admin-setup.handoff` for the user who
   will run the shell. The handoff is restricted to that user and SYSTEM; the source
   token's owner and ACL must remain within `AdminBootstrap.Validate`'s trusted set.
   The elevated installer's profile must not be assumed to be the shell user's profile,
   and neither may the console user (RDP, switched user). Setup runs a check as the
   original user (`ExecAsOriginalUser`), which reports that account's SID, and passes it as
   `-HandoffUserSid`; `Resolve-DesktopHandoffTarget` reads the profile of exactly that SID
   and fails provisioning when the account has no profile. Setup run as SYSTEM or another
   service account by software distribution hands off to the user signed in at the
   console, or to the service account when nobody is signed in. `Update-Desktop.ps1`
   passes the account running the update; `-HandoffUserProfile` overrides the directory
   for the dev loop and tests.
3. The local setup page exposes only `completeAdminSetup({username,password})`.
   Electron's main process reads the token and sends `POST /api/auth/login` with
   `X-Setup-Token`, shares the returned cookies with the SPA session, deletes both
   token copies and opens the SPA. The renderer never receives the token.

Provisioning is implemented in [Provision-LocalDb.ps1](../deploy/desktop/Provision-LocalDb.ps1);
shell behaviour and tests are described in the [Electron README](../src/nodepilot-desktop/README.md).

## Packaging contracts

[Build-DesktopInstaller.ps1](../deploy/desktop/Build-DesktopInstaller.ps1) stages the
payload and compiles [NodePilot.iss](../deploy/desktop/NodePilot.iss). Preserve these rules:

- Publish the API and operator clients self-contained for `win-x64`. The API is
  folder-deployed so the hosted PowerShell SDK can load its modules.
- Copy the SDK's built-in PowerShell modules to `app\Modules`, beside
  `System.Management.Automation.dll`. Fail the build if `Microsoft.PowerShell.Utility`
  is missing; the installed app must not depend on a system-wide PowerShell 7.
- Verify PostgreSQL major version **16** from `bin\postgres.exe`. Bundle `bin`, `lib`
  and `share`; `initdb` requires `share\postgres.bki` and timezone data.
- Build the SPA into `app\wwwroot` and documentation into `app\wwwroot\docs`.
  [Sync-DesktopApp.ps1](../deploy/desktop/Sync-DesktopApp.ps1) must exclude `docs` from
  the SPA mirror, then sync documentation separately. API sync preserves Modules and
  wwwroot; the script does not replace the packaged shell.
- Generate icons from tracked `appicon-<skin>.png` assets with
  [generate-desktop-icons.ps1](../scripts/generate-desktop-icons.ps1). Generated assets
  are gitignored. The default is blue (`appicon-dark.png`); windows and tray icons
  follow the SPA favicon through `src/skins.ts`. EXE and shortcut icons stay static.
  `-WizardImageDirectory` generates 24-bit wizard bitmaps for each DPI size.
- Package Electron with archive integrity checks and hardened fuses: no Node mode,
  `NODE_OPTIONS` or inspector flags, encrypted cookies, and loading only from `app.asar`.
  Packaging rejects filesystem links/reparse paths. New fuses require an explicit value
  in [harden-electron-fuses.mjs](../src/nodepilot-desktop/scripts/harden-electron-fuses.mjs).
- Sign after packaging and before generating checksums. Use the
  [release build](../RELEASING.md); the standalone desktop build does not sign.

## Setup lifecycle

- **Provisioning:** `CurStepChanged` / `ssPostInstall` checks the provisioner's exit
  code. Failure reports `%TEMP%\nodepilot-provision.log` and suppresses shell launch;
  it does not roll back the installed files and database.
- **Before replacing files:** `PrepareToInstall` extracts
  [Prepare-DesktopSetup.ps1](../deploy/desktop/Prepare-DesktopSetup.ps1) from the new
  installer, creates an ACL-protected `pg_dump` and stops the shell, clients and services.
  [DesktopRuntime.ps1](../deploy/desktop/DesktopRuntime.ps1) waits for actual process
  exit, including PostgreSQL processes that outlive their service wrapper. Failure
  aborts setup before program files change.
- **Reprovisioning:** wait for service deletion to complete, recreate the services and
  reuse the cluster when `pgdata\PG_VERSION` exists.
- **Staged rollback:** [Update-Desktop.ps1](../deploy/desktop/Update-Desktop.ps1) also
  supports restoring binaries, configuration and database. Before anything changes it
  snapshots both service registrations (binary path, account, start type, dependencies,
  failure actions, environment, key ACL) and refuses to start when either service is
  missing. The rollback registers both again from that snapshot before the database
  restore. Start only PostgreSQL until the database restore succeeds. A failed restore
  leaves the API stopped and reports the backup path for manual recovery.
- **Uninstall:** `CurUninstallStepChanged` passes the user's data choice to
  [Uninstall-Desktop.ps1](../deploy/desktop/Uninstall-Desktop.ps1). Remove services,
  certificate and private key, setup handoffs and rendered configuration. Purging also
  removes ProgramData and NodePilot folders in every user profile. Report incomplete
  removal through the exit code and `%TEMP%\nodepilot-uninstall.log`; the uninstaller then
  stops before Inno removes the program files or the "Apps & features" entry, so the
  uninstall can be run again once the cause is fixed.

## Validation

The [release lab](../scripts/release-lab/README.md) defines the installation, reboot,
upgrade, uninstall and reinstall matrix for clean Windows VMs. The
[UI acceptance catalog](../scripts/release-lab/computer-use/README.md) covers interactive
behaviour and is experimental, not a release gate.

Unit and contract tests do not establish Electron or Windows-service behaviour.
[Test-DeploymentTemplates.ps1](../deploy/Test-DeploymentTemplates.ps1) checks script
contracts as text; [Test-DesktopUpdate.ps1](../deploy/Test-DesktopUpdate.ps1) exercises
rollback ordering offline. Also validate a forced-health-failure rollback on a disposable
VM, first-run setup, tray restart, skin changes, single-instance behaviour and triggers
continuing after the window closes. Record what was actually exercised.
