# NodePilot.Mcp (`nodepilot-mcp`) — Konventionen

Gilt für `src/NodePilot.Mcp/`. Projektweite Regeln stehen in der Root-`CLAUDE.md`.

Reiner HTTP-Client gegen die REST-API (wie die CLI) + In-Proc-Analyse gegen `NodePilot.Core` — **kein** neuer Backend-Pfad (99 Tools, 3 Resources), Transport stdio; reused die DPAPI-Session der CLI (`np auth login`). Destruktive Tools (`delete_*`, `force_unlock_workflow`, `cancel_all_executions`, `test_step`) werden nur bei `NODEPILOT_MCP_ALLOW_DESTRUCTIVE=true` registriert; Workflow-Definitionen werden vor Tool-Output secret-redigiert, bei publish/patch werden echte Secrets aus der gespeicherten Version wiederhergestellt. Volle Doku: `docs/mcp-server.md`.

**Architektur-Konvention:** Neuer API-Endpoint → Methode in `Api/NodePilotApiClient.cs` (DTOs in `Api/Dtos/` dupliziert) + `[McpServerTool]`-Methode in der passenden `Tools/*Tools.cs` (destruktiv → `DestructiveTools` + `get_safety_status`-Liste pflegen), ggf. Klasse in `Program.cs` via `WithTools<T>()` registrieren (**nie** `WithToolsFromAssembly`), WireMock-Test ergänzen. Frontend-Databus-/Lint-Logik wird in `Analysis/` gespiegelt (`upstreamVariables.ts`, `activityConfigFacts.ts`, `workflowLint.ts`).
