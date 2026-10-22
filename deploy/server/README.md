# NodePilot Server Setup (`NodePilot-Server-Setup-<version>.exe`)

GUI installer for the **server installation** (Windows service). It does not replace the ZIP route —
it is a second way to the same installation. `deploy/Install-NodePilot.ps1` remains usable unchanged
and is still the reference; the setup calls exactly that script.

> For **a single machine with no network access**, the desktop app is the faster route — see
> [`../desktop/README.md`](../desktop/README.md). It binds loopback only.

The server artifact also contains the self-contained WPF **NodePilot Switcher** under
`tools\switcher`; the GUI installer adds its Start-menu shortcut. It controls only the
validated local NodePilot service and the exact SCOrch allowlist `omanagement`, `oremoting`,
`omonitor`, and `orunbook`. Database services are never included. See
[`docs/switcher.md`](../../docs/switcher.md) for its fail-closed behavior and the
required absolute local or UNC workflow/runbook allowlist paths.

## Quick start

The rest of this file explains **why** things are the way they are. This section is **what you do**.

### Get two things ready first

**1. The HTTPS certificate**, either as a PFX to import in the wizard or already in the machine store:

```powershell
Import-PfxCertificate -FilePath cert.pfx -CertStoreLocation Cert:\LocalMachine\My `
  -Password (Read-Host -AsSecureString)
```

`MachineKeySet|PersistKeySet` is the default for this call and is mandatory — without a persisted
machine key, `Grant-CertPrivateKeyAccess` cannot find the key file later and the installation aborts.
Valid, and issued for the public hostname: an expired certificate is an amber row that does not stop
the installation, a mismatched name is a warning too.

If you **do not have one yet**, leave the thumbprint field empty: the readiness page then reports
"No certificate selected" and offers to create a self-signed one — for lab and pilot use, not for
production. Unattended, this is the same case: an empty `certificate.thumbprint` plus
`"provisioning": { "generateSelfSignedCertificate": true }`.

**2. A database server** that is reachable and whose **TLS the NodePilot host can verify** — for a
self-signed certificate that means importing its public half into `LocalMachine\Root` on the NodePilot
server. SQL Server must be **2022 CU1** (`16.0.4003.1`) or newer. For PostgreSQL, also have the
**root CA as a PEM file** ready.

**The setup creates the database, the login and the permissions** — on SQL Server if the account
running it is `sysadmin`; on PostgreSQL if superuser credentials are supplied. That is no longer
preparatory work, see [Auto-fixes](#auto-fixes).

Only on the gMSA path: create the account in AD and allow the host to retrieve its password.
The setup can install AD tools and prepare the existing account locally. Access to the certificate's
private key is handled by the installer.

### The wizard

| # | Page | Input |
|---|---|---|
| 1 | Mode | New installation (or update / remove) |
| 2 | Destination | Installation directory |
| 3 | Service identity | LocalSystem or gMSA |
| 4 | Account | gMSA only: `DOMAIN\name$` |
| 5 | Database | SQL Server 2022 CU1+ or PostgreSQL 16+ |
| 6a | SQL Server | Server, database, certificate host name (empty = derived from the server) |
| 6b | PostgreSQL | Host/port/database, then user, password, root certificate — optionally superuser + password so role and database can be created |
| 7 | Network | Public hostname, ports, URL preview; allowed hosts under Advanced; checked port suggestions |
| 8 | HTTPS certificate | Existing certificate, PFX import or explicit lab certificate generation |
| 9 | Review configuration | Address, paths and optional product source code (~27 MB, selected by default) |
| 10 | Prerequisites | Scrolling status list and details; Apply selected fixes and Check again; Next only navigates and refuses while a fix is still ticked |
| 11 | Installation | Measured copy progress, activity indicator for unknown durations, elapsed time and log access |
| 12 | Finish | Open NodePilot, save credentials/setup token and the **external-trigger API key, which appears only here** |

First login: enter the setup token in the field the sign-in form reveals on the first attempt.

### The source-code option

The server artifact carries a snapshot of the product source under `knowledge\source` — around
2500 files and 27 MB — which the AI assistant reads for source-code questions. Untick the box on
the configuration review and it is not kept on the machine. Everything else is unchanged; only that one knowledge
source ends up empty, and it is off by default anyway (`AiKnowledge:SourceCodeEnabled`).

The snapshot is removed **after** the artifact has been verified, not skipped during the copy:
`Assert-NodePilotExtractedFiles` requires the install directory to hold exactly the signed
contents, so dropping files earlier would fail the installation. The signature check therefore
still runs against the complete artifact, and only then is the declared subtree deleted.

An update keeps the choice. `Update-NodePilot.ps1` looks at whether the installation currently has
the snapshot and reproduces that state afterwards, so an update never hands back a source tree the
operator chose not to have. It goes by the directory rather than the installation marker on
purpose: the marker is machine-wide and a second instance on the same host overwrites it.

Unattended: `"includeSourceSnapshot": false` in the answer file, or `-OmitSourceSnapshot` on
`Install-NodePilot.ps1`. The key is optional and **absent means include**, so an answer file
written before this option existed behaves exactly as it did.

There is deliberately no `/`-switch for it — see
[What is configurable from the command line](#what-is-configurable-from-the-command-line).

### Unattended

```
NodePilot-Server-Setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /ANSWERFILE=answers.json
```

The eleven mandatory keys are listed under [Answer file](#answer-file). In practice, always add:

```json
"provisioning": { "createDatabaseAndLogin": true },
"bootstrap":    { "adminUsername": "npadmin" }
```

The first creates the database and login (on PostgreSQL, also supply
`provisioning.postgresSuperUser` / `.postgresSuperPassword`); the second prevents the run from ending
with a token nobody types in — the generated password lands in `<dataPath>\bootstrap-admin.json`
under a restrictive ACL. To clone a reference instance instead, use `seed.backupPath`; see
[Turnkey rollout](#turnkey-rollout-unattended-without-typing-a-token).

### What is configurable from the command line

For a packaged rollout the answer file **is** the configuration surface, and it is a superset of
the wizard: every value the pages collect is expressible there, plus seven that the wizard never
offers — `serviceDisplayName`, `bootstrap.adminUsername`, `bootstrap.credentialOutputPath`,
`seed.backupPath`, `seed.passphrase`, `skips.databaseCheck` and `skips.gmsaCheck`. There is
deliberately no per-setting `/SWITCH`: a second way to say the same thing is a second thing that
can disagree with the first, and `SetupContract.ps1` validates the file strictly, so a misspelled
key is rejected before anything is installed rather than halfway through.

The setup's own switches are the ones that cannot be answer-file values, because they decide
*which* run this is rather than how it is configured:

| Switch | Effect |
|---|---|
| `/ANSWERFILE=<path>` | Unattended configuration. Replaces the wizard pages entirely |
| `/FULLREINSTALL` | Set an existing installation up from scratch instead of updating it |
| `/PURGEDATA` | Uninstall only: also remove the data directory |
| `/DIR=<path>` | Inno's own — see the trap below |
| `/VERYSILENT /SUPPRESSMSGBOXES /LOG=<path>` | Inno's own |

**`/DIR` has to match `installPath`, and nothing enforces it.** Under `/ANSWERFILE` the directory
page never runs, so Inno's `{app}` keeps `DefaultDirName` (`C:\Program Files\NodePilot`) while the
adapter installs the product to the answer file's `installPath`. Point them at different places and
the installation works, but `{app}` ends up holding only `unins000.exe` and `deploy\` — the
uninstaller sits apart from the product it removes. The Apps-&-Features entry is corrected to the
real path, so this is not visible there. Pass both:

```
NodePilot-Server-Setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES ^
  /ANSWERFILE=answers.json /DIR="D:\Apps\NodePilot"
