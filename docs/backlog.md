# Designer Feature Backlog

Ideas that have been considered, scoped, and consciously deferred. Each entry includes the
estimated effort and the concrete reason the work hasn't shipped yet so the next person can
pick it up without re-doing the discovery.

---

## Rerun-from-Step (Hot Resume)

**Idea:** In the History tab (or canvas right-click), pick a failed step → "Rerun from here".
The engine starts a fresh execution but seeds itself with the **outputs of all upstream steps
from the original run**, so only the chosen step and its descendants actually re-execute. Saves
the cost of re-running the first 46 of 50 steps when you just need to debug step 47.

**Why deferred (2026-04-25):** Three coupled changes are needed and each carries non-trivial
risk:

1. **Engine signature** — `WorkflowEngine.ExecuteAsync` needs a new optional parameter
   `Dictionary<string, ActivityResult>? prefilledResults`. The scheduling loop must seed
   `results` and `completed` with those entries so the scheduler skips them and proceeds
   from the first uncached descendant. Touches the hot path; needs careful tests for the
   waitAny/waitAll/junction cases.
2. **Schema migration** — `StepExecution` currently does **not** persist `OutputParameters`
   (only `Output` and `ErrorOutput` text). Without `OutputParametersJson` (nullable column),
   a rerun cannot recover `{{step.param.hostName}}`-style refs from the original run — they
   would resolve to empty strings, silently breaking downstream logic. Migration must land
   in beiden App-DB-Providern (sqlserver / postgres).
3. **API + UI** — new endpoint `POST /api/executions/{id}/rerun-from/{stepId}` that loads
   the original execution, builds `prefilledResults` (excluding `stepId` and its downstream
   per a BFS on the workflow graph), and calls `ExecuteAsync`. UI: context-menu entry in
   the History step list.

Estimated effort: 1.5–2 days with proper test coverage for the engine changes.

**Files in scope:**
- `src/NodePilot.Engine/WorkflowEngine.cs` (ExecuteAsync signature + scheduler init around line 329)
- `src/NodePilot.Core/Models/StepExecution.cs` (add `OutputParametersJson`)
- `src/NodePilot.Data/Migrations/...` (gemeinsames provider-agnostisches Migration-Set)
- `src/NodePilot.Api/Controllers/ExecutionsController.cs` (new endpoint)
- `src/nodepilot-ui/src/components/designer/ExecutionPanel.tsx` (HistoryRow context menu)

---

## Other ideas not yet picked up

For the full catalog of explored ideas (Multi-Select bulk, command palette, heatmap, etc.) see
the README "Power-User Features" table — those are already shipped. The list below is what's
**still** on the table.

### #4 Group / Subgraph collapsible
The `GroupNode` already exists structurally (Ctrl+G). Missing: a "collapse" affordance that
folds N child nodes into a single placeholder node, restorable on double-click. Useful for
diagrams >50 nodes where logical chunks ("DB-Phase", "Notification-Phase") could collapse.
**Effort:** 2 days (React Flow internal node + edge handling for collapsed-group state).

### #19 Edge-Reroute manual handle — ✅ SHIPPED
Drag a midpoint handle on an edge to manually pull it into a different lane. Helps when
auto-routing creates ugly crossings on dense workflows. **Implemented** via pen-tool-style
draggable cubic-Bézier control handles in `components/designer/edges/EdgeReshapeHandles.tsx`
(backed by `smartEdgePath.ts` + the `data.controlPoints` override documented in CLAUDE.md).

### #15-extension: Inline expression validation — ✅ SHIPPED
As-you-type template validation runs in every `VariableInsertField` and the inline
CodeMirror `CodeField` via `lib/templateValidation.ts` (`validateTemplateExpression`) —
malformed `{{}}`, unsupported shapes and not-in-upstream producers surface as inline
messages below the field. Runtime namespaces (`globals.*`, `manual.*`) and the
`.error/.success` tails of known producers are tolerated, matching the publish-time lint.

### #6: JSONPath-Picker on Output tab
Click on a value in a step's JSON output → tooltip "Use as `{{step.param.hosts[0].name}}`"
with copy-button. Faster than typing JSON paths from scratch.
**Effort:** 1.5 days (recursive JSON tree render with path-tracking per cell).

### Machine-Online Indicator per Node
Each remote-activity node shows a small green/red dot indicating whether its target machine
is currently reachable. Backend polls similar to `POST /machines/{id}/test` with a 5-min cache.
Surfaces ops state inline without leaving the editor.
**Effort:** 1 day.

### Workflow Tags / Labels
DB column on `Workflow` for an array of color-coded tags (`prod`, `qa`, `incident-response`).
Filter dashboard / quick-switcher / executions list by tag. Pills shown next to workflow
names everywhere.
**Effort:** 1 day (migration + UI).

### Drag-and-Drop Variable to Field — ✅ SHIPPED
Variable pills are draggable (`setVariableDragData` in
`components/designer/properties/shared.tsx`) and drop into the Properties-Panel
inputs/textareas — see docs/workflow-designer-features.md §Databus.

### Workflow Templates / Boilerplate Library
"Save as template" on a finished workflow. "New Workflow" gets a Template-Picker step with
curated patterns ("Backup", "Patch-and-Verify", "API-Health-Probe"). Big onboarding win.
**Effort:** 1.5 days (DB table + clone-on-instantiate logic + Template-Picker UI).

### Side-by-Side Workflow Diff
Diff exists today as a text-style overlay. A two-canvas mode showing old + new workflow
side-by-side, with changed nodes color-highlighted, would be much more intuitive for
workflow-shaped diffs than line-based JSON diffing.
**Effort:** 2 days.
