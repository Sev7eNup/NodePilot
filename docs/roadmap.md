# NodePilot Roadmap

**The authoritative document for "what gets built".** What isn't listed here is not an initiative — even if
it shows up elsewhere as an idea, plan, or follow-up.

As of: **2026-08-22**. Consolidated on 2026-08-03 from the then-current `docs/backlog.md`, `docs/ai-feature-ideas.md`,
`docs/db-schema-audit-followups.md`, the "still open" sections in `docs/performance-improvements.md`
and `docs/alerting.md`, the open GitHub issues, and the session memory. The status of the R1 items
was verified against `main` on 2026-08-22 — what is marked done below is in the code.
On 2026-09-16 the remaining open items from the repository audit (August/September 2026) were added;
until then it lived as `audit.md` in the repo root; every item was cross-checked against the code beforehand.

## Ground rules

- **R1** — committed. Will be built; the order below is the recommended one.
- **R2** — on the roadmap, but starts only on a **named trigger condition**.
  No date, no "someday" — every R2 item carries its trigger.
- **E** — open decision. Cannot be built until it is made.
- **Appendix** — deliberately *not* on the roadmap, with reasons. The appendix is a blocklist:
  it keeps rejected or measured-and-disproved ideas from resurfacing.

An item moves from R2 to R1 when its trigger occurs — not because it got old.

---

## R1 — Committed

### Dependencies menu (decided 2026-10-23)

Implemented and on `main`:
a readable usage overview, a focused map, and usage locations for workflows, machines,
credentials, global variables, and Custom Activities. API, CLI, MCP, and the browser demo are
wired up. [Usage and limits](dependencies.md), [feature plan](dependencies-plan.md).

### Credentials menu (decided 2026-10-24)

Credentials are global service accounts and therefore get their own menu entry under
Infrastructure instead of a card in the personal settings. Visible only to Admin and
Operator, like the API. A badge counts expired and soon-to-expire accounts using the same
calculation as the system policy `credential-expiring`, whose link `/credentials` thereby has a
target. Implemented and on `main`. Still open as a separate small step: a
delete warning that names the usages from the dependencies menu, because deleting silently removes the
machine default.

### General AI agents and teams (decided 2026-09-18)

`aiAgent` and `aiAgentTeam`: general-purpose agents with selectable native tools,
PowerShell/CMD/Git Bash, MCP client, versioned skills, bounded log collection, and
visible teams with bounded parallel tasks. The loop lives inside the step; the workflow
engine stays as it is. Publishing authorizes configured autonomous actions;
chat approvals remain unchanged. [ADR 0016](adr/0016-general-ai-agent-activities.md)
and the [operations/configuration reference](ai-agents.md) are binding. Developed in
three stages, released in full once. [ADR 0017](adr/0017-parallel-team-delegation.md)
adds parallel delegation bundles and live team hints; nested teams and
resumption after restart remain excluded. Implemented and on `main` (`AiAgentActivity`, `AiAgentTeamActivity`).

28 items in eight waves. The wave order is deliberate: first what is currently
dishonest is made honest, then we build.

### Wave 1 — Security

Details on the findings are deliberately **not** in this public repo but in the
audit log of 2026-07-26 (session memory `security-audit-2026-07-26-open-items`).
Only *what* is being changed is listed here.

| # | Item | Content |
|---|---|---|
| 1 | ~~**Harden local script execution**~~ **Decided (2026-08-15)** | `Operator` is deliberately a trusted automation author. Local activities may run under the NodePilot service identity; folder RBAC is not a code sandbox. README and CLAUDE.md document this trust boundary. An `Engine:RequireTargetMachineForScripts` flag is therefore not planned. |
| 2 | ~~**SSRF guard: complete the address coverage**~~ **Done (2026-08-18)** | Fixed and registered as M-34 in [`security-findings.md`](security-findings.md), with a regression test in both directions. |
| 3 | **Password policy for break-glass accounts** | Raise the minimum length for accounts explicitly marked as break-glass and add a complexity rule. |
| 4 | **`restApi` gets a first-class credential field** | A `credentialId` field analogous to the remote activities, so that auth data takes the same path as everywhere else instead of freely fillable headers. Backend + Designer configuration. |
| 5 | **Audit follow-up: the remaining dimensions** | The audit of 2026-07-26 did not reach all areas. **Partially done (2026-08-09):** a follow-up audit over `deploy/`, IDOR/folder scoping, alerting, and the anonymous endpoints produced five findings, all fixed and registered in [`security-findings.md`](security-findings.md) — H-18, M-31, M-32, M-33, L-17. The rest of the surface is still pending; the scope is tracked in the audit log, not here. |

### Wave 2 — Hygiene

One day of work. After that no source lies anymore — a prerequisite for this roadmap to hold up.

