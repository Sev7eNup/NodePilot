# Custom Activities ("Custom Nodes")

User-authored, reusable workflow activities backed by a parameterized PowerShell template. They
appear in a dedicated **Custom Nodes** section in the designer palette (below the built-in
categories), have typed input fields and declared outputs (`{{node.param.X}}`), and are
import-/exportable. A custom activity is, conceptually, a **reusable `runScript` preset** — it reuses
the entire existing PowerShell execution path (engine selection, process isolation, marker-based
structured output, secret redaction, exit-code semantics, local/remote routing). There is no second
script engine and no new backend execution path.

## Concepts

- **Definition** (`CustomActivityDefinition`): the reusable template — name, immutable `Key`, icon,
  optional accent colour, PowerShell `ScriptTemplate`, engine, remote/isolation flags, default
  timeout, success exit codes, declared input/output parameters.
- **Activity type**: a node referencing a definition carries `activityType = "custom:<Key>"`
  end-to-end. The definition is linked via `config.__customDefinitionId` (authoritative) plus
  `config.__customKey` (drift cross-check).
- **Executor**: a single `CustomActivityExecutor` serves every custom activity. `ActivityRegistry`
  resolves any `custom:*` type to it (registered under the reserved sentinel type `custom`).

## Lifecycle & governance

- A definition is **created disabled (Draft)**. Admin **and** Operator may create/edit/delete it
  **while it is disabled**.
- **Enable/Disable is Admin-only.** Once a definition is enabled (live), **every mutation is
  Admin-only** — this closes the latest-wins instant-publish gap (an Operator editing a live,
  latest-wins definition would otherwise globally re-publish it without review).
- **Latest-wins**: editing a live definition immediately affects all workflows that use it.
  Version history + rollback are provided (`CustomActivityDefinitionVersion`, mirrors
  `WorkflowVersion`).
- **Reproducibility**: every execution persists `StepExecution.CustomActivityKey/Version/Hash`, so a
  past run remains attributable to the exact script+options that ran, even after later edits or a
  rollback.
- **Optimistic concurrency**: updates carry the `ConcurrencyToken` the caller read; a stale token →
  `409 Conflict`.
- **Delete is a soft tombstone** (`IsDeleted`), retaining the script + versions for audit /
  reproducibility while removing it from the catalog/palette. The `Key` becomes free again.

## Inputs, outputs & the capture allow-list

- Declared **inputs** (`string | number | boolean | select | multiline`) are resolved against the
  databus (`{{...}}` / `{{globals.X}}`) and injected as `$name` PowerShell variables. Author the
  script using `$ServiceName`, not `{{ServiceName}}`.
- Declared **outputs** are surfaced downstream as `{{node.param.<name>}}`. The PowerShell wrapper
  runs in a **capture allow-list** mode for custom activities: it captures **only** the declared
  output variables (plus the always-present `exitCode`). Injected inputs and undeclared helper
  locals therefore never leak as `param.*`.
- Parameter names must match `[A-Za-z0-9_]+` (PowerShell variable grammar). Input and output names
  must be **disjoint**; `exitCode` is reserved.

## Security

- The threat surface equals `runScript`, which Operators can already author — this is a
  packaging/reuse layer, not a new capability.
- **No `secret` input type.** Secrets must come via `{{globals.X}}` or credentials; free-form secret
  fields cannot be reliably redacted out of workflow JSON / export / backup / AI context.
- `ScriptTemplate` is RCE-linted (`Invoke-Expression`, `& $var`, `$ExecutionContext.InvokeScript`)
  on create/update/import/enable via `WorkflowScriptLinter.LintScript`; warnings are surfaced
  (non-blocking, mirrors the workflow linter).
- Local execution runs in the API process unless `Isolated` is set (Windows Job Object). Set a
  timeout; consider enabling isolation for untrusted local scripts.
- **Imported** definitions land disabled and require an Admin to review the script and enable them.

## API

