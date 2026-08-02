# NodePilot KI-Features

NodePilot integriert einen LLM für drei No-Code-Helfer:

1. **AI-Script-Generierung** — im Fullscreen-Editor des `runScript`-Activities ein
   Sparkles-Button. Prompt → generiertes PowerShell-Script tippt sich live in Monaco
   (am Cursor oder optional den ganzen Editor ersetzen). Das aktuelle Skript wird als
   Refactor-Basis mitgeschickt.
2. **AI-Workflow-Generierung** — auf der Workflow-Übersicht ein „KI generieren"-Button.
   Prompt → JSON-Preview mit Stats → User bestätigt → neuer Workflow + Editor öffnet.
3. **KI-Workflow-Assistent** — ein angedocktes Chat-Panel im Designer (lila Button neben
   dem Standard/Experte-Toggle). Erklärt den aktuellen Workflow live als Markdown und
   schlägt auf Wunsch komplette Umbauten vor. Alle Rollen dürfen fragen; nur Admin/Operator
   können einen Vorschlag übernehmen. Stale-Schutz via Canvas-Hash; Secrets werden vor dem
   LLM-Call redigiert; Vorschläge werden per Node-ID aufs Original zurückgemergt
   (Layout/Secrets/Felder bleiben erhalten). Die Proposal-Karte zeigt ein strukturiertes
   Changelog, selektives Übernehmen, Verfeinern und Rückgängig/Tidy. Chat-UX: benannte
   Threads je Workflow, reload-persistenter Verlauf, Markdown-Export und AI-Aktivitäts-
   Ansicht (`GET /api/ai/chat/activity/{workflowId}`, Admin/Op, Folder-RBAC). Panel und
   Node-/Edge-Properties teilen sich den rechten Slot: der Chat überlagert die Properties,
   jede Einzelselektion im Canvas (Klick, Drop, Suche, Tastatur-Navigation) gibt den Slot
   zurück; eine Mehrfachauswahl lässt den Chat offen — sie ist sein „Auswahl (N)"-Kontext.

Die KI-Features sind **opt-in** und Default-aus: `appsettings.json` liefert `Llm:Enabled=false`
aus (ein default-aktiver LLM-Egress wäre ein authentifizierter Ausgangspfad, den ein Operator
bewusst freischalten muss). Operator schaltet sie via `Llm:Enabled=true` in `appsettings.json`
(oder `appsettings.Production.json`) scharf. Im **Dev-Profil** (`appsettings.Development.json`) ist
`Llm:Enabled=true` gesetzt, damit die Assistenten und die `llmQuery`-Activity lokal (LM Studio/
Ollama) sofort funktionieren.

Zusätzlich zum Master-Switch braucht es **mindestens ein LLM-Profil** und eine Auswahl, welches
davon aktiv ist. Ohne auflösbares aktives Profil antworten alle AI-Endpoints mit
`503 LLM_NO_ACTIVE_PROFILE` — der Dienst startet trotzdem (die KI ist ein Opt-in-Feature und darf
den Boot nicht blockieren).

---

## Konfiguration

Verbindungen liegen als **benannte Profile** unter `Llm:Profiles`, gekeyt nach einer
unveränderlichen Profil-Id; `Llm:ActiveProfileId` bestimmt, welches davon alle KI-Features
benutzen. Genau ein Profil ist gleichzeitig aktiv — Umschalten ist ein Settings-Save, kein
Neu-Eintippen.

```json
{
  "Llm": {
    "Enabled": false,
    "ActiveProfileId": "openai",
    "Profiles": {
      "openai": {
        "Name": "OpenAI Cloud",
        "BaseUrl": "https://api.openai.com/v1",
        "ApiKey": null,
        "Model": "gpt-4o-mini",
        "MaxTokens": 4096,
        "TimeoutSeconds": 90,
        "EnableToolCalling": false,
        "ToolCallMaxDepth": 6
      },
      "ollama": {
        "Name": "Local Ollama",
        "BaseUrl": "http://localhost:11434/v1",
        "Model": "qwen3.6:27b",
        "MaxTokens": 16384,
        "TimeoutSeconds": 360,
        "EnableToolCalling": true,
        "ToolCallMaxDepth": 6
      },
      "openai-responses": {
        "Name": "OpenAI Responses",
        "BaseUrl": "https://api.openai.com/v1/responses",
        "Model": "gpt-5.1",
        "MaxTokens": 32768,
        "TimeoutSeconds": 300
      }
    }
  }
}
```

