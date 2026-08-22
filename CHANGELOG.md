# Changelog

All notable changes to NodePilot are recorded here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Releases are
tagged `v<major>.<minor>.<patch>`. Each version heading links to its GitHub release, which carries
the full notes, the download table, and the checksum and signature details — this file is an index,
not a reprint.

Entries were reconstructed from the published release notes, so they are condensed rather than
exhaustive.

## [Unreleased]

### Added

- The file-watcher trigger publishes `fileNameWithoutExtension` and `fileDirectory` alongside
  `filePath` and `fileName`. Both are trivially derived from the path — but not inside a `{{…}}`
  template, which has no expression language, so "name the output after the dropped file with a
  different extension" or "work in the folder it landed in" previously needed a script step for
  what is really just addressing the event. It is also what lets a SCOrch import map Monitor File's
  `FileName` (extension-less) and `Path` (the watched folder) instead of reporting them as having
  no counterpart.

### Fixed


- **SCOrch import, measured against a real export.** The importer was written against an assumed
  file format, and a real 2016 runbook showed how far that had drifted: of 47 activities only 11
  were usable, and not one of the 147 Published Data references was translated — every marker in a
  real export is written backslash-backtick, which neither rewrite pattern matched. Encrypted global
  variables were classified as plaintext for the same reason and imported with their ciphertext as
  the value.
- Activity mapping now uses the names and properties SCOrch actually writes. *Invoke Runbook* is
  `Trigger Policy` on the wire (a third of that runbook) and its child arguments were dropped;
  *Run Program* carries `Program`/`Parameters`/`StartupDir`, so every imported node had an empty
  path. *Query XML*, *Delete File*, *Delete Folder*, *Generate Random Text* and the file, folder,
  archive, text-file, WMI and power activities are mapped as well. A mapping that cannot fill a
  required setting now degrades to a placeholder instead of shipping a node that looks configured
  and does nothing, and placeholders are disabled — an enabled one let a half-translated runbook run
  green from end to end.
- Runbooks without a trigger of their own get one. NodePilot starts from trigger nodes, so a
  faithfully translated SCOrch runbook invoked by another runbook imported as something that failed
  on every run.
- On-success and on-failure links survive. SCOrch writes them with a bare GUID and an outcome *set*
  (`warning#failed`), both of which the parser rejected, so the links routing a runbook's failures
  came out unconditional. `does not contain` no longer maps to `contains`, which made an edge fire
  under exactly the opposite condition.
- Imported graphs keep their original arrangement. SCOrch's coordinates cannot be copied as they
  are - it draws small icons on a 75 px grid where NodePilot draws cards several times that size,
  so verbatim positions overlapped nearly everywhere and started off-canvas. The graph is scaled
  uniformly instead, which is a similarity transform: every distance keeps its ratio, so it is the
  same picture at a larger size. The factor is sized against the designer's default rendering - the
  icon view, where the label column sets the footprint - not against the card view, which spread the
  reference export over 6900x3000 with more air between nodes than the nodes were wide. It now lands
  on 3460x1500, small enough to take in at once. Where no scale works - activities sharing a
  position, or spaced too tightly for a usable canvas - the import reports it and falls back to a
  left-to-right layout.
- The folder tree an export carries is rebuilt instead of flattened. SCOrch files both runbooks and
  global variables in folders, and the importer read neither — every workflow landed in the one
  folder chosen at import time and every variable in the root, so a whole-estate migration arrived
  as a flat list to re-file by hand. Both trees are now recreated below the destination, with
  existing folders reused (matched ignoring case, so an import cannot produce `SCCM` next to
  `sccm`). Names over 120 characters are shortened and a tree deeper than NodePilot's five levels is
  merged into the deepest that fits, both reported. No new permission is involved: everything
  created sits under the destination the caller already needs edit rights on and inherits its
  access, and each new shared folder is audited like a hand-created one.
- The case-sensitivity warning names the comparisons it is about. SCOrch compares case-insensitively
  by default and NodePilot's `==` does not, so a branch that took there goes quiet here with no
  error anywhere — a bare count of affected nodes left the operator hunting for which ones.
- Imported links are drawn as curves rather than rectangular loops. Scaling a graph faithfully keeps
  every edge pointing the way it did, and that includes backwards: the designer routes a backward
  right-to-left edge as an angular U below both nodes, and because the offset is measured between
  ports rather than nodes, two activities in the same column trip it just by being a node wide. On
  the reference export that was 15 of 49 links, 13 of them stacked pairs, and clearing them by hand
  took five minutes of dragging. Such a pair now docks top-to-bottom, which fixes the edge without
  moving either node; a link no dock can help has its target nudged right until the edge reads
  forward. Rows are never touched, a genuine loop back to an earlier step keeps its loop so it still
  stands out, and disabled links count too - they are drawn, so they are part of the picture. The
  reference export comes out with none of its 49 links angular, on the same 3460x1500 canvas.
