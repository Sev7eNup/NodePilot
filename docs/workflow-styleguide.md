# NodePilot Workflow Layout Styleguide

Konventionen für hand-layoutete Workflow-JSONs (`nodes`/`edges`), die in der GUI ohne Überlappungen und mit lesbaren Edge-Labels dargestellt werden. Abgeleitet aus dem Master-Test-Workflow [scripts/test-master-all-activities.json](../scripts/test-master-all-activities.json) — dort ist das lebende Referenz-Beispiel (alle Activities, alle 14 Edge-Operatoren, alle 3 Junction-Modes).

> Hinweis: Die UI bietet einen Dagre-Auto-Layout-Button ([autoLayout.ts](../src/nodepilot-ui/src/lib/autoLayout.ts), `rankdir:LR, ranksep:180, nodesep:80`). Für hand-kuratierte Demo-Workflows mit vielen Condition-Labels reicht Dagre nicht — die Regeln unten sind dafür.

---

## 1 — Leitprinzipien

Es gibt **keine vorgeschriebene Flussrichtung**. LTR, TTB, sternförmig, zwei parallele Spalten, Mischung aus mehreren Sub-Topologien — was den Workflow für einen menschlichen Leser am klarsten macht, ist richtig. Dank Flexible Ports (Abschnitt 7.1) kann jede Edge frei eine Seite des Source-/Target-Nodes wählen, statt sich in ein Layout-Korsett zwängen zu müssen.

Drei harte Regeln, an denen alles andere hängt:

1. **Keine Überlappungen** — weder Node-auf-Node, noch Edges durch Nodes hindurch, noch Labels über Labels. Bei Konflikt: mehr Platz lassen. Lieber ein zu großer Workflow auf dem Canvas als ein zu enger, der nicht lesbar ist.
2. **Edge-Labels müssen ohne Zoomen lesbar sein** — kurze, semantische Bedingungen. Wenn ein Label zu lang wird, splittet man die Logik auf zwei sequenzielle Edges, fasst sie zu einer Sub-Pattern-Phrase zusammen, oder zieht den Operator-Spellout in das nachfolgende Node-Label.
3. **Flussrichtung ist innerhalb einer Region konsistent** — wenn ein Abschnitt LTR fließt, fließt er ganz LTR; man wechselt nicht mittendrin ohne semantischen Grund. Loop-Backs, Retry-Pfade und „springt zurück nach Phase X bei Fehler" sind legitime Ausnahmen — die sollen visuell auffallen.

Alle weiteren Abschnitte sind **Konventionen für den häufigsten Fall (LTR mit klarer Haupt-Lane)**. Wer eine andere Topologie wählt, übersetzt die Abstandsmaße entsprechend (für TTB: x↔y vertauschen; für radiale Layouts: in Slot-Winkeln denken).

## 1.5 — Kompaktheit (wichtiger als alles andere)

**Das Endprodukt soll bei `fit-view` ohne Scrollen lesbar bleiben.** Hand-layoutete Workflows haben einen impliziten Größen-Etat — wer ihn überzieht, produziert ein 35%-Mini-Diagramm in dem niemand mehr was erkennt. Daumenregel pro Activity-Count:

| Aktivitäten | Ziel-Canvas (px) | Layout-Strategie |
|---|---|---|
| ≤ 15 | ≤ 2500 × 1200 | reines LTR oder TTB, 1 Zeile |
| 16–30 | ≤ 3500 × 1800 | Snake mit 2 Zeilen |
| 31–50 | ≤ 4500 × 2200 | Snake mit 3 Zeilen |
| > 50 | — | SubWorkflows extrahieren statt Canvas wachsen lassen |

**Snake-Pattern** (das wichtigste Werkzeug für kompakte Layouts):

