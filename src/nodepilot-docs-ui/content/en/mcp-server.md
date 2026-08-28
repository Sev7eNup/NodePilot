# MCP server (`nodepilot-mcp`)

A [Model Context Protocol](https://modelcontextprotocol.io) server that lets an AI agent
(Claude Desktop/Code or any MCP client) **control and edit NodePilot workflows** and **read data**.
Like the `np` CLI it is a pure HTTP client against the existing REST API — **no new backend path**;
every tool calls an existing endpoint or computes in-process against `NodePilot.Core`. 101 tools
across 10 groups, plus 3 MCP resources.

## Installation

The server is **not** a .NET global tool: `PackAsTool` cannot cope with the inherited
`net10.0-windows` TFM (NETSDK1146), so `dotnet pack` fails. It is delivered through
`dotnet publish`; the MCP client points at the resulting `.exe`.

```powershell
# Both installers already ship the server: <install>\tools\mcp\nodepilot-mcp.exe.
# Build it yourself only from a source checkout:
dotnet publish src/NodePilot.Mcp -c Release -o C:\Tools\NodePilot-Mcp

np auth login          # the MCP server reuses this CLI session
```

```jsonc
// .mcp.json — point the client at the server
{
  "mcpServers": {
    "nodepilot": {
      "command": "C:\\Tools\\NodePilot-Mcp\\nodepilot-mcp.exe",
      "env": { "NODEPILOT_MCP_SERVER": "https://nodepilot.example.com", "NODEPILOT_MCP_PROFILE": "default" }
    }
  }
}
```

## Configuration & authentication

Headless (started by the MCP client) → environment first, falling back to the CLI configuration/session:

| What | Order (first wins) |
|---|---|
| Server URL | `NODEPILOT_MCP_SERVER` › `NODEPILOT_SERVER` › the CLI `config.json` profile |
| Profile | `NODEPILOT_MCP_PROFILE` › `NODEPILOT_PROFILE` › the CLI default › `default` |
| Token | `NODEPILOT_MCP_TOKEN` (a raw bearer, the CI escape) › the DPAPI session from `np auth login` (auto-refresh) |

The transport is **stdio** (streamable HTTP is planned as a later option). Windows only
(`net10.0-windows`, DPAPI).

## Security

- **Destructive gate:** `delete_*`, `force_unlock_workflow`, `cancel_all_executions` and `test_step` are
  only **registered** with `NODEPILOT_MCP_ALLOW_DESTRUCTIVE=true` — otherwise they do not even appear in
  `tools/list`. `get_safety_status` shows the mode.
- **Secrets:** workflow definitions are redacted before tool output (`secret`/`apiKey`/`password`/
  `authToken`/`bearer`/`connectionString` → `***`). On `publish`/`update`/`apply_workflow_patch`, real
  secrets are restored by node ID from the stored version — the agent's `***` never overwrites a real
  value. Credentials/globals never emit secrets.
- **Annotations:** read tools are `readOnly`, gated tools `destructive`, and execute/enable/disable/lock
  are `idempotent`.

## Tool groups

- **Discovery:** `whoami`, `get_safety_status`, `list_activity_types`, `get_activity_config_reference`, `validate_cron`
- **Reading workflows:** `list_workflows`, `get_workflow`, `get_workflow_definition` (redacted), `get_workflow_contract`, versions, `export_workflow`
- **Editing workflows:** lock/unlock/`publish_workflow`/`update_workflow_definition`, `validate_workflow_definition`, `preview/apply_workflow_patch` (merge by ID, secret protection, validate before save), create/duplicate/enable/disable/rollback/import (JSON via `import_workflow`, SCOrch `.ois_export` via `import_scorch_workflow`), step-test context
- **Gated destructive:** `test_step` (runs a real activity; a configuration override additionally requires edit permission and your own lock), delete/force-unlock/cancel-all
- **Executions:** list/get/steps/paused-steps, `execute_workflow`, cancel/retry/resume, `trigger_external_workflow`
- **Telemetry:** dashboard, coverage/step-health/step-stats, `query_audit_log` (admin), `get_support_diagnostics` (admin)
- **Database / text2sql (admin, read-only):** `list_db_tables` (the schema catalog; secret columns hidden, `GlobalVariable.Value` masked), `get_db_info` (provider + row/timeout limits), `run_readonly_sql` (one read-only statement, with the server enforcing a keyword allow-list + rollback; there is no write tool). Secret columns are unreachable through raw SQL too — three layers: a direct reference returns `protected_column`; a `SELECT *` returns the values as `***`; and serializing a **whole row** of a table with a secret column (`to_json`/`row_to_json`/`::text`/`FOR JSON`) returns `protected_row_projection` — that route is exactly what carried the values past the first two, purely name-based layers. Naming columns explicitly always works. Translating natural language into SQL is the agent's job.
- **Supporting:** machines, credentials, globals (secrets never emitted)
- **Alerting:** `get_alerting_catalog` (the rule vocabulary) + `list/get/create/update/test_fire_alerting_rule` + `list_alerting_deliveries` (the ledger) (+ gated `delete_alerting_rule`; route secrets are never emitted)
- **System alerts (ADR 0008):** `get_system_alert_catalog` + `list/get/create/update/enable/disable/test_fire_system_alert_policy` (+ gated `delete_system_alert_policy`)
- **Canvas assistant** (for the designer chat, largely in-process): `analyze_workflow`, `get_available_variables`, `get_failure_context`, `find_unresolved_references`, `validate_edge_condition`, `validate_activity_config`, `preview_template_resolution`, `suggest_layout`, `diff_workflow_definition`, `get_workflow_node`, `check_styleguide`

## Resources

- `nodepilot://activity-catalog` — all activity/trigger types (category, isTrigger/isRemote, output parameters)
- `nodepilot://activity-config-reference` — curated per-activity **configuration key** schemas
- `nodepilot://styleguide` — the workflow layout style guide

The complete reference is in the repository: `docs/mcp-server.md`.