**Section-Root:**

| Key | Default | Erklärung |
|---|---|---|
| `Enabled` | `false` | Master-Switch. Wenn aus, antworten alle AI-Endpoints (`generate-script`/`generate-workflow`/`chat`/`knowledge/ask`) mit `503 LLM_DISABLED`. |
| `ActiveProfileId` | `""` | Id des Profils, das alle KI-Features nutzen. Muss einen Eintrag in `Profiles` benennen — es gibt bewusst **kein** „nimm halt das erste"-Fallback (still gegen einen anderen Endpunkt zu reden ist schlimmer als ein klares 503). Passt nichts, antworten die Endpoints `503 LLM_NO_ACTIVE_PROFILE`. |
| `Profiles` | `{}` | Die gespeicherten Verbindungen, gekeyt nach Profil-Id. Frische Installationen liefern **keins** aus — siehe „Profile anlegen". |

**Pro Profil (`Llm:Profiles:<id>:*`):**

| Key | Default | Erklärung |
|---|---|---|
| `Name` | `""` | Anzeigename, frei umbenennbar. Die Id bleibt dabei stehen — sie ist der Anker für Secret, Env-Override und `ActiveProfileId`. |
| `BaseUrl` | OpenAI Cloud | Adresse des OpenAI-kompatiblen Endpunkts. Der Pfad bestimmt den Wire-Dialekt — siehe „Wire-Dialekt" unten. Für lokale Modelle siehe Tabelle weiter unten. |
| `ApiKey` | `null` | OpenAI-Cloud verlangt einen Key; lokale Endpoints meist nicht. **Empfohlener Weg: Env-Var `Llm__Profiles__<id>__ApiKey`** — Klartext in der Settings-Datei löst eine Startup-Hardening-Warnung aus. |
| `Model` | `gpt-4o-mini` | Wird für Script-, Workflow-Generierung und beide Chats verwendet. |
| `MaxTokens` | `4096` | Cap der LLM-Antwort. Reicht für ein typisches Script und einen mittelgroßen Workflow. Bei großen Modellen (32k+ Context) gerne erhöhen. |
| `TimeoutSeconds` | `90` | HTTP-Timeout. Großzügig für lokale Modelle, klein genug um nicht ewig zu hängen. |
| `EnableToolCalling` | `false` | Opt-in. Lässt die Chat-Assistenten read-only Analyse-Tools per OpenAI-Function-Calling callen (`tool_choice: auto`). Braucht ein Modell, das Function-Calling zuverlässig kann — viele kleine lokale Modelle nicht. **Pro Profil**, weil das eine Eigenschaft des Modells ist, nicht der Installation: beim Umschalten auf ein kleines lokales Modell wandert die Fähigkeit mit. |
| `ToolCallMaxDepth` | `6` | Max LLM-Runden mit Tool-Calls pro Chat-Turn (Loop-Guard, gültig 1–10). Lässt bei text2sql nach Schema-Discovery noch Raum für SQL-Korrekturen. In der letzten erlaubten Runde sendet der Server **keine** `tools` → erzwingt eine Text-Antwort. |

**Restart erforderlich**: nein — die Sektion ist hot-reloadable. Ein Save in der Admin-UI (inkl.
Profilwechsel) greift beim nächsten Aufruf.

### Wire-Dialekt (aus der `BaseUrl` abgeleitet)

OpenAI betreibt zwei Request-Formate nebeneinander: das klassische **Chat Completions**
(`/chat/completions`) und die neuere **Responses-API** (`/responses`). Manche Modelle werden nur
noch über Responses ausgeliefert. NodePilot spricht beide und leitet den Dialekt **aus dem Pfad der
`BaseUrl`** ab — es gibt bewusst keinen zusätzlichen Config-Key: der Anbieter nennt ohnehin eine
URL, und die trägt die Information schon.

| `BaseUrl` endet auf | Dialekt | Wohin gepostet wird | Wohin die Test-Probe geht |
|---|---|---|---|
| `/responses` | Responses | genau diese URL | `…/models` neben der URL |
| `/chat/completions` | Chat Completions | genau diese URL | `…/models` neben der URL |
| alles andere (`…/v1`) | Chat Completions | `{BaseUrl}/chat/completions` | `{BaseUrl}/models` |

