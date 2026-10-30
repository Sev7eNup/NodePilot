# NodePilot

Modern, lean replacement for Microsoft System Center Orchestrator. Agentless workflow orchestration for Windows environments via WinRM.

**This file is the index, not the documentation.** It holds behaviour rules, lookup tables and invariants — the depth lives in `docs/`.

**Upkeep:** A feature PR adds at most one line here. Anyone who wants to write a paragraph writes it in `docs/claude-reference.md` and links it from here. No error flashbacks ("it used to be…"), no measurements as evidence — the same rule as for code comments. If an explanation already exists in `docs/`, only the pointer stands here. Size is capped by `DocumentationCountsTests`.

**Language:** Repository documentation is English. Only the German website content (`src/nodepilot-docs-ui/content/de/`, `*.de.md`, the `de` dictionaries) is German.

## Agent skills

- **Issue tracker:** GitHub Issues for `Sev7eNup/NodePilot`, see `docs/agents/issue-tracker.md`
- **Triage labels:** five-label vocabulary, see `docs/agents/triage-labels.md`
- **Domain docs:** single-context repo → root `CONTEXT.md` + `docs/adr/`, see `docs/agents/domain.md`

## Documentation map

- **UI work on the dependencies menu:** read `design/dependencies/DESIGN.md` first (local only, not in the repo); mockup, notes and approval state form the visual plan.

- `docs/roadmap.md` — **the leading document for "what gets built"** (R1 committed, R2 trigger-gated, E open, rejection appendix). Anything not in it is not a plan.
- `docs/claude-reference.md` — **overflow reference for this file**; the sections here point to their depth there
- `docs/alerting.md` — notification rules + system policies (ADR 0008), dispatcher, sinks, ledger
- `docs/custom-activities.md` — custom activities (plugin system)
- `docs/mcp-server.md` — MCP server incl. tool catalog + `.mcp.json` example
- `docs/ai-features.md` — AI features: config keys, model recommendations
- `docs/deployment-guide.md` — verify the artifact, build it yourself, troubleshooting — **not** the installation path, which lives **once** on the documentation website (`content/{de,en}/deployment/production.md`)
- `docs/av-exclusions.md` — antivirus exclusions (server + desktop) as a hand-over document for an AV team
- `docs/workflow-styleguide.md` — layout style guide for workflow JSONs (**read before every workflow generation**)
- `docs/workflow-tests.md` — test suite under `scripts/test-suite/`: 51 generated workflows against the running engine, `suite-manifest.json` as the coverage source, guard test `TestSuiteCoverageTests`
- `docs/enterprise-features.md` — HA, secret providers, LDAP/SSO, SIEM, folder RBAC
- `docs/threat-model.md` — trust boundaries, global credentials, agent policy and security evidence
- `docs/ai-feature-ideas.md` — descriptive depth for the AI ideas, **not a specification**. Priority and status live in `docs/roadmap.md`.
- `src/nodepilot-ui/e2e/README.md` — E2E coverage map + spec conventions
- `src/nodepilot-ui/demo/` — **browser demo** of the SPA on www.nodepilot.run (`/demo/`), the same app against an in-memory backend; rules in `src/nodepilot-ui/CLAUDE.md`
- `src/nodepilot-docs-ui/src/site/` — **project website** (DE/EN) at the website root, docs beneath it under `/docs/`; **never** part of `dist/`. Build, routes, preview: `src/nodepilot-docs-ui/README.md`. Imprint/privacy policy are supplied by the user.

## Tech stack

- **Backend:** ASP.NET Core Web API, .NET 10, Windows-only (`net10.0-windows`)
- **Database:** PostgreSQL (default) / SQL Server (`Database:Provider` = `postgres` | `sqlserver`). SQLite only as the in-memory test backend.
- **Remote execution:** PowerShell SDK / WinRM, agentless. `Remote:Provider`: `winrm` (default) | `noop` (`noop` needs `Remote:AllowNoop=true` or `NODEPILOT_ALLOW_NOOP_REMOTE=1`, otherwise boot aborts). Local user scripts (`engine: auto`) run as a Windows PowerShell 5.1 process, like remote; the in-proc pool (WinPS compatibility deliberately off) serves the built-in activities and `engine: runspace`: `docs/claude-reference.md`
- **Real-time:** SignalR (`/hubs/execution`)
- **Logging:** Serilog. Format via `Logging:Format`: `text`|`cmtrace`|`json`|`ecs-json` (ECS 1.x for SIEM, see `docs/siem-logging.md`). Support log: file + DB projection
- **MCP server (opt-in):** `nodepilot-mcp` (stdio) — an AI agent drives/edits workflows through 116 tools, HTTP-only against the REST API
- **Enterprise (opt-in):** active/passive HA (`Cluster:Enabled`), pluggable secret providers (`Secrets:Provider` = `Dpapi`|`AesGcm`), LDAP/Windows SSO, ECS-JSON SIEM, folder RBAC

## Solution structure

Project layout under `src/` + `tests/` — not mirrored here, look it up directly. The dependency direction is binding:

**Dep graph:** `Api -> Ai, Engine, Scheduler, Data, Remote, Core, Telemetry` | `Engine -> Ai, Data, Remote, Core, Telemetry` | `Scheduler -> Engine, Data, Core` (application tier: consumes Engine notifications/conditions/security) | `Ai -> Core` (LLM stack, sits below Engine so Api+Engine share it) | `Data -> Core` | `Remote -> Core` | `Telemetry -> Core` | `Cli -> Core` (HTTP-only) | `Mcp -> Core` (HTTP-only, MCP server) | `Switcher -> ∅` (local Windows SCM WPF app). Machine-enforced by `DependencyDirectionTests` (Api.Tests/Architecture) — a graph change means csproj + this line + the test change together.

## Running the project

```powershell
& 'C:\NodePilot-Postgres\pgsql\bin\pg_ctl.exe' start -D 'C:\NodePilot-Postgres\data' -l 'C:\NodePilot-Postgres\data\postgres.log' -w   # this machine's Postgres — always first
cd src\NodePilot.Api; dotnet run          # backend, port 5000 (launchSettings.json = target of the Vite proxy)
cd src\nodepilot-ui; npm run dev          # frontend, port 5173
cd src\nodepilot-docs-ui; npm run dev     # documentation website, port 5174 — only if /docs should be reachable in dev
```