- The import report says what the translation lost: references to fields the target activity does
  not publish, references across parallel branches, remote steps without a target machine, dropped
  run-as accounts, approximated schedules and links that ended up unconditional.
- Values a step writes to the bus at run time are recognised. The check asked the static activity
  catalog what a step publishes, which for a runScript is only its exit code, so every reference to
  a value a script produced was reported as broken. Link conditions are checked as well — a filter
  reading a value its source never publishes makes the edge silently never match.
- *Compare Values* becomes a `decision` instead of a `log`, and the links reading its result are
  re-pointed at the decision's case. As a log it published nothing those links could read, so every
  branch behind a comparison was dead — eleven of them in the reference runbook.
- Published-data field names are translated where the two products name the same value differently
  (`Query XML`'s `queryResult` is `xmlQuery`'s `result`). Only the exact equivalents are mapped:
  SCOrch's `Monitor File` publishes the watched folder and the extension-less file name, and
  NodePilot has neither, so those stay reported rather than bent onto the nearest-looking name.

## [1.2.11] - 2026-08-21

The release 1.2.10 should have been. Everything below was on `main` while 1.2.10 was the only
download, including an unlicensed image in the shipped bundle, an SSRF bypass, three desktop
first-run blockers, and the two release assets the deployment guide tells people to verify against.

### Added

- **The documentation website is published** at <https://sev7enup.github.io/NodePilot/> and is now
  **bilingual** — all 42 pages exist in English and German, with the language in the route
  (`#/en/…`, `#/de/…`), a language switcher, browser-language detection, and search over the active
  language. A parity test fails the build if a page or navigation title is added in only one
  language.
- `SECURITY.md` with a private vulnerability-reporting path, supported versions, and an explicit
  scope section naming the behaviours that are intended by design (operator trust, the localhost
  bypass, self-signed release signing) so they are not reported as findings.
- `CODE_OF_CONDUCT.md`, `CHANGELOG.md`, `RELEASING.md` and `.github/CODEOWNERS`.
- A release checklist (`RELEASING.md`) covering the two things that actually went wrong before: the
  publisher certificate silently stopping after 1.2.8, and the npm manifests drifting from the
  product version.
- The publisher certificate and the deployment scripts are now build outputs covered by
  `SHA256SUMS` rather than manual uploads — the verification path the deployment guide describes
  can be followed end to end.
- A desktop-installer troubleshooting guide (`docs/desktop-troubleshooting.md`), in English.
- The desktop build asserts the bundled PostgreSQL major version by reading it out of the binary; a
  17.x payload used to produce an installer that failed against every existing database.
- The desktop installer's shortcut is now an opt-out task rather than unconditional.

### Fixed

- **Desktop first run under a separate admin account.** The setup token was written to the elevated
  account's profile while the app looked in the interacting user's, so anyone installing with
  different admin credentials — the norm in managed environments — reached a login form for an
  account that did not exist, with no documented way out.
- A failed desktop provisioning step no longer reports success; the installer surfaces the error and
  names the log.
- The SSRF guard treats the unspecified addresses `0.0.0.0` and `::` as loopback. They are a plain
  spelling of the local host that `IPAddress.IsLoopback` does not recognise, so
  `http://0.0.0.0:5000/` walked straight through `RestApi:BlockPrivateNetworks`.
- 56 broken relative links across `docs/`, and the version placeholders in the deployment guide that
  told readers to download files that never existed under that name.
- The three npm manifests carried 1.2.5 while the product was on 1.2.10, so "Programs and Features"
  and the executable's file properties disagreed. A parity test now enforces all four version
  sources.
- The schedule preview reads a five-field Unix cron the way the user means it. Shorter expressions
  were padded from the wrong end, so the seconds default landed last instead of first and
  `20 15 * *` previewed as "every second". Fixed by cron-parser 5.10.0 and pinned by two tests —
  every existing case used six or seven fields, which is why it stayed invisible.

### Changed

- The README is a front door again: 1,428 lines down to about 540. The feature inventory it carried
  duplicated the documentation site and is replaced by links to it; the SCOrch import path, the
  strongest reason for a System Center Orchestrator user to look at this at all, moved from
  undocumented to a section of its own.
- Documentation aimed at people outside the project is English throughout — the deployment scripts'
  READMEs, the antivirus-exclusion handover document, the HA guide.
