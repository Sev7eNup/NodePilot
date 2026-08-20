# Workflow Designer — feature overview

A complete inventory of every feature in the NodePilot workflow designer (React SPA, `src/nodepilot-ui/src/components/designer/` plus `pages/WorkflowEditorPage.tsx`), organised by functional area. Source: code as of 2026-07-02.

**Two operating modes:** the designer has a **standard mode** (reduced feature set, friendlier for newcomers) and an **expert mode** (full toolbar, all overlays, single-key shortcuts). Features visible only in expert mode are marked **(Expert)** below.

---

## Contents

1. [Canvas & viewport](#1-canvas--viewport)
2. [Node operations](#2-node-operations)
3. [Node types](#3-node-types)
4. [Activity library / palette](#4-activity-library--palette)
5. [Edges / connections](#5-edges--connections)
6. [Edge conditions](#6-edge-conditions)
7. [Properties panel](#7-properties-panel)
8. [Variable system & autocomplete](#8-variable-system--autocomplete)
9. [Activity configuration](#9-activity-configuration)
10. [Trigger configuration](#10-trigger-configuration)
11. [Running from the designer](#11-running-from-the-designer)
12. [Live monitoring](#12-live-monitoring)
13. [Step debugger](#13-step-debugger)
14. [Step test & simulation](#14-step-test--simulation)
15. [Overlays & productivity tools](#15-overlays--productivity-tools)
16. [Lint & pre-publish](#16-lint--pre-publish)
17. [Editor chrome (header, banners, sidebar)](#17-editor-chrome)
18. [Edit lifecycle (lock / publish)](#18-edit-lifecycle-lock--publish)
19. [Persistence & export](#19-persistence--export)
20. [Visual overlays & display options (designStore)](#20-visual-overlays--display-options)
    - [20a. Live & annotation infrastructure](#20a-live--annotation-infrastructure)
21. [Mobile view](#21-mobile-view)
22. [Keyboard shortcuts (reference)](#22-keyboard-shortcuts-reference)

---

## 1. Canvas & viewport

- **Pan & zoom:** pan with the middle or right mouse button; zoom down to `0.15×`. Wheel zoom via React Flow.
- **Fit view:** `Home` fits all nodes with 15 % padding (300 ms animation); automatic fit on first load (not on lock/unlock/publish refetches).
- **Zoom to selection (Expert):** `Ctrl+Shift+E` zooms to the selected nodes (20 % padding).
- **MiniMap:** pannable and zoomable, node colour follows live status (Running = amber, Paused = orange, Succeeded = green, Failed = red, Skipped = grey), falling back to the last historical health or the activity type colour.
- **Controls panel:** the standard React Flow controls (zoom in/out, fit, lock).
- **Background:** a **dot grid** (gap 24 px, dot size 1.6) — identical in premium **and** classic; the earlier two-level crosshatch is gone without replacement. Opacity is skin-dependent: light skins carry a noticeably stronger alpha, because the grid needs more contrast on a light ground than on a dark one. With snap-to-grid active, a **line** grid on the snap step size replaces the dot grid.
- **Snap to grid (Expert):** `G` toggles grid snapping (20 px by default, configurable).
- **Node scaling:** 8 size presets (XS … 4XL, default L); `Ctrl+Shift+>` / `Ctrl+Shift+<` (Expert) change node size, `Ctrl+Alt+.` / `Ctrl+Alt+,` the label font size (classic node style only).
- **Fullscreen / distraction-free:** `F11` hides the sidebar, panels and banners, keeping header, canvas and an exit pill.
- **Viewport virtualisation:** only visible elements are rendered (`onlyRenderVisibleElements`) — noticeable from around 50 nodes.

## 2. Node operations

- **Adding:**
  - Drag and drop from the library (MIME `application/nodepilot-activity`) at the cursor position — with smart defaults (the last machine/credential of the same type is carried over).
  - Quick-connect: dragging an edge into empty space opens the activity picker and creates node plus edge.
  - Double-click / command palette / toolbar.
- **Moving:** drag (only with write permission); arrow-key nudge (Expert): 10 px, or 1 px with `Shift`.
- **Multi-select:** `Shift/Ctrl + click` (toggle) plus marquee selection (left-drag on the canvas, partial touch is enough). `Ctrl+A` selects everything.
- **Duplicate:** `Ctrl+D` (copy+paste) or the context menu, offset by +40/+40 px, with fresh IDs.
- **Delete:** `Delete`/`Backspace`; for groups the children are re-parented and survive.
- **Group (Expert):** `Ctrl+G` wraps the selection in a group node.
- **Re-parenting:** nodes can be dragged into group nodes (the target group highlights live).
- **Clipboard:** `Ctrl+C` / `Ctrl+V` uses only the in-memory buffer of the open editor tab — no `sessionStorage` and no persistence across a reload, tab close or editor unmount. As long as the same editor stays mounted, the buffer can survive switching workflows within the tab. On paste, IDs are reassigned, edges and parent references remapped, and the offset increased by 40 px per paste.
- **Auto-layout / tidy:** `Ctrl+Shift+T`. Standard mode is always LR; expert cycles LR → TB → Compact → ELK. **Restore original layout** (Expert) `Ctrl+Shift+O` puts the starting layout back.
- **Undo/redo:** `Ctrl+Z` / `Ctrl+Y` (also `Ctrl+Shift+Z`). History depth 50 snapshots; structural changes immediately, property edits batched with an 800 ms debounce; cleared when switching workflows.
- **Bulk edit:** with 2 or more nodes selected, the bulk-edit panel appears (target machine, enable/disable, timeout, retry policy — each with its own apply button).

## 3. Node types

### Activity node
- **Shapes by category:** a pentagon bookend pair (trigger `◁` plus returnData `▷`, mirrored horizontally); **every action activity has its own clip-path silhouette** (the icon stays centred and unclipped); control flow has one shape each (decision = diamond, junction = hexLong, forEach = reel, startWorkflow = tagLeft) with an **indigo group frame** — all via clip-path.
- **Display styles:** **classic** (compact, icon-centred) and **card/MD3** (header plus config summary). Toggle with `Ctrl+Shift+N`.
- **Colours & icon:** per activity type from CSS variables (`--act-<type>-color/-bg/-border`) plus a Material Symbols icon.
- **Handles/ports:** by default only left (in) and right (out); in flexible-ports mode all four sides bidirectionally.
- **Status badges:**
  - **Disabled** (crossed-out eye) — the node is skipped but stays editable (50 % opacity, dashed).
  - **Breakpoint** (red dot; amber when conditional) — pulses while the debugger waits there.
  - **Fan-in** (the incoming edge count) colour-coded by junction mode (waitAll/waitAny/waitNofM/auto).
  - **Lint** (red = error, amber = warning, counter caps at 9+).
  - **Simulation** ("will run" / "skipped").
- **Live status overlay:** Running (amber, pulsing ring), Succeeded (green), Failed (red), Skipped (grey, dashed), Paused (dark orange, pulsing).
- **Heatmaps & paths:** failure tint (red, scaled by failure rate), critical-path glow (orange) plus slack badge, coverage greying (never/rare).
- **Schedule preview:** for `scheduleTrigger`, the next fire time is shown (client-side via cron-parser), "⏸ Paused" when the workflow is disabled.
- **Health sparkline / stats:** the last up to 8 outcomes as dots — they stay visible during a running or canvas-pinned run as well (only the dry-run simulation hides them); performance stats (runs, failure rate, avg/p95 duration) in the hover tooltip (400 ms delay).
- **Output variable & description:** `→ {{var.output}}` in the tooltip; a description indicator bottom right.
- **Variable-flow highlight:** producer/consumer rings when hovering over variables.

### Group node
- Visual grouping only (never executed); children move with it.
- **Resize** (NodeResizer, min 200×120), **5 colours** (blue/green/amber/rose/slate), **collapse/expand** with a child-count badge, **inline label edit** (double-click), drop-target highlight.

### Sticky-note node
- Pure annotation (no handles, always skipped).
- **Resize** (min 140×50), **inline edit** (double-click, commit on blur or Ctrl+Enter), **5 font-size presets** (11–28 px), hover scaling on a far zoomed-out canvas, yellow "sticky note" look with a slight rotation.

## 4. Activity library / palette

- **Categories** (`buildActivityCategories`): **Triggers**, **Actions**, **Control Flow**, **Logic**, **Annotations** (sticky note). Items are sorted alphabetically by label; categories collapse.
- **Search:** filters activities and snippets by name/type (case-insensitive); forces an expand while a search is active.
- **Drag and drop / click** to add (only with write permission; otherwise 50 % opacity).
- **Snippets section:** predefined mini-patterns (try-catch, forEach, fan-out+join, HTTP retry) — they insert a complete group with fresh IDs.
- **Activity picker grid:** a two-column popup grid (quick-connect / edge inserter) — without triggers or annotations.
- **Activity type filter (toolbar):** hides activity types on the canvas (a checkbox per type present, sorted by frequency, "clear all", a badge with the filter count). `Ctrl+Alt+X` (Expert) clears the filter.

## 5. Edges / connections

- **Edge type:** a custom `LabeledEdge` with an arrow marker.
- **Semantic colour coding (status tokens, dark-aware):** success = green, failed = red, custom condition = indigo (stroke, arrow and label pill share the colour), always = grey, disabled = dashed and dimmed — dashed now means *only* disabled; idle edges are solid.
- **Labels:** manual (up to 60 characters in the graph) or auto-labelled from the condition ("On Success" / "On Failure" / "Always"); canonical labels sync automatically, manual ones are preserved.
- **Routing modes:** `smart` (Bézier 0.25), `curved` (0.5), `straight` (step with a radius). `R` (Expert) cycles the modes.
- **Manual bending:** two draggable Bézier control points (reshape handles, only while selected and with write permission), optionally with snap-to-grid; "reset shape" restores auto-routing.
- **Backward edges:** an automatic U shape below the nodes when the flow runs right→left.
- **Edge width/animation:** `Ctrl+]` / `Ctrl+[` (width), `A` (Expert, flow animation). The animated flow dash now only runs during a live execution, on the edge into the currently running step — idle edges stay calm and solid.
- **Inline insert:** the ⊕ button on an edge inserts a node between A→B (A→NEW→B); the first half is unconditional, the second inherits label and condition.
- **Quick-connect picker:** drag an edge into empty space → the activity picker appears at the cursor; it creates the node and the connecting edge.
- **Data-flow overlay:** chips show which variables travel across an edge.
- **Context menu (right-click):** enable/disable, swap source↔target (Expert), reset shape (Expert), delete.
- **Validation:** duplicate connections are prevented (with a toast).
- **Edge properties panel:** source→target information, port selector (Expert plus flexible ports), a label field with a "use auto" reset, simple or expression condition, a disabled toggle, delete (with confirmation).

## 6. Edge conditions

- **Simple mode:** "On Success" / "On Failure" / "Always" buttons plus a free-text condition (`stepId.success` and similar).
- **Expression builder (Expert):** a visual AST editor.
  - **Comparison operators:** `==`, `!=`, `<`, `>`, `<=`, `>=`, `contains`, `startsWith`, `endsWith`, `matches` (regex).
  - **Unary:** `isEmpty`, `isNotEmpty`, `isTrue`, `isFalse`.
  - **Composition:** AND/OR groups (nestable), NOT wrappers.
  - **Operands:** a variable (step `output`/`error`/`success`/`param.X`, global, manual) or a literal (with `{{globals.X}}` support).
- **Disabled edge:** skipped (the target node does not become a root), marked with a ⊘ badge.

## 7. Properties panel

- **Sections:** execution context, input variables, configuration, timeout, test & debug — collapsible with a variable counter.
- **Header:** activity icon, inline edit for name and description, close.
- **Common node fields:**
  - **Label** and **output variable** (falling back to the step ID; downstream `{{var.output}}`).
  - **Description** (lazy "+ add description").
  - **Target machine** and **credential** (for remote activities) as a `DynamicTargetField` — GUID, variable or literal, with a resolved label and a test-connection button.
  - **Timeout** (`config.timeoutSeconds`, only for types that support it).
  - **Disable toggle** and **breakpoint** (plus an optional breakpoint condition, Expert).
- **Read-only mode:** without write permission the whole fieldset is disabled.
- **Clone-config button (Expert):** copies configuration from a step of the same type — either completely or just machine plus credential (it never copies label, output or body).
- **External-trigger note:** a green info box for schedule/webhook/file-watcher/database/event-log triggers ("Active …, the workflow must be enabled").

## 8. Variable system & autocomplete

- **Inline autocomplete:** typing `{{` opens a dropdown (`useVariableAutocomplete`); keyboard navigation (↑/↓, Enter/Tab selects, Esc closes). Can be switched on or off per field via the zap toggle (mirrored into localStorage).
- **Variable sources:** upstream outputs of every ancestor step, globals (`{{globals.NAME}}`), manual and trigger data (`{{manual.NAME}}`).
- **Tail types:** `.output`, `.error`, `.success`, `.param.X`.
- **Derived outputs (`describeNodeOutputs`):** every step provides `.output`; additional `.param.*` entries are derived per type — `runScript` by regex from `$var = …` assignments, `returnData` from the data keys, `decision` → `param.case`, `wmiQuery` from captureProperties (plus `param.count`), `registryOperation` depending on the operation (value/exists/created and so on), `manualTrigger` from the declared parameters.
- **VariableInsertField:** a unified input with a picker tray (variable picker, global picker with a secret lock, options picker), drag and drop of variables into text areas, template validation (unbalanced `{{}}`, invalid identifiers), and an SQL-injection warning on dynamic queries.
- **Live preview tooltip:** hovering a variable shows the last runtime value from the most recent execution (250 ms delay, a "[Truncated]" badge, anchored to the right).
- **AvailableVariablesList** (input-variables section): grouped by step, with type badges, click-to-copy and drag and drop.
- **ParameterTable:** a key/value table for trigger parameters and free-form child-workflow parameters.
- **ContractMappingTable:** typed mapping for sub-workflows with a known contract — inputs (type badge, required star, default hint, validation), a stale-key warning, and a read-only list of outputs.
- **JsonPathTree:** interactive JSONPath selection from the last step output (click to copy, down to depth 7).

## 9. Activity configuration

Each activity type has its own config component (`properties/activities/`, registered in `activityConfigMap.ts`). Many fields are **conditional** — they appear depending on the chosen operation or action. Required fields come from `lib/activityConfigFacts.ts` (which also feeds the `missing-required-config` lint rule).

| Activity | Configuration fields (with real option values) |
|---|---|
| `runScript` | **engine** (auto/pwsh/powershell), **succeed on exit codes** (`successExitCodes`, empty = error-based only), **transcript** (auto-logging checkbox), **process isolation** (checkbox, local only — its own process inside a Windows job object; greyed out for a remote target) plus optional caps **memory limit (MB)** / **max processes**, **script** (PowerShell code field, minimum 12 lines) plus a fullscreen Monaco editor with AI generation and step test |
| `fileOperation` | **operation** (copy/move/rename/delete/exists/create), **path**, **destination** (for copy/move), **newName** (for rename) |
| `folderOperation` | **operation** (copy/move/rename/delete/exists/list/create), **path**, **destination** (for copy/move), **newName** (for rename) |
| `fileHash` | **path**, **algorithm** (SHA256/SHA1/MD5/SHA384/SHA512), **expected** (optional target hash) |
| `zipOperation` | **operation** (compress/extract), **source**, **destination**, **compressionLevel** (Optimal/Fastest/NoCompression, compress only), **force** |
| `serviceManagement` | **serviceName**, **action** (status/start/stop/restart/create/delete/setStartType); for create: binaryPath, displayName, description, **startupType** (Automatic/AutomaticDelayedStart/Manual/Disabled) |
| `scheduledTask` | **taskName**, **taskPath**, **action** (get/start/stop/enable/disable/register/unregister); for register: program, arguments, workingDirectory, **triggerType** (once/daily/weekly/atLogon/atStartup), startTime, daysInterval/weeksInterval, daysOfWeek (Mon–Sun toggles), runAsUser, **runLevel** (limited/highest), force |
| `registryOperation` | **operation** (read/write/deleteValue/deleteKey/createKey/exists/listSubKeys/listValues), **keyPath**, **valueName**, **valueType** (String/ExpandString/Binary/DWord/MultiString/QWord, for write), **value** (for write) |
| `wmiQuery` | **mode** (query/wql/invokeMethod), **className**, **namespace** (default `root\cimv2`), **filter** (WHERE), **query** (WQL), **methodName** plus **arguments** (invokeMethod), **captureProperties** (property list) |
| `startProgram` | **filePath**, **arguments**, **workingDirectory**, **useShellExecute**, **waitForExit**, **successExitCodes** (with waitForExit) |
| `powerManagement` | **action** (shutdown/restart/logoff/abort/hibernate), **delaySeconds**, **force**, **message** (for shutdown/restart) |
| `waitForCondition` | **conditionType** (script/pathExists/serviceRunning/portOpen/httpOk), each with its own fields (script plus snippet buttons / path / serviceName / host+port / url), **intervalSeconds** |
| `restApi` | **method** (GET/POST/PUT/PATCH/DELETE/HEAD), **url**, **headers** (Key: Value per line), **body** (JSON code field, for POST/PUT/PATCH), a collapsible proxy section: **proxyMode** (default/custom/direct), proxyAddress, noProxy |
| `sql` | **provider** (sqlserver/sqlite/postgres), connection mode **builder ↔ connection string** (builder fields per provider: server/database/auth/encrypt …, or host/port/sslMode …, or dataSource), **query** (SQL code field) |
| `emailNotification` | **to**, **subject**, **body** (multiline), **isHtml** (checkbox) |
| `textFileEdit` | **operation** (append/prepend/insert/replaceLine/delete/replace), **path**, **content**, **lineNumber**, delete sub-mode (line/range/pattern), matchPattern, replace, useRegex, ignoreCase, occurrences (all/first), encoding (auto/utf8/utf8-bom/utf16le/utf16be/ascii), lineEnding (preserve/crlf/lf), backupSuffix, createIfMissing, dryRun |
| `delay` | **seconds** (number, minimum 1) |
| `generateText` | **random text generator** — **mode** (alphanumeric/alphabetic/numeric/hex/guid/password/custom), **length** (except guid), **customCharset** (for custom), **excludeAmbiguous** |
| `llmQuery` | **LLM query** — an OpenAI-compatible endpoint (prompt→text; chat completions or the responses API, depending on the `baseUrl` path). **prompt** (required, supports `{{templates}}`), **systemPrompt**, **jsonMode**; override section: **baseUrl**, **model**, **apiKey** (secret), **maxTokens**, **temperature** (0–2), timeout field automatic. Empty → the active LLM profile; needs `Llm:Enabled=true` plus a resolvable active profile |
| `xmlQuery` | **source** (inline/file), content (XML code field) or path, **xpath**, **resultMode** (single/all), **namespaces** (JSON mapping) |
| `jsonQuery` | **source** (inline/file), content (JSON code field) or path, **jsonPath** plus a JSONPath picker, **resultMode** (single/all) |
| `log` | **level** (info/warning/error), **message** (multiline) |
| `junction` | **mode** (waitAll/waitAny/waitNofM), **requiredCount** (for waitNofM) — controls the fan-in behaviour |
| `forEach` | **items** (multiline), **itemsFormat** (auto/json/lines), **childWorkflowNameOrId** plus picker, **itemParameterName** (default "item"), **indexParameterName** (default "index"), **maxParallelism** (0–64), **timeoutSecondsPerItem**, **continueOnError**, additional **parameters** (ParameterTable) |
| `decision` | **cases** (name plus ConditionBuilder, sortable via up/down, first match → `param.case`), **defaultCaseName** |
| `returnData` | **data** (a freely extendable key/value table) |
| `startWorkflow` | workflow picker plus preview button, **workflowNameOrId**, **waitForCompletion**; parameters via ContractMappingTable (with a known contract) otherwise ParameterTable; a timeout in synchronous mode |

> Every remote activity additionally shows target machine, credential, test connection and, where applicable, a timeout.
> Inline code fields use **CodeMirror** (languages: powershell/sql/json/xml/plain) with `{{` autocomplete; only the **fullscreen script editor** is Monaco.

## 10. Trigger configuration

| Trigger | Key fields (with real option values) |
|---|---|
| `manualTrigger` | **title**, **description**, **input parameters** (name plus **type** string/number/boolean/select plus required plus default); a live hint shows the access syntax |
| `scheduleTrigger` | **cronExpression** plus 4 presets (`0 */5 * * * ?`, `0 0 * * * ?`, `0 0 6 * * ?`, `0 0 8 ? * MON-FRI`), a **live preview of the next 5 fire times** (client-side, Quartz normalised to cron-parser, relative times, red parse errors), description |
| `webhookTrigger` | **HTTP method** (POST/PUT/GET), **webhook path** (template-capable), optional **secret** (password field, HMAC) |
| `fileWatcherTrigger` | **directory**, **fileFilter** (default `*.*`, comma-separated globs), **watchType** (created/changed/deleted/renamed/any), **includeSubdirectories** |
| `databaseTrigger` | **connectionRef** (a reference into `Trigger:Database:Connections`), **pollingIntervalSeconds** (default 30, minimum 5; alias `intervalSeconds`), **query** (multiline, with an SQL template warning on `{{`) |
| `eventLogTrigger` | **logName** (Application/System/Security/Setup), **entryType** (any/Error/Warning/Information/SuccessAudit/FailureAudit), **source** (optional), **eventId** (optional), **messagePattern** (optional, a regex against the message text), **lookbackMinutes** (default 5, applies to the manual test run only) |

> Trigger data arrives in the run as `manual.*` variables (`{{manual.<name>}}`), and as `param.*` of the trigger node — there is **no** `trigger.*` namespace.

## 11. Running from the designer

- **Test run** (`Ctrl+Enter`): starts immediately, or opens the parameter dialog for a `manualTrigger` that declares parameters (prefilled with the values last used).
- **Debug run** (Expert, `Ctrl+Shift+Enter`): runs with `debug: true` → breakpoints are active.
- **Auto-save before a run:** unsaved changes are saved first when you have write permission.
- **Cancel run** (`Ctrl+Shift+X`): aborts the running execution.
- **Canvas pinning:** a started execution is pinned → nodes and paths colour live; a snapshot keeps it after the 30 s SignalR TTL.

## 12. Live monitoring

- **Live execution panel with tabs:** **Live**, **History**, **Output**, **Watch** (Expert) — driven by SignalR live updates.
- **Live tab:** a virtualised list of active runs (status badge, done/total, failed counter, paused pulse) plus a step inspector (config, **output parameters** from the live data bus (the `param.*` entries of the selected step), output/error/transcript, start/end, offset, duration); both panes are resizable.
- **Live console:** a chronological stream (stdout/stderr/transcript), up to 1000 lines, text filter, an "errors only" toggle, auto-scroll, click-to-inspect, line prefix `+Xms · Step · Text`.
- **Stats strip:** step progress, succeeded/failed/running counts, elapsed time, **ETA** (from the median of historical runs).
- **Live timeline & Gantt chart:** step bars coloured by status, running bars grow on a 250 ms ticker; shared between live and history; list/Gantt switching; the Gantt has a **scrubber** (replay at a point in time).
- **History tab:** a virtualised, sortable table (status, ID, trigger, user, steps, failed step, start/end, duration, error, extras) with a scope toggle (current workflow versus all); drill-down into the StepTimeline (list/Gantt) — the expanded step detail shows config, **output parameters** (`outputParametersJson`, the persisted `param.*` entries), output/error/transcript; a gap indicator (`∥` for parallel) and a replay toggle.
- **Output tab:** a data-bus browser, grouped (trigger/global/step outputs/other), with coloured badges (OUT/ERR/PAR/TRG/GLB), a filter with auto-expand, expand/collapse all, and a copy button.
- **Watch tab (Expert):** arbitrary `{{expression}}` entries with a right-click variable picker, resolved live against the data bus, persisted per workflow in localStorage.

## 13. Step debugger

- **Breakpoints:** `B` (Expert) or the context menu; optionally conditional.
- **Auto-pause detection:** when a step pauses, the live tab opens automatically.
- **Paused variables inspector:** a fullscreen inspector while paused — the header shows "Paused at: step", the reason (breakpoint/stepOver) and the time.
  - **Three resume modes:** **continue**, **step over** (skip this step), **stop** (abort the execution) → `POST /executions/{id}/resume`.
  - **Variable inspection and override:** grouped (globals/manual/step/other), inline editing with a dirty highlight, a reset button, and overrides handed over on resume.

## 14. Step test & simulation

- **Step test panel:** test a single step in isolation (`POST /workflows/{id}/steps/{stepId}/test` with a `configOverride`).
  - **Modes (Expert):** empty / last run / pick run (from the 10 most recent) / manual mocks.
  - **Context preview:** `GET …/test-context` shows variables by origin; there is a refresh button.
  - **Result:** success/failure with output, error, output parameters and duration; a JSONPath picker for JSON output.
- **Expression tester (Expert):** ad-hoc testing of template expressions against mock variables (auto-prefilled from the last run), green or amber depending on resolution.
- **Simulation / dry run (Expert):** `Ctrl+Shift+R` — a topological path analysis over the active edges (control flow only, nothing actually executes); it shows the execution order and the reachable versus skipped nodes with a step-by-step animation, plus a progress banner.

## 15. Overlays & productivity tools

- **Command palette** (`Ctrl+Shift+P`): fuzzy search across roughly 45 designer commands, grouped by category, keyboard navigation, expert filtering.
- **Search overlay** (`Ctrl+F`): node search (label/type/ID), jump to a hit (centre plus select), up to 30 results.
- **Find & replace** (Expert, `Ctrl+H`): across node labels, edge labels and config values (deep traverse); replace one / replace all, match navigation with a context snippet.
- **Help overlay** (`?`): the full shortcut reference by category.
- **Node context menu (right-click):** duplicate, toggle disabled, toggle breakpoint (Expert), delete.
- **Quick-edit popup (double-click):** inline editing of the most important field per activity type (script, URL, service name and so on) — `runScript` opens the full script editor instead.
- **Script editor dialog:** a Monaco editor (PowerShell, with its own theme), draggable and resizable or fullscreen, word wrap, font size, toggle comment, a variables sidebar (upstream plus automatically detected `$var` assignments → `{{step.param.var}}`), inline linting of unknown `{{…}}`, `{{` autocomplete, **AI generation** (replace/insert) and **run step test** with a result panel.
- **Workflow quick switcher** (`Ctrl+P`): fast switching between workflows, recents (localStorage), scored substring matching.
- **Sub-workflow preview:** clicking a `startWorkflow` node opens a read-only preview (its own React Flow instance, pan/zoom), optionally "open in editor".
- **Workflow diff** (Expert, `Ctrl+Shift+D`): a version timeline plus diff (added/removed/changed for nodes and edges, ID-stable including handles and positions), with an admin restore to the selected version (`POST …/rollback/{v}`).
- **AI workflow assistant** (the violet button next to the standard/expert toggle): a docked multi-turn chat panel that explains the current workflow (in Markdown) and, on request, proposes complete rebuilds. Every role may ask; only admins and operators can apply a proposal. Proposals are sent to the LLM with secrets redacted server-side, merged back onto the original by node ID (layout, secrets and fields are preserved), presented as a **proposal card** (a structured changelog, selective per-change adoption, refine, undo/auto-layout) and applied to the canvas (no database write — saving goes through the normal edit-lock/publish flow; stale protection blocks outdated proposals). Opt-in via `Llm:Enabled`.

## 16. Lint & pre-publish

- **Lint panel** (`Ctrl+Shift+L`): lists every error and warning with its code, the target node or edge, and click-to-jump. Lint runs live on every graph change; a node badge shows the count.
- **Rules detected (excerpt):**
  - **Errors (block publishing):** `no-trigger`, `isolated-node`, `dup-output-variable`, `duplicate-edge`, `missing-required-config`, `missing-target-machine`.
  - **Warnings:** `orphan-root`, `unreachable-node`, `unknown-template-ref`, `startjob-in-runspace`, `unknown-workflow-ref`, `edge-to-disabled`, `disabled-with-downstream`, `edge-occluded`, `edge-crowded`.
- **Pre-publish checklist modal:** shown before publishing — blocked on errors (publish disabled), "publish anyway" on warnings, straight through when clean; every issue is clickable to its node or edge.

## 17. Editor chrome

### Header & toolbar
Seven clusters with a proximity-driven colour glow (purely cosmetic, prefers-reduced-motion aware):
1. **History:** undo, redo.
2. **Layout** (with write permission): tidy (Expert shows the algorithm), restore original layout (Expert).
3. **Inspect:** search, find & replace (Expert), zoom to selection (Expert), diff (Expert), simulation (Expert), shortcuts (Expert), hidden-types pill.
4. **View** (Expert): the **"appearance" settings dialog** (settings icon, `role="dialog"`) — it gathers every canvas display option (node style, icon view, ports/auto-hide, edge animation/routing/width, node and label size, premium canvas, snap grid) as labelled card rows with a switch, segmented control or stepper; plus the activity type filter.
5. **Run:** test run, debug run (Expert), cancel, lint pill, and the **"view" popover** (Expert, an eye icon with an active counter): it gathers the overlay switches — machine colouring, failure heatmap, data flow, coverage, critical path — as switch rows.
6. **Lifecycle** (with role write permission): edit-lock toggle, save (with a dirty dot), publish/disable/enable.
7. **Export** (Expert): JSON, PNG. In standard mode these live in the more menu.

Outside the seven clusters in the header (to the right of the standard/expert toggle):
- **AI workflow assistant button** (violet, sparkles icon) — opens the chat panel; described fully in § 15.
- **Toolbar layout switch** (`Rows3` icon, `data-testid="toggle-toolbar-layout"`, to the left of the skin switcher, visible in **both** layouts): flips the header between the compact three-zone layout (the default; grouped view/overlays/tools popovers, a centred name, a green "run" CTA) and the classic inline row (every toggle and tool as an individual button, a right-aligned name, an icon-only play). Persisted in the `designStore` (`toolbarLayout`, default `compact`). Both layouts share `EditorIdentity`/`SkinSwitcher`/`RunControls`/`LifecycleControls`/`StandardMoreMenu` (so there is no double-maintenance drift); the `EditorHeader` dispatcher only reads `toolbarLayout` and renders `CompactEditorHeader` or `ClassicEditorHeader`. The classic row wraps whole clusters onto several lines in narrow windows and keeps the proximity glow trays.
- **Colour skin switcher** (palette icon, icon only, next to the AI button): opens a popover with all 7 skins plus `system` from the `THEMES` registry. The selection syncs with the settings picker and the sidebar (a shared `useThemeStore`); the active skin gets a check icon; clicking outside closes the popover.

### Inline workflow name
Grows with its content; read-only without write permission.

### Status banners (contextual)
Replay mode, test-run mode, viewer read-only, **locked by other** (plus admin force-unlock), **locked by me**, productive/disabled.

### Breadcrumbs
Pills for outgoing workflow references (`startWorkflow` / `forEach`); a resolved one becomes a link, an unresolved one an amber warning; dynamic `{{…}}` references are ignored.

### Folder path breadcrumb (canvas, top left)
The folder path of the open workflow. Every RBAC-visible segment opens a popover with sub-folders (drill down via a mini browser) and workflows (open directly) — navigating the folder hierarchy without leaving the canvas. Root workflows show no breadcrumb; ancestors that are not visible stay non-interactive text. Frontend only (from `workflow.folderPath`).

### Sidebar (left)
Two tabs (**node library** / **workflow browser**), collapsible to an icon bar, resizable.

### Workflow browser
- **View toggle:** folder view (a hierarchical shared-folder tree with drag and drop into folders) versus trigger view (grouped by trigger type).
- **Search/filter**, a status dot (enabled/disabled), an activity count, hover actions (insert into a start-workflow / open) and a "current" marker.
- **Splitter plus info card.**

### Workflow info card
Metadata of the hovered or current workflow: status, name, version, trigger, steps, last run, success rate, average duration, modified (time/user), locked by, folder path.

### Maintenance-window badge
A self-fetch of `…/maintenance-windows/affecting/{id}` — shows the relevant maintenance windows as pills ("· Active Now") with an active indicator.

### Right panel
Depending on the selection it shows: bulk edit (2 or more nodes, Expert), properties (1 node), edge properties (1 edge), or nothing; resizable.

## 18. Edit lifecycle (lock / publish)

- **`canWrite = roleCanWrite && isLockedByMe`** — both conditions are required.
- **Lock** (`Ctrl+E`): atomically sets `IsEnabled=false` plus the lock; 409 when already locked.
- **Unlock** (`Ctrl+U`): releases the lock (`IsEnabled` stays as it is).
- **Publish** (`Ctrl+Shift+S`): atomically save plus enable plus unlock (through the pre-publish checklist).
- **Disable/enable:** the kill switch (disable is **not** lock-gated); enable only without a foreign lock.
- **Force unlock** (admin, `Ctrl+Shift+U`): breaks a foreign lock (confirmation plus an audit entry).
- **Read-only behaviour:** without `canWrite`, dragging, connecting, deleting, context menus and inline edits are all disabled.

## 19. Persistence & export

- **Dirty state:** every structural or property change marks the workflow dirty.
- **Auto-save:** a 5 s debounce after the last change (with write permission); runtime fields (`__liveStatus`, `__health`, `__stats`, …) are stripped before serialising.
- **Manual save:** `Ctrl+S`; save-before-run is awaitable.
- **Dirty protection:** a `beforeunload` warning plus a navigation blocker ("discard changes?") while there are unsaved changes.
- **Version history:** update and rollback snapshot the previous definition.
- **Export:** **JSON** (`Ctrl+Shift+J`, `{name}.workflow.json` via the API) and **PNG** (`Ctrl+Alt+P`, the WYSIWYG viewport without controls or minimap, DPI ≥ 2).

## 20. Visual overlays & display options

Every display setting lives in the **`designStore`** (Zustand plus persist, key `nodepilot-design`) and applies editor-wide:

| Setting | Default | Values / range | Effect |
|---|---|---|---|
| `designerMode` | `standard` | standard / expert | Unlocks the extended toolbar, overlays and shortcuts |
| `designerTheme` | `atelier` | atelier / classic | The designer's design language: **Atelier** (its own workbench look — paper and graphite ground with the familiar squared grid, floating card chrome, one accent; `styles/designer-atelier.css` maps the `--color-*` tokens through a `--wd-*` palette layer) versus **Classic** (the previous look, byte-identical). **Colour skins adapt both looks:** in Atelier each skin re-points the accent family and the base tone (`--wd-accent*`, `--wd-canvas`/`--wd-panel`) while the Atelier geometry stays universal; status colours remain skin-stable in both. The switch (`Brush` icon, `role="switch"`, `data-testid="toggle-atelier-theme"`) is in both header layouts |
| `toolbarLayout` | `compact` | compact / classic | Header layout: compact (grouped popover menus, a green "run") versus the classic inline row (every toggle and tool as an individual button, icon-only play). The switch button exists in both layouts |
| `nodeStyle` | `classic` | classic / card | Node rendering (`Ctrl+Shift+N`) |
| `nodeScaleIndex` | 3 | 0–7 (XS … 4XL) | Node size |
| `labelFontOffsetIndex` | 2 | 0–6 (−4 … +8 px) | Label font size |
| `edgesAnimated` | true | bool | Flow animation on edges (`A`) |
| `edgeWidthIndex` | 2 | 1.5/2/2.5/3.5/5/7 px | Edge width (`Ctrl+]`/`[`) |
| `edgeRouting` | `smart` | smart / curved / straight | Routing (`R`) |
| `flexiblePortsEnabled` | false | bool | All four node sides as ports |
| `snapToGrid` | false | bool | Grid snapping (`G`) |
| `snapGridSize` | 20 | 10/20/30/40/60 px | Grid spacing |
| `layoutMode` | `LR` | LR / TB / Compact / ELK | Auto-layout algorithm |
| `machineColoringEnabled` | false | bool | Colours nodes by target machine plus a legend (`M`, an 8-colour palette) |
| `failureHeatmapEnabled` | false | bool | Red tint by failure rate (`H`, from `__stats.failureRate`) |
| `coverageHeatmapEnabled` | false | bool | Greys out nodes that never or rarely ran (coverage API) |
| `coverageWindowDays` | 30 | 1–365 | The coverage heatmap's time window |
| `criticalPathEnabled` | false | bool | Highlights the longest path plus slack badges (`C`) |
| `dataFlowOverlayEnabled` | false | bool | Visualises variable flow along edges |
| `premiumCanvas` | true | bool | Depth shadows, glass effect, coloured arrows, glow. **Not** the canvas background — the dot grid is the same in both modes (see [1. Canvas & viewport](#1-canvas--viewport)) |
| `designRefresh` | true | bool | The new designer design on or off (a header toggle; off = the classic look) |

- **Coverage heatmap:** reads `GET /workflows/{id}/coverage?windowDays=N` → the node annotation `__coverage` (`never`/`rare`/`common` plus a red dot when there were failures).
- **Critical path:** a real CPM calculation (topological Kahn sort plus a forward and backward pass) weighted by `p95DurationMs`; it ignores disabled nodes and edges; critical nodes turn orange, the rest get a slack badge.

## 20a. Live & annotation infrastructure

- **SignalR live stream** (`/hubs/execution`, `useSignalR` plus a reducer): the events **StepStarted**, **StepCompleted**, **StepPaused**, **StepResumed**, **ExecutionStatusChanged**, **LiveEventsBatch**. Authentication via the httpOnly cookie on the WebSocket upgrade.
  - **Transport ladder:** WebSockets → server-sent events → long polling. Neither client nor hub restricts the transports and `skipNegotiation` is never set — a WebSocket upgrade dropped by a proxy or TLS inspection is therefore **not an outage** but a silent fallback to SSE (plus one error line from `@microsoft/signalr` in the console). Diagnosis: `docs/deployment-guide.md`, section *Is the live connection healthy?*.
  - Builds the **live data bus** (`{stepId}.output/.error/.param.*` plus the output-variable aliases).
  - **Hydration concurrency:** at most 4 parallel step fetches (a semaphore) against a thundering herd; auto-hydration covers only the 10 most recent active runs.
  - **TTL/eviction:** finished runs disappear after 30 s; a periodic refresh every 10 s; reconnect re-joins the workflow.
  - **Cache invalidation:** the React Query caches (`workflow-executions`, `executions`, `dashboard-stats`) are invalidated on a terminal status with a 500 ms debounce.
- **Node annotation aggregator** (`useNodeAnnotations`): writes per node `__liveStatus` (live or replay scrub), `__health` (a sparkline from `step-health`, refreshed every 60 s), `__stats` (`step-stats?windowDays=30`, refreshed every 5 minutes), `__machineColorIdx`, `__varFlowRole` (producer/consumer while hovering a variable) and the edge highlight `__varFlowHighlighted` along the producer→consumer path.
- **Displayed-graph projection** (`useDisplayedGraph`): a read-only transformation of the raw data before rendering — it hides filtered activity types (and their edges), computes `inDegreeCount` (the junction badge), the simulation status (`reachable`/`revealing`/`skipped`), lint counters, the failure tint, the data-flow variables, and folds collapsed groups together (`collapsedGraphView`).
- **Smart defaults** (`lastSimilarNode`): new nodes inherit configuration (engine, machine and so on) from the most recently placed step of the same type.
- **Resizable panels** (`useResizable`): sidebar, right panel, info card and the execution panes resize by dragging; double-click resets.
- **Persisted UI state:** `workflowBrowserStore` (view mode folder/trigger, collapsed folders, info-card height) and `sidebarStore` (sidebar collapsed).

## 21. Mobile view

`MobileWorkflowView` — a read-only graph for phones and portrait tablets:
- Reuses the editor's node and edge types; `draggable:false`, `selectable:false`.
- **Live status** of the nodes via SignalR.
- An enlarged node scale (`NodeScaleOverrideContext`, index 3) for readability; zoom clamped to 0.7–1.6.
- A header with a back button, the workflow name and a read-only note; pan and pinch-zoom instead of editing.

## 22. Keyboard shortcuts (reference)

### Global / standard
| Key | Action |
|---|---|
| `?` | Help / shortcut reference |
| `Esc` | Close overlays |
| `Home` | Fit all |
| `F11` | Fullscreen |
| `Ctrl+P` | Quick switcher |
| `Ctrl+Shift+P` | Command palette |
| `Ctrl+S` | Save |
| `Ctrl+Shift+S` | Publish / enable / disable |
| `Ctrl+F` | Search |
| `Ctrl+C` / `Ctrl+V` | Copy / paste |
| `Ctrl+D` | Duplicate |
| `Ctrl+Z` / `Ctrl+Y` / `Ctrl+Shift+Z` | Undo / redo |
| `Ctrl+A` | Select all |
| `Ctrl+E` / `Ctrl+U` | Lock / unlock |
| `Ctrl+Enter` / `Ctrl+Shift+Enter` | Test run / debug run |
| `Delete` / `Backspace` | Delete |

### Expert mode (in addition)
| Key | Action |
|---|---|
| `A` / `R` / `M` / `H` / `C` / `G` | Edge animation / routing / machine colouring / heatmap / critical path / snap grid |
| `D` / `B` | Toggle disabled / breakpoint on the selection |
| `Tab` / `Shift+Tab` | Move to the next/previous connected node |
| Arrow keys (`+Shift`) | Nudge the selection (10 px / 1 px) |
| `Ctrl+G` | Group |
| `Ctrl+H` | Find & replace |
| `Ctrl+Shift+E` | Zoom to selection |
| `Ctrl+Shift+U` | Force unlock (admin) |
| `Ctrl+Shift+X` | Cancel run |
| `Ctrl+Shift+T` / `Ctrl+Shift+O` | Tidy / restore layout |
| `Ctrl+Shift+L` | Lint panel |
| `Ctrl+Shift+D` | Diff |
| `Ctrl+Shift+R` | Simulation |
| `Ctrl+Shift+N` | Node style (classic/card) |
| `Ctrl+Alt+X` | Clear the activity type filter |
| `Ctrl+]` / `Ctrl+[` | Edge width +/− |
| `Ctrl+Shift+>` / `Ctrl+Shift+<` | Node size +/− |
| `Ctrl+Alt+.` / `Ctrl+Alt+,` | Label font +/− |
| `Ctrl+Shift+J` / `Ctrl+Alt+P` | Export JSON / PNG |
| `Ctrl+Shift+1…5` | Navigate: workflows / executions / machines / globals / audit |

### Canvas gestures
- Left-drag = marquee selection · middle/right-drag = pan · `Shift+click` = extend the selection.
