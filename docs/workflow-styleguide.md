# NodePilot Workflow Layout Styleguide

Conventions for hand-laid-out workflow JSON (`nodes`/`edges`) that renders in the GUI without overlaps and with readable edge labels. Derived from the master test workflow [scripts/test-master-all-activities.json](../scripts/test-master-all-activities.json) — the living reference example there covers every activity, all 14 edge operators and all 3 junction modes.

> Note: the UI has a Dagre auto-layout button ([autoLayout.ts](../src/nodepilot-ui/src/lib/autoLayout.ts), `rankdir:LR, ranksep:180, nodesep:80`). For hand-curated demo workflows with many condition labels, Dagre is not enough — the rules below are for those.

---

## 1 — Guiding principles

There is **no prescribed flow direction**. LTR, TTB, radial, two parallel columns, a mix of several sub-topologies — whatever makes the workflow clearest to a human reader is right. Every node exposes all four ports (section 7.1), so an edge can freely pick a side of its source and target instead of being forced into a layout corset.

Three hard rules everything else hangs on:

1. **No overlaps** — not node on node, not edges cutting through nodes, not labels over labels. When in conflict: leave more room. A workflow that is too large on the canvas beats one that is too tight to read.
2. **Edge labels must be readable without zooming** — short, semantic conditions. If a label grows too long, split the logic across two sequential edges, condense it into a sub-pattern phrase, or move the operator spell-out into the following node label.
3. **Flow direction is consistent within a region** — if a section flows LTR, it flows LTR throughout; you do not switch mid-way without a semantic reason. Loop-backs, retry paths and "jumps back to phase X on failure" are legitimate exceptions — those *should* stand out visually.

Every further section is a **convention for the most common case (LTR with a clear main lane)**. If you pick a different topology, translate the spacing figures accordingly (for TTB: swap x↔y; for radial layouts: think in slot angles).

## 1.5 — Compactness (more important than anything else)

**The end result should stay readable at `fit-view` without scrolling.** Hand-laid-out workflows have an implicit size budget — overrun it and you produce a 35 % mini-diagram nobody can read. Rule of thumb per activity count:

| Activities | Target canvas (px) | Layout strategy |
|---|---|---|
| ≤ 15 | ≤ 2500 × 1200 | plain LTR or TTB, 1 row |
| 16–30 | ≤ 3500 × 1800 | snake with 2 rows |
| 31–50 | ≤ 4500 × 2200 | snake with 3 rows |
| > 50 | — | extract sub-workflows instead of growing the canvas |

**The snake pattern** (the most important tool for compact layouts):

1. ROW 1: LTR, left to right
2. Transition at the right edge: `sourceHandle: bottom` → `targetHandle: top`
3. ROW 2: RTL, right to left (`sourceHandle: left` → `targetHandle: right`)
4. Transition at the left edge: `sourceHandle: bottom` → `targetHandle: top`
5. ROW 3: LTR again

Snake transitions belong **at row ends only** — do not bend mid-row (it confuses the reader and looks like a mistake). At most one direction change per row. Branches and fans hang perpendicular to the row direction — a fan branch in an LTR row fans upward/downward, not backwards against the flow.

**Compactness trade-offs:**

- A y-spread of 160 px (instead of 180) is fine with short branch labels; with labels wrapping beyond 40 characters, keep 180+.
- An x-step of 280 (instead of 300) is fine for ordinary 1→1 chains. Fan-out/fan-in still needs 340–400.
- More than 3 snake rows means the workflow is too big for a single definition. Extract sub-workflows via `startWorkflow`.
- Use sticky notes sparingly — one per row or phase transition is enough. Every note steals visual space needed for readable edge labels.

## 2 — Spacing (LTR example figures)

