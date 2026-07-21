# Workflow-Kontrollfluss & Edit-Lifecycle

## Execution

`POST /api/workflows/{id}/execute` — asynchron, `202` + `ExecutionId`. Body:

```json
{ "parameters": {}, "timeoutSeconds": 120, "debug": false }
```

Fortschritt via SignalR. Mit `debug: true` → Breakpoints, `StepPaused`, Resume via `POST /executions/{id}/resume`.

| Endpoint | Semantik |
|---|---|
| `POST /execute` | Startet Lauf |
| `POST /enable` / `/disable` | Kill-Switch. `enable` erfordert kein Lock (423 sonst). `disable` ignoriert Locks. |
| `POST /cancel-all` | Cancelt alle Running-Executions des Workflows |
| `POST /executions/{id}/cancel|retry|resume` | Einzelner Lauf |

**Disable + cancel-all = Quarantäne.**

## Edit-Lock (SCOrch-style)

Workflows haben einen per-User Edit-Lock (`CheckedOutByUserId` + `CheckedOutAt`). Mutierende Endpoints liefern `423 Locked`, wenn der Caller nicht Lock-Owner ist. `disable` ist **nicht** lock-gegated (Incident-Kill-Switch).

| Endpoint | Verhalten |
|---|---|
| `POST /lock` | Atomar `IsEnabled=false` + Lock-Fields setzen. 409 wenn schon gelockt. |
| `POST /unlock` | Lock-Fields auf null. `IsEnabled` bleibt unverändert. |
| `POST /publish` | Atomar: Save + `IsEnabled=true` + Unlock. |
| `POST /force-unlock` | Admin-only. Bricht fremden Lock. |

## UX-Flow

1. Workflow ist **Productive** (Enabled, kein Lock) → Designer read-only. Toolbar: **Edit** + **Disable**.
2. **Edit** → `lock` → Locked-by-Me + Disabled. Save wird sichtbar, Disable-Slot wird zu **Publish**.
3. **Save** → Zwischenstand (PUT, kein Status-Wechsel). Repeatierbar.
4. **Publish** → atomar Save + Enable + Unlock. Workflow ist Productive.
5. Alternative: **Exit** → `unlock`. Workflow bleibt Disabled. Publish-Slot callt `/enable` (reaktivieren ohne Edit-Roundtrip).

## Publish/Disable-Toggle (ein Button-Slot, vier States)

| Workflow-State | Label | Endpoint |
|---|---|---|
| `IsEnabled=true`, kein Lock | "Disable" (red) | `/disable` |
| `IsEnabled=false`, lock-by-me | "Publish" (primary) | `/publish` |
| `IsEnabled=false`, kein Lock | "Publish" (primary) | `/enable` |
| `IsEnabled=false`, lock-by-other | "Publish" disabled | (none — Tooltip nennt Lock-Owner) |

Sichtbarkeit gegated durch `roleCanWrite` (Admin/Operator), nicht durch lock-by-me. Viewer sehen den Slot nicht.

## `canWrite`-Regel

```
canWrite = role !== 'Viewer' && checkedOutByUserId === currentUserId
```

Alle `nodesDraggable`/`nodesConnectable`/Save/Tidy-Affordances folgen automatisch — kein separates Edit-Mode-Toggle. `currentUserId` via `/auth/me` und `LoginResponse.userId` (das JWT ist ein httpOnly-Cookie, den die SPA nicht dekodieren kann).

## Workflow-Version-History

`Update` / `Rollback` snapshotten die vorherige Definition.

## Idempotency-Keys

`POST /api/trigger/{name}` akzeptiert `Idempotency-Key`-Header.

## Trigger-los / Empty-Workflow

- **Kein (aktiver) Trigger** (trigger-los **oder** nur Zyklen) → 0 Roots → `Failed` (ErrorMessage nennt den fehlenden Trigger/Start) + Warning. Roots sind ausschließlich Trigger-Nodes — kein `inDegree==0`-Fallback.
- **Leer** (0 Nodes) → läuft mit 0 Steps durch (`Succeeded`).

