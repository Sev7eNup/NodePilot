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

**1. The Kestrel certificate** in the machine store on the target server:

```powershell
Import-PfxCertificate -FilePath cert.pfx -CertStoreLocation Cert:\LocalMachine\My `
  -Password (Read-Host -AsSecureString)
```

`MachineKeySet|PersistKeySet` is the default for this call and is mandatory — without a persisted
machine key, `Grant-CertPrivateKeyAccess` cannot find the key file later and the installation aborts.
Valid, and issued for the public hostname: an expired certificate is a red blocking row, a mismatched
name is only a warning.

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

Only on the gMSA path: create the account in AD, allow the host to retrieve its password, and run
`Install-ADServiceAccount` on the target server. Access to the certificate's private key is handled
by the installer.

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
| 7 | Network and TLS | Public hostname, HTTPS port, HTTP port (**`0`** = no redirect), allowed hosts, thumbprint — the list below fills the field, **empty** means "I don't have one yet" |
| 8 | Optional content | Whether to install the product source code (~27 MB). Ticked by default; see below |
| 9 | Prerequisites | Ten check rows; red blocking rows disable "Next". Where a checkbox appears: tick it, "Next" runs the fix and **re-checks** |
| 10 | Installation | Runs with a progress bar and phase text, 2–3 minutes |
| 11 | Finish | URL, credentials or setup token, paths, certificate — and the **external-trigger API key, which appears only here** |

First login: enter the setup token in the field the sign-in form reveals on the first attempt.

### The source-code option

The server artifact carries a snapshot of the product source under `knowledge\source` — around
2500 files and 27 MB — which the AI assistant reads for source-code questions. Untick the box on
page 8 and it is not kept on the machine. Everything else is unchanged; only that one knowledge
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

## What it takes off your hands — and what it does not

| | |
|---|---|
| **Takes off your hands** | Downloading five release assets, comparing checksums, verifying the signer thumbprint out of band, typing nine parameters without a mistake, digging the Kestrel thumbprint out of the certificate MMC. One asset, one double-click. |
| **Takes off your hands (opt-in)** | Installing the ASP.NET Core runtime, creating the SQL login and database, **creating the PostgreSQL role and database**, generating a self-signed Kestrel certificate, trusting the publisher certificate. |
| **Does not** | Creating the gMSA (an AD task), TLS for the database, Kerberos delegation, antivirus exclusions. |

The **readiness page** checks all of that *before* anything is changed — ten rows: .NET runtime,
Kestrel certificate, **HTTP/HTTPS ports**, gMSA, service identity, domain membership, database
reachability, database version, **the service identity's database access**, and **the artifact's
publisher**. The runtime row explicitly checks for the **64-bit** runtime: NodePilot is published as
`win-x64`, and a 32-bit host cannot start the apphost — a row that let any architecture through would
be green while the service still failed to start. Every row carries a status glyph on the right —
check, cross, exclamation mark or dash — and is colour-coded as well. The glyph is not decoration:
colour alone means nothing to someone who cannot tell this green from this red, and certainly not in
a screenshot pasted into a ticket. Red blocking rows disable "Next" — the installer would abort
anyway, and a wizard that walks you into a guaranteed failure is worse than one that stops.

Clicking a row shows the matching instructions below it — a **read-only, scrolling `TNewMemo`**.
This used to be a label, because a memo next to eight fixed-height check rows was only one line tall
and looked like a broken input field; now that the rows are laid out dynamically that reason no
longer applies, and instructions do not end after five lines: a database fix is a
`CREATE LOGIN` / `CREATE USER` / `ALTER ROLE` block. A side effect that would have justified the
change on its own: the text is selectable again. "Save to file…" stays regardless, because Inno
Pascal has no clipboard API.

The rows are only positioned once their text is known. Previously each of the eight rows reserved
16 px for an auto-fix checkbox that is almost never visible — 128 px of a 309 px area for nothing.

### Auto-fixes

Red rows the adapter can repair itself carry a checkbox. Tick it, press "Next", and the fix runs and
**re-checks afterwards** — a fix never counts as successful merely because it ran. The tick is
consumed by the attempt: it is cleared afterwards, otherwise a permanently failing fix (typically:
no permissions on the SQL Server) would loop between "Next" and the same red row.

**Publisher** is the row with the most unusual history. It used to be red and blocking, because
`Install-NodePilot.ps1` verified the signature **including the certificate chain** and
`CN=NodePilot Release Signing` is self-signed: on every host that did not know it — that is, on every
first installation — verification failed mid-run, with a rollback. The row anticipated that.

The installer now verifies the signature **without the chain** against the compiled-in thumbprint
(plus code-signing EKU, KeyUsage and validity explicitly — the part the chain used to supply). For a
self-signed publisher the trust anchor is the same certificate, so the chain only confirmed what the
pin already says. That makes the row **amber and optional**: importing into `LocalMachine\Root` now
only causes Windows to validate the Authenticode signature of the installers themselves — offered,
**not** pre-ticked, with the thumbprint in the message.

What still makes the row **red** is everything the installer itself will reject: thumbprint ≠ pin,
expired or not yet valid, missing code-signing EKU, a KeyUsage without
`DigitalSignature`/`NonRepudiation`, or a chain error that is **not** merely missing trust. In all of
those cases the offer disappears — an import repairs none of them, and a button that makes a foreign
CA machine-wide trusted would be worse than a refused installation.

An empty thumbprint field is the route to exactly one of these fixes: the certificate row then
reports "No certificate selected" instead of a thumbprint it could not find, and offers to generate a
self-signed one — **not** pre-ticked, because a lab certificate is created on request, not by pressing
"Next". For an **expired** certificate, generation is deliberately not offered (see below).

One row comes **pre-ticked**: the service identity's database access. The pre-flight tests
reachability using the installing administrator's identity — but at runtime it is the *service* that
signs in, under the computer account (LocalSystem) or the gMSA. This row asks exactly that (login
present? user in the target database? `db_owner`?) and creates it if needed. That is part of
installing, not an intervention in someone else's infrastructure — unlike `CREATE DATABASE`, which
stays opt-in. The tick is visible and can be cleared regardless.

The fix runs through `Provision-NodePilotDatabase.ps1`: first a permission gate (`sysadmin` or
`CREATE ANY DATABASE`), then existence-checked login → database → user → `db_owner`. Without the
permissions, **nothing** is changed and the wizard shows the instructions for the DBA.

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

During installation the wizard shows a phase and a progress bar. It used to sit on "Preparing to
Install" and show **nothing** — measured at 136 s for a successful run, 187 s for one that runs into
the health-probe timeout. Long enough for Windows to grey out the window and add "Not responding";
that is exactly how it was read.

The cause was `Exec` with `ewWaitUntilTerminated`: synchronous, blocking Inno's UI thread completely.
**Only the installation** therefore now runs detached (`ewNoWait`); probe, provision, certificates and
cleanup stay synchronous, as those finish in seconds.

Four points about this that are not obvious:

- **The exit code comes from `result.ini`, not from `Exec`.** With `ewNoWait` there is none — the
  process is still running. The adapter writes the file in a `finally`, so it exists on the rollback
  paths as well. What is checked is not its existence but whether `summary.exitCode` is in it:
  `WriteAllLines` is not atomic, so the file can be present and half-written.
- **Inno has no message pump.** `AppProcessMessages`, `ProcessMessages` and `Application` are all
  unknown identifiers in 6.7.3 (measured). The loop therefore calls `ProgressPage.SetProgress` once
  per tick — the mechanism Inno provides for long operations.
- **The progress comes from the output of the installer scripts**, not from new messages added to
  them. `Install-NodePilot.ps1` (10 phases) and `Update-NodePilot.ps1` (4) are unchanged; the adapter
  translates their `Write-Step` lines into `percent|text` in passing. Matching is by **prefix**,
  because several headings embed a value (`Stopping service '$ServiceName'`). That is safe because
  `Write-Step` writes flush left and `Write-Info` indents — a detail line starts with a space and
  cannot prefix a phase name.
- **The drift contract runs in both directions.** Every table entry must exist in the script *and*
  every `Write-Step` in the script must be covered by an entry. The second direction was missing at
  first, and that is exactly what slipped through: the updater reports four phases, the table knew
  two; the installer ten, the table nine. The bar stood still for half the update runtime without a
  single test turning red.
- **No cancel.** A half-installed system is worse than waiting three minutes.

The bar **stands still** during "Starting service" — that phase waits up to 180 s for
`/healthz/ready`. The text says so. A bar that kept moving artificially would claim progress nobody
is measuring.

A 45-minute timeout bounds the loop. It only triggers if the adapter was killed outright and
`result.ini` never appears — otherwise the wizard would wait forever.

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

Below the *Certificate thumbprint* field there is a list of the certificates in
`Cert:\LocalMachine\My`. Selecting one writes the thumbprint **into the field above** — that field
remains the single value read by the answer file, the validation and the write-back path of the
self-signed certificate. That is why the list required no change at any of those places.

**An empty field is allowed** and means "I don't have one yet". The page only checks the length if
something is actually there; the decision happens on the readiness page, which turns the certificate
row red and offers to generate one. Previously the page unconditionally demanded 40 characters — and
in the same message told you to leave the field as it was. On a machine with no certificate at all,
the only way to reach the offer was to invent 40 hex characters.

The reason for the list is the route it replaces: the only other way to get the thumbprint of an
already-installed certificate is the certificate MMC, whose copy button prepends an **invisible
U+200E**. That is exactly why `Install-NodePilot.ps1` strips all non-hex characters before measuring
the length — 40 characters, looks right, still rejected.

Four decisions that are not obvious:

- **A dedicated adapter mode, not the probe.** `-Mode Certificates` only reads the certificate store:
  no answer file, no session directory, no database connection. The probe does not run until the
  readiness page, i.e. one page *after* the one where the thumbprint is typed — and it is allowed to
  spend seconds on a network timeout.
- **Never blocking.** If the list cannot be read, a row appears saying so, and the thumbprint is typed
  as before. The readiness page checks it anyway. A convenience feature that stops a working
  installation would be a bad trade.
- **Certificates without a private key are shown**, marked `NO PRIVATE KEY`, rather than filtered out.
  "It is in the store, why isn't it listed?" has one common answer — a `.cer` was imported where a
  `.pfx` was meant — and a filtered list turns that into a riddle.
- **Every row carries the thumbprint**, not just subject and expiry. On CM1 there are two certificates
  with the same subject **and** the same expiry date ("NodePilot Lab HTTPS" and "NodePilot Lab SQL
  TLS", issued 39 seconds apart): without the thumbprint those were two identical rows, and the wrong
  one would have handed Kestrel the database certificate without comment. Side effect: the value can be
  checked against a supplied thumbprint instead of being taken on trust.
- **Sorted by expiry, latest first.** A renewed certificate sits next to the one it replaces under the
  same subject; the date is the first thing that tells them apart.

**Layout.** The list is on the same page as the five input fields — which required re-flowing the
page. Inno budgets 54 px per label+field pair while the controls actually need ~43 px; five pairs
therefore occupied 270 of the 309 px available and the list was drawn **below the visible edge**. An
input page does not scroll and gives no hint that there is more. The re-flow measures the controls
instead of hard-coding constants, and a clamp additionally forces the list to end inside the area. The
alternative would have been a second page — five values that belong to one decision, split across two
screens.

**What the readiness page checks:** presence in the machine store, the private key, the **validity
period** and a **name match**. An expired (or not-yet-valid) certificate is a red blocking row and
stops the installation — previously the expiry was only a date in the green row, the installation ran
through, and the first person to find out was a user with a browser warning. There is deliberately
**no** auto-fix here: answering "your PKI certificate has expired" with "here, have a lab certificate"
would be worse than stopping.

The name match runs against the SAN list (wildcards cover exactly one label, RFC 6125; without a SAN
the CN counts) and is **only a warning** — behind a reverse proxy or under an alias a different name
is legitimate, and "Next" stays available. It reads `DnsNameList` from the PowerShell certificate
provider, not `Extensions.Format()`: that output is localized (`DNS Name=` vs. `DNS-Name=`), so a
parser built on it works on an English host and silently finds nothing on a German one.

**What still nobody checks:** whether the chain is trusted by the clients. For a PKI certificate from
your own CA that is the normal case — it has to be in the machine store beforehand, and the setup
asks for nothing more:

```powershell
Import-PfxCertificate -FilePath cert.pfx -CertStoreLocation Cert:\LocalMachine\My `
  -Password (Read-Host -AsSecureString)
```

