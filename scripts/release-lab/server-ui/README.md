# Optional server installer acceptance

This is release step **6a**, an optional supplement to the mandatory [release lab](../README.md).
It covers the server installer cases exercised during the GW1 installer review. It does not run
automatically and does not replace either setup matrix or the workflow suite. If omitted, record
`Not run (optional)` in the release evidence. If selected, record every case below, including
blocked cases and their reason; a partial run is not a full installer-acceptance pass.

The steps are independent of old development-build numbers, old passwords and ignored `out/`
fixtures. Use the candidate for the release being tested and a **real published older installer**.
Record installer version and embedded application version separately: a development installer may
deliberately carry an unchanged development application. Reinstalling the same payload does not
prove an upgrade from an older application version.

## Execution and evidence

1. Record commit, dirty-tree state, Windows build, VM name, console mode, guest resolution/scale,
   candidate EXE and payload versions/SHA256, previous published version/EXE SHA256 and source URL.
   Verify the candidate against the release checksum/signing material before copying it; compare
   the destination hash. Do not modify a release EXE or its signed payload for a fault test.
2. Use Computer Use for every `UI` case: native input and screenshots in the guest's Hyper-V
   console. PowerShell/SQL/API may prepare fixtures and independently verify results. A command
   succeeding, source inspection or an API probe cannot stand in for a dialog observation.
   `CLI` cases intentionally test silent commands; `AUTO` cases test script contracts.
3. Start from the stated baseline. Run only one wizard at a time on a VM. Use **Check again** after
   changing a fixture and wait for completion. Do not count a cached result as a new probe.
4. For **every prerequisite row**, scroll the list to its actual last item, select the row, and read
   the detail from the top in overlapping Page Down/wheel steps. Capture every segment of long
   text, then Ctrl+End and an extra scroll to establish the real end. At high DPI, scroll the outer
   page separately from the inner detail. Record the final sentence/command; seeing a scrollbar
   or only the first paragraph is not complete-text coverage. Apply the same rule to error and
   completion summaries. Compare title, severity, body, offered fix and final instructions.
5. Keep an execution ledger outside the repository. One row per case **and variant**:

   ```text
   caseId,variant,method,status,installerSha256,payloadVersion,baseline,scale,resolution,startedUtc,finishedUtc,evidence,observation,cleanup
   ```

   Status is `Not run`, `Pass`, `Fail`, `Blocked` or `N/A (reason)`. Duplicate rows for retries;
   retain failures. Evidence names start with the case ID, e.g. `PG-03-03-end.png`, plus setup/log
   paths and independent probe JSON. A pass needs its expected result and cleanup, not just a
   screenshot of the finish page. Store credential-bearing screenshots/logs with restricted access;
   publish only redacted evidence. Never commit answer files, PFX/private keys, tokens or passwords.
6. A fix means a new candidate hash. Repeat affected cases and one complete real installation or
   update; record unchanged cases under their earlier hash. To claim the whole matrix on the final
   candidate, repeat all real cases on that candidate. Preview rows remain explicitly synthetic.

For a complete run, use this order: AUTO, NAV with previews, real prerequisites/certificates,
PostgreSQL variants, real installation/progress, lifecycle/silent cases, synthetic failures, DPI,
then restoration. Dependencies below identify the cases which share an installation.
LIFE-07/SIL-05 and LIFE-09/SIL-06 describe the same executions from different groups; one run may
supply both rows if all assertions are recorded. Expand multi-variant cases into separate ledger rows.

To create a blank ledger directly from the tables (repository-root shell; destination parent must
already exist), use:

```powershell
$ledgerPath = 'C:\lab-cred\installer-run01.csv'
if (Test-Path -LiteralPath $ledgerPath) { throw 'Choose a new ledger path.' }
$rows = Get-Content scripts/release-lab/server-ui/README.md | ForEach-Object {
    if ($_ -match '^\| ([A-Z]+-[0-9]{2}) \| ([^|]+) \|') {
        [pscustomobject][ordered]@{
            caseId = $Matches[1]; variant = ''; method = $Matches[2].Trim(); status = 'Not run'
            installerSha256 = ''; payloadVersion = ''; baseline = ''; scale = ''; resolution = ''
            startedUtc = ''; finishedUtc = ''; evidence = ''; observation = ''; cleanup = ''
        }
    }
}
[IO.File]::WriteAllLines($ledgerPath, [string[]]($rows | ConvertTo-Csv -NoTypeInformation),
    (New-Object Text.UTF8Encoding($false)))
```

## Baselines and fixtures

Use a disposable domain-member server VM such as GW1, with the prerequisites in the main lab
README: SQL Server 2022 CU1+, PostgreSQL 16 with TLS/root PEM and current CRL, an existing AD gMSA
whose password the host may retrieve, and a hostname-valid HTTPS certificate with its private key.
The installing account needs the relevant SQL/AD rights; PostgreSQL administrator credentials are
supplied separately. Keep those credentials in the protected lab configuration, outside Git.