- Documentation for the SmartScreen prompt a downloaded installer raises: why it appears
  (Mark of the Web plus a self-signed publisher with no reputation), how to verify the checksum and
  the certificate thumbprint before dismissing it, and why unblocking a ZIP has to happen before
  extraction. Covers the README, the deployment guide, `docs/av-exclusions.md` and the
  documentation site.
- The Windows Server walkthrough exists once, on the documentation site. Two full walkthroughs had
  drifted: the deployment guide's database chapter was SQL Server end to end while PostgreSQL — the
  default provider — got one sentence. The guide keeps what only it does: verifying the artifact
  against its checksums and publisher, SmartScreen, building it yourself, and troubleshooting.
- Every getting-started link in the README points at Installation rather than Quick start, which
  lists a running instance as a prerequisite.
- PowerShell SDK 7.6.5 — it ships inside the artifact — plus frontend and documentation-site
  dependency updates.
- A pull request that touches only Markdown or documentation images skips the frontend, desktop and
  E2E jobs. Backend and the documentation site still run: Markdown is an input to both, and a blunt
  `paths-ignore` would have let through exactly the README breakages CI has caught.

### Removed

- Two easter eggs and the image one of them shipped in every build.
- The public roadmap no longer names file and class locations for open security work.

## [1.2.10] - 2026-08-16

### Fixed

