# Desktop app

The desktop app is a local NodePilot package for Windows 11 x64. One installer sets up the API, the product interface, the Electron shell and PostgreSQL. External runtimes, an external database and internet access are not required during installation.

## Where it fits

Suitable for:

- Productive automation on a single Windows system
- Local schedule, file, database and event-log triggers
- Outbound WinRM, REST, SQL, SMTP and alerting connections
- Offline installation

Not suitable for:

- Access from other systems
- Inbound webhooks
- An external REST or trigger API
- LDAP, Windows SSO, OIDC or SCIM
- High availability

## Runtime architecture

```text
Electron shell
      |
      | HTTPS on localhost, certificate pinned
      v
Windows service "NodePilot"      LocalSystem
      |
      v
Windows service "NodePilotDb"    NetworkService
      |
      v
PostgreSQL 16 on 127.0.0.1
```

The services start at system startup. Closing the Electron window stops no workflows or triggers.

If `NodePilotDb` stops or hangs during operation, the API stays reachable and the interface shows a
database outage. Database accesses answer quickly with 503, workflows wait at durable step
boundaries, and operation resumes automatically once the PostgreSQL check succeeds. Diagnosis:
`/healthz/ready` and `/healthz/database`. Details and timeout settings:
[Database providers](../configuration/database).

The window and tray icons follow the colour of the skin chosen in the interface. The icon of the executable, the installer and the Start-menu entry stays the blue default, because Windows resolves those icons from the file itself.

## Installed paths

| Path | Contents |
|---|---|
| `C:\Program Files\NodePilot\app` | The self-contained API and product interface |
| `C:\Program Files\NodePilot\desktop` | The Electron shell |
| `C:\Program Files\NodePilot\pgsql` | The PostgreSQL server runtime |
| `C:\Program Files\NodePilot\tools\np` | The `np` operations CLI (the desktop package does not add it to `PATH`) |
| `C:\Program Files\NodePilot\tools\mcp` | `nodepilot-mcp` — the MCP server for AI agents |
| `C:\ProgramData\NodePilot\pgdata` | PostgreSQL data |
| `C:\ProgramData\NodePilot\logs` | Application logs |
| `C:\ProgramData\NodePilot\backups` | Update backups |
| `C:\ProgramData\NodePilot\desktop.json` | The link between the shell and the backend |

## Security model

`Deployment:Mode=Desktop` applies the following rules:

- Kestrel binds to loopback only.
- No inbound firewall rule is created.
- PostgreSQL binds to `127.0.0.1` only.
- A self-signed certificate protects the local HTTPS connection.
- Electron verifies the certificate's SHA-256 fingerprint.
- The certificate is not installed as a global root CA.
- Electron uses `contextIsolation`, `sandbox` and `webSecurity`.
- Node integration, a preload bridge, external navigation, pop-ups, downloads and permission requests are disabled.

An ordinary browser may show a certificate warning for the local URL. The supported access route is the Electron shell.

## Permissions for local and remote execution

The API runs as LocalSystem. Two distinct cases follow from that:

| Activity target | Identity |
|---|---|
| A local `runScript` activity without a machine | `NT AUTHORITY\SYSTEM` |
| Remote execution with a machine | The stored credential |

Credential-less Kerberos delegation is not provided for in desktop mode.

## Installer availability

