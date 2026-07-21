# Canvas, Nodes & Edges

## Canvas & Viewport

- **Pan & Zoom:** Pan mittlere/rechte Maustaste; Zoom bis `0.15×`. Mouse-Wheel-Zoom via React Flow.
- **Fit-View:** `Home` passt alle Nodes mit 15% Padding (300 ms Animation); Auto-Fit beim ersten Load.
- **Zoom to Selection (Expert):** `Ctrl+Shift+E` (20% Padding).
- **MiniMap:** Pan/zoombar, Node-Farbe nach Live-Status (Running=Amber, Paused=Orange, Succeeded=Green, Failed=Red, Skipped=Gray).
- **Background:** Classic-Dot-Grid (20 px) oder **Premium Canvas** (zweistufiges Crosshatch 80/16 px); Snap-Grid-Variante.
- **Snap-to-Grid (Expert):** `G` toggelt Grid-Snapping (default 20 px, konfigurierbar).
- **Node Scaling:** 8 Größen (XS…4XL); `Ctrl+Shift+>`/`Ctrl+Shift+<` (Expert) Node-Größe, `Ctrl+Alt+.`/`Ctrl+Alt+,` Label-Font.
- **Fullscreen:** `F11` versteckt Sidebar/Panels/Banner.
- **Viewport-Virtualization:** nur sichtbare Elements werden gerendert — ab ~50 Nodes spürbar.

## Node-Operationen

- **Add:** Drag & Drop aus der Library (MIME `application/nodepilot-activity`) an der Cursor-Position mit Smart Defaults (zuletzt genutzte Machine/Credential desselben Typs). Quick-Connect: Edge in leeren Raum ziehen öffnet Activity-Picker. Double-Click / Command Palette / Toolbar.
- **Move:** Drag (nur mit Schreibrechten); Arrow-Key-Nudge (Expert): 10 px, mit `Shift` 1 px.
- **Multi-Select:** `Shift/Ctrl+Click` + Marquee; `Ctrl+A` alles.
- **Duplicate:** `Ctrl+D`, Offset +40/+40 px, frische IDs.
- **Delete:** `Delete`/`Backspace`; Group-Children werden reparented.
- **Group (Expert):** `Ctrl+G` wraps Selektion in einen Group-Node. Reparenting per Drag.
- **Clipboard:** `Ctrl+C`/`Ctrl+V` via `sessionStorage` — **workflow-übergreifend**; IDs werden neu vergeben, Edges/Parents remapped.
- **Auto-Layout / Tidy:** `Ctrl+Shift+T`. Standard immer LR; Expert zyklisch LR → TB → Compact → ELK. **Restore Original Layout** (Expert) `Ctrl+Shift+O`.
- **Undo/Redo:** `Ctrl+Z`/`Ctrl+Y` (auch `Ctrl+Shift+Z`). History-Tiefe 50, 800 ms Debounce bei Property-Edits.
- **Bulk-Edit:** Bei ≥2 selektierten Nodes erscheint das Bulk-Edit-Panel (Machine, Enable/Disable, Timeout, Retry).

## Node-Typen

### Activity-Node
- **Shapes nach Kategorie:** Pentagon-Bookend-Paar (Trigger `◁` + returnData `▷`, gespiegelt); **jede Action-Activity eine eigene Clip-Path-Silhouette** (Icon zentriert/unclipped); Control-Flow je eigene Form (decision=Raute, junction=hexLong, forEach=reel, startWorkflow=tagLeft) mit **indigo Group-Frame** — via Clip-Path.
- **Darstellungs-Stile:** **Classic** (kompakt, Icon-zentriert) und **Card/MD3** (Header + Config-Summary). Umschaltbar mit `Ctrl+Shift+N`.
- **Farben & Icon:** Pro Activity-Typ aus CSS-Variablen + Material-Symbol.
- **Handles/Ports:** Standard Links (In) / Rechts (Out); im Flexible-Ports-Modus alle vier Seiten bidirektional.
- **Status-Badges:** Disabled, Breakpoint (rot/amber), Fan-In (junction-Mode), Lint (Fehler/Warnung), Simulation.
- **Live-Status-Overlay:** Running (Amber, pulsierend), Succeeded (Green), Failed (Red), Skipped (Gray, dashed), Paused (Dark Orange, pulsierend).
- **Heatmaps & Pfade:** Failure-Tint, Critical-Path-Glow + Slack-Badge, Coverage-Gray-Out.

