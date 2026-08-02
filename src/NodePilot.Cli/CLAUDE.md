# NodePilot.Cli (`np`) — Konventionen

Gilt für `src/NodePilot.Cli/`. Projektweite Regeln stehen in der Root-`CLAUDE.md`.

Reiner HTTP-Client gegen die REST-Endpoints (Spectre.Console.Cli), ausgeliefert per `dotnet publish` — **kein** `dotnet global tool` (`PackAsTool` × `net10.0-windows` = NETSDK1146). Befehlsbereiche: `auth`, `workflow`, `exec`, `machine`, `credential`, `globals`, `user`, `shared-folder`, `maintenance`, `alerting`, `system-alert`, `audit`, `backup`, `db`, `cron`, `health`, `dashboard`, `operations`, `observability`, `settings`, `secrets`, `config`. Details: `docs/claude-reference.md`.

**Architektur-Konvention:** Neuer API-Endpoint → parallel Methode in `NodePilotApiClient.cs` + Command anlegen. DTOs in `Cli/Api/Dtos/` duplizieren (kein ProjectReference).
