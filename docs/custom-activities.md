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
  optional accent colour, PowerShell `ScriptTemplate`, engine (`auto`/`powershell` = Windows
  PowerShell 5.1, `pwsh`, `runspace` = in-process pool, same meaning as on `runScript`;
  `runspace` cannot be combined with isolation), remote/isolation flags, default timeout, success
  exit codes, declared input/output parameters.
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
  locals therefore never leak as `param.*`. Plain `runScript` reaches the same guarantee by a
  different route: it publishes whatever the script assigns, and the wrapper's scope split keeps
  read-only upstream parameters out.
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
- **Workflow import** (`POST /api/workflows/import`) remaps `config.__customDefinitionId` to the
  destination definition with the same key (`CustomActivityReferences.RemapByKey`). Import the
  `.npca` first; for a key that does not exist on the destination the import reports a message
  and leaves the node unchanged.
- Every designer path that creates a node (palette click, drag onto the canvas, quick-connect,
  insert on an edge) writes `__customDefinitionId`, `__customKey` and the declared input defaults
  through `newActivityConfig` (`lib/customActivities.ts`).
- **Version history**: a per-row "Version history" action opens a dialog listing the stored
  snapshots (version, created at/by, change note) with a per-version **rollback** button
  (confirm + toast; hidden when the caller may not mutate — i.e. Operator on an enabled
  definition). Rollback snapshots the current state first, so it always moves forward.
- Runtime catalog: fetched from `GET /api/custom-activities`, mirrored into a module-level cache +
  Zustand store (`lib/customActivities.ts`, hydrated by `useCustomActivityCatalog`). The static
  `activityCatalog.generated.ts` and its parity test are **untouched** — custom activities are a
  runtime catalog, never the static one.
- Consumers wired via `isCustomActivityType` + runtime facts: palette (`activityCategories.ts`),
  labels (`shared.tsx`), node visuals (`nodes/activityConfig.ts` — `getActivityVisual` is also the
  single source for the palette/picker glyph in `NodeLibrary.tsx`, which therefore carries no icon
  or colour table of its own), config form (`DynamicActivityConfig`), output variables
  (`upstreamVariables.ts`), and PropertiesPanel remote/timeout gating.

## System-configuration backup (ADR 0001)

Custom activities are part of the `.npbackup` DR snapshot (`CustomActivityBackupPart`, section
`customActivities`). The live definition of each (including disabled drafts) is exported. The
PowerShell script template and the complete input-schema JSON (whose defaults become runtime
values) are encrypted under the backup passphrase inside the fully encrypted v4 payload. Earlier
plaintext-envelope schemas are rejected by the current reader.
Version-history snapshots are excluded. Restore is full-fidelity (the enabled state is preserved, unlike the `.npca` import which
forces disabled), conflict policies skip/overwrite apply by `Key` (rename is unsupported — a key is
embedded in workflow references — and falls back to skip with a warning). A workflow node's
`config.__customDefinitionId` is **remapped** to the restored definition id, so overwrite-merge
restores keep references intact (a no-op for a clean restore, which preserves source ids). Selecting
workflows automatically includes custom-activity definitions; a missing hard reference aborts before
any restore write.

## AI awareness

The chat assistant and workflow generator receive all enabled custom-activity definitions from
`ICustomActivityDefinitionStore`, rendered by `ActivityCatalogPromptRenderer` (name, remote flag,
inputs and outputs). This includes definitions not yet used on the current canvas, so the
assistant can propose new custom nodes and wire their inputs and outputs. Disabled definitions
are excluded.

## Known follow-ups (not in v1)

- **`np` CLI**: no `custom-activity` command group yet; use the REST API or MCP.
- **AI `list_activity_types` tool**: this read-only chat tool still lists only built-ins.
  Adding custom definitions to that tool remains a follow-up; the assistant and workflow
  generator already receive the enabled custom catalog through their system prompts.
- **Composite / sub-workflow custom nodes**: deliberately out of scope (v1 is PowerShell-only).