Take and verify a restorable checkpoint **with the VM off** before testing. Record its exact name.
If preserving a long-running installation instead, first back up binaries, protected data, service
settings, firewall rules, `HKLM\SOFTWARE\NodePilot\Server`, the NodePilot Inno uninstall entry and
shortcuts; verify hashes and backup completeness. Protected files may require backup privilege
(`robocopy /B`). A proposed/unverified checkpoint is not a backup. The single machine-wide marker,
uninstall entry and shortcut can be replaced by another installation: test instances are sequential,
and every removal must first verify its service name, program path and data path.

| Baseline | Required state |
|---|---|
| B0 clean | No NodePilot, no .NET/ASP.NET runtime or local AD tools/gMSA preparation; SQL/PG servers and domain account exist. Use for actual prerequisite installation. |
| B1 SQL | Working gMSA/SQL installation with known admin and saved smoke workflow; valid original HTTPS certificate. |
| B2 PG | Working LocalSystem/PostgreSQL installation with known admin and saved smoke workflow; imported test PFX. |
| B3 older | Real published older setup installed into a custom directory, admin created and smoke workflow executed before upgrading. |
| B4 draft | B1 plus a new setup in “from scratch” mode. Changes to page inputs only; cancel before installation except in explicitly named repair/reconfiguration cases. |
| B5 preview | Inert wizard built from the candidate's source with the helper below. Cannot install or provision. |

Choose unique names per run, for example `C:\NP-InstallerUI\run-01`, service `NPUI`, SQL database
`NodePilotUiSql`, PostgreSQL role/database `np_ui_pg`, second database `np_ui_pg_second` and an
absent role `np_ui_absent`. Verify absence before creating anything. Custom HTTPS 8443/9443 and
HTTP 0 avoid the original instance. Record actual names in the ledger. Create the run directory
with inheritance disabled and access limited to SYSTEM, Administrators and the test operator before
writing secrets. Do not reuse another run's objects or “repair” an occupied fixture name.

Prepare three HTTPS fixtures in that directory: an exportable, hostname-valid certificate/PFX with
roughly 29 days remaining; its password; and a public PEM for the PostgreSQL root. Export the test
PFX, record its thumbprint, then remove only that certificate from `LocalMachine\My` before testing
import. Keep the original certificate installed. Also have an intentionally wrong PFX password,
a nonexistent 40-hex thumbprint (40 zeroes), a different public hostname, an occupied HTTP.SYS
port (normally IIS/80), an unused TCP port and a nonexistent gMSA name. Do not stop existing IIS
or database services to manufacture these draft-only cases.

Use the existing [smoke workflow](../smoke-workflow.json). For every installed baseline record:
`VERSION.txt`, service identity/state, configuration SHA256, data directory, certificate thumbprint,
admin username and saved workflow ID. Independently probe `/healthz/ready` (200), sign in with
the same admin and execute **that same workflow** to `Succeeded` after updates/rollback/reinstall.
Existing release-lab HTTP/probe code is in `../server/Invoke-GuestScenario.ps1`; it also handles the
lab's Schannel quirk. Keep health, application smoke and UI assertions distinct. Compare config
hashes before/after updates, rollback and reboot; a hash of a protected file that could not be read
is not evidence of preservation. A fresh/reconfiguration install regenerates configuration and a
trigger key, including reinstall after keep-data removal. Check its intended settings separately;
preservation of database users/workflows does not imply a byte-identical configuration file.

## Navigation and review

These use B5 `Wizard` for isolated validation/layout, then the real candidate in B4 for real file
pickers and values. `Layout` preview deliberately bypasses validation and cannot pass NAV-02/06.

| ID | Method | Starting state and actions | Expected result |
|---|---|---|---|
| NAV-01 | UI | Open setup, decline then accept license; navigate forward. | Acceptance gates navigation, all buttons usable. |
| NAV-02 | UI | LocalSystem -> SQL Server; leave SQL host empty, Next, dismiss message. | Specific required-host message; focus returns to host. |
| NAV-03 | UI | Enter SQL host/database and network values; Back twice, then forward. | Entries retained, no unexpected resets. |
| NAV-04 | UI | Select PostgreSQL, enter server/port/database, continue to credentials; toggle database preparation off/on. | Separate server/credentials pages; both administrator fields only when enabled; labels match subsequent hints. |
| NAV-05 | UI | PEM Browse; select the real root PEM, Back/forward. | Native picker works; selected path retained. |
| NAV-06 | UI | Select PFX import, leave file empty and Next. | File/Browse/password controls visible; specific missing-path message prevents proceeding. |
| NAV-07 | UI | PFX Browse; select real test PFX from local path or staging share. | Native picker accepts file; correct path shown. |
| NAV-08 | UI | Select existing certificate with no PFX; refresh list containing original and test certificates. | PFX optional; subject, thumbprint and expiry distinguish certificates; selected original is used. |
| NAV-09 | UI | Type public host, expand/collapse Advanced host settings, enter allowed hosts. | URL updates; fields, URL and port button do not overlap; values survive Back. |
| NAV-10 | UI | Find available ports; review and accept suggestion. | Only checked ports suggested; HTTP can be disabled; own existing service port is handled correctly. |
| NAV-11 | UI | Review configuration with source snapshot selected, then unselected. | Full multiline address/path summary and checkbox visible; choice reaches installation. |
| NAV-12 | UI | At 200%, click each mode/provider/identity/source choice once. | First click selects intended row without focus scrolling or a missed selection. |

