# Muster- & Test-Workflows

Kuratiertes, importierbares Set an Beispiel-Workflows, das **jede** der 27 Aktivitäten und 6 Trigger
abdeckt — einzeln und in Kombination. Import über die UI (`Import`) oder `POST /api/workflows/import`.

## Das saubere Muster-Set

| Datei | Inhalt |
|---|---|
| `muster-alle-aktivitaeten.json` | **Master**: 1 Workflow der alle 27 Aktivitäten in Variation anwendet (alle 14 Edge-Operatoren, AND/OR/NOT, disabled Edges/Nodes, Retry, alle 3 Junction-Modi, decision/forEach/startWorkflow) + Child. Roots: manualTrigger (aktiv) + scheduleTrigger (disabled, demonstrativ). |
| `muster-einzeltests.json` | **33 Einzeltests** — je 1 Workflow pro Aktivität (`Test — <activity>`) und pro Trigger. Jeder Aktivitäts-Workflow spielt **alle (sicheren) Varianten** seiner Aktivität durch (z.B. fileOperation create/exists/copy/rename/move/delete, folderOperation +list, fileHash SHA256/SHA1/MD5/SHA384/SHA512, zipOperation compress+extract, registry createkey/write/read/exists/deletevalue/deletekey, textFileEdit append/prepend/insert/replace/replaceline/delete, waitForCondition script/pathExists/serviceRunning/portOpen/httpOk, generateText alle 7 Modi, junction alle 3 Modi, forEach json+lines, startWorkflow wait+fire-forget). Destruktive Varianten (serviceManagement stop/restart, powerManagement shutdown, scheduledTask enable/disable) werden **nicht** gegen echte Ressourcen ausgeführt. Plus gemeinsamer `Muster Test: Child`. |
| `muster-kombinationen.json` | **Kombinationen/Topologie**: `Muster — Trigger → Databus` (beweist, dass Trigger-Output-Parameter auf dem Databus landen), `Muster — Variable-Pipe` (Databus-Durchreichung runScript→jsonQuery→decision) und `Muster — Sub-Workflow (Parent)` + Child (startWorkflow + forEach Fan-out). |

Remote-Aktivitäten nutzen `targetMachineId: "localhost"` → laufen via **Localhost-Bypass in-process**
auf dem API-Host, sind also ohne WinRM-Ziel real ausführbar.

**Umgebungsabhängige Nodes** (Config korrekt, Ausführung host-/config-abhängig): `emailNotification`
(braucht SMTP), `llmQuery` (braucht `Llm:Enabled=true`), `scheduledTask` (braucht funktionierende
Task-Scheduler-CIM auf dem Ziel).

Reine Hintergrund-Trigger-Tests (`scheduleTrigger`/`webhookTrigger`/`databaseTrigger`/
`fileWatcherTrigger`/`eventLogTrigger`) werden **disabled** importiert, damit sie nicht im Hintergrund
feuern/pollen — zum Testen einfach aktivieren.

## Trigger-Output-Parameter auf dem Databus

Jeder Trigger publisht seine Event-Daten auf den Databus. Verifiziert (fileWatcher → `filePath`/
`fileName`/`fileAction`, webhook → `webhookBody`/`webhookMethod`/`webhookPath` + JSONPath-`fieldMappings`,
manual → deklarierte Parameter). **Lies sie über `{{<triggerNode>.param.<key>}}`** — das ist der
universelle, Contract-korrekte Weg, der in Engine-local-Configs (log/returnData) auflöst. `{{manual.<key>}}`
ist eine flache runScript-Variable und bleibt in Configs Literal — daher nutzen alle Trigger-Muster
`{{trg.param.X}}`.

> **Wichtig:** Trigger-ausgelöste Läufe brauchen einen *effective principal* (`Workflow.PublishedByUserId`),
> sonst werden sie mit `missing_effective_principal` abgebrochen (Enterprise-SSO-Härtung). Import setzt
> ihn nicht — Workflow **publishen** (nicht nur enablen) oder `PublishedByUserId` setzen, dann feuern
> Trigger-Läufe sauber durch.

## Referenz / Anker (nicht Teil des Import-Sets)

- `test-master-all-activities.json` — lebendes **Styleguide-Referenz-Beispiel** (siehe `docs/workflow-styleguide.md`)
  und Few-Shot für die KI-Workflow-Generierung (`src/NodePilot.Ai/Prompts/workflow-example.json`). Nicht verändern.

## Realistische Betriebs-Beispiele (hand-gebaut)

- `example-windows-update-health-workflow.json` — Windows-Update-Health-Check eines Hosts (CBS-/WU-Log-Tails, Service-/Registry-/WMI-/FS-Probes, `decision`-Klassifikation).
- `endsystem-log-korrelation-workflow.json` — **Stündliche KI-Log-Korrelation** über drei Endsysteme: SCCM-Server (CCM/CBS/VSS), Billing-Block (konfigurierbare App-Logs) und PostgreSQL-DB-Server. `scheduleTrigger` (`0 0 * * * ? *`) + manueller Run; je System `runScript`-Sammler → `llmQuery`-Triage, dann `waitAll` → `llmQuery`-Korrelation (`jsonMode`) → `jsonQuery` (severity/summary) → `decision` → Log + `returnData`. Konfiguration (Hosts, Log-Pfade, Tail, Fehler-Regex) im **CONFIG-Block des `init`-Nodes**. Braucht `Llm:Enabled=true`; die drei Ziel-Hosts sind Platzhalter-Hostnamen → für echten WinRM-Lauf via `/api/machines` + `/api/credentials` registrieren und im CONFIG-Block eintragen.

## Kreative Demo-Workflows (hand-gebaut)

`dog-care-workflow.json`, `rose-garden-care-workflow.json`, `example-uboot-workflow.json`,
`decorative-flower.json`, `male-health-workflow.json`.