1. ROW 1: LTR von links nach rechts
2. Übergang am rechten Rand: `sourceHandle: bottom` → `targetHandle: top`
3. ROW 2: RTL von rechts nach links (`sourceHandle: left` → `targetHandle: right`)
4. Übergang am linken Rand: `sourceHandle: bottom` → `targetHandle: top`
5. ROW 3: wieder LTR

Snake-Übergänge **nur an Zeilen-Enden** — nicht in der Mitte einer Zeile abknicken (verwirrt den Leser, wirkt wie ein Fehler). Pro Zeile maximal ein Richtungswechsel. Branches/Fans hängen rechtwinklig zur Zeilenrichtung — eine Fan-Branch fan't z.B. in einer LTR-Zeile nach oben/unten weg, nicht nach hinten gegen die Flussrichtung.

**Kompaktheits-Tradeoffs:**

- y-Spread von 160 px (statt 180) ist OK bei kurzen Branch-Labels; bei >40 Zeichen Label-Wrap unbedingt 180+ behalten.
- x-Step von 280 (statt 300) ist OK bei normalen 1→1-Ketten. Fan-Out/Fan-In braucht weiterhin 340–400.
- Mehr als 3 Snake-Zeilen heißt: der Workflow ist zu groß für eine Single-Definition. Sub-Workflows via `startWorkflow` extrahieren.
- StickyNotes sparsam einsetzen — eine pro Zeilen-/Phasen-Übergang reicht. Jede Note klaut visuellen Platz, der für lesbare Edge-Labels gebraucht wird.

## 2 — Abstände (LTR-Beispielmaße)

| Situation | x-Step | y-Lane |
|---|---|---|
| Normale Kette (1→1) | **300 px** | — |
| Kurze Labels, 1→1 | 280 px minimum | — |
| Fan-Out mit ≥5 Condition-Labels | **340–400 px** | — |
| Fan-In mit ≥5 Branches | **340–400 px** | — |
| 2–3 parallele Branches | — | 160 px |
| 4–5 parallele Branches | — | **180 px** |
| 6+ parallele Branches | — | **180–200 px** |

Die Fan-Out/Fan-In-Regel ist die wichtigste: bei 7 Edges, die radial aus einer Junction zu Phase E gehen, clustern sich die Midpoint-Labels ohne extra Abstand und werden unlesbar. Breiter horizontal = mehr Midpoint-Spread = weniger Label-Kollision.

Fan-Branches **symmetrisch um die Haupt-Lane** zentrieren, wenn die Anzahl ungerade ist. Bei gerader Anzahl: leicht asymmetrisch akzeptieren, aber Span nicht größer als nötig — jedes extra y-Pixel kostet vertikalen Scroll-Raum.

**Shape-Bonus für Trigger-Nodes**: Triggers werden zu Oktagonen mit 1.55× Bbox (siehe Abschnitt 8). Direkt nach einem Trigger entsprechend **+80–100 px extra x-Vorlauf** einplanen, sonst überlappt der Oktagon-Rand mit dem nachfolgenden Node.

## 3 — Node-Labels

- **Mit Activity-Prefix starten**: `"runScript: collect host info"`, `"Junction: waitAll (5)"`, `"Log: no PANIC"`. So sieht man auf einen Blick, was für ein Step das ist.
- **Card-Mode-Breite 220–280 px**: Labels länger als ~40 Zeichen werden abgeschnitten oder umgebrochen.
- **Keine Operator-Hints im Node-Label**, wenn die Bedingung schon auf der Edge steht: statt `"Log: env == production (AND ==, !=)"` einfach `"Log: env is production"`.
- **Technische Parameter kurz**: `"Junction: waitNofM (2/3)"` statt `"Junction: waitNofM (requiredCount=2 of 3)"`.

## 4 — Edge-Labels