## Prerequisite list, actions and common text

Use the full-reading procedure above for each row and every state in these tables. Baseline SQL/gMSA
has ten checks; PostgreSQL/LocalSystem may have a different set. Record the actual rows, counts and
last row instead of assuming a fixed count for future versions.

| ID | Method | Starting state and actions | Expected result |
|---|---|---|---|
| PRE-01 | UI | B1/B4 valid SQL/gMSA; select every row, including rows below the initial list viewport. | Runtime, certificate, ports, gMSA, rollback identity, domain, SQL connection, TDS, service login and publisher all readable through end; summary matches rows. |
| PRE-02 | UI | B2/B4 valid PostgreSQL/LocalSystem; read every row. | gMSA skipped with actual computer account; PG authenticated success, runtime path, original service identity, Domain firewall scope and full signer thumbprint consistent. |
| PRE-03 | UI | B0 missing runtimes, AD tools/local gMSA preparation and SQL database; select fixes and Apply. | Selected prerequisites installed/prepared, progress shown, automatic recheck; only verified results pass. Account itself must already exist in AD. |
| PRE-04 | UI | Deselect an offered PFX/database fix, Check again; then select only the database repair and Apply while PFX is invalid. | Deselection retained on read-only recheck; Apply count correct; only selected repair runs; PFX still blocks Next. |
| PRE-05 | UI | After a successful repair recheck, inspect selections/counts and Next. | Consumed selections reset; Apply disabled at zero; Next enabled only without required failures and navigates without applying another fix. |
| PRE-06 | UI | Occupied HTTP.SYS HTTP port and different public hostname; read both hints. | Port failure separate from certificate-name notice; Network/port finder, both diagnostic commands and final HTTPS-vs-HTTP-redirect distinction consistent. Does not assert IIS always owns both ports. |
| PRE-07 | UI | B4 SQL database changed to an absent test name. | Missing/inaccessible database, original error, rights gate, available fix and DBA alternative readable; manual SQL has object guards, GO batches, SID mapping and final grant. Reading SQL is not executing it. |
| PRE-08 | UI | B4 nonexistent gMSA name, then LocalSystem. | gMSA lookup notice preserves error, explains service-start risk, ends with correct Test-ADServiceAccount identity; LocalSystem shows network computer account, no invented gMSA fix. |
| PRE-09 | UI | Save instructions on a failing prerequisite page; inspect saved file in Notepad. | Correct destination confirmation; complete instructions preserved, including tail commands. |
| PRE-10 | UI | B0/B4 complete a selected prerequisite repair, then Cancel/Yes. | Wizard closes; already installed prerequisites remain; no application install/reconfiguration occurred. Independently verify actual side effects. |

## HTTPS certificate variants

| ID | Method | Starting state and actions | Expected result |
|---|---|---|---|
| CERT-01 | UI | Empty certificate selection; read detail. | Required failure; lab self-signed option is explicit and unchecked; GUI PFX alternative, complete manual import and final select/recheck instruction. No certificate created just by Next/recheck. |
| CERT-02 | UI | Enter nonexistent 40-zero thumbprint. | Required failure; correct LocalMachine store and complete import/select/recheck guidance. |
| CERT-03 | UI | Select available certificate with about 29 days remaining. | Actual expiry displayed accurately; no contradictory missing-certificate advice. |
| CERT-04 | UI | Real PFX, wrong password. | Required failure; password/single-certificate/private-key explanation; no import offered as a successful repair and Next disabled. |
| CERT-05 | UI | Correct PFX password, deselect import, recheck; select import and Apply. | Subject/action visible, deselection retained; one explicit import; automatic recheck passes with correct certificate. Verify private key in machine store. |
| CERT-06 | UI | Existing hostname-valid certificate, then draft public-name mismatch. | Existing certificate path needs no PFX. Mismatch notice explains name/TLS termination; common import wording stays consistent through the final line. |

## PostgreSQL variants

Use existing lab PostgreSQL with valid root PEM unless stated otherwise. Keep a known valid role
and database. Administrator credentials may discover catalog state; without them the UI must not
guess from localized `psql` text. Prepare missing objects by choosing unused names, not by deleting
real objects. Run PG-04 before PG-05/06; PG-05 creates the role needed for PG-06. PG-03 is a read-only
role-only case against an existing database. Run PG-09 against the isolated fixture below.

