# Tech-Demo XL

Umfangreicher Showcase-Workflow als Ergänzung zum schlankeren [main.json](main.json). Baut auf denselben Styleguide-Regeln auf ([docs/workflow-styleguide.md](../../docs/workflow-styleguide.md)) und referenziert denselben Child-Workflow ([child.json](child.json)).

## Zahlen

| Metrik | Wert |
|---|---|
| Activity-Nodes | 114 |
| Edges | 190 |
| Layout-Bounds | 10720 x 1800 px |
| Activity-Types abgedeckt | 25 (19 Activities + 6 Trigger) |
| Edge-Operatoren | alle 14 + AND / OR / NOT |
| Junction-Modes | 3 (waitAll, waitAny, waitNofM) |
| Breakpoints | 4 |
| Disabled Nodes | 8 (5 Trigger-Gallery + 3 Demo) |
| Disabled Edges | 6 |

## Layout — 3 Swim-Lanes

```
 y=300    LANE TOP (Discovery)    Trigger-Gallery -> Host-Erhebung -> 12 Remote-Ops -> Operator-Gallery -> Hold -> Deep-Ops ----.
                                                                                                                                 \
 y=900    LANE MID (Processing)   REST/SQL -> XML/JSON -> forEach/Sub-WF -> Retry-NofM -> 10 Deep Combos ------------------------+--> Finale
                                                                                                                                 /
 y=1500   LANE BOT (Ops/Finale)   Timing-Fan -> Remote-Ops -> Email/Power -> Debug-Zone -> 8 Deep Combos -> Cleanup -------------'
         +----------------------------------------------------------------------------------------------------------------------->
          x=0                   x=2100                 x=4600                x=7100                   x=10000          x=10720
          Boot (TOP only)       Phase 2 (3 Lanes)     Phase 3 (3 Lanes)    Phase 4 (3 Lanes)         Finale (merge)
```

Alle drei Lanes starten parallel am `sync1`-Junction (x=1960, waitAll) nach der Host-Erhebung und laufen unabhaengig bis zum `final-junction` (x=10000, waitAny). Das sequentielle Gathering am Anfang stellt sicher, dass `{{host.param.*}}` in allen Lanes verfuegbar ist.

## Was wird demonstriert

### Alle 6 Trigger-Typen (Phase A-TOP)
Der aktive `trg-manual` feedet `log-kickoff`. Die 5 weiteren Trigger (`trg-schedule`, `trg-webhook`, `trg-filewatch`, `trg-database`, `trg-eventlog`) sind per `data.disabled: true` als Anschauungs-Nodes sichtbar — sie zeigen die jeweilige Config-Schema im Designer, werden aber beim Run geskippt.

### Alle 19 Activity-Typen
Jeder Activity-Type ist mindestens einmal vertreten. Haeufigkeits-Schwerpunkte:
- 49x `log` (Operator-Coverage, Error-Branches, Debug-Targets, Phase-Marker)
- 11x `delay` (Timing-Demo, Retry-NofM, Hold-Patterns)
- 11x `junction` (alle 3 Modes an mehreren Stellen)
- 4x `runScript` (inkl. einer mit Breakpoint), 4x `fileOperation`/`folderOperation`, 4x `wmiQuery`
- Die selteneren: 1x `forEach`, 1x `powerManagement` (action=`abort`, safe), 1x `emailNotification`

### Alle 14 Edge-Operatoren + AND/OR/NOT
Phase D-TOP (jmatch-top Fan-Out) kapselt die Operator-Gallery in 10 log-Branches:

| Branch | Label | Operatoren |
|---|---|---|
| D1 | `env==prod & env!=stg` | `==`, `!=`, AND |
| D2 | `cpu in [1..128]` | `>=`, `<=`, AND |
| D3 | `disk% > thr OR free < 1GB` | `>`, `<`, OR |
| D4 | `contains+starts+ends+matches` | `contains`, `startsWith`, `endsWith`, `matches`, AND |
| D5 | `output set & isDomain` | `isNotEmpty`, `isTrue`, AND |
| D6 | `empty & !dry` | `isEmpty`, `isFalse`, AND |
| D7 | `NOT contains PANIC` | NOT, `contains` |
| D8 | `build matches & env!=prod` | `matches`, NOT, `==`, AND |
| D9 | `netUp OR gw set` | `isTrue`, `isNotEmpty`, OR |
| D10 | `env starts prod OR stg` | `startsWith`x2, OR |

Alle 14 Operatoren sowie AND, OR und NOT werden abgedeckt (Validierung im Generator-Script).

### Alle 3 Junction-Modes
- `waitAll`: `sync1`, `jall-top`, `bot13-jallremote`
- `waitAny`: `jmatch-top`, `mid-jrest`, `bot04-jany`, `top-j-combo`, `m-jdeep`, `bot-jdeep`, `final-junction`
- `waitNofM` (`requiredCount: 2`): `m16-jnofm`

### Breakpoints, Disabled Nodes, Disabled Edges
- Breakpoints: `process-data` (runScript), `m06-json` (jsonQuery), `bot19-log-bp1` (log), `bot20-runscript-bp` (runScript)
- Disabled Nodes: die 5 Trigger + `bot07-delay-disabled`, `bot16-log-disnode`, `bot21-log-disnode2`
- Disabled Edges: 5 Dummy-Trigger-Kanten + `bot14-email -> bot17-log-ghost`

### Retry-Policies & Error-Paths
- Retry auf: `collect-host`, `process-data`, `r05-prog-cmd`, `m01-rest-get`, `m07-rest-post`, `m13-delay-a`, `m14-delay-b`, `m15-delay-c` (verschiedene backoff-Strategien als Anschauung)
- Error-Paths: `m01-rest-get.failed -> m02-log-restfail`, `m09-sw-sync.failed -> m10-log-childfail`

## Import

1. **Child-Workflow zuerst**: der Child (`NodePilot Tech-Demo - Child` aus [child.json](child.json)) muss bereits importiert sein, sonst scheitern die `startWorkflow`- und `forEach`-Nodes beim Run.
2. **XL importieren**: via Designer-Import-Button (Workflows-Seite, *Import*) oder per API:
   ```bash
   curl -X POST http://localhost:5000/api/workflows/import \
        -H "Authorization: Bearer $TOKEN" \
        -H "Content-Type: application/json" \
        -d @xl.json
   ```
3. **Visueller Check**: im Designer oeffnen, Auto-Layout **nicht** klicken — der Workflow ist hand-layoutiert, Dagre wuerde die 3-Lane-Struktur zerlegen.

## Regenerieren

Die Datei wird von [build_xl.py](build_xl.py) erzeugt — nicht direkt editieren. Der Generator validiert beim Build alle Invarianten (Node-Count >= 100, alle Types, alle Operatoren, keine Dangling-Edges, LTR-Invariante, 20-px-Grid, waitNofM hat `requiredCount`).

```bash
python scripts/tech-demo/build_xl.py
```
