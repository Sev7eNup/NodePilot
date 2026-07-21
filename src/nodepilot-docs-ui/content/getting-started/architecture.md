# Architektur

NodePilot ist eine Solution aus mehreren .NET-Projekten plus einer React-SPA. Klare Dep-Graph, kein Shared-Heavy-Domain-Layer.

## Solution-Struktur

```
src/
  NodePilot.Core/       Domain Models, Interfaces, Enums (keine Abhängigkeiten)
  NodePilot.Ai/         LLM-Stack: ILlmClient/OpenAI-Transport + SSRF-Guard, Prompt-Katalog, Script-/Workflow-Gen + Chat-Assistent
  NodePilot.Data/       EF Core DbContext, CredentialStore, MigrationBootstrapper
  NodePilot.Remote/     WinRM Session Factory + Session, NoOp-Factory
  NodePilot.Engine/     WorkflowEngine, ActivityRegistry, Activities, Trigger
  NodePilot.Scheduler/  TriggerOrchestrator + ITriggerSource-Impl.
  NodePilot.Telemetry/  OTel-Setup, Constants, Options, PrometheusClient
  NodePilot.Api/        ASP.NET Core Host, Controller, SignalR Hub, DTOs
  NodePilot.Cli/        `np` CLI — dotnet global tool, Spectre.Console.Cli
  NodePilot.Mcp/        `nodepilot-mcp` MCP-Server — dotnet global tool, HTTP-only
  nodepilot-ui/         React SPA (Vite + Tailwind + React Flow)
  nodepilot-docs-ui/    Diese Doku-Site (Vite + React SPA)
tests/
  NodePilot.Engine.Tests / Ai.Tests / Data.Tests / Api.Tests / Cli.Tests / Mcp.Tests
  NodePilot.LoadTests / TestCommons
```

## Dep-Graph

```
Api -> Ai, Engine, Scheduler, Data, Remote, Core, Telemetry
Engine -> Ai, Data, Remote, Core, Telemetry
Ai -> Core
Data -> Core
Remote -> Core
Telemetry -> Core
Cli -> Core   (HTTP-only, kein direkter Engine-Zugriff)
Mcp -> Core   (HTTP-only, MCP-Server)
```

`NodePilot.Core` hat **keine** Abhängigkeiten — Domain-Modelle und Interfaces leben dort.

## Tech-Stack

| Schicht | Technologie |
|---|---|
| Backend | ASP.NET Core Web API, .NET 10, Windows-only (`net10.0-windows`) |
| Frontend | React 19, TypeScript, Tailwind CSS 4, Vite 8, @xyflow/react (React Flow v12) |
| Datenbank | PostgreSQL (default) / SQL Server (`Database:Provider`) |
| Remote Execution | PowerShell SDK / WinRM, agentless |
| Real-time | SignalR (`/hubs/execution`) |
| Auth | JWT Bearer + BCrypt, Rollen Admin / Operator / Viewer |
| Scheduling | Quartz.NET + `TriggerOrchestrator` |
| Logging | Serilog (`text` / `cmtrace` / `json` / `ecs-json`) |
| Observability | OpenTelemetry opt-in, Prometheus-Scrape |
| Hosting | Windows Service (`Microsoft.Extensions.Hosting.WindowsServices`) |

## Execution-Modell

- **Event-driven:** Queue + `inFlight`-Dict. Roots = **ausschließlich aktive Trigger-Nodes** (jeder Trigger-Typ); ohne aktiven Trigger → 0 Roots → Execution `Failed`. Disabled Nodes nie Root. Orphan-/Nicht-Trigger-Activities ohne eingehende Edge → `Skipped`.
- **Per-Step-DI-Scope:** eigener Scope pro Step → scope-lokaler `DbContext`.
- **Cancellation:** `_runningExecutions`-Dict (Guid → CTS).
- **Startup-Reconciler:** hängende `Running`/`Pending`/`Paused` → `Cancelled`.
- **Sub-Workflows:** `startWorkflow` ruft Child-Workflow in frischem DI-Scope auf. Max Call-Depth: 10.

## Konfigurierbarkeit

Fast alles ist über `appsettings.json` steuerbar — Provider (DB, Remote, Secrets), Hardening-Flags, Retention, Logging-Format, HA, Auth-Pfade. Siehe [Konfiguration](../configuration/appsettings).