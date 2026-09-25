# Release lab: install matrix for the server and desktop setups

Every release runs this matrix **before it is published** (see `RELEASING.md`, step 6). It installs the
signed setups from the release build unattended on real Windows machines, the way an operator or a
software-distribution tool would, and fails the release if any path leaves an error behind.

It exists because the unit and contract tests stop at the scripts: until 1.4.1 no PostgreSQL server
installation could complete, LocalSystem against a local SQL Server rolled back, and the bundled
`psql` was never extracted — all green in CI, all found here.

## What is checked

### Server setup (`server/Invoke-ServerMatrix.ps1`, lab server VM)

Each scenario starts from the clean checkpoint.

| Id | Identity | Database | Path | Then |
|---|---|---|---|---|
| F1 | gMSA | SQL Server | fresh install | uninstall keeping data, install again over it |
| F2 | LocalSystem | SQL Server | fresh install | uninstall with `/PURGEDATA=1` |
| F3 | gMSA | PostgreSQL | fresh install | uninstall keeping data |
| F4 | LocalSystem | PostgreSQL, port omitted | fresh install | uninstall with `/PURGEDATA=1` |
| U1 | gMSA | SQL Server | previous release, then update | uninstall with `/PURGEDATA=1` |
| U2 | LocalSystem | SQL Server | previous release, then update | uninstall keeping data |
| U3 | gMSA | PostgreSQL | previous release, then update | uninstall with `/PURGEDATA=1` |
| U4 | LocalSystem | PostgreSQL | previous release, then update | uninstall keeping data |
| I1 | LocalSystem, then gMSA | SQL Server | fresh install | a second installation as the gMSA over it |
| S1 | LocalSystem | SQL Server on another host | fresh install | uninstall with `/PURGEDATA=1` |
| R1 | gMSA | SQL Server | fresh install | the VM restarts |
| A | gMSA | SQL Server | fresh install | database stopped, service started, database back |
| B | gMSA | PostgreSQL, CRL removed | fresh install | the pre-flight must refuse |
| P1 | gMSA | SQL Server | fresh install | machine policy `AllSigned`, then every local PowerShell path runs |

**Pass** for an install or update: setup exit 0, `/healthz/ready` 200, the bootstrap admin signs in,
a workflow (manual trigger + log step) runs to *Succeeded*, and the service log has **no error
entry** (CMTrace `type="3"`) for two minutes after the setup finished. On an update the count starts
at the new service's `NodePilot.Api started` line; what the previous release logged before is
reported as `errorsBeforeNewStart`.

**Pass** for an uninstall: service, program directory, installation marker, Apps & Features entry and
the product's firewall rules are gone; the database is still there; the data directory is kept, or gone
with `/PURGEDATA=1`. The reinstall in F1 must accept the kept data: no new admin, the old one signs
in, the imported workflow is still there.

**I1** passes when the second installation runs as the gMSA, the signing key's owner moved to the
gMSA, the existing admin still signs in and no error entry appears. **S1** covers the
computer-account path (`DOMAIN\HOST$`), which only a SQL Server on another host sees; it needs the
optional `remoteSql` block in the config and reports N/A without it. That server is not restored by a
checkpoint, so the scenario refuses an existing database of the configured name and afterwards drops
its database, and the login only if it created it. **R1** passes when, after a restart, the service
comes up on its own (SQL Server starts delayed, so NodePilot boots before its database), becomes
ready within five minutes, and logs no error entry.

**A** passes when `Start-Service` returns within 30 s while the database is down, the wait line
names the connection error, and the service becomes ready on its own once the database is back,
without an error entry. **B** passes when the setup refuses before installing anything and says the
certificate's revocation status could not be checked.

**P1** sets the machine execution policy to `AllSigned` the way a GPO does, then runs
`allsigned-workflow.json` instead of the smoke workflow: a script step in the default Windows
PowerShell process, an isolated one, one in the in-process pool, a `waitForCondition` script, a
`startProgram` that starts Windows PowerShell, and a built-in activity. It passes like an install:
the run *Succeeded* and no error entry. A failed run lists its failed steps in `failedSteps`.

### Desktop setup (`desktop/Invoke-DesktopMatrix.ps1`, lab client VM)