| ID | Method | Starting state and actions | Expected result |
|---|---|---|---|
| PG-01 | UI | Wrong role password, administrator inputs disabled. | Original login error, catalog state unknown, credentials/DBA guidance and final recheck; no CREATE advice or offered repair. |
| PG-02 | UI | Wrong role password, correct administrator credentials; both objects exist. | Existing role/database explicitly confirmed; correct credentials page named; no recreation or password reset. |
| PG-03 | UI | Absent role, existing database, valid administrator. | Only role missing; fix label/manual CREATE ROLE/password prompt only, no CREATE DATABASE. Read-only, cancel without repair. |
| PG-04 | UI | Both role and database absent, valid administrator. | Combined repair; correctly quoted role, interactive password instruction and database owner statement through end. |
| PG-05 | UI | Continue PG-04: select only PG repair and Apply. | Only dedicated role/database created; automatic recheck verifies authenticated login. Inspect catalog ownership and privileges independently. |
| PG-06 | UI | Existing role from PG-05, second absent database; inspect and Apply database-only repair. | No CREATE ROLE/password-reset advice; database owned by existing role; same role/password authenticates afterward. |
| PG-07 | UI | Change draft port to a verified unused port, e.g. 65432. | Required network failure; host/port/DNS/firewall/listening-server advice through recheck; no pg_hba or CREATE advice for a TCP failure. |
| PG-08 | UI | Draft host 127.0.0.1 against DNS-only server certificate. | TLS hostname error preserved; root/host guidance, catalog state unverified, no speculative object creation or fix. |
| PG-09 | UI | Isolated server certificate with unreachable CRL; then explicitly revoked certificate with reachable CRL. | Both required failures without automatic repair. Unavailable status explains CRL publication/reachability/full certutil command/recheck; revoked status requires certificate replacement, not CRL import as a cure. Record separate variants and full hints. |
| PG-10 | UI | Replace only isolated server certificate with non-revoked one using same CA/reachable CRL; Check again. | Authenticated success with correct role/database/host/port; entire success text read. |
| PG-11 | UI | Remove only active setup extraction's psql.exe temporarily, Check again; restore exact file, recheck. | Missing client is Notice/unverified login, no false success/CREATE advice; complete package/credentials/DBA guidance. Restored client hash matches and authenticated success returns. |
| PG-12 | UI | Wrong role password and wrong administrator password, valid server/PEM. | Failed administrator catalog lookup explicitly unverified; valid-admin/DBA and conditional TLS/pg_hba guidance; no speculative repair, Apply zero and Next disabled. |

### Isolated CRL fixture for PG-09/10

Generate fresh certificates for every run: expired certificates or cached CRLs can otherwise test
the wrong branch. `New-RevocationCertificates.py` uses the existing Python `cryptography` package
on the preparation host (record its version); it generates a unique CA, three hostname-valid leaf
certificates and a current CRL which revokes only one serial. A unique `?run=` in each run's CRL
URLs prevents reuse of an earlier URL-cache entry; the HTTP helper accepts this query. No private key/certificate is checked
into Git. There is no need to import this CA into Windows trust.

```powershell
python scripts/release-lab/server-ui/New-RevocationCertificates.py `
  --output C:\lab-cred\ui-crl-run01 --hostname gw1.example.test `
  --crl-port 55412 --unavailable-port 55413
```

On the disposable guest, create a **new protected** `C:\NP-InstallerUI\crl-run01` and copy that
bundle plus `Serve-TestCrl.ps1` into it. Verify ports 55410, 55412 and 55413 are all unused. Use
the PostgreSQL 16 `bin` directory from the lab config. In a shell/fixture channel (not via Computer
Use typing terminal commands), initialize a separate cluster:

```powershell
$fixtureRoot = 'C:\NP-InstallerUI\crl-run01'
$pgBin = 'C:\PostgreSQL\pgsql\bin'
$fixtureCredential = Get-Credential -UserName np_ui_crl -Message 'Temporary isolated PostgreSQL role'
$pwFile = Join-Path $fixtureRoot 'init-password.txt'
try {
    [IO.File]::WriteAllText($pwFile, $fixtureCredential.GetNetworkCredential().Password)
    & "$pgBin\initdb.exe" -D "$fixtureRoot\data" -U np_ui_crl -A scram-sha-256 --pwfile=$pwFile --encoding=UTF8 --locale=C
    if ($LASTEXITCODE -ne 0) { throw 'Isolated initdb failed.' }
} finally { Remove-Item -LiteralPath $pwFile -ErrorAction SilentlyContinue }
```

Append the following to **that cluster's** `postgresql.conf`, substituting the guest's real IPv4
address. Write its `pg_hba.conf` with only the two shown hostssl rows. Do not edit the lab's main
PostgreSQL configuration. Paths in PostgreSQL config use forward slashes.

```text
# postgresql.conf
listen_addresses = '127.0.0.1,<guest-ipv4>'
port = 55410
ssl = on
ssl_cert_file = 'C:/NP-InstallerUI/crl-run01/unavailable.crt'
ssl_key_file = 'C:/NP-InstallerUI/crl-run01/unavailable.key'

# pg_hba.conf (separate file)
hostssl all all 127.0.0.1/32 scram-sha-256
hostssl all all <guest-ipv4>/32 scram-sha-256
```

