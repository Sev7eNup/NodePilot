# NodePilot Test-Workflow Suite

29 Runbooks die jede Activity, jede Edge-Operator-Variante, jeden Junction-Modus, jedes Retry-Backoff und jeden Trigger-Typ end-to-end gegen die laufende Engine testen. Alle laufen alle 5 Minuten per `scheduleTrigger` und liefern damit einen kontinuierlichen Smoke-Test der gesamten Engine-/Activity-/Persistenz-Pipeline.

JSON-Quellen: [scripts/test-runbooks/](../scripts/test-runbooks/). Live-Installation im Folder `/Test-Workflows`.

## Zweck

- **Regression-Detection:** Jede Activity-Variante feuert alle 5 Min. Wer das Schema einer Activity bricht, sieht's im Audit-Log / Execution-List innerhalb von Minuten.
- **Engine-Throughput-Monitoring:** Bei vollem Lauf produzieren die 29 Workflows ~340 Step-Executions pro Run × 12 Runs/h = ~4.000 StepExecutions/h. Macht Pool-/Queue-/Persistenz-Engpässe sichtbar bevor sie produktiv beißen.
- **Lebende Beispiel-Bibliothek:** Jeder Runbook ist ein Few-Shot-Beispiel für seine Activity — neue Engine-/UI-Features können daran ausprobiert werden.
- **Coverage-Heatmap:** Verheiratet mit `GET /api/workflows/{id}/coverage` macht der kontinuierliche Schedule die Heatmap aussagekräftig (sonst nur Grayscale für nie-gelaufene Steps).

## Konventionen