```

Leave `/DIR` off whenever `installPath` is the default, which is the common case.

### Two stumbling blocks

- **On a host running IIS, ports 80 and 443 belong to HTTP.SYS** and cannot be bound by Kestrel.
  Choose different ports or set the HTTP port to `0`. The readiness page says so up front — with
  "reserved by Windows", not "in use by System (PID 4)".
- **Submit the antivirus exclusions beforehand:**
  [`../../docs/av-exclusions.md`](../../docs/av-exclusions.md) is written as a hand-off document for
  a security team.

Uninstalling **never** touches the database; it touches the data directory only with `/PURGEDATA=1`.

## Prerequisites and actions

The readiness page has a scrolling, keyboard-accessible list with text statuses. Failed checks come
first, then notices, passed checks and skipped checks. Selecting a row shows its full explanation and
copyable instructions in a separate scrolling detail area. Available fixes have a selection checkbox;
selected rows are marked in the list and counted on **Apply selected fixes**.

**Check again** only reads the machine state. **Apply selected fixes** performs the selected changes,
reports each operation, consumes the selections and runs the checks again. **Next** only navigates and
is disabled until the check succeeds without required failures; while a fix is still ticked it refuses
with a message, so apply the ticked fixes or clear them first. Prerequisites already installed are
retained if the operator cancels the wizard.

Available actions:

- Install the bundled x64 .NET and ASP.NET Core runtimes.
- Create the database/login and permissions on an existing SQL Server or PostgreSQL server.
- Import the selected HTTPS PFX into `LocalMachine\\My`, or explicitly generate a lab certificate.
- Prepare an existing gMSA locally, installing RSAT-AD-PowerShell if necessary. The account and its
  password-retrieval permissions must already exist in AD. Readiness is tested after installation;
  a required Windows restart is reported without restarting the machine.
- Optionally trust the pinned publisher certificate. This remains unselected and is not required
  for artifact verification.

No database server is installed. Domain account creation, AD permissions and database-server TLS
remain infrastructure tasks. The network page offers checked HTTPS alternatives (443, 8443, 9443)
with HTTP redirection disabled; the operator explicitly accepts the suggestion.

### PostgreSQL

Same row, different mechanics — and one difference you need to know: on SQL Server the authorization
is free, because `Trusted_Connection` *is* the Windows identity of the installing administrator.
PostgreSQL has no such thing. For `CREATE ROLE`/`CREATE DATABASE` the setup therefore needs
**superuser credentials**, which it cannot obtain anywhere else: two additional fields on the
credentials page (leave them empty → no fix offered), or `provisioning.postgresSuperUser` /
`.postgresSuperPassword` when unattended. The service never sees them; they live only in the
ACL-protected session of the running installation.

The client (`psql`) is part of the payload — seven files, 8.4 MB, determined from `psql.exe`'s import
table, so without ICU and without the pgAdmin libraries. It is **only extracted when it is needed**;
an installation on SQL Server never touches it. It is only built in with `-PgBinariesPath`; without
that switch the resulting installer is the same as before, and the Postgres row then states
explicitly that the sign-in went unverified.

That is what makes the row meaningful in the first place: previously it was a plain TCP probe that
stayed **green** for a missing role, a missing database or a wrong password — the error surfaced
180 seconds later at the health probe and rolled the installation back. Now the check signs in as the
NodePilot role, in the same TLS shape as the runtime (`sslmode=verify-full` against the supplied root
certificate).

**Certificate revocation is checked separately.** `psql` does not check revocation, but the service
does (online, `Check Certificate Revocation=true`) and refuses a server whose certificate is revoked
or whose CRL it cannot reach. The row therefore fetches the server certificate and checks it the same
way. If that fails, the installation aborts with *"Aborted: Postgres pre-flight failed - certificate
revocation could not be checked."* The fix is to make the CRL distribution point in the certificate
reachable from the NodePilot server, or to import the CRL into its `LocalMachine\CA` store
(`certutil -addstore CA <file>.crl`).

**What is missing is looked up in the catalog, not read out of the error message.** psql messages are
localized — a German server answers "Rolle »nodepilot« existiert nicht" — so a matcher written
against English classifies correctly on one host and marks everything as "rejected" on the next. On a
failed sign-in the check therefore queries `pg_roles` and `pg_database` with the superuser
credentials. Without superuser credentials it says it cannot tell the difference, and passes the
server's message through verbatim — rather than guessing.

The fix (`Provision-NodePilotPostgres.ps1`) follows the same rule as the SQL Server side: first a
permission gate (`rolsuper`, or `rolcreaterole` **and** `rolcreatedb`), then existence-checked
role → database, and finally a test sign-in as the role itself. Two things it deliberately does
**not** do: reset the password of an existing role (a mismatched password is a typo in the answer
file — "healing" it would hide the typo and lock out everything else that uses that role), and change
the owner of an existing database. Both are reported, not corrected.

## Turnkey rollout (unattended, without typing a token)

An unattended run otherwise ends with an instance nobody can use: a human would have to type the
setup token into the sign-in form. There are two ways around that, and they are mutually exclusive —
which one applies depends solely on whether the instance has users afterwards.

### Variant 1: random admin

With the optional `bootstrap` group, the setup redeems the token itself.

```json
"bootstrap": {
  "adminUsername": "npadmin",
  "credentialOutputPath": "C:\\ProgramData\\NodePilot\\bootstrap-admin.json"
}
```

`credentialOutputPath` is optional; without it the file lands at
`<DataPath>\bootstrap-admin.json`. Contents: user name, password, address, timestamp.

**The password is generated randomly per machine, not supplied.** A fixed value would be identical
across every machine, would have a known value, and would be found rather than guessed — on a product
that runs PowerShell on every managed machine and, in server mode, listens on all interfaces. The
answer file therefore has **no** `adminPassword`.

### Variant 2: seed an existing estate from a backup

The richer variant. Install a reference machine normally, set it up, run `np backup export` — the
result is the seed for every further machine:

```json
"seed": {
  "backupPath": "\\\\share\\golden.npbackup",
  "passphrase": "…"
}
```

The installer copies the file to `<DataPath>\seed.npbackup` (restrictive ACL, same writer as the
configuration) and puts the passphrase into the `Environment` value of the service key — **not** into
`appsettings.Production.json`, exactly like the Postgres connection string. On first start,
`ProvisioningSeeder` applies it **before** anything reads the users table, and deletes the file
afterwards.

The machine therefore comes up with users, workflows, machines, credentials **and settings**. Because
a restore into an empty database requires a break-glass admin, `EnterpriseRecoveryInvariant` is
satisfied afterwards too — so LDAP/SSO can be switched on. (The authentication section is
restart-required per the hot-reload matrix; restart once after seeding.)

Two rules that make it safe to leave the seed configured permanently:

- **Only into an empty instance.** If users exist, nothing happens — the seed is initial population,
  never migration. A machine in production keeps everything it has, whatever the configuration says.
- **Fail closed.** Wrong passphrase, missing or corrupt file → the service does **not** start. The
  alternative would be an empty instance with an open bootstrap window that the operator believes is
  provisioned.

What happens in each case:

| Situation | Result |
|---|---|
| `seed` group set, instance empty | The estate is applied. There is no token, `bootstrap.status=AlreadyProvisioned`, **no** credential file. |
| Users already exist (seeded, or a new installation over an existing database) | There is no token and nothing to redeem. `bootstrap.status=AlreadyProvisioned`, **no** credential file. |
| No users, `bootstrap.adminUsername` set | The account is created and the credentials are written out. `bootstrap.status=Created`. |
| No users, no `bootstrap` group | As before: token on the finish page, manual first sign-in. |

**The credential file is a live credential.** It is created ACL-before-content (SYSTEM +
Administrators, no inheritance) through the same mechanism as the signed-artifact staging — so it
never briefly inherits from `DataPath`. It is **not** deleted automatically: a rollout that has not
collected it yet would otherwise be left without an account. Collecting it, deleting it and rotating
the password is the operator's step.

Two properties that are not obvious:

- **A failed bootstrap does not fail the installation.** The service is running and healthy when the
  login is attempted. The exit code stays 0 and `bootstrap.status=Failed` carries the server's answer
  verbatim. Reporting a working installation as a failure would push SCCM into a retry — and a repeat
  install is considerably more destructive than a missing account.
- **The name is pinned.** If `bootstrap.adminUsername` is set, the installer writes
  `NodePilot:BootstrapAdminUsername` into the configuration. Even a token intercepted between service
  start and the adapter's login can then only create that one account.

**LDAP/SSO does not replace this.** JIT provisioning is explicitly blocked while no local break-glass
admin exists (`external_jit_blocked_until_breakglass_admin_exists`), and
`EnterpriseRecoveryInvariant` aborts the boot if SSO is enabled without one. The account created this
way carries `IsBreakGlass` and satisfies exactly that condition — SSO can be enabled afterwards.

## Progress display

Probe, provisioning and installation run through the same asynchronous worker. The wizard shows the
current operation, elapsed time and an **Open setup log** button. The outcome still comes from the
result file's exit code.

Install and update scripts report structured progress through an optional `ProgressCallback`.
`DeploymentProgress.ps1` writes complete, delimited records to the protected session. Progress is
independent of console text and failures in the progress listener cannot fail a deployment.

Copying reports bytes and completed files, including updates within large files, at most four times
per second plus phase boundaries. Its percentage describes the current copy operation. Extraction,
signature verification, runtime installation and service readiness use an activity indicator because
their duration is unknown. There is no estimated overall percentage.

The wizard detects a worker that exits without a result and retains the existing 45-minute timeout.
On timeout it retains the protected session because the worker may still need it; close setup and
inspect the log before retrying. Setup does not cancel an in-flight mutation.

Small scripts and the publisher certificate precede the large payload in the package. Large payloads
are outside the solid-compressed script block, avoiding decompression of the artifact just to read a
helper script.

## Port check

The ports are checked for bindability **before** the installation — measured against what happens
without that check: on a ConfigMgr site server, HTTP.SYS reserves ports 80 and 443, so Kestrel failed
at startup with `SocketException 10013`. None of that was visible. By then the installer had copied
everything, registered the service, waited 180 seconds for `/healthz/ready`, then rolled back and
reported "did not report /healthz/ready" — three minutes for a statement nobody can act on.

Two things this check distinguishes that a naive version would not:

- **`10013` does not mean "in use".** Windows returns it for an HTTP.SYS reservation or an excluded
  port range — there is **no listener** to be found. A message saying "port in use" sends the operator
  chasing a process that does not exist. The instructions therefore name
  `netsh interface ipv4 show excludedportrange protocol=tcp`.
- **Your own service does not count as a conflict.** When NodePilot is installed over itself, the
  service being replaced holds the port. Reporting that as an error would punish someone for a correct
  first installation.

The bind is against `IPAddress.Any` — the same address Kestrel uses
(`AnyIPListenOptions.BindAsync`). A test against `127.0.0.1` would wave through a port that is
reserved on the wildcard address. Bound and immediately released: a probe, not a change, otherwise it
would not be permissible behind the "Check again" button.

If the installation fails anyway, the **cause is now in the dialog**: the adapter pulls the run's last
`.NET Runtime` exception out of the Application log and appends it to the message
(`SocketException (10013): …`). Previously only the symptom sentence was shown, and the cause sat in
a log file nobody opens.

## Certificate selection (TLS page)

The HTTPS certificate has its own page after the network configuration:

- **Existing certificate:** select from `Cert:\\LocalMachine\\My` or enter its thumbprint. Each
  choice includes subject, thumbprint and expiry; missing private keys are named. Inventory reloads
  on each visit, and failure to enumerate preserves manual entry.
- **PFX import:** select a PFX and enter its password (empty is allowed). Probe inspects the file
  without persisting its private key. Applying the selected fix imports it into the machine's
  personal store, without making the private key exportable or adding root trust. The imported
  thumbprint becomes the selected certificate; the password is cleared from the wizard.
- **Lab certificate:** explicitly select generation, then apply the certificate fix on the readiness
  page. Generation is never selected by default.

The same readiness check evaluates presence, private key, validity and hostname after provisioning.
Validity and hostname problems remain warnings; existing certificates are not silently replaced.
A PFX containing multiple end-entity certificates is rejected so the setup cannot choose the wrong
server identity.

## Architecture

The Pascal layer is deliberately **thin**: pages, payload, `Exec`, reading INI. No installation logic.

```
Wizard pages   ->  answers.json  ->  Invoke-NodePilotSetup.ps1  ->  Install-NodePilot.ps1
(NodePilotServer.iss)  (ACL-protected)       (adapter)              (unchanged)
```

Why a file instead of a command line — three reasons, each sufficient on its own:

1. `-PostgresPassword` is a `[SecureString]` and **cannot** be passed through `powershell.exe -File`
   at all.
2. `/VERYSILENT /SUPPRESSMSGBOXES /ANSWERFILE=` then falls out for SCCM/GPO, with no second code path.
3. Inno Pascal has no unit-test story. Whatever lives in PowerShell is testable
   ([`../Test-SetupAdapter.ps1`](../Test-SetupAdapter.ps1), 55 assertions).

Results come back as **INI**, not JSON: Inno has `GetIniString` built in and nothing at all for JSON —
a parser in Pascal would be ~120 lines that no test reaches.

## Unattended (SCCM, GPO)

```powershell
NodePilot-Server-Setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /ANSWERFILE=C:\prod\answers.json
```

| Switch | Effect |
|---|---|
| `/ANSWERFILE=<path>` | Answers from a file instead of the pages. It wins over everything. |
| `/FULLREINSTALL` | Forces a fresh install instead of an update. **Generates a new external-trigger API key** — the old one cannot be reconstructed. |
| `/LOG=<path>` | Inno's log. The adapter additionally writes `%TEMP%\nodepilot-server-setup.log`. |

The supplied answer file is **copied**, not used in place: the copy inherits the restrictive DACL of
the session directory and is shredded at the end. The original is left untouched.

### Answer file

`schemaVersion: 1`. Unknown keys and missing mandatory keys are rejected **by name** — stricter than
PowerShell's binding, because a typo in an SCCM file would otherwise strike in the middle of the
installation.

```json
{
  "schemaVersion": 1,
  "mode": "install",
  "installPath": "C:\\Program Files\\NodePilot",
  "dataPath": "C:\\ProgramData\\NodePilot",
  "serviceName": "NodePilot",
  "identity": { "type": "gmsa", "account": "CONTOSO\\svc-nodepilot$" },
  "database": {
    "provider": "sqlserver",
    "sqlServer": "sql01.contoso.local",
    "sqlDatabase": "NodePilot",
    "sqlCertificateHostName": ""
  },
  "network": {
    "publicHostname": "nodepilot.contoso.local",
    "httpsPort": 443, "httpPort": 80,
    "allowedHosts": "nodepilot.contoso.local", "knownProxyIps": []
  },
  "certificate": { "thumbprint": "A1B2...", "source": "existing" },

  "bootstrap": { "adminUsername": "npadmin" },
  "seed": { "backupPath": "\\\\share\\golden.npbackup", "passphrase": "..." }
}
```

`identity.type` is `localSystem` or `gmsa` (then `identity.account` is mandatory).
`database.provider` is `sqlserver` (then `sqlServer` + `sqlDatabase`) or `postgres` (then
`postgresHost`, `postgresDatabase`, `postgresUser`, `postgresPassword`, `postgresRootCertificate`).
`certificate.thumbprint` is the only mandatory key that may be **empty**: empty means "none available
yet" and requires either `provisioning.generateSelfSignedCertificate` or an explicitly requested PFX import. If something is there, it must be
40 hex characters — otherwise the run aborts here instead of later in the Kestrel configuration.
For `"mode": "update"`, `installPath` and `serviceName` are enough. `dataPath` is optional and preserves
a custom data-directory location in the completion summary. Installation-only keys are rejected,
so that a stale file is not half-applied.

**Optional keys at a glance:**

| Key | Effect |
|---|---|
| `serviceDisplayName` | Display name of the service |
| `database.sqlCertificateHostName` | Leave empty → the installer derives it from `sqlServer` |
| `network.allowedHosts`, `network.knownProxyIps` | Host filter and trusted proxy IPs. Blank host lists use the public hostname, short computer name and `localhost`. Explicit lists retain their restrictions with `localhost` appended for the health probe; certificate SANs do not expand them |
| `certificate.source` | `existing` (default when omitted) or `pfx` |
| `certificate.pfxPath`, `.pfxPassword` | PFX path and password; both required for `source: pfx`, with an empty thumbprint. The password may be empty. |
| `provisioning.importPfxCertificate` | Import that PFX before installation; mutually exclusive with self-signed generation or an existing thumbprint |
| `provisioning.installGmsa`, `.installAdTools` | Prepare the existing gMSA locally; optionally install missing AD tools. `installAdTools` requires `installGmsa`. |
| `provisioning.installDotnetRuntime`, `.createDatabaseAndLogin`, `.generateSelfSignedCertificate`, `.trustArtifactSigner` | The same auto-fixes as on the readiness page, **in silent mode too** — there the answer file is the only place they can be requested. They run before the installation, not after. |
| `provisioning.postgresSuperUser`, `.postgresSuperPassword` | For the PostgreSQL fix only. `CREATE ROLE`/`CREATE DATABASE` need an authorization that SQL Server gets for free via the Windows identity. The service never sees them |
| `bootstrap.adminUsername` | Creates the first admin, password random (see [Turnkey rollout](#turnkey-rollout-unattended-without-typing-a-token)) |
| `bootstrap.credentialOutputPath` | Where the credentials are written. Default `<dataPath>\bootstrap-admin.json` |
| `seed.backupPath` | A `.npbackup` applied on first start |
| `seed.passphrase` | Its passphrase. **Never** lands in `appsettings.Production.json`, but in the `Environment` value of the service key |
| `skips.databaseCheck`, `skips.gmsaCheck` | Skip the respective pre-flight check |

`bootstrap` and `seed` are not mutually exclusive, but only one takes effect: if the seed brings users
with it, there is no token and `bootstrap` finds nothing to do. Without either, the token on the finish
page remains.

**For a rollout onto a fresh database, `provisioning.createDatabaseAndLogin` belongs in the answer
file.** One key, both providers — which script runs follows from `database.provider` and not from a
second flag that could contradict the first. On the Postgres path,
`provisioning.postgresSuperUser` / `.postgresSuperPassword` must be set as well, otherwise the role is
left untouched and the run says so in the log.

On SQL Server the key covers both things an unattended run otherwise leaves open: creating the database
and login, and granting the service identity (computer account or gMSA) `db_owner`. Without it the
service starts and answers `/healthz/ready` with 503, because it cannot sign in to the database.
Existence-checked — on a machine where everything is already there, the run changes nothing. It runs
with the permissions of the account that started the setup; without `sysadmin` or `CREATE ANY DATABASE`
nothing is changed and the reason is in the log. Interactively the key is not needed — there the
readiness page ticks the row itself.

**The password is in clear text in the file.** It is protected by the DACL of its directory (SYSTEM +
Administrators + the installing user, set atomically at creation). A local administrator can read it
during the installation — the same reader group that can also read the secret's permanent storage
location (`HKLM\SYSTEM\CurrentControlSet\Services\NodePilot\Environment`). The answer file opens **no
new class of attacker**. You should still treat your own template like any other file containing a
production password.

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 3 | Fatal Inno error while preparing the next installation phase |
| 7 | Setup could not proceed: invalid answer file, prerequisite preparation or installation failure. Read the log for the cause and any rollback result |
| 8 | Setup could not proceed and Windows must be restarted |

Any nonzero exit code means setup did not complete. Code 7 alone does not prove a rollback occurred:
validation can fail before installation starts, and restoration can itself fail. Check the setup log.

Adapter-internal (visible in the log): 2 = readiness red, 3 = answer file invalid, 4 = installation
failed, 1 = adapter crash.

## Update

A repeat run detects an existing installation via `HKLM\SOFTWARE\NodePilot\Server` — **including one
installed via the ZIP route** — and by default applies `Update-NodePilot.ps1` semantics: binaries only,
`appsettings.Production.json` preserved, database and service identity untouched, rollback on failure,
service running afterwards.

## Finish page

After a successful run the adapter writes a `[result]` section into its INI; the last wizard page shows
it. It contains everything needed for first access:

- **Address** (`https://<host>:<port>/`). On an update this is derived from the already-installed
  `appsettings.Production.json` — an update does not ask for network details.