Die Erkennung ist case-insensitiv und ignoriert Trailing-Slashes. Eine `BaseUrl` mit Query-String
(Azure-OpenAI-Stil `?api-version=…`) fällt in die letzte Zeile und wird nicht unterstützt.

Was sich im Responses-Dialekt intern unterscheidet (für Fehlersuche in Logs relevant): der Prompt
reist als `input` statt `messages`, der Cap heißt `max_output_tokens`, JSON-Mode ist `text.format`
statt `response_format`, Tool-Definitionen sind flach (kein verschachteltes `function`-Objekt), und
der Stream besteht aus typisierten Events statt Choice-Deltas. Zusätzlich sendet NodePilot immer
`store: false` — die Responses-API würde sonst per Default jeden Prompt 30 Tage im
OpenAI-Dashboard aufbewahren, während Chat Completions nichts speichert.

Die vier Kompatibilitäts-Fallbacks des Chat-Completions-Pfads (`max_tokens` →
`max_completion_tokens`, `stream_options`, `response_format`, `strict`-Tool-Schemas) gibt es im
Responses-Dialekt nicht: die ersten beiden Felder existieren dort gar nicht, die anderen beiden
sind keine optionalen Extras. Ein Responses-Endpunkt, der sie ablehnt, scheitert laut statt still
etwas anderes zu senden.

### Profile anlegen

`appsettings.json` und die Deploy-Templates liefern **`"Profiles": {}`** aus. Das ist Absicht: die
Runtime-Override-Datei (`appsettings.runtime.json`, von der Settings-UI geschrieben) ist nur ein
weiterer Configuration-Provider *über* der Basis-Config, und der Merge ist additiv. Ein in
`appsettings.json` definiertes Profil ließe sich über die UI deshalb nie löschen — es käme beim
nächsten Reload zurück. Die API weist ein solches Delete mit **400 `LLM_PROFILE_NOT_DELETABLE`**
ab statt es still zu ignorieren; die UI zeigt das Profil mit Schloss-Symbol und deaktiviertem
Löschen-Button (**editieren** geht weiter, der Override gewinnt).

Empfohlener Weg ist deshalb: **Settings → System → Integrations → LLM → „Profil hinzufügen"**.
So angelegte Profile gehören der Runtime-Datei und sind vollständig verwaltbar.

### Migration von der alten Flach-Config

Vor der Profil-Umstellung lagen `BaseUrl`/`ApiKey`/`Model`/… direkt unter `Llm`. Diese Keys werden
nicht mehr gelesen (kein Kompatibilitäts-Shim). Einmalig anzupassen:

1. **Settings-Datei / Runtime-Overrides:** den flachen Block nach `Llm:Profiles:<id>` verschieben
   und `Llm:ActiveProfileId` auf diese Id setzen. Ein bereits verschlüsselter `ApiKey`
   (`enc:v1:…`) zieht **unverändert** mit um — gleicher Protector, gleicher DPAPI-Scope, der Key
   muss nicht neu eingegeben werden.
2. **Env-/CLI-Overrides umbenennen:** `Llm__ApiKey` → `Llm__Profiles__<id>__ApiKey` (analog
   `Llm__BaseUrl`, `Llm__Model`, `Llm__MaxTokens`, `Llm__TimeoutSeconds`,
   `Llm__EnableToolCalling`, `Llm__ToolCallMaxDepth`). `Llm__Enabled` bleibt.
3. **Symptom bei vergessener Migration:** der Dienst bootet normal, aber jeder AI-Endpoint
   antwortet `503 LLM_NO_ACTIVE_PROFILE` und das Startup-Log warnt entsprechend.

---

## Lokale Modelle (empfohlen)

NodePilot bevorzugt lokale Modelle: keine Daten verlassen das Netzwerk, kein API-Pricing,
keine Rate-Limit-Sorgen. Der OpenAI-kompatible Transport läuft gegen Ollama, LM Studio,
vLLM, LocalAI und llama.cpp-Server.