Register/start a uniquely named demand-start service, then start the CRL HTTP fixture hidden:

```powershell
& "$pgBin\pg_ctl.exe" register -N NPUICrlRun01 -D "$fixtureRoot\data" -S demand
if ($LASTEXITCODE -ne 0) { throw 'Registration failed.' }
Start-Service NPUICrlRun01
$crlServer = Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList @(
    '-NoProfile', '-File', "$fixtureRoot\Serve-TestCrl.ps1", '-CrlPath', "$fixtureRoot\issuer.crl", '-Port', '55412')
```

Use the bundle's hostname, port 55410, database `postgres`, role `np_ui_crl`, its fixture password
and `root.crt` in the real wizard. Port 55413 must have no listener. For the revoked variant stop
**only NPUICrlRun01**, change both ssl paths to `revoked.crt/key`, restart and recheck. First verify
the exact `revoked` CDP URL in `manifest.json` returns 200 inside the guest. For recovery repeat with `valid.crt/key`.
Record certificate serials/CDPs, HTTP result, actual UI failure and success. Finish by canceling
setup, stopping/unregistering the exact fixture service and stopping the recorded CRL-server PID
after checking its command line. Verify all three ports closed. Restore checkpoint, or remove only
the resolved fixture directory after validating its path and absence of reparse points. Do not flush
machine-wide certificate caches or remove the main PostgreSQL CA/CRL.

### Missing client fixture for PG-11

Keep the real wizard open after a PG probe. Read the matching `powershell.exe` child command line
to locate its `Invoke-NodePilotSetup.ps1 -Mode Probe` and `-PayloadRoot`; there must be exactly one
matching wizard/worker. Record the path and `psql.exe` hash. After the probe finishes, rename only
that temporary extraction's client to `psql.exe.ui-disabled` in the same directory. Validate the
absolute target belongs to this setup's temporary extraction first. Recheck in the UI; then restore
the filename in a finally/cleanup step and compare its hash before the recovery recheck. Never
rename the installed PostgreSQL client, alter the release EXE or leave the extracted client disabled.

## Real installation, progress and finish

| ID | Method | Starting state and actions | Expected result |
|---|---|---|---|
| RUN-01 | UI | B0, gMSA/SQL, existing certificate, missing prerequisites prepared via PRE-03; Install. | Installed/running; real health, admin and smoke workflow pass; intended identity/certificate/paths. |
| RUN-02 | UI | Reconfigure SQL baseline from scratch to LocalSystem/PG; use PEM picker and PFX import/PG repair. | Real installation succeeds; imported certificate and PG configured; new credentials/trigger key are labelled as new. |
| RUN-03 | UI | During real install/update observe extraction, backup, copy, verification and service readiness. | Unknown phases retain activity animation; copy shows advancing MB/total, file count and filename; elapsed time visible; correct backup label; transition after copy does not repeatedly switch bar style. Capture multiple frames per phase. |
| RUN-04 | UI | During real install click Open setup log and read Notepad; return to setup. | Actual current transcript opens; install continues and log path matches case evidence. |
| RUN-05 | UI | Fresh-install finish: read entire summary, Save summary; inspect destination confirmation/file. | Correct URL, identity/paths/certificate and credentials/token where applicable; warning about secrets accurate. Keep evidence protected. |
| RUN-06 | UI | Update finish: Save summary with an existing fresh-install summary already present. | Separate nodepilot-update-summary.txt; no fabricated new credentials or secret warning; original credential summary not overwritten (compare hash/timestamp). |
| RUN-07 | UI | Finished SQL install on custom HTTPS port; Open NodePilot. | Browser reaches real NodePilot login URL with correct port. First-run browser setup alone is not a pass; certificate interstitial alone is not a pass. Do not bypass a TLS warning to claim success. |
| RUN-08 | UI | From installed PG test, reconfigure back to original gMSA/SQL/certificate. | All original settings shown at review/finish; backup phase correct; health succeeds with original identity/certificate. This is a reconfiguration, so do not claim trigger-key preservation. |
| RUN-09 | UI + probe | Install with source snapshot unchecked; update it. | Signed artifact still verified; knowledge/source absent afterward and remains absent after update. Record the choice and actual directory state. |

## Upgrade, failure recovery and uninstall

Run these on a disposable installation. Read Ready, failure, uninstall and finish text through the
end. The real rollback is separate from synthetic worker faults and must verify restored behavior.

