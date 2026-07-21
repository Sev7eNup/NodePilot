# NodePilot Tech-Demo — Full Showcase

Ein Muster-Workflow, der jede Activity, jeden Edge-Condition-Operator und jede Control-Flow-Variante demonstriert, die NodePilot unterstützt.

## Dateien

| Datei | Zweck |
|---|---|
| `main.json` | Haupt-Workflow (37 Nodes, ~55 Edges) |
| `child.json` | Kleiner Sub-Workflow (3 Nodes), den der Haupt-Workflow via `startWorkflow` aufruft |
| `seed.ps1` | Idempotentes Seed-Script — loggt ein und POSTet beide Workflows nach `/api/workflows` |

Die JSON-Dateien werden **nicht** via `/api/workflows/import` geladen. `seed.ps1` liest sie, embedded ihren Inhalt als `DefinitionJson`-String in `CreateWorkflowRequest` und schickt sie an den regulären `POST /api/workflows`-Endpoint.

## Aufruf

Backend muss laufen (Default `http://localhost:5000`).

```powershell
# Aus dem Repo-Root:
./scripts/tech-demo/seed.ps1

# Oder mit expliziten Parametern:
./scripts/tech-demo/seed.ps1 -BaseUrl http://localhost:5000 -AdminUser admin -AdminPassword 'Abcd1234!' -Force
```

Das Script erkennt automatisch, ob NodePilot im Bootstrap-Modus ist (leere User-Tabelle + `admin-setup.token` vorhanden) und nutzt dann den `X-Setup-Token`-Header für den First-Admin-Login. Ansonsten fragt es das Passwort normal ab.

Bei bereits existierenden Workflows gleichen Namens wird gefragt: `k` = behalten, `r` = löschen + neu anlegen, `a` = abbrechen. Mit `-Force` wird immer ersetzt.

## Was der Workflow zeigt

### Activities (alle 18)

`manualTrigger`, `log`, `delay`, `runScript`, `fileOperation`, `folderOperation`, `serviceManagement`, `registryOperation`, `wmiQuery`, `startProgram`, `junction`, `restApi`, `sql`, `xmlQuery`, `jsonQuery`, `emailNotification`, `startWorkflow`, `returnData`.

### Edge-Condition-Operatoren (alle 14)

- **Numerisch:** `==` `!=` `<` `>` `<=` `>=`
- **String:** `contains` `startsWith` `endsWith` `matches`
- **Unär:** `isEmpty` `isNotEmpty` `isTrue` `isFalse`
- **Kombinatoren:** `AND`-Gruppe, `OR`-Gruppe, `NOT`
- **Legacy-Shortcuts:** `stepId.success`, `stepId.failed`

Jeder Phase-E-Branch in `main.json` ist eine Kondition, die einen oder mehrere dieser Operatoren zeigt.

### Junction-Modi (alle 3)

- `waitAll` → `junction-all` (Phase D, wartet auf 5 Parallel-Branches)
- `waitAny` → `junction-match`, `junction-rest`, `junction-any`, `junction-final` (feuert auf den ersten erfolgreichen Branch)
- `waitNofM` → `junction-nofm` (`requiredCount: 2` von 3 Branches)

### Weitere Features

- **Retry-Policy:** `collect-host` (exponential 2x), `prog-cmd` (linear 3x), `rest-get` (exponential 3x)
- **Step-Timeouts:** `collect-host` (60s), `rest-get` (15s), Remote-Activities (30s)
- **Node-Level `disabled`:** `log-disabled-node` → erscheint als `Skipped`
- **Node-Level `breakpoint`:** `log-breakpoint` → pausiert im Debug-Run
- **Edge-Level `disabled`:** `email-notify` → `log-nowhere` Edge feuert nie
- **Error-Path:** `rest-get.failed` → `log-rest-fail` (Demo für On-Failure-Branching)
- **Sub-Workflow:** `invoke-child` ruft `child.json`-Workflow auf, dessen `returnData` als `{{child.param.*}}` zurückkommt

## Testen in der UI

1. Workflow öffnen (Link kommt vom Seed-Script).
2. **Layout-Check:** LTR, keine überlappenden Edges, 11 Phasen klar voneinander getrennt.
3. **Run** klicken → Parameter-Dialog mit Defaults:
   - `environment` = `staging` (lass so, um `log-eq-neq` zu skippen; setze auf `production`, um den Branch zu feuern)
   - `threshold` = `80` (disk-alarm-Schwelle)
   - `dryRun` = `false`
   - `pattern` = `Windows.*`
4. **Start** → Live-Status via SignalR, ~15 Sekunden Laufzeit.
5. Nach dem Lauf: **Execution-Details** öffnen, Step-Status prüfen:
   - `log-disabled-node` sollte `Skipped` sein
   - `log-nowhere` sollte nicht in der Step-Liste sein (Edge disabled → Node unreachable → Skipped)
   - `email-notify` schlägt fehl (kein SMTP konfiguriert), das ist erwartet — der "Always"-Edge führt weiter
   - `rest-get`: Status hängt von httpbin.org-Erreichbarkeit ab, beide Pfade (Success→SQL, Failure→log) führen über `junction-rest` zurück auf den Main-Path

**Debug-Test:** "Run with Debug" → Workflow pausiert bei `log-breakpoint` → Variable-Inspector zeigt `host.param.*`, `rest.param.*` etc. → "Continue" läuft weiter.

## Verifizierte Engine-Details (wichtig beim Editieren)

| Detail | Wert |
|---|---|
| `waitNofM`-Config-Key | `requiredCount` (NICHT `n` — im bestehenden `samples/all-activities-horizontal.workflow.json` ist das falsch als `n` und wird silent auf Default 1 zurückgesetzt) |
| Boolean-Truthiness | Leer, `"false"` (case-insensitive), `"0"` = falsy. Alles andere truthy. PowerShell-`[string]$true` = `"True"` → truthy ✓ |
| `runScript`-Param-Capture | Alle lokal deklarierten Variablen werden automatisch als `param.*` exponiert (ProcessExecutionEngine hängt einen Capture-Block ans Script). Kein Marker nötig. |
| `{{var.output}}` in Script-Text | Wird als PowerShell-single-quoted-String eingesetzt (`'value'`). Nicht selbst zusätzlich quoten. |
| `targetMachineId: "localhost"` | Löst Localhost-Bypass aus → läuft in-process ohne WinRM-Session. Ideal für Demo. |