Alle `BaseUrl`-Beispiele unten gehen von Ollama unter `http://localhost:11434/v1` aus. Diese
Runtimes sprechen ausschließlich Chat Completions — der `/responses`-Dialekt ist ausschließlich für
OpenAI-gehostete Modelle relevant, die nicht anders erreichbar sind.

| Modell | Ollama-Tag | Größe | Stärke | Mindest-RAM |
|---|---|---|---|---|
| **Gemma 4 31B** | `gemma4:31b` | 20 GB | Beste Allround-Code- + Reasoning-Qualität in der Klasse | 32 GB |
| Gemma 4 26B | `gemma4:26b` | 18 GB | MoE (4B active) — schnelle Inferenz, hoher Durchsatz bei geringer Rechenlast | 24 GB |
| Qwen 3.6 27B | `qwen3.6:27b` | 17 GB | Ausgezeichneter strukturierter / JSON-Output + zuverlässiges Tool-Calling | 32 GB |
| Qwen 3.6 35B | `qwen3.6:35b` | 24 GB | Größte Gesamt-Param-Anzahl, Top-JSON/Tool-Calling | 32 GB |
| Gemma 4 E4B | `gemma4:e4b` | 9,6 GB | Edge-Größe — gute Wahl für ein erstes lokales Profil | 16 GB |

**Tipp**: Workflow-Generierung profitiert von größeren Context-Windows
(`workflow-example.json` als Few-Shot frisst ~1k Token). Modelle mit ≥ 16k Context bevorzugt.
`MaxTokens` entsprechend hochsetzen (`16384` ist beim Hauskonfig-Default für lokale Modelle).

### Setup-Beispiel: Ollama

```bash
ollama serve
ollama pull qwen3.6:27b
```

`appsettings.json`:

```json
{
  "Llm": {
    "Enabled": true,
    "ActiveProfileId": "ollama",
    "Profiles": {
      "ollama": {
        "Name": "Local Ollama",
        "BaseUrl": "http://localhost:11434/v1",
        "Model": "qwen3.6:27b",
        "MaxTokens": 16384,
        "TimeoutSeconds": 360
      }
    }
  }
}
```

### Setup-Beispiel: LM Studio

LM Studio startet einen OpenAI-kompatiblen Server unter `http://localhost:1234/v1`.
Modell laden, „Local Server" → Start. `BaseUrl` entsprechend setzen.

---

## OpenAI Cloud

Wenn lokal nicht geht, läuft NodePilot auch gegen die OpenAI-API:

```json
{
  "Llm": {
    "Enabled": true,
    "ActiveProfileId": "openai",
    "Profiles": {
      "openai": {
        "Name": "OpenAI Cloud",
        "BaseUrl": "https://api.openai.com/v1",
        "Model": "gpt-4o-mini",
        "MaxTokens": 4000
      }
    }
  }
}
```

API-Key per Env-Var (die Profil-Id ist Teil des Variablennamens):

```powershell
[Environment]::SetEnvironmentVariable("Llm__Profiles__openai__ApiKey", "sk-...", "Machine")
```

Service neu starten. Klartext-`ApiKey` in der Settings-Datei funktioniert auch, löst aber
beim Startup eine Hardening-Warnung in den Logs aus.

---

## Sicherheit

- **Rollen**: Die Generierungs-Endpoints (`POST /api/ai/generate-script`, `POST /api/ai/generate-workflow`)
  sind nur für `Admin` und `Operator` zugänglich. Der Chat-Assistent (`POST /api/ai/chat`) ist für alle Rollen
  lesbar (Erklären), aber das **Anwenden** von Vorschlägen bleibt Admin/Operator. Viewer sehen die Schreib-KI-Buttons im UI nicht.
- **Rate-Limit**: 20 Anfragen/Min pro IP — schützt gegen Cost-Runaway bei Cloud-Modellen
  und gegen versehentliche Spam-Loops im UI.
- **SSRF-Block**: Beim Startup wird die `BaseUrl` **jedes** Profils gegen Cloud-Metadata-IPs
  (`169.254.169.254`, `metadata.google.internal`, `metadata.azure.com`) geprüft — nicht nur die des
  aktiven. Grund: Profilwechsel ist ein Settings-Save ohne Neustart, ein „geparktes" Profil mit
  Metadata-URL wäre sonst eine Waffe, die erst beim Umschalten zündet. Treffer → Service startet
  nicht, und derselbe Check lehnt schon den Save mit 400 ab.