- **Setup token** for the first sign-in, as long as no account exists yet. If the file is
  owner-exclusive and unreadable, its path and the `robocopy /B` trick are named instead; if it is
  missing entirely, the database already has accounts — that is stated too, rather than saying nothing.
- **External-trigger API key.** The only place it ever appears: it is generated by the adapter,
  `Install-NodePilot.ps1` prints it to a console that does not exist under `Exec(…, SW_HIDE)`, and
  `install-report.txt` deliberately omits it.
- **Certificate thumbprint**, with a note about importing a self-signed one on the clients.
- **Service name, program and data directory.**

Here it is a `TNewMemo`, not a label as on the readiness page: the page is otherwise empty, so there is
room for a properly sized one, and a 64-character API key that cannot be selected would have to be
typed out by hand. "Save this summary…" writes the same text to the desktop — **including the secrets**,
which the confirmation dialog points out.

Updates confirm that existing sign-in details were retained and save to
`nodepilot-update-summary.txt`. Installation credentials remain in the separate
`nodepilot-setup-summary.txt`; saving an update summary does not overwrite them.

The summary is built in `PrepareToInstall`, not in `CurPageChanged`: `DeinitializeSetup` clears the
session directory, so the INI would be long gone by display time. And only on the success path — a
rolled-back run must not present values as if it had worked.