`api/custom-activities` (see CLAUDE.md → API Endpoints):

| Method | Route | Role |
|---|---|---|
| GET | `/api/custom-activities[?includeDisabled=true]` | all (drafts: Admin/Op) |
| GET | `/api/custom-activities/{id}` · `/{id}/versions` | Admin/Op |
| POST | `/api/custom-activities` | Admin/Op (creates disabled) |
| PUT/DELETE | `/api/custom-activities/{id}` | Admin/Op; Admin-only if enabled |
| POST | `/api/custom-activities/{id}/rollback/{version}` | Admin/Op; Admin-only if enabled |
| POST | `/api/custom-activities/{id}/enable` · `/disable` | Admin |
| GET/POST | `/api/custom-activities/export` · `/import` | Admin/Op (import → disabled) |

Audit codes: `CUSTOM_ACTIVITY_CREATED|UPDATED|DELETED|ENABLED|DISABLED|IMPORTED|ROLLED_BACK`.

## Frontend

- Management page `/custom-activities` (palette: "Custom Nodes" nav entry): CRUD, CodeMirror
  PowerShell script editor (syntax-highlighted, theme-aware), input/output schema editors,
  **icon picker** + accent colour with live preview, import/export, enable/disable,
  Live/Draft badges.
- **Import** uses a file picker (`.npca`/`.json` envelope, `nodepilot-customactivity-export/v1`);
  imported nodes land disabled for Admin review. Export downloads the full set as one envelope.
- **Version history**: a per-row "Version history" action opens a dialog listing the stored
  snapshots (version, created at/by, change note) with a per-version **rollback** button
  (confirm + toast; hidden when the caller may not mutate — i.e. Operator on an enabled
  definition). Rollback snapshots the current state first, so it always moves forward.
- Runtime catalog: fetched from `GET /api/custom-activities`, mirrored into a module-level cache +
  Zustand store (`lib/customActivities.ts`, hydrated by `useCustomActivityCatalog`). The static
  `activityCatalog.generated.ts` and its parity test are **untouched** — custom activities are a
  runtime catalog, never the static one.
- Consumers wired via `isCustomActivityType` + runtime facts: palette (`activityCategories.ts`),
  labels/icons (`shared.tsx`, `NodeLibrary.tsx`), node visuals (`nodes/activityConfig.ts`),
  config form (`DynamicActivityConfig`), output variables (`upstreamVariables.ts`), and
  PropertiesPanel remote/timeout gating.

## System-configuration backup (ADR 0001)

Custom activities are part of the `.npbackup` DR snapshot (`CustomActivityBackupPart`, section
`customActivities`). The live definition of each (including disabled drafts) is exported — script
template in cleartext, like a workflow's runScript `script` field; version-history snapshots are
excluded. Restore is full-fidelity (the enabled state is preserved, unlike the `.npca` import which
forces disabled), conflict policies skip/overwrite apply by `Key` (rename is unsupported — a key is
embedded in workflow references — and falls back to skip with a warning). A workflow node's
`config.__customDefinitionId` is **remapped** to the restored definition id, so overwrite-merge
restores keep references intact (a no-op for a clean restore, which preserves source ids).

## AI awareness

The chat assistant's system prompt includes facts for the enabled custom activities the current
workflow references (name, remote flag, inputs, outputs) — built by `BuildActivityMetadata` from the
`ICustomActivityDefinitionStore` — so it can wire their inputs/outputs instead of treating them as
unknown types.

## Known follow-ups (not in v1)

- **`np` CLI**: no `custom-activity` command group yet; use the REST API or MCP.
- **AI `list_activity_types` discovery**: the read-only chat tool lists only built-ins, so the
  assistant can't yet *discover* custom activities it could add (it can use ones already on the
  canvas — see AI awareness above). Wiring the scoped store into the singleton tool registry is the
  remaining step.
- **Composite / sub-workflow custom nodes**: deliberately out of scope (v1 is PowerShell-only).
