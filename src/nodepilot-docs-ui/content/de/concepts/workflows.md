# Workflows & Activities

Ein Workflow ist ein gerichteter Ablauf. Nodes stellen Trigger und Activities dar. Edges verbinden Nodes und können Bedingungen enthalten. Die Engine startet am Trigger und aktiviert anschließend alle erreichbaren Pfade.

## Aufbau

- **Trigger-Nodes** sind die Roots eines Laufs (`manualTrigger`, `scheduleTrigger`, …) und injizieren Event-Daten als `{{manual.*}}`-Variablen.
- **Activity-Nodes** sind die Arbeitsschritte. Jede Activity hat einen `activityType`, optional ein `targetMachineId` (Remote) und ein `config`-Objekt.
- **Edges** verbinden Nodes und tragen **Conditions**, die entscheiden, ob der Target-Node ausgeführt wird.

## Activity-Scopes

| Scope | Ausführung |
|---|---|
| **Remote** | Auf der Zielmaschine via `targetMachineId` / WinRM |
| **Engine-local** | Im API-Prozess |
| **Hybrid** | Beides (`runScript`, `waitForCondition`) |
| **ControlFlow** | Engine-local, Kategorie `ControlFlow` im `ActivityCatalog` (Palette-Achse, unabhängig vom Scope) |

Die vollständige Liste aller 27 Activity-Typen mit Config-Keys und Output-Semantik: [Activity-Referenz](../activities-reference).

## Execution-Lifecycle

Ein Workflow-Lauf (`POST /execute`, asynchron, `202` + `ExecutionId`) durchläuft pro Step:

1. Auflösung der Templates im `config` gegen den Datenbus.
2. Ausführung der Activity im per-Step DI-Scope.
3. Schreiben der Outputs (`output`, `error`, `success`, `param.*`) in den Datenbus.
4. Evaluation der ausgehenden Edge-Conditions → Scheduling der Target-Nodes.

Step-Status: `Pending`, `Running`, `Succeeded`, `Failed`, `Skipped`, `Paused`.

## Trigger nach Neustart und Failover

**Es wird nichts nachgeholt.** Jede Trigger-Quelle führt einen durablen Cursor, aber der dient der
Deduplizierung und der Diagnose — nicht dem Backfill. Beim Start spult jede Quelle ihn auf den
aktuellen Stand vor, ohne zu feuern, und schreibt eine Log-Zeile plus den Zähler
`nodepilot.scheduler.triggers.fires_skipped` über die Größe des übersprungenen Fensters. Ohne diese
Regel erzeugt ein Minutentakt 60 Läufe je Stunde Stillstand — und zwar je Workflow.

Der **laufende** Betrieb ist davon unberührt: Ein Signal, das eine aktive Quelle bereits beobachtet
hat, wird wiederholt, bis die Datenbank es annimmt; FileWatcher und EventLog holen weiterhin
Benachrichtigungen nach, die ihnen im laufenden Betrieb entgehen.

Der Preis wird ausdrücklich benannt: Dateien und EventLog-Einträge, die entstehen, während NodePilot
gestoppt ist, ein Failover läuft oder kein Leader existiert, werden nicht verarbeitet. Wo eine Last
das nicht verlieren darf, gehört eine durable Queue davor statt eines Triggers.

## Retry & Timeout

- **Retry pro Step:** `config.retry` mit `maxAttempts`, `backoff`, `initialDelayMs`, `maxDelayMs`.
- **Execution-Timeout:** `timeoutSeconds` im Execute-Body + per-Step `config.timeoutSeconds`.

## Disabled Nodes & Edges

- `data.disabled: true` → Node wird `Skipped`; Downstream ohne andere Quellen ebenfalls.
- `disabled: true` auf einer Edge → Target-Node wird nicht zum Root.
- **Kein (aktiver) Trigger** (trigger-los **oder** nur Zyklen) → 0 Roots → `Failed` mit ErrorMessage + Warning. Roots = ausschließlich Trigger-Nodes (kein `inDegree==0`-Fallback).
- **Leerer Workflow** (0 Nodes) → läuft mit 0 Steps durch (`Succeeded`).

## Version-History & Edit-Lock

`Update` / `Rollback` snapshotten die vorherige Definition. Ein per-User Edit-Lock (`CheckedOutByUserId` + `CheckedOutAt`) schützt vor Parallel-Edits — mutierende Endpoints liefern `423 Locked`, wenn der Caller nicht Lock-Owner ist. Details: [Workflow-Kontrollfluss](../api/workflow-control).
