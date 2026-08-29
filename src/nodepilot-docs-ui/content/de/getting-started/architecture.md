# Architektur

NodePilot besteht aus einem .NET-Backend, einer React-Oberfläche und einer relationalen Datenbank. Das Backend enthält die Workflow-Engine, stellt die REST-API bereit und liefert im Produktivbetrieb auch die Weboberfläche aus.

## Laufzeitübersicht

```text
Browser oder Electron
        |
        | HTTPS / SignalR
        v
NodePilot.Api
  |-- Workflow-Engine und Scheduler
  |-- REST-API und Authentifizierung
  |-- Remote-Ausführung über WinRM
  |
  v
PostgreSQL oder SQL Server
```

Remote-Windows-Systeme benötigen keine NodePilot-Installation. Die Verbindung erfolgt vom NodePilot-Host über WinRM.

## Solution-Struktur

```text
src/
  NodePilot.Core/       Domänenmodelle, Interfaces und Enums
  NodePilot.Ai/         LLM-Transport, Prompts und AI-Funktionen
  NodePilot.Data/       EF Core, Datenbankzugriff und Migrationen
  NodePilot.Remote/     WinRM-Sitzungen und Remote-Ausführung
  NodePilot.Engine/     Workflow-Engine, Activities und Trigger
  NodePilot.Scheduler/  Zeitpläne und Trigger-Orchestrierung
  NodePilot.Telemetry/  OpenTelemetry und Prometheus
  NodePilot.Api/        ASP.NET-Core-Host, Controller und SignalR
  NodePilot.Cli/        Kommandozeilenwerkzeug `np`
  NodePilot.Mcp/        MCP-Server `nodepilot-mcp`
  nodepilot-ui/         Produktoberfläche
  nodepilot-docs-ui/    Dokumentationsoberfläche (auch mitausgeliefert, unter /docs)
tests/
  ...                   Unit-, Integrations- und Lasttests
```

## Abhängigkeitsrichtung

```text
Api -> Ai, Engine, Scheduler, Data, Remote, Core, Telemetry
Engine -> Ai, Data, Remote, Core, Telemetry
Ai -> Core
Data -> Core
Remote -> Core
Telemetry -> Core
Cli -> Core
Mcp -> Core
```

`NodePilot.Core` besitzt keine Projektabhängigkeiten. CLI und MCP greifen ausschließlich über HTTP auf eine laufende NodePilot-Instanz zu.

## Technologiestack

| Bereich | Technologie |
|---|---|
| Backend | ASP.NET Core Web API, .NET 10, Windows (`net10.0-windows`) |
| Oberfläche | React 19, TypeScript, Tailwind CSS 4, Vite 8, React Flow |
| Datenbank | PostgreSQL oder SQL Server |
| Remote-Ausführung | PowerShell SDK und WinRM |
| Live-Updates | SignalR unter `/hubs/execution` |
| Authentifizierung | JWT-Sitzungen; lokale Konten und optionale Enterprise-Provider |
| Rollen | Admin, Operator und Viewer |
| Scheduling | Quartz.NET und `TriggerOrchestrator` |
| Logging | Serilog mit Text-, CMTrace-, JSON- oder ECS-JSON-Ausgabe |
| Telemetrie | OpenTelemetry und Prometheus |
| Produktiv-Hosting | Windows-Dienst; Desktop zusätzlich mit Electron |

## Ausführungsmodell

1. Ein aktiver Trigger startet eine Execution.
2. Die Engine stellt ausführbare Nodes in eine interne Queue.
3. Jede Activity läuft in einem eigenen Dependency-Injection-Scope mit eigenem Datenbankkontext.
4. Erfolgreiche oder fehlgeschlagene Ergebnisse bestimmen die folgenden Edge-Pfade.
5. SignalR überträgt Statusänderungen an die Oberfläche.
6. Ein Neustart markiert verwaiste laufende oder pausierte Executions als abgebrochen.

Ein Workflow ohne aktiven Trigger besitzt keinen Startpunkt und schlägt beim Start fehl. Sub-Workflows laufen in einem neuen Scope; die maximale Aufruftiefe beträgt 10.

## Konfiguration

`appsettings.json`, umgebungsspezifische Dateien und Umgebungsvariablen steuern Datenbank, Remote-Ausführung, Secret-Provider, Sicherheitsregeln, Retention, Logging, Authentifizierung und Hochverfügbarkeit. Details enthält die [Konfigurationsübersicht](../configuration/appsettings).
