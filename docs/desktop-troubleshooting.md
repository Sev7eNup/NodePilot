# NodePilot Desktop — Troubleshooting

For the desktop app (`NodePilot-Desktop-Setup-<version>.exe`). The server rollout has its own
troubleshooting section in [deployment-guide.md](deployment-guide.md#troubleshooting) — that guide
is written for the Windows-service install and its steps do not apply here.

## Where things are

| What | Path |
|---|---|
| Installation log (one run of the installer) | `%TEMP%\nodepilot-provision.log` |
| Application logs | `C:\ProgramData\NodePilot\logs` |
| Database cluster | `C:\ProgramData\NodePilot\pgdata` |
| Shell configuration (origin + pinned certificate) | `C:\ProgramData\NodePilot\desktop.json` |
| Services | `NodePilot` (API) and `NodePilotDb` (PostgreSQL) |

Two notes that cost people time:

- **`C:\ProgramData\NodePilot` is readable by administrators only.** Opening it from Explorer as a
  standard user gives "access denied" — that is the intended ACL, not damage. Use an elevated
  terminal.
- **`%TEMP%` is the temp folder of whoever the installer ran as.** If you elevated with a different
  administrator account, the log is in *that* account's temp directory, not yours.

## The app opens a login form instead of "create administrator"

On a first install NodePilot should show a local setup page that creates the first administrator.
It appears because the installer leaves a one-shot token at
`%LOCALAPPDATA%\NodePilot\admin-setup.handoff` for the user the app runs as.

**Before 1.2.11 this could land in the wrong profile.** The installer runs elevated; if a standard
user started it and entered a *different* administrator's credentials at the UAC prompt, the token
went to the administrator's profile while the app ran as the standard user, which never found it.
The installer now resolves the interactive user explicitly, so a fresh install of 1.2.11 or later
does not hit this.

If you are on an affected installation, recover it in one of two ways.

**Option A — reinstall (simplest).** The setup token only exists while no user account exists, so
re-running the installer of 1.2.11 or later puts the handoff in the right place. Nothing is lost:
the database is preserved across a reinstall.

**Option B — read the token by hand.** In an **elevated** PowerShell:

```powershell
$token = 'C:\ProgramData\NodePilot\admin-setup.token'
takeown.exe /f $token /a
icacls.exe $token /grant '*S-1-5-32-544:(R)'
Get-Content $token
```

Then sign in with the username and password you want, and paste that value into the **Setup token**
field the login page reveals on the first attempt.

> **Do not loosen the permissions any further than the two commands above.** The backend validates
> the token file's ACL before it accepts the value, and it only trusts SYSTEM, Administrators,
> TrustedInstaller and CreatorOwner. Granting your own user account or `Everyone` access makes the
> file fail that check, and **every subsequent token entry is rejected** — including the correct
> one. If that has already happened, Option A is the way out.

## "Setup completed" but the app does not start

Installers of 1.2.11 and later report a failed provisioning with a dialog naming the log. Older
ones reported success regardless, so an app that never starts after an older installer usually
means provisioning failed silently.

Read `%TEMP%\nodepilot-provision.log` — it is a full transcript of the run and ends at the failing
step. The common causes:

| In the log | Cause | Fix |
|---|---|---|
| `No free port available in range 47000-47049` (or `47100-47149`) | Every port in the pool is taken | Free some, or find the offender with `Get-NetTCPConnection -LocalPort 47000..47049` |
| `Re-install with a clean DataPath` | `pgdata` exists but `secrets\pg-superuser.secret` is gone, so the cluster's password is unrecoverable | Back up `C:\ProgramData\NodePilot\pgdata` if you need the data, then remove `C:\ProgramData\NodePilot` and reinstall |
| `API did not report /healthz/ready within the timeout` | The API service started but did not become healthy | Check `C:\ProgramData\NodePilot\logs`; the first start migrates the database and is the slowest one |
| `Required path not found` | The installation is incomplete | Reinstall |

## The window says the backend did not become ready

The shell waits 240 seconds for the API. The first start after an install is the slow one — it
creates the schema against a brand-new cluster.

1. Close the window and start NodePilot again. If the service simply needed longer, this is enough.
2. Otherwise check the services:

```powershell
Get-Service NodePilot, NodePilotDb
```

`NodePilotDb` must be running before `NodePilot` can be. If `NodePilotDb` is stopped, start it and
then start `NodePilot`. If the API is running but unhealthy, the reason is in
`C:\ProgramData\NodePilot\logs`.

While the database is down the API deliberately stays up and answers `503 DATABASE_UNAVAILABLE`
rather than hanging, so a NodePilot window that loads and then reports database errors means the
`NodePilotDb` service — not the app.

## The browser warns about the certificate

Expected. NodePilot terminates HTTPS on a loopback certificate that is **pinned by SHA-256 in the
Electron shell**, deliberately not installed into a system trust store. The shell trusts it; an
ordinary browser pointed at the same URL has no reason to, and will warn.

## Removing NodePilot completely

The uninstaller in "Apps & features" removes the services, the certificate and the program files —
but **keeps** `C:\ProgramData\NodePilot`, including your database. That is on purpose: an uninstall
is not meant to destroy data.

To remove the data as well, run the purge **before** uninstalling:

```powershell
# elevated
& 'C:\Program Files\NodePilot\deploy\Uninstall-Desktop.ps1' -InstallPath 'C:\Program Files\NodePilot' -PurgeData
```

**The ordering matters.** That script lives under the installation directory, and the normal
uninstall deletes it. Running the uninstaller first leaves you with a `C:\ProgramData\NodePilot`
whose ACL excludes your own account and no script left to remove it. If that already happened, take
ownership and delete it by hand in an elevated shell:

```powershell
takeown.exe /f 'C:\ProgramData\NodePilot' /r /a
icacls.exe 'C:\ProgramData\NodePilot' /grant '*S-1-5-32-544:(F)' /t
Remove-Item 'C:\ProgramData\NodePilot' -Recurse -Force
```

## Antivirus

The installer sets no antivirus exclusions. NodePilot starts PowerShell child processes and runs
generated scripts out of `%TEMP%`, which endpoint protection may block. The hand-off document for a
security team is [av-exclusions.md](av-exclusions.md).

Note that this is separate from the blue **"Windows protected your PC"** dialog on first launch of
a downloaded installer — that is SmartScreen, which ignores antivirus exclusion lists entirely. See
[First run: the SmartScreen prompt](deployment-guide.md#first-run-the-smartscreen-prompt).

## Still stuck

Open an issue at [github.com/Sev7eNup/NodePilot/issues](https://github.com/Sev7eNup/NodePilot/issues)
with the version, the last ~50 lines of `%TEMP%\nodepilot-provision.log`, and the output of
`Get-Service NodePilot, NodePilotDb`. For anything security-relevant use the private channel in
[SECURITY.md](../SECURITY.md) instead.
