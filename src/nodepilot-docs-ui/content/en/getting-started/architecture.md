# Architecture

NodePilot consists of a .NET backend, a React interface and a relational database. The backend contains the workflow engine, provides the REST API, and in production also serves the web interface.

## Runtime overview

```text
Browser or Electron
        |
        | HTTPS / SignalR
        v
NodePilot.Api
  |-- Workflow engine and scheduler
  |-- REST API and authentication
  |-- Remote execution over WinRM
  |
  v
PostgreSQL or SQL Server
```

Remote Windows systems need no NodePilot installation. The connection is made from the NodePilot host over WinRM.

## Solution structure

```text
src/
  NodePilot.Core/       Domain models, interfaces and enums
  NodePilot.Ai/         LLM transport, prompts and AI features
  NodePilot.Data/       EF Core, database access and migrations
  NodePilot.Remote/     WinRM sessions and remote execution
  NodePilot.Engine/     Workflow engine, activities and triggers
  NodePilot.Scheduler/  Schedules and trigger orchestration
  NodePilot.Telemetry/  OpenTelemetry and Prometheus
  NodePilot.Api/        ASP.NET Core host, controllers and SignalR
  NodePilot.Cli/        The `np` command-line tool
  NodePilot.Mcp/        The `nodepilot-mcp` MCP server
  nodepilot-ui/         Product interface
  nodepilot-docs-ui/    Documentation interface
tests/
  ...                   Unit, integration and load tests
```

## Dependency direction

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

`NodePilot.Core` has no project dependencies. The CLI and MCP server access a running NodePilot instance over HTTP only.

## Technology stack

| Area | Technology |
|---|---|
| Backend | ASP.NET Core Web API, .NET 10, Windows (`net10.0-windows`) |
| Interface | React 19, TypeScript, Tailwind CSS 4, Vite 8, React Flow |
| Database | PostgreSQL or SQL Server |
| Remote execution | PowerShell SDK and WinRM |
| Live updates | SignalR at `/hubs/execution` |
| Authentication | JWT sessions; local accounts and optional enterprise providers |
| Roles | Admin, Operator and Viewer |
| Scheduling | Quartz.NET and the `TriggerOrchestrator` |
| Logging | Serilog with text, CMTrace, JSON or ECS-JSON output |
| Telemetry | OpenTelemetry and Prometheus |
| Production hosting | A Windows service; on the desktop additionally Electron |

## Execution model

1. An active trigger starts an execution.
2. The engine puts runnable nodes into an internal queue.
3. Every activity runs in its own dependency-injection scope with its own database context.
4. Successful or failed results determine which edge paths follow.
5. SignalR pushes status changes to the interface.
6. A restart marks orphaned running or paused executions as cancelled.

A workflow without an active trigger has no starting point and fails when started. Sub-workflows run in a new scope; the maximum call depth is 10.

## Configuration

`appsettings.json`, environment-specific files and environment variables control the database, remote execution, secret providers, security rules, retention, logging, authentication and high availability. Details are in the [configuration overview](../configuration/appsettings).
