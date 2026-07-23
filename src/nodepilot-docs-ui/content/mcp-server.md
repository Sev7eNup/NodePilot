# MCP-Server (`nodepilot-mcp`)

Ein [Model-Context-Protocol](https://modelcontextprotocol.io)-Server, mit dem ein KI-Agent
(Claude Desktop/Code oder ein beliebiger MCP-Client) **NodePilot-Workflows steuern und bearbeiten**
sowie **Daten auslesen** kann. Wie die `np`-CLI ist er ein reiner HTTP-Client gegen die bestehende
REST-API — **kein neuer Backend-Pfad**; jedes Tool ruft einen vorhandenen Endpoint auf oder rechnet
in-proc gegen `NodePilot.Core`. 99 Tools über 10 Gruppen, plus 3 MCP-Resources.

## Installation

```powershell
dotnet pack src/NodePilot.Mcp -c Release -o ./out/mcp
dotnet tool install --global --add-source ./out/mcp NodePilot.Mcp

np auth login          # der MCP-Server nutzt diese CLI-Session weiter
```

```jsonc
// .mcp.json — Client auf den Server zeigen
{
  "mcpServers": {
    "nodepilot": {
      "command": "nodepilot-mcp",
      "env": { "NODEPILOT_MCP_SERVER": "https://nodepilot.example.com", "NODEPILOT_MCP_PROFILE": "default" }
    }
  }
}
```

## Konfiguration & Auth

Headless (vom MCP-Client gestartet) → env-first, mit Fallback auf die CLI-Config/-Session:

| Was | Reihenfolge (erstes gewinnt) |
|---|---|
| Server-URL | `NODEPILOT_MCP_SERVER` › `NODEPILOT_SERVER` › CLI-`config.json`-Profil |
| Profil | `NODEPILOT_MCP_PROFILE` › `NODEPILOT_PROFILE` › CLI-Default › `default` |
| Token | `NODEPILOT_MCP_TOKEN` (Raw-Bearer, CI-Escape) › DPAPI-Session aus `np auth login` (Auto-Refresh) |

Transport ist **stdio** (Streamable HTTP ist als spätere Option vorgesehen). Windows-only
(`net10.0-windows`, DPAPI).

## Sicherheit

- **Destructive-Gate:** `delete_*`, `force_unlock_workflow`, `cancel_all_executions`, `test_step` werden nur bei
  `NODEPILOT_MCP_ALLOW_DESTRUCTIVE=true` **registriert** — sonst tauchen sie nicht einmal in
  `tools/list` auf. `get_safety_status` zeigt den Modus.
- **Secrets:** Workflow-Definitionen werden vor Tool-Output redigiert (`secret`/`apiKey`/`password`/
  `authToken`/`bearer`/`connectionString` → `***`). Bei `publish`/`update`/`apply_workflow_patch`
  werden echte Secrets per Node-ID aus der gespeicherten Version wiederhergestellt — das `***` des
  Agenten überschreibt nie einen echten Wert. Credentials/Globals geben Secrets nie aus.
- **Annotations:** Read-Tools `readOnly`, gated Tools `destructive`, execute/enable/disable/lock
  `idempotent`.

## Tool-Gruppen

- **Discovery:** `whoami`, `get_safety_status`, `list_activity_types`, `get_activity_config_reference`, `validate_cron`
- **Workflow lesen:** `list_workflows`, `get_workflow`, `get_workflow_definition` (redigiert), `get_workflow_contract`, Versionen, `export_workflow`
- **Workflow editieren:** lock/unlock/`publish_workflow`/`update_workflow_definition`, `validate_workflow_definition`, `preview/apply_workflow_patch` (Merge-by-ID, Secret-Schutz, Validate-before-Save), create/duplicate/enable/disable/rollback/import, step-test context
- **Gated destructive:** `test_step` (führt eine echte Activity aus; Config-Override zusätzlich nur mit Edit + eigenem Lock), delete/force-unlock/cancel-all
- **Executions:** list/get/steps/paused-steps, `execute_workflow`, cancel/retry/resume, `trigger_external_workflow`
- **Telemetrie:** dashboard, coverage/step-health/step-stats, `query_audit_log` (Admin), `get_support_diagnostics` (Admin)
- **DB / text2sql (Admin, nur lesend):** `list_db_tables` (Schema-Katalog; Secret-Spalten hidden, `GlobalVariable.Value` maskiert), `get_db_info` (Provider + Row-/Timeout-Limits), `run_readonly_sql` (ein Read-Only-Statement, Server erzwingt Keyword-Whitelist + Rollback; kein Write-Tool). NL→SQL macht der Agent.
- **Supporting:** Machines, Credentials, Globals (Secrets nie ausgegeben)
- **Alerting:** `list/get/create/update/test_fire_alerting_rule` + `list_alerting_deliveries` (Ledger) (+ gated `delete_alerting_rule`; Route-Secrets nie ausgegeben)
- **System-Alerts (ADR 0008):** `get_system_alert_catalog` + `list/get/create/update/enable/disable/test_fire_system_alert_policy` (+ gated `delete_system_alert_policy`)
- **Canvas-Assistent** (für den Designer-Chat, überwiegend in-proc): `analyze_workflow`, `get_available_variables`, `get_failure_context`, `find_unresolved_references`, `validate_edge_condition`, `validate_activity_config`, `preview_template_resolution`, `suggest_layout`, `diff_workflow_definition`, `get_workflow_node`, `check_styleguide`

## Resources

- `nodepilot://activity-catalog` — alle Activity-/Trigger-Typen (Kategorie, isTrigger/isRemote, Output-Params)
- `nodepilot://activity-config-reference` — kuratierte per-Activity **Config-Key**-Schemata
- `nodepilot://styleguide` — der Workflow-Layout-Styleguide

Vollständige Referenz im Repo: `docs/mcp-server.md`.