## Uninstalling

Reachable in two places: through "Apps & features" like any Windows program, **and as a third option
on the mode page** if you start the setup on a machine where NodePilot is already installed. The second
exists because nobody who has just double-clicked the setup then goes looking in Control Panel. The
mode page asks nothing itself there, but hands over to the same uninstaller — one decision, one prompt.

For an instance installed via the ZIP route there is no `unins000.exe`. The option then names the path
to `Uninstall-NodePilot.ps1` instead of grasping at nothing.

It removes **everything this setup installed**: the Windows service, the service binaries, firewall
rules, the installation marker, the registry environment (including the Postgres password stored there)
and the uninstall entry.

Exactly one question is asked: **keep the data directory?** (`C:\ProgramData\NodePilot` — logs, the JWT
signing key, the data-protection key ring). The default is **keep**, everywhere: interactive, `/SILENT`
without switches, "Apps & features", and when invoked by Inno itself.

```powershell
"C:\Program Files\NodePilot\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES              # keep data
"C:\Program Files\NodePilot\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /PURGEDATA=1 # delete data
```

**What gets removed is read from the installation marker**
(`HKLM\SOFTWARE\NodePilot\Server`: `ServiceName`, `InstallPath`, `DataPath`) — not from the wizard
defaults. That is the only approach that works for an installation with a different service name or
different paths: under `/ANSWERFILE` the mode and directory pages never run, `{app}` is then merely
where the uninstaller lives, and the uninstaller process no longer knows the setup's service name.
Deleting the marker by hand takes away the uninstaller's only source; it then falls back to `NodePilot`
and `{app}`.

