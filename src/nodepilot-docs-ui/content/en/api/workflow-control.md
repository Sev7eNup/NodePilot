# Workflow control flow & edit lifecycle

Workflow execution and workflow editing are separate processes. Executions run asynchronously. Changing a workflow definition requires an edit lock.

## Execution

`POST /api/workflows/{id}/execute` — asynchronous, `202` + `ExecutionId`. Body:

```json
{ "parameters": {}, "timeoutSeconds": 120, "debug": false }
```

Progress through SignalR. With `debug: true` → breakpoints, `StepPaused`, resume via `POST /executions/{id}/resume`.

| Endpoint | Semantics |
|---|---|
| `POST /execute` | Starts a run |
| `POST /enable` / `/disable` | The kill switch. `enable` requires a **lock-free** workflow — an existing lock (including your own) returns `423`; from edit mode you publish instead. `disable` ignores locks. |
| `POST /cancel-all` | Cancels every running execution of the workflow |
| `PUT /concurrency-limit` | Caps how many executions run at once. Body: `{"maxConcurrentExecutions": 5}`, or `null` for unlimited. The property is required — an empty body is a `400`, so a client can never clear the limit by omission. `0` is rejected; disable the workflow instead. |
| `POST /executions/{id}/cancel|retry|resume` | A single run |

**Disable + cancel-all = quarantine.**

## Concurrency limit

`MaxConcurrentExecutions` caps how many executions of one workflow run at the same time. The
limit holds across **every** caller — manual runs, schedule/file/database/event-log triggers,
webhooks, external triggers and sub-workflow calls from `startWorkflow` and `forEach` — so two
parents fanning out to the same child cannot exceed it between them.

Reaching the limit **queues**, it never rejects: a queued run stays `Pending` and starts on its
own as soon as a slot frees. Nothing is lost and no run is marked failed. A `forEach` loop's
`maxParallelism` still bounds that one loop; the workflow limit bounds the child across all of
them, and the tighter of the two wins.

Changing the limit takes effect immediately. Lowering it below the number of active runs cancels
nothing — the excess drains and the cap applies from then on. Raising it releases queued runs at
once rather than one per completing run.

Setting it needs no edit lock and creates no new workflow version, because it is an operational
control rather than a change to the workflow definition.

## The edit lock (SCOrch style)

Workflows have a per-user edit lock (`CheckedOutByUserId` + `CheckedOutAt`). Mutating endpoints return `423 Locked` if the caller is not the lock owner. `disable` is **not** lock-gated (it is the incident kill switch).

| Endpoint | Behaviour |
|---|---|
| `POST /lock` | Atomically sets `IsEnabled=false` + the lock fields. 409 if already locked. |
| `POST /unlock` | Sets the lock fields to null. `IsEnabled` stays as it is. |
| `POST /publish` | Atomically: save + `IsEnabled=true` + unlock. |
| `POST /force-unlock` | Admin only. Breaks someone else's lock. |

## The UX flow

1. The workflow is **productive** (enabled, no lock) → the designer is read-only. Toolbar: **Edit** + **Disable**.
2. **Edit** → `lock` → locked-by-me and disabled. Save becomes visible, and the disable slot becomes **Publish**.
3. **Save** → an intermediate state (PUT, no status change). Repeatable.
4. **Publish** → atomically save + enable + unlock. The workflow is productive.
5. Alternative: **Exit** → `unlock`. The workflow stays disabled. The publish slot then calls `/enable` (reactivating without an edit round trip).

## The publish/disable toggle (one button slot, four states)

| Workflow state | Label | Endpoint |
|---|---|---|
| `IsEnabled=true`, no lock | "Disable" (red) | `/disable` |
| `IsEnabled=false`, locked by me | "Publish" (primary) | `/publish` |
| `IsEnabled=false`, no lock | "Publish" (primary) | `/enable` |
| `IsEnabled=false`, locked by someone else | "Publish" disabled | (none — the tooltip names the lock owner) |

Visibility is gated by `roleCanWrite` (admin/operator), not by locked-by-me. Viewers do not see the slot.

## The `canWrite` rule

```
canWrite = role !== 'Viewer' && checkedOutByUserId === currentUserId
```

