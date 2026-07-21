# Properties, Modi & Shortcuts

## Properties-Panel

Kontextsensitiv pro selektiertem Node/Edge. Sektionen: Execution Context, Input Variables, Configuration, Timeout, Test & Debug (kollabierbar).

**Common Node-Fields:**
- **Label** + **Output Variable** (Fallback Step-ID; downstream `{{var.output}}`).
- **Description** (lazy "+ Add description").
- **Target Machine & Credential** (Remote-Activities) als `DynamicTargetField` — GUID/Variable/Literal, mit aufgelöstem Label und Test-Connection-Button.
- **Timeout** (`config.timeoutSeconds`, nur für Timeout-fähige Typen).
- **Disable Toggle** und **Breakpoint** (+ optionale Bedingung, Expert).

Ohne Schreibrechte ist das gesamte Fieldset deaktiviert (Read-Only).

**Clone-Config (Expert):** übernimmt Config von einem Step desselben Typs — vollständig oder nur Machine+Credential.

## Variablen-System & Autocomplete

- **Inline-Autocomplete:** `{{` öffnet ein Dropdown (`useVariableAutocomplete`); ↑/↓-Navigation, Enter/Tab select, Esc close. Per-Field via Zap-Toggle.
- **Variable Sources:** Upstream-Outputs aller Vorgänger-Steps, Globals (`{{globals.NAME}}`), Manual/Trigger-Daten (`{{manual.NAME}}`).
- **Tail Types:** `.output`, `.error`, `.success`, `.param.X`.
- **Derived Outputs (`describeNodeOutputs`):** jeder Step liefert `.output`; zusätzliche `.param.*` pro Typ — `runScript` via Regex aus `$var = …`, `returnData` aus Data-Keys, `decision` → `param.case`, `wmiQuery` aus `captureProperties` (+ `param.count`), `manualTrigger` aus definierten Parametern.
- **VariableInsertField:** Unified Input mit Picker-Tray (Variable/Global/Options), Drag & Drop in Textareas, Template-Validation, SQL-Injection-Warnung bei dynamischen Queries.
- **Live Preview Tooltip:** Hover über Variable zeigt letzten Runtime-Wert (250 ms Delay, "[Truncated]"-Badge).
- **JsonPathTree:** interaktive JSONPath-Selection aus letztem Step-Output (bis Tiefe 7).
- **ContractMappingTable:** typisiertes Mapping für Sub-Workflows mit bekanntem Contract (Inputs typisiert, Outputs read-only).

## Activity-Konfigurationen

Jeder Activity-Typ hat eine eigene Config-Komponente (`properties/activities/`, registriert in `activityConfigMap.ts`). Viele Felder sind **conditional** — sie erscheinen je nach gewählter Operation/Action. Required-Fields kommen aus `lib/activityConfigFacts.ts` (speist auch die `missing-required-config`-Lint-Regel).

Inline-Code-Felder nutzen **CodeMirror** (powershell/sql/json/xml/plain) mit `{{`-Autocomplete; nur der **Fullscreen-Script-Editor** ist Monaco (eigenes Theme, AI Generate, Step Test).

Die vollständige Config-Keys-Referenz: [Activity-Referenz](../activities-reference).

## Trigger-Konfigurationen

| Trigger | Key-Fields |
|---|---|
| `manualTrigger` | Title, Description, Input-Parameter (name + type string/number/boolean/select + required + default) |
| `scheduleTrigger` | `cronExpression` + 4 Presets, Live-Vorschau der nächsten 5 Feeds (Quartz→cron-parser), Description |
| `webhookTrigger` | HTTP-Methode (POST/PUT/GET), Webhook-Path (template-fähig), optionales Secret (HMAC) |
| `fileWatcherTrigger` | `directory`, `fileFilter` (default `*.*`), `watchType` (created/changed/deleted/renamed/any), `includeSubdirectories` |
| `databaseTrigger` | `connectionRef`, `pollingIntervalSeconds` (default 60, min 5), `query` |
| `eventLogTrigger` | `logName` (Default-Allow: `Application`/`System`; `Security`/`Setup`/andere nur via `Trigger:EventLog:AllowedLogs`), `entryType`, `source`, `eventId`, `lookbackMinutes` (default 5) |