- **Semantisch, nicht strukturell**: `"env==prod & env!=stg"` statt `"AND(env=='production', env!='staging')"`.
- **ASCII-Symbole**: `&` für AND, `OR` für OR, `NOT` für NOT, `!=`, `>=`, `<=`, `!x` für Negation.
- **Bereiche** statt zwei-Operator-Spellout: `"cpu in [1..128]"` statt `"AND(cpu >= 1, cpu <= 128)"`.
- **≤ 30 Zeichen** wo möglich. Bei langen Bedingungen: entweder zu einem Sub-Pattern zusammenfassen (`"4× string ops"`) und im nachfolgenden Node-Label detaillieren — oder auf zwei sequenzielle Edges/Decision-Nodes splitten.
- **Standard-Phrasen**:
  - `"Always"` — unbedingte Edge (kein `condition` / `conditionExpression`)
  - `"On Success"` — Shortcut `condition: "<stepId>.success"`
  - `"On Failure"` — Shortcut `condition: "<stepId>.failed"`
- **Disabled Edges**: Label `"DISABLED edge"` + `data.disabled: true`. Target-Node im Label markieren: `"Log (disabled edge target)"`.

## 5 — Condition-Operator-Coverage-Pattern

Für Demo-Workflows, die **alle 14 Operatoren + AND/OR/NOT** zeigen sollen, die Operatoren in 6–7 Branches gruppieren — jede Branch kombiniert 2–4 Operatoren zu einer **semantisch sinnvollen Regel**. Nicht ein Operator pro Branch (bläht den Workflow auf), nicht alles in eine Branch packen (dann zeigt der Demo nichts).

Bewährte Gruppierung (aus [test-master-all-activities.json](../scripts/test-master-all-activities.json)):

| Branch | Regel | Operatoren |
|---|---|---|
| 1 | `env==prod & env!=stg` | `==`, `!=`, AND |
| 2 | `cpu in [1..128]` | `>=`, `<=`, AND |
| 3 | `disk% > thr OR free < 1GB` | `>`, `<`, OR |
| 4 | `contains+starts+ends+matches` | `contains`, `startsWith`, `endsWith`, `matches`, AND |
| 5 | `output set & isDomain` | `isNotEmpty`, `isTrue`, AND |
| 6 | `empty & !dry` | `isEmpty`, `isFalse`, AND |
| 7 | `NOT contains PANIC` | `NOT`, `contains` |

## 6 — Engine-Gotchas (must-know beim JSON-Bauen)

