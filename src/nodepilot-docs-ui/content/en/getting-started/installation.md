# Installation

NodePilot can be installed as a desktop app, as a Windows service, or directly from source. Which one you choose depends on whether you need a local single-machine setup, a central team server, or a development environment.

## Choosing an installation type

| Requirement | Installation | Result |
|---|---|---|
| Use NodePilot locally on a Windows 11 system | **Desktop app** | An Electron app with its own PostgreSQL database and background services |
| Run NodePilot centrally for several people | **Windows Server** | A Windows service with HTTPS and an external database |
| Develop NodePilot or test it from source | **Installation from source** | PostgreSQL, the API and the React interface as separate development processes |

The desktop app is intended as the fastest way in locally. For network access, webhooks, central sign-in or high availability, the Windows Server deployment is required.

## Variant 1: desktop app

The desktop app sets up every required component on a Windows 11 x64 system:

- The NodePilot API as a Windows service
- PostgreSQL 16 as a local Windows service
- The product interface in an Electron shell
- A local HTTPS connection with certificate pinning

Access is only possible on the machine it is installed on. Inbound webhooks, external API clients, central sign-in and high availability are not available in this operating mode.

### Obtaining the installer

`NodePilot-Desktop-Setup-<version>.exe` is an asset on the [latest release](https://github.com/Sev7eNup/NodePilot/releases/latest). Download it, verify it against `NodePilot-<version>.SHA256SUMS.txt` and run it — the installer sets up the database, the certificate and both services, and hands the setup token straight to the sign-in form.

When the downloaded installer is started, SmartScreen reports "Windows protected your PC" — expected, because the file carries a Mark of the Web and the signing certificate is self-signed. What to do: [First launch: the blue SmartScreen window](../deployment/production#first-launch-the-blue-smartscreen-window).

Alternatively, build it yourself: `deploy\desktop\Build-DesktopInstaller.ps1` additionally requires Inno Setup 6 and the PostgreSQL 16 binaries. A self-built `.exe` is **unsigned** — SmartScreen flags it too, as soon as it reaches the target machine through a download.

Build prerequisites, the full command, installation, update and uninstall are under [Desktop app](../deployment/desktop).

## Variant 2: Windows Server

The Windows Server deployment is intended for central production use.

Supported combinations:

- SQL Server 2022 or PostgreSQL 16+
- LocalSystem or a gMSA as the service identity
- Single node or an active/passive cluster
- Kestrel HTTPS with a certificate from `LocalMachine\My`

There are two routes to the same installation.

### GUI setup

`NodePilot-Server-Setup-<version>.exe` is an asset on the [latest release](https://github.com/Sev7eNup/NodePilot/releases/latest). It brings the signed artifact and the ASP.NET Core runtime with it and checks every prerequisite **before** it changes anything. On request it creates the SQL login and database, or the PostgreSQL role and database, itself; the Kestrel certificate is picked from a list of the certificates in `Cert:\LocalMachine\My` instead of being typed in as a thumbprint. Unattended, for SCCM or GPO: `NodePilot-Server-Setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /ANSWERFILE=answers.json`.

This is the shortest route: one file instead of five, and no manual comparison of the publisher thumbprint.

### PowerShell scripts

The same thing the setup runs, and the more direct route for automation: the repository produces a signed ZIP artifact, and `deploy\Install-NodePilot.ps1` installs the Windows service from it, sets ACLs and firewall rules, and checks the health endpoint.

Prerequisites and the complete installation commands for both routes are under [Windows Server deployment](../deployment/production).

## Variant 3: installation from source

This variant starts the database, the backend and the product interface separately, and is meant for development and technical testing. For permanent production use, the desktop app or Windows Server are the intended options.

### Result

Once finished, the following components are running:

| Component | Address |
|---|---|
| PostgreSQL | `127.0.0.1:5432` |
| NodePilot API | `http://localhost:5000` |
| Product interface | `http://localhost:5173` |

The product interface forwards API, health and SignalR calls to port 5000.

### Prerequisites

- Windows
- Git
- .NET 10 SDK — the accepted SDK band is in `global.json`
- Node.js — the minimum version is declared in the `engines` field of the `package.json` files (react-router 8 sets the floor); `npm` warns on an older version
- PostgreSQL 16 or newer
- Local administrator rights to install the prerequisites

Example installation with `winget`:

```powershell
winget install Microsoft.DotNet.SDK.10
winget install OpenJS.NodeJS.LTS
winget install PostgreSQL.PostgreSQL
```

Verification:

```powershell
git --version
dotnet --version
node --version
npm --version
& "C:\Program Files\PostgreSQL\16\bin\psql.exe" --version
```

If a package ID is unavailable, `winget search <name>` can find the current one. Alternatively, the installation packages are available from the respective vendors.

### 1. Get the repository

```powershell
git clone https://github.com/Sev7eNup/NodePilot.git
Set-Location NodePilot
```

All further commands use the repository root as their starting point.

### 2. Create the PostgreSQL database

A development user and an empty database are created once:

```powershell
$pgClient = "C:\Program Files\PostgreSQL\16\bin\psql.exe"
& $pgClient -U postgres -c "CREATE ROLE nodepilot WITH LOGIN PASSWORD 'ChangeMe!';"
& $pgClient -U postgres -c "CREATE DATABASE nodepilot OWNER nodepilot;"
```

`ChangeMe!` is purely a local example value. Shared or reachable databases require a strong password of your own.

After a default installation, PostgreSQL runs as a Windows service. The service status can be checked like this:

```powershell
Get-Service -Name "postgresql*"
```

### 3. Configure the database connection

The connection string is set in the terminal that then starts the backend:

```powershell
$env:ConnectionStrings__Postgres = "Host=127.0.0.1;Port=5432;Database=nodepilot;Username=nodepilot;Password=ChangeMe!"
```

The double underscore maps to the .NET configuration key `ConnectionStrings:Postgres`. The environment variable applies only to the current terminal and avoids putting a password into a repository file.

### 4. Start the backend

In the same terminal:

```powershell
Set-Location src\NodePilot.Api
dotnet run --urls "http://localhost:5000"
```

The first start performs a package restore, a build and the database migrations. The API is ready as soon as this message appears:

```text
Now listening on: http://localhost:5000
```

Health check in a second terminal:

```powershell
Invoke-RestMethod http://localhost:5000/healthz/live
Invoke-RestMethod http://localhost:5000/healthz/ready
```

`live` confirms the process is running. `ready` additionally confirms the database is reachable.

### 5. Start the product interface

In a second terminal, from the repository root:

```powershell
Set-Location src\nodepilot-ui
npm install
npm run dev
```

`npm install` has to be repeated after changes to `package-lock.json`. Vite starts on `http://localhost:5173` by default.

### 6. Create the first admin account

1. Open `http://localhost:5173` in a browser.
2. Enter the user name and password you want and sign in — on the first attempt the login page reveals a **setup token field**.
3. Paste the token from `src\NodePilot.Api\admin-setup.token` and sign in again.

With an empty database, the backend creates that token file at startup. After a successful setup it is deleted. There is no preconfigured account.

The next step is the [Quick start](./quickstart).

### Stopping and restarting

- Frontend: `Ctrl+C` in the Vite terminal
- Backend: `Ctrl+C` in the API terminal
- Restart: check PostgreSQL first, then start the backend and the frontend

The data stays in PostgreSQL.

### Troubleshooting

| Symptom | Check | Fix |
|---|---|---|
| The backend exits at startup | `/healthz/live` is unreachable; the log contains database errors | Check the PostgreSQL service and the connection string |
| `password authentication failed` | The password in `ConnectionStrings__Postgres` does not match the role | Correct the password, or change the role in PostgreSQL |
| Port 5000 is in use | `Get-NetTCPConnection -LocalPort 5000` | Terminate the occupying process or configure a different API port |
| `MSB3027` during the build | A running API process is holding a DLL open | Stop the API and run the build again |
| Port 5173 is in use | `Get-NetTCPConnection -LocalPort 5173` | Terminate the process, or use the replacement port Vite reports |
| Frontend dependencies are missing | `npm run dev` reports missing modules | Run `npm install` again |

### Limits of the source installation

The source installation has no Windows service, no autostart and no production TLS configuration. For production systems, the two installation variants described above are available:

- [Windows Server deployment](../deployment/production) for team access, APIs, webhooks and high availability
- [Desktop app](../deployment/desktop) for a local single machine

The comparison is under [Operating modes](../deployment/overview).