**The database is never removed, and there is no option for it.** This setup does not create it — it
was provisioned separately, often has its own backup, replication and retention regime, and in an
active/passive cluster **both nodes share the same database**. What you never installed, you do not
remove. The wizard says so explicitly in the prompt rather than staying silent, and a contract test
prevents the capability from coming back.

Also deliberately left in place: the gMSA's "Log on as a service" right and the read ACE on the TLS
certificate's private key — both may be shared with another service. The uninstaller **names** them
explicitly at the end.

## Building

```powershell
# On its own:
.\deploy\server\Build-ServerInstaller.ps1 `
    -ArtifactPath .\out\NodePilot-<version>.zip `
    -TrustedSignerThumbprint 277EAB317A581C88302CE92BE805938C86B4650D

# As part of the release build (recommended — signed and in SHA256SUMS):
.\deploy\Build-Artifact.ps1 -SigningCertificateThumbprint $tp `
    -IncludeServerInstaller -InstallerSigningCertificateThumbprint $tp
```

Prerequisites: Inno Setup 6, a **signed** artifact (the setup verifies it at installation time, an
`-AllowUnsignedDevelopmentArtifact` build is skipped), and network access on the first run for the
runtime download.

Both runtime installers are fetched at build time and each is verified three ways: against the **SHA512**
published in Microsoft's release metadata, against the checked-in pin in
[`runtime-payload.lock.json`](runtime-payload.lock.json), and by Authenticode against "Microsoft
Corporation". It is the **standalone runtime**, never the Hosting Bundle — that one wires up IIS and
restarts W3SVC, which is undesirable on shared hosts. None of it is checked in except the pin; that
makes the payload reproducible and every change to it review-worthy.

Size: ~52 MB (desktop installer: ~176 MB — no Electron, no bundled PostgreSQL).

## Inno Setup traps measured here

All seven occurred on real infrastructure, none were derived from the documentation:

1. **`ssPostInstall` cannot report a failure.** Neither `RaiseException` nor `Abort` changes the exit
   code there — a failed run reports 0. Under SCCM that would be a deployment reporting success having
   installed nothing. The installation therefore runs in `PrepareToInstall` (returning a message,
   exit 7).
2. **`[UninstallRun]` evaluates `{code:…}` at installation time** and freezes the result into
   `unins000.dat`. A decision made at uninstall time can never get through that way. The section does
   not exist; the call is made from `[Code]`.
3. **`[Run]` cannot check exit codes.** Also does not exist; a contract test forbids both sections.
4. **Inno deduplicates identical source files.** A `dontcopy` entry and a `DestDir` entry for the same
   file collapse into one; the `dontcopy` variant silently disappears. Hence two separate staging trees
   (`payload\` and `deploy\`).
5. **`{app}` does not exist yet during the wizard.** The readiness page and `PrepareToInstall` run
   before the copy — everything needed at that time is `dontcopy` and lives in `{tmp}`.
6. **No `SaveStringToUTF8File` in this version**, only `SaveStringsToUTF8File` (`TArrayOfString`). The
   AnsiString variant would write a password containing umlauts in the system code page, which the
   adapter then rejects. And `LoadStringFromFile` returns AnsiString — so the session path lives under
   `%ProgramData%` (guaranteed ASCII), and a BOM is stripped on both sides.
7. **No line in `[Code]` may start with `#`** — the ISPP preprocessor reads that as a directive and
   aborts with "Unknown preprocessor directive". This affects wrapped `#13#10` continuations.