| Gotcha | Richtig | Falsch |
|---|---|---|
| `waitNofM`-Config | `"requiredCount": 2` | `"n": 2` (silent default 1) — siehe [WorkflowEngine.cs:535](../src/NodePilot.Engine/WorkflowEngine.cs#L535) |
| `manualTrigger`-Params | Alle `type: "string"`, Defaults als Strings (`"80"`, `"false"`) | `type: "number"` → UI sendet Zahl → 400 "cannot convert to System.String" beim Execute |
| `isTrue`/`isFalse` | Operiert auf Strings: `""`, `"false"` (case-insensitive), `"0"` sind falsy | Alles andere truthy — `"False"` (PowerShell `[string]$false`) ist falsy ✓ |
| `runScript` Params | Alle deklarierten PS-Variablen werden automatisch als `param.*` captured — [ProcessExecutionEngine.cs:85](../src/NodePilot.Engine/PowerShell/ProcessExecutionEngine.cs#L85) hängt einen Capture-Block an | Kein `###NODEPILOT_PARAMS###`-Marker selbst einfügen |
| `targetMachineId` | Leer/`"localhost"` → In-Process-Bypass (läuft engine-local; Script kann via `Invoke-Command`/`New-PSSession` **selbst** remoten, SCOrch-Stil — siehe [claude-reference.md](claude-reference.md#runscript--ausführungsort-local-vs-remote--self-managed-remoting)). GUID → managed WinRM | Echte Machines brauchen GUID aus DB |
| `==` / `!=` | Numerisch wenn BEIDE parseable, sonst String-Compare | `"80" == 80` → numerisch gleich ✓ |
| Node-Skip | `data.disabled: true` auf dem Node → Step wird als `Skipped` markiert, Downstream-Nodes ohne andere aktive Quellen kaskadieren auf `Skipped` | Nicht mit Edge-Disable verwechseln — letzteres skippt nur die eine Kante, nicht den Node |
| Breakpoint | `data.breakpoint: true` → bei `POST /execute` mit `{"debug": true}` pausiert die Engine vor dem Step. Resume via `POST /executions/{id}/resume` | Außerhalb des Debug-Modus wird das Flag ignoriert |

## 7 — JSON-Schema-Erweiterungen seit Initial-Version

Vier Designer-Features, die im Workflow-JSON sichtbar sind und beim Generieren bewusst gesetzt werden können.

### 7.1 Flexible Ports — `sourceHandle` / `targetHandle`

Jeder Activity-Node hat vier Handles: `top` | `right` | `bottom` | `left`. Default ohne explizite Angabe ist `right` → `left` (klassisch LTR). Für vertikale Layouts, Junction-Anläufe von unten, Loop-Back-Edges oder gemischte Topologien können Edges explizit andere Seiten wählen:

```json
{
  "id": "e1", "source": "decision", "target": "junction",
  "sourceHandle": "bottom", "targetHandle": "top",
  "type": "labeled", "data": { "label": "case A" }
}
```

Implementierung in [edgePorts.ts](../src/nodepilot-ui/src/lib/edgePorts.ts). Der Toolbar-Toggle „Flexible Ports" macht im UI alle vier Handles anklickbar — aber **die JSON-Felder funktionieren unabhängig vom Toggle**. Ein Agent darf `sourceHandle`/`targetHandle` immer setzen, wenn das Layout es braucht; die EdgePropertiesPanel zeigt die Port-Auswahl automatisch, sobald eine Edge nicht-default-Handles hat.

**Wann nutzen:** vertikale Phasen-Sektionen, Loop-Back-Edges (von rechts unten zurück nach links oben), zentrale Hub-Topologien, Junctions, die von mehreren Seiten gespeist werden. **Wann weglassen:** klassisches LTR — die Default-Belegung ist sauber, explizite Handles wären nur Rauschen.

**Anti-Pattern:** Edges mit konfliktierenden Handles, die durch andere Nodes hindurch zwängen würden. Erst Layout planen, dann Handles wählen — nicht umgekehrt.

### 7.2 Edge-Reshape — `data.controlPoints`

Hand-gebogene Edges (cubic-Bezier) overriden alle Auto-Routing-Modi (smart/curved/straight und Backward-U-Loop):

```json
{
  "id": "e1", "source": "a", "target": "b",
  "data": {
    "controlPoints": { "cp1x": 240, "cp1y": 200, "cp2x": 360, "cp2y": 200 }
  }
}
```

Details in [smartEdgePath.ts](../src/nodepilot-ui/src/components/designer/edges/smartEdgePath.ts) + [EdgeReshapeHandles.tsx](../src/nodepilot-ui/src/components/designer/edges/EdgeReshapeHandles.tsx). Round-Trip-stabil: Save/Load/Export/Import strippt das Feld nicht.

**Beim Neu-Generieren** eines Workflows in der Regel weglassen — Auto-Routing ist gut genug und der User kann später hand-anpassen. **Beim Re-Generieren mit Layout-Preservation** (AI-Refactor eines bestehenden Workflows) das Feld unbedingt erhalten, sonst verliert der User seine hand-gebogenen Kurven.

### 7.3 Node-Level Disable & Breakpoint

Zwei Boolean-Flags im `data`-Object jedes Activity-Nodes:

- **`data.disabled: true`** — Node wird beim Execute als `Skipped` markiert. Downstream-Nodes ohne andere aktive Quellen kaskadieren ebenfalls auf `Skipped`. Erlaubt „kommentiere diesen Step temporär aus" ohne die Edge-Topologie anzufassen — ideal für „diese Branch ist noch nicht fertig, soll aber nicht aus dem Workflow gelöscht werden".
- **`data.breakpoint: true`** — Engine pausiert vor dem Step, wenn der Run mit `{"debug": true}` gestartet wurde. SignalR-Event `StepPaused` triggert den Variable-Inspector im UI. Resume via `POST /executions/{id}/resume`.

Beide standen im Initial-Styleguide nicht, sind aber Teil des stabilen JSON-Vertrags.

### 7.4 StickyNote- & Group-Nodes

Neben `type: "activity"` existieren zwei Annotations-/Gruppierungs-Knoten ([StickyNoteNode.tsx](../src/nodepilot-ui/src/components/designer/nodes/StickyNoteNode.tsx), [GroupNode.tsx](../src/nodepilot-ui/src/components/designer/nodes/GroupNode.tsx)). Die Engine ignoriert beide vollständig (StickyNotes haben keine Handles, GroupNodes sind reine Layout-Container).

**StickyNote** — Inline-Kommentar im Workflow, z.B. eine Erklärung pro Phase. Sehr empfehlenswert für agentisch generierte Demo-/Lehr-Workflows:

```json
{
  "id": "note-phase-b", "type": "stickyNote",
  "position": { "x": 1200, "y": 60 },
  "data": {
    "label": "Note", "activityType": "note",
    "text": "Phase B: 7-fach Operator-Coverage-Fan — jede Branch deckt 2-4 Operatoren ab.",
    "disabled": true
  }
}
```

`data.disabled: true` ist Pflicht — schützt davor, dass ein versehentlicher Edge-Import die Note zum Endpoint macht. `data.fontSize` (Preset-Werte 11/13/16/20/28) ist optional, Default 13.

**GroupNode** — visueller Container, der mehrere Nodes umrahmt. Phase-Boundaries oder „diese fünf Nodes gehören zusammen"-Cluster.

## 8 — Visuelle Effekte ohne JSON-Wirkung

Zwei Designer-Features, die das Erscheinungsbild beeinflussen, aber **nicht im JSON kodiert** sind:

- **Node-Shape-System** ([shapes.ts](../src/nodepilot-ui/src/components/designer/nodes/shapes.ts)): aus `activityType` abgeleitet. Triggers → Oktagon (1.55× Bbox), Control-Flow-Nodes (`junction`, `decision`, `forEach`, `waitForCondition`) → Raute (1.18×), `returnData` → Pentagon-Flag (1.10×), Rest → Quadrat. **Auswirkung aufs Layout**: bei Trigger-Nodes mehr horizontalen Vorlauf einplanen (typisch 380–400 px statt 300), bei Rauten den Platz für die Diamond-Spitzen mitdenken — sonst überlappen die Shapes mit Nachbar-Nodes.
- **Coverage-Heatmap**: Toolbar-Toggle tinted Nodes nach Ausführungs-Häufigkeit der letzten N Tage. „Nie ausgeführt" → 40% Opacity + Grayscale, „rare" → 80% Opacity. Für hand-gebaute Demo-Workflows, die als Tutorial taugen sollen, ist es sinnvoll, alle Pfade so zu konstruieren, dass sie mit Default-Trigger-Parametern erreichbar sind — sonst zeigt die Heatmap nach dem ersten Run viel Grau.

## 9 — Lebendes Referenz-Beispiel

[scripts/test-master-all-activities.json](../scripts/test-master-all-activities.json) — 45 Nodes, 54 Edges, ~7900×1320 px. Identisch eingebettet in [workflow-example.json](../src/NodePilot.Ai/Prompts/workflow-example.json) als Few-Shot für die KI-Workflow-Generierung.

- **Phase A** (Trigger + Init): x=0…600, y=600 — `scheduleTrigger` (cron `0 0/5 * * * ? *`) → `runScript` (init vars) → `log`. Plus dead-end `log_fail` (legacy `.failed`-Edge).
- **Phase B** (7-fach Condition-Coverage-Fan): Branch-Logs bei x=1340, y=60/240/420/600/780/960/1140 (180-Spread). Jede der 7 Edges deckt 2–4 Operatoren ab — zusammen alle 14 + AND/OR/NOT. Plus 1 disabled Edge zu `disabled_log` (y=1320). Junction `waitAll` bei x=1680.
- **Phase C** (3-fach Activity-Fan → Junction `waitAny`): Branch-1 (File/Hash/Zip) bei y=200, Branch-2 (Data: json/xml/rest+retry/sql) bei y=600, Branch-3 (System/Process: svc/reg×4/wmi/startProgram/scheduledTask) bei y=1000. Junction bei x=4500.
- **Phase D** (Decision + waitNofM): `decision` (mit `breakpoint:true`) bei x=4900, 3 Case-Logs bei y=420/600/780, Junction `waitNofM` (`requiredCount:1`) bei x=5500.
- **Phase E** (Async + Loop): `delay` → `waitForCondition` → `forEach` (3 items, parallel=2) → `startWorkflow` (fire-and-forget) bei x=5800…6700.
- **Phase F** (Finish): `emailNotification` → `powerManagement` (`disabled:true`) → `log` → `returnData` bei x=7000…7900.

**Child-Workflow** (`Master Test Child: Simple Task`): Minimale `manualTrigger → log → returnData`-Kette für `forEach`/`startWorkflow`-Aufrufe. Im Export gebündelt.

Das Beispiel ist LTR aufgebaut — als ein gut funktionierender Default-Stil, nicht als verbindliche Vorlage. Andere Topologien sind ausdrücklich erlaubt, solange die Leitprinzipien (Abschnitt 1) erfüllt sind.

## 10 — Checkliste vor dem Import

Bevor du einen hand-gebauten Workflow via `POST /api/workflows/import` oder `POST /api/workflows` pushst:

- [ ] **Canvas-Gesamtmaße innerhalb des Etats** (siehe Abschnitt 1.5): ≤3500×1800 für 16–30 Activities, ≤4500×2200 für 31–50
- [ ] Bei >15 Activities: Snake-Layout statt einer einzigen riesigen Zeile
- [ ] Keine zwei Nodes überlappen sich (Bbox-Check, inkl. Trigger-Oktagon und Control-Flow-Raute)
- [ ] Keine Edge schneidet einen Node, durch den sie nicht durch soll
- [ ] Edge-Labels sind ohne Zoomen lesbar (keine Cluster, ≤30–50 Zeichen)
- [ ] Node-Labels haben Activity-Prefix und keine Operator-Redundanz zu Edge-Labels
- [ ] Flussrichtung ist innerhalb jeder Region konsistent (Loop-Backs/Retries dürfen visuell ausbrechen)
- [ ] `sourceHandle`/`targetHandle` nur gesetzt, wo das Routing es semantisch braucht — sonst weglassen
- [ ] Trigger-Nodes haben +80–100 px extra horizontalen Vorlauf wegen Oktagon-Shape
- [ ] Alle `position.x`/`position.y` in 20er-Schritten ausgerichtet (Snap-Grid-Feeling)
- [ ] Fan-Out/Fan-In mit ≥5 Branches hat +340 px x-Gap zum nächsten Node
- [ ] Trigger-Params (falls `manualTrigger`) alle `type: "string"` mit String-Defaults
- [ ] `waitNofM`-Junctions nutzen `requiredCount`, nicht `n`
- [ ] Keine Edge referenziert einen nicht-existierenden Source/Target (Dangling-Check)
- [ ] Referenzen in `startWorkflow.workflowNameOrId` zeigen auf existierende Workflow-Namen (Child zuerst posten!)
- [ ] StickyNotes haben `data.disabled: true` (Schutz vor versehentlichen Edges)
