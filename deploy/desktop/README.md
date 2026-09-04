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
 ├─ C:\Program Files\NodePilot\   app\ (self-contained API + wwwroot + Modules) · desktop\ (Electron) · pgsql\ (PG16 server runtime) · deploy\ · tools\np (np CLI) · tools\mcp (nodepilot-mcp)
 ├─ C:\ProgramData\NodePilot\     pgdata\ · logs\ · secrets\ · keys · admin-setup.token · desktop.json · backups\
 ├─ Service "NodePilotDb"  (postgres, NetworkService, 127.0.0.1:<pgport>, boot-start)
 ├─ Service "NodePilot"    (NodePilot.Api.exe, LocalSystem, https://127.0.0.1:<apiport>, boot-start, depend= NodePilotDb)
 └─ Start Menu (+ Desktop shortcut, optional) → NodePilot.exe (Electron → loads the origin from desktop.json)
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

Waiting for the database before the migration bootstrap used to be listed here as a third Desktop
relaxation. It is not one any more: `DatabaseReadinessGate` runs in **both** deployment modes,
because both race the same way at boot — Desktop against the bundled Postgres service, Server
against a remote database still recovering. The bound is `Database:StartupWaitSeconds` (default
120 s). Only reachability is retried; a migration/schema error surfaces immediately.

> **Runtime outages:** If the bundled `NodePilotDb` service stops or hangs, the API stays up and
> answers `503 DATABASE_UNAVAILABLE`; `/healthz/ready` returns 503 while `/healthz/database` reports
> the state and reason. The UI shows a banner and resumes automatically after Postgres recovers.
> Running workflows pause at a durable step boundary, and trigger fires observed during the outage
> are not replayed. `RejectedByServer` requires fixing the local credentials, database or TLS setup;
> restart the service when its connection settings changed.

Everything else stays hardened. `Deployment:Mode` defaults to `Server`; an unknown value is a boot error.

### desktop.json — installer → shell handoff (no secrets)

`%ProgramData%\NodePilot\desktop.json` tells the Electron shell what to load and trust:

```json
{ "schemaVersion": 1, "origin": "https://localhost:47000",
  "certificateSha256": "<uppercase-hex>", "serviceName": "NodePilot" }
```

The port is **not fixed at 47000**: `Provision-LocalDb.ps1` picks the first free port from 47000
upwards (and 47100–47149 for Postgres), so an installation that hit a busy port looks different.
Read the actual origin out of `desktop.json` rather than assuming the number above.

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
  (also disabled because no scoped external-trigger key is configured) are unusable.
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
   writes the value to `%LOCALAPPDATA%\NodePilot\admin-setup.handoff` (restricted to that user +
   SYSTEM) and Inno launches Electron as that user. Owner and every remaining ACE stay inside the
   backend's trusted set, so `AdminBootstrap.Validate` still accepts the token.

   **Which user is "that user" is resolved explicitly, not assumed.** The installer runs elevated
   but launches the shell with `runasoriginaluser`, so the two are different principals whenever a
   standard user elevates with someone *else's* administrator credentials — the normal case on a
   managed machine. `Get-InteractiveUserProfile` therefore resolves the console user via
   `Win32_ComputerSystem.UserName` and reads their profile directory out of the `ProfileList`
   registry, instead of using the elevated process's own `%LOCALAPPDATA%`. Inno's `{localappdata}`
   would be no better: it expands in the elevated context too. `-HandoffUserProfile` overrides the
   resolution for the dev loop and for tests. Getting this wrong strands the user on a login form
   for an account that does not exist yet, with the only remaining token copy SYSTEM-owned —
   recovery steps are in [`docs/desktop-troubleshooting.md`](../../docs/desktop-troubleshooting.md).
3. Electron shows a **local** setup page whose only bridge is `completeAdminSetup({username,password})`.
4. The main process reads the handoff token, `POST /api/auth/login` with header `X-Setup-Token`, shares
   the returned cookies with the SPA session, deletes **both** token copies, and opens the preload-less
   SPA window. The token is never exposed to the renderer.

## Build

Requirements: .NET 10 SDK, Node + npm, [Inno Setup 6](https://jrsoftware.org/isdl.php) (`ISCC.exe`),
and a PostgreSQL 16 binaries folder (the `pgsql` directory from the EDB zip distribution).

The major version is enforced, not assumed: the build reads it out of `pgsql\bin\postgres.exe` — the
binary, never the path, because EDB's portable zip unpacks to a plain `pgsql` folder with no version
in it, and a path that does carry one is still just a renameable label — and refuses
anything but 16 before it stages a single file. A cluster initialised by one major cannot be opened
by another, and this package upgrades in place over an existing `pgdata` — so a 17.x payload would
compile, sign and ship without a warning, then fail against every installation it reached.

```powershell
./Build-DesktopInstaller.ps1 -PgBinariesPath 'C:\path\to\pgsql' -Version 1.0.0
# -> out\NodePilot-Desktop-Setup-1.0.0.exe   (sign with your Authenticode cert before distribution)
```

The build generates the icons via `scripts/generate-desktop-icons.ps1` (see **Icons** below),
publishes the API self-contained (`-r win-x64 --self-contained true`, no single-file — the PowerShell
SDK is folder-deployed), publishes the operator clients (`np`, `nodepilot-mcp`) self-contained to
`tools\np` and `tools\mcp` (self-contained because the desktop package promises zero prerequisites),
builds the SPA into `app\wwwroot` and the documentation site into `app\wwwroot\docs`, packages the
Electron shell with Electron Packager, stages the Postgres server runtime + scripts, and compiles
the installer.

The documentation is why the package needs no internet to be usable: the API serves it at `/docs`,
and the documentation button at the bottom left of the sidebar, next to the skin and language
controls, opens it. Because the shell has no menu bar and no back gesture, it gets a window of its
own rather than replacing the app view — that window is
pinned to `/docs` for navigations *and* redirects, so it cannot become a second, chrome-less view
of the application. Links that lead out of the documentation are handed to the system browser
(`https:` only); the shell never renders foreign content itself. See `src/security.ts`.

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

### Icons

`scripts/generate-desktop-icons.ps1` renders `src/nodepilot-desktop/assets/` from the SPA's tracked
brand assets (`src/nodepilot-ui/public/appicon-<skin>.png`). The output is gitignored; the sources
are versioned, so a clean clone can always rebuild it.

| Output | Used for |
|---|---|
| `icon.ico` (16/32/48/256) | exe, installer, Start-Menu entry, Explorer |
| `icon.png` / `tray.png` | every window + the tray until the SPA reports its skin |
| `skins\<id>.png` / `<id>-tray.png` | window + tray icon per SPA color skin |

The static default is **blue** — rendered from `appicon-dark.png`, not from the untinted orange
source art `appicon.png` (`-DefaultSkin` picks a different one). At runtime the shell follows the
skin: the SPA rewrites `<link rel="icon">` to `/appicon-<skin>.png` on every skin switch, Chromium
reports that as `page-favicon-updated`, and `src/skins.ts` maps it back onto `skins\<id>.*`. That
keeps the production SPA window preload-less and IPC-free — the shell reads a one-way signal the
renderer already broadcasts. The per-skin set is discovered from the `appicon-*.png` files, so a new
UI skin needs no change here.

The `.exe`/installer/Start-Menu icon cannot follow a skin — Windows resolves those from the file
itself, which is why the shipped default matters.

Running the Electron shell straight from source (`npm start`, see below) starts with an empty
`assets/`: run `npm run icons` in `src/nodepilot-desktop` once to populate it.

## Install / update / uninstall

- **Install:** run the `.exe` as a local administrator (UAC). It lays down files, runs
  `Provision-LocalDb.ps1` (Postgres cluster + service, cert, config, API service, desktop.json, token
  handoff), and launches the shell. Provisioning runs from `CurStepChanged`/`ssPostInstall`, **not**
  from `[Run]`, so its exit code is inspected: a failed run reports an error naming
  `%TEMP%\nodepilot-provision.log` and suppresses the "Launch NodePilot" step, instead of finishing
  green with a dead app. Setup is deliberately not rolled back at that point — the files are already
  in place and a rollback would take the database with it.
- **When something goes wrong:** [`docs/desktop-troubleshooting.md`](../../docs/desktop-troubleshooting.md)
  — log locations, the "setup page never appeared" recovery, port-pool exhaustion, and the uninstall
  ordering trap below.
- **Update:** run a newer installer. On an existing cluster it takes an ACL-protected `pg_dump` first,
  overwrites binaries, and re-provisions. Re-provisioning is repeatable but not side-effect free: step 0
  **stops and deletes** both services and recreates them, and the existing cluster is reused (`initdb`
  is skipped when `pgdata\PG_VERSION` exists), so data survives. `Update-Desktop.ps1` also implements a
  full staged update with binary + config + DB rollback for direct/advanced use. Postgres **major**
  upgrades are out of scope for v1.
- **Uninstall:** removes both services, the certificate, and Program Files. **ProgramData and `pgdata`
  are preserved** unless `-PurgeData` is used. The script lives inside the installation and takes a
  mandatory `-InstallPath`:
  `& 'C:\Program Files\NodePilot\deploy\Uninstall-Desktop.ps1' -InstallPath 'C:\Program Files\NodePilot' -PurgeData`
  **Run the purge before the normal uninstall, not after** — the uninstaller deletes that very
  script, and what is left behind is a `ProgramData\NodePilot` whose ACL excludes the current user.
  The manual way out is in
  [`docs/desktop-troubleshooting.md`](../../docs/desktop-troubleshooting.md#removing-nodepilot-completely).
- **Antivirus:** the installer sets no AV exclusions. Electron's Chromium native DLLs, Postgres' WAL
  I/O and the generated `%TEMP%\nodepilot_*.ps1` scripts are the usual false-positive sources — a
  hand-off list with per-entry rationale and residual risk is in [`docs/av-exclusions.md`](../../docs/av-exclusions.md).

## Files

| File | Role |
|---|---|
| `Build-DesktopInstaller.ps1` | Build orchestrator (icons + publish + SPA + Modules + Electron + operator clients → `tools\{np,mcp}` + PG subset + ISCC). |
| `../../scripts/generate-desktop-icons.ps1` | Icon set from the SPA brand assets (default + per-skin); also runnable standalone. |
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
| Electron shell, icons | `npm run icons` in the same folder — regenerates `assets/` (empty in a fresh clone) | seconds |
| Backend / SPA, normal work | ordinary dev mode (backend on 5000, Vite on 5173 with HMR) | seconds |
| Backend / SPA, **as packaged** | `Sync-DesktopApp.ps1 -Component api\|spa\|shell\|all` (elevated) — incremental publish/build, robocopy into the installation, service restart + health poll | ~1 min |
| Distribution | `Build-DesktopInstaller.ps1` | ~10–15 min |

Use the sync script when the *packaging* matters — service identity is LocalSystem, the DB is the
bundled Postgres, TLS is the pinned loopback cert — none of which dev mode reproduces. It never
mirrors over `app\Modules` or `app\wwwroot`, so the PowerShell modules and SPA stay intact.

The `spa` component syncs two bundles, and the order matters: the SPA mirror runs against
`app\wwwroot` with `/MIR`, which deletes whatever the source lacks, so it excludes
`app\wwwroot\docs` (`/XD`) and the documentation is mirrored separately afterwards. Without that
exclusion every sync would silently remove the documentation, and `/docs` would 404 long after the
cause. `DocsSiteDeploymentTests` guards it.

Quit the installed shell first (tray → *Quit Electron*) before `npm start`: both resolve to the same
`productName`, so the single-instance lock makes the second one focus the first and exit. Shell
changes reach the *installed* app only through a new installer — `app.asar` is not patchable.

## Known gaps (deliberately not covered in v1)

Honest inventory so nobody assumes more coverage than exists:

- **The Electron module's pure logic is unit-tested; its Electron-runtime behaviour is not.**
  `npm run test:run` in `src/nodepilot-desktop` (vitest, node environment) covers `config.ts`
  (desktop.json handoff validation — origin, fingerprint, serviceName injection barrier),
  `security.ts` (certificate-pin match/mismatch/parse-failure, non-loopback rejection, permission
  and download blocking, navigation containment) and `skins.ts` (favicon → skin-icon resolution,
  including the path-charset guard on the renderer-supplied id). What still needs a real Electron
  process — the setup-token IPC guard, the elevated `restartBackend` path, window lifecycle, and
  whether Chromium actually reports the SPA's favicon swap — is verified only by hand. The backend half of the feature *is* unit-tested (`DeploymentModeTests`,
  `DatabaseTlsBootValidatorTests`, `DatabaseReadinessGateTests`, `KestrelHttpsConfiguratorTests`).
- **No CI coverage for `deploy/desktop/*`.** The `desktop` CI job runs `npm audit`, typecheck and
  vitest for `src/nodepilot-desktop`, and the nightly script adds a `desktop-vitest` suite; there is
  still no lint config, and `Test-DeploymentTemplates.ps1` validates the server templates only —
  `appsettings.Desktop.json.template` is never parsed by any check.
- **The vulnerable legacy ZIP extractor is not installed.** Electron Packager 20.3.0 uses Electron's
  hardened native extractor, both are pinned exactly, malicious symlink archives are tested, and a
  final filesystem-boundary gate rejects links/reparse paths before Inno Setup can recursively copy
  the output. The packaged shell still has zero runtime npm dependencies.
- **The installer is unsigned unless you ask for a signature.** `Build-DesktopInstaller.ps1` alone
  never signs. Building through `deploy\Build-Artifact.ps1 -IncludeDesktopInstaller
  -InstallerSigningCertificateThumbprint <tp>` signs it as part of the run — which is where signing
  belongs, because doing it afterwards rewrites the `.exe` and invalidates its entry in
  `NodePilot-<version>.SHA256SUMS.txt`. A self-signed publisher still leaves SmartScreen warning on first launch; only a
  reputation-carrying certificate silences that.
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
