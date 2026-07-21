# Einführung

**NodePilot** ist ein moderner, schlanker Ersatz für Microsoft System Center Orchestrator (SCOrch). Es orchestriert Workflows in Windows-Umgebungen **agentless** via WinRM — auf den Zielmaschinen muss keine Software installiert werden.

## Was NodePilot macht

NodePilot lässt sich als **grafischer Workflow-Designer** plus **event-driven Execution-Engine** plus **REST/SignalR-API** plus **`np`-CLI** zusammenfassen:

- **Designer** (React SPA): Workflows werden auf einer Canvas aus Nodes und Edges gebaut — pro Activity ein eigener Node-Typ mit Shape, Icon und Properties-Panel.
- **Engine** (.NET 10): Führt Workflows event-driven aus — Queue + inFlight-Dict, parallele Steps, per-Step DI-Scope, Retry, Timeout, Step-Debugger mit Breakpoints.
- **Scheduler**: `TriggerOrchestrator` scannt alle 5 s alle Trigger-Quellen (Schedule, FileWatcher, Database, EventLog, Webhook, Manual).
- **API**: ASP.NET Core Web API + SignalR für Realtime-Updates (`/hubs/execution`).
- **CLI `np`**: `dotnet global tool`, reiner HTTP-Client gegen die REST-Endpoints.

## Activity-Scopes

Jede Activity läuft in einem von drei Scopes:

| Scope | Bedeutung |
|---|---|
| **Remote** | Ausführung auf der Zielmaschine via `targetMachineId` / WinRM |
| **Engine-local** | Ausführung im API-Prozess (z. B. REST, SQL, E-Mail, Control-Flow) |
| **Hybrid** | Beides — `runScript` und `waitForCondition` |

## Datenfluss

Steps produzieren Outputs, die im **Datenbus** landen und downstream per Template aufgelöst werden:

```
{{varName.output}}      # Stdout
{{varName.error}}       # Stderr
{{varName.success}}     # "true" / "false"
{{varName.param.xxx}}   # deklarierter OutputParameter
{{globals.NAME}}        # Globale Variable
```

Edges steuern den Kontrollfluss über **Conditions** (`stepId.success`, `stepId.failed`, strukturierte Vergleichs-Ausdrücke, AND/OR/NOT-Gruppen).

## Wo es weitergeht

- [Installation](./installation) — Postgres starten, Backend + Frontend hochfahren.
- [Quickstart](./quickstart) — Ersten Login, ersten Workflow, ersten Run.
- [Konzepte](../concepts/workflows) — Workflows, Activities, Trigger, Datenbus im Detail.