**The first login needs the setup token** from `src\NodePilot.Api\admin-setup.token` (the login form shows the field on the first attempt). Creating the role/DB, connection string: `CONTRIBUTING.md` § Local setup.

**`/docs` in dev:** Vite proxies `/docs` to 5174; if the docs dev server is not running, the docs button leads nowhere — expected, not a defect. The docs dev server itself runs under `/docs/` (`--base=/docs/` in the dev script, **because Vite 8 ignores the config's `base` in dev**); without it you land in the app instead of the docs.

**For Claude:** use dev mode. **API restarts (stop+rebuild+start) are allowed at any time without asking** — DLL locks are normal. First find the PID via `Get-NetTCPConnection -LocalPort 5000`, then `Stop-Process` + rebuild + start. `npm run dev` broken → `npm install`. Deploy scripts under `deploy/` run **only on explicit request** — each approval covers only that one run.

**Long-running processes (API + Vite):** start detached/as a background process (e.g. `Start-Process` with redirected logs) so they survive tool-call boundaries. Report "running" only after a full port check (`Get-NetTCPConnection -LocalPort 5000` **without** `-First N`) **and** an HTTP health probe. Before every kill, verify it is the dev instance — **never** hit the installed Windows service.

## How Claude works here

- **Nothing leaves the machine without an explicit request.** `git commit`, `git push`, `gh pr create`, `gh pr merge`, `gh pr comment`, `gh issue create`/`comment`, `gh release create`, tags, branch deletion — each of these steps needs its own request from the user. Working locally (creating a branch, editing, building, testing) is free; as soon as something leaves the repository or lands in `main`, ask.
  - **"go", "do it", "implement it" means: implement.** It does **not** mean commit, push, open a PR or merge. If the user wants a commit/PR/merge, they say so (e.g. "pr and merge please") — and that approval covers **only that one operation**, not the next task.
  - After finishing, leave the state in the working tree and briefly report what is ready. Don't commit pre-emptively "so nothing gets lost".
- **Branching:** start non-trivial work on a new branch **before** editing; ask only if the branch name is unclear. Trivial one-liners (e.g. `.gitignore`) get **no** branch/PR of their own — fold them into the ongoing work.
- **PR budget: at most 5 PRs at a time** for your own work batches (cut larger efforts into ≤5 PRs). **This number does not apply to Dependabot**: `.github/dependabot.yml` bundles minor/patch per ecosystem (`open-pull-requests-limit: 1` per block → max. 5 group PRs), but **every open major drops out of the group and gets its own PR**. Majors stay ungrouped on purpose, because a bundle makes review worse.
- **Every change to `.github/dependabot.yml` immediately re-triggers all blocks** (regardless of the Monday schedule) and produces new PRs within minutes. So **batch** config edits instead of merging them one after another.
- **Scope:** minimal root-cause fix. If a fix would touch significantly more files than the named problem → stop and state the planned scope in 3 bullets before editing.
- **No backward compatibility:** no shims, feature flags, or optional defaults for a gentle migration. Go all the way: `NOT NULL`, required properties, delete old code paths without replacement. Old DB → run migrations, done. A break in a format that installations keep (export/backup envelope, workflow JSON) is raised beforehand and then carried through just as shim-free.
- **New dependency** (NuGet via `Directory.Packages.props`, npm) only after asking. Touch lockfiles only when resolution actually changes.
- **PowerShell 5.1 / Windows:** no inline SQL through PowerShell quoting — write the query to a `.sql` file and run it with `psql -f`. Write files as UTF-8 **without** BOM. No `sed`/regex line edits on source files (CRLF breaks them) — use the Edit tool. No `$args` splatting; explicit named parameters.
- **Code comments:** factual and short, in plain English. They say **what** the code does and **why** — nothing more. No derivation, no narrative, no flashback to earlier failed attempts, no measurements or example figures as evidence, no "X used to …, which meant …". Whoever needs the background finds it in the commit message, PR or `docs/`. One to three lines almost always suffice; a comment longer than the code below it is usually a narrative. The existing long comments in the repo are **not** a model.
- **Reporting:** report briefly — what changed, what was verified, what is open. No per-file walkthroughs, no retelling of the plan. Interactive questions only when the answer really blocks.

## Language rules (C#, React/TS, PowerShell)

**C#** (`src/**/*.cs`, `tests/**/*.cs`)
- Async all the way. No `.Result`, no `.Wait()`, no `GetAwaiter().GetResult()` on I/O.
- Pass `CancellationToken` through wherever the operation can sensibly be cancelled. State and lifecycle writes that must land even on cancellation save with `CancellationToken.None` (a `SaveChangesAsync` cancelled after the commit leaves entities `Added` → duplicate INSERT; guard: `StepStatePersistenceInvariantTests`).
- Fix nullability warnings; don't silence them with `!` or `#pragma`.
- An interface only for a real second implementation or a real test seam (remote layer, clock, LLM client), not one per class.
- Concurrency only bounded (semaphore/channel with capacity), with defined error and cancel behaviour.

**React / TypeScript** (`src/nodepilot-ui/`, `src/nodepilot-docs-ui/`)
- No unsafe casts (`as unknown as T`) to get rid of a type error.
- Don't store state that can be derived from props or other state.
- Render or event logic before `useEffect`. The lint rule `react-hooks/set-state-in-effect` is off, so this is a convention, not a check.
- No `useMemo`/`useCallback` without a concrete reason (reference stability for a dependency, measured cost).
- Network access goes through `src/api/` and React Query, not directly in components.
- Semantic, accessible controls (`<button>`, `<label>`) instead of clickable `<div>`s.

**PowerShell** (`**/*.ps1`, `**/*.psm1`)
- Target Windows PowerShell 5.1. No 7.x syntax (`??`, `?.`, ternary, `&&`/`||`).
- Script header as in `deploy/`: `[CmdletBinding()]`, `$ErrorActionPreference = 'Stop'`, `Set-StrictMode -Version 3.0`.
- Approved verbs, full cmdlet and parameter names, no aliases.
- Credentials as `PSCredential` or through the secret provider, never in plain text.
- Installer scripts under `deploy/desktop/` and `deploy/server/` stay ASCII-only (PS 5.1 reads UTF-8 without BOM as ANSI).

## Database

| Provider | `Database:Provider` | Connection string key |
|---|---|---|
| PostgreSQL (default) | `"postgres"` | `ConnectionStrings:Postgres` |
| SQL Server | `"sqlserver"` | `ConnectionStrings:DefaultConnection` |

- **One shared migration set**, provider-agnostic (without `type:` strings). Bootstrap via `db.Database.Migrate()`. Never automatically delete existing databases or rewrite their migration history (baseline and upgrade path: `CONTRIBUTING.md`).
- **New migration:** `dotnet ef migrations add <Name> --project src/NodePilot.Data --startup-project src/NodePilot.Api --context NodePilotDbContext`. **Mandatory post-processing — two steps:**
  1. In the migration (`<Name>.cs`): remove all `type: "..."` annotations.
  2. In the designer file (`<Name>.Designer.cs`): add `MigrationModelPortability.UseActiveProviderStoreTypes(modelBuilder);` as the last line before `#pragma warning restore 612, 618` in `BuildTargetModel`. The `ModelSnapshot` deliberately does **not** get the call (diff baseline, not a migration target model).

  Both steps are covered by `MigrationDriftTests` — run them instead of relying on memory.
- Schema changes ALWAYS via EF migration. No DDL hot-patching.
- Credentials encrypted with DPAPI (`Credentials:DpapiScope`). DB TLS is strict (`DatabaseTlsBootValidator`); escape conditions, retention services and all background services: `docs/claude-reference.md`.

### Database availability (runtime outage, ADR 0011)

Process-wide in-memory breaker (`NodePilot.Data.Availability`): if the DB fails at runtime, `/api` answers `503 DATABASE_UNAVAILABLE` immediately instead of hanging. Invariants and health endpoints: `docs/adr/0011-database-availability-breaker.md` + `docs/claude-reference.md`. Two invariants stand here because with only this file you would otherwise "repair" them:

- **Only the probe publishes `Available`** (`SELECT 1` on its own unpooled connection, never `CanConnectAsync`); EF interceptors only degrade, a command timeout only arms the probe. Background services park via `WaitUntilServableAsync`.
- `Database:AuthReadTimeoutSeconds` and the other availability budgets are restart-required boot config and deliberately **not** a `SettingsSchema` entry — the connection string belongs on no HTTP surface.

**Known trap (deliberate):** `HostOptions.BackgroundServiceExceptionBehavior` stays at `StopHost`, and the seven retention services have their broad catch one level *below* the host-fatal boundary — code that lands in their `RunIterationAsync` outside the inner `try` can kill the host.

## API endpoints

Routes + role gating live on the controllers in `src/NodePilot.Api/Controllers/` (`[Route]`/`[Authorize]`) — look there instead of mirroring them here. Role matrix under `## Authorization`.

**Unmatched `/api` paths answer `404 application/problem+json`** (`code: NOT_FOUND`), not with the SPA bundle — a dedicated `MapFallback("/api/{**rest}")` sits before `MapFallbackToFile("index.html")`. This also covers route parameters that miss their type constraint. Deep links outside `/api` still go to the SPA.

## Workflow control flow

| Endpoint | Semantics |
|---|---|
| `POST /execute` | Starts a run, asynchronous. Body: `{"parameters": {}, "timeoutSeconds": N, "debug": bool}`. 202 + ExecutionId, progress via SignalR. |
| `POST /enable` / `/disable` | Kill switch. `enable` requires a lock-free workflow (any lock, including your own → 423), validates the **saved** definition and fills `PublishedByUserId` if unset — an existing one is never overwritten. Already active = no-op (`204`) **without** a stamp. `disable` ignores locks. |
| `POST /cancel-all` | Cancels all `Running` **and** `Pending` executions of the workflow. |
| `PUT /concurrency-limit` | Sets `MaxConcurrentExecutions` (1..1000, `null` = unlimited). `maxConcurrentExecutions` is **required** (missing → 400, otherwise `{}` would silently clear the limit), `0` is rejected. No edit lock, no version bump, no history snapshot. |
| `POST /executions/{id}/cancel\|retry\|resume` | A single run. Resume body: `{"stepId": "<node-id>", "mode": "continue"\|"stepOver"\|"stop", "overrides": {}}` — `stepId` is **required**. |
| `POST /{id}/rollback` | Like `Update`, snapshots the previous definition into the version history. |

**Disable+cancel-all = quarantine.**

**Per-workflow concurrency:** `Workflow.MaxConcurrentExecutions` limits concurrent runs of *one* workflow across **all** callers; at the limit runs are **queued instead of rejected** (one counter for both paths: `IWorkflowConcurrencyGate`), and the dispatch claim skips workflows at the limit. Not versioned, not in the update/publish body. Details: `docs/claude-reference.md` § Execution and concurrency.

## Edit lifecycle (SCOrch-style edit lock)

Workflows have a per-user edit lock (`CheckedOutByUserId` + `CheckedOutAt`). Mutating endpoints return `423 Locked` if the caller is not the lock owner. `PUT` and `publish` require `expectedVersion`; a stale draft → `409 workflow_version_conflict`. `Disable` is **not** lock-gated (incident kill switch).

| Endpoint | Behaviour |
|---|---|
| `POST /lock` | Atomically sets `IsEnabled=false` + the lock fields. 409 if already locked. |
| `POST /unlock` | Lock fields to null. `IsEnabled` stays unchanged. |
| `POST /publish` | Atomic: save + `IsEnabled=true` + unlock. Validates the **submitted** body first: weak webhook HMAC secret → `400 weak_webhook_hmac_secret`, cron expression invalid for Quartz → `400 invalid_cron_expression`, `waitNofM` above its inputs → `400 invalid_junction_required_count`. `enable` checks the same on the saved definition. |
| `POST /force-unlock` | Admin-only. Breaks someone else's lock. |

UX flow and button-state matrix: `docs/workflow-designer-features.md`. In short: `canWrite = role !== 'Viewer' && checkedOutByUserId === currentUserId`.

## Activity types

"Remote" = `targetMachineId`/WinRM. "Engine-local" = inside the API process. `(controlFlow)` = category `ControlFlow` in the backend `ActivityCatalog` (palette axis, independent of scope).

- **Remote:** `fileOperation`, `folderOperation`, `textFileEdit`, `serviceManagement`, `registryOperation`, `wmiQuery`, `startProgram`, `powerManagement`, `scheduledTask`, `fileHash`, `zipOperation`
- **Engine-local:** `restApi`, `sql`, `emailNotification`, `delay`, `log`, `generateText`, `llmQuery`, `aiAgent`, `aiAgentTeam` + controlFlow: `junction`, `forEach`, `decision`, `startWorkflow`, `returnData`
- **Hybrid:** `runScript`, `waitForCondition`, `xmlQuery`, `jsonQuery`

Config keys & output semantics per activity and process isolation (`config.isolated: true`, local only, no-op on the WinRM path): `docs/claude-reference.md`.

- **Retry per step:** `config.retry` (`maxAttempts`, `backoff`, `initialDelayMs`, `maxDelayMs`). Permanent remote errors (rejected WinRM logon, blocked HTTP session, undecryptable credential) are **not** retried — otherwise a step locks out an account.
- **Execution timeout:** `timeoutSeconds` in the execute body + per-step `config.timeoutSeconds`.

## Custom activities (plugin system)

User-authored, PowerShell-backed activities (UI "Custom Nodes") as pure **runScript presets** — the same engine, isolation, marker capture and redaction, no second script engine. `activityType = custom:<key>` → one sentinel-registered `CustomActivityExecutor` (`__customDefinitionId` authoritative, `__customKey` drift guard); only the declared outputs (+ `exitCode`) are captured. Governance: mutation only while disabled (Admin+Operator), once enabled Admin-only; no `secret` input type. Shared facts layer `NodePilot.Core.Activities.CustomActivityType`, frontend mirror `lib/customActivities.ts`; `activityCatalog.generated.ts` stays **untouched**. Full docs: `docs/custom-activities.md`.

## Alerting (notification rules)

Custom rules (`Kind=Custom`, execution events, filter AST = the same `ConditionEvaluator` as edge conditions) and system policies (`Kind=System`, ADR 0008, `ISystemAlertSource` catalog), delivered via SMTP / webhook + HMAC; idle until a rule exists. **Delivery is at-least-once:** the `NotificationDispatcher` persists the attempt before every I/O, a crash afterwards can deliver again — **webhook receivers deduplicate on `EventKey`.** All mutations + test fire Admin-only, new rules are created disabled. Full docs: `docs/alerting.md`.

## Architecture conventions

- **New activity:** class in `Engine/Activities/`, implement `IActivityExecutor` — auto-discovery via `AddNodePilotActivities()`, no DI wiring. Mandatory part of the same change: the UI side (`src/nodepilot-ui/CLAUDE.md`) **and** an entry in `src/NodePilot.Core/Activities/Embedded/activity-config-reference.json`, which feeds the AI prompt catalog and the MCP config tools. Guard tests: table under *Build & test*.
- **New API controller:** in `Api/Controllers/`, DTOs in `Api/Dtos/`; needs a CLI command **and** an MCP tool (see *Clients*).
- **Frontend:** pages, nodes, i18n, state and design tokens: `src/nodepilot-ui/CLAUDE.md`.
- **Models/interfaces:** always in `NodePilot.Core`
- **Doc sync:** feature changes keep all documentation surfaces in sync — README, `docs/*.md`, `docs/testing/E2ETests.md` + `e2e/README.md` and the documentation website `src/nodepilot-docs-ui/content/` (its own curated corpus, not a render of `docs/`). **Bilingual:** every page needs `content/de/…` **and** `content/en/…` plus a title entry in `src/i18n/locales/{de,en}.json`; cross-references **without** a language prefix. The AI knowledge corpus deliberately pulls **only** `content/en/`. The docs `index.html` must contain **no inline `<script>`** (CSP `script-src 'self'`, guard: `document-head.test.ts`).

## Workflow JSON format

```json
{
  "nodes": [{
    "id": "step-123", "type": "activity",
    "position": { "x": 100, "y": 200 },
    "data": {
      "label": "Check Disk", "activityType": "runScript",
      "targetMachineId": "guid", "credentialId": null,
      "outputVariable": "diskCheck",
      "config": { "script": "Get-PSDrive C", "timeoutSeconds": 60 }
    }
  }],
  "edges": [{
    "id": "e1", "source": "step-123", "target": "step-456",
    "type": "labeled",
    "data": {
      "label": "On Success", "condition": "step-123.success", "disabled": false,
      "controlPoints": { "cp1x": 240, "cp1y": 200, "cp2x": 360, "cp2y": 200 }
    }
  }]
}
```

`data.controlPoints` overrides auto-routing. If it is missing → the existing routing applies. Implementation details: `docs/workflow-designer-features.md`.

Layout style guide for workflow JSONs: read `docs/workflow-styleguide.md` **first**. Reference example: `scripts/test-master-all-activities.json`.

## Data bus / variable resolution

- `{{varName.output}}` — stdout
- `{{varName.error}}` — stderr
- `{{varName.success}}` — step success (`"true"` / `"false"`)
- `{{varName.param.xxx}}` — output parameter
- `{{globals.NAME}}` — global variable
- `{{manual.NAME}}` — the run's trigger input (the same keys are also available as `param.*` of the trigger node). Declared `manualTrigger` parameters are seeded with their `default` at run start when the caller omits them. A declared parameter **without** a default stays absent, and the reference fails.
- No `outputVariable` → the step ID is used: `{{step-123.output}}`

**A published value has exactly one owner (SCOrch model).** `{{activity.param.name}}` is binding and resolves for **every** descendant; the resolver binds the unqualified short name (`$name` in `runScript`) only when there is **exactly one** publisher on the ancestor path; with two, **nothing** is bound. Linter code `dup-published-param`, reported only for self-chosen names (`WorkflowDataBusAnalyzer.AuthoredParameters` vs. `TypeDerivedParameters`). Details: `docs/workflow-designer-features.md`.

**Contract guarantee:** three patterns in `VariableResolver` — `GlobalsPattern`, `ManualPattern` and `StepPattern` with exactly four tails (`output`, `error`, `success`, `param.X`). Other tails stay literal; unresolved → granular diagnostics per namespace (StepRunner T-7.1). The globals pattern also applies to `runScript`/custom activities, because a non-existent global is never legitimate script text. If **loading** the globals fails, a run that references globals ends as `Failed` before the first step. **A new namespace needs its own pattern** — a freely chosen tail can in principle never be matched by `StepPattern`.

**Visibility scope (ancestors only):** a step sees **only** the results of its graph ancestors (`AncestorIndex` + `AncestorScopedResults`). A reference to a node in a **parallel branch** never resolves — not even if that branch happens to have finished already. **An ancestor without a result stays unresolvable**; for `junction`/waitAny and skipped nodes that is correct.

**The out-of-scope gate also applies to `runScript`/custom activities.** Both are exempt from the general T-7.1 check (a leftover `{{...}}` can be legitimate script text) — but **not** from the cross-branch case. What counts is **graph membership**, not whether the node already has a result; otherwise the gate becomes a race. Typos/unknown steps stay tolerated.

**Structured output:** `runScript` captures the variables the script **assigns itself** as `param.*`. `$hostName = ...` → `{{step.param.hostName}}`. **Not** included: passed-through upstream parameters and PowerShell automatic/preference variables (list in `NodePilot.Core.Activities.PowerShellReservedVariables`). Implemented with two nested scopes in the wrapper: injection outside, user script inside.

**RunScript auto-quoting:** `{{step.output}}` is inserted as a single-quoted string. In the script write `$x = {{step.output}}`, NOT `$x = '{{step.output}}'`.

**RunScript success (error-based, local and remote):** a step fails on **every** PowerShell error record (`!HadErrors`; the 5.1 process bootstrap mirrors this through the `ERROR` marker); native stderr ends the script in 5.1. An `exit N` does **not** turn the step red; opt-in `config.successExitCodes` makes non-zero codes fail again. The exit code is available as `{{step.param.exitCode}}` and means the last native command of **this** script — the wrapper resets `$LASTEXITCODE` and `$Error` beforehand, because both would otherwise survive from another run in the long-lived runspace pool. **Engine asymmetry:** a script's own `exit N` is visible only on the process/isolated path (the runspace cannot observe `exit` → `0`). A **parse error** is red on every engine: the process engines treat a missing `###NODEPILOT_START###` marker as "the script never ran". Gating in `RunScriptActivity`.

## Edge conditions

- `stepId.success` / `stepId.failed` — shortcut
- `null` / empty — always
- `disabled: true` — skipped; this does **not** make the target node a root, and if all incoming edges are disabled → `Skipped`
- `conditionExpression` — type `comparison` (==, !=, <, >, <=, >=, contains, startsWith, endsWith, matches, isEmpty, isNotEmpty, isTrue, isFalse), `group` (AND/OR), `not`. Operands: `variable` or `literal`.

**Conditions are fail-closed (ADR 0015):** save/publish/import reject an unusable condition with `400 invalid-edge-condition` (`EdgeConditionValidator` in Core); at runtime the scheduler aborts the run with a reference to the edge instead of silently skipping it. A variable operand **without a value** is undecidable and never satisfies the condition, not even through `not`. Alerting filters (`source: event`) read missing fields as empty. Details: `docs/adr/0015-fail-closed-edge-conditions.md`.

## Sub-workflows & contract

`startWorkflow` calls any enabled workflow (fresh DI scope), regardless of trigger type; `parameters` land as `manual.*` in the child run; with `waitForCompletion: true` (default) the parent blocks and mirrors the child's `returnData` as `param.*`. **Max call depth: 10** — also applies to `forEach`. `GET /{id}/contract` derives inputs/outputs; by-name lookup (API, engine, trigger/webhook): exact case wins, otherwise case-insensitive, ambiguous → 409 or a step error (`WorkflowNameResolver`). Details: `docs/claude-reference.md` § Triggers and child workflows.

## Triggers

| Trigger | Backing |
|---|---|
| `scheduleTrigger` | Quartz cron |
| `fileWatcherTrigger` | FileSystemWatcher |
| `databaseTrigger` | Timer + SELECT polling |
| `eventLogTrigger` | EventLog.EntryWritten |
| `webhookTrigger` | HTTP `/api/webhooks/{name}/{path}` |
| `manualTrigger` | UI / API |

`TriggerOrchestrator` scans every 5 s. Trigger data lands as `manual.*` variables in the run + as `param.*` of the trigger node — **no** `trigger.*` namespace. Key names, config contract, liveness and delivery: `docs/claude-reference.md` § Triggers and child workflows. Invariants:

- **A trigger key lives once in `Core/Triggers/`** and is read by the node executor (`Engine/Triggers/`) and the background source (`Scheduler/Sources/`). New key = settings class + `activity-config-reference.json` + designer field, otherwise `TriggerContractParityTests` breaks. `databaseTrigger` fires on a **sentinel change**, not per row.
- **No catch-up after restart or failover:** the durable cursor deduplicates, it does not backfill — signals from a downtime window are discarded (`nodepilot.scheduler.triggers.fires_skipped`).
- `ITriggerSource.Health` is a **pure in-memory read**; `unhealthy` → the source is evicted and rebuilt with backoff; a `FileSystemWatcher` cannot be re-armed in place. Buffer overflow is deliberately **not** a fault.
- **webhookTrigger:** `signatureMode` = `header` (default) or `nodepilot-hmac-v2`; **legacy `hmac` (body-only) is rejected.** `fieldMappings` extracts body fields via JSONPath as `manual.*`.

## WorkflowEngine — execution model

- **Event-driven:** queue + `inFlight` dict. Roots = **trigger nodes only** (ADR 0006): with no active trigger, 0 roots → execution `Failed`, **no** `inDegree==0` fallback. Activities without an incoming edge and nodes with `data.disabled: true` → `Skipped`, as is downstream without other sources. An empty workflow runs through with 0 steps (`Succeeded`).
- **Explicit fan-in (ADR 0013):** only a `junction` may have multiple incoming edges; the designer and the SCOrch import insert a `waitAll` junction where needed, structural validation protects save/publish/API.
- **Cancellation:** `_runningExecutions` dict (Guid → CTS). **Per-step DI scope:** its own scope per step → scope-local `DbContext`.
- **Startup reconciler (ADR 0014):** `Running`/`Paused` and `Pending` without a dispatch intent → `Cancelled`; `Pending` with a durable outbox intent is leased again.
- **No step outlives its execution:** every terminal execution write then sets all still `Running`/`Paused` steps to `Cancelled` (`ExecutionStateLifecycle.CancelOrphanedStepsAsync`). Otherwise a step would stay `Running` forever — visible as an endless spinner and as a permanently inflated "active runs" badge, because both read only `StepExecution.Status`.
- **Step debugger:** `POST /execute` with `debug: true` → breakpoints, SignalR `StepPaused`, resume via `POST /executions/{id}/resume`.

## Build & test

Standard invocations (`dotnet build|test`, in `src/nodepilot-ui` the `package.json` scripts). The backend uses Central Package Management (`Directory.Packages.props`).

**Conventions:** tests are mandatory — every relevant code change brings matching test code in the same change. Naming `MethodName_Scenario_ExpectedResult`; remote layer (WinRM) ALWAYS mocked; DB tests SQLite in-memory. Never weaken assertions, never skip or delete tests to make them green; no sleeps against races. Backend coverage gate line >= 85 % / branch >= 70 %, **enforced in `.github/workflows/ci.yml`, the only authoritative figure** (ratchet — only raise, never lower); frontend see `vitest.config.ts`. Measurement procedure, assembly filters and the `[ExcludeFromCodeCoverage]` rule: `docs/claude-reference.md` § Further references.

### Test scope per change

**Writing tests ≠ running all tests.** The duty above applies unchanged to *writing*; locally you *run* only what the change affects. The full suite is measurably disproportionate (6,597 backend test cases, 260 Vitest files, 81 E2E specs — `DocumentationCountsTests` pins the two frontend figures to the file list, the backend figure stays a hand measurement) and gives no new signal locally: the safety net is `ci.yml`, which runs on **every PR and every push to main** (coverage gate and E2E included).

**The nightly is not a reliable second floor.** It runs as a Windows task at 22:00 against the checked-out tree and is missed whenever the machine is off at that time. Anyone relying on it checks the date in `C:\temp\nodepilot-nightly\latest.md` first.

**Markdown exception:** a PR that touches **only** `*.md` or `docs/images/**` skips frontend, desktop, database and E2E (`changes` job). **Backend and docs-ui always run**, because Markdown is an input for them (`DocumentationCountsTests`, `SettingsSchemaDocumentationTests`, `MonitoringDeploymentSecurityTests`, language parity guard). Pushes to `main` **always** run in full; every error path of the detection ends in "run everything".

Default for feature work:

```powershell
# Backend — one project, one class/namespace
dotnet test tests/NodePilot.Engine.Tests --filter "FullyQualifiedName~WorkflowCallGraphBuilder"

# Frontend — a single file or directory
cd src\nodepilot-ui; npx vitest run src/__tests__/lib/opsTimeline.test.ts

# E2E — one spec, against the running dev server (no build)
cd src\nodepilot-ui; npx playwright test e2e/operations.spec.ts --config=playwright.dev.config.ts
```

**Escalate only for a reason, never prophylactically:**

1. **Scoped** (default) — filter on the changed class/component.
2. **Project suite** (`dotnet test tests/NodePilot.Api.Tests`) — when the change cuts across *within* the project: shared base class, DI wiring, `Program.cs`.
3. **Full suite** — only on (a) explicit request by the user, (b) release cut/direct push to main, (c) an inherently global change (`Directory.Packages.props`, dependency bump, project-wide refactoring).

**Never measure coverage locally** — `--collect:"XPlat Code Coverage"` or `npm run test:coverage` are CI jobs, not a local step.

**Reporting:** not "all tests green", but **which** ran (project + filter + count). What did not run is named as "covered by CI", not left unsaid.

### Guard tests: on trigger, not on suspicion

Parity/drift tests enforce consistency between files that sit far apart and are spread over six test projects. Therefore: **touched a trigger surface → run exactly that test**, instead of everything just to be safe.

| Touched | Guard test | Project |
|---|---|---|
| Activity + `activity-config-reference.json` + frontend catalog mirror | `ActivityCatalogTests`, `ActivityConfigReferenceTests`, `ActivityCatalogFrontendSyncTests` | Engine.Tests |
| `KnownProgramLaunchers` / `lib/knownProgramLaunchers.ts` | `KnownProgramLaunchersFrontendSyncTests` | Engine.Tests |
| New EF migration / designer post-processing | `MigrationDriftTests` | Data.Tests |
| `*.csproj` references / dep graph | `DependencyDirectionTests` | Api.Tests |
| New audit code | `AuditActionsCatalogTests` | Api.Tests |
| API DTO (+ CLI mirror) | `ApiDtoParityTests` | Cli.Tests |
| Trigger config key | `TriggerContractParityTests` | Engine.Tests |
| `SettingsSchema.cs` / admin settings UI | `AdminSettingsFrontendSyncTests`, `SettingsSchemaDocumentationTests` | Api.Tests |
| AI prompt catalog | `PromptCatalogDriftTest` | Ai.Tests |
| Alerting catalog / system policies | `AlertingCatalogFrontendSyncTests`, `SystemAlertCatalogTests` | Engine.Tests |
| Workflow analyzer (`WorkflowAnalyzer`/`WorkflowDataBusAnalyzer` in Core — MCP **and** AI chat) | `WorkflowAnalyzerFrontendParityTests` | Engine.Tests |
| Template grammar / variable resolution | `TemplateGrammarParityTests` | Engine.Tests |
| Metrics dashboard catalog | `MetricsDashboardCatalogTests` | Api.Tests |
| Count-bearing doc claim (MCP tool count, activity types, skins), a new Vitest/E2E file **or the size of this file** | `DocumentationCountsTests` | Mcp.Tests |
| `RequestSizeLimit` on `/import`/`/import-scorch` or the upload gates in `WorkflowsPage.tsx` | `ImportSizeLimitFrontendSyncTests` | Api.Tests |
| LLM profile defaults (`LlmProfileOptions`, `LlmProfileSettingsDto`, `SettingsSections.cs`, `IntegrationsSection.tsx`) | `LlmProfileDefaultsTests` | Api.Tests |
| `vite.config.ts` proxy / dev ports | `AppSettingsHygieneTests` | Api.Tests |
| OpenTelemetry block in `Directory.Packages.props` | `OpenTelemetryVersionParityTests` | Api.Tests |
| New test project in `NodePilot.slnx` / `coverage.runsettings` | `TestRunSettingsTests` | Api.Tests |
| Browser demo (`demo/`), hub/docs/auth seam, root-absolute URL literals in `src/` | `src/__tests__/demo/*` + `e2e-demo/*.spec.ts` (seam, routing, all ten tours) | nodepilot-ui |
| `index.css` / `designer-atelier.css` designer-light tokens | `designerLightParity.test.ts` | nodepilot-ui |
| Font tokens / Monaco stack | `fontTokens.test.ts` | nodepilot-ui |

**E2E (Playwright):** hermetic specs in `src/nodepilot-ui/e2e/`, all APIs mocked — conventions in `src/nodepilot-ui/CLAUDE.md` + `src/nodepilot-ui/e2e/README.md`. **Desktop shell:** its own vitest suite and CI job `desktop`, see `src/nodepilot-desktop/README.md`. **Nightly:** `scripts/nightly-tests.ps1` (Windows task, 22:00, report to `C:\temp\nodepilot-nightly\`); change the time with `scripts/register-nightly-task.ps1 -Time HH:mm`.

## Clients (`np` CLI + `nodepilot-mcp`)

Both are pure HTTP clients against the REST API — **no** backend path of their own; the MCP server adds in-proc analysis against `NodePilot.Core` (116 tools, 3 resources, stdio). Packaging, sign-in paths, shared client infrastructure and tool catalog: `src/NodePilot.Cli/CLAUDE.md`, `src/NodePilot.Mcp/CLAUDE.md`, `docs/mcp-server.md`.

**Every new API endpoint needs both clients** (guard: `EndpointClientCoverageTests`).

## Authorization

| Endpoint | Admin | Operator | Viewer |
|---|---|---|---|
| `GET /api/{workflows,executions,machines}` | ✓ | ✓ | ✓ |
| `POST /api/workflows`, `PUT`, `POST /{id}/duplicate\|execute` | ✓ | ✓ | ✗ |
| `POST /api/machines`, `PUT` | ✓ | ✓ | ✗ |
| `GET\|POST\|PUT /api/credentials` | ✓ | ✓ | ✗ |
| `POST /api/executions/{id}/cancel` | ✓ | ✓ | ✗ |
| `DELETE /{workflows,machines,credentials}/{id}` | ✓ | ✗ | ✗ |
| `DELETE /api/shared-workflow-folders/{id}` | folder `Edit`, empty only | folder `Edit`, empty only | ✗ |
| `DELETE /api/shared-workflow-folders/{id}?recursive=true` | ✓ | ✗ | ✗ |
| `GET /api/alerting/rules`, `POST /preview-filter` | ✓ | ✓ | ✗ |
| `POST/PUT/DELETE /api/alerting/rules`, `POST /{id}/enable\|disable\|test-fire` | ✓ | ✗ | ✗ |
| `POST /api/trigger/{name}` | API key via `X-Api-Key` header |

**Folder deletion has two security boundaries:** an empty folder remains a folder `Edit` mutation. `?recursive=true` also removes workflows and their execution history and is therefore globally Admin-only, like `DELETE /api/workflows/{id}`. The folder capabilities return `canDelete` separately from `canEdit` for this; **the UI must not derive the recursive delete from `canEdit`.**

**The global-variable folder tree has the same mechanics but stays Admin-only** — there is no per-folder RBAC there to relax.

## Security

- **Session:** JWT cookie + CSRF token, absolute lifetime **8h** (`Authentication:SessionAbsoluteLifetimeHours`; refresh does **not** extend it), `jti` revocation. Auth paths (local BCrypt with production default `LocalLoginMode=BreakGlassOnly`, LDAP, Windows Negotiate, OIDC + SCIM): `docs/ldap-windows-sso.md`; details `docs/threat-model.md`.
- **External trigger:** `X-Api-Key` against SHA-256 hashes under `ExternalTrigger:Keys:<id>`; every entry has a GUID-only `AllowedWorkflowIds` list. The `Keys` map comes **atomically** from the highest-priority provider (`Keys: {}` revokes all lower ones); scope arrays likewise (`[]` = deny-all). The workflow additionally needs an active `manualTrigger`. Legacy `ApiKey` is inert without its own list.
- **Idempotency:** `POST /api/trigger/{name}` accepts `Idempotency-Key`, replay only within the same key principal and workflow; `Pending` + reservation + dispatch intent are created in **one** transaction and survive failover.
- **Rate limiting** (per IP, sliding window): login 50/min, refresh 20/min, webhook 60/min, trigger 30/min, ai-generate 20/min, audit 60/min, alerting-heavy 20/min, backup 10/min.
- **Output redaction:** `OutputRedactor` masks secrets. Always active. Custom patterns via `Logging:Redaction:Patterns`; an invalid pattern stops the boot.
- **Localhost bypass / operator trust:** without credentials, execution runs in-process under the NodePilot service identity. `Operator` is deliberately a trusted automation author and may publish/run such workflow code. Folder RBAC is not a code sandbox. **Product feature — do not add a require-target guard.**
- **Security headers (non-dev):** HSTS, CSP, X-Frame-Options=DENY, nosniff, Referrer-Policy. **SignalR auth** via the httpOnly `np_auth` cookie on the WebSocket upgrade (only `/hubs/`); no `?access_token=` query string.
- **REST API proxy:** `RestApi:Proxy:Enabled` (default `false`). Per-step override via `proxyMode`.

**Hardening flags** — table with defaults and effect: `docs/threat-model.md`. Two subtleties: `Webhook:RequireSecret` is **default `true`** (a missing key reads as `true`), and `WaitForCondition:AllowedHosts` is a **separate** list for the `portOpen`/`httpOk` probes, deliberately apart from `RestApi:AllowedHosts` and the **sole** authority for both probe types.

## Admin settings hot reload

Admin settings saves persist atomically to `appsettings.runtime.json` (`reloadOnChange: true`). Per section, `SettingsSchema.cs` carries an `IsHotReloadable` flag; only `false` sections set the restart marker. 13 sections are hot-reloadable, 10 require a restart; the hard core (JWT, DB, Kestrel, cluster/HA, `Remote:Provider`) stays boot-fixed. Matrix: `docs/claude-reference.md` § Settings, statistics and operations.

**Service restart from the UI** (`POST /api/admin/settings/restart-service`): self-exit with code 75 before `SERVICE_STOPPED`; the restart is done by the installers' SCM recovery; available only when every recovery action is a restart — `docs/claude-reference.md` § Settings, statistics and operations.

**Consumer rule:** read hot-reloadable values via `IOptionsMonitor<T>.CurrentValue` or raw `IConfiguration` per use/pass — **never** an `IOptions<T>.Value` snapshot.

**Sizing:** `Performance:ManualTuning` (default **`false`**) decides whether runspace, step, threading and dispatch worker counts are derived from the detected CPU+RAM or taken verbatim from config; restart-required. **`Engine:MaxConcurrentExecutions:*` is excluded** (a safety cap, not tuning). Details: `docs/performance-improvements.md`.

## AuditLog

Inject `IAuditWriter`, `await _audit.LogAsync(AuditActions.VerbNoun, "Resource", resourceId, detailsJson, ct)` **after** `SaveChanges`. A write failure must never abort the normal mutation. Exception: DB admin write SQL runs fail-closed — without a previously persisted `DBADMIN_SQL_WRITE_ATTEMPTED` entry the SQL is not executed. Never put passwords/secrets in the details. Codes follow `VERB_NOUN` and are registered **centrally** in `NodePilot.Core.Audit.AuditActions` — never a raw string literal at the call site (guard: `AuditActionsCatalogTests`). Code overview: `src/NodePilot.Core/Audit/AuditActions.cs`.

## AI features

Opt-in (`Llm:Enabled=false` default), OpenAI-compatible endpoint, rate limit 20/min/IP. Full docs: `docs/ai-features.md`.

- `POST /api/ai/generate-script` (Admin/Op, SSE streaming) + `POST /api/ai/generate-workflow` (Admin/Op, JSON).
- `POST /api/ai/generate-agent-team` (Admin/Op, JSON) — team draft from a description: names are resolved server-side against the inventory, an unresolved machine/credential blocks taking it over, the preview is mandatory, deliberately no CLI/MCP (`docs/ai-agents.md`).
- `POST /api/ai/chat` (all roles, SSE) — workflow assistant; proposals only Admin/Op, merge by node ID onto the unredacted original. **Secrets are redacted before every LLM call** (`WorkflowSecretRedactor`).
- `POST /api/ai/knowledge/ask` (SSE) — global knowledge assistant in `/ai-chat`, four admin-toggleable sources (section `AiKnowledge`). **DB / text2sql global Admin only** (central guard via `ISqlKnowledgeReader`); folder grants never elevate to raw SQL.
- `llmQuery` activity: engine-local, per-node overrides, gated by `Llm:Enabled`; the only BaseUrl validation point is `LlmEndpointGuard`.

**Profiles:** `Llm:Profiles:<id>` is an object keyed by immutable id, not an array; `Llm:ActiveProfileId` selects the active one — **no "take the first" fallback** (503 `LLM_NO_ACTIVE_PROFILE`). No scoped `ILlmClient` registration; consumers take `ILlmClientFactory`. Wire dialects, timeouts, proxy and hardening: `docs/ai-features.md`.

## Workflow import/export

`GET /{id}/export` / `GET /export` / `POST /import`, envelope `nodepilot-workflow-export/v1`. Import always creates new entries **disabled**; target folder via `?folderId=`, RBAC = Edit on it. **Secrets are redacted** (`***`) — a sharing artifact, not DR. SCOrch import (`POST /import-scorch`): `docs/claude-reference.md` § SCOrch import.

## System configuration backup (ADR 0001)

Separate from workflow export: a portable, passphrase-encrypted configuration backup (`.npbackup`, envelope `nodepilot-system-backup/v4`), Admin-only, fail-closed on incomplete export or restore; **no** execution history and **no complete DR**. UI `/backup`, CLI `np backup`. Details: `docs/adr/0001-system-configuration-backup-restore.md`.

## Production deployment

Rollout via the `deploy/` scripts (approval rule under *Running the project*). Docs: `deploy/README.md`; architecture, config keys and pitfalls: `docs/deployment-guide.md`. **Desktop app** (Electron, `deploy/desktop/`, `Deployment:Mode=Desktop`): relaxes **only** loopback DB TLS + Kestrel `ListenLocalhost`, the rest stays production-hardened — `deploy/desktop/README.md`.

**Mandatory before every release:** the lab matrix `scripts/release-lab/` against the signed artifacts from `out\` — server setup in all identity/DB combinations, fresh and as an update, each with uninstall, plus the desktop setup on the lab client. Both setups test direct upgrades from each of the last five published stable versions to the candidate (20 server and five desktop upgrade cases; no skipped mandatory paths). Then the test suite `scripts/test-suite/` on the hardened lab instance with `fail=0`. No tag without a green run of all three (`RELEASING.md` step 6). The computer-use run `scripts/release-lab/computer-use/` is experimental and not a gate.