And one outside Inno, but equally expensive: **`icacls /grant '<SID>:(OI)(CI)F'` on a leaf file reports
success and adds no ACE.** `(OI)`/`(CI)` are container inheritance flags and are discarded there.
Without them it works. This affects `-PurgeData`, which otherwise fails on the owner-only
`jwt-secret.key`.

## Test coverage — and what is missing

**Automated** (CI, both PowerShell versions):
[`../Test-SetupAdapter.ps1`](../Test-SetupAdapter.ps1) checks answer-file behaviour behaviourally
(torture round-trip, schema rejection naming the key, splat separation per provider, SecureString, INI
escaping, the two-layer nature of the pre-flight).
[`../Test-DeploymentTemplates.ps1`](../Test-DeploymentTemplates.ps1) statically pins whatever is
checkable in the `.iss`, the adapter, the runtime fetch and the build — every contract mutation-tested.

**Not automated, named honestly:**

- **The Pascal code itself.** Page flow, the `ShouldSkipPage` matrix, the JSON escaper, INI reading,
  control states. Inno Pascal has no tooling for it. Countermeasure: minimal surface plus the contracts
  above. What a compiler run does cover — syntax and every identifier used — can be had without
  building an installer via
  `ISCC /Qp /O- /DStageDir=<stage> /DOutputDir=<out> NodePilotServer.iss`; that this really compiles the
  `[Code]` section can be demonstrated with a deliberately wrong identifier in a copy. Does **not** run
  in CI (no ISCC on the runner).
- **Positions of computed controls.** That the certificate list lands *inside* the area is enforced by
  the clamp in `CompactNetworkPage` and a contract on it. How the page then looks — whether the spacing
  is right or it feels cramped — is only visible in the running wizard. That is exactly how the list
  shipped cut off the first time.
- **Native GUI coverage is outside CI.** The October 2026 GW1 review exercised real dialogs,
  repairs, progress, upgrade/uninstall and DPI, plus separate synthetic worker failures. Repeatable
  cases and their evidence requirements are in the optional installer matrix linked below;
  results belong to the tested artifact hash, not automatically to the next release.
- **Historical unattended matrix.** All four identity/database combinations (gMSA or LocalSystem, SQL Server or
  PostgreSQL 16 with TLS) were installed fresh in the lab, and the SQL Server ones also as an update
  from 1.4.0; see the addendum of 2026-09-23 below. The wizard pages for them were not clicked.
- **SQL Server only on the NodePilot host itself.** A remote SQL Server with LocalSystem (the
  computer-account path) did not run in this lab.
- **Only Windows Server 2025.** Server 2022 (the `MinVersion`) is untested.

### Manual smoke matrix before each release

The reproducible [optional server installer acceptance step](../../scripts/release-lab/server-ui/README.md)
is release step 6a. It records the GW1 cases with stable IDs, baseline preparation, full-text scrolling,
expected results, reusable fault/CRL fixtures and cleanup. Use it for the native-dialog, silent,
upgrade/rollback and DPI follow-up; the broader catalog below remains a list of additional smoke
scenarios, not a claim that every row has been exercised. Mandatory release gates remain in
[RELEASING.md](../../RELEASING.md).