All `nodesDraggable`/`nodesConnectable`/save/tidy affordances follow automatically — there is no separate edit-mode toggle. `currentUserId` comes from `/auth/me` and `LoginResponse.userId` (the JWT is an httpOnly cookie the SPA cannot decode).

## Workflow version history

`Update` / `Rollback` snapshot the previous definition.

## Idempotency keys

`POST /api/trigger/{name}` accepts an `Idempotency-Key` header.

## Trigger-less / empty workflows

- **No (active) trigger** (trigger-less **or** only cycles) → 0 roots → `Failed` (the error message names the missing trigger/start) + a warning. Roots are trigger nodes exclusively — there is no `inDegree==0` fallback.
- **Empty** (0 nodes) → runs through with 0 steps (`Succeeded`).

## Node-level `disabled`

`data.disabled: true` → the node becomes `Skipped`, and downstream nodes without another source do too.

## Examples

> `NP=http://localhost:5000`, authentication via `-b cookie.jar` (see [Authentication](./authentication)). JSON property keys are camelCase; enum values are PascalCase strings (`"status":"Succeeded"`).

### Running

```bash
# Start asynchronously — 202 + ExecutionResponse, with a Location header pointing at /api/executions/{id}
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../execute" \
  -H 'Content-Type: application/json' \
  -d '{ "parameters": { "version":"2.1.0", "env":"prod" },
        "timeoutSeconds": 300, "debug": false }'
# 202 → {"id":"7e3f...","workflowId":"...","status":"Pending","triggeredBy":"manual",...}

# A debug run with a pause — resume via /api/executions/{id}/resume
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../execute" \
  -H 'Content-Type: application/json' \
  -d '{ "parameters":{}, "debug": true }'

# An admin can bypass a maintenance window
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../execute?force=true" \
  -H 'Content-Type: application/json' -d '{}'
```

Progress through SignalR (`/hubs/execution`). `parameters` keys with a `__` prefix → 400. A disabled workflow → 400. A maintenance-window block → 423 `{"message":"...","windowId":"...","activeUntil":"..."}`.

### The edit-lock lifecycle

```bash
# Request the lock — atomically IsEnabled=false + the lock fields. 409 if someone else holds it.
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../lock"
# 200 → WorkflowResponse (locked by me, disabled)

# Save an intermediate state (PUT, no status change) — 423 without your own lock
curl -s -b cookie.jar -X PUT "$NP/api/workflows/21f1c0d4-..." \
  -H 'Content-Type: application/json' \
  -d '{ "name":"Deploy App", "description":"wip",
        "definitionJson":"{\"nodes\":[...],\"edges\":[...]}" }' -i   # 204

# Publish — atomically save + enable + unlock. The workflow is productive again.
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../publish" \
  -H 'Content-Type: application/json' \
  -d '{ "name":"Deploy App", "description":null,
        "definitionJson":"{\"nodes\":[...],\"edges\":[...]}" }'

# Exit without publishing: unlock only — the workflow stays disabled
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../unlock"

# Reactivate without an edit round trip (IsEnabled=false, no lock → /enable)
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../enable" -i   # 204

# Break someone else's lock (admin only)
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../force-unlock"
```

### Quarantine & controlling a single run

```bash
# Quarantine = disable + cancel-all
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../disable" -i       # 204, ignores locks
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../cancel-all"
# 200 → {"total":3,"signalled":2}

# A single execution
curl -s -b cookie.jar -X POST "$NP/api/executions/7e3f.../cancel" -i            # 204
curl -s -b cookie.jar -X POST "$NP/api/executions/7e3f.../retry"  -i            # 202 + Location

# Debug resume — stepId is required, mode: continue|stepOver|stop
curl -s -b cookie.jar -X POST "$NP/api/executions/7e3f.../resume" \
  -H 'Content-Type: application/json' \
  -d '{ "stepId":"runHealth", "mode":"stepOver",
        "overrides": { "vars.targetHost":"srv02" } }' -i                        # 204
```

`resume` returns 409 `{"message":"No paused step with this id ..."}` if the pause is already over, and 403 if the caller is not the debug session's owner (and not an admin).