| Step | What happens | Checked |
|---|---|---|
| T1 | fresh install | services, `/healthz/ready`, certificate, first admin via the setup token, a workflow runs, no error entry for two minutes |
| T1b | the VM restarts | services come up on their own, ready within five minutes, admin signs in, no error entry |
| T2 | same setup again with the shell open | services, admin still signs in, pre-update backup, no file-in-use errors, no error entry |
| T3 | uninstall, keep data | services, certificate and private key, program files, Apps & Features entry gone; database kept |
| T4 | install again | old admin signs in, no setup page, a workflow runs, no error entry |
| T5 | uninstall, `/PURGEDATA=1` | additionally the data directory and every per-user NodePilot folder gone |
| T6 | install after the purge | setup token again, the old admin is gone |
| T7 | uninstall keeping data, install with `/DISCARDDATA=1` | fresh database, the previous admin is gone |
| T8 | uninstall, `/PURGEDATA=1` | nothing left |
| U1 | from the clean checkpoint: the previous release, an admin and a workflow, then this release over it | the admin signs in, pre-update backup, a workflow runs, no error entry; uninstall with `/PURGEDATA=1` |

Interactive pages (uninstall question, leftover-data page, the wizard itself) are not reachable over
PowerShell Direct and are not covered.

## Running it

```powershell
# once: copy the example, fill in the lab values; keep it outside the repository
Copy-Item scripts\release-lab\release-lab.example.json C:\lab-cred\release-lab.json

# after deploy\Build-Artifact.ps1 has written .\out
.\scripts\release-lab\server\Invoke-ServerMatrix.ps1  -ConfigPath C:\lab-cred\release-lab.json -ArtifactDir .\out -Version 1.4.1
.\scripts\release-lab\desktop\Invoke-DesktopMatrix.ps1 -ConfigPath C:\lab-cred\release-lab.json -ArtifactDir .\out -Version 1.4.1
```

Both need an elevated session on the Hyper-V host (checkpoint restore) and run independently, so
they can run at the same time. The server matrix takes about 1.5 h (fourteen VM restores and
installs, each with a two-minute observation window), the desktop run about an hour. Results, logs
and a `summary.md` land in `%TEMP%\nodepilot-release-lab\<version>\`. Either script exits 1 if
anything failed.

`-Only F1,A` runs a subset while fixing something; the release itself needs the full run.

## Lab prerequisites

- **Server VM**: domain member, SQL Server 2022 CU1+ (default instance), PostgreSQL 16 with TLS on
  the same host, a gMSA the host may retrieve, the Kestrel certificate in `LocalMachine\My`, no .NET
  and no NodePilot. The checkpoint must be taken **with the VM off** — online checkpoints in this lab
  could not be restored.
- **PostgreSQL TLS**: the server certificate needs a CRL the service can check (the service verifies
  revocation). In the lab the CA's CRL is imported into the server VM's `LocalMachine\CA` store;
  scenario B removes it to prove the pre-flight notices.
- **Client VM**: Windows 11, no .NET, no PostgreSQL, clean checkpoint.
- **Remote SQL Server** (optional, for S1): another lab host with SQL Server 2022 CU1+ on which the
  account running the matrix is sysadmin. If its TLS certificate is self-signed, export the public
  part and name it in `remoteSql.trustCertificate`; the matrix imports it into the server VM's
  `LocalMachine\Root` after each restore, so the checkpoint stays untouched.
- **Previous release**: the server and desktop setups of the release before this one, for U1-U4 and
  the desktop U1. The `previousRelease` flags in the config describe what that release needs; for 1.4.0 the runtimes
  have to be preinstalled, LocalSystem needs `NT AUTHORITY\SYSTEM` granted by hand, and PostgreSQL
  installs cannot complete (U3/U4 report N/A).

## Known lab quirks

- `curl.exe` (Schannel) and `Invoke-WebRequest` fail against Kestrel on the server VM, a raw
  `SslStream` works; the server scripts therefore speak HTTP/1.1 by hand.
- SQL Server starts delayed-automatic; the scripts wait for it before installing.
- `unins000.exe` relaunches itself from `%TEMP%` and returns at once; the scripts wait for the `_iu*`
  process.
- `Start-Process -PassThru` reports no exit code inside a PowerShell Direct session; the scripts use
  `System.Diagnostics.Process`.