## Node-Level `disabled`

`data.disabled: true` → Node wird `Skipped`, Downstream ohne andere Quellen ebenfalls.

## Beispiele

> `NP=http://localhost:5000`, Auth via `-b cookie.jar` (siehe [Authentifizierung](./authentication)). JSON-Property-Keys sind camelCase; Enum-Werte als PascalCase-String (`"status":"Succeeded"`).

### Ausführen

```bash
# Asynchron starten — 202 + ExecutionResponse, Location-Header auf /api/executions/{id}
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../execute" \
  -H 'Content-Type: application/json' \
  -d '{ "parameters": { "version":"2.1.0", "env":"prod" },
        "timeoutSeconds": 300, "debug": false }'
# 202 → {"id":"7e3f...","workflowId":"...","status":"Pending","triggeredBy":"manual",...}

# Debug-Run mit Pause — Resume via /api/executions/{id}/resume
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../execute" \
  -H 'Content-Type: application/json' \
  -d '{ "parameters":{}, "debug": true }'

# Admin kann eine Maintenance-Window bypassen
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../execute?force=true" \
  -H 'Content-Type: application/json' -d '{}'
```

Fortschritt via SignalR (`/hubs/execution`). `parameters`-Keys mit `__`-Prefix → 400. Disabled Workflow → 400. Maintenance-Window-Block → 423 `{"message":"...","windowId":"...","activeUntil":"..."}`.

### Edit-Lock-Lifecycle

```bash
# Lock holen — atomar IsEnabled=false + Lock-Fields. 409 wenn schon von jemand anderem.
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../lock"
# 200 → WorkflowResponse (Locked-by-Me, Disabled)

# Zwischenstand speichern (PUT, kein Status-Wechsel) — 423 ohne eigenen Lock
curl -s -b cookie.jar -X PUT "$NP/api/workflows/21f1c0d4-..." \
  -H 'Content-Type: application/json' \
  -d '{ "name":"Deploy App", "description":"wip",
        "definitionJson":"{\"nodes\":[...],\"edges\":[...]}" }' -i   # 204

# Publish — atomar Save + Enable + Unlock. Workflow ist wieder Productive.
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../publish" \
  -H 'Content-Type: application/json' \
  -d '{ "name":"Deploy App", "description":null,
        "definitionJson":"{\"nodes\":[...],\"edges\":[...]}" }'

# Exit ohne Publish: nur Unlock — Workflow bleibt Disabled
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../unlock"

# Reaktivieren ohne Edit-Roundtrip (IsEnabled=false, kein Lock → /enable)
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../enable" -i   # 204

# Fremden Lock brechen (Admin-only)
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../force-unlock"
```

### Quarantäne & Einzelne Lauf-Kontrolle

```bash
# Quarantäne = Disable + cancel-all
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../disable" -i       # 204, ignoriert Locks
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../cancel-all"
# 200 → {"total":3,"signalled":2}

# Einzelne Execution
curl -s -b cookie.jar -X POST "$NP/api/executions/7e3f.../cancel" -i            # 204
curl -s -b cookie.jar -X POST "$NP/api/executions/7e3f.../retry"  -i            # 202 + Location

# Debug-Resume — stepId required, mode: continue|stepOver|stop
curl -s -b cookie.jar -X POST "$NP/api/executions/7e3f.../resume" \
  -H 'Content-Type: application/json' \
  -d '{ "stepId":"runHealth", "mode":"stepOver",
        "overrides": { "vars.targetHost":"srv02" } }' -i                        # 204
```

`resume` 409 `{"message":"No paused step with this id ..."}` wenn die Pause schon vorbei ist; 403 wenn nicht Debug-Session-Owner (und nicht Admin).