| Aspekt | Regel |
|---|---|
| **Naming** | `[TestSuite] <activity-or-feature> - <variant-summary>`. Eckige Klammern als Sortier-Anker und Filter-Hook für API-Listings. |
| **Trigger** | Jeder Workflow hat genau einen `scheduleTrigger` mit `cronExpression: "0 0/5 * * * ? *"` (Quartz 7-field, alle 5 Min auf der Minute). Workflows mit mehreren Trigger-Typen (siehe `27-trigger-types.json`) haben den Schedule als Primary plus zusätzliche dokumentarische Trigger. |
| **Folder** | Alle in `/Test-Workflows` (siehe [SharedWorkflowFolder](../src/NodePilot.Core/Models/SharedWorkflowFolder.cs)). |
| **Target Machine** | `targetMachineId: "localhost"` — nutzt den Localhost-Bypass (in-process PowerShell, keine Credentials, kein WinRM). |
| **Sandbox-Pfade** | `C:\Temp\NP-TestSuite\<runbook-name>\` — wird im selben Run angelegt und am Ende per `folderOperation delete` weggeräumt. Kein verwaister State zwischen Runs. |
| **Destructive-Disabled-Pattern** | Knoten die das Test-System verändern würden (shutdown, restart, scheduled-task register, service start/stop, restApi POST/PUT/DELETE auf interne Endpunkte) sind via `data.disabled: true` markiert. Sie validieren das Schema beim Save, feuern aber nie. |
| **Failure-Routing-Pattern** | Wo möglich enthält jedes Runbook einen Knoten der absichtlich fehlschlägt + eine Edge mit `condition: "<step>.failed"` auf einen `log`-Knoten — testet den On-Failure-Pfad ohne den ganzen Run zu killen. |
| **Junction am Ende** | Jeder Runbook konvergiert auf eine Junction (meist `waitAll`) gefolgt von `returnData`. Erlaubt Engine-seitige Detection ob ein Branch hängt. |

## Workflow-Katalog

### Per-Activity-Runbooks (24)

| # | Datei | Workflow-Name | Was wird getestet |
|---|---|---|---|
| 00 | [00-child-helper.json](../scripts/test-runbooks/00-child-helper.json) | `[TestSuite] Child: Echo Item` | Geteiltes Child für `forEach`/`startWorkflow`. Hat zwei Roots (`scheduleTrigger` + `manualTrigger`), echo't Input-Params zurück per `returnData`. |
| 01 | [01-runScript.json](../scripts/test-runbooks/01-runScript.json) | `runScript - All Variants` | engine=`auto`/`pwsh`/`powershell`, structured Output (multi-var capture), Variable-Resolution `{{step.param.x}}`, Custom-Timeout, Retry exponential, deliberate failure + On-Failure-Routing. |
| 02 | [02-fileOperation.json](../scripts/test-runbooks/02-fileOperation.json) | `fileOperation - All Variants` | create → exists(true) → copy → rename → move → exists(false) → delete. Sandbox cleanup. |
| 03 | [03-folderOperation.json](../scripts/test-runbooks/03-folderOperation.json) | `folderOperation - All Variants` | create, create-nested, exists(true/false), list, copy, rename, move, delete. |
| 04 | [04-fileHash.json](../scripts/test-runbooks/04-fileHash.json) | `fileHash - All Algorithms` | MD5/SHA1/SHA256/SHA384/SHA512 parallel auf `notepad.exe` + expected-hash-Mismatch-Failure-Pfad. |
| 05 | [05-zipOperation.json](../scripts/test-runbooks/05-zipOperation.json) | `zipOperation - Compress + Extract` | compress mit allen 3 `compressionLevel` (Optimal/Fastest/NoCompression) + extract round-trip. |
| 06 | [06-serviceManagement.json](../scripts/test-runbooks/06-serviceManagement.json) | `serviceManagement - All Actions` | status auf Spooler/W32Time aktiv, start/stop/restart als DISABLED-Knoten, status auf nicht-existentem Service als Failure-Pfad. |
| 07 | [07-registryOperation.json](../scripts/test-runbooks/07-registryOperation.json) | `registryOperation - All Operations & Types` | createKey → 6× write (String/ExpandString/DWord/QWord/Binary/MultiString) → read-single, read-all, exists-key, exists-value, listValues, listSubKeys → deleteValue → deleteKey. |
| 08 | [08-wmiQuery.json](../scripts/test-runbooks/08-wmiQuery.json) | `wmiQuery - Various Classes & Filters` | 6 Klassen parallel: OS, ComputerSystem, Processor, LogicalDisk (mit Filter), Service (Running-Filter), BIOS. |
| 09 | [09-startProgram.json](../scripts/test-runbooks/09-startProgram.json) | `startProgram - All Variants` | `cmd.exe /c echo` mit Args + Variable-Resolution, custom `successExitCodes` (exit 42 = success), exit 1 als Failure-Pfad, `waitForExit: false` (fire-and-forget). |
| 10 | [10-powerManagement.json](../scripts/test-runbooks/10-powerManagement.json) | `powerManagement - All Actions` | **Workflow ist gesamt DISABLED.** Nur `abort` wäre safe (no-op). shutdown/restart/logoff/hibernate sind als Knoten dokumentiert mit `disabled: true` für Schema-Validation. |
| 11 | [11-scheduledTask.json](../scripts/test-runbooks/11-scheduledTask.json) | `scheduledTask - All Actions` | `get` auf Edge-Updater + non-existent (Failure). start/stop/enable/disable/unregister/register (alle 5 trigger types: once/daily/weekly/atLogon/atStartup) als DISABLED-Knoten — Schema-Validation, keine echten Mutationen. |
| 12 | [12-waitForCondition.json](../scripts/test-runbooks/12-waitForCondition.json) | `waitForCondition - Various Polling Patterns` | `$true` (immediate), Service-Status-Poll (Spooler), File-Existence (mit Vorbereitung), `$false` mit Timeout 4s als Failure-Pfad. |
| 13 | [13-restApi.json](../scripts/test-runbooks/13-restApi.json) | `restApi - All Methods & Proxy Modes` | GET `/healthz/live` + `/healthz/ready` (mit Retry exp), GET 404 Failure-Pfad, alle 3 `proxyMode` (default/direct/custom). POST/PUT/DELETE als DISABLED. |
| 14 | [14-sql.json](../scripts/test-runbooks/14-sql.json) | `sql - All Providers & Connection Variants` | sqlite `:memory:` (single-row, multi-row, Syntax-Error), postgres dev-cluster (Builder), sqlserver Integrated + SQL-Auth (DISABLED), raw `connectionString`, `connectionRef` (DISABLED). |
| 15 | [15-emailNotification.json](../scripts/test-runbooks/15-emailNotification.json) | `emailNotification - Text & HTML` | Plaintext + HTML mit Variable-Resolution in Subject/Body + invalid recipient als Failure. Ohne SMTP-Config alles im Failure-Pfad — testet Routing. |
| 16 | [16-delay.json](../scripts/test-runbooks/16-delay.json) | `delay - Various Durations` | 1s/3s/7s parallel + 3×1s sequenziell. |
| 17 | [17-junction.json](../scripts/test-runbooks/17-junction.json) | `junction - All 3 Modes` | `waitAll` (5 Branches), `waitAny` (3 Branches mit unterschiedlich langen Delays), `waitNofM` (5 Branches, requiredCount=3). |
| 18 | [18-forEach.json](../scripts/test-runbooks/18-forEach.json) | `forEach - All Variants` | itemsFormat=json/lines/auto, maxParallelism=1 und 4, continueOnError, custom item-/index-ParameterName, leere Collection (0 Iterationen Edge-Case), additional static parameters. Ruft `[TestSuite] Child: Echo Item`. |
| 19 | [19-startWorkflow.json](../scripts/test-runbooks/19-startWorkflow.json) | `startWorkflow - Wait + Fire-and-Forget` | `waitForCompletion: true` mit Output-Capture als `param.*`, `false` (fire-and-forget), missing Child Failure-Pfad. |
| 20 | [20-returnData.json](../scripts/test-runbooks/20-returnData.json) | `returnData - Multiple Keys & Templates` | 12 Keys mit Templates aus 2 verschiedenen Upstream-Steps + statische Konstanten + embedded Templates (`host={{x}}|env={{y}}`). |
| 21 | [21-xmlQuery.json](../scripts/test-runbooks/21-xmlQuery.json) | `xmlQuery - Inline + Path + Namespaces + Modes` | source=inline (single/all/count), Namespaces, source=path (mit Vorbereitung). |
| 22 | [22-jsonQuery.json](../scripts/test-runbooks/22-jsonQuery.json) | `jsonQuery - Inline + Path + JSONPath Modes` | Index-Lookup, Wildcard, Filter-Expression `[?(@.active==true)]`, count, exists (true + false), source=path. |
| 23 | [23-log.json](../scripts/test-runbooks/23-log.json) | `log - All Levels & Templates` | info/warning/error + Multi-Line + Template-Resolution (`{{x.success}}`, `{{x.output}}`, `{{x.param.y}}`). |
| 24 | [24-decision.json](../scripts/test-runbooks/24-decision.json) | `decision - Multi-Case Routing` | 2 Decisions hintereinander: env-Routing (==, contains, default), severity-Routing (group AND mit cpu-comparison, group AND mit NOT-isEmpty, default). Edge-Routing per `param.case ==`. |

### Cross-Cutting-Runbooks (5)

| # | Datei | Workflow-Name | Was wird getestet |
|---|---|---|---|
| 25 | [25-edge-conditions.json](../scripts/test-runbooks/25-edge-conditions.json) | `edge-conditions - All 14 Operators + AND/OR/NOT + Legacy + Disabled` | Vollständige Coverage des `ConditionEvaluator`: alle 14 comparison-Operatoren (`==`, `!=`, `<`, `>`, `<=`, `>=`, `contains`, `startsWith`, `endsWith`, `matches`, `isEmpty`, `isNotEmpty`, `isTrue`, `isFalse`), `group` AND, `group` OR, `not`, `disabled` Edge (Target wird Skipped), legacy String-Conditions `stepId.success` / `stepId.failed`, node-level `disabled` (Downstream wird Skipped). |
| 26 | [26-retry-policy.json](../scripts/test-runbooks/26-retry-policy.json) | `retry-policy - Fixed/Linear/Exponential` | Alle 3 Backoff-Modi (fixed/linear/exponential) auf runScript, plus restApi-Retry, plus always-fail-Step der die `maxAttempts` ausschöpft (Failure-Routing). |
| 27 | [27-trigger-types.json](../scripts/test-runbooks/27-trigger-types.json) | `trigger-types - All 6 Trigger Types` | Alle 6 Trigger-Typen schema-validiert: `scheduleTrigger` (ACTIVE), `manualTrigger`, `webhookTrigger`, `fileWatcherTrigger`, `eventLogTrigger` (alle als Roots) + `databaseTrigger` (DISABLED weil kein Test-DB-Connstring). Engine validiert beim Save, dass alle Trigger-Source-Schemas korrekt sind. |
| 28 | [28-performance-stress.json](../scripts/test-runbooks/28-performance-stress.json) | `performance-stress - Deep Parallelism + Multi-Decision + forEach + Retry` | **52 Nodes / 78 Edges.** 12-fach Fan-Out (alle 12 Activity-Typen parallel) → waitAll → 4-Wege-Decision per env → waitAny → 6-Cases-Decision per severity (group AND + NOT) → waitAny → forEach 5×parallel=3 + 3 parallele Retry-Chains → waitAll → 5 parallele Post-Steps → waitAll → final Decision (3 Cases) → waitAny → returnData mit 22 Keys. |

## Coverage-Matrix

### Activities (24/24)
Alle in [TriggerActivityTypes.cs](../src/NodePilot.Core/Constants/TriggerActivityTypes.cs)+`IActivityExecutor`-Implementations registrierten Activities sind abgedeckt:

`runScript` · `fileOperation` · `folderOperation` · `fileHash` · `zipOperation` · `serviceManagement` · `registryOperation` · `wmiQuery` · `startProgram` · `powerManagement` · `scheduledTask` · `waitForCondition` · `restApi` · `sql` · `emailNotification` · `delay` · `junction` · `forEach` · `startWorkflow` · `returnData` · `xmlQuery` · `jsonQuery` · `log` · `decision`

### Trigger-Typen (6/6)
`scheduleTrigger` · `manualTrigger` · `webhookTrigger` · `fileWatcherTrigger` · `databaseTrigger` · `eventLogTrigger`

### Edge-Condition-Operatoren (14/14)
`==` · `!=` · `<` · `>` · `<=` · `>=` · `contains` · `startsWith` · `endsWith` · `matches` · `isEmpty` · `isNotEmpty` · `isTrue` · `isFalse`

### Edge-Condition-Strukturen
`group AND` · `group OR` · `not` · `disabled` Edge · legacy `stepId.success` · legacy `stepId.failed` · empty/null condition (`Always`) · node-level `disabled`

### Junction-Modi (3/3)
`waitAll` · `waitAny` · `waitNofM` (mit `requiredCount`)

### Retry-Backoffs (3/3)
`fixed` · `linear` · `exponential`

### Variable-Resolution
`{{step.output}}` · `{{step.error}}` · `{{step.success}}` · `{{step.param.x}}` · auto-quoting in runScript · embedded templates · multi-source merge in returnData

## Operative Hinweise

### Lastprofil
| Metrik | Pro Run | Pro Stunde (12 Runs) |
|---|---|---|
| Workflow-Executions | 29 | 348 |
| Step-Executions (geschätzt) | ~340 | ~4.080 |
| Sub-Workflow-Calls (forEach + startWorkflow) | ~10 | ~120 |
| WinRM/Localhost-Bypass-Calls | ~80 | ~960 |
| HTTP-Calls (restApi) | ~6 | ~72 |

Mit Default-Engine-Settings (Postgres dev-cluster) läuft das problemlos. Bei reduzierten Pool-Sizes oder SQL-Server-Targets kann's eng werden — `Engine:MaxConcurrent*`-Settings prüfen.

### Disabled-Workflows
Standardmäßig deaktiviert (zur Sicherheit / weil destructive):
- `[TestSuite] powerManagement - All Actions (DESTRUCTIVE DISABLED)` — explizit disabled, alle internen Knoten sind ohnehin node-disabled

Alle anderen 28 sind enabled und feuern alle 5 Min.

### Bulk-Disable / Bulk-Enable
```powershell
# Token holen
$base = 'http://localhost:5000'
$token = (Invoke-RestMethod -Uri "$base/api/auth/login" -Method POST -ContentType 'application/json' `
  -Body (@{username='admin';password='admin123'} | ConvertTo-Json)).token
$h = @{ Authorization = "Bearer $token" }

# Folder-Id holen
$folderId = (Invoke-RestMethod -Uri "$base/api/shared-workflow-folders" -Headers $h |
  Where-Object { $_.name -eq 'Test-Workflows' -and $_.depth -eq 1 }).id

# Alle im Folder disable (cancelt zusätzlich alle laufenden Executions)
Invoke-RestMethod -Uri "$base/api/workflows" -Headers $h |
  Where-Object { $_.folderId -eq $folderId } | ForEach-Object {
    Invoke-RestMethod -Uri "$base/api/workflows/$($_.id)/cancel-all" -Method POST -Headers $h | Out-Null
    Invoke-RestMethod -Uri "$base/api/workflows/$($_.id)/disable" -Method POST -Headers $h | Out-Null
    "disabled: $($_.name)"
  }
```

Bulk-Enable analog mit `/enable` statt `/disable` + `cancel-all`.

### Sandbox-Cleanup
Falls ein Runbook crasht und `C:\Temp\NP-TestSuite\<x>` zurücklässt:
```powershell
Remove-Item -LiteralPath 'C:\Temp\NP-TestSuite' -Recurse -Force -ErrorAction SilentlyContinue
```

### Audit / Beobachtbarkeit
- **Execution-List in der UI:** Filter auf "[TestSuite]" zeigt alle Test-Runs.
- **`GET /api/workflows/{id}/coverage?windowDays=N`:** liefert pro Step `executedCount`/`failedCount`/`skippedCount`. Ideal für Drift-Detection (Wert sollte ~12/h pro Step liegen, abweichende Werte zeigen broken Branches an).
- **Support-Events:** `GET /api/diagnostics/support-events?eventType=EXECUTION_STARTED` zeigt die Engine-Starts einschließlich Trigger-Kontext. `EXECUTION_STARTED` im Audit-Log ist auf manuell gestartete Runs beschränkt.
- **Coverage-Heatmap im Editor:** Toolbar-Toggle (Target-Icon) tinted Knoten je nach Run-Frequenz.

## Wartung & Erweiterung

### Re-Import nach Änderung an JSON-Quellen
Der Import-Endpoint kennt **kein Update** — er erzeugt immer neue Workflows mit `(Imported 2)`-Suffix bei Namenskollision. Workflow-Updates per UI machen, oder vorher via API löschen + re-importieren.

```powershell
# Beispiel: einzelnes Runbook löschen + re-importieren
$wf = Invoke-RestMethod -Uri "$base/api/workflows" -Headers $h |
  Where-Object { $_.name -eq '[TestSuite] runScript - All Variants' }
Invoke-RestMethod -Uri "$base/api/workflows/$($wf.id)" -Method DELETE -Headers $h
$envelope = Get-Content -LiteralPath 'e:\NodePilot\scripts\test-runbooks\01-runScript.json' -Raw
$resp = Invoke-RestMethod -Uri "$base/api/workflows/import" -Method POST -Headers $h `
  -Body $envelope -ContentType 'application/json'
# Move zurück in Folder
Invoke-RestMethod -Uri "$base/api/workflows/$($resp.workflows[0].id)/move-folder" -Method POST `
  -Headers $h -Body (@{TargetFolderId=$folderId} | ConvertTo-Json) -ContentType 'application/json'
```

### Neuen Runbook anlegen
1. Datei `scripts/test-runbooks/<NN>-<name>.json` anlegen (NN = nächste freie Nummer)
2. Envelope-Format wie die anderen: `schema=nodepilot-workflow-export/v1`, `exportVersion=1`, `workflows[]`
3. Pflichtkonventionen:
   - Workflow-Name `[TestSuite] <feature> - <variants>`
   - Genau ein `scheduleTrigger` mit `cronExpression: "0 0/5 * * * ? *"`
   - Sandbox unter `C:\Temp\NP-TestSuite\<runbook>\` falls Filesystem-Mutation
   - Cleanup-Knoten am Ende
   - Destructive Actions als `disabled: true`
   - `targetMachineId: "localhost"` für Remote-Activities
4. JSON validieren:
   ```powershell
   Get-Content -LiteralPath 'scripts\test-runbooks\NN-name.json' -Raw | ConvertFrom-Json | Out-Null
   ```
5. Importieren + in Folder verschieben (siehe Re-Import-Block)

### Wenn neue Activity hinzugefügt wird
Wer eine neue `IActivityExecutor`-Implementation merged, soll auch ein Runbook in dieser Suite anlegen. Drift-Schutz: ein Test in `tests/NodePilot.Engine.Tests/` der die `IActivityExecutor`-Liste gegen die Runbook-Files asserted, wäre wünschenswert (TODO, noch nicht implementiert).

## Bekannte Limitierungen

- **`emailNotification`** schlägt fehl wenn keine `Smtp:*`-Config gesetzt ist. Das ist der erwartete Pfad in Dev — testet Routing, nicht echten Mail-Versand.
- **`sql`-Postgres-Step** funktioniert nur mit dem Dev-Cluster auf `127.0.0.1:5432` mit `postgres/postgres`. Mit anderem Setup wird's roter Step im Dashboard.
- **`registryOperation`** schreibt unter `HKCU:\SOFTWARE\NP-TestSuite-Reg` — wird im selben Run wieder gelöscht, aber bei einem Crash kann der Key zurückbleiben. Manuell weg: `Remove-Item HKCU:\SOFTWARE\NP-TestSuite-Reg -Recurse`.
- **`startProgram` exit-1-Knoten** taucht im Audit-Log als Failure auf — das ist gewollt (testet Failure-Routing). Nicht als echten Bug interpretieren.
- **`scheduledTask get` auf Edge-Updater** schlägt fehl wenn Microsoft Edge nicht installiert ist. Auf dem Default-Win11-Test-Host ist er da.
- **`forEach` Child-Workflow-Lookup** ist by-name (exact-case gewinnt, sonst case-insensitive). Wer `[TestSuite] Child: Echo Item` umbenennt, bricht 4 Runbooks (18, 19, 28, 00 selbst). Falls Renaming nötig: alle Referenzen in den anderen Runbooks mit-updaten.

## Referenzen

- Workflow-JSON-Format: [CLAUDE.md → Workflow-JSON Format](../CLAUDE.md)
- Activity-Typen-Übersicht: [CLAUDE.md → Activity-Typen](../CLAUDE.md)
- Workflow-Layout-Styleguide: [docs/workflow-styleguide.md](workflow-styleguide.md)
- Master-Beispiel (Pre-TestSuite): [scripts/test-master-all-activities.json](../scripts/test-master-all-activities.json)
- Folder-RBAC-Doku: [docs/enterprise-features.md](enterprise-features.md) (Shared Folders / RBAC) + CLAUDE.md → Autorisierung