| Situation | x-step | y-lane |
|---|---|---|
| Ordinary chain (1→1) | **300 px** | — |
| Short labels, 1→1 | 280 px minimum | — |
| Fan-out with ≥ 5 condition labels | **340–400 px** | — |
| Fan-in with ≥ 5 branches | **340–400 px** | — |
| 2–3 parallel branches | — | 160 px |
| 4–5 parallel branches | — | **180 px** |
| 6+ parallel branches | — | **180–200 px** |

The fan-out/fan-in rule is the most important one: with 7 edges radiating from a junction to phase E, the midpoint labels cluster without extra spacing and become unreadable. Wider horizontally = more midpoint spread = fewer label collisions.

Centre fan branches **symmetrically around the main lane** when their count is odd. With an even count, accept slight asymmetry but keep the span no larger than necessary — every extra y-pixel costs vertical scroll space.

**Shape bonus for trigger nodes:** triggers render as octagons at 1.55× bounding box (see section 8). Plan **+80–100 px of extra x-headroom** right after a trigger, or the octagon edge overlaps the following node.

## 3 — Node labels

- **Start with the activity prefix**: `"runScript: collect host info"`, `"Junction: waitAll (5)"`, `"Log: no PANIC"`. That way the kind of step is obvious at a glance.
- **Card-mode width 220–280 px**: labels longer than ~40 characters get truncated or wrapped.
- **No operator hints in the node label** when the condition is already on the edge: instead of `"Log: env == production (AND ==, !=)"`, simply `"Log: env is production"`.
- **Keep technical parameters short**: `"Junction: waitNofM (2/3)"` rather than `"Junction: waitNofM (requiredCount=2 of 3)"`.

## 4 — Edge labels

- **Semantic, not structural**: `"env==prod & env!=stg"` rather than `"AND(env=='production', env!='staging')"`.
- **ASCII symbols**: `&` for AND, `OR` for OR, `NOT` for NOT, `!=`, `>=`, `<=`, `!x` for negation.
- **Ranges** instead of a two-operator spell-out: `"cpu in [1..128]"` rather than `"AND(cpu >= 1, cpu <= 128)"`.
- **≤ 30 characters** where possible. For long conditions: either condense into a sub-pattern (`"4× string ops"`) and give the detail in the following node label — or split across two sequential edges/decision nodes.
- **Standard phrases**:
  - `"Always"` — unconditional edge (no `condition` / `conditionExpression`)
  - `"On Success"` — the `condition: "<stepId>.success"` shortcut
  - `"On Failure"` — the `condition: "<stepId>.failed"` shortcut
- **Disabled edges**: label `"DISABLED edge"` plus `data.disabled: true`. Mark the target node in its label too: `"Log (disabled edge target)"`.

## 5 — The condition-operator coverage pattern

For demo workflows meant to show **all 14 operators plus AND/OR/NOT**, group the operators into 6–7 branches — each branch combining 2–4 operators into a **semantically meaningful rule**. Not one operator per branch (that bloats the workflow), and not everything in one branch (then the demo shows nothing).

A grouping that works well (from [test-master-all-activities.json](../scripts/test-master-all-activities.json)):

| Branch | Rule | Operators |
|---|---|---|
| 1 | `env==prod & env!=stg` | `==`, `!=`, AND |
| 2 | `cpu in [1..128]` | `>=`, `<=`, AND |
| 3 | `disk% > thr OR free < 1GB` | `>`, `<`, OR |
| 4 | `contains+starts+ends+matches` | `contains`, `startsWith`, `endsWith`, `matches`, AND |
| 5 | `output set & isDomain` | `isNotEmpty`, `isTrue`, AND |
| 6 | `empty & !dry` | `isEmpty`, `isFalse`, AND |
| 7 | `NOT contains PANIC` | `NOT`, `contains` |

## 6 — Engine gotchas (must-know while building JSON)