| ID | Method | Starting state and actions | Expected result |
|---|---|---|---|
| LIFE-01 | UI + probe | B3 published older setup; install candidate as Update. | Correct older version/path detected; Ready names version/service/path and stop/backup/replace/restart/preservation; same config hash, admin and saved workflow survive; new application version running. |
| LIFE-02 | UI + probe | Current SQL installation; update candidate again at custom program/data paths and port. | Same-version update succeeds; completion uses custom data path, correct host/port and retained certificate; independent smoke unchanged. |
| LIFE-03 | UI + probe | Current PG/gMSA fixture; candidate Update at custom paths/9443. | Same preservation and completion checks as LIFE-02 with PostgreSQL. |
| LIFE-04 | UI + probe | Healthy fixture; arm dual-stack port fault below; update candidate. | Service readiness really fails, UI reports restoration and full error/log path; after fault release old service becomes healthy, original config/admin/workflow restored. No pass merely from exit 7. |
| LIFE-05 | UI + probe | After working installation, use Windows Start -> Restart; log in again. | Boot time changed; service/DB recover automatically and readiness 200 within recorded deadline (five minutes); same config, login and workflow. Record transient AD/SQL errors; manual service start invalidates automatic-recovery claim. |
| LIFE-06 | UI + probe | Choose Remove, keep data. | Full text explains database retention even if setup created it; service/program removed, data and DB retained; capture data hashes or explicitly limited metadata comparison. |
| LIFE-07 | CLI + probe | Reinstall into LIFE-06 paths with same DB and no new bootstrap provisioning. | Old admin signs in, same saved workflow runs; retained data accepted. |
| LIFE-08 | UI + probe | SQL fixture, choose Remove -> delete data directory. | Read decision/progress/success; service, binaries and custom data removed; SQL database retained. |
| LIFE-09 | CLI + probe | PG fixture, silent uninstaller /PURGEDATA=1. | Exit 0; service/binaries/data gone, PostgreSQL database and role retained. |
| LIFE-10 | UI + probe | Restore original baseline metadata if fixture displaced it; final candidate Update on original installation. | Correct marker, Inno version and single Switcher shortcut; original config hash/certificate/identity preserved, health 200; fixture services absent. Restore normal display and close all dialogs. |

### Real rollback fixture for LIFE-04

Copy `Watch-UpdateRollback.ps1` to the protected guest run directory. Run it before pressing Install,
under an elevated local account or SYSTEM scheduled task, hidden; pass the fixture service,
custom install directory, HTTPS port and a **new** evidence directory. Only one setup may run.

```powershell
$watcher = Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList @(
    '-NoProfile', '-File', 'C:\NP-InstallerUI\run-01\Watch-UpdateRollback.ps1',
    '-ServiceName', 'NPUI', '-InstallPath', 'C:\NP-InstallerUI\run-01\program',
    '-Port', '8443', '-EvidenceDirectory', 'C:\NP-InstallerUI\run-01\rollback')
```

The watcher validates the target, waits for that running service to stop, binds **both IPv4 and
IPv6** and releases both listeners as soon as the adapter reports rollback. Binding only IPv4 is
insufficient: Kestrel can still start on IPv6. Require `bound.txt`, `rollback.txt`, `released.txt`,
no `error.txt`, the actual setup failure/restore log and independent post-rollback probes. A setup
that succeeds is not rollback coverage. If no Apply worker/progress can be identified uniquely or
the timeout expires, the helper releases its ports and fails; inspect the run instead of relabelling
it passed. Remove any wrapper scheduled task and confirm no watcher/listener remains.

## Silent command reproduction

