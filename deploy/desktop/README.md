# NodePilot Desktop (Electron) — machine-wide, offline one-click installer

This directory builds NodePilot as a **local desktop application** for **Windows 11 x64**: a single
signed `.exe` that installs the app, a bundled .NET 10 runtime, and a **local PostgreSQL** server,
then runs everything as background Windows services with a native Electron window on top. No .NET
runtime, no external database, and no internet connection are required at install time.

It is a distinct shipping target from the server rollout in [`../README.md`](../README.md) — that one
is a domain-joined Windows **Server** as a service behind Kestrel TLS with an external database.

## Architecture

NodePilot is an *orchestrator*: schedule / file-watcher / webhook triggers must fire even when no
window is open. So the backend runs as an always-on service and the Electron shell is a thin viewer.

```
Installer (.exe, signed)
 ├─ C:\Program Files\NodePilot\   app\ (self-contained API + wwwroot + Modules) · desktop\ (Electron) · pgsql\ (PG16 server runtime) · deploy\
 ├─ C:\ProgramData\NodePilot\     pgdata\ · logs\ · secrets\ · keys · admin-setup.token · desktop.json · backups\
 ├─ Service "NodePilotDb"  (postgres, NetworkService, 127.0.0.1:<pgport>, boot-start)
 ├─ Service "NodePilot"    (NodePilot.Api.exe, LocalSystem, https://127.0.0.1:<apiport>, boot-start, depend= NodePilotDb)
 └─ Start Menu / Desktop → NodePilot.exe (Electron → loads the origin from desktop.json)
```

The .NET backend already serves the SPA same-origin (`UseStaticFiles` + `MapFallbackToFile`), so the
Electron shell never bundles or renders the frontend itself — it points a hardened `BrowserWindow`
at `https://localhost:<port>` and manages nothing but the window and the tray.

### `Deployment:Mode=Desktop`

The desktop package runs with `ASPNETCORE_ENVIRONMENT=Production` (full hardening: security headers,
Swagger off, inline-password guard) plus a new posture key **`Deployment:Mode=Desktop`**. Desktop mode
relaxes **only** the things that make sense for a machine talking to itself:

- `DatabaseTlsBootValidator` accepts `Database:AllowInsecureTls=true` **only** for a loopback DB host
  under Desktop mode (a 127.0.0.1 Postgres with no PKI). Remote hosts still fail closed.
- Kestrel binds **loopback only** (`ListenLocalhost`), never every interface.
- Before the migration bootstrap, the API waits up to **120 s** for Postgres connectivity (only
  reachability is retried; a migration/schema error surfaces immediately).

Everything else stays hardened. `Deployment:Mode` defaults to `Server`; an unknown value is a boot error.

### desktop.json — installer → shell handoff (no secrets)

`%ProgramData%\NodePilot\desktop.json` tells the Electron shell what to load and trust:

```json
{ "schemaVersion": 1, "origin": "https://localhost:47000",
  "certificateSha256": "<uppercase-hex>", "serviceName": "NodePilot" }
```

The DB password is **never** here — it lives only in the ACL-restricted `ConnectionStrings__Postgres`
service-environment value.

## Security model

- **Loopback binds the whole listener, not individual routes.** `Deployment:Mode=Desktop` forces
  `ListenLocalhost`, so the SPA, *every* `/api/*` endpoint, `/hubs/*`, `/healthz` and
  `/api/webhooks/*` are reachable from that machine only — nothing listens on a network interface
  and no firewall rule is created. A common misreading is that only the trigger route is blocked
  while the rest of the API stays reachable; it is not.
  The practical rule: **anything NodePilot initiates works, anything that must reach in does not.**
  Schedule/file-watcher/database/event-log triggers and all outbound automation (WinRM, `restApi`,
  `sql`, SMTP, alerting webhooks) are unaffected; inbound webhooks and the external trigger API
  (also disabled via an empty `ExternalTrigger:ApiKey`) are unusable.
- **API runs as LocalSystem** (zero-config). Consequence: loopback `runScript` activities run with
  **SYSTEM** rights. This is an explicit v1 decision for a single-user local orchestrator.