### Group-Node
Visuelle Gruppierung (keine Execution). Resize, 5 Farben, Collapse/Expand mit Child-Count-Badge, Inline-Label-Edit, Drop-Target-Highlight.

### Sticky-Note-Node
Reine Annotation (keine Handles, übersprungen). Resize, Inline-Edit, 5 Font-Größen, leicht rotiertes Sticky-Look.

## Edges / Connections

- **Edge-Typ:** Custom `LabeledEdge` mit Pfeil-Marker.
- **Semantische Farben (Status-Tokens):** Success=Green, Failed=Red, Custom-Condition=Indigo (Stroke, Pfeil und Label-Pill in derselben Farbe), Always=Gray, Disabled=dashed/dimmed — idle Kanten sind solid, der Fluss-Dash läuft nur bei Live-Executions.
- **Labels:** Manual (bis 60 chars) oder Auto-Label aus Condition ("On Success"/"On Failure"/"Always"); kanonische Labels syncen automatisch.
- **Routing-Modi:** `smart` (Bezier 0.25), `curved` (0.5), `straight` (Step mit Radius). `R` (Expert) zyklisch.
- **Manuelles Bending:** Zwei draggable Bezier-Kontrollpunkte (nur selektiert + Schreibrechte); "Reset Shape" stellt Auto-Routing wieder her.
- **Backward Edges:** Automatisches U-Shape unter Nodes für Right→Left-Flow.
- **Edge Width/Animation:** `Ctrl+]`/`Ctrl+[` (Breite), `A` (Expert, Flow-Animation).
- **Inline Insert:** ⊕-Button auf Edge fügt Node zwischen A→B ein.
- **Data-Flow-Overlay:** Chips zeigen, welche Variablen über eine Edge fließen.
- **Kontextmenü (Rechtsklick):** Enable/Disable, Swap Source↔Target (Expert), Reset Shape (Expert), Delete.
- **Validation:** Doppel-Connections werden verhindert (Toast-Hinweis).

## Overlays & Display-Optionen

Alle Display-Settings leben im **`designStore`** (Zustand + persist, Key `nodepilot-design`) und gelten editorweit:

| Setting | Default | Werte |
|---|---|---|
| `designerMode` | `standard` | standard / expert |
| `nodeStyle` | `classic` | classic / card |
| `nodeScaleIndex` | 0 | 0–7 (XS…4XL) |
| `edgeRouting` | `smart` | smart / curved / straight |
| `flexiblePortsEnabled` | false | bool |
| `snapToGrid` | false | bool |
| `layoutMode` | `LR` | LR / TB / Compact / ELK |
| `machineColoringEnabled` | false | bool — Node-Farbe nach Machine (`M`) |
| `failureHeatmapEnabled` | false | bool — rote Tint nach Failure-Rate (`H`) |
| `coverageHeatmapEnabled` | false | bool — Gray-Out nie/selten gelaufener Nodes |
| `criticalPathEnabled` | false | bool — Critical Path + Slack-Badges (`C`) |
| `dataFlowOverlayEnabled` | false | bool — Variablen-Flow auf Edges |
| `premiumCanvas` | true | bool — Depth-Shadows, Glas-Effekt, Glow |

**Critical Path:** echte CPM-Berechnung (topologische Kahn-Sortierung + Forward/Backward-Pass) gewichtet mit `p95DurationMs`; ignoriert disabled Nodes/Edges. **Coverage-Heatmap** liest `GET /workflows/{id}/coverage?windowDays=N`.