| Gotcha | Right | Wrong |
|---|---|---|
| `waitNofM` config | `"requiredCount": 2` | `"n": 2` (silently defaults to 1) — see [WorkflowEngine.cs:535](../src/NodePilot.Engine/WorkflowEngine.cs#L535) |
| `manualTrigger` params | All `type: "string"`, defaults as strings (`"80"`, `"false"`) | `type: "number"` → the UI sends a number → 400 "cannot convert to System.String" on execute |
| `isTrue`/`isFalse` | Operates on strings: `""`, `"false"` (case-insensitive) and `"0"` are falsy | Everything else is truthy — `"False"` (PowerShell's `[string]$false`) is falsy ✓ |
| `runScript` params | Every declared PS variable is captured automatically as `param.*` — [ProcessExecutionEngine.cs:85](../src/NodePilot.Engine/PowerShell/ProcessExecutionEngine.cs#L85) appends a capture block | Do not insert a `###NODEPILOT_PARAMS###` marker yourself |
| `targetMachineId` | Empty or `"localhost"` → in-process bypass (runs engine-local; the script can remote **itself** via `Invoke-Command`/`New-PSSession`, SCOrch style — see [claude-reference.md](claude-reference.md#runscript--ausführungsort-local-vs-remote--self-managed-remoting)). A GUID → managed WinRM | Real machines need the GUID from the database |
| `==` / `!=` | Numeric when BOTH sides parse as numbers, otherwise a string compare | `"80" == 80` → numerically equal ✓ |
| Node skip | `data.disabled: true` on the node → the step is marked `Skipped`, and downstream nodes with no other active source cascade to `Skipped` | Do not confuse this with disabling an edge — that skips only that one edge, not the node |
| Breakpoint | `data.breakpoint: true` → with `POST /execute` and `{"debug": true}`, the engine pauses before the step. Resume via `POST /executions/{id}/resume` | Outside debug mode the flag is ignored |
| **Data-bus visibility** | A step only reads results of its **graph ancestors**. Anything referencing `{{X.output}}` needs a path from `X` to the reading node | A reference into a **parallel branch** always fails — even if that branch finishes first. With `runScript` this used to survive as a literal in the script and the step went **green with a placeholder** |
| **Trigger parameters** | `{{<triggerNodeId>.param.<name>}}` — e.g. `{{trg.param.filePath}}`, or alternatively `{{manual.<name>}}` | `{{trigger.doctorEmail}}` without `param.` — not a valid tail, stays a literal, and ends up in the config as (for example) an email address |
| **Orphan nodes** | Every non-trigger node needs at least **one active incoming edge** | Without one the node never runs (`Skipped`) — and a `waitAll` junction waiting on it skips too, along with everything behind it. The run still reports **`Succeeded`**, because skipped is not a failure |

## 7 — JSON schema extensions since the initial version

Four designer features that are visible in the workflow JSON and can be set deliberately when generating one.

### 7.1 Flexible ports — `sourceHandle` / `targetHandle`

Every activity node has four handles: `top` | `right` | `bottom` | `left`. Without an explicit value the default is `right` → `left` (classic LTR). For vertical layouts, junctions approached from below, loop-back edges or mixed topologies, edges can pick other sides explicitly:

```json
{
  "id": "e1", "source": "decision", "target": "junction",
  "sourceHandle": "bottom", "targetHandle": "top",
  "type": "labeled", "data": { "label": "case A" }
}
```

Implemented in [edgePorts.ts](../src/nodepilot-ui/src/lib/edgePorts.ts). All four handles are always present and connectable in both directions — the "flexible ports" toggle that used to gate this is gone (it only ever gated the mouse; the JSON fields were always free, which made the two disagree). An agent may set `sourceHandle`/`targetHandle` whenever the layout needs it; the edge properties panel offers the port selection in Expert mode.

**When to use:** vertical phase sections, loop-back edges (from bottom-right back to top-left), central hub topologies, junctions fed from several sides. **When to omit:** classic LTR — the default assignment is clean, and explicit handles would only be noise.

**Anti-pattern:** edges with conflicting handles that would have to squeeze through other nodes. Plan the layout first, then choose handles — not the other way round.

### 7.2 Edge reshape — `data.controlPoints`

Hand-bent edges (cubic Bézier) override every auto-routing mode (smart/curved/straight and the backward U-loop):

```json
{
  "id": "e1", "source": "a", "target": "b",
  "data": {
    "controlPoints": { "cp1x": 240, "cp1y": 200, "cp2x": 360, "cp2y": 200 }
  }
}
```

Details in [smartEdgePath.ts](../src/nodepilot-ui/src/components/designer/edges/smartEdgePath.ts) and [EdgeReshapeHandles.tsx](../src/nodepilot-ui/src/components/designer/edges/EdgeReshapeHandles.tsx). Round-trip stable: save/load/export/import does not strip the field.

**When generating a workflow from scratch**, usually leave it out — auto-routing is good enough and the user can adjust by hand later. **When re-generating with layout preservation** (an AI refactor of an existing workflow) the field must be preserved, or the user loses their hand-bent curves.

### 7.3 Node-level disable and breakpoint

Two boolean flags in the `data` object of every activity node:

- **`data.disabled: true`** — the node is marked `Skipped` on execute. Downstream nodes with no other active source cascade to `Skipped` as well. This allows "comment out this step for now" without touching the edge topology — ideal for "this branch is not finished yet, but should not be deleted from the workflow".
- **`data.breakpoint: true`** — the engine pauses before the step when the run was started with `{"debug": true}`. The SignalR event `StepPaused` opens the variable inspector in the UI. Resume via `POST /executions/{id}/resume`.

Neither was in the initial styleguide, but both are part of the stable JSON contract.

### 7.4 Sticky-note and group nodes

Besides `type: "activity"` there are two annotation/grouping nodes ([StickyNoteNode.tsx](../src/nodepilot-ui/src/components/designer/nodes/StickyNoteNode.tsx), [GroupNode.tsx](../src/nodepilot-ui/src/components/designer/nodes/GroupNode.tsx)). The engine ignores both entirely (sticky notes have no handles, group nodes are pure layout containers).

**Sticky note** — an inline comment in the workflow, for example one explanation per phase. Highly recommended for agent-generated demo and teaching workflows:

```json
{
  "id": "note-phase-b", "type": "stickyNote",
  "position": { "x": 1200, "y": 60 },
  "data": {
    "label": "Note", "activityType": "note",
    "text": "Phase B: 7-way operator-coverage fan — each branch covers 2-4 operators.",
    "disabled": true
  }
}
```

`data.disabled: true` is mandatory — it stops an accidental edge import from turning the note into an endpoint. `data.fontSize` (preset values 11/13/16/20/28) is optional and defaults to 13.

**Group node** — a visual container framing several nodes. Good for phase boundaries or "these five nodes belong together" clusters.

## 8 — Visual effects with no JSON footprint

Two designer features that affect appearance but are **not encoded in the JSON**:

- **The node shape system** ([shapes.ts](../src/nodepilot-ui/src/components/designer/nodes/shapes.ts)): derived from `activityType`. Triggers → octagon (1.55× bounding box), control-flow nodes (`junction`, `decision`, `forEach`, `waitForCondition`) → diamond (1.18×), `returnData` → pentagon flag (1.10×), everything else → square. **Layout impact**: plan more horizontal headroom after trigger nodes (typically 380–400 px instead of 300), and account for the diamond tips — otherwise the shapes overlap their neighbours.
- **The coverage heatmap**: a toolbar toggle tints nodes by how often they ran over the last N days. "Never executed" → 40 % opacity plus grayscale, "rare" → 80 % opacity. For hand-built demo workflows meant to work as a tutorial, it pays to construct every path so it is reachable with the default trigger parameters — otherwise the heatmap shows a lot of grey after the first run.

## 9 — The living reference example

[scripts/test-master-all-activities.json](../scripts/test-master-all-activities.json) — 45 nodes, 54 edges, roughly 7900 × 1320 px. Embedded identically in [workflow-example.json](../src/NodePilot.Ai/Prompts/workflow-example.json) as the few-shot example for AI workflow generation.

- **Phase A** (trigger + init): x=0…600, y=600 — `scheduleTrigger` (cron `0 0/5 * * * ? *`) → `runScript` (init vars) → `log`. Plus a dead-end `log_fail` (legacy `.failed` edge).
- **Phase B** (7-way condition-coverage fan): branch logs at x=1340, y=60/240/420/600/780/960/1140 (180 spread). Each of the 7 edges covers 2–4 operators — together all 14 plus AND/OR/NOT. Plus one disabled edge to `disabled_log` (y=1320). Junction `waitAll` at x=1680.
- **Phase C** (3-way activity fan → junction `waitAny`): branch 1 (file/hash/zip) at y=200, branch 2 (data: json/xml/rest+retry/sql) at y=600, branch 3 (system/process: svc/reg×4/wmi/startProgram/scheduledTask) at y=1000. Junction at x=4500.
- **Phase D** (decision + waitNofM): `decision` (with `breakpoint:true`) at x=4900, 3 case logs at y=420/600/780, junction `waitNofM` (`requiredCount:1`) at x=5500.
- **Phase E** (async + loop): `delay` → `waitForCondition` → `forEach` (3 items, parallel=2) → `startWorkflow` (fire-and-forget) at x=5800…6700.
- **Phase F** (finish): `emailNotification` → `powerManagement` (`disabled:true`) → `log` → `returnData` at x=7000…7900.

**Child workflow** (`Master Test Child: Simple Task`): a minimal `manualTrigger → log → returnData` chain for `forEach`/`startWorkflow` calls. Bundled in the export.

The example is built LTR — as a default style that works well, not as a binding template. Other topologies are explicitly allowed as long as the guiding principles (section 1) hold.

## 10 — Checklist before importing

Before you push a hand-built workflow via `POST /api/workflows/import` or `POST /api/workflows`:

- [ ] **Total canvas size within budget** (see section 1.5): ≤ 3500×1800 for 16–30 activities, ≤ 4500×2200 for 31–50
- [ ] Beyond 15 activities: snake layout rather than one enormous row
- [ ] No two nodes overlap (bounding-box check, including the trigger octagon and the control-flow diamond)
- [ ] No edge cuts through a node it is not meant to pass
- [ ] Edge labels readable without zooming (no clusters, ≤ 30–50 characters)
- [ ] Node labels carry the activity prefix and no operator redundancy with the edge labels
- [ ] Flow direction is consistent within each region (loop-backs and retries may break out visually)
- [ ] `sourceHandle`/`targetHandle` set only where the routing semantically needs it — otherwise omitted
- [ ] Trigger nodes have +80–100 px of extra horizontal headroom for the octagon shape
- [ ] All `position.x`/`position.y` aligned to steps of 20 (snap-grid feel)
- [ ] Fan-out/fan-in with ≥ 5 branches has a +340 px x-gap to the next node
- [ ] Trigger params (if `manualTrigger`) are all `type: "string"` with string defaults
- [ ] `waitNofM` junctions use `requiredCount`, not `n`
- [ ] No edge references a non-existent source or target (dangling check)
- [ ] **Every `{{X.…}}` points at an ancestor** of the reading node (a path from `X` to it) — references across parallel branches fail
- [ ] **No non-trigger node without an active incoming edge** — it would never run, and would block a waiting `waitAll` junction with it
- [ ] Trigger parameters written as `{{<triggerNodeId>.param.<name>}}`, **not** `{{trigger.<name>}}`
- [ ] References in `startWorkflow.workflowNameOrId` point at existing workflow names (post the child first!)
- [ ] Sticky notes carry `data.disabled: true` (protection against accidental edges)