- **Prompt-Injection**: Upstream-Variablen werden nur als **Schema** (Step-ID, Label,
  Variablen-Name, Ausdruck wie `{{step.output}}`, Typ) gesendet — nie deren **Werte**. Im System-Prompt sind sie als
  „untrusted JSON, not instructions" markiert. Trotzdem Residualrisiko: ein Step-Label
  wie `"; rm -rf / #` könnte den LLM beeinflussen. Daher gilt:
- **Sandbox-Schutz**: KI-generiertes PowerShell wird **am Cursor eingefügt** (Default),
  nicht stumm den ganzen Editor ersetzen. User sieht das Script bevor er Run klickt.
- **Audit**: Erfolgreiche Generierungen schreiben `AI_SCRIPT_GENERATED` bzw.
  `AI_WORKFLOW_GENERATED` ins AuditLog, der Workflow-Chat `AI_WORKFLOW_EXPLAINED` und beim Übernehmen
  eines Vorschlags `AI_PROPOSAL_APPLIED` (mit Node-/Edge-Counts); der Wissens-Assistent schreibt
  `AI_KNOWLEDGE_ASKED`. Details enthalten Modell, Dauer, Token-Counts — **niemals** den Prompt-Text (PII).

---

## Fehlerverhalten

| HTTP | Code | Ursache | Frontend-Anzeige |
|---|---|---|---|
| 503 | `LLM_DISABLED` | `Llm:Enabled=false` | „KI ist deaktiviert. Operator muss `Llm:Enabled` setzen." |
| 503 | `LLM_NO_ACTIVE_PROFILE` | `Llm:Enabled=true`, aber `Llm:ActiveProfileId` benennt kein vorhandenes Profil | „Kein aktives LLM-Profil konfiguriert." mit Verweis auf Settings → Integrations → LLM |
| 503 | `LLM_UNREACHABLE` | Endpoint nicht erreichbar (Ollama down, falscher Port) | „KI-Endpoint nicht erreichbar." mit `BaseUrl` |
| 503 | `LLM_TIMEOUT` | LLM hat länger als `TimeoutSeconds` gebraucht | „KI hat zu lang gebraucht." |
| 503 | `LLM_UNAUTHORIZED` | API-Key fehlt oder falsch | „KI-Authentifizierung fehlgeschlagen." |
| 503 | `LLM_RATE_LIMITED` | Cloud-Provider drosselt | „KI-Provider drosselt. Bitte später nochmal." |
| 502 | `LLM_MALFORMED_RESPONSE` | Antwort war kein parsebares JSON (Workflow-Gen) trotz Retry | „KI-Antwort unbrauchbar." |
| 502 | `LLM_UPSTREAM_ERROR` | Anderer 5xx oder unerwarteter Wire-Fehler | „KI-Provider liefert Fehler." |
| 429 | — | Lokales Rate-Limit (20/min/IP) | „Zu viele KI-Anfragen. Bitte 1 Min warten." |

---

## Empfohlene Prompt-Beispiele (Smoke-Tests)

**Script-Gen** (im `runScript`-Editor):

```
Liste alle Dienste auf die "Automatic" gestellt sind aber nicht laufen.
Gib jeden als $svc.Name aus, plus exit 1 wenn mindestens einer existiert.
```

**Workflow-Gen** (auf der Workflow-Übersicht):

```
Täglich um 06:00 prüft der Workflow den Disk-Space von ServerA.
Wenn freier Speicher unter 10% fällt, wird ein Cleanup-Skript ausgeführt
und eine Mail an ops@firma geschickt.
```

Beide sollten in <30 s ein syntaktisch valides Resultat produzieren.

**KI-Aufruf im Workflow** (prüft, dass `llmQuery` gewählt wird — nicht `restApi`):

```
Lies die letzte Zeile aus C:\logs\app.log, lass ein Modell beurteilen ob sie
auf einen Fehler hindeutet, und logge die Beurteilung.
```

Das Ergebnis muss einen `llmQuery`-Node enthalten. Ein handgebauter OpenAI-POST auf einem
`restApi`-Node wäre ein Rückfall in das Verhalten von vor der generierten Katalog-Sektion.

---

## Activity-Wissen der KI

