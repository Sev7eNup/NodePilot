# NodePilot.Cli (`np`) — Konventionen

Gilt für `src/NodePilot.Cli/`. Projektweite Regeln stehen in der Root-`CLAUDE.md`.

Reiner HTTP-Client gegen die REST-Endpoints, `dotnet global tool` (Spectre.Console.Cli). Befehlsbereiche: `auth`, `workflow`, `exec`, `machine`, `credential`, `globals`, `user`, `shared-folder`, `maintenance`, `alerting`, `system-alert`, `audit`, `backup`, `db`, `cron`, `health`, `dashboard`, `operations`, `observability`, `settings`, `secrets`, `config`. Details: `docs/claude-reference.md`.

**Architektur-Konvention:** Neuer API-Endpoint → parallel Methode in `NodePilotApiClient.cs` + Command anlegen. DTOs in `Cli/Api/Dtos/` duplizieren (kein ProjectReference).