| # | Case | Expectation |
|---|---|---|
| 1 | Fresh, SQL Server + LocalSystem | Service runs, `/healthz/ready` 200 |
| 2 | Fresh, PostgreSQL + gMSA | ditto |
| 3 | Repeat run over an existing installation | Update semantics, configuration preserved |
| 4 | `/FULLREINSTALL` | Confirmation dialog appears, new API key |
| 5 | `/VERYSILENT /SUPPRESSMSGBOXES /ANSWERFILE` | Exit 0, service runs |
| 6 | Runtime missing, offer declined | "Next" stays disabled |
| 7 | Blocking readiness row | "Next" disabled, instructions visible |
| 8 | Cancel mid-wizard | No session directory left behind |
| 9 | Uninstall without switches | Everything gone, data and database remain |
| 10 | Uninstall `/PURGEDATA=1` | Data directory gone as well, database remains |
| 11 | Mode page → "Remove" | Uninstaller takes over, setup closes without a cancel prompt |
| 12 | Reboot after installation | Service comes up unattended, even if the database becomes ready later |
| 13 | Finish page after a new installation | URL, setup token, API key, thumbprint, paths visible and selectable |
| 14 | Finish page after an update | URL from the installed configuration, no token, no new API key |
| 15 | Update over a running service | Waits for the process instead of aborting |
| 16 | TLS page, certificate chosen from the list | Thumbprint appears in the field above, readiness row OK |
| 17 | TLS page, empty certificate store | Informational row instead of a list, "Next" stays available |
| 18 | HTTP port 80 on a host with IIS | Readiness row Needs attention with "reserved by Windows", "Next" disabled |
| 19 | HTTP port 0 | Row OK, "HTTP disabled" |
| 20 | Interactive installation | Bar and phase text advance, window stays responsive, no "Not responding" |
| 21 | Unattended installation | No UI, exit code unchanged |
| 22 | Unattended with `bootstrap.adminUsername` | Exit 0, credential file present (ACL SYSTEM + Administrators only), sign-in works without a token, `admin-setup.token` gone |
| 23 | Unattended without a `bootstrap` group | As before: token on the finish page, no account |
| 24 | Unattended with a `seed` group, empty instance | Sign-in with a user **from the backup**, no token, no credential file, seed file deleted |
| 25 | The same seed against a populated instance | Nothing happens, no duplicates, seed file left in place |
| 26 | Wrong seed passphrase | Service does **not** start, the message names the passphrase, no partial data in the database |
| 27 | SQL Server without a login for the service identity | Row Needs attention **with the fix selected**, "Apply selected fixes" creates login + user + `db_owner`, OK afterwards |
| 28 | The same run with the login already present | Row OK without a checkbox, nothing is changed |
| 29 | Fix without `sysadmin` | Nothing changed, the message names the reason, tick cleared afterwards (no loop) |
| 30 | Unattended with `provisioning.createDatabaseAndLogin` | Exit 0, database + login created, `/healthz/ready` 200 |
| 31 | Expired certificate selected | Row **Notice** with the expiry date, "Next" stays available, no auto-fix offered |
| 32 | Certificate with a foreign SAN | Row **Notice**, names both names, "Next" stays available |
| 33 | Postgres without role/database, superuser supplied | Row Needs attention with a checkbox, "Apply selected fixes" creates both, re-check OK |
| 34 | The same without superuser fields | Row Needs attention **without** a checkbox, server message verbatim, snippet visible |
| 35 | Postgres with a wrong role password | Row Needs attention, says "both present", no fix offered, password unchanged |
| 36 | Installer built without `-PgBinariesPath`, Postgres selected | Row **Notice**: reachable, sign-in unverified |
| 37 | New installation with gMSA over a LocalSystem installation | Exit 0, service runs as the gMSA, `jwt-secret.key` now owned by the gMSA |
| 38 | Failure **after** the ACL step | Rollback restores the service **and** the directory ACL, the previous installation keeps running |
| 39 | Thumbprint field left empty | "Next" leads to the readiness page, row Needs attention with "No certificate selected" plus a **not** pre-ticked offer; tick + "Apply selected fixes" creates one, writes the thumbprint back into the field, re-check OK |
| 40 | Field filled with 12 characters | Message "40 hexadecimal characters", the page stays put |
| 41 | Host that does not know the publisher | Row "Artifact publisher" **Notice**, "Next" stays available, installation completes **without** an import |
| 42 | The same case, offer ticked | Tick + "Apply selected fixes" imports into `LocalMachine\Root`, re-check OK, afterwards `Get-AuthenticodeSignature` on the setup `.exe` reports `Valid` |
| 43 | Expired publisher certificate | Row **Needs attention**, "Next" disabled, **no** offer (an import does not repair it) |
| 44 | Only the **32-bit** or an older runtime (< 10.0.11) installed | Row "ASP.NET Core 10.0.11+ runtime" **Needs attention, fix selected**, naming the architecture or the version found together with its path |
| 45 | x64 runtime present, x86 first in `PATH` | Row **OK**, naming the 64-bit host it queried |
| 46 | SQL Server reachable, target database not created yet | Row Needs attention **with the fix selected**, "Apply selected fixes" creates database + login + user + `db_owner`, re-check OK |
| 47 | The same, but the installing account is not `sysadmin` | Nothing changed, the message names the permission, the DDL stays on the page, tick cleared (no loop) |
| 48 | SQL Server whose certificate this host does not trust | Row Needs attention, the remediation names TLS and reachability **before** the DDL, no claim that a login is missing |
| 49 | **Bare machine, no .NET at all** | Runtime row Needs attention **with the fix selected**; "Apply selected fixes" installs both bundled packages; afterwards `C:\Program Files\dotnet\dotnet.exe` exists and `--list-runtimes` reports `Microsoft.NETCore.App` **and** `Microsoft.AspNetCore.App`; re-check OK. Two MsiInstaller 1033 events, not one |
| 50 | Installer that exits 0 without installing anything | Row stays red **and** a dialog names it: the verdict comes from re-running the readiness check, never from the exit code |
| 51 | Machine carrying a **newer** .NET runtime but no ASP.NET Core | The host package declines with 1638, the run continues to the second package, re-check OK — a decline is not a reason to stop |
| 52 | PFX import, correct and incorrect password | Probe does not persist a key; correct import is rechecked; incorrect password is actionable and never logged |
| 53 | Existing gMSA, AD tools absent | Explicit fix installs tools and prepares the local account; a required restart blocks continuation |
| 54 | gMSA without password-retrieval permission | No AD permission change; actionable error and no success claim |
| 55 | All fixes selected, long messages, keyboard navigation at 100/150/200% | List and detail scroll independently; no overlaps; every action remains reachable |
| 56 | Large-file copy and a slow readiness probe | Real byte counts while copying, activity indicator while waiting, responsive log button |
| 57 | Terminated worker or timeout | Failure reported; timeout retains the protected session and prevents another operation |
| 58 | Port suggestion accepted or declined | Only accepted values change; URL preview updates; ports are checked again before installation |
| 59 | Update or answer-file run | No unused certificate/content pages; existing source-content choice preserved |
| 60 | Finish page | First-access values precede technical details; Open NodePilot uses the reported URL |

Historical status before the October 2026 GUI review: 1, 3, 5, 9, 10, 22, 23, 30, 37 and 38 ran in the Hyper-V lab against real AD, a real
gMSA and SQL Server 2022 CU. On **2026-08-06** the **logic** behind 39, 40, 41, 43 and 44 was added —
through the adapter against the running CM1 installation (`InitSession` → `Probe` → `Certificates` →
`Cleanup`, certificates as in-memory fixtures, no store writes): all ten rows green, an answer file with
an empty thumbprint accepted with exit 2, one with twelve characters rejected with exit 3, `Cleanup`
left behind neither the session directory nor the answer file, the service still `Running` afterwards
and `/healthz/ready` 200. What is missing there, as with 33 to 35, is the **page**.

Then open: 2, 4, 6, 7, 8, 11 to 21, 24 to 29, 31 to 36, 42 and 45. 42 would write machine-wide into
`LocalMachine\Root`; 45 needs a host with **both** runtimes — demonstrated on the development machine
(x86 first in `PATH`, row stays green and names the x64 host), and there is no 32-bit runtime in the
lab. The **logic** behind 33 to 35 ran against a real PostgreSQL 16 with TLS (see below).

Addendum 2026-09-23 (install matrix, 1.4.1): unattended on Windows Server 2025 with SQL Server 2022
CU1 and PostgreSQL 16 (TLS, `verify-full`) on the same host, each run from a clean checkpoint. Pass
means exit 0, `/healthz/ready` 200, a workflow executed to `Succeeded`, and no error entry in the
service log for five minutes after start.

| Run | Identity | Database | Path | Result |
|---|---|---|---|---|
| F1 | gMSA | SQL Server | fresh | Pass |
| F2 | LocalSystem | SQL Server | fresh | Pass |
| F3 | gMSA | PostgreSQL | fresh | Pass |
| F4 | LocalSystem | PostgreSQL (port omitted) | fresh | Pass |
| U1 | gMSA | SQL Server | 1.4.0 → 1.4.1 | Pass |
| U2 | LocalSystem | SQL Server | 1.4.0 → 1.4.1 | Pass (1.4.0 needs a manual `NT AUTHORITY\SYSTEM` grant to install at all) |
| U3/U4 | either | PostgreSQL | 1.4.0 → 1.4.1 | Not applicable: 1.4.0 cannot complete a PostgreSQL installation |