- **Postgres runs as NetworkService**, bound to 127.0.0.1 only.
- **Loopback TLS by pinning, not a root CA.** The installer creates a self-signed `localhost`
  certificate in `LocalMachine\My`; the Electron session pins it by SHA-256 fingerprint. No system
  trust store is modified, so an ordinary browser visiting the URL *may warn* — that is expected;
  Electron is the supported entry point.
- **Electron hardening:** the SPA window has `contextIsolation`, `sandbox`, `webSecurity` on,
  `nodeIntegration` off, and **no preload / no IPC**. Navigation off-origin, popups, downloads, and
  permission requests are all blocked.
- **First-run token never reaches the renderer.** See below.
- **Minimal ACLs** on ProgramData, the service registry key, the cert key, `pgdata`, `secrets\`,
  `backups\`, and the per-user handoff file.

### First-run admin setup

1. On first boot (empty users table) the API writes a one-shot `admin-setup.token` (SYSTEM-owned).
2. The elevated installer hands it to the user session. The token's ACL is owner-only, so even an
   elevated Administrator can neither read it nor change its DACL — the provisioner therefore first
   takes ownership for `BUILTIN\Administrators` (`takeown /a`) and grants that group read, then
   writes the value to `%LOCALAPPDATA%\NodePilot\admin-setup.handoff` (restricted to the installing
   user + SYSTEM) and launches Electron as that user. Owner and every remaining ACE stay inside the
   backend's trusted set, so `AdminBootstrap.Validate` still accepts the token.
3. Electron shows a **local** setup page whose only bridge is `completeAdminSetup({username,password})`.
4. The main process reads the handoff token, `POST /api/auth/login` with header `X-Setup-Token`, shares
   the returned cookies with the SPA session, deletes **both** token copies, and opens the preload-less
   SPA window. The token is never exposed to the renderer.

## Build

Requirements: .NET 10 SDK, Node + npm, [Inno Setup 6](https://jrsoftware.org/isdl.php) (`ISCC.exe`),
and a PostgreSQL 16 binaries folder (the `pgsql` directory from the EDB zip distribution).

```powershell
./Build-DesktopInstaller.ps1 -PgBinariesPath 'C:\path\to\pgsql' -Version 1.0.0
# -> out\NodePilot-Desktop-Setup-1.0.0.exe   (sign with your Authenticode cert before distribution)
```

The build generates the icons from the tracked brand asset `src/nodepilot-ui/public/appicon.png`
(multi-resolution `.ico`: 16/32/48/256), publishes the API self-contained (`-r win-x64
--self-contained true`, no single-file — the PowerShell SDK is folder-deployed), builds the SPA into
`app\wwwroot`, packages the Electron shell with Electron Forge, stages the Postgres server runtime +
scripts, and compiles the installer.

Two build steps are load-bearing and easy to break by accident:

- **`app\Modules` (PowerShell built-in modules).** `Microsoft.PowerShell.SDK` ships Utility,
  Management, CimCmdlets etc. under `runtimes\win\lib\<tfm>\Modules`, but the hosted runspace looks
  for them at `$PSHOME\Modules` — i.e. next to `System.Management.Automation.dll` in the app root.
  The build copies them there and fails if `Microsoft.PowerShell.Utility` is missing afterwards.
  Without this every `runScript` fails with *"the module could not be loaded … compatible with the
  'Core' edition"* unless PowerShell 7 happens to be installed system-wide — which the desktop
  package must not depend on.
- **PostgreSQL subset.** Only `bin`, `lib` and `share` are bundled. A stock EDB distribution also
  carries pgAdmin 4 (~630 MB, a GUI with its own Chromium), `doc`, `include` and StackBuilder, none
  of which NodePilot uses; excluding them takes the installer from ~350 MB to **~176 MB**. `share`
  is not optional — `initdb` fails without `postgres.bki` and the timezone data. `-PgBinariesPath`
  may therefore point at a full EDB folder or an already-trimmed one.

## Install / update / uninstall

- **Install:** run the `.exe` as a local administrator (UAC). It lays down files, runs
  `Provision-LocalDb.ps1` (Postgres cluster + service, cert, config, API service, desktop.json, token
  handoff), and launches the shell.
- **Update:** run a newer installer. On an existing cluster it takes an ACL-protected `pg_dump` first,
  overwrites binaries, and re-provisions. Re-provisioning is repeatable but not side-effect free: step 0
  **stops and deletes** both services and recreates them, and the existing cluster is reused (`initdb`
  is skipped when `pgdata\PG_VERSION` exists), so data survives. `Update-Desktop.ps1` also implements a
  full staged update with binary + config + DB rollback for direct/advanced use. Postgres **major**
  upgrades are out of scope for v1.
- **Uninstall:** removes both services, the certificate, and Program Files. **ProgramData and `pgdata`
  are preserved** unless `Uninstall-Desktop.ps1 -PurgeData` is used.

## Files

| File | Role |
|---|---|
| `Build-DesktopInstaller.ps1` | Build orchestrator (icons + publish + SPA + Modules + Electron + PG subset + ISCC). |
| `Sync-DesktopApp.ps1` | Dev loop: pushes local changes into an installed app in ~1 min (see below). |
| `NodePilot.iss` | Inno Setup installer definition. |
| `Provision-LocalDb.ps1` | First-run/repeatable runtime provisioner (DB, services, cert, config, handoff). |
| `Update-Desktop.ps1` | Pre-upgrade backup + full staged update with rollback. |
| `Uninstall-Desktop.ps1` | Service + cert removal (data preserved by default). |
| `appsettings.Desktop.json.template` | Production-hardened desktop config (rendered by the provisioner). |

## Iterating without rebuilding the installer

A full installer build takes ~10–15 minutes, but it is only needed to **distribute**. The installed
app is just files plus two services, so day-to-day changes have much shorter loops:

| Changing | Fastest loop | Time |
|---|---|---|
| Electron shell | `cd src/nodepilot-desktop; npm start` — runs **from source** against the installed backend (it reads `%ProgramData%\NodePilot\desktop.json`), no packaging at all | seconds |
| Backend / SPA, normal work | ordinary dev mode (backend on 5000, Vite on 5173 with HMR) | seconds |
| Backend / SPA, **as packaged** | `Sync-DesktopApp.ps1 -Component api\|spa\|all` (elevated) — incremental publish/build, robocopy into the installation, service restart + health poll | ~1 min |
| Distribution | `Build-DesktopInstaller.ps1` | ~10–15 min |

Use the sync script when the *packaging* matters — service identity is LocalSystem, the DB is the
bundled Postgres, TLS is the pinned loopback cert — none of which dev mode reproduces. It never
mirrors over `app\Modules` or `app\wwwroot`, so the PowerShell modules and SPA stay intact.

## Known gaps (deliberately not covered in v1)

Honest inventory so nobody assumes more coverage than exists:

- **No automated tests for the Electron module.** Certificate pinning, the setup-token IPC guard and
  the navigation/download/permission blocking in `src/nodepilot-desktop` are verified only by hand.
  The backend half of the feature *is* unit-tested (`DeploymentModeTests`,
  `DatabaseTlsBootValidatorTests`, `DatabaseReadinessGateTests`, `KestrelHttpsConfiguratorTests`).
- **No CI coverage** for `src/nodepilot-desktop` or `deploy/desktop/*`. A `typecheck` script exists
  but nothing invokes it; there is no lint config. `Test-DeploymentTemplates.ps1` validates the
  server templates only — `appsettings.Desktop.json.template` is never parsed by any check.
- **The installer is unsigned.** SmartScreen warns on first launch until an Authenticode certificate
  is wired into the build.
- **Not exercised end-to-end:** upgrade with a forced health failure (the rollback path),
  installation on a genuinely clean VM, and process-isolated `runScript` (`config.isolated`).
- **Postgres major-version upgrades** and **Electron auto-update** are out of scope by design.

## On-VM validation (test plan)

The PowerShell + Inno + provisioning paths cannot be exercised on a build host; validate on a clean
Windows 11 x64 VM without .NET/Postgres preinstalled:

1. Install → both services running, `pgdata` initialized, `desktop.json` written, cert pinned.
2. `GET https://127.0.0.1:<apiport>/healthz/ready` → 200 (migration ran).
3. Launch shell → SPA loads without a cert warning → first-run admin creation → a `runScript`
   workflow against `localhost` runs in-process.
4. Close the window → a `scheduleTrigger` still fires in the background → reopen is single-instance.
5. Reboot → services auto-start. Upgrade → users/credentials/workflows/PG data survive; forced health
   failure rolls back. Uninstall → services gone, `pgdata` preserved (or purged with `-PurgeData`).
