# Server installer tests

[Installer setup and configuration](README.md).

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