## Execution aus dem Designer

- **Test Run** `Ctrl+Enter` — sofort oder Parameter-Dialog bei `manualTrigger`.
- **Debug Run** (Expert) `Ctrl+Shift+Enter` — `debug: true`, Breakpoints aktiv.
- **Auto-Save vor Run:** ungespeicherte Änderungen werden vorher gespeichert.
- **Cancel Run** `Ctrl+Shift+X`.
- **Canvas Pinning:** gestartete Execution wird gepinnt → Live-Coloring; via Snapshot auch nach 30 s SignalR-TTL erhalten.

## Step Test & Simulation

- **Step Test Panel:** einzelnen Step isoliert testen (`POST /workflows/{id}/steps/{stepId}/test` mit `configOverride`). Modi (Expert): Empty / Last Run / Pick Run / Manual Mocks.
- **Expression Tester (Expert):** Template-Expressions ad-hoc mit Mock-Variablen testen.
- **Simulation / Dry-Run (Expert)** `Ctrl+Shift+R` — topologische Pfad-Analyse über aktive Edges (nur Control-Flow, keine echte Ausführung); zeigt Reihenfolge, erreichbare und übersprungene Nodes.

## Lint & Pre-Publish

- **Lint Panel** `Ctrl+Shift+L` — alle Errors/Warnings mit Code, Target-Node/Edge, Click-to-Jump. Lint läuft live bei jeder Graph-Änderung.
- **Errors (block Publish):** `no-trigger`, `isolated-node`, `dup-output-variable`, `duplicate-edge`, `missing-required-config`, `missing-target-machine`.
- **Warnings:** `orphan-root`, `unreachable-node`, `unknown-template-ref`, `startjob-in-runspace`, `unknown-workflow-ref`, `edge-to-disabled`, `disabled-with-downstream`, `edge-occluded`, `edge-crowded`.
- **Pre-Publish Checklist:** bei Errors Publish blockiert, bei Warnings "Publish anyway", bei Clean direkt.

## Live Monitoring

Live Execution Panel mit Tabs: **Live**, **History**, **Output**, **Watch** (Expert) — via SignalR (`/hubs/execution`, Events: StepStarted, StepCompleted, StepPaused, StepResumed, ExecutionStatusChanged, LiveEventsBatch).

- **Live Tab:** virtualisierte Liste aktiver Runs + Step-Inspector (Config, Output/Error/Transcript, Timing).
- **Live Console:** chronologischer Stream (stdout/stderr/transcript), bis 1000 Lines, Filter, "Errors only", Auto-Scroll.
- **Timeline & Gantt:** Step-Balken nach Status, wachsende Running-Balken (250 ms Ticker), Scrubber für Replay.
- **History Tab:** virtualisierte, sortierbare Tabelle (Status, ID, Trigger, User, Steps, Failed-Step, Timing, Error), Scope-Toggle (aktueller Workflow vs. alle).
- **Output Tab:** Datenbus-Browser, gruppiert (trigger/global/step-outputs/other), Badges (OUT/ERR/PAR/TRG/GLB).
- **Watch Tab (Expert):** beliebige `{{expression}}` mit Live-Auflösung, persistiert per Workflow in localStorage.

## Step-Debugger

- **Breakpoints:** `B` (Expert) oder Kontextmenü; optional conditional.
- **Auto-Pause Detection:** bei pausiertem Step öffnet sich der Live-Tab automatisch.
- **Paused Variables Inspector:** fullscreen — drei Resume-Modi **Continue** / **Step Over** / **Stop** → `POST /executions/{id}/resume`. Variable-Inspection & Override (Globals/Manual/Step/Other).