Generierung und Assistent kennen **alle** Activity-Typen. Der Katalog-Abschnitt der System-Prompts
wird zur Laufzeit aus dem Backend-Katalog plus der kuratierten Config-Reference
(`src/NodePilot.Core/Activities/Embedded/activity-config-reference.json`) gerendert — es gibt keine
Ausschlussliste mehr, aus der eine Activity herausfallen könnte.

Zusätzlich werden die **enabled Custom Activities** der Installation pro Anfrage angehängt, sodass
die KI sie vorschlagen kann statt sie aus rohen `runScript`-Schritten nachzubauen. Namen und
Beschreibungen sind operator-geschriebener Freitext und werden als Daten gerahmt und einzeilig
normalisiert; die Liste ist auf 40 Einträge bzw. ~10 k Zeichen gedeckelt, Überlauf wird ausgewiesen.

---

## Tool-Calling

Opt-in über `EnableToolCalling=true` **am aktiven Profil**
(`Llm:Profiles:<id>:EnableToolCalling`). Ist es an, läuft der Chat-Assistent (`POST /api/ai/chat`)
eine OpenAI-Function-Calling-Schleife (`tool_choice: auto`): das Modell darf **read-only** Tools auf
der **secret-redigierten** Workflow-Definition aufrufen, deren Ergebnisse zurückgespeist werden, bevor
es die finale Antwort/den Vorschlag produziert. Verfügbare Tools:

- **`analyze_workflow`** — deterministische Static-Analysis (fehlender Trigger, unreachable/orphan Steps,
  Zyklen, Remote-Step ohne Target-Machine, Strukturfehler) — dieselben Codes wie der Canvas-Linter.
- **`list_activity_types`** — der Activity-Katalog.
- **`list_recent_executions`** — die jüngsten Läufe des aktuell geöffneten Workflows (Status, Zeiten,
  Fehlermeldung, fehlgeschlagene Steps; `take` 1–20, Default 10).
- **`get_execution_steps`** — die Step-Details einer Execution (Status, Versuche, Output/ErrorOutput).
- **`get_failure_context`** — One-Call-Debugging: jüngster fehlgeschlagener Lauf + dessen Failed-Steps
  mit ErrorOutput. Erste Wahl bei „warum ist der Workflow fehlgeschlagen?".

Die drei Execution-Log-Tools sind nur aktiv, wenn der Workflow **gespeichert** ist UND der Caller
**Folder-Read-Recht** auf ihn hat (der Controller prüft das vor dem Stream; sonst werden sie gar nicht
erst angeboten — der Chat läuft normal weiter). Ihre Ergebnisse sind **doppelt secret-redigiert**
(beim Persistieren durch den `OutputRedactor` und erneut beim Lesen im `ExecutionLogReader`) und
gekürzt (1500 Zeichen pro Output-Feld, 500 für Fehlermeldungen, max. 100 Steps; `get_failure_context`
kürzt Step-Outputs erst bei 2000 Zeichen).

Begrenzt durch `ToolCallMaxDepth` des aktiven Profils (Default `6`, gültig 1–10): in der letzten erlaubten Runde sendet
der Server **keine** `tools` mehr und erzwingt so eine Text-Antwort. Der SSE-Stream erhält dabei
zusätzlich `tool_call`- und `tool_result`-Events; das UI zeigt einen „🔧 analyze_workflow —
running…/checked"-Indikator. Voraussetzung: ein Modell, das Function-Calling zuverlässig kann —
viele kleine lokale Modelle nicht. Aus → der Chat verhält sich exakt wie vorher.

---

## Globaler AI-Chat / Wissens-Assistent (`/ai-chat`)

Vom workflow-spezifischen Chat getrennt: ein seitenweiter, **canvas-freier** read-only Q&A-Assistent
in der UI-Seite `/ai-chat` (`POST /api/ai/knowledge/ask`, SSE; Capabilities `GET /api/ai/knowledge/capabilities`).
Erklärt Konzepte, beantwortet „wie viele Workflows/Maschinen/Execution gibt es", hilft bei Konfig- und
Code-Fragen — ohne einen Workflow im Designer zu öffnen. Opt-in via `AiKnowledge:Enabled` (zusätzlich zu
`Llm:Enabled`), hot-reloadbar.

**Vier Wissensquellen**, jede admin-toggelbar (Sektion `AiKnowledge`); Docs + Operational default an,
Source-Code + DB default aus:

| Quelle | Toggle | Tools | Gate |
|---|---|---|---|
| Dokumentation | `DocsEnabled` | `search_docs`, `read_doc` | — |
| Workflows & Betrieb | `OperationalEnabled` | `get_workflow_definition`, `analyze_workflow`, `get_next_scheduled_fires` | RBAC-folder-scoped |
| Workflows & Betrieb (Listen) | via DB-Quelle | "Welche Workflows/Läufe/Maschinen gibt es" → `list_db_tables` + `execute_readonly_sql` | Admin/Operator (text2sql) |
| Systemkonfiguration | (immer, wenn privilegiert) | `read_settings` | Admin/Operator |
| Quellcode | `SourceCodeEnabled` | `search_source`, `read_source` | Admin/Operator |
| **DB / text2sql** | `DbEnabled` | `list_db_tables`, `get_db_table`, `execute_readonly_sql` | Admin/Operator |

**text2sql** heißt: das LLM übersetzt die Frage in provider-spezifisches SQL, NodePilot liefert SQL-Dialekt,
Schema, Foreign Keys und Read-Only-Ausführung.
Die DB-Tools laufen über `ISqlKnowledgeReader` (Core-Interface, Api-Impl `SqlKnowledgeReader` reuses die
DbAdmin-Services). `execute_readonly_sql` nimmt ein einzelnes Statement bis 64 KiB. Der Guard sitzt am
Executor (nicht nur am HTTP-Controller), erlaubt als erstes Keyword nur `SELECT`/`WITH`/`EXPLAIN`/`SHOW`/
`VALUES`/`TABLE` und lehnt mutierende Keywords, gefährliche Routinen, Multi-Statements sowie
`EXPLAIN ANALYZE` ab. PostgreSQL setzt zusätzlich `SET TRANSACTION READ ONLY`; alle Provider rollen die
Transaktion zurück. **Secret-Schutz mehrlagig**: Schema-Tools verbergen `IsHidden`-Spalten; jede SQL-Referenz
auf eine geschützte Spalte wird bereits vor Ausführung abgelehnt (auch Alias-/Ausdrucksvarianten);
Result-Spalten werden zusätzlich nach Namen maskiert und übrige Zellen durch den `IAuditDetailsRedactor`
geführt. Row-Cap 200. Übergroße Tool-Resultate bleiben valides JSON mit explizitem Truncation-Hinweis.
DB-Tools nutzen Strict Function Schemas; inkompatible lokale Endpoints erhalten automatisch einen
Best-Effort-Retry. SQL-Text wird nicht auditiert, stattdessen nur Anzahl und SHA-256-Kurzfingerprints.
Text2SQL ist nur als Capability sichtbar, wenn das aktive Profil `EnableToolCalling=true` hat.

**Einstiegsvorschläge folgen den Capabilities.** Der Empty-State hält zwei Prompt-Sets in i18n
(`ai:knowledge.examples` + `ai:knowledge.examplesLite`, je 8 Strings) und wählt anhand von `caps.db`:
mit DB-Quelle Betriebsauswertungen (letzte Fehlläufe, hängende Runs, unerreichbare Maschinen,
Audit-Trail), sonst Doku- und Zeitplan-Fragen. Grund: die Alltagsfragen brauchen text2sql, das per
Default aus **und** Admin/Operator-only ist — einem Viewer würden sie nur „Quelle nicht verfügbar"
liefern. Das Grid rendert erst nach dem Auflösen der Capabilities, sonst blitzt das Lite-Set auf.

---

## Bewusst auf v2 verschoben

- **Per-User-Token-Spend-Tracking** — DB-Schema-Erweiterung. Erst wenn IP-Rate-Limit nicht reicht.
- **AI-Activity zur Laufzeit** (LLM-Call innerhalb eines Workflow-Runs) — würde den LLM-Client
  von der API in die Engine promoten müssen. Nicht zu verwechseln mit der bereits verfügbaren
  `GET /api/ai/chat/activity/{workflowId}`-Audit-Ansicht.
- **React-Flow-Graph-Preview** im Workflow-Gen-Dialog — JSON+Stats reicht v1.
- **Multi-Provider-Fallback** (lokal-first, Cloud-Fallback) — zu clever für v1.
