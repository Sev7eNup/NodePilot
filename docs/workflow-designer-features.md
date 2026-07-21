# Workflow-Designer — Feature-Übersicht

Vollständige Auflistung sämtlicher Features des NodePilot-Workflow-Designers (React-SPA, `src/nodepilot-ui/src/components/designer/` + `pages/WorkflowEditorPage.tsx`). Gegliedert nach Funktionsbereich. Quelle: Code-Stand 2026-07-02.

**Zwei Bedien-Modi:** Der Designer kennt einen **Standard-Modus** (reduzierter Funktionsumfang, einsteigerfreundlich) und einen **Expert-Modus** (volle Toolbar, alle Overlays, Einzeltasten-Shortcuts). Features, die nur im Expert-Modus sichtbar sind, sind unten mit **(Expert)** markiert.

---

## Inhalt

1. [Canvas & Viewport](#1-canvas--viewport)
2. [Node-Operationen](#2-node-operationen)
3. [Node-Typen](#3-node-typen)
4. [Activity-Library / Palette](#4-activity-library--palette)
5. [Edges / Verbindungen](#5-edges--verbindungen)
6. [Edge-Bedingungen](#6-edge-bedingungen)
7. [Properties-Panel](#7-properties-panel)
8. [Variablen-System & Autocomplete](#8-variablen-system--autocomplete)
9. [Activity-Konfigurationen](#9-activity-konfigurationen)
10. [Trigger-Konfigurationen](#10-trigger-konfigurationen)
11. [Ausführung aus dem Designer](#11-ausführung-aus-dem-designer)
12. [Live-Monitoring](#12-live-monitoring)
13. [Step-Debugger](#13-step-debugger)
14. [Step-Test & Simulation](#14-step-test--simulation)
15. [Overlays & Produktivitäts-Werkzeuge](#15-overlays--produktivitäts-werkzeuge)
16. [Lint & Pre-Publish](#16-lint--pre-publish)
17. [Editor-Chrome (Header, Banner, Sidebar)](#17-editor-chrome)
18. [Edit-Lifecycle (Lock / Publish)](#18-edit-lifecycle-lock--publish)
19. [Persistierung & Export](#19-persistierung--export)
20. [Visuelle Overlays & Darstellungsoptionen (designStore)](#20-visuelle-overlays--darstellungsoptionen)
    - [20a. Live- & Annotations-Infrastruktur](#20a-live--annotations-infrastruktur)
21. [Mobile-Ansicht](#21-mobile-ansicht)
22. [Keyboard-Shortcuts (Referenz)](#22-keyboard-shortcuts-referenz)

---

## 1. Canvas & Viewport

- **Pan & Zoom:** Verschieben via mittlerer/rechter Maustaste; Zoom bis minimal `0.15×`. Mausrad-Zoom über React Flow.
- **Fit-View:** `Home` passt alle Nodes mit 15 % Padding ein (300 ms Animation); automatisches Fit beim ersten Laden (nicht bei Lock/Unlock/Publish-Refetches).
- **Zoom to Selection (Expert):** `Ctrl+Shift+E` zoomt auf die selektierten Nodes (20 % Padding).
- **MiniMap:** Pan-/zoombar, Node-Farbe nach Live-Status (Running=Amber, Paused=Orange, Succeeded=Grün, Failed=Rot, Skipped=Grau), Fallback auf letzte historische Health bzw. Activity-Typ-Farbe.
- **Controls-Panel:** Standard React-Flow-Controls (Zoom in/out, Fit, Lock).
- **Hintergrund-Varianten:** Klassisches Punkt-Raster (20 px) oder **Premium-Canvas** mit zweistufigem Crosshatch (80 px Major + 16 px Minor); Snap-Grid-Variante.
- **Snap-to-Grid (Expert):** `G` togglet Raster-Snapping (Standard 20 px, konfigurierbar).
- **Node-Skalierung:** 8 Größen-Presets (XS … 4XL, Default SM); `Ctrl+Shift+>` / `Ctrl+Shift+<` (Expert) ändern Node-Größe, `Ctrl+Alt+.` / `Ctrl+Alt+,` die Label-Schriftgröße (nur Classic-Node-Stil).
- **Fullscreen / Distraction-Free:** `F11` blendet Sidebar, Panels und Banner aus, behält Header + Canvas + Exit-Pill.
- **Viewport-Virtualisierung:** Nur sichtbare Elemente werden gerendert (`onlyRenderVisibleElements`) — spürbar ab ~50 Nodes.

## 2. Node-Operationen

- **Hinzufügen:**
  - Drag & Drop aus der Library (MIME `application/nodepilot-activity`) an Cursor-Position — mit Smart-Defaults (letzte Machine/Credential desselben Typs werden übernommen).
  - Quick-Connect: Edge ins Leere ziehen öffnet Activity-Picker und legt Node + Edge an.
  - Doppelklick / Command-Palette / Toolbar.
- **Verschieben:** Drag (nur bei Schreibrecht); Pfeiltasten-Nudge (Expert): 10 px, mit `Shift` 1 px.
- **Multi-Select:** `Shift/Ctrl + Klick` (Toggle) sowie Marquee-Auswahl (Linksziehen auf Canvas, Partial-Touch reicht). `Ctrl+A` selektiert alles.
- **Duplizieren:** `Ctrl+D` (Copy+Paste) oder Kontextmenü, Versatz +40/+40 px, frische IDs.
- **Löschen:** `Delete`/`Backspace`; bei Gruppen werden die Kinder reparentet und bleiben erhalten.
- **Gruppieren (Expert):** `Ctrl+G` packt die Auswahl in eine Group-Node.
- **Reparenting:** Nodes lassen sich per Drag in Group-Nodes hineinziehen (Live-Highlight der Ziel-Gruppe).
- **Clipboard:** `Ctrl+C` / `Ctrl+V` über `sessionStorage` — **funktioniert workflow-übergreifend**; IDs werden beim Einfügen neu vergeben, Edges/Parent-Referenzen remapped, Versatz 40 px × Paste-Zähler.
- **Auto-Layout / Tidy:** `Ctrl+Shift+T`. Standard-Modus immer LR; Expert cyclet LR → TB → Compact → ELK. **Restore Original Layout** (Expert) `Ctrl+Shift+O` stellt das Ausgangs-Layout wieder her.
- **Undo/Redo:** `Ctrl+Z` / `Ctrl+Y` (auch `Ctrl+Shift+Z`). History-Tiefe 50 Snapshots; strukturelle Änderungen sofort, Property-Edits mit 800 ms Debounce gebündelt; wird beim Workflow-Wechsel geleert.
- **Bulk-Edit:** Bei ≥ 2 selektierten Nodes erscheint das Bulk-Edit-Panel (Ziel-Maschine, Enable/Disable, Timeout, Retry-Policy — jeweils mit eigenem Apply-Button).

## 3. Node-Typen

### Activity-Node
- **Shapes nach Kategorie:** Pentagon-Bookend-Paar (Trigger `◁` + returnData `▷`, horizontal gespiegelt); **jede Action-Activity eine eigene Clip-Path-Silhouette** (Icon bleibt zentriert/unclipped); Control-Flow je eigene Form (decision=Raute, junction=hexLong, forEach=reel, startWorkflow=tagLeft) mit **indigo Group-Frame** — via Clip-Path.
- **Darstellungs-Stile:** **Classic** (kompakt, Icon-zentriert) und **Card/MD3** (Header + Config-Summary). Umschaltbar mit `Ctrl+Shift+N`.
- **Farben & Icon:** Pro Activity-Typ aus CSS-Variablen (`--act-<type>-color/-bg/-border`) + Material-Symbol-Icon.
- **Handles/Ports:** Standard nur Links (In) / Rechts (Out); im Flexible-Ports-Modus alle vier Seiten bidirektional.
- **Status-Badges:**
  - **Disabled** (durchgestrichenes Auge) — Node wird übersprungen, bleibt editierbar (50 % Opacity, gestrichelt).
  - **Breakpoint** (roter Punkt; Amber bei conditional) — pulsiert, wenn der Debugger dort wartet.
  - **Fan-In** (eingehende Kanten-Zahl) farbcodiert nach Junction-Modus (waitAll/waitAny/waitNofM/auto).
  - **Lint** (rot=Error, amber=Warning, Zähler 9+).
  - **Simulation** („Will run" / „Skipped").
- **Live-Status-Overlay:** Running (Amber, pulsierender Ring), Succeeded (Grün), Failed (Rot), Skipped (Grau, gestrichelt), Paused (Dunkelorange, pulsierend).
- **Heatmaps & Pfade:** Failure-Tint (rot, skaliert nach Fehlerrate), Critical-Path-Glow (orange) + Slack-Badge, Coverage-Ausgrauung (never/rare).
- **Schedule-Preview:** Für `scheduleTrigger` Anzeige des nächsten Fire-Zeitpunkts (client-seitig via cron-parser), „⏸ Paused" wenn Workflow disabled.
- **Health-Sparkline / Stats:** Letzte bis zu 8 Outcomes als Dots; Performance-Stats (Runs, Fehlerrate, avg/p95-Dauer) im Hover-Tooltip (400 ms Delay).
- **Output-Variable & Beschreibung:** Im Tooltip `→ {{var.output}}`; Beschreibungs-Indikator unten rechts.
- **Variable-Flow-Highlight:** Producer/Consumer-Ringe beim Hover über Variablen.

### Group-Node
- Visuelle Gruppierung (keine Ausführung); Kinder bewegen sich mit.
- **Resize** (NodeResizer, min 200×120), **5 Farben** (blue/green/amber/rose/slate), **Collapse/Expand** mit Kinder-Zähler-Badge, **Inline-Label-Edit** (Doppelklick), Drop-Target-Highlight.

### Sticky-Note-Node
- Reine Annotation (keine Handles, wird übersprungen).
- **Resize** (min 140×50), **Inline-Edit** (Doppelklick, Commit per Blur/Ctrl+Enter), **5 Schriftgrößen-Presets** (11–28 px), Hover-Scale bei weit herausgezoomtem Canvas, gelbe „Haftnotiz"-Optik mit leichter Rotation.

## 4. Activity-Library / Palette

- **Kategorien** (`buildActivityCategories`): **Triggers**, **Actions**, **Control Flow**, **Logic**, **Annotations** (Sticky-Note). Items alphabetisch nach Label sortiert; Kategorien kollabierbar.
- **Suche:** Filtert Activities + Snippets nach Name/Typ (case-insensitive); erzwingt Expand bei aktiver Suche.
- **Drag & Drop / Klick** zum Hinzufügen (nur bei Schreibrecht; sonst 50 % Opacity).
- **Snippets-Sektion:** Vordefinierte Mini-Patterns (Try-Catch, ForEach, Fan-out+Join, HTTP-Retry) — fügen komplette Gruppe mit frischen IDs ein.
- **Activity-Picker-Grid:** 2-spaltiges Popup-Grid (Quick-Connect / Edge-Inserter) — ohne Triggers/Annotations.
- **Activity-Type-Filter (Toolbar):** Blendet Activity-Typen auf dem Canvas aus (Checkbox je vorkommendem Typ, nach Häufigkeit sortiert, „Clear All", Badge mit Filter-Zahl). `Ctrl+Alt+X` (Expert) löscht den Filter.

## 5. Edges / Verbindungen

- **Edge-Typ:** Custom `LabeledEdge` mit Pfeil-Marker.
- **Semantische Farbcodierung (Status-Tokens, dark-fähig):** Success=Grün, Failed=Rot, Custom-Condition=Indigo (Stroke, Pfeil und Label-Pill teilen dieselbe Farbe), Always=Grau, Disabled=gestrichelt/gedimmt — gestrichelt ist NUR noch disabled; idle Kanten sind solid.
- **Labels:** Manuell (bis 60 Zeichen im Graph) oder Auto-Label aus Bedingung („On Success" / „On Failure" / „Always"); kanonische Labels syncen automatisch, manuelle bleiben erhalten.
- **Routing-Modi:** `smart` (Bezier 0.25), `curved` (0.5), `straight` (Step mit Radius). `R` (Expert) cyclet die Modi.
- **Manuelles Umbiegen:** Zwei draggbare Bezier-Control-Points (Reshape-Handles, nur bei Auswahl + Schreibrecht), optional mit Snap-to-Grid; „Reset Shape" stellt Auto-Routing wieder her.
- **Rückwärtskanten:** Automatische U-Form unter den Nodes bei Right→Left-Verlauf.
- **Edge-Breite/Animation:** `Ctrl+]` / `Ctrl+[` (Breite), `A` (Expert, Fluss-Animation). Der animierte Fluss-Dash läuft nur noch während einer Live-Execution auf der Kante zum gerade laufenden Step — idle Kanten bleiben ruhig/solid.
- **Inline-Insert:** ⊕-Button auf der Kante fügt einen Node zwischen A→B ein (A→NEW→B); erste Hälfte bedingungslos, zweite erbt Label/Bedingung.
- **Quick-Connect-Picker:** Edge ins Leere ziehen → Activity-Picker an Cursor; legt Node + verbundene Edge an.
- **Data-Flow-Overlay:** Chips zeigen, welche Variablen über eine Kante fließen.
- **Kontextmenü (Rechtsklick):** Enable/Disable, Swap Source↔Target (Expert), Reset Shape (Expert), Delete.
- **Validierung:** Duplikat-Verbindungen werden verhindert (Toast-Hinweis).
- **Edge-Properties-Panel:** Source→Target-Info, Port-Selektor (Expert + Flexible Ports), Label-Feld mit „Use Auto"-Reset, Simple-/Expression-Bedingung, Disabled-Toggle, Delete (mit Bestätigung).

## 6. Edge-Bedingungen

- **Simple-Modus:** Buttons „On Success" / „On Failure" / „Always" + Freitext-Bedingung (`stepId.success` etc.).
- **Expression-Builder (Expert):** Visueller AST-Editor.
  - **Vergleichsoperatoren:** `==`, `!=`, `<`, `>`, `<=`, `>=`, `contains`, `startsWith`, `endsWith`, `matches` (Regex).
  - **Unäre:** `isEmpty`, `isNotEmpty`, `isTrue`, `isFalse`.
  - **Verknüpfung:** AND/OR-Gruppen (verschachtelbar), NOT-Wrapper.
  - **Operanden:** Variable (Step `output`/`error`/`success`/`param.X`, Global, Manual) oder Literal (mit `{{globals.X}}`-Support).
- **Disabled-Edge:** wird übersprungen (Ziel-Node nicht als Root), ⊘-Badge.

## 7. Properties-Panel

- **Sektionen:** Execution Context, Input Variables, Configuration, Timeout, Test & Debug — kollabierbar mit Variablen-Zähler.
- **Header:** Activity-Icon, Inline-Edit für Name/Beschreibung, Close.
- **Gemeinsame Node-Felder:**
  - **Label** + **Output-Variable** (Fallback Step-ID; downstream `{{var.output}}`).
  - **Beschreibung** (Lazy „+ Add description").
  - **Target Machine** & **Credential** (für Remote-Activities) als `DynamicTargetField` — GUID/Variable/Literal, mit Resolved-Label und Test-Connection-Button.
  - **Timeout** (`config.timeoutSeconds`, nur für Timeout-fähige Typen).
  - **Disable-Toggle** und **Breakpoint** (+ optionale Breakpoint-Bedingung, Expert).
- **Read-Only-Modus:** Bei fehlendem Schreibrecht wird das gesamte Fieldset deaktiviert.
- **Clone-Config-Button (Expert):** Übernimmt Konfiguration von einem Step gleichen Typs — entweder komplett oder nur Machine+Credential (kopiert nicht Label/Output/Body).
- **Externe-Trigger-Hinweis:** Grüne Info-Box bei Schedule/Webhook/FileWatcher/Database/EventLog („Active …, Workflow muss enabled sein").

## 8. Variablen-System & Autocomplete

- **Inline-Autocomplete:** Tippen von `{{` öffnet ein Dropdown (`useVariableAutocomplete`); Tastatur-Navigation (↑/↓, Enter/Tab wählt, Esc schließt). Per-Field via Zap-Toggle ein-/ausschaltbar (in localStorage gespiegelt).
- **Variablen-Quellen:** Upstream-Outputs aller Vorgänger-Steps, Globals (`{{globals.NAME}}`), Manual-/Trigger-Daten (`{{manual.NAME}}`).
- **Tail-Typen:** `.output`, `.error`, `.success`, `.param.X`.
- **Abgeleitete Outputs (`describeNodeOutputs`):** jeder Step liefert `.output`; zusätzliche `.param.*` werden je Typ abgeleitet — `runScript` per Regex aus `$var = …`-Zuweisungen, `returnData` aus den data-Keys, `decision` → `param.case`, `wmiQuery` aus captureProperties (+ `param.count`), `registryOperation` operationsabhängig (z. B. value/exists/created), `manualTrigger` aus den definierten Parametern.
- **VariableInsertField:** Einheitliches Eingabefeld mit Picker-Tray (Variable-Picker, Global-Picker mit Secret-Lock, Options-Picker), Drag&Drop von Variablen in Textareas, Template-Validierung (ungepaarte `{{}}`, ungültige Bezeichner), SQL-Injection-Warnung bei dynamischen Queries.
- **Live-Preview-Tooltip:** Hover über eine Variable zeigt den letzten Runtime-Wert aus der jüngsten Ausführung (250 ms Delay, „[Truncated]"-Badge, rechts verankert).
- **AvailableVariablesList** (Input-Variables-Sektion): nach Step gruppiert, Type-Badges, Click-to-Copy, Drag&Drop.
- **ParameterTable:** Key/Value-Tabelle für Trigger-Parameter & freie Child-Workflow-Parameter.
- **ContractMappingTable:** Typisiertes Mapping für Sub-Workflows mit bekanntem Contract — Inputs (Typ-Badge, Required-Stern, Default-Hinweis, Validierung), Stale-Key-Warnung, Read-only-Outputs-Liste.
- **JsonPathTree:** Interaktive Auswahl von JSONPath aus dem letzten Step-Output (Click-Copy, bis Tiefe 7).

## 9. Activity-Konfigurationen

Eigene Config-Komponente je Activity-Typ (`properties/activities/`, registriert in `activityConfigMap.ts`). Viele Felder sind **conditional** — sie erscheinen abhängig von der gewählten Operation/Action. Pflichtfelder kommen aus `lib/activityConfigFacts.ts` (speist auch die Lint-Regel `missing-required-config`).

| Activity | Konfigurationsfelder (mit echten Options-Werten) |
|---|---|
| `runScript` | **engine** (auto/pwsh/powershell), **Erfolg bei Exit-Codes** (`successExitCodes`, leer = nur fehler-basiert), **transcript** (Auto-Logging-Checkbox), **Prozess-Isolation** (Checkbox, nur lokal — eigener Prozess im Windows Job Object; ausgegraut bei Remote-Ziel) + optionale Caps **Speicherlimit (MB)** / **Max. Prozesse**, **script** (PowerShell-CodeField, min 12 Zeilen) + Fullscreen-Monaco-Editor mit KI-Generieren & Step-Test |
| `fileOperation` | **operation** (copy/move/rename/delete/exists/create), **path**, **destination** (bei copy/move), **newName** (bei rename) |
| `folderOperation` | **operation** (copy/move/rename/delete/exists/list/create), **path**, **destination** (bei copy/move), **newName** (bei rename) |
| `fileHash` | **path**, **algorithm** (SHA256/SHA1/MD5/SHA384/SHA512), **expected** (optionaler Soll-Hash) |
| `zipOperation` | **operation** (compress/extract), **source**, **destination**, **compressionLevel** (Optimal/Fastest/NoCompression, nur compress), **force** |
| `serviceManagement` | **serviceName**, **action** (status/start/stop/restart/create/delete/setStartType); bei create: binaryPath, displayName, description, **startupType** (Automatic/AutomaticDelayedStart/Manual/Disabled) |
| `scheduledTask` | **taskName**, **taskPath**, **action** (get/start/stop/enable/disable/register/unregister); bei register: program, arguments, workingDirectory, **triggerType** (once/daily/weekly/atLogon/atStartup), startTime, daysInterval/weeksInterval, daysOfWeek (Mo–So-Toggles), runAsUser, **runLevel** (limited/highest), force |
| `registryOperation` | **operation** (read/write/deleteValue/deleteKey/createKey/exists/listSubKeys/listValues), **keyPath**, **valueName**, **valueType** (String/ExpandString/Binary/DWord/MultiString/QWord, bei write), **value** (bei write) |
| `wmiQuery` | **mode** (query/wql/invokeMethod), **className**, **namespace** (Std. `root\cimv2`), **filter** (WHERE), **query** (WQL), **methodName** + **arguments** (invokeMethod), **captureProperties** (Property-Liste) |
| `startProgram` | **filePath**, **arguments**, **workingDirectory**, **useShellExecute**, **waitForExit**, **successExitCodes** (bei waitForExit) |
| `powerManagement` | **action** (shutdown/restart/logoff/abort/hibernate), **delaySeconds**, **force**, **message** (bei shutdown/restart) |
| `waitForCondition` | **conditionType** (script/pathExists/serviceRunning/portOpen/httpOk) mit jeweils eigenen Feldern (script + Snippet-Buttons / path / serviceName / host+port / url), **intervalSeconds** |
| `restApi` | **method** (GET/POST/PUT/PATCH/DELETE/HEAD), **url**, **headers** (Key: Value je Zeile), **body** (JSON-CodeField, bei POST/PUT/PATCH), kollabierbare Proxy-Sektion: **proxyMode** (default/custom/direct), proxyAddress, noProxy |
| `sql` | **provider** (sqlserver/sqlite/postgres), Connection-Modus **Builder ↔ Connection String** (Builder-Felder je Provider: server/database/auth/encrypt … bzw. host/port/sslMode … bzw. dataSource), **query** (SQL-CodeField) |
| `emailNotification` | **to**, **subject**, **body** (multiline), **isHtml** (Checkbox) |
| `textFileEdit` | **operation** (append/prepend/insert/replaceLine/delete/replace), **path**, **content**, **lineNumber**, Delete-Sub-Modus (line/range/pattern), matchPattern, replace, useRegex, ignoreCase, occurrences (all/first), encoding (auto/utf8/utf8-bom/utf16le/utf16be/ascii), lineEnding (preserve/crlf/lf), backupSuffix, createIfMissing, dryRun |
| `delay` | **seconds** (Number, min 1) |
| `generateText` | **Zufallstext-Generator** — **mode** (alphanumeric/alphabetic/numeric/hex/guid/password/custom), **length** (außer guid), **customCharset** (bei custom), **excludeAmbiguous** |
| `llmQuery` | **LLM-Abfrage** — OpenAI-kompatibler Endpunkt (Prompt→Text). **prompt** (Pflicht, `{{templates}}`), **systemPrompt**, **jsonMode**; Override-Abschnitt: **baseUrl**, **model**, **apiKey** (Secret), **maxTokens**, **temperature** (0–2), Timeout-Feld auto. Leer → globale `Llm:*`-Config; braucht `Llm:Enabled=true` |
| `xmlQuery` | **source** (inline/file), content (XML-CodeField) bzw. path, **xpath**, **resultMode** (single/all), **namespaces** (JSON-Mapping) |
| `jsonQuery` | **source** (inline/file), content (JSON-CodeField) bzw. path, **jsonPath** + JSONPath-Picker, **resultMode** (single/all) |
| `log` | **level** (info/warning/error), **message** (multiline) |
| `junction` | **mode** (waitAll/waitAny/waitNofM), **requiredCount** (bei waitNofM) — steuert das Fan-In-Verhalten |
| `forEach` | **items** (multiline), **itemsFormat** (auto/json/lines), **childWorkflowNameOrId** + Picker, **itemParameterName** (Std. „item"), **indexParameterName** (Std. „index"), **maxParallelism** (0–64), **timeoutSecondsPerItem**, **continueOnError**, zusätzliche **parameters** (ParameterTable) |
| `decision` | **cases** (Name + ConditionBuilder, sortierbar via Up/Down, First-Match → `param.case`), **defaultCaseName** |
| `returnData` | **data** (frei erweiterbare Key/Value-Tabelle) |
| `startWorkflow` | Workflow-Picker + Vorschau-Button, **workflowNameOrId**, **waitForCompletion**; Parameter via ContractMappingTable (bei bekanntem Contract) sonst ParameterTable; Timeout bei Sync-Modus |

> Alle Remote-Activities zeigen zusätzlich Target-Machine, Credential, Test-Connection und ggf. Timeout.
> Inline-Code-Felder nutzen **CodeMirror** (Sprachen: powershell/sql/json/xml/plain) mit `{{`-Autocomplete; nur der **Fullscreen-Script-Editor** ist Monaco.

## 10. Trigger-Konfigurationen

| Trigger | Wichtige Felder (echte Options-Werte) |
|---|---|
| `manualTrigger` | **Title**, **Description**, **Input-Parameter** (Name + **Typ** string/number/boolean/select + Required + Default); Live-Hinweis zeigt die Zugriffs-Syntax |
| `scheduleTrigger` | **cronExpression** + 4 Presets (`0 */5 * * * ?`, `0 0 * * * ?`, `0 0 6 * * ?`, `0 0 8 ? * MON-FRI`), **Live-Vorschau der nächsten 5 Fire-Zeiten** (client-seitig, Quartz→cron-parser normalisiert, relative Zeiten, rote Parse-Fehler), Description |
| `webhookTrigger` | **HTTP-Methode** (POST/PUT/GET), **Webhook-Pfad** (Template-fähig), optionales **Secret** (Passwort-Feld, HMAC) |
| `fileWatcherTrigger` | **directory**, **fileFilter** (Std. `*.*`, komma-separierte Globs), **watchType** (created/changed/deleted/renamed/any), **includeSubdirectories** |
| `databaseTrigger` | **connectionRef** (Verweis auf `Trigger:Database:Connections`), **pollingIntervalSeconds** (Std. 60, min 5), **query** (multiline, SQL-Template-Warnung bei `{{`) |
| `eventLogTrigger` | **logName** (Application/System/Security/Setup), **entryType** (any/Error/Warning/Information/SuccessAudit/FailureAudit), **source** (optional), **eventId** (optional), **lookbackMinutes** (Std. 5) |

> Trigger-Daten landen im Lauf als `manual.*`-Variablen (`{{manual.<name>}}`) bzw. als `param.*` des Trigger-Nodes — es gibt **keinen** `trigger.*`-Namespace.

## 11. Ausführung aus dem Designer

- **Test Run** (`Ctrl+Enter`): startet sofort oder öffnet Parameter-Dialog bei `manualTrigger` mit Parametern (Prefill der zuletzt genutzten Werte).
- **Debug Run** (Expert, `Ctrl+Shift+Enter`): Ausführung mit `debug: true` → Breakpoints aktiv.
- **Auto-Save vor Run:** ungespeicherte Änderungen werden bei Schreibrecht zuvor gespeichert.
- **Cancel Run** (`Ctrl+Shift+X`): bricht laufende Ausführung ab.
- **Canvas-Pinning:** gestartete Execution wird gepinnt → Live-Färbung der Nodes/Pfade; bleibt per Snapshot auch nach 30 s SignalR-TTL erhalten.

## 12. Live-Monitoring

- **Live-Execution-Panel mit Tabs:** **Live**, **History**, **Output**, **Watch** (Expert) — getrieben von SignalR-Live-Updates.
- **Live-Tab:** virtualisierte Liste aktiver Läufe (Status-Badge, Done/Total, Failed-Counter, Paused-Pulse) + Step-Inspector (Config, **Output parameters** aus dem Live-Databus (`param.*`-Entries des selektierten Steps), Output/Error/Transcript, Start/End, Offset, Dauer); beide Panes resizable.
- **Live-Console:** chronologischer Stream (stdout/stderr/transcript), bis 1000 Zeilen, Text-Filter, „Errors only"-Toggle, Auto-Scroll, Click-to-Inspect, Zeilen-Prefix `+Xms · Step · Text`.
- **Stats-Strip:** Steps-Fortschritt, Succeeded/Failed/Running-Counts, verstrichene Zeit, **ETA** (aus Median historischer Läufe).
- **Live-Timeline & Gantt-Chart:** Step-Balken farbig nach Status, Running-Balken wachsen mit 250-ms-Ticker; geteilt zwischen Live & History; List-/Gantt-Umschaltung; Gantt mit **Scrubber** (Replay an Zeitpunkt).
- **History-Tab:** virtualisierte, sortierbare Tabelle (Status, ID, Trigger, User, Steps, Failed-Step, Start/Ende, Dauer, Error, Extras) mit Scope-Toggle (aktueller Workflow vs. alle); Drilldown in StepTimeline (List/Gantt) — erweiterter Step-Detail zeigt Config, **Output parameters** (`outputParametersJson`, persistierte `param.*`-Entries), Output/Error/Transcript; Gap-Anzeige (`∥` für parallel), Replay-Toggle.
- **Output-Tab:** Databus-Browser, gruppiert (Trigger/Global/Step-Outputs/Other), farbige Badges (OUT/ERR/PAR/TRG/GLB), Filter mit Auto-Expand, Expand/Collapse-All, Copy-Button.
- **Watch-Tab (Expert):** beliebige `{{expression}}` mit Rechtsklick-Variablen-Picker, Live-Auflösung gegen den Databus, persistiert pro Workflow in localStorage.

## 13. Step-Debugger

- **Breakpoints:** `B` (Expert) oder Kontextmenü; optional conditional.
- **Auto-Pause-Detection:** bei pausiertem Step öffnet sich automatisch der Live-Tab.
- **Paused-Variables-Inspector:** Vollbild-Inspektor im Pause-Zustand — Header zeigt „Paused at: Step", Grund (breakpoint/stepOver) und Zeit.
  - **Drei Resume-Modi:** **Continue** (weiter), **Step Over** (diesen Step überspringen), **Stop** (Execution abbrechen) → `POST /executions/{id}/resume`.
  - **Variablen-Inspektion & Override:** gruppiert (Globals/Manual/Step/Other), Inline-Edit mit Dirty-Highlight, Reset-Button, Overrides werden beim Resume übergeben.

## 14. Step-Test & Simulation

- **Step-Test-Panel:** einzelnen Step isoliert testen (`POST /workflows/{id}/steps/{stepId}/test` mit `configOverride`).
  - **Modi (Expert):** Empty / Last Run / Pick Run (aus 10 jüngsten) / Manual Mocks.
  - **Context-Preview:** `GET …/test-context` zeigt Variablen nach Herkunft; Refresh-Button.
  - **Ergebnis:** Success/Failure mit Output, Error, Output-Parametern, Dauer; JSONPath-Picker bei JSON-Output.
- **Expression-Tester (Expert):** Ad-hoc-Test von Template-Ausdrücken mit Mock-Variablen (Auto-Prefill aus letztem Lauf), grün/gelb je nach Auflösung.
- **Simulation / Dry-Run (Expert):** `Ctrl+Shift+R` — topologische Pfad-Analyse über aktive Edges (nur Kontrollfluss, keine echte Ausführung); zeigt Ausführungsreihenfolge, erreichbare und übersprungene Nodes mit schrittweiser Animation; Banner mit Fortschritt.

## 15. Overlays & Produktivitäts-Werkzeuge

- **Command Palette** (`Ctrl+Shift+P`): Fuzzy-Suche über ~45 Designer-Befehle, gruppiert nach Kategorie, Tastatur-Navigation, Expert-Filterung.
- **Search-Overlay** (`Ctrl+F`): Node-Suche (Label/Typ/ID), Sprung zum Treffer (Center + Select), bis 30 Ergebnisse.
- **Find & Replace** (Expert, `Ctrl+H`): über Node-Labels, Edge-Labels und Config-Werte (Deep-Traverse); Replace-One/Replace-All, Match-Navigation mit Kontext-Snippet.
- **Help-Overlay** (`?`): vollständige Shortcut-Referenz in Kategorien.
- **Node-Kontextmenü (Rechtsklick):** Duplicate, Toggle Disabled, Toggle Breakpoint (Expert), Delete.
- **Quick-Edit-Popup (Doppelklick):** Inline-Bearbeitung des wichtigsten Feldes je Activity-Typ (z. B. Script, URL, Service-Name) — `runScript` öffnet stattdessen den vollen Script-Editor.
- **Script-Editor-Dialog:** Monaco-Editor (PowerShell, eigenes Theme), draggable/resizable oder Fullscreen, Wort-Umbruch, Schriftgröße, Toggle-Comment, Variablen-Sidebar (Upstream + auto-erkannte `$var`-Zuweisungen → `{{step.param.var}}`), Inline-Linting unbekannter `{{…}}`, `{{`-Autocomplete, **KI-Generieren** (Replace/Insert) und **Run Step Test** mit Ergebnis-Panel.
- **Workflow-Quick-Switcher** (`Ctrl+P`): schneller Wechsel zwischen Workflows, Recents (localStorage), gescortes Substring-Matching.
- **Sub-Workflow-Preview:** Klick auf `startWorkflow`-Node öffnet read-only Vorschau (eigene React-Flow-Instanz, Pan/Zoom), optional „Open in Editor".
- **Workflow-Diff** (Expert, `Ctrl+Shift+D`): Versions-Timeline + Diff (Added/Removed/Changed für Nodes & Edges, ID-stabil inkl. Handles/Positionen), Admin-Restore zur ausgewählten Version (`POST …/rollback/{v}`).
- **KI-Workflow-Assistent** (lila Button neben dem Standard/Experte-Toggle): angedocktes Multi-Turn-Chat-Panel, das den aktuellen Workflow erklärt (Markdown) und auf Wunsch komplette Umbauten vorschlägt. Alle Rollen dürfen fragen; nur Admin/Operator können einen Vorschlag übernehmen. Vorschläge werden serverseitig secret-redigiert ans LLM geschickt, per Node-ID aufs Original zurückgemergt (Layout/Secrets/Felder bleiben erhalten), als **Proposal-Karte** (strukturiertes Changelog, selektives Übernehmen pro Änderung, Refine, Undo/Auto-Layout) gezeigt und auf den Canvas übernommen (kein DB-Write — Speichern über den normalen Edit-Lock/Publish-Flow; Stale-Schutz blockt veraltete Vorschläge). Opt-in via `Llm:Enabled`.

## 16. Lint & Pre-Publish

- **Lint-Panel** (`Ctrl+Shift+L`): listet alle Fehler/Warnungen mit Code, Ziel-Node/-Edge und Klick-zu-Sprung. Lint läuft live bei jeder Graph-Änderung; Node-Badge zeigt Zahl.
- **Erkannte Regeln (Auszug):**
  - **Fehler (blockieren Publish):** `no-trigger`, `isolated-node`, `dup-output-variable`, `duplicate-edge`, `missing-required-config`, `missing-target-machine`.
  - **Warnungen:** `orphan-root`, `unreachable-node`, `unknown-template-ref`, `startjob-in-runspace`, `unknown-workflow-ref`, `edge-to-disabled`, `disabled-with-downstream`, `edge-occluded`, `edge-crowded`.
- **Pre-Publish-Checklist-Modal:** vor dem Publish — bei Fehlern blockiert (Publish disabled), bei Warnungen „Trotzdem publizieren", bei sauberem Stand direkt; jede Issue klickbar zum Node/Edge.

## 17. Editor-Chrome

### Header & Toolbar
Sieben Cluster mit proximity-getriebenem Farb-Glow (rein kosmetisch, prefers-reduced-motion-aware):
1. **History:** Undo, Redo.
2. **Layout** (bei Schreibrecht): Tidy (Expert mit Algorithmus-Anzeige), Restore Original Layout (Expert).
3. **Inspect:** Search, Find&Replace (Expert), Zoom-to-Selection (Expert), Diff (Expert), Simulation (Expert), Shortcuts (Expert), Hidden-Types-Pill.
4. **View** (Expert): **„Darstellung"-Einstellungsdialog** (Settings-Icon, `role="dialog"`) — bündelt alle Canvas-Darstellungsoptionen (Node-Stil, Icon-Ansicht, Ports/Auto-Hide, Kanten-Animation/-Routing/-Dicke, Node-/Label-Größe, Premium-Canvas, Snap-Grid) als beschriftete Karten-Zeilen mit Switch/Segment-Control/Stepper; dazu Activity-Type-Filter.
5. **Run:** Test Run, Debug Run (Expert), Cancel, Lint-Pill, sowie das **„Ansicht"-Popover** (Expert, Eye-Icon mit Aktiv-Zähler): bündelt die Overlay-Switches Machine-Coloring/Failure-Heatmap/Data-Flow/Coverage/Critical-Path als Switch-Zeilen.
6. **Lifecycle** (bei Rollen-Schreibrecht): Edit-Lock-Toggle, Save (mit Dirty-Punkt), Publish/Disable/Enable.
7. **Export** (Expert): JSON, PNG. Im Standard-Modus über More-Menu.

Außerhalb der sieben Cluster im Header (rechts neben dem Standard/Experte-Toggle):
- **KI-Workflow-Assistent-Button** (lila, Sparkles-Icon) — öffnet das Chat-Panel; vollständig beschrieben in § 15.
- **Toolbar-Layout-Umschalter** (`Rows3`-Icon, `data-testid="toggle-toolbar-layout"`, links vom Skin-Umschalter, in **beiden** Layouts sichtbar): schaltet die Kopfleiste zwischen dem kompakten Drei-Zonen-Layout (Default; gruppierte View/Overlays/Tools-Popovers, zentrierter Name, grüner „Ausführen"-CTA) und der klassischen Inline-Reihe (jeder Toggle/Tool als einzelner Button, rechtsbündiger Name, icon-only Play). Persistiert im `designStore` (`toolbarLayout`, Default `compact`). Beide Layouts teilen sich `EditorIdentity`/`SkinSwitcher`/`RunControls`/`LifecycleControls`/`StandardMoreMenu` (kein Doppel-Pflege-Drift); der Dispatcher `EditorHeader` liest nur `toolbarLayout` und rendert `CompactEditorHeader` bzw. `ClassicEditorHeader`. Die klassische Reihe wrappt bei schmalen Fenstern ganze Cluster mehrzeilig und behält die Proximity-Glow-Trays.
- **Farb-Skin-Umschalter** (Palette-Icon, icon-only, neben dem KI-Button): öffnet ein Popover mit allen 8 Skins plus `system` aus der `THEMES`-Registry. Auswahl synchronisiert sich mit dem Einstellungs-Picker und der Sidebar (gemeinsamer `useThemeStore`); Aktiv-Skin erhält ein Check-Icon; Außen-Klick schließt das Popover.

### Inline-Workflow-Name
Wächst mit Inhalt; read-only bei fehlendem Schreibrecht.

### Status-Banner (kontextuell)
Replay-Mode, Test-Run-Mode, Viewer-Read-Only, **Locked by Other** (+ Admin-Force-Unlock), **Locked by Me**, Productive/Disabled.

### Breadcrumbs
Pills zu ausgehenden Workflow-Referenzen (`startWorkflow` / `forEach`); gefundene = Link, nicht gefundene = Amber-Warnung; ignoriert dynamische `{{…}}`-Refs.

### Folder-Pfad-Breadcrumb (Canvas, oben links)
Ordner-Pfad des geöffneten Workflows. Jedes RBAC-sichtbare Segment öffnet ein Popover mit Unterordnern (Tieferblättern via Mini-Browser) und Workflows (direkt öffnen) — Navigation durch die Ordnerhierarchie ohne die Canvas zu verlassen. Root-Workflows zeigen keinen Breadcrumb; nicht sichtbare Ancestors bleiben nicht-interaktiver Text. Frontend-only (aus `workflow.folderPath`).

### Sidebar (links)
Zwei Tabs (**Node-Library** / **Workflow-Browser**), kollabierbar auf Icon-Bar, resizable.

### Workflow-Browser
- **View-Toggle:** Folder-View (hierarchischer Shared-Folder-Tree, Drag&Drop in Ordner) vs. Trigger-View (gruppiert nach Trigger-Typ).
- **Suche/Filter**, Status-Dot (enabled/disabled), Activity-Count, Hover-Aktionen (in Start-Workflow einsetzen / öffnen), „current"-Marker.
- **Splitter + Info-Card.**

### Workflow-Info-Card
Metadaten der gehoverten/aktuellen Workflow: Status, Name, Version, Trigger, Steps, Last Run, Success-Rate, avg-Dauer, Modified (Zeit/User), Locked-By, Folder-Pfad.

### Maintenance-Window-Badge
Self-Fetch `…/maintenance-windows/affecting/{id}` — zeigt betreffende Wartungsfenster als Pills („· Active Now"), Aktiv-Indikator.

### Right-Panel
Zeigt je nach Auswahl: Bulk-Edit (≥2 Nodes, Expert), Properties (1 Node), Edge-Properties (1 Edge) oder nichts; resizable.

## 18. Edit-Lifecycle (Lock / Publish)

- **`canWrite = roleCanWrite && isLockedByMe`** — beide Bedingungen nötig.
- **Lock** (`Ctrl+E`): atomar `IsEnabled=false` + Lock setzen; 409 wenn bereits gelockt.
- **Unlock** (`Ctrl+U`): Lock freigeben (`IsEnabled` bleibt).
- **Publish** (`Ctrl+Shift+S`): atomar Save + Enable + Unlock (durch Pre-Publish-Checklist).
- **Disable/Enable:** Kill-Switch (Disable ist **nicht** lock-gegated); Enable nur ohne fremden Lock.
- **Force-Unlock** (Admin, `Ctrl+Shift+U`): bricht fremden Lock (Bestätigung + Audit-Eintrag).
- **Read-Only-Verhalten:** ohne `canWrite` sind Drag, Connect, Delete, Kontextmenüs und Inline-Edits deaktiviert.

## 19. Persistierung & Export

- **Dirty-State:** jede strukturelle/Property-Änderung markiert „dirty".
- **Auto-Save:** 5 s Debounce nach letzter Änderung (bei Schreibrecht); Runtime-Felder (`__liveStatus`, `__health`, `__stats`, …) werden vor dem Serialisieren entfernt.
- **Manual Save:** `Ctrl+S`; Save-before-Run awaitable.
- **Dirty-Schutz:** `beforeunload`-Warnung + Navigations-Blocker („Discard changes?") bei ungespeicherten Änderungen.
- **Version-History:** Update/Rollback snapshotten die vorherige Definition.
- **Export:** **JSON** (`Ctrl+Shift+J`, `{name}.workflow.json` via API) und **PNG** (`Ctrl+Alt+P`, WYSIWYG-Viewport ohne Controls/MiniMap, DPI ≥ 2).

## 20. Visuelle Overlays & Darstellungsoptionen

Alle Darstellungs-Einstellungen liegen im **`designStore`** (Zustand + persist, Key `nodepilot-design`) und gelten editor-weit:

| Setting | Default | Werte / Bereich | Wirkung |
|---|---|---|---|
| `designerMode` | `standard` | standard / expert | Schaltet erweiterte Toolbar/Overlays/Shortcuts frei |
| `designerTheme` | `atelier` | atelier / classic | Designsprache des Designers: **Atelier** (eigener Werkbank-Look — Papier/Graphit-Grund mit dem gewohnten Karo-Raster, schwebendes Karten-Chrome, ein Akzent; `styles/designer-atelier.css` mappt die `--color-*`-Tokens über eine `--wd-*`-Palettenschicht) vs. **Classic** (bisheriger Look, byte-identisch). **Farb-Skins adaptieren beide Looks:** im Atelier re-pointet jeder Skin Akzentfamilie + Grundton (`--wd-accent*`, `--wd-canvas`/`--wd-panel`), die Atelier-Geometrie bleibt universell; Status-Farben bleiben in beiden Looks skin-stabil. Umschalter (`Brush`-Icon, `role="switch"`, `data-testid="toggle-atelier-theme"`) in beiden Kopfleisten-Layouts |
| `toolbarLayout` | `compact` | compact / classic | Kopfleisten-Layout: kompakt (gruppierte Popover-Menüs, grüner „Ausführen") vs. klassische Inline-Reihe (jeder Toggle/Tool als einzelner Button, icon-only Play). Umschalter-Button in beiden Layouts |
| `nodeStyle` | `classic` | classic / card | Node-Darstellung (`Ctrl+Shift+N`) |
| `nodeScaleIndex` | 1 | 0–7 (XS … 4XL) | Node-Größe |
| `labelFontOffsetIndex` | 2 | 0–6 (−4 … +8 px) | Label-Schriftgröße |
| `edgesAnimated` | true | bool | Fluss-Animation der Kanten (`A`) |
| `edgeWidthIndex` | 2 | 1.5/2/2.5/3.5/5/7 px | Kantenbreite (`Ctrl+]`/`[`) |
| `edgeRouting` | `smart` | smart / curved / straight | Routing (`R`) |
| `flexiblePortsEnabled` | false | bool | Alle vier Node-Seiten als Ports |
| `snapToGrid` | false | bool | Raster-Snapping (`G`) |
| `snapGridSize` | 20 | 10/20/30/40/60 px | Rasterabstand |
| `layoutMode` | `LR` | LR / TB / Compact / ELK | Auto-Layout-Algorithmus |
| `machineColoringEnabled` | false | bool | Node-Einfärbung nach Ziel-Maschine + Legende (`M`, 8-Farben-Palette) |
| `failureHeatmapEnabled` | false | bool | Rote Tinte nach Fehlerrate (`H`, aus `__stats.failureRate`) |
| `coverageHeatmapEnabled` | false | bool | Ausgrauen nie/selten gelaufener Nodes (Coverage-API) |
| `coverageWindowDays` | 30 | 1–365 | Zeitfenster der Coverage-Heatmap |
| `criticalPathEnabled` | false | bool | Längsten Pfad hervorheben + Slack-Badges (`C`) |
| `dataFlowOverlayEnabled` | false | bool | Variablen-Fluss auf Kanten visualisieren |
| `premiumCanvas` | true | bool | Tiefenschatten, Glaseffekt, farbige Pfeile, Glow |
| `designRefresh` | true | bool | Neues Designer-Design an/aus (Kopfleisten-Toggle; aus = klassischer Look) |

- **Coverage-Heatmap:** liest `GET /workflows/{id}/coverage?windowDays=N` → Node-Annotation `__coverage` (`never`/`rare`/`common` + roter Punkt bei Fehlern).
- **Critical-Path:** echte CPM-Berechnung (topologischer Kahn-Sort + Forward/Backward-Pass) gewichtet mit `p95DurationMs`; ignoriert disabled Nodes/Edges; kritische Knoten orange, sonst Slack-Badge.

## 20a. Live- & Annotations-Infrastruktur

- **SignalR-Live-Stream** (`/hubs/execution`, `useSignalR` + Reducer): Events **StepStarted**, **StepCompleted**, **StepPaused**, **StepResumed**, **ExecutionStatusChanged**, **LiveEventsBatch**. Auth über httpOnly-Cookie beim WebSocket-Upgrade.
  - Baut den **Live-Databus** auf (`{stepId}.output/.error/.param.*` + Output-Variable-Aliase).
  - **Hydration-Concurrency:** max. 4 parallele Step-Fetches (Semaphore) gegen Thundering Herd; Auto-Hydration nur Top-10 jüngste aktive Läufe.
  - **TTL/Eviction:** abgeschlossene Läufe verschwinden nach 30 s; periodischer Refresh alle 10 s; Reconnect joint Workflow neu.
  - **Cache-Invalidierung:** React-Query-Caches (`workflow-executions`, `executions`, `dashboard-stats`) werden bei Terminal-Status mit 500 ms Debounce invalidiert.
- **Node-Annotations-Aggregator** (`useNodeAnnotations`): schreibt pro Node `__liveStatus` (live/Replay-Scrub), `__health` (Sparkline aus `step-health`, 60 s Refresh), `__stats` (`step-stats?windowDays=30`, 5 min Refresh), `__machineColorIdx`, `__varFlowRole` (producer/consumer beim Variablen-Hover) und das Edge-Highlight `__varFlowHighlighted` entlang des Producer→Consumer-Pfads.
- **Displayed-Graph-Projektion** (`useDisplayedGraph`): read-only Transformation der Rohdaten vor dem Rendern — versteckt gefilterte Activity-Typen (+ deren Kanten), berechnet `inDegreeCount` (Junction-Badge), Simulations-Status (`reachable`/`revealing`/`skipped`), Lint-Zähler, Failure-Tint, Data-Flow-Variablen und faltet kollabierte Gruppen (`collapsedGraphView`) zusammen.
- **Smart-Defaults** (`lastSimilarNode`): neue Nodes übernehmen Konfiguration (z. B. Engine, Maschine) vom zuletzt platzierten Step gleichen Typs.
- **Resizable-Panels** (`useResizable`): Sidebar, Right-Panel, Info-Card und Execution-Panes per Drag größenveränderbar, Doppelklick = Reset.
- **Persistierte UI-States:** `workflowBrowserStore` (View-Modus Folder/Trigger, eingeklappte Ordner, Info-Card-Höhe) und `sidebarStore` (Sidebar collapsed).

## 21. Mobile-Ansicht

`MobileWorkflowView` — read-only Graph für Smartphones/Portrait-Tablets:
- Wiederverwendung der Editor-Node-/Edge-Typen; `draggable:false`, `selectable:false`.
- **Live-Status** der Nodes via SignalR.
- Vergrößerte Node-Skala (`NodeScaleOverrideContext`, Index 3) für Lesbarkeit; Zoom geklemmt auf 0.7–1.6.
- Header mit Zurück-Button, Workflow-Name, Read-Only-Hinweis; Pan/Pinch-Zoom statt Edit.

## 22. Keyboard-Shortcuts (Referenz)

### Global / Standard
| Taste | Aktion |
|---|---|
| `?` | Hilfe / Shortcut-Referenz |
| `Esc` | Overlays schließen |
| `Home` | Fit all |
| `F11` | Fullscreen |
| `Ctrl+P` | Quick-Switcher |
| `Ctrl+Shift+P` | Command Palette |
| `Ctrl+S` | Save |
| `Ctrl+Shift+S` | Publish / Enable / Disable |
| `Ctrl+F` | Suche |
| `Ctrl+C` / `Ctrl+V` | Copy / Paste |
| `Ctrl+D` | Duplizieren |
| `Ctrl+Z` / `Ctrl+Y` / `Ctrl+Shift+Z` | Undo / Redo |
| `Ctrl+A` | Select All |
| `Ctrl+E` / `Ctrl+U` | Lock / Unlock |
| `Ctrl+Enter` / `Ctrl+Shift+Enter` | Test Run / Debug Run |
| `Delete` / `Backspace` | Löschen |

### Expert-Modus (zusätzlich)
| Taste | Aktion |
|---|---|
| `A` / `R` / `M` / `H` / `C` / `G` | Edge-Animation / -Routing / Machine-Coloring / Heatmap / Critical-Path / Snap-Grid |
| `D` / `B` | Disabled / Breakpoint der Auswahl togglen |
| `Tab` / `Shift+Tab` | Zum nächsten/vorherigen verbundenen Node |
| Pfeiltasten (`+Shift`) | Auswahl nudgen (10 px / 1 px) |
| `Ctrl+G` | Gruppieren |
| `Ctrl+H` | Find & Replace |
| `Ctrl+Shift+E` | Zoom to Selection |
| `Ctrl+Shift+U` | Force-Unlock (Admin) |
| `Ctrl+Shift+X` | Cancel Run |
| `Ctrl+Shift+T` / `Ctrl+Shift+O` | Tidy / Restore Layout |
| `Ctrl+Shift+L` | Lint-Panel |
| `Ctrl+Shift+D` | Diff |
| `Ctrl+Shift+R` | Simulation |
| `Ctrl+Shift+N` | Node-Stil (Classic/Card) |
| `Ctrl+Alt+X` | Activity-Type-Filter löschen |
| `Ctrl+]` / `Ctrl+[` | Edge-Breite +/− |
| `Ctrl+Shift+>` / `Ctrl+Shift+<` | Node-Größe +/− |
| `Ctrl+Alt+.` / `Ctrl+Alt+,` | Label-Schrift +/− |
| `Ctrl+Shift+J` / `Ctrl+Alt+P` | Export JSON / PNG |
| `Ctrl+Shift+1…5` | Navigieren: Workflows / Executions / Machines / Globals / Audit |

### Canvas-Gesten
- Linksziehen = Marquee-Auswahl · Mittel-/Rechtsziehen = Pan · `Shift+Klick` = Auswahl erweitern.
