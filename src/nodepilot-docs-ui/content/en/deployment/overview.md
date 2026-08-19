# Operating modes

NodePilot supports three operating modes. The choice determines installation, network access, database, service account and the available enterprise features. The workflow engine and the workflow format stay the same.

## Choosing in a minute

| Requirement | Suitable operating mode |
|---|---|
| Development on the NodePilot source code | **Installation from source** |
| Local automation on a single Windows 11 machine | **Desktop app** |
| Access by several people | **Windows Server deployment** |
| Inbound webhooks or external REST calls | **Windows Server deployment** |
| LDAP, Windows SSO, OIDC or SCIM | **Windows Server deployment** |
| Active/passive high availability | **Windows Server deployment** |

## Technical comparison

| Aspect | Source installation | Windows Server | Desktop app |
|---|---|---|---|
| Purpose | Development and testing | Production team use | Production single machine |
| Operating system | Windows | Windows Server 2022/2025 | Windows 11 x64 |
| Installation | Source, manual processes | A signed Inno Setup installer **or** a signed ZIP with PowerShell scripts | A signed Inno Setup installer |
| Backend | `dotnet run` | A Windows service | A Windows service |
| Interface | The Vite dev server | An SPA served by the backend | An SPA served by the backend, inside Electron |
| Database | Local PostgreSQL | External SQL Server 2022 or PostgreSQL 16+ | Bundled PostgreSQL 16 |
| Network | Local development ports | HTTPS on the network | Loopback only |
| Service account | The interactive user | LocalSystem or a gMSA | LocalSystem |
| TLS | Optional in local testing | A certificate from `LocalMachine\My` | A self-signed loopback certificate with pinning |
| Inbound webhooks and the external trigger API | Testable locally | Supported | Not reachable |
| Schedule, file, database and event-log triggers | While the development processes are running | Available | Available |
| Outbound connections, for example WinRM, REST, SQL and SMTP | Available | Available | Available |
| Operation without the interface open | Only while the development processes are running | Yes | Yes |
| Enterprise authentication | Technically testable | The supported target path | Not available |
| High availability | No | Active/passive possible | No |
| Guide | [Installation](../getting-started/installation) | [Windows Server deployment](./production) | [Desktop app](./desktop) |

For the desktop app, **loopback only** concerns inbound connections exclusively. Automatic triggers and outbound connections remain available. Closing the window does not stop running workflows. Local `runScript` activities run as `LocalSystem`; remote WinRM requires stored credentials.

## Unsupported deployment shapes

Linux, containers, Kubernetes, Helm, systemd, IIS hosting and cloud-specific managed-app packages are not supported production targets. The supported server target is a Windows service with direct Kestrel HTTPS. The documented active/passive scenario uses a load balancer in front.

## Changing operating mode

A change is done through a system configuration backup: create a backup, reinstall the target system, restore the backup and verify the target system. Moving an installation directory directly is not supported.

The backup contains configuration, workflows and encrypted secrets. Execution history, audit data and statistics additionally require a native database backup. Details are in [Import, export and backup](../import-export).