- `np` reaches the machine PATH again. The tools-directory literal in `Install-`, `Update-` and
  `Uninstall-NodePilot.ps1` was split across two lines, so the string carried a real newline,
  `Test-Path` rejected the resulting path, and the surrounding `catch` downgraded that to a warning
  — install and update both finished green with no PATH entry. Present in 1.2.8 and 1.2.9. The
  clients themselves were never affected; `np.exe` and `nodepilot-mcp.exe` land under `tools\`
  regardless.

### Changed

- The deployment template contract check pins the directory literal on one line in all three
  scripts. It previously asserted only that each script calls the shared PATH helper, which a
  mangled argument satisfies.

## [1.2.9] - 2026-08-16

### Fixed

- A `manualTrigger` parameter declared with a default now seeds `{{manual.NAME}}` when the caller
  omits it, instead of failing the step. The engine fills the run's inputs from the declared
  defaults before the execution row is written, so the recorded input is what the run saw and a
  retry replays the same values. A supplied value still wins; a parameter declared *without* a
  default stays absent and a reference to it still fails loudly.

### Changed

- `analyze_workflow` has one implementation in `NodePilot.Core` backing both the AI chat and the
  MCP tool. The two copies had drifted: `cycle` was an error on one side and a warning on the
  other, the chat copy flagged `missing-target-machine` on `runScript` and `waitForCondition`
  (valid under the localhost bypass) and did not know unresolvable `{{...}}` references at all. A
  parity test now guards the mirror against the canvas linter. `unknown-activity-type` is no longer
  reported separately — the structural pre-check already names the node index and the offending
  type.

## [1.2.8] - 2026-08-16

Found on a real 1.2.6 installation during an end-to-end test of the API, engine, CLI and MCP
server. Every item has the same shape: the run reported success while doing the wrong thing.

### Added

- `np` and `nodepilot-mcp` ship in both installers: `<install>\tools\np\np.exe`, added to the
  machine PATH by install and update and removed on uninstall, and
  `<install>\tools\mcp\nodepilot-mcp.exe`, which `.mcp.json` points at. The desktop package ships
  them self-contained.

### Fixed

- `{{manual.NAME}}` resolves. The resolver had no pattern for the trigger namespace — the name
  after the dot is user-chosen, not one of the four fixed step tails — so the placeholder survived
  untouched *and* slipped past the unresolved-template check, which scans step patterns only. An
  unknown trigger input now fails the step with its own diagnostic.
- A cross-branch reference no longer depends on which branch finishes first. The gate asked whether
  the referenced step had already produced a value, which made it a race: fatal when the sibling
  had finished, tolerated when it was still running, in which case `$wert = {{sibling.output}}`
  reached PowerShell verbatim and the step reported success. Membership now comes from the graph.
- Unmatched `/api` paths answer `404 application/problem+json`. The SPA fallback claimed anything
  no endpoint matched, so a typo, a moved endpoint, or a route parameter failing its type
  constraint returned `200 text/html` with the SPA bundle.
- `analyze_workflow` reports unresolvable template references. The check existed only behind
  `find_unresolved_references`, so the tool agents actually call answered `ok` for a workflow that
  fails on its first run.
- Two activity-reference entries described a shape the executor rejects:
  `wmiQuery.captureProperties` needs a JSON array, and `startProgram.filePath` needs an absolute
  local path.

## [1.2.7] - 2026-08-16

### Fixed

- The Add/Remove Programs entry names the directory NodePilot is actually in. `/ANSWERFILE` skips
  the wizard's directory page, so Inno's `{app}` kept its default while the installer went to the
  answer file's `installPath`. `InstallLocation` is now corrected from the installation marker.
- Installing from the ZIP over a setup installation no longer leaves an entry that cannot be
  removed. The GUI setup keeps its uninstaller inside the install directory, which
  `Install-NodePilot.ps1` empties; the dead entry is now removed, and only when its uninstaller
  lived in the emptied directory and is really gone.
- `Update-NodePilot.ps1` refreshes the installation marker, so a script-driven update is visible to
  the setup wizard's mode page instead of leaving `Version` naming the last install.

## [1.2.6] - 2026-08-15

Field test on a lab server: a real uninstall, every answer-file combination the machine could
express, then 174 imported workflows executed.

### Fixed

- An unattended install no longer skips provisioning on a host that already has NodePilot.
  `/ANSWERFILE` never shows the mode page, but the mode was read from that page's default, so a
  file saying `"mode": "install"` was treated as an update and every `provisioning.*` key was
  accepted, validated and then ignored.
- Database provisioning is given the certificate host name. `database.sqlCertificateHostName` now
  reaches the provisioner, so `localhost` against a SQL Server whose certificate names the FQDN no
  longer fails with "The target principal name is incorrect".
- A failed provisioning stops an unattended run and says why, reading the result out of
  `provision.ini` the way the readiness page always has.
- The uninstaller removes the installation, not just the record of it. `serviceName`, `installPath`
  and `dataPath` are read back from the installation marker instead of using the wizard defaults,
  and `/PURGEDATA=1` reaches the right directory.
- A `waitAny` or `waitNofM` junction no longer fails the run it just completed correctly. Branches
  built from `runScript` had their cancellation converted into an ordinary failed step; remote
  (WinRM) branches are covered too, and a genuine timeout remains a failure. This also corrects
  `GET /api/workflows/{id}/coverage`, which now counts junction-race cancellations as skipped
  rather than failed.
- Long Active Directory passphrases are no longer rejected before the LDAP bind (#209).
- A client-side wildcard route no longer covers literal endpoint routes (#210).

## [1.2.5] - 2026-08-15

### Security

- External trigger keys are scoped per key. `X-Api-Key` is matched against SHA-256 hashes under
  `ExternalTrigger:Keys:<id>`, each with its own GUID-only `AllowedWorkflowIds` list. The whole
  `Keys` map is taken atomically from the highest-priority provider that declares it, so
  `Keys: {}` revokes lower-priority keys instead of merging with them. Legacy `ApiKey` is inert
  without its own scope list.
- Historic workflow definitions are protected at rest: `WorkflowVersions.DefinitionJson` is stored
  as an opaque envelope rather than plaintext, decrypted on read through authorised API paths.
  Legacy plaintext rows stay readable during a rolling upgrade.
- Desktop key rings fail closed — an exposed key ring aborts the boot instead of running with
  weakened protection — and the desktop data root is read-restricted.
- Hardened production publish and tightened desktop data ACLs.
- Read-only agent SQL rejects dynamic XML exporters (`query_to_xml` and relatives), which take
  their target as a string and so defeat identifier-based checks, and PostgreSQL `U&"..."` escaped
  identifiers, which spell a protected column name without writing it.

### Changed

- CLI and MCP share one session state: the API returns `ExpiresAt`, tokens rotate proactively,
  refresh runs as a single flight, and an expired session is reported as an explicit re-login state
  instead of a generic failure. A cross-process lock plus atomic replacement keeps the shared DPAPI
  session file consistent between both clients.
- The workflow editor tracks a save revision, so an edit made while a save is in flight is no
  longer silently dropped by the response that lands afterwards.
- Orchestrator runbook import brings global variables along. An existing variable of the same name
  is never overwritten.
- LLM `MaxTokens` ceiling raised from 128k to 1M.
- System settings tabs are grouped by topic; the LLM outbound-proxy block moved into a disclosure
  panel.
- Live Ops timeline: bounded lane height, shrinking label column, and window coverage backed by
  real runs.

## [1.2.4] - 2026-08-11

### Fixed

- An install could fail at the first service start because of an ACL left behind by an *earlier*
  installation that ran under a different service identity: the trusted set for the directory
  holding the JWT signing key includes the identity the service actually runs as, so A's leftover
  ACE is a stranger with write access once the service runs as B. The installer now asks the
  directory the same question the service will, using the same rule and SIDs, immediately after
  applying the ACL and **before the artifact is extracted**; it repairs and re-checks once, then
  stops. The security check itself is unchanged.
- The rejection message names the culprit — `…to DOMAIN\account (S-1-5-21-…)`, or the bare SID with
  *account no longer exists*, which is what `icacls` needs to remove it — instead of "an untrusted
  principal".

## [1.2.3] - 2026-08-11

### Changed

- Reaching an LLM endpoint no longer shares the model's answer budget. Name resolution (15 s), the
  TCP connection (15 s) and the TLS handshake (30 s) have their own deadlines and their own message
  prefixes (`LLM endpoint DNS:` / `TCP:` / `TLS:`); TCP distinguishes dropped from refused and
  lists the addresses tried, and the certificate case names the **machine** trust store.
  `TimeoutSeconds` is now purely an answer budget, so an unreachable endpoint fails in seconds
  rather than after minutes of blaming the model. No new configuration key.
- Resolved addresses and per-stage timings log at `Debug` under `NodePilot.Ai.LlmConnect`, which
  can be raised on its own via `Serilog:MinimumLevel:Override`.

### Fixed

- The settings **Test connection** button kept only the generic "An error occurred while sending
  the request" and discarded the inner exception, which is where the stage or the certificate error
  lives. It now shows both.

## [1.2.2] - 2026-08-11

### Added

- `Llm:Proxy` — one outbound proxy block covering every LLM call, including both chats, script and
  workflow generation, the `llmQuery` activity and the settings probe. Keys: `Mode`
  (`Off` / `System` / `Custom`), `Address`, `BypassList`, `Username` / `Password`,
  `UseDefaultCredentials`. The LLM `HttpClient` hard-coded `UseProxy = false`, so on a network
  where outbound traffic only leaves through a proxy every AI call ran into its timeout, with no
  configuration key and no workaround short of a code change. It stays hot-reloadable:
  `LlmConfiguredProxy` implements `IWebProxy` and resolves the live configuration per request
  rather than binding into the `SocketsHttpHandler`. `Mode: Off` is byte-for-byte the previous
  direct connection.

### Changed

- The server updater announces `Extracting artifact` before it expands, instead of sitting on a
  motionless bar for minutes while signature verification, extraction and the first hash pass ran
  with nothing to display.
- Extraction moved from `Expand-Archive` to `ZipFile.ExtractToDirectory` — measured faster
  everywhere, though the margin varies by machine. Equivalence was verified rather than assumed,
  and zip-slip is still rejected.
- `docs/av-exclusions.md` documents `%TEMP%\nodepilot-artifact-*`, the directory a real-time
  scanner turns into the dominant cost of an update.
- The prerequisites page got room for the remediation box, which had been clipped to about one line.
- Bundled runtime moves to ASP.NET Core 10.0.11 from 10.0.10, pinned by hash in a committed lock
  file.

## [1.2.1] - 2026-08-09

### Fixed

- Trigger configuration is parsed once, in `NodePilot.Core/Triggers`, by both the node executor
  behind the manual sample run and the background source that actually fires. Nothing had compared
  them and they had drifted, silently: an event-id filter set in the designer was ignored by the
  live listener, the poll loop read `intervalSeconds` while the designer, docs and node executor
  wrote `pollingIntervalSeconds`, and `messagePattern` existed only on the listener. Nothing was
  taken away — `intervalSeconds` remains a documented alias, the 30-second default is unchanged, a
  missing `logName` defaults to `Application`, and a configured `AllowedLogs` list extends the
  defaults on the manual path instead of replacing them.
- Three shipped lockfiles carried corrupt data: cutting 1.1.2 replaced every `"version": "1.1.1"`
  in the tree, so six transitive entries declared a version that does not exist and did not match
  the tarball they resolved to, breaking `npm ci`.
- Every download instruction named `SHA256SUMS.txt`, which the build has never emitted — it emits
  `NodePilot-<version>.SHA256SUMS.txt`. The contract check now pins the exact filename across all
  seven guides that name it.
- The deployment guide presented importing the publisher certificate as a prerequisite for the
  artifact check. It is optional and unrelated: the installer pins the thumbprint passed on the
  command line and builds no chain (ADR 0012).
- `performance-improvements.md` presented its numbers as the live configuration, although
  `Performance:ManualTuning` defaults to `false` and the effective values are derived from detected
  hardware at boot.
- The heatmap axis-ordering fix from the SonarQube sweep gained the regression test it shipped
  without.

### Added

- `np settings effective-sizing` — `GET /api/admin/settings/effective-sizing` had a frontend client
  only, so "what is this host actually sized to?" could not be answered without a browser, and
  `np settings get Engine` answers it wrongly under automatic tuning. Each resolved value is shown
  next to the constraint that produced it (`Cpu`, `Ram`, `Floor`, `Ceiling`, `Manual`). The docs
  site gains a Performance page covering the formulas, floors and ceilings.

### Security

- Security-audit follow-up: install-directory ACLs, log redaction, pre-auth request limits, SignalR
  connection scoping and HSTS header ordering, plus an API pipeline smoke layer that pins auth
  gating, the role matrix and CSRF behaviour at the request level.

### Changed

- `TriggerContractParityTests` fails the build if either runtime reads a key the reference does not
  document. The reference also stopped mis-describing `databaseTrigger`, which fires on a sentinel
  change — the first column of the first row — not once per returned row.
- The Live-Ops page gained a manual test plan entry derived from what its Playwright spec asserts,
  with the retry path explicitly marked as not automated.
- Dependency updates across all three npm trees and the NuGet graph.

## [1.2.0] - 2026-08-07

The first release that keeps running when something underneath it stops. Also carries the 1.1.2
setup fixes, which only ever shipped as a server installer attached to the 1.1.1 release.

### Added

- Database availability breaker (ADR 0011). A wedged database used to wedge the process: every
  `DbContext` carried `EnableRetryOnFailure(5)` over a 120-second command timeout, and token
  validation ran ahead of every authenticated request, so *every* request burned roughly twelve
  minutes — while the header's status pill polled `/healthz/live`, which is 200 by design. A
  process-wide breaker now owns that state under one rule: after boot, only the recovery probe may
  publish `Available`; interceptors may only degrade. The API answers `503 DATABASE_UNAVAILABLE`
  with `Retry-After` immediately, background services park instead of throwing, the engine pauses
  before starting a new step rather than finalising a failed one as succeeded, and recovery is
  automatic after two clean reads. A command timeout never opens the breaker directly — a slow
  query is not an outage; it only arms the probe.
- Health surface: `/healthz/ready` fails fast for a load balancer, `/healthz/database` always
  answers 200 with a status the SPA renders as a banner and a traffic light.
- Trigger self-healing. `ITriggerSource` answers `Health`, contractually a pure in-memory read
  because the orchestrator evaluates it for every trigger in its five-second pass. An unhealthy
  source is evicted and routed back through the existing registration path, which retries with
  exponential backoff up to five minutes, indefinitely — so a `fileWatcherTrigger` whose UNC share
  went away resumes when the share returns, instead of staying dead until a restart. A
  `FileSystemWatcher` cannot be re-armed in place, so a fresh instance is created; a buffer
  overflow is deliberately not a fault.
- `trigger-unhealthy` system-alert policy, firing past 60 seconds, so a registration that keeps
  failing is no longer silent while a drop folder goes unwatched.

### Fixed

- A first installation no longer fails after nine green prerequisite rows. `Install-NodePilot.ps1`
  verifies the artifact signature against the pinned, self-signed publisher; the readiness page had
  no row for the signer and the wizard wrote `trustArtifactSigner: false` as a constant, so the
  remedy could not be reached from the interface at all.
- The trust decision is now unnecessary: chain validation is dropped, since it only confirmed what
  the thumbprint pin already established, at the price of a permanent machine-wide change. What it
  enforced is now explicit — KeyUsage must permit signing, EKU must be code-signing, validity
  checked against the signing time.
- 32-bit .NET hosts are reported red by path and architecture. NodePilot ships as win-x64, and a
  machine whose only `dotnet.exe` was 32-bit passed the runtime row and then failed to start the
  service. The pre-flight reads the machine type out of the PE header, because `dotnet --info` is
  localised.
- AI buttons render only when there is an LLM. The designer assistant, the script-editor generate
  button and the AI workflow generation button rendered unconditionally and failed with a 503 when
  no endpoint was configured; all three are gated on a shared capability query, and the
  script-editor button is hidden for Viewers to match its Admin/Operator endpoint.
- `waitForCondition` can probe localhost. The `httpOk` probe ran through the `restApi` SSRF guard
  before consulting its own allow-list, so the shipped `["localhost"]` default was inert on every
  non-Development instance. The probe path has its own validation; link-local and cloud-metadata
  addresses stay blocked for both probe types.
- The 1.1.2 setup fixes: the empty-certificate-field fix, fix checkboxes that had no clickable
  place on the page, and a setup log that narrated steps it had not taken.
- Locale-dependent formatting is routed through one module, so dates and numbers no longer differ
  between components.

### Changed

- Coherence pass with no user-visible trace: controller DTOs moved out of controller files, folder
  RBAC routed through one authorization gate, LLM error mapping and the agentic tool loop
  deduplicated, scheduler telemetry names and the DPAPI session entropy single-sourced, and
  `ExternalIdentityResolutionController` removed. Three new guard tests pin the dependency
  direction, endpoint parity across API/CLI/MCP, and the deployment template grammar. Coverage now
  has exactly one authoritative gate, in CI.

### Security

- `nanoid` to 3.3.18 across all three npm trees (GHSA-2v37-7h3g-55p8, build-time only) — a lockfile
  refresh, since the range that pulls it in already allowed the fixed version.

## [1.1.1] - 2026-08-05

### Fixed

- The TLS page accepts an empty thumbprint field, meaning what it has always meant in an answer
  file: *I do not have one yet*. It previously demanded exactly 40 hexadecimal characters while its
  own error box told you to leave the field as it is if you wanted the next page to create a
  certificate for you — so on a machine with no certificate at all, the only route to the offer was
  to invent a thumbprint. The prerequisite page now reports "No certificate selected" and offers to
  create one, offered rather than pre-ticked. Nothing was loosened: a thumbprint you do type is
  still checked, and the certificate row still blocks the install until something real is in
  `LocalMachine\My`.
- The documentation site's installation page described only the ZIP and the PowerShell scripts,
  while its own comparison table already promised an installer.
- The README told readers to pass `-DesktopSigningCertificateThumbprint` to
  `Build-DesktopInstaller.ps1`, which has no signing parameter; signing happens in
  `Build-Artifact.ps1` via `-InstallerSigningCertificateThumbprint`, before the checksums are
  written.

## [1.1.0] - 2026-08-05

### Added

- **NodePilot-Server-Setup** — a signed GUI installer for the Windows service. Installing the
  1.0.1 artifacts still meant reading the deployment guide, creating a SQL login by hand, granting
  the service account access to a certificate's private key, and getting a dozen
  `Install-NodePilot.ps1` parameters right on the first try. The setup runs those same scripts and:
  - checks the host first and says why — administrator rights, ports (naming HTTP.SYS when the
    kernel, not IIS, holds 443), the .NET runtime, the TLS certificate, the service account, and
    whether the database is reachable — changing nothing until every blocking item is green;
  - creates the database access it needs (SQL Server login and database with `db_owner`;
    PostgreSQL role and database from superuser credentials), skipping what exists and naming the
    missing privilege when it may not proceed;
  - picks the certificate from a list rather than asking for a thumbprint — expired blocks, a name
    mismatch is an overridable warning, because split-DNS deployments are legitimate;
  - grants the private key to the service account, sets ACLs and the firewall rule, installs and
    starts the service, and waits for `/healthz/ready`;
  - shows the setup token and the external-trigger API key on the finish page, the only place they
    are ever shown;
  - runs unattended for SCCM or GPO
    (`/VERYSILENT /SUPPRESSMSGBOXES /ANSWERFILE=answers.json`), where the answer file can create
    the first administrator and seed from a configuration backup;
  - upgrades in place, keeping the existing configuration, with the service down for roughly half a
    minute.

### Fixed

- Changing the service identity no longer bricks the installation it replaces. Installing over an
  existing instance under a different account (LocalSystem to gMSA) left the identity-bound secrets
  readable by nobody; they are handed over to the new identity, and a failed switch rolls the
  directory ACL back instead of leaving a half-migrated install.
- An update leaves the service running. It used to stop the service and hand it back stopped.
- The uninstaller waits for the service process instead of orphaning a still-running one.
- The service waits for its database at boot rather than crash-looping past it, in both the server
  and desktop postures.
- `AllowedHosts` derived from the public hostname no longer excludes `localhost`, which broke the
  installer's own health probe.
- Registry ACLs are addressed by path, not by a literal that drifted.
- The global query-error toast no longer fires every fifteen seconds while a background refresh
  retries.

### Changed

- The installer pre-flight was extracted so it can run without side effects, so the wizard and the
  scripts check the same way. The setup's PowerShell adapter and the deployment templates gained
  contract tests.

## [1.0.1] - 2026-08-02

The first release you can actually download — `v1.0.0` shipped as source only, so both product
install paths started with "build it yourself".

### Added

- Release artifacts: the desktop installer, the server ZIP with its `.manifest.json` and detached
  `.p7s`, a `SHA256SUMS.txt` covering everything a build produced, and the public signing
  certificate.
- One command produces both shipping targets under one version
  (`Build-Artifact.ps1 -IncludeDesktopInstaller -PgBinariesPath <pgsql>`). Two scripts previously
  wrote to two directories with two hand-typed version strings and had already drifted apart;
  missing Inno Setup or Postgres binaries now skip the desktop step with a warning instead of
  failing the run.
- `docs/av-exclusions.md` — antivirus exclusions as a hand-off list for a security team. The
  service spawns PowerShell and runs generated scripts out of `%TEMP%`, and nothing said so.
- The OpenAI Responses API dialect for the LLM stack, hardware-adaptive engine sizing, the Live-Ops
  run-density strips, and the AD SSO lab harness with Kerberos field-test results, among the 130
  commits merged since `v1.0.0`.

### Changed

- The product version has one declaration (`Directory.Build.props`) instead of five, and
  `np --version` reads it from the assembly rather than a literal — which is why it used to answer
  `1.0.0` indefinitely.
- `np` and the MCP server are installed with `dotnet publish` plus a `PATH` entry. They were
  documented as `dotnet tool install -g` installs, which never worked: `PackAsTool` rejects the
  `net10.0-windows` target these projects inherit, so `dotnet pack` fails outright. The dead csproj
  properties that made the wrong instructions look plausible are gone.
- Backend line coverage raised to 89%, and the test suite moved to xunit.v3.

### Fixed

- `dotnet run` binds port 5000, the port every doc and the Vite proxy already assumed;
  `launchSettings.json` said 5068, so a plain `dotnet run` produced a backend the frontend never
  talked to.
- An unreachable database fails with a message naming the provider, server and database instead of
  an unhandled provider stack trace. The password is never part of that message.
- `appsettings.Development.json` no longer carries a developer's checkout path.
- Local logins are enabled in Development. The `BreakGlassOnly` production default silently
  rejected every additional local user a developer created.
- A WinPSCompat session leak in the in-process runspace pool.
- The installer warns when the host is not domain-joined, where its Domain-profile firewall rules
  apply to no active profile.
- Inno Setup is found in its per-user install location, not just under Program Files.

## [1.0.0] - 2026-07-21

### Added

First public release — agentless Windows workflow orchestration over WinRM, a modern, open
replacement for Microsoft System Center Orchestrator. Design, schedule, debug and observe
multi-step automation in the browser, with no agents on the targets.

- Visual workflow designer (React Flow) with live execution status and a step debugger
- 27 activity types and 6 triggers, sub-workflows, per-step retries, conditional edges
- Agentless remote execution via WinRM / PowerShell SDK
- Scheduling (Quartz), webhooks (HMAC), file, database and event-log triggers
- REST API, `np` CLI, and an opt-in MCP server for AI-driven workflow editing
- JWT auth (Admin / Operator / Viewer), audit log, alerting, system backup and restore
- PostgreSQL or SQL Server; optional HA, LDAP / Windows SSO, ECS/SIEM logging
- Licensed under Apache-2.0

[Unreleased]: https://github.com/Sev7eNup/NodePilot/compare/v1.2.11...main
[1.2.11]: https://github.com/Sev7eNup/NodePilot/releases/tag/v1.2.11
[1.2.10]: https://github.com/Sev7eNup/NodePilot/releases/tag/v1.2.10
[1.2.9]: https://github.com/Sev7eNup/NodePilot/releases/tag/v1.2.9
[1.2.8]: https://github.com/Sev7eNup/NodePilot/releases/tag/v1.2.8
[1.2.7]: https://github.com/Sev7eNup/NodePilot/releases/tag/v1.2.7
[1.2.6]: https://github.com/Sev7eNup/NodePilot/releases/tag/v1.2.6
[1.2.5]: https://github.com/Sev7eNup/NodePilot/releases/tag/v1.2.5
[1.2.4]: https://github.com/Sev7eNup/NodePilot/releases/tag/v1.2.4
[1.2.3]: https://github.com/Sev7eNup/NodePilot/releases/tag/v1.2.3
[1.2.2]: https://github.com/Sev7eNup/NodePilot/releases/tag/v1.2.2
[1.2.1]: https://github.com/Sev7eNup/NodePilot/releases/tag/v1.2.1
[1.2.0]: https://github.com/Sev7eNup/NodePilot/releases/tag/v1.2.0
[1.1.1]: https://github.com/Sev7eNup/NodePilot/releases/tag/v1.1.1
[1.1.0]: https://github.com/Sev7eNup/NodePilot/releases/tag/v1.1.0
[1.0.1]: https://github.com/Sev7eNup/NodePilot/releases/tag/v1.0.1
[1.0.0]: https://github.com/Sev7eNup/NodePilot/releases/tag/v1.0.0
