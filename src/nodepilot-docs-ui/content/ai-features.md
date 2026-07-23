# AI-Features

Opt-in (`Llm:Enabled=false` default). OpenAI-kompatibel. Rate-Limit 20/Min/IP. Generierung `[Authorize(Roles = "Admin,Operator")]`, der Chat-Assistent `[Authorize]` (alle Rollen, Änderungen nur Admin/Operator).

## Drei Helper

1. **AI Script Generation** — Sparkles-Button im Fullscreen-Editor der `runScript`-Activity. Prompt → der Prompt-Dialog schließt sofort, das generierte PowerShell **tippt sich live** in den Editor (am Cursor oder „komplett ersetzen"). Das **aktuelle Skript** wird als Refactor-Basis mitgeschickt, damit „refactor/fix das Skript" auf dem Bestehenden aufsetzt statt etwas Neues zu erfinden.
2. **AI Workflow Generation** — "KI generieren"-Button auf der Workflow-Übersicht. Prompt → JSON-Preview mit Stats → User bestätigt → neuer Workflow + Editor öffnet sich.
3. **AI Workflow Assistant** — lila Button neben dem Standard/Experte-Toggle im Designer öffnet ein angedocktes Chat-Panel. Multi-Turn: erklärt den **aktuellen** Workflow (Markdown) und schlägt auf Wunsch komplette Umbauten vor. Alle Rollen dürfen fragen; nur Admin/Operator können einen Vorschlag übernehmen. Secrets werden vor dem LLM-Call redigiert; Vorschläge werden per Node-ID aufs Original zurückgemergt (Layout/Secrets/Felder bleiben erhalten), als Proposal-Karte gezeigt und auf den Canvas übernommen (gespeichert über den normalen Edit-Lock/Publish-Flow). Stale-Schutz blockt das Übernehmen, wenn sich der Canvas seit der Frage geändert hat. **Auf leerem Canvas** (keine Activities, nur Trigger) schaltet der Assistent in einen From-Scratch-Design-Mode und bekommt dasselbe verzweigte Referenzbeispiel wie die Workflow-Generation — so kommt auch beim ersten Erstellen ein **verzweigter** Workflow statt einer linearen Kette. Chat-Komfort: **Kopieren** (Antwort + Code-Blöcke), **Regenerieren/Retry**, **@Node-Mentions**, **Starter-Vorschläge** im leeren Zustand, ein **Usage-Footer** (Model · Dauer · Tokens · tok/s), **benannte Threads** je Workflow (wechseln / umbenennen / löschen), **reload-persistenter Verlauf** (localStorage, Snapshots/Proposal-JSON gestrippt, nie bei ungespeicherten Workflows, Logout leert), **Markdown-Export** des Threads und eine workflow-scoped **AI-Aktivitäts-Ansicht** (`GET /api/ai/chat/activity/{workflowId}`, Admin/Op, Folder-RBAC). Vorschlags-Komfort: ein **strukturiertes Changelog** (hinzugefügt/entfernt/geändert, reine Layout-Verschiebungen separat markiert), **selektives Übernehmen** (Checkbox je Änderung; Kanten ohne Endpunkt werden übersprungen), **Verfeinern** (den Vorschlag als Basis weiter anpassen), **Rückgängig** + **Layout aufräumen** nach dem Übernehmen sowie **Auswahl-Scoping** (Frage auf markierte Canvas-Nodes beziehen).

## LLM-Konfiguration

```json
{
  "Llm": {
    "Enabled": false,
    "BaseUrl": "https://api.openai.com/v1",
    "ApiKey": null,
    "Model": "gpt-4o-mini",
    "MaxTokens": 4096,
    "TimeoutSeconds": 90,
    "EnableToolCalling": false,
    "ToolCallMaxDepth": 6
  }
}
```

| Key | Default | Erklärung |
|---|---|---|
| `Enabled` | `false` | Master-Switch. Off → alle AI-Endpoints antworten `503 LLM_DISABLED`. |
| `BaseUrl` | OpenAI Cloud | OpenAI-kompatibler Chat-Completions-Root. |
| `ApiKey` | `null` | Cloud braucht Key; lokale Endpoints meist nicht. Empfohlen: env var `Llm__ApiKey` — Plaintext in Settings triggert Startup-Hardening-Warning. |
| `Model` | `gpt-4o-mini` | Für beide Generierungen. |
| `MaxTokens` | `4096` | Cap der LLM-Response. |
| `TimeoutSeconds` | `90` | HTTP-Timeout. |
| `EnableToolCalling` | `false` | Opt-in. Lässt den Chat (`POST /api/ai/chat`) read-only Analyse-Tools per OpenAI-Function-Calling callen (`tool_choice: auto`). Braucht ein Modell, das Function-Calling zuverlässig kann — viele kleine lokale Modelle nicht. Aus → keine `tools` gesendet. |
| `ToolCallMaxDepth` | `6` | Max LLM-Runden mit Tool-Calls pro Chat-Turn (Loop-Guard, gültig 1–10). Letzte Runde droppt `tools` → erzwingt Text-Antwort. |

**Sofort wirksam (Hot-Reload):** `Llm` ist eine der hot-reloadablen Settings-Sektionen — `ILlmClientFactory` und die Controller-Gates lesen `IOptionsMonitor<LlmOptions>.CurrentValue` pro Verwendung. Ein Save (inkl. des `Llm:Enabled`-Kill-Switches) wirkt ohne Dienst-Neustart.

## Endpoints

| Endpoint | Zweck | Transport |
|---|---|---|
| `POST /api/ai/generate-script` | PowerShell aus Prompt (tippt live in Monaco) | SSE (`text/event-stream`) |
| `POST /api/ai/chat` | Workflow-Assistent (erklären + Änderungen vorschlagen) | SSE (`text/event-stream`) |
| `POST /api/ai/generate-workflow` | Workflow-JSON aus Prompt | JSON |
| `POST /api/ai/chat/applied` | Audit `AI_PROPOSAL_APPLIED` beim Übernehmen eines Vorschlags (Admin/Op, Folder-RBAC Edit) | JSON |
| `GET /api/ai/chat/activity/{workflowId}` | KI-Audit-Einträge eines Workflows, neueste zuerst (Admin/Op, Folder-RBAC Read) | JSON |

## llmQuery-Activity

Neben den drei UI-Helpern gibt es eine **LLM-Activity für Workflows**: [`llmQuery`](../activities-reference) ist eine engine-lokale Activity, die einen OpenAI-kompatiblen Prompt→Text-Call aus einem Step heraus ausführt.

- Nutzt per Default die globale `Llm:*`-Config; pro Node überschreibbar: `baseUrl`, `model`, `apiKey` (Secret, auto-redigiert) + `maxTokens`, `temperature` (nur pro Node — kein globaler Knopf), `timeoutSeconds`, `jsonMode`, `systemPrompt`.
- **Gated durch `Llm:Enabled`** — der zentrale Kill-Switch greift auch bei einem Node-eigenen Endpunkt.
- Teilt Transport + SSRF-Guard mit dem Assistenten über denselben `ILlmClientFactory` (einziger Per-Node-Override-Einstieg).
- Outputs: `{{step.output}}` = Antworttext; `param.model`, `param.promptTokens`, `param.completionTokens`, `param.totalTokens`, `param.finishReason` (Token-Keys immer gesetzt, `""` wenn der Server keine `usage` liefert).
- Prompt-excluded — `llmQuery` taucht nicht in der Workflow-Auto-Generierung auf.

## Streaming

`chat` und `generate-script` streamen als **Server-Sent-Events** — die Ausgabe ist ab dem ersten Token sichtbar. Events: `delta` (Text-Token), `building` (chat: Start der Definitions-Bauphase — UI zeigt „Generiere Workflow-Änderung…"), `proposal` (chat: fertiger Vorschlag, am Ende), `done` (Model + Dauer), `error`. Der Stop-Button bzw. das Schließen des Dialogs bricht den Stream sauber ab (kein Fehler, partielle Ausgabe bleibt; Audit `cancelled=true`). Voraussetzung: der LLM-Endpoint unterstützt `stream:true` (alle OpenAI-kompatiblen Server; `stream_options` hat einen Fallback, und der `max_tokens`→`max_completion_tokens`-Quirk neuerer OpenAI-Modelle wird automatisch per Retry abgefangen). `generate-workflow` bleibt non-streaming (JSON-Envelope + Stats-Preview).

Ist `Llm:EnableToolCalling=true`, kann der Chat zusätzlich eine **opt-in read-only Tool-Calling-Schleife** fahren (`tool_choice: auto`): das Modell ruft Analyse-Tools (`analyze_workflow`, `list_activity_types`) auf der secret-redigierten Definition auf sowie **Execution-Log-Tools** (`list_recent_executions`, `get_execution_steps`, `get_failure_context`), mit denen der Assistent vergangene Läufe und Fehlschläge des geöffneten Workflows analysiert — letztere nur bei gespeichertem Workflow und Folder-Read-Recht des Callers; die Outputs sind secret-redigiert und gekürzt. Der Stream erhält dann `tool_call`- und `tool_result`-Events. Begrenzt durch `Llm:ToolCallMaxDepth` (Default `6`); die letzte Runde droppt `tools` und erzwingt eine Text-Antwort.

## Globaler Wissens-Chat (`/ai-chat`)

Vom workflow-spezifischen Chat getrennt: ein seitenweiter, **canvas-freier** read-only Q&A-Assistent
(`POST /api/ai/knowledge/ask`, SSE; Capabilities `GET /api/ai/knowledge/capabilities`). Opt-in via
`AiKnowledge:Enabled` (zusätzlich zu `Llm:Enabled`), hot-reloadbar. **Vier admin-toggelbare
Wissensquellen** (Sektion `AiKnowledge`): Docs (`DocsEnabled`), Workflows & Betrieb
(`OperationalEnabled`, RBAC-folder-scoped — liefert nur die Workflow-Definition, die statische
Analyse und die Cron-Voraussage; reine Listen wie Workflows/Läufe/Maschinen werden über die
DB-Quelle per text2sql beantwortet), Quellcode (`SourceCodeEnabled`, Admin/Op) und
**DB / text2sql** (`DbEnabled`, Admin/Op) — letzteres default aus. Zudem `read_settings` (Admin/Op).

**text2sql**: das LLM übersetzt die Frage in provider-spezifisches SQL. `list_db_tables` liefert Dialekt
und Schema, `get_db_table` zusätzlich Foreign Keys. `execute_readonly_sql` akzeptiert genau ein Statement
bis 64 KiB; ein zentraler Executor-Guard erzwingt die Read-only-Whitelist und blockiert mutierende Keywords,
gefährliche Routinen und `EXPLAIN ANALYZE`. Geschützte Spalten werden im Schema verborgen und bereits bei
jeder SQL-Referenz abgelehnt (auch Alias-/Ausdrucksvarianten); Result-Masking und der Redactor bleiben als
zweite Schicht. Row-Cap 200; große Resultate bleiben valides JSON mit Truncation-Hinweis. DB-Tools verwenden
Strict Function Schemas mit automatischem Best-Effort-Fallback für inkompatible lokale Endpoints. Die
Capability erscheint nur bei aktivem `Llm:EnableToolCalling`.

## Hardening

- SSRF-Block, `UseProxy=false`, Klartext-ApiKey-Warning.
- **Prompt-Injection-Mitigation:** Schema-only, User-reviewed Insert; der Chat behandelt Workflow-JSON/Configs/Scripts als untrusted Daten (eigener Context-Block in der User-Message) und **redigiert Secrets** vor jedem LLM-Call.
- **Kein DB-Write durch die KI:** Chat-Vorschläge werden serverseitig per Node-ID aufs Original gemergt und nur auf den Canvas übernommen — Persistenz läuft über den lock-gegateten `PUT`.
- **Drift-Schutz:** `PromptCatalogDriftTest.cs`.

## Audit

`AI_SCRIPT_GENERATED`, `AI_WORKFLOW_GENERATED`, `AI_WORKFLOW_EXPLAINED`, `AI_PROPOSAL_APPLIED` (mit Node-/Edge-Counts beim Übernehmen eines Vorschlags).
