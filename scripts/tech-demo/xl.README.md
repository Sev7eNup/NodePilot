# Tech-Demo XL

Extensive showcase workflow as a complement to the leaner [main.json](main.json). Builds on the same styleguide rules ([docs/workflow-styleguide.md](../../docs/workflow-styleguide.md)) and references the same child workflow ([child.json](child.json)).

## Figures

| Metric | Value |
|---|---|
| Activity nodes | 114 |
| Edges | 190 |
| Layout bounds | 10720 x 1800 px |
| Activity types covered | 25 (19 activities + 6 triggers) |
| Edge operators | all 14 + AND / OR / NOT |
| Junction modes | 3 (waitAll, waitAny, waitNofM) |
| Breakpoints | 4 |
| Disabled nodes | 8 (5 trigger gallery + 3 demo) |
| Disabled edges | 6 |

## Layout — 3 swim lanes

```
 y=300    LANE TOP (Discovery)    Trigger-Gallery -> Host-Gathering -> 12 Remote-Ops -> Operator-Gallery -> Hold -> Deep-Ops ---.
                                                                                                                                 \
 y=900    LANE MID (Processing)   REST/SQL -> XML/JSON -> forEach/Sub-WF -> Retry-NofM -> 10 Deep Combos ------------------------+--> Finale
                                                                                                                                 /
 y=1500   LANE BOT (Ops/Finale)   Timing-Fan -> Remote-Ops -> Email/Power -> Debug-Zone -> 8 Deep Combos -> Cleanup -------------'
         +----------------------------------------------------------------------------------------------------------------------->
          x=0                   x=2100                 x=4600                x=7100                   x=10000          x=10720
          Boot (TOP only)       Phase 2 (3 Lanes)     Phase 3 (3 Lanes)    Phase 4 (3 Lanes)         Finale (merge)
```

All three lanes start in parallel at the `sync1` junction (x=1960, waitAll) after the host gathering and run independently until the `final-junction` (x=10000, waitAny). The sequential gathering at the start ensures that `{{host.param.*}}` is available in all lanes.

## What is demonstrated

### All 6 trigger types (Phase A-TOP)
The active `trg-manual` feeds `log-kickoff`. The 5 other triggers (`trg-schedule`, `trg-webhook`, `trg-filewatch`, `trg-database`, `trg-eventlog`) are visible as illustration nodes via `data.disabled: true` — they show the respective config schema in the designer but are skipped at run time.

### All 19 activity types
Every activity type is represented at least once. Frequency highlights:
- 49x `log` (operator coverage, error branches, debug targets, phase markers)
- 11x `delay` (timing demo, retry-NofM, hold patterns)
- 11x `junction` (all 3 modes in several places)
- 4x `runScript` (including one with a breakpoint), 4x `fileOperation`/`folderOperation`, 4x `wmiQuery`
- The rarer ones: 1x `forEach`, 1x `powerManagement` (action=`abort`, safe), 1x `emailNotification`

### All 14 edge operators + AND/OR/NOT
Phase D-TOP (jmatch-top fan-out) encapsulates the operator gallery in 10 log branches:

| Branch | Label | Operators |
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

All 14 operators as well as AND, OR and NOT are covered (validated in the generator script).

### All 3 junction modes
- `waitAll`: `sync1`, `jall-top`, `bot13-jallremote`
- `waitAny`: `jmatch-top`, `mid-jrest`, `bot04-jany`, `top-j-combo`, `m-jdeep`, `bot-jdeep`, `final-junction`
- `waitNofM` (`requiredCount: 2`): `m16-jnofm`

### Breakpoints, Disabled Nodes, Disabled Edges
- Breakpoints: `process-data` (runScript), `m06-json` (jsonQuery), `bot19-log-bp1` (log), `bot20-runscript-bp` (runScript)
- Disabled nodes: the 5 triggers + `bot07-delay-disabled`, `bot16-log-disnode`, `bot21-log-disnode2`
- Disabled edges: 5 dummy trigger edges + `bot14-email -> bot17-log-ghost`

### Retry-Policies & Error-Paths
- Retry on: `collect-host`, `process-data`, `r05-prog-cmd`, `m01-rest-get`, `m07-rest-post`, `m13-delay-a`, `m14-delay-b`, `m15-delay-c` (various backoff strategies for illustration)
- Error paths: `m01-rest-get.failed -> m02-log-restfail`, `m09-sw-sync.failed -> m10-log-childfail`

## Import

1. **Child workflow first**: the child (`NodePilot Tech-Demo - Child` from [child.json](child.json)) must already be imported, otherwise the `startWorkflow` and `forEach` nodes fail at run time.
2. **Import XL**: via the designer import button (workflows page, *Import*) or via the API:
   ```bash
   curl -X POST http://localhost:5000/api/workflows/import \
        -H "Authorization: Bearer $TOKEN" \
        -H "Content-Type: application/json" \
        -d @xl.json
   ```
3. **Visual check**: open in the designer, do **not** click auto layout — the workflow is hand-laid-out, Dagre would break up the 3-lane structure.

## Regenerating

The file is generated by [build_xl.py](build_xl.py) — do not edit it directly. During the build the generator validates all invariants (node count >= 100, all types, all operators, no dangling edges, LTR invariant, 20 px grid, waitNofM has `requiredCount`).

```bash
python scripts/tech-demo/build_xl.py
```