Run elevated **locally on the guest**, or in a highest-privilege interactive task using the logged-in
lab account. Nested WinRM may lack the AD/SQL credentials required by gMSA preparation: record
that failure separately, then repeat in the intended local context. Do not disable checks to make
it pass. The input format is the [server answer-file contract](../../../deploy/server/README.md#answer-file).

Create protected `sql-install.json` using the complete example there: unique service/program/data/
database names, `includeSourceSnapshot: false`, `network.httpPort: 0`, custom HTTPS port and known
existing certificate. Use `identity.type: localSystem` for the older SQL fixture or `gmsa` plus
account for the gMSA variant. Set `provisioning.createDatabaseAndLogin: true`,
`installDotnetRuntime: true`; for missing local AD setup also `installGmsa: true, installAdTools: true`.
Set `bootstrap.adminUsername` for a fresh DB and retain its generated protected credential file.

For `pg-install.json`, replace the database block with `provider: postgres`, `postgresHost`,
`postgresPort: 5432`, unique `postgresDatabase`, `postgresUser`, role password and
`postgresRootCertificate`. Add `provisioning.postgresSuperUser` and `postgresSuperPassword`.
For PFX import use `certificate: { source: pfx, thumbprint: "", pfxPath: ..., pfxPassword: ... }`
and `provisioning.importPfxCertificate: true`. Do not combine that with self-signed generation.
Resolve passwords from protected fixture configuration, never copy example placeholders into a run.

Create a separate minimal update file (JSON):

```json
{
  "schemaVersion": 1,
  "mode": "update",
  "installPath": "C:\\NP-InstallerUI\\run-01\\program",
  "dataPath": "C:\\NP-InstallerUI\\run-01\\data",
  "serviceName": "NPUI"
}
```

`dataPath` is optional for update; include it to check the custom-directory summary. Installation-
only keys are not valid update inputs. `/DIR` must equal the answer's `installPath`, or Inno's
uninstaller files can end up in a different directory. Use absolute quoted paths:

```text
candidate.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /ANSWERFILE="C:\NP-InstallerUI\run-01\sql-install.json" /DIR="C:\NP-InstallerUI\run-01\program" /LOG="C:\NP-InstallerUI\run-01\install-inno.log"
candidate.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /ANSWERFILE="C:\NP-InstallerUI\run-01\update.json" /DIR="C:\NP-InstallerUI\run-01\program" /LOG="C:\NP-InstallerUI\run-01\update-inno.log"
"C:\NP-InstallerUI\run-01\program\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /PURGEDATA=1 /LOG="C:\NP-InstallerUI\run-01\uninstall-inno.log"
```

Wait for the actual process completion (the uninstaller can spawn `_iu*`); record outer exit code
and adapter result separately. Use the existing release-lab process waiting pattern for PowerShell
Direct, where `Start-Process -PassThru` alone may not expose an exit code. Save Inno log, adapter
log and probe results for every attempt. Remove bootstrap/provisioning from the keep-data reinstall
answers; do not create a replacement admin to conceal lost data.

| ID | Method | Starting state and actions | Expected result |
|---|---|---|---|
| SIL-01 | CLI + probe | Clean fixture, real published older EXE and compatible install answers. | Exit 0, older VERSION.txt, admin login and stored smoke workflow succeed; establishes LIFE-01 baseline. |
| SIL-02 | CLI + probe | Existing SQL fixture; candidate with minimal update answers and matching /DIR. | Exit 0, unchanged config/admin/workflow; source choice retained. |
| SIL-03 | CLI + probe | Clean PG/gMSA fixture; candidate with PG provision/PFX answers. | Exit 0; database/role created, PFX imported, gMSA prepared; health/login/workflow succeed. |
| SIL-04 | CLI + probe | Copy valid update JSON and add unknown key `misspelledSetting: true`; run candidate. | Unknown key named; outer Inno 7/internal validation 3 for current implementation; service and config unchanged. Nonzero alone does not prove rollback. |
| SIL-05 | CLI + probe | LIFE-06 retained-data fixture; same install answers without bootstrap/provisioning. | Reinstall exit 0; retained data, original admin and workflow work (LIFE-07); regenerated configuration matches supplied paths/provider/identity/certificate. Do not require identical config/trigger key. |
| SIL-06 | CLI + probe | PG fixture after GUI update; run silent purge command. | LIFE-09 retention/removal checks plus actual exit 0 and complete logs. |

## Synthetic wizard failures

Build from the candidate's source with Windows PowerShell 5.1 and Inno Setup 6. Use a new output
directory for each kind/run; the helper refuses to overwrite one. For example, from repository root:

```powershell
powershell.exe -NoProfile -File scripts/release-lab/server-ui/Build-InstallerPreview.ps1 `
  -Kind Fault -OutputPath out/installer-acceptance/run-01/fault
```

Repeat with `-Kind Wizard` or `-Kind Layout` and separate directories when required. Copy the
resulting EXE and `preview-manifest.json` to the guest; retain the manifest with the evidence.
The EXE has a different AppId, runs without elevation, uses inert payloads and refuses installation
in both the wizard and adapter. `Wizard` keeps input validation for NAV cases; `Layout` bypasses
validation solely for layout inspection. All are **preview** evidence, never release-install evidence.
Use fictitious passwords/input values in previews; their temporary sessions are retained for
inspection and are not credential stores. Preview banners/license are placeholders, so inspect
actual branding/license text only on the real candidate.

On the guest, set `%TEMP%\nodepilot-installer-ui-scenario.txt` for the **same logged-in user** using
a fixture/shell channel. Valid contents: `pass`, `repair`, `crash`, `restart`, `timeout`. Missing or
invalid scenario files fail closed. Start a new preview for each case (session markers track repair
attempts). For crash/timeout, first get passing results with `pass`, then change the file and click
Check again. For repair/restart, set the scenario before opening the preview.

| ID | Method | Actions | Expected result |
|---|---|---|---|
| FAULT-01 | UI preview | `pass`; select all ten rows, read long detail through last line/action. | Scrolling reaches explicit end; zero selected fixes disables Apply. |
| FAULT-02 | UI preview | `repair`; select fix, Apply fails once, read error; explicitly reselect/retry. | Concrete permission error/log path, recheck resets selections, Next stays disabled; retry gives passing rows and Next enabled. |
| FAULT-03 | UI preview | `pass` -> `crash`, Check again; dismiss error, then Check again. | Worker-without-result error persists in detail, stale pass rows cleared, Next/Apply disabled; explicit recheck recovers. |
| FAULT-04 | UI preview | `restart`; Apply simulated gMSA fix. | Restart instruction persists; Next/Apply remain disabled even when subsequent checks pass. No real reboot claimed. |
| FAULT-05 | UI preview | `pass` -> `timeout`, Check again. | At six-second test limit stale rows cleared; Next/Apply/Check again disabled; persistent timeout instructions. Wait for inert worker exit, close preview. This does not test a real 45-minute wait. |

## DPI and window fit

Use the **basic** Hyper-V console: View -> Enhanced session off. Enhanced/RDP sessions can disable
Windows scale/resolution settings; that is a blocked DPI attempt, not a pass. Log in, open Windows
Display Settings, set the requested scale (custom 200% may require sign-out/in), confirm the actual
value and resolution, then launch a fresh wizard. Do not shrink fonts or use host screenshot zoom
as a substitute. At the end restore 100%/1600x900 and verify it visibly.

The page sweep covers license, destination, identity/account, both database branches, PG credentials
with administrator fields expanded/collapsed, network with Advanced expanded/collapsed, existing
certificate/PFX controls, multiline review/source checkbox, prerequisites/list last rows/detail ends,
all action buttons, cancellation, progress/log button and finish. Scroll the outer page when needed;
footer navigation must remain accessible. Click mode/provider rows once to catch focus-scroll jumps.

| ID | Method | Scale/resolution and baseline | Expected result |
|---|---|---|---|
| DPI-01 | UI real | 100%, 1600x900; actual candidate page sweep and update. | No clipping/overlap; advanced URL and fields separate; entire finish readable. |
| DPI-02 | UI preview | 125%, 1600x900; B5 Layout page sweep. | All controls readable/reachable, list/detail scroll independently; cancellation works. |
| DPI-03 | UI preview + real | 150%, 1600x900; B5 sweep plus actual candidate update. | Footer and dense fields fit; real copy/progress/summary/save accessible. Record preview and real separately. |
| DPI-04 | UI preview | 100%, 1024x768; B5 PG/administrator/certificate/review/prerequisite sweep. | Last prerequisite rows, full details and buttons reachable. |
| DPI-05 | UI real | 200%, 1600x900; candidate page sweep, SQL/PG updates, SQL purge. | No clipped checkboxes, cut bottom admin field or AllowedHosts/URL overlap; first-click selection works; outer scroll preserves footer; full progress/completion/uninstall readable. |

## Automated corroboration

Run from repository root using **Windows PowerShell 5.1**, not only PowerShell 7. Run ACL tests in
an elevated session and record `IsInRole(Administrator)` from that same token. An access-denied
non-elevated attempt is a failed attempt, not a passing ACL test. Counts can change; store the real
output instead of requiring an old assertion count.

| ID | Method | Command/action | Expected result |
|---|---|---|---|
| AUTO-01 | AUTO elevated | `powershell.exe -NoProfile -File deploy/Test-SetupAdapter.ps1` | All assertions pass, including credential-file restrictive ACL, inheritance disabled, readers and replacement on second write. |
| AUTO-02 | AUTO | `powershell.exe -NoProfile -File deploy/Test-SetupExperience.ps1` | Progress, selected actions, gMSA readiness, source/answer contract and custom update completion regressions pass. |
| AUTO-03 | AUTO | `powershell.exe -NoProfile -File deploy/Test-PostgresProvisioning.ps1` | Catalog-based diagnosis/provisioning, missing-only statements, quoting/escaping and role-password preservation pass. |
| AUTO-04 | AUTO | `powershell.exe -NoProfile -File deploy/Test-DeploymentTemplates.ps1`; separately `powershell.exe -NoProfile -File deploy/server/Test-ServerWizardCompile.ps1` and `git diff --check`. | Contract/compile/whitespace checks pass. Inert compile artifact is not installed. |

## Restoration and acceptance boundaries

Save evidence before restoring the disposable checkpoint. Otherwise remove only run-owned fixture
services/tasks/listeners, temporary HTTPS certificates and verified empty test DB/role objects;
first record that uninstall retained its database/role as required. Verify names, ownership, object
counts and resolved paths before deletion. Use `.sql` files with `psql -f`/the SQL client, not inline
SQL interpolated through shell quoting. Do not delete a shared gMSA login, original DB or original
certificate. Keep protected backups until the run is accepted.

If test installs replaced original machine metadata, restore the saved marker/uninstall entry and
perform LIFE-10; verify original config hash, certificate, service identity, readiness and shortcut.
Restore client extraction names, console scale/resolution and any browser state changed for the run;
close every preview/wizard. Record cleanup proof and remaining intentional test objects.
For previews, remove the run's scenario file and its recorded `nodepilot-ui-preview-*` session
directories from that user's TEMP only after checking resolved paths and saving needed evidence.

Report actual coverage by candidate hash and method. The original review included both real and
preview layouts, synthetic failures and a real rollback; none can silently substitute for another.
Transient frame flicker cannot be ruled out by sparse screenshots: report sampled observations or
attach a short video if stronger evidence is needed. Manual SQL/PFX instructions read in a hint
were not necessarily executed; the real provisioning cases use Apply selected fixes. New releases
start with blank results, not the old run's passes.