Without `MachineKeySet|PersistKeySet`, `Grant-CertPrivateKeyAccess` later fails to find the key file
under `ProgramData\Microsoft\Crypto` and aborts.

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
yet" and then requires `provisioning.generateSelfSignedCertificate`. If something is there, it must be
40 hex characters — otherwise the run aborts here instead of later in the Kestrel configuration.
For `"mode": "update"`, `installPath` and `serviceName` are enough; every further key is rejected, so
that a stale file is not half-applied.

**Optional keys at a glance:**

| Key | Effect |
|---|---|
| `serviceDisplayName` | Display name of the service |
| `database.sqlCertificateHostName` | Leave empty → the installer derives it from `sqlServer` |
| `network.allowedHosts`, `network.knownProxyIps` | Host filter and trusted proxy IPs. The installer always appends `localhost` — its own health probe goes there |
| `certificate.source` | Purely documentary |
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
| 3 | Preparation failed (Inno) |
| 7 | Installation failed — the message is in the log, the installer has already rolled back |

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

The ASP.NET Core runtime is fetched at build time and verified three ways: against the **SHA512**
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
- **The GUI has never been clicked.** All lab runs were unattended. The interactive path — readiness
  indicators, auto-fix checkboxes, certificate selection — is untested.
- **Only SQL Server + gMSA tested.** The PostgreSQL path and the LocalSystem path have never run in the
  lab.