`NodePilot-Desktop-Setup-<version>.exe` is an asset on the [latest release](https://github.com/Sev7eNup/NodePilot/releases/latest); the matching checksums are in `NodePilot-<version>.SHA256SUMS.txt`.

Even the signed installer triggers SmartScreen on first launch once it has been downloaded — the certificate is self-signed and carries no reputation. Explanation and procedure: [First launch: the blue SmartScreen window](./production#first-launch-the-blue-smartscreen-window).

The installer also remains a build target of the repository — the section below describes how to produce it yourself. A self-built `.exe` is additionally **unsigned** until it is signed with your own Authenticode certificate.

## Building the installer

### Prerequisites on the build system

- .NET 10 SDK
- Node.js and npm
- Inno Setup 6 with `ISCC.exe`
- A PostgreSQL 16 binaries directory `pgsql`
- An Authenticode certificate for distribution

Build from `deploy\desktop`:

```powershell
Set-Location deploy\desktop
.\Build-DesktopInstaller.ps1 `
  -PgBinariesPath "C:\Packages\pgsql" `
  -Version <version>
```

Result:

```text
out\NodePilot-Desktop-Setup-<version>.exe
```

The build:

1. publishes the API self-contained for `win-x64`,
2. publishes the operator clients (`np`, `nodepilot-mcp`) self-contained to `tools\np` and `tools\mcp`,
3. builds the React interface,
4. copies the required PowerShell modules,
5. packages the Electron shell,
6. takes over the required part of PostgreSQL,
7. produces the Inno Setup installer.

The resulting installer has to be Authenticode-signed before distribution.

## Installation

1. Transfer the signed installer to the Windows 11 target system.
2. Start the installer and confirm the UAC prompt.
3. Let the provisioning finish completely.
4. Start the Electron shell.
5. Create a local admin account in the setup dialog.

The installer:

- installs the files,
- initializes PostgreSQL,
- registers `NodePilotDb` and `NodePilot`,
- creates the loopback certificate,
- writes the production configuration,
- sets the ACLs,
- creates `desktop.json`,
- hands the one-time setup token to the Electron shell in a protected way.

The setup token goes to the profile of the **interactive** user — that is, the user the shell then
runs as. That is not necessarily the user who runs the installer: the installer runs elevated, and if
a standard user enters the credentials of a *different* administrator account at the UAC prompt,
those are two different profiles. The installer therefore resolves the interactive user explicitly
instead of assuming its own profile.

If the provisioning fails, the installer reports it with a pointer to its log at
`%TEMP%\nodepilot-provision.log` and does not start the shell — rather than reporting a successful
installation that then does not start. Causes and remedies:
[Desktop troubleshooting](https://github.com/Sev7eNup/NodePilot/blob/main/docs/desktop-troubleshooting.md).

## Verifying the installation

```powershell
Get-Service NodePilotDb, NodePilot
Get-Content "$env:ProgramData\NodePilot\desktop.json"
```

Expected results:

| Check | Expectation |
|---|---|
| `NodePilotDb` | `Running` |
| `NodePilot` | `Running` |
| The Electron shell | The product interface with no certificate dialog |
| Admin setup | A local account can be created |
| Reboot | Both services start automatically |

The origin from `desktop.json` can be checked locally:

```powershell
$desktop = Get-Content "$env:ProgramData\NodePilot\desktop.json" | ConvertFrom-Json
Invoke-WebRequest "$($desktop.origin)/healthz/ready" -SkipCertificateCheck
```

`-SkipCertificateCheck` is available in PowerShell 7 and is intended here only for local diagnosis of the self-signed certificate that Electron pins.

## Update

A new signed installer can be run over the existing installation.

The update sequence:

1. Create an ACL-protected `pg_dump`.
2. Stop the services.
3. Replace the binaries.
4. Reuse the existing PostgreSQL data directory.
5. Re-provision the services.
6. Check the health endpoint.

`Update-Desktop.ps1` additionally offers a staged update with rollback for binaries, configuration and database.

PostgreSQL major upgrades and Electron auto-update are not part of the current desktop version.

## Uninstalling

The normal uninstall removes:

- both Windows services,
- the loopback certificate,
- the files under `C:\Program Files\NodePilot`.

The data under `C:\ProgramData\NodePilot`, including `pgdata`, is preserved by default.

For complete removal:

The script lives inside the installation, not in the current directory, and `-InstallPath` is a
mandatory parameter:

```powershell
& 'C:\Program Files\NodePilot\deploy\Uninstall-Desktop.ps1' `
    -InstallPath 'C:\Program Files\NodePilot' -PurgeData
```

`-PurgeData` deletes the local database and cannot be undone. A backup is required beforehand.

**The order matters: this script first, then the normal uninstall.** The normal uninstall deletes the
script itself. Running it first leaves you with a `C:\ProgramData\NodePilot` whose permissions
exclude your own account, and no script left to remove it. The manual route is in the
[Desktop troubleshooting](https://github.com/Sev7eNup/NodePilot/blob/main/docs/desktop-troubleshooting.md) guide.

## Backup and moving systems

To protect the configuration:

1. Create a system configuration backup in NodePilot.
2. Store the backup file and the passphrase separately.
3. For a complete history, also back up PostgreSQL.

Copying `pgdata` to another machine is not a supported migration route. With DPAPI in use, credentials are bound to the machine. The supported move uses the system configuration backup, so that secrets are re-encrypted on the target system.

## Known limits

- The installer has to be signed as part of the release process.
- PostgreSQL major upgrades are not automated.
- Electron has no auto-updater.
- Installation and rollback need a test on a clean Windows 11 VM.
- Desktop mode is deliberately limited to a local single machine.

The complete build and validation details are in `deploy\desktop\README.md`.
