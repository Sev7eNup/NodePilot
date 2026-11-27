# NodePilot Desktop

Offline installer for **Windows 11 x64**, bundling the Electron app, .NET runtime,
PostgreSQL 16, CLI/MCP clients and documentation. The target machine needs no separate
runtime, database or internet connection to install.

The backend and database run as Windows services (`NodePilot`, `NodePilotDb`), so
workflows keep running when the window is closed. For a shared installation, use the
[server deployment](../README.md).

## Build

On Windows, install the .NET 10 SDK, Node.js 22.22.0+ with npm,
[Inno Setup 6](https://jrsoftware.org/isdl.php), and PostgreSQL 16 binaries
(the `pgsql` folder from the EDB distribution).

From the repository root:

```powershell
.\deploy\desktop\Build-DesktopInstaller.ps1 -PgBinariesPath 'C:\path\to\pgsql' -Version 1.0.0
```

Output: `deploy\desktop\out\NodePilot-Desktop-Setup-1.0.0.exe`.
Use `-IsccPath` if the Inno Setup compiler is not detected.

**This command does not sign the installer.** For distribution, follow
[RELEASING.md](../../RELEASING.md): the release build signs before generating checksums.

## Install, update and uninstall

- **Install:** run the installer with administrator rights. On first launch, create
  the administrator account; there is no default username or password.
- **Update:** run a newer installer, or the same version again. Setup backs up the
  database, closes the app and replaces the runtime while keeping accounts and data.
- **Uninstall:** choose *Keep data* or *Delete everything*. Reinstalling with retained
  data keeps the existing accounts. Unattended uninstall keeps data unless
  `/PURGEDATA=1` is supplied.
- **Start fresh with leftover data:** choose to delete it during setup, or use
  `/DISCARDDATA=1` for unattended setup. Both delete options are irreversible.

Open NodePilot from the Start menu. Documentation is available offline through the
sidebar. See the [installation guide](../../src/nodepilot-docs-ui/content/en/deployment/desktop.md)
for verification, backups and migration, or [troubleshooting](../../docs/desktop-troubleshooting.md)
for log locations and setup recovery.

## Development

For the Electron shell, use an existing desktop installation as the backend. Quit
the installed shell through the tray first, then run from the repository root:

```powershell
cd src\nodepilot-desktop
npm ci
npm run icons
npm start
```

For backend or SPA changes in an installed app, run an elevated PowerShell from the
repository root:

```powershell
.\deploy\desktop\Sync-DesktopApp.ps1 -Component spa
```

Use `api` for the backend or `all` for both. Shell changes need a new installer to
reach the installed app. For everyday backend/SPA work, use the normal
[development setup](../../CONTRIBUTING.md); shell commands and tests are in the
[Electron README](../../src/nodepilot-desktop/README.md).

## Limits and further reading

- **Local access only:** the UI and API bind to loopback. Remote clients and inbound
  webhooks from other machines cannot connect; outbound automation still works.
- **Local scripts run as SYSTEM:** the API service uses LocalSystem.
- **Manual updates:** no Electron auto-update or PostgreSQL major-version upgrade. The
  app carries its own .NET runtime, so runtime security fixes arrive only with a new package.

Further reading:

- [Technical reference](../../docs/desktop-internals.md): services, security, packaging and setup lifecycle.
- [Release validation](../../scripts/release-lab/README.md): installation and upgrade matrix.
- [Antivirus guidance](../../docs/av-exclusions.md): exclusions to review when troubleshooting.