Defects found and fixed on the way: LocalSystem against a local SQL Server was granted as the
computer account although SQL Server sees `NT AUTHORITY\SYSTEM`; the provisioning allowlist rejected
that name; the bundled `psql` was never extracted (`ExtractTemporaryFiles` needs a wildcard);
every PostgreSQL installation failed rendering its connection string (a `PSObject` path); an omitted
`postgresPort` reached provisioning as `0`; EF logged an error on the first migration of an empty
PostgreSQL database; and the outbox claim inherited a pooled connection's Serializable isolation.

Addendum 2026-08-06 (second finding): on a fresh host **all** rows were green and the installation then
aborted with exit 4 and a rollback — `CheckSignature` failed on the chain of the self-signed publisher.
The readiness page simply did not know about this requirement (nine IDs, none of them `signer`), and the
fix, which has always been in the adapter, was hard-wired to `false` in the wizard. On top of that a
second, independent defect: `Invoke-ProvisionSigner` looked for the `.cer` under `signer\` — a folder the
build never creates, because `[Files]` with `dontcopy` and **without** `recursesubdirs` puts everything
flat into `{tmp}`. So the auto-fix would have found nothing even if it had been requested through the
answer file. Both fixed — and on the first run with the new row the layout caveat struck immediately:
the tenth row was five lines tall, which pushed its own checkbox behind the buttons. A fix you can see,
have explained to you, and cannot tick. Two corrections: the message is shortened to two lines (the
operating system's chain reasoning now lives in the scrollable instructions field), and `LayoutReadiness`
counts the visible fix boxes up front and guarantees each one a clickable strip above the buttons. When the
rows still do not leave the instructions box about four lines, passing rows (then warning and skipped rows,
and as the last step failing rows) are cut to one line and show their full text in that box when selected. Rows
41/42 below cover the case and have **not** been clicked yet.

Follow-up on the consequence: the row has since lost its blocking effect again — not because it was
wrong, but because the **requirement** was. `Install-NodePilot.ps1` now verifies the signature without
the chain against the compiled-in thumbprint and explicitly replaces what the chain used to supply
(code-signing EKU, KeyUsage, validity window). No first installation needs a root import any more; the
row is amber and the offer optional. It stays red for everything the installer itself rejects.

From the same field run, three readability defects in the log, all fixed: the adapter wrote every
installer line a second time (`Write-Host` reaches the transcript even when the information stream is
redirected — every line appeared twice and read as if every step had run twice). And on a **fresh**
machine the rollback talked about restoring an installation that never existed: "Restoring the previous
installation" plus "Existing service found - stopping and removing" for the service the same run had
registered two minutes earlier. The action was right in each case, only the words came from the upgrade
path. It is now distinguished by `$previousService`.

Addendum 2026-08-06: row 39 surfaced in the field — empty field, and the probe run died with "Answer
file is missing required key 'certificate.thumbprint'", because the contract check equated mandatory
with non-empty. Fixed; then reproduced with a real answer file (empty thumbprint) against `-Mode Probe`:
exit 2 (`ExitProbeFailed`, the expected answer for a red blocking row), `check.certificate` reports
"No certificate selected" with `canAutoFix=1` and `autoFixDefault=0`. What is **still** not clicked:
the tick itself and writing the generated thumbprint back into the field — that is, the second half of
row 39.

Addendum 2026-08-04: the unattended path was run against CM1 in **both** directions. `httpPort: 80`
aborts after 7 s with exit 7 — service, binaries and configuration demonstrably unchanged, `healthz`
200 throughout — while `httpPort: 0` installs through with exit 0. The port row of the readiness **page**
(18/19) has therefore still not been clicked, only the check behind it.

Addendum 2026-08-05: the database-access check against a real SQL Server 2022, all three verdicts —
existing `db_owner` → green (also with a differently cased user name, because it is resolved by SID),
login without the role → red with a fix offered, login absent entirely → red. `autoFixDefault=1`
demonstrably arrives in `probe.ini`. The fix itself was run twice in a row: the second time `Pass`
without changes. Case 30 end to end: the database did not exist, `/VERYSILENT /ANSWERFILE` with
`createDatabaseAndLogin` → exit 0, database + login + `db_owner` created, 36 tables migrated, `healthz`
200. Counter-test without the key: exit 7 in the pre-flight, nothing touched. What is still missing is
the **page** (27–29): the checkbox has never been clicked.

Addendum 2026-08-05 (PostgreSQL): against a purpose-built PostgreSQL 16 with `ssl = on` and
`sslmode=verify-full`, seven cases. Role and database missing, superuser present → red, both named
explicitly, fix offered; the same without a superuser → red, German server message verbatim, no fix. The
fix creates both and signs in as the role to verify; a second run changes nothing (`Pass`); re-check
green without a checkbox. The fix with an account lacking `CREATEROLE`/`CREATEDB` → `Skipped`, and
verified in the catalog: **nothing** created. Wrong role password with the role present → red with "both
present", no fix, password unchanged.

The cluster answered in German — which changed the design: the original error classification read psql
messages and would have passed "Rolle »nodepilot« existiert nicht" through as "rejected". Since then
`pg_roles`/`pg_database` are queried rather than parsed.

Addendum 2026-08-05 (identity change): reported case reproduced — install as LocalSystem, then fresh
with a gMSA. Two defects, both fixed and re-measured.

First, the service wrote `jwt-secret.key` with itself as owner and **one** ACE for itself; after the
change the new identity could no longer reach its own file ("the file, its owner, or its ACL could not
be verified"). The installer now hands it over.

Second — and this was the worse part — the **rollback** left the new identity's ACE on the data
directory. From the restored identity's point of view that is an *untrusted principal* with mutation
rights on the parent directory of the JWT key, so the restored installation no longer started either:
"ROLLBACK ALSO FAILED" in the log, and on screen the message about "grants mutation rights to an
untrusted principal" — which came from the *rolled-back* service, not the new one. A failed identity
change thereby took the running installation down with it. Re-measured: remove the ACE and the service
starts again.

Afterwards, both against CM1: gMSA installation over LocalSystem → exit 0, service runs as
`CORP\q-sdvorch2$`, key ownership moved with it. Forced failure after the ACL step → exit 7, service
**still running** under the old identity, directory ACL and key ownership restored.

Found and fixed along the way: an `AllowedHosts` list without `localhost` made the installation fail on
its **own** health probe — `UseHostFiltering` answers `Host: localhost` with 400, but the probe goes to
`https://localhost:<port>/healthz/ready`. The result was a rollback after a successful migration, with
"did not report /healthz/ready within 180s" as the only hint. The installer now always appends
`localhost`. Foreign hosts stay rejected (measured: `Host: evil.example` → 400).