## Persistenz & Export

- **Dirty State:** jede strukturelle/property-Änderung markiert "dirty".
- **Auto-Save:** 5 s Debounce; Runtime-Fields werden vor dem Serialisieren entfernt.
- **Manual Save** `Ctrl+S`.
- **Dirty Protection:** `beforeunload`-Warnung + Navigation-Blocker.
- **Version History:** Update/Rollback snapshotten die vorherige Definition.
- **Export:** **JSON** `Ctrl+Shift+J` und **PNG** `Ctrl+Alt+P` (WYSIWYG, DPI ≥ 2).

## Mobile View

`MobileWorkflowView` — read-only-Graph für Smartphones/Portrait-Tablets: reused Node/Edge-Typen, `draggable:false`/`selectable:false`, Live-Status via SignalR, vergrößerter Node-Scale, Zoom 0.7–1.6, Pan/Pinch-Zoom statt Edit.

## Tastenkürzel (Referenz)

### Global / Standard

| Key | Action |
|---|---|
| `?` | Help / Shortcut-Referenz |
| `Esc` | Overlays schließen |
| `Home` | Fit all |
| `F11` | Fullscreen |
| `Ctrl+P` | Quick Switcher |
| `Ctrl+Shift+P` | Command Palette |
| `Ctrl+S` | Save |
| `Ctrl+Shift+S` | Publish / Enable / Disable |
| `Ctrl+F` | Search |
| `Ctrl+C` / `Ctrl+V` | Copy / Paste |
| `Ctrl+D` | Duplicate |
| `Ctrl+Z` / `Ctrl+Y` / `Ctrl+Shift+Z` | Undo / Redo |
| `Ctrl+A` | Select All |
| `Ctrl+E` / `Ctrl+U` | Lock / Unlock |
| `Ctrl+Enter` / `Ctrl+Shift+Enter` | Test Run / Debug Run |
| `Delete` / `Backspace` | Delete |

### Expert-Modus (zusätzlich)

| Key | Action |
|---|---|
| `A` / `R` / `M` / `H` / `C` / `G` | Edge-Animation / Routing / Machine-Coloring / Heatmap / Critical Path / Snap-Grid |
| `D` / `B` | Disabled / Breakpoint der Selektion toggeln |
| `Tab` / `Shift+Tab` | Next/previous verbundener Node |
| Arrow keys (`+Shift`) | Nudge (10 px / 1 px) |
| `Ctrl+G` | Group |
| `Ctrl+H` | Find & Replace |
| `Ctrl+Shift+E` | Zoom to Selection |
| `Ctrl+Shift+U` | Force-Unlock (Admin) |
| `Ctrl+Shift+X` | Cancel Run |
| `Ctrl+Shift+T` / `Ctrl+Shift+O` | Tidy / Restore Layout |
| `Ctrl+Shift+L` | Lint Panel |
| `Ctrl+Shift+D` | Diff |
| `Ctrl+Shift+R` | Simulation |
| `Ctrl+Shift+N` | Node-Style (Classic/Card) |
| `Ctrl+Alt+X` | Activity-Type-Filter löschen |
| `Ctrl+]` / `Ctrl+[` | Edge-Breite +/− |
| `Ctrl+Shift+>` / `Ctrl+Shift+<` | Node-Größe +/− |
| `Ctrl+Alt+.` / `Ctrl+Alt+,` | Label-Font +/− |
| `Ctrl+Shift+J` / `Ctrl+Alt+P` | Export JSON / PNG |
| `Ctrl+Shift+1…5` | Navigate: Workflows / Executions / Machines / Globals / Audit |

**Canvas-Gesten:** Left-drag = Marquee-Selektion · middle/right-drag = Pan · `Shift+Click` = Selektion erweitern.