- **Only Windows Server 2025.** Server 2022 (the `MinVersion`) is untested.

### Manual smoke matrix before each release

| # | Case | Expectation |
|---|---|---|
| 1 | Fresh, SQL Server + LocalSystem | Service runs, `/healthz/ready` 200 |
| 2 | Fresh, PostgreSQL + gMSA | ditto |
| 3 | Repeat run over an existing installation | Update semantics, configuration preserved |
| 4 | `/FULLREINSTALL` | Confirmation dialog appears, new API key |
| 5 | `/VERYSILENT /SUPPRESSMSGBOXES /ANSWERFILE` | Exit 0, service runs |
| 6 | Runtime missing, offer declined | "Next" stays disabled |
| 7 | Red readiness row | "Next" disabled, instructions visible |
| 8 | Cancel mid-wizard | No session directory left behind |
| 9 | Uninstall without switches | Everything gone, data and database remain |
| 10 | Uninstall `/PURGEDATA=1` | Data directory gone as well, database remains |
| 11 | Mode page → "Remove" | Uninstaller takes over, setup closes without a cancel prompt |
| 12 | Reboot after installation | Service comes up unattended, even if the database becomes ready later |
| 13 | Finish page after a new installation | URL, setup token, API key, thumbprint, paths visible and selectable |
| 14 | Finish page after an update | URL from the installed configuration, no token, no new API key |
| 15 | Update over a running service | Waits for the process instead of aborting |
| 16 | TLS page, certificate chosen from the list | Thumbprint appears in the field above, readiness row green |
| 17 | TLS page, empty certificate store | Informational row instead of a list, "Next" stays available |
| 18 | HTTP port 80 on a host with IIS | Readiness row red with "reserved by Windows", "Next" disabled |
| 19 | HTTP port 0 | Row green, "HTTP disabled" |
| 20 | Interactive installation | Bar and phase text advance, window stays responsive, no "Not responding" |
| 21 | Unattended installation | No UI, exit code unchanged |
| 22 | Unattended with `bootstrap.adminUsername` | Exit 0, credential file present (ACL SYSTEM + Administrators only), sign-in works without a token, `admin-setup.token` gone |
| 23 | Unattended without a `bootstrap` group | As before: token on the finish page, no account |
| 24 | Unattended with a `seed` group, empty instance | Sign-in with a user **from the backup**, no token, no credential file, seed file deleted |
| 25 | The same seed against a populated instance | Nothing happens, no duplicates, seed file left in place |
| 26 | Wrong seed passphrase | Service does **not** start, the message names the passphrase, no partial data in the database |
| 27 | SQL Server without a login for the service identity | Row red **and pre-ticked**, "Next" creates login + user + `db_owner`, green afterwards |
| 28 | The same run with the login already present | Row green without a checkbox, nothing is changed |
| 29 | Fix without `sysadmin` | Nothing changed, the message names the reason, tick cleared afterwards (no loop) |
| 30 | Unattended with `provisioning.createDatabaseAndLogin` | Exit 0, database + login created, `/healthz/ready` 200 |
| 31 | Expired certificate selected | Row red with the expiry date, "Next" disabled, no auto-fix offered |
| 32 | Certificate with a foreign SAN | Row **amber**, names both names, "Next" stays available |
| 33 | Postgres without role/database, superuser supplied | Row red with a checkbox, "Next" creates both, re-check green |
| 34 | The same without superuser fields | Row red **without** a checkbox, server message verbatim, snippet visible |
| 35 | Postgres with a wrong role password | Row red, says "both present", no fix offered, password unchanged |
| 36 | Installer built without `-PgBinariesPath`, Postgres selected | Row **amber**: reachable, sign-in unverified |
| 37 | New installation with gMSA over a LocalSystem installation | Exit 0, service runs as the gMSA, `jwt-secret.key` now owned by the gMSA |
| 38 | Failure **after** the ACL step | Rollback restores the service **and** the directory ACL, the previous installation keeps running |
| 39 | Thumbprint field left empty | "Next" leads to the readiness page, row red with "No certificate selected" plus a **not** pre-ticked offer; tick + "Next" creates one, writes the thumbprint back into the field, re-check green |
| 40 | Field filled with 12 characters | Message "40 hexadecimal characters", the page stays put |
| 41 | Host that does not know the publisher | Row "Artifact publisher" **amber**, "Next" stays available, installation completes **without** an import |
| 42 | The same case, offer ticked | Tick + "Next" imports into `LocalMachine\Root`, re-check green, afterwards `Get-AuthenticodeSignature` on the setup `.exe` reports `Valid` |
| 43 | Expired publisher certificate | Row **red**, "Next" disabled, **no** offer (an import does not repair it) |
| 44 | Only the **32-bit** or an older runtime (< 10.0.11) installed | Row "ASP.NET Core 10.0.11+ runtime" **red**, naming the architecture or the version found together with its path, offer present |
| 45 | x64 runtime present, x86 first in `PATH` | Row **green**, naming the 64-bit host it queried |

Status: 1, 3, 5, 9, 10, 22, 23, 30, 37 and 38 have been run in the Hyper-V lab against real AD, a real
gMSA and SQL Server 2022 CU. On **2026-08-06** the **logic** behind 39, 40, 41, 43 and 44 was added —
through the adapter against the running CM1 installation (`InitSession` → `Probe` → `Certificates` →
`Cleanup`, certificates as in-memory fixtures, no store writes): all ten rows green, an answer file with
an empty thumbprint accepted with exit 2, one with twelve characters rejected with exit 3, `Cleanup`
left behind neither the session directory nor the answer file, the service still `Running` afterwards
and `/healthz/ready` 200. What is missing there, as with 33 to 35, is the **page**.

Open: 2, 4, 6, 7, 8, 11 to 21, 24 to 29, 31 to 36, 42 and 45. 42 would write machine-wide into
`LocalMachine\Root`; 45 needs a host with **both** runtimes — demonstrated on the development machine
(x86 first in `PATH`, row stays green and names the x64 host), and there is no 32-bit runtime in the
lab. The **logic** behind 33 to 35 ran against a real PostgreSQL 16 with TLS (see below).

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
counts the visible fix boxes up front and guarantees each one a clickable strip above the buttons. Rows
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
