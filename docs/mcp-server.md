# NodePilot MCP Server (`nodepilot-mcp`)

A [Model Context Protocol](https://modelcontextprotocol.io) server that lets an AI agent (Claude
Desktop/Code, or any MCP client) **drive and edit NodePilot workflows and read data** through a
curated set of tools. It is a thin HTTP client against the existing NodePilot REST API — exactly
like the `np` CLI — so it adds no new backend surface; every tool calls an existing endpoint (or
computes in-process against `NodePilot.Core`).

- **Project:** `src/NodePilot.Mcp/` (`net10.0-windows`, packaged as the dotnet tool `nodepilot-mcp`).
- **Transport:** stdio (default). Streamable HTTP is a documented future option (see [Transport](#transport)).
- **Tests:** `tests/NodePilot.Mcp.Tests/` (xUnit + WireMock.Net + a stdio-process MCP smoke test).

## Install & run

```powershell
# From the repo. Not a dotnet global tool: PackAsTool rejects the net10.0-windows TFM this
# project inherits (NETSDK1146), so `dotnet pack` fails. Publish and reference the .exe.
dotnet publish src/NodePilot.Mcp -c Release -o C:\Tools\NodePilot-Mcp
```

Authenticate once with the CLI (the MCP server reuses that session):

```powershell
np auth login            # stores a DPAPI-encrypted session under %APPDATA%\NodePilot
```

Then point an MCP client at the `nodepilot-mcp` command.

### `.mcp.json` example

```json
{
  "mcpServers": {
    "nodepilot": {
      "command": "C:\\Tools\\NodePilot-Mcp\\nodepilot-mcp.exe",
      "env": {
        "NODEPILOT_MCP_SERVER": "https://nodepilot.example.com",
        "NODEPILOT_MCP_PROFILE": "default"
      }
    }
  }
}
```

## Configuration & auth

The server is headless (launched by an MCP client, cannot prompt). It resolves everything from
environment variables, falling back to the CLI's on-disk config/session:

| What | Precedence (first wins) |
|---|---|
| Server URL | `NODEPILOT_MCP_SERVER` › `NODEPILOT_SERVER` › CLI `config.json` profile |
| Profile | `NODEPILOT_MCP_PROFILE` › `NODEPILOT_PROFILE` › CLI default profile › `default` |
| Token | `NODEPILOT_MCP_TOKEN` (raw bearer, CI/headless escape — no refresh) › DPAPI `np auth login` session (cross-process single-flight rotation shared with CLI before its server-issued absolute expiry; transient proactive failures use a 15-second token-bound cooldown; re-login after expiry) |

The server starts even when unconfigured/unauthenticated; tools then return an actionable error
(`run np auth login`, or set `NODEPILOT_MCP_SERVER`).

> **Windows-only.** Like the rest of NodePilot, the MCP server is `net10.0-windows` and uses DPAPI
> for the session store. `NODEPILOT_MCP_TOKEN` exists for Windows CI/headless runs without a DPAPI
> session — it is not a cross-platform path.

## Safety: the destructive-tool gate

Destructive/admin tools are **not registered at all** unless `NODEPILOT_MCP_ALLOW_DESTRUCTIVE=true`
— so by default the agent never even sees them in `tools/list` and cannot "try" them. Call
`get_safety_status` to see the active mode and which tools are gated. Gated tools:

`delete_workflow`, `force_unlock_workflow`, `cancel_all_executions`, `test_step`, `delete_machine`,
`delete_credential`, `delete_global_variable`, `delete_global_variable_folder`,
`delete_alerting_rule`, `delete_system_alert_policy`.

Every tool also carries MCP annotations: read tools are `readOnly`, the gated ones are
`destructive`, and `execute`/`enable`/`disable`/`lock`/`unlock`/`cancel` are `idempotent`.

## Secret handling

- Workflow definitions are **redacted** before they reach the agent. Named secret keys plus opaque
  executable/runtime payloads (scripts, bodies, headers, arguments, queries, prompts, condition
  literals and Custom-Activity string inputs) are masked to `***`. The API only redacts by role;
  MCP re-applies the shared `WorkflowSecretRedactor` for everyone.
- On `publish_workflow`/`update_workflow_definition`/`apply_workflow_patch`, real secrets are
  **restored from the stored version** by node id (including masks nested in condition/case arrays)
  — the agent's `***` never overwrites them, and a
  secret the agent invents on a new node is rejected (masked + noted).
- Credentials carry no password field; secret global values arrive masked. Create/update accept
  secrets write-only.

## Token budget

Large free-text fields (stdout/stderr, return data, audit details, diagnostics) are truncated to
~4 KB with an explicit marker. List tools return slim summaries (no `DefinitionJson`) and respect a
`limit`; the audit log is cursor-paginated; `get_workflow_definition` supports `nodeIdsOnly`.

## Tool catalog

89 default tools across 10 groups, plus 10 gated destructive tools (99 total). (Roles refer to the
authenticated user.)

### Discovery
`whoami` · `get_safety_status` · `list_activity_types` · `get_activity_config_reference` · `validate_cron`

### Workflow read (Viewer+)
`list_workflows` · `get_workflow` · `get_workflow_definition` (redacted) · `get_workflow_contract` ·
`list_workflow_versions` · `get_workflow_version` · `export_workflow`

### Workflow editing (Operator+, lock→edit→publish)
`validate_workflow_definition` (in-proc) · `lock_workflow` · `unlock_workflow` · `publish_workflow` ·
`update_workflow_definition` · `preview_workflow_patch` · `apply_workflow_patch` (merge-by-id,
secrets protected, validate-before-save) · `create_workflow` · `duplicate_workflow` ·
`enable_workflow` · `disable_workflow` · `rollback_workflow` · `import_workflow` ·
`import_scorch_workflow` · `list_step_test_runs` · `get_step_test_context`

### Executions (Operator+ for control)
`list_executions` · `get_execution` · `get_execution_steps` · `list_paused_steps` ·
`execute_workflow` · `cancel_execution` · `retry_execution` · `resume_execution` ·
`trigger_external_workflow` (X-Api-Key)

### Telemetry / data
`get_dashboard_stats` · `get_operations_graph` · `get_workflow_coverage` · `get_workflow_step_health` ·
`get_workflow_step_stats` · `query_audit_log` (Admin) · `get_support_diagnostics` (Admin)

`get_operations_graph` deckelt `recent` zusätzlich zum Server-Cap auf die **neuesten 200** und weist die
Kürzung in `meta.recentToolCap` / `meta.recentWithheldByTool` aus. Der Server-Cap (4000) ist ein
Render-Budget für eine Timeline, die jeden Balken zeichnet — für ein Agenten-Kontextfenster wären das
~900 KB GUIDs und ISO-Zeitstempel. `running` wird **nie** gekürzt: das ist die Antwort auf „was läuft
gerade“. Gefensterte Gesamtzahlen kommen ohnehin aus `density[]`, nicht aus der Balkenliste.

### DB / text2sql (Admin; read-only)
`list_db_tables` · `get_db_info` · `run_readonly_sql`. Schema discovery + single read-only SQL
statement against the NodePilot App-DB (the agent does the NL→SQL translation). Read keyword
whitelist + rollback enforced server-side; no write tool. `list_db_tables` hides secret columns
(`PasswordHash`/`EncryptedPassword`) und maskiert `GlobalVariable.Value`. PostgreSQL-`U&"…"`-Identifier
und dynamische XML-Exporter (`query_to_xml` & Co.) sind im serverseitigen Read-Guard generell gesperrt.

`Workflows`, `WorkflowVersions` und die Custom-Activity-Tabellen sind **bewusst nicht** gesperrt: die
Tools sind Admin-only, und ein Admin liest dieselben Zeilen ohnehin über DbAdmin. Eine Sperre hätte nur
Bestandsfragen („welche Workflows gibt es") unbeantwortbar gemacht. Für Definitionen inklusive
Secret-Redaction bleibt `get_workflow_definition` der bequemere Weg.

Für die übrigen Secret-Spalten gelten drei serverseitige Schichten im `DbAdminSecretColumns`-Contract:

1. Nennt das Statement eine geschützte Spalte → Ablehnung (`protected_column`).
2. Wildcard-Select → die geschützten Ergebnis-Spalten kommen als `***` zurück.
3. Serialisiert das Statement eine **ganze Zeile** einer Tabelle mit Secret-Spalte
   (`to_json`/`row_to_json`/`to_jsonb`/`json_agg`/`::text`/`FOR JSON`/`FOR XML`) → Ablehnung
   (`protected_row_projection`). Schicht 1 und 2 arbeiten beide über **Namen**; eine Row-
   Serialisierung nennt die Spalte nie und liefert sie unter einem harmlosen Ergebnis-Spaltennamen
   aus — sie umging damit beide auf einen Schlag (Security-Audit 2026-07-26).

So landet kein Secret im Kontext des Agents. Schicht 3 ist bewusst grob und greift auch bei
harmlosen Casts auf diesen Secret-Tabellen; explizit benannte, nicht geschützte Spalten funktionieren.

### Supporting resources (secrets never surfaced)
`list_machines` · `get_machine` · `create_machine` · `update_machine` · `test_machine` ·
`list_credentials` · `get_credential` · `create_credential` · `update_credential` ·
`list_global_variables` · `create_global_variable` · `update_global_variable` ·
`list_global_variable_folders` · `create_global_variable_folder` · `rename_global_variable_folder` ·
`move_global_variable_folder` · `move_global_variable_to_folder`

### Alerting (notification rules; route secrets never surfaced)
`list_alerting_rules` · `get_alerting_rule` · `create_alerting_rule` · `update_alerting_rule` ·
`test_fire_alerting_rule` · `list_alerting_deliveries`

### System alerts (ADR 0008; catalog-driven policies)
`get_system_alert_catalog` · `list_system_alert_policies` · `get_system_alert_policy` ·
`create_system_alert_policy` · `update_system_alert_policy` · `enable_system_alert_policy` ·
`disable_system_alert_policy` · `test_fire_system_alert_policy`

### Canvas assistant (for the in-designer chat — mostly in-process, work on the unsaved definition)
`analyze_workflow` (orphans / no-trigger / cycles / remote-without-machine) ·
`find_unresolved_references` · `get_available_variables` · `check_styleguide` ·
`validate_edge_condition` · `validate_activity_config` · `preview_template_resolution` ·
`suggest_layout` · `diff_workflow_definition` · `get_workflow_node` · `get_failure_context`

### Gated destructive (only when `NODEPILOT_MCP_ALLOW_DESTRUCTIVE=true`)
`test_step` (Operator+, executes a real activity; config override also requires Edit + own lock) ·
`delete_workflow` (Admin) · `force_unlock_workflow` (Admin) · `cancel_all_executions` ·
`delete_machine` (Admin) · `delete_credential` (Admin) · `delete_global_variable` (Admin) ·
`delete_global_variable_folder` (Admin) · `delete_alerting_rule` (Admin) · `delete_system_alert_policy` (Admin)

## Resources

| URI | Content |
|---|---|
| `nodepilot://activity-catalog` | All activity/trigger types with categories, isTrigger/isRemote flags and output params (in-process). |
| `nodepilot://activity-config-reference` | Curated per-activity purpose + **config-key** schema (key/type/required/description, plus `promptNotes`) — the schema the backend catalog does not carry. Served from `NodePilot.Core` (`Activities/Embedded/activity-config-reference.json`), which is also what the AI prompts render their activity catalog from. |
| `nodepilot://styleguide` | The workflow layout/naming styleguide (`docs/workflow-styleguide.md`), embedded. |

The config-reference is also exposed as the `get_activity_config_reference` tool (client resource
support varies). It is hand-maintained from `src/nodepilot-ui/src/lib/activityConfigFacts.ts` and
the activity config components — keep it in sync when activity configs change.

## Transport

Default is **stdio**: stdout is the JSON-RPC channel, so all logging goes to **stderr** (a stray
`Console.WriteLine` would corrupt the protocol). Tools are registered **explicitly**
(`WithTools<T>()`, never `WithToolsFromAssembly()`) so the gated destructive tools can never be
pulled in by an assembly scan.

**Streamable HTTP (future option):** add the `ModelContextProtocol.AspNetCore` package, build a
`WebApplication`, call `.WithHttpTransport()` and `app.MapMcp()`, and gate it behind a
`NODEPILOT_MCP_TRANSPORT=http` switch in `Program.cs`. Bind to loopback by default. Not built today
(NodePilot's MCP use is local/stdio); the registration code is structured so this drops in without
touching the tools.

## Development

```powershell
dotnet build src/NodePilot.Mcp
dotnet test  tests/NodePilot.Mcp.Tests          # xUnit + WireMock + stdio-process smoke test
```

Architecture mirrors the CLI: `Api/NodePilotApiClient.cs` (one method per endpoint, every non-2xx →
`ApiException`), `Auth/TokenStore.cs` (DPAPI, shared with the CLI), `Config/` (env-first
resolution). Tools live in `Tools/`, in-process graph/databus analysis in `Analysis/`, mapping
helpers (error mapping, redaction, payload shaping, patch engine) in `Mapping/`.

**New endpoint → new tool:** add the method to `NodePilotApiClient`, add a `[McpServerTool]` method
to the right `Tools/*Tools.cs` class (or `DestructiveTools` if destructive), register the tool type
in `Program.cs` if it is a new class, and add a WireMock-backed test.