| # | Item | Content |
|---|---|---|
| 6 | ~~**Delete stale branches**~~ **Done (verified 2026-08-22)** | The seven pre-migration branches that were content-wise *behind* `main` no longer exist; `git branch --merged main` is empty. What remains locally are unmerged working branches, not legacy leftovers. |
| 7 | **Correct status drift in the session memory** | About a dozen memory entries claimed "unmerged/parked" for work that is long since in `main`. Done as part of creating this roadmap — the item stays as a reminder that memory status should be checked against the code regularly. |
| 8 | ~~**`docs/backlog.md` dissolved**~~ **Done (2026-07-27, PR #81)** | Contained ideas marked ✅ SHIPPED next to real initiatives. The content has been moved into this file, the file is removed, README points here. |
| 9 | ~~**Clear out open issues**~~ **Done (2026-08-09)** | [#76](https://github.com/Sev7eNup/NodePilot/issues/76) (row serialization via `DbAdminSecretColumns`), [#77](https://github.com/Sev7eNup/NodePilot/issues/77) (regression test for the heatmap sort fix), and [#79](https://github.com/Sev7eNup/NodePilot/issues/79) (`premiumCanvas` description with canvas background) are closed; the issue tracker is empty. |
| 10 | ~~**Set up Dependabot**~~ **Done (2026-08-07, PR #126)** | `.github/dependabot.yml` for four ecosystems (nuget + 3× npm), minor/patch grouped per ecosystem, majors deliberately individual — semantics and PR budget are in CLAUDE.md. Without it version drift built up, hence the 10.0.0/10.0.5/10.0.10 split. |
| 11 | ~~**Doc sync AI chat / knowledge**~~ **Done (2026-08-22)** | Deliberately left out of the AI chat PR, since caught up on all four surfaces: CLAUDE.md (endpoint + knowledge sources), `docs/ai-features.md`, docs-ui `content/{de,en}/ai-features.md`, and — as the last piece — the README highlight for the global AI chat. |

### Wave 3 — Following our own conventions

Not features, but violations of rules that are already binding in the repo.

| # | Item | Content |
|---|---|---|
| 12 | **`np alerting enable\|disable` + MCP tools** | CLAUDE.md: *"Every new API endpoint needs both clients."* The endpoints `POST /api/alerting/rules/{id}/enable\|disable` exist. Today the CLI reaches the state only indirectly via `np alerting update --enabled\|--disabled`; dedicated `enable\|disable` commands and the MCP tool are missing. |
| 13 | **`np` command group for Custom Activities** | Same rule, same gap. The controller exists, the CLI group is missing. |
| 14 | **Settings UI: alerting thresholds + complete retention** | (a) `Alerting:LongRunningSeconds` / `QueuedLongSeconds` have no schema section — the code comments literally *"Operator lowers … in the Settings UI"*, which doesn't exist. New section `Alerting`, hot-reloadable. (b) `RetentionOptions` has five sweepers, `RetentionSettingsDto` only three — Notifications and SupportEvents cannot be tuned via the UI, nor can the AuditLog verify parameters. (c) **Threshold unification done (#310):** `DashboardStats.LongRunningCount` uses `Alerting:LongRunningSeconds`. The Settings UI stays prioritized: Live Ops and the dashboard make the effect visible, but the operator can still only change the value via configuration. (a) and (b) remain open. |
| 15 | ~~**Internationalize the config panes**~~ **Done** | All previously named components under `components/designer/properties/` run through `useTranslation`; no hard-wired German UI texts remain. |

**Pattern for item 14:** `SettingsSchema.cs` → DTO under `Api/Dtos/Settings/` → adapter in
`SettingsSections.cs` → frontend section under `pages/settings/` + tab in `SystemSettingsPage.tsx`
→ i18n DE+EN → tests. `AdminSettingsFrontendSyncTests` enforces parity.

### Wave 4 — Product value

| # | Item | Content |
|---|---|---|
| 16 | **Alert → Workflow (auto-remediation)** | Third route target type "Workflow" in the `NotificationDispatcher`, no new `ITriggerSource`. Because of at-least-once delivery the route needs an idempotent dispatch key (see [ADR 0011](adr/0011-database-availability-breaker.md)). |
| 17 | **Starter presets + "Import examples"** | Verified finding: NodePilot has **no** seeding, template, or gallery mechanism. The only ingest path is `POST /api/workflows/import`. The `scripts/*.json` consistently use `targetMachineId: "localhost"` and are therefore directly seedable, independent of the environment. Ten presets with an ascending learning curve are designed (see below). Delivery mechanism: **button on the Workflows page**, no first-run seeding — the DB should not fill itself unasked. |
| 18 | **Mail trigger (IMAP/EWS/Graph)** | The SCOrch classic "Monitor Email" and the direct counterpart to `emailNotification`. Filter on sender/subject/attachment, attachments as `{{manual.attachmentPath}}`. Highest expectation among former SCOrch users. |
| 19 | **Document a ChatOps recipe** | Teams/Slack triggers already work today via `webhookTrigger` + `fieldMappings`. A documented recipe covers ~80 % of the need without a line of code. One afternoon. |
| 20 | **Alerting rule templates (catalog)** | Detailed plan: [alerting-rule-templates-plan.md](alerting-rule-templates-plan.md). Same idea as item 17, for alerting: 30 curated custom rules currently exist only in the dev DB, `scripts/seed-custom-alert-rules.ps1` knows 17 of them, is not idempotent, and hard-wires a private email address. Going forward an **embedded catalog** in `NodePilot.Core` (pattern `ActivityConfigReference`) + dialog on `/alerts` — **no** boot seeding, existing installations stay untouched. Templates are bilingual (DE/EN, created in the UI language), come **without a route** and always disabled; the recipient is created on activation. Four prerequisites, all already verified: (a) new column `NotificationRule.SourceTemplateId` as origin ID — name-based detection survives neither renaming nor a language switch; `SystemPresetId` is **not** overloaded (documented as system-only). (b) Allow routeless drafts, exactly as the system policies already do (`SystemAlertingController` 292-295 + enable gate 135-136) — this includes three follow-up locations: `PreviewRule` calls `TryBuildDraft` with a hard-coded `isEnabled: true`, and `results.All(…)` is `true` over the empty route list, so `test-fire` today **reports success without having sent anything**. (c) Catch up the client validation: `AlertingRuleEditor.formValid` requires routes unconditionally, and `toggleMutation` has no `onError` — an enable 400 vanishes without a trace. (d) Batch contract validate-then-mutate: language, unknown and duplicate IDs → 400 **before** any mutation; after that, deliberately a partial result only for the concurrent name collision, with `Detached` per failure, because `NotificationRuleStore.CreateAsync` saves per rule and a `DbUpdateException` would otherwise poison the tracker for the rest of the batch. Guard test with a positive **and** a negative fixture per template plus an allowlist check of the filter fields against `NotificationContext.ToFieldMap()` — a mistyped `folderpath` would otherwise yield a rule that looks correct and never fires. Replaces `scripts/seed-custom-alert-rules.ps1` (to be deleted). The actual effort is not the scaffolding but the 30 rule bodies including the English texts — those get their own PR without controller noise, so the review can ask the question that matters. Five PRs. |

**The ten presets (item 17), ascending learning curve:**

1. *Hello NodePilot* — `manualTrigger`→`log`→`runScript`→`returnData` (data bus, trigger-only roots)
2. *Disk Space Check* — `scheduleTrigger`→`runScript`→`decision`→`junction` (`param.*` capture, edge conditions)
3. *Log Rotation & Archive* — `folderOperation`→`forEach`→`fileHash`→`zipOperation`→`fileOperation`
4. *API Health Check with Alert* — `restApi`(+retry)→`jsonQuery`→`decision`→`emailNotification`
5. *Webhook Receiver* — `webhookTrigger`(fieldMappings)→`decision`→`runScript`
6. *Service Self-Healing* — `serviceManagement`→`decision`→start→`waitForCondition`→`emailNotification`
7. *Sub-Workflow Pair* (two workflows) — `startWorkflow` + child `returnData` → contract derivation
8. *Error Handling & Compensation* — `.failed` edges, `retry`, `exit N` vs. `successExitCodes`
9. *Inventory Report* — parallel branches→`junction`→`textFileEdit`→`emailNotification` (ancestor scope)
10. *Folder Watcher* — `fileWatcherTrigger`→`fileHash`→`decision`→zip/move (idempotency via hash)

Coverage: 21 of 35 catalog types + all five ControlFlow nodes. Deliberately left out (invasive or
environment-dependent, they belong in the test suite under `scripts/test-suite/`, see
`docs/workflow-tests.md`): `registryOperation`, `powerManagement`,
`scheduledTask`, `startProgram`, `xmlQuery`, `generateText`, `sql`, `eventLogTrigger`. Optional and
gated: AI log triage (`llmQuery`, needs `Llm:Enabled`), DB watcher (`databaseTrigger`).
Guardrails: `localhost` instead of a GUID, sticky notes as inline docs, external triggers arrive disabled
and are activated via *Publish* or *Enable* — both set `PublishedByUserId` if
the column is empty. A workflow that reaches `IsEnabled=true` without one of these paths ends in
`missing_effective_principal`.

### Wave 5 — AI expansion stage 1

The three items belong together and should be built as a block. They follow the shared
guardrails in [`ai-feature-ideas.md`](ai-feature-ideas.md).

| # | Item | Content |
|---|---|---|
| 21 | **Execution Incident Copilot** | "Analyze error with AI" on failed executions (list, operations view, history tab). Explains observed errors, separates evidence from conjecture, names next checks. Read-only, folder read permission, redacted data only, no retry, no publish. |
| 22 | **AI repair for lint and publish** | "Fix with AI" hands selected deterministic findings to the workflow assistant; the result is a diff that is applied in full or selectively. Applying requires Admin/Operator **and** the user's own edit lock; stale-hash protection, secret merge, and the save/publish flow remain binding. |
| 23 | **AI chat run card** | Tool `propose_workflow_run` executes **nothing** — it only resolves: workflow via `WorkflowNameResolver` (exact-case → case-insensitive → 409 on ambiguity, don't let the LLM guess), parameters against `GET /{id}/contract`, target machines. The chat renders a card with a "Run" button; only the **user's click** calls `POST /execute` — the user is the principal, not "the AI". Missing required parameters → follow-up question instead of an invented default. **The confirmation is a hard security boundary:** the chat reads docs, DB, and execution logs — each of these channels would otherwise be a prompt-injection vector. Additionally the toggle `AiKnowledge:AllowExecution` (default false), Admin/Operator gating, and its own audit code. |

### Wave 6 — Engine and Designer value

| # | Item | Content |
|---|---|---|
| 24 | **Rerun from step (hot resume)** | "Rerun from here" on a failed step: fresh execution, prefilled with the outputs of all upstream steps of the original run. Saves re-executing the first 46 of 50 steps. Three coupled changes, each with risk — detail below. Effort ~1.5–2 days with proper test coverage. |
| 25 | **Workflow tags / labels** | Color-coded tags on `Workflow` (`prod`, `qa`, `incident-response`); dashboard, quick switcher, and executions list filter by them, pills everywhere next to the name. ~1 day (migration + UI). Best effort/impact ratio in the Designer backlog. |

**Detail on item 24** — the three coupled changes:

1. **Engine signature** — `WorkflowEngine.ExecuteAsync` needs an optional
   `Dictionary<string, ActivityResult>? prefilledResults`. The scheduling loop must prefill `results`
   and `completed` with it, so that the scheduler skips them and uses them at the first
   uncached descendant. Hot path — needs careful tests for the
   `waitAny`/`waitAll`/`junction` cases.
2. **Output parameters** — `StepExecution` already persists `OutputParameters` in redacted form in the
   nullable column `OutputParametersJson`. A rerun must restore `{{step.param.hostName}}` references of the
   original run from it; if they are missing, they resolve to empty strings and silently break
   downstream logic.
3. **API + UI** — `POST /api/executions/{id}/rerun-from/{stepId}`: load the original run,
   build `prefilledResults` (via BFS on the graph, excluding `stepId` and its downstream),
   call `ExecuteAsync`. UI: context-menu entry in the history step list.

Files: `WorkflowEngine.cs` (signature + scheduler init), `Core/Models/StepExecution.cs`,
`Data/Migrations/`, `Api/Controllers/ExecutionsController.cs`,
`components/designer/ExecutionPanel.tsx` (HistoryRow context menu).

### Wave 7 — Guardrail

| # | Item | Content |
|---|---|---|
| 26 | **CI matrix: run migrations against real containers** | Top recommendation of the DB schema audit. **Run** the fresh install **and** the upgrade path against real PostgreSQL and SQL Server containers, not just `GenerateScript`. The existing `MigrationDriftTests` only generate scripts and therefore catch no pure runtime errors — e.g. an unguarded `DropTable` on a diverged DB. Closes an entire class of errors. |

### Wave 8 — Delivery

| # | Item | Content |
|---|---|---|
| 27 | ~~**GUI setup for the server installation**~~ **Done (2026-08-03, PR #108)** | Built and shipped under `deploy/server/`: `NodePilotServer.iss` + `Build-ServerInstaller.ps1`, adapter `deploy/Invoke-NodePilotSetup.ps1` + `deploy/SetupContract.ps1`, `deploy/Preflight.ps1`, tests `deploy/Test-SetupAdapter.ps1` + `deploy/Test-DeploymentTemplates.ps1`. The draft below stays as a blocklist entry because its decisions still hold. — `NodePilot-Server-Setup-<version>.exe` (Inno Setup 6) as a second way to the same installation; the ZIP path remains the reference, unchanged. The Pascal layer stays thin — pages and payload, no installation logic: the wizard writes an ACL-protected answer file and calls the unchanged `Install-NodePilot.ps1` through `Invoke-NodePilotSetup.ps1`. Necessary because `-PostgresPassword` is a `[SecureString]` and cannot be passed at all via `powershell.exe -File`; `/SILENT /ANSWERFILE=` thereby also covers SCCM/GPO. The biggest gain is not the UI but the removal of the trust ceremony: one asset instead of five, no manual thumbprint comparison, no manual intervention in `LocalMachine\Root`. Readiness page via the side-effect-free `Preflight.ps1` (item from the same wave), two opt-in actions (SQL login+DB only with sufficient rights, otherwise DDL output only; lab certificate with warning), ASP.NET Core runtime as the official Microsoft installer in the payload (SHA512 against release metadata **and** the checked-in pin, Authenticode on Microsoft, standalone runtime instead of Hosting Bundle). A re-run detects the installation via `HKLM\SOFTWARE\NodePilot\Server` and applies update semantics; `/FULLREINSTALL` forces a fresh setup including a new external-trigger API key (requires confirmation). **Uninstall does not remove the database and gets no option for it** — the installer doesn't create it, it is provisioned separately and shared by both nodes in a cluster. The only question is the data directory, default everywhere "keep". Test surface: `Test-SetupAdapter.ps1` (behavior) plus mutation-checked contracts in `Test-DeploymentTemplates.ps1`; the Pascal part deliberately stays minimal and, together with the manual smoke matrix, is documented as a test gap in `deploy/server/README.md`. |
| 28 | ~~**Turnkey first installation (without token entry)**~~ **Done (2026-08-05, PR #114)** | Built: `ProvisioningSeeder` (`Api/Security`), `NodePilot:BootstrapAdminUsername` in the token redemption path, answer-file groups `bootstrap`/`seed` in `deploy/SetupContract.ps1`. The draft below stays as a blocklist entry because its decisions still hold. — An unattended rollout used to end with an instance nobody can use: a human would have to type the setup token into the login form. Two mutually exclusive paths, without an additional switch — which one applies alone decides whether the instance has users afterwards. **(a) Answer-file group `bootstrap`:** the setup redeems the token itself and stores the generated credentials ACL-protected (SYSTEM + Administrators, ACL before content). Password **random per machine**, no `adminPassword` key — a fixed value would be identical across the whole fleet and would be scanned for rather than guessed, on a product that runs PowerShell on all managed machines. `NodePilot:BootstrapAdminUsername` is set along with it, so that an intercepted token can create only exactly this account. **(b) Answer-file group `seed`:** `ProvisioningSeeder` imports a `.npbackup` on first start — users, workflows, machines, credentials, and settings — **before** anything reads the user table, so that no token is created at all. Possible because the admin lock sits on the controller and not on the `BackupRestoreService`; the "empty target database" case is already handled there and requires a break-glass admin in the imported set — exactly the condition on which enabling LDAP/SSO later depends. Two rules make it safe to leave the seed configured permanently: it fills **only** an empty instance (never a migration), and it is **fail-closed** — a wrong passphrase or a broken file keeps the service from starting, instead of leaving a seemingly provisioned but actually empty instance. The passphrase goes into the `Environment` value of the service key, never into the JSON. **Fixed default credentials were considered and rejected.** Verified in the lab in five silent scenarios. |

---

## R2 — On the roadmap, trigger-gated

Every item carries its trigger condition. Nothing starts without a trigger.

### Data & Performance

| Item | Trigger |
|---|---|
| **F3 — SupportEvents free-text search** (full scan on unindexed `Message`/`UserName`) | Diagnostic query p95 > ~500 ms **or** SupportEvents > ~5 M rows in the retention window, **and** the search is actually used. Measurement query + provider-specific fix: [`db-schema-audit-followups.md`](db-schema-audit-followups.md). |
| **F7 — presumably unused AuditLog composites** | `idx_scan = 0` over ≥30 days of real traffic for a specific composite. |
| **Per-endpoint read budgets** | A slow but living database still lets interactive reads wait up to 120 s; outage and retry amplification are already covered by [ADR 0011](adr/0011-database-availability-breaker.md). |
| **Workflow list beyond 500 entries** — `GET /api/workflows` deliberately ends at 500 without continuation (DoS guard, pinned by `GetAll_CapsResultSetAt500_EvenWhenMoreWorkflowsExist`); UI search and folder filter work locally, MCP reports `totalAvailable` | An installation with more than 500 accessible workflows, e.g. a correspondingly large SCOrch export. Then build it as a feature across SPA, CLI, and MCP — the model is the executions pagination. Until then only the longest-untouched workflows beyond that disappear from lists and pickers; deep links and `/export` remain. Small interim step without a trigger: in MCP, replace `totalAvailable` with a `truncated` flag. |
| **F9 — GUID PK fragmentation** (SQL Server only) | Only on a SQL Server deployment with measured fragmentation > ~30 % despite regular rebuilds. Irrelevant on the default PostgreSQL. |
| Four small backend perf items (`ExecuteDeleteAsync` in the execution retention, shared `JsonSerializerOptions`, `LoggerMessage.DefineScope` in the StepRunner, incremental `HubRevocationSweeper`) | All size S–M and clean, but without measured pain. A good filler block when capacity is left over. |
| `manualChunks` in `vite.config.ts` · load locales separately (DE+EN ≈ 385 KB raw both sit in the eager path) | As soon as NodePilot is made reachable outside the LAN or initial load over VPN is reported. **The rest of this item is done**: route-level `React.lazy` covers 16 of 20 pages, Material Symbols no longer appear in the build at all, and ECharts was pulled out of the boot chunk on 2026-09-11 (eager path 591 → 386 KB gzip, see [`performance-improvements.md`](performance-improvements.md)). For the locale item, first assess the startup waterfall — an asynchronous i18n init must be awaited before the first render. |
| Enable React Compiler (`babel-plugin-react-compiler`) | When broad work on Designer render paths resumes. Today **only** the lint half runs: `eslint-plugin-react-hooks` v7 with `flat.recommended`, while `vite.config.ts` runs a bare `react()` and the Babel plugin is not installed — so there is **no** auto-memoization. The code is therefore largely compiler-clean (exception: `react-hooks/set-state-in-effect` is deliberately turned off), but every memo boundary is manual work. Needs the full frontend **and** E2E suite, and doesn't solve the algorithmic items anyway. |
| SignalR batch coalescing (condense N step events per execution into one delta) | If the concurrency plateau is to be lifted beyond the current ~600 in-flight steps. The only remaining way there. |
| Backend mini benchmark (`BenchmarkDotNet`) | When perf claims need to be substantiated. Not preventively. |

**Shared prerequisite for F3/F7/F9:** enable `pg_stat_statements` or Query Store and collect
≥30 days of representative production traffic. Without real statistics each of these
changes is speculation.

### Trigger & Integration

| Item | Trigger |
|---|---|
| Execution event trigger (workflow A ends → workflow B, without A knowing B) | First concrete chaining need. Needs a cycle/depth guard analogous to `startWorkflow` (maxCallDepth 10). |
| `databaseTrigger` from poll to push (Postgres `LISTEN/NOTIFY`, SQL Server Change Tracking) | When polling latency or load actually bothers. Upgrade of an existing type, not a new trigger. |
| WMI event subscription (`__InstanceCreationEvent` via WQL over WinRM, real push) | Customer demand. The most demanding trigger, but with the biggest "nobody else can do this" factor. |
| Threshold/performance trigger ("CPU > 90 % for 10 min", with hysteresis) | Customer demand **and** a prior delineation against the ADR 0008 system policies — they conceptually cover something similar. |
| AD/LDAP change trigger (DirSync/`uSNChanged`) · message queue (RabbitMQ/Service Bus/Kafka) · SNMP trap receiver | In each case the first concrete customer need. |

**Mandatory gotchas for every new trigger** (apply regardless of type):

- The extension point is `src/NodePilot.Scheduler/ITriggerSource.cs`. Mechanically cheap — the side surfaces
  are expensive: `ActivityCatalog.cs` + frontend mirror `activityCatalog.generated.ts`
  (parity test!), config component under `properties/triggers/`, i18n DE/EN, docs, tests.
- Trigger sources must not leak into the root DI container (see commit `34f7874b`).
- On the HA passive node nothing may fire twice → leader-gate external triggers.
- Trigger data lands as `manual.*` (+ `param.*` of the trigger node) — **no** `trigger.*` namespace.
- Trigger runs need `Workflow.PublishedByUserId`. Publish, import, duplicate, restore, and `/enable` set the column if it is empty (`/enable` never overwrites an existing publisher). Still not covered are a workflow that reaches `IsEnabled=true` without one of these paths (first-boot provisioning → `missing_effective_principal`) and a deactivated publisher (`effective_principal_inactive`); both need one publish.
- **Answer `Health`.** The orchestrator evaluates it sequentially for *every* trigger in the 5 s pass
  → **pure in-memory read**, no I/O, no lock. Anyone who needs real I/O probes on their own
  timer and caches the verdict. A constant `Healthy` is allowed, but only with a comment that names the
  gap (pattern: `EventLogTriggerSource`). Without it, a source that dies in operation stays
  registered forever and never fires again.

| Trigger-gated item | Trigger |
|---|---|
| Real health for `eventLogTrigger` (own probe loop instead of a constant `Healthy`) | First reported case of a silently dead `eventLogTrigger` subscription. `EventLog` has no fault channel; the only probe would be an RPC to the EventLog service. |
| Read API for runtime trigger state (`GET /api/triggers/status` + CLI + MCP) | An operator wants to inspect live state without waiting for an alert. The system policy `trigger-unhealthy` already covers "tell me"; this would be "let me browse". |

### Engine & Data Bus

| Item | Trigger |
|---|---|
| **`{{run.*}}` — metadata of the running workflow in the template** (`run.workflowName`, `run.executionId`, possibly `run.workflowId`/`run.startedAt`) | An SCOrch import going into production whose runbooks use `Policy.Name`/`Policy.PID` — **or** the first own need to name the run and workflow in text (ticket subject, log line, correlation ID). Measured on the reference export: **13 of the 46 warnings** are exactly these two fields. |

Today there is **nothing** for this: `VariableResolver` knows `globals.`, `manual.`, and `StepPattern`
(four tails). `__executionId`/`__workflowName` exist only as outputs of a **child** run that
`startWorkflow` started — from the inside, a workflow cannot get at its own data.

**Gotcha, if the item starts:** a new namespace needs its **own regex pattern** and
fundamentally cannot be hit by `StepPattern` — exactly that is how `manual.NAME` silently failed until 1.2.7
(the placeholder stayed in place, the step reported success). In addition: seeding in the engine,
`TemplateGrammarParityTests`, Designer variable picker, MCP/AI analyzer, and docs.

### AI

| Item | Trigger |
|---|---|
| Activity configuration copilot (idea 3) | After Wave 5 is complete. Starting with the most complex configs (decision trees, cron, REST, SQL, JSONPath/XPath, retry). |
| Workflow Generator v2 — guided draft with follow-up questions, graph preview, lint (idea 4) | When the one-shot flow is reported as a bottleneck. Today it works. |
| Reliability/optimization review (5) · custom activity copilot (6) · test case and mock generator (7) · operator copilot and shift briefing (8) | All of them build on 1–4. Reassess after Wave 5. |
| Six more knowledge tools in the global chat (globals, maintenance windows, custom activities, alerting rules, credentials, users) | Deliberately deferred. Resume when the chat is concretely asked for them. `read_settings` and the DB/text2sql source are already built. |

Description, user problem, and security boundaries per idea: [`ai-feature-ideas.md`](ai-feature-ideas.md).

### Alerting

Per-rule dedup key templates are already implemented (`DedupKeyTemplate`, "Group by" in the UI); see [Alerting](alerting.md).

| Item | Trigger |
|---|---|
| PagerDuty / Opsgenie sinks | First customer need. The enum values already exist, the sinks are missing — validation correctly rejects channels without a registered sink today. |
| "Resolved" recovery notifications for signal events | When silent recovery is reported as a deficiency. |
| Escalation policies | Enterprise need. |
| Populate `sourceKey` for execution events too | Consistency item, take it along when the dispatcher is being worked on anyway. |

### Designer

| Item | Trigger |
|---|---|
| Group/subgraph collapsible — fold N child nodes into a placeholder, back via double-click | Workflows regularly > 50 nodes. ~2 days (React Flow-internal node and edge handling for the collapsed state). |
| JSONPath picker in the output tab — click on a value → `{{step.param.hosts[0].name}}` with a copy button | When JSON paths stand out as a source of errors. ~1.5 days. |
| Machine online indicator per node (green/red, 5-min cache) | Build together with a WinRM reachability poller, not before. ~1 day. |
| Side-by-side workflow diff (two canvases, changed nodes highlighted) | When the existing text diff is reported as unusable for workflow-shaped changes. ~2 days. |
| Remaining list from the Designer redesign: skin block dedup in `index.css`, full ModalShell v2 consolidation of the eight overlays, checkbox→switch sweep across the remaining ~13 config files, palette drag ghost | As *one* collective item. Cosmetics with e2e risk — only tackle it when working on the Designer anyway. |

### Enterprise

| Item | Trigger |
|---|---|
| RBAC stage B — `WorkflowPermissions` table for per-resource sharing | Concrete customer request. Stage A (folder RBAC incl. group SID integration) has been in `main` since May 2026. |
| Active/Active cluster | Only on a need for **load** scaling. For availability, Active/Passive suffices (RTO 40–60 s). |
| HashiCorp Vault Transit / KMIP / cloud KMS · HSM-backed AES key · per-row key ID | `ISecretProtector` is the prepared seam — a new provider is one class plus a DI line. No up-front work needed, can be retrofitted at any time. Details: [`secrets-providers.md`](secrets-providers.md). |
| Windows SSO: remaining gaps until the "Preview" label is dropped | **Kerberos was verified against real AD on 2026-08-02** (Server 2025, gMSA, domain-joined client; 22 PASS — real `HTTP/<host>` ticket, transitive `tokenGroups`, LDAP↔Windows same UserId, JIT race, group gate). Harness + evidence: `scripts/ad-sso-labtest/`. **Still open:** HAProxy path, multi-DC consensus (lab has 1 DC), NTLM negative test (needs an SPN-free DNS alias — the lab attempt was inconclusive), session revocation on group removal, OIDC/SCIM, and HA restart. The label falls only when these points are cleared. |

### Quality & Operations

| Item | Trigger |
|---|---|
| Backend line coverage 89 % → 90 % | The ratchet in `ci.yml` is at 85 % line / 70 % branch. Raise it when broad testing happens anyway — not an end in itself. |
| `IWorkflowDefinitionMutator` — one mutation path for workflow definitions | From the audit 2026-08-15 (P1). Version, `UpdatedAt`, computed metadata, history, and trigger sync today sit side by side multiple times in Create/Publish/Duplicate/Restore. Trigger: the next bug that hits only one of these paths. |
| Enforce custom activity invariants on the DB side | From the audit 2026-08-15 (P2). Today only an app-level `ConcurrencyToken` in `CustomActivityDefinitionStore`; DB-side `IsConcurrencyToken()` and live key uniqueness are missing. Needs an EF migration + a parallel test with two DbContexts. Trigger: the first race observed in practice. |
| ~~Couple credential rotation to the WinRM pool~~ **Done (2026-09-06)** | From the audit 2026-08-15 (P2). The pool key carries a fingerprint of username, domain, and stored password; an edited credential immediately gets a fresh session, idle sessions under the old key expire via TTL, borrowed sessions finish their step under the identity they were opened with. Pulled forward because the idle TTL restarted on every reuse and an old identity could keep running indefinitely under constant load. |
| Bundle N+1 queries for notifications, workflow statistics, and folder grants | From the audits 2026-08-15 (P2) and 2026-09-05 (M2). Target scale according to the operator: ~100 parallel runs. Folder grants: `ResourceAuthorizationService.GetAncestryGrantsAsync` queries once per distinct folder — only for non-admins, bounded by the 500 cap; a candidate would be the folder tree endpoint, which scales with the total folder count. Trigger: measured latency, not suspicion — first cross-read `docs/performance-improvements.md`. |
| Fence step writes DB-side (HA) | From the audit 2026-08-26. Execution terminal writes go through CAS, step writes through a normal `SaveChanges`; `ClusterFencingHost` cancels cooperatively, and orphaned `Running` steps are cleaned up by `ExecutionStateLifecycle.CancelOrphanedStepsAsync`. Trigger: an observed step write from a deposed leader after a failover. |
| Secure audit and SIEM delivery via an outbox | From the audit 2026-08-26. `AuditWriter` deliberately does not abort a mutation on a write failure — a successful mutation therefore does not guarantee an audit record. Trigger: a compliance requirement that demands a gap-free audit trail per mutation. |
| Separate the migration identity from the runtime identity | From the audit 2026-08-26. The runtime identity today needs `db_owner` on the NodePilot database because it migrates at boot. Trigger: a customer DBA demands the separation. |
| Strict E2E fixture: unmocked `/api` calls fail the test | From the audit 2026-09-05 (T1). `e2e/fixtures/mockApi.ts` answers unmocked calls with `200 []` — a documented convention (`e2e/README.md`). The gap affects read-only endpoints whose payload no spec checks; object endpoints visibly fail with `[]`. Trigger: a bug that slipped through the catch-all response. First review all specs for incidental calls. |
| Implement the tree algorithms of the two folder trees once | From the audit 2026-09-05. `RecomputePathsRecursive`, `IsDescendant`, and `DescendantMaxDepth` exist twice, in `SharedWorkflowFoldersController` and `GlobalVariableFolderStore`. Separate services because of different authorization remain right, duplicate algorithms do not. Trigger: the next bug that hits only one of the two copies. |
| Move the process-wide state of the `WorkflowEngine` into a DI singleton | From the audit 2026-09-05. Static dictionaries (running executions, cancel reasons, user counters, debug handles) force the engine tests into the serial `SerialEngineTestCollection`. Trigger: the engine suite becomes a time factor in CI, or a test flakes on the shared state. |
| Small remaining items from the audit 2026-09-05 | (a) `GlobalVariableFoldersController.MapError`: `_ => throw ex` resets the stack trace, and `InvalidOperationException` is blanket-treated as an input error (400). (b) Switcher: `ActivityLogger` keeps the history in memory without bound, `MainWindowViewModel` rebuilds the whole list on every entry — ring buffer and incremental update. (c) Done: `Toc.tsx` re-reads the headings on a language change, and `resolveDocLink` in `DocMarkdown.tsx` keeps the fragment. Trigger: take along when working on the respective file anyway. |
| Real integration boundaries (container migrations, browser↔real API, Electron smoke) | From the audit 2026-08-15 (P2). All existing suites are hermetically mocked. The largest single item on the list; sensible only piece by piece. |
| Split `WorkflowEditorPage` into hooks + gzip budgets in CI | From the audit 2026-08-15 (P2). The page has grown further recently. Monaco and ELK remain permitted lazy exceptions. Trigger: when the initial chunk becomes noticeable. |
| Reduce xUnit1051, production nullable warnings as errors | From the audit 2026-08-15 (P3). Deliberately incremental — as a big bang it breaks half the suite at once. |
| Split `WorkflowEditorPage.test.tsx` (2166 lines / ~108 tests) | When the frontend suite becomes a time factor in CI. The flakiness itself is fixed. |
| E2E spec for `/metrics/:section` | The only page without a Playwright spec. Mock-smoke-testable. |
| `/operations` (Live Ops) beyond the snapshot happy path | RBAC cross-checks, drilldown, and health rail are unasserted; SignalR live remains untestable (404 stub). |
| MCP Streamable HTTP transport | Deliberately documented as an extension point, not built. First need for a non-stdio connection. |
| Least-privilege DB login instead of the lexical read-only check | A deployment topic, not a code topic. The authoritative solution for the DbAdmin read path on SQL Server/SQLite. |

---

## E — Open decisions

These items cannot be built as long as the decision is pending. They are
deliberately not R2 items: what's missing is not the trigger, it's the decision. The question about
downloads in the desktop app is decided: same origin with a native Save dialog (PR #448).

| Question | Context |
|---|---|
| **SSH/Linux cross-platform: rebuild or drop?** | The earlier implementation is **no longer in the repo** — `RemoteProtocol` appears nowhere in `NodePilot.Core`, the branch and PR were lost with the public-repo migration. At that time: ~11,000 lines across 107 files, platform-aware backend (bash adapter for runScript/File/Folder/Service/StartProgram), SSH.NET stack with host-key TOFU/strict, verified against Debian bookworm via Docker. The known gap was UI-side only: `RunScriptConfig.tsx` remained PowerShell-centric. Continuing it as "paused" is the only option that is clearly wrong. |
| **Multi-DC: quorum or all-DC consensus?** | Today strict all-DC consensus, **no** failover: one DC down ⇒ external logins 503. This is pinned by a unit test and is now honestly documented (it used to say "failover" by mistake). Whether it stays that way is a product decision — not a bug. |
| **Make third-party MCP servers usable in the AI chat?** | An admin configures external MCP servers, the chat may call their read-only tools. That is the actual benefit of an MCP **client** in the backend — connecting third-party tools. The reverse direction (driving the chat over `nodepilot-mcp` onto the *own* tools) was examined and rejected: `nodepilot-mcp` authenticates with *one* DPAPI session, whereas the chat runs per request under the caller's `ClaimsPrincipal` (folder RBAC, SQL only for global admins) — redirecting would put all tool calls under a service identity; plus loopback HTTP per call and ~90 write tools that the chat deliberately doesn't have. The shared analysis logic lives in `NodePilot.Core` instead (`WorkflowAnalyzer`, `WorkflowDataBusAnalyzer`), consumed by both surfaces. Open for the client case: trust model against prompt injection from third-party tool results, RBAC gate, process lifecycle. |

---

## Appendix — Deliberately not on the roadmap

This section is a **blocklist**. It exists so these ideas don't resurface every few months
and get re-analyzed.

### Disproved by measurement (Performance)

All of the following levers were measured in real 500-parallel stress tests and are
**actively harmful**. Evidence, numbers, and reasoning: [`performance-improvements.md`](performance-improvements.md).

| Idea | Measured result |
|---|---|
| Raise concurrency caps (`MaxConcurrentSteps` 600→1500, runspace 768→1500) | **42 % worse.** The system saturates *downstream* of the engine — CIM providers serialize, the OS process spawn rate is throttled by CSRSS. |
| Reduce concurrency caps (600→300) | **9 % worse.** 500 workflows × 1 step need at least 500 slots. The defaults are close to the optimum in both directions. |
| Raise `ChildSemaphore` from 128 to 600 | **16 % worse.** Only moves the backlog one layer further. |
| Enlarge the DB pool (800→1500) | **Zero effect.** Only ~85 of 480 pool slots are occupied under load. |
| Eager RunspacePool pre-warm + `MinRunspaces` 768 | **28 % regression.** OS thread costs dominate over runspace init costs; the pool grows organically under load anyway. |
| `VACUUM ANALYZE` on the hot tables | **Zero effect.** Autovacuum already keeps `n_dead_tup` near 0. |
| Batch writes for `StepExecution` persists via a 50 ms channel | Attractive on paper, but breaks the live step display (the UI doesn't see steps in the DB before the batch flushes) and complicates the cancellation paths. Architecture risk too high. |

**Rule derived from this:** when the next "let's build pre-warm / raise the caps" proposal comes,
first verify it with the documented 500-parallel test, then commit. The gut-feeling
"more slots must be faster" is concretely wrong here.

### Rejected product decisions

| Idea | Reason |
|---|---|
| Native Teams / Slack sinks | Struck by the Product Owner. The need is covered via `webhookTrigger` + `fieldMappings` and generic webhooks (see R1 item 19). |
| Multi-tenancy (RBAC stage C) | Explicitly struck on 2026-05-15 — no enterprise customer need in sight that would justify 6–8 person-days. |
| OIDC/Entra ID as a roadmap item | Has meanwhile been built (release-gated, incl. SCIM controller) — the old "struck" note is outdated. |
| Composite / sub-workflow nodes for custom activities | Deliberately rejected for v1: custom activities remain pure `runScript` presets, no second script engine. |
| `secret` input type for custom activities | Struck — secrets go through `{{globals.X}}` and credentials. |
| R2 "only `manualTrigger` can be called as a child" | Withdrawn in favor of model C: `startWorkflow` calls **every** enabled workflow, regardless of trigger type. Known, deliberately accepted edge case: a child with multiple triggers fires all of them as roots on the call. |
| Multi-tone node icons | Rejected on 2026-07-08 — flat silhouette + glyph stays. |
| `dotnet pack` for CLI/MCP (global tools) | The tools depend transitively on `net10.0-windows`; `PackAsTool` forbids a platform TFM (NETSDK1146). Real tool packages would need multi-targeting of the whole chain. **Re-measured on 2026-08-02: `dotnet pack` fails reproducibly for both projects.** Until then the docs had given `dotnet tool install -g` as the installation path — i.e. a path that never existed; they now point to `dotnet publish` + `PATH`. The dead `PackAsTool`/`ToolCommandName`/`PackageId` properties have been removed from both `.csproj` files so the claim doesn't resurface. |

### Refactoring without a trigger

| Idea | Reason |
|---|---|
| Split up `WorkflowEngine` (~1500 LOC) | Well tested, high risk in the split, no pain point. A detailed 8-PR cut plan was presented and assessed as technically sound — but not carried out without a trigger. **Resume only** if a new feature (dynamic sub-DAGs, saga patterns, engine plugins) fails on the monolith. |
| Slim down controllers (`WorkflowsController` ~970 LOC, `ExecutionsController` ~660 LOC) | Controllers may be fat as long as the logic is testable — it is. **Resume only** if new endpoints become ugly without a service layer. |
| Decompose `BackupRestoreService` (~1650 LOC) | From the audit 2026-09-05. The restore is correct and tested. **Resume** when a new backup area no longer fits in cleanly — then cut along a restore planner. |
| Decompose `Install-NodePilot.ps1` (~1700 LOC: ACLs, certificate rights, SCM creation, rollback) | From the audit 2026-09-05. Secured by `Test-SetupAdapter.ps1` and `Test-DeploymentTemplates.ps1`. **Resume** when an installer change fails on the size; the cut boundary is the existing `SetupContract`. |
| ~~`WorkflowEditorPage` overlays host (B5)~~ **Done (2026-10-16)** | The trigger (over ~1700 LOC) had occurred at about 2060 lines. The overlay states live in `useEditorOverlays`, lock/save/publish in a once-computed `EditorLifecycle` (`useEditorLifecycle`); edge detach, tidy, and export are their own hooks, the new-workflow pill and simulation banner their own components. `EditorOverlays` and `EditorHeader` get grouped objects instead of about 90 and 46 individual props respectively. The page is at about 1490 lines. |

### Externally blocked

Not our problem, but documented so it isn't tried again:

- **Microsoft.OpenApi 3.x** — Swashbuckle 10.2.3 requires the 2.x surface (transitive floor 2.7.5).
  The pin on 2.x (currently 2.12.2) remains necessary. Only follow after a Swashbuckle major.
- **TypeScript 7** — `typescript-eslint` aborts hard on load; support is only planned from TS ≥ 7.1.
  Side-by-side TS 6+7 or sacrificing the lint gate were both rejected.
- **Spectre.Console 0.57.2** — `Spectre.Console.Cli` ends stably at 0.55.0 and pins the core to
  its own version. Family deliberately closed at 0.55.0.
- **WireMock.Net 2.16.0** — pulls in `Microsoft.OpenApi 3.10.2` transitively and collides with the 2.x
  floor above (NU1605 on restore). Stays on 2.15.0, to be raised together with the OpenApi entry.
- **jsdom 30.1.0** — the four `BackupPage` tests that post a FormData body via msw run into
  the timeout; reproduced locally against 30.1.0. Stays on 30.0.x until msw catches up.
- **vitest 5** — `@vitest/coverage-v8` pins its peer to the exact vitest version, so the bump
  only works as a family. Tried: vitest 5.0.1 runs all three roots green (3,219 / 136 / 112) and the
  coverage gate holds, but in the full run of `nodepilot-ui` `ExecutionsPage` fails on a
  cross-file contamination that does not occur in isolation or under vitest 4. A migration, not a bump.

### Not product scope

- **Codex plan review hook** — developer tooling (`PreToolUse` hook on `ExitPlanMode`), belongs on
  a tooling list, not on the product roadmap.
- **OS tuning** (`MaxShellsPerUser`, CSRSS desktop heap) — to be set per host, not controllable in the repo.
- **Workflow definition rewrites** (e.g. replacing `Get-CimInstance Win32_*` with cached calls) —
  would be the only lever with a double-digit perf gain in the demo workload, but is
  workflow-specific and outside the engine scope. A starting point for future Designer lints.

---

## Related documents

| Document | Role |
|---|---|
| [`db-schema-audit-followups.md`](db-schema-audit-followups.md) | Measurement queries, thresholds, and resumption conditions for F3/F7/F9. The format of this document is the template for all R2 items. |
| [`ai-feature-ideas.md`](ai-feature-ideas.md) | User problem, functional description, and security boundaries per AI idea. Prioritization lives here in the roadmap. |
| [`performance-improvements.md`](performance-improvements.md) | Implemented optimizations, tuning insights, and the measurement logs behind the blocklist above. |
| [`security-findings.md`](security-findings.md) | Register of **fixed** findings with fix and test. Not a backlog. |
| [`alerting-rule-templates-plan.md`](alerting-rule-templates-plan.md) | Detailed plan for item 20: catalog schema, batch contract, routeless drafts, PR cut. State of the decision, not of the code. |
| [`alerting.md`](alerting.md) · [`custom-activities.md`](custom-activities.md) · [`enterprise-features.md`](enterprise-features.md) | Domain detail for the respective areas. |
