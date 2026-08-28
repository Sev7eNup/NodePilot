# API endpoints

The REST API manages workflows, executions, infrastructure and administration. In the local development environment it runs on port 5000. Live status is pushed over SignalR at `/hubs/execution`. Mutating workflow endpoints return `423 Locked` if the calling user does not hold the edit lock; `disable` is exempt from that.

> **JSON format:** property names use `camelCase`. Enum values are serialized as the .NET name in PascalCase, for example `"role":"Admin"` and `"status":"Succeeded"`. Sign-in uses the httpOnly cookie `np_auth` by default. `curl` stores and sends it with `-c cookie.jar -b cookie.jar`. The examples use `$NP = "http://localhost:5000"`.

## Workflows

| Endpoint | Purpose |
|---|---|
| `GET /api/workflows` | The list (an array, 500-row cap, filtered by folder RBAC) |
| `POST /api/workflows` | Create (Admin/Operator) — 201 |
| `PUT /api/workflows/{id}` | Update — 204 (423 without the lock, 409 on a version conflict) |
| `DELETE /api/workflows/{id}` | Delete (Admin) — 204 |
| `POST /{id}/execute` | Start a run — 202 + ExecutionId |
| `POST /{id}/duplicate` | Duplicate — 201 (the copy is born disabled) |
| `POST /{id}/enable` / `disable` | The kill switch — 204 |
| `POST /{id}/cancel-all` | Cancel every running execution — 200 |
| `POST /{id}/lock` / `unlock` | The edit lock — 200 |
| `POST /{id}/publish` | Save + enable + unlock (atomically) — 200 |
| `POST /{id}/force-unlock` | Admin only, breaks someone else's lock — 200 |

```bash
# The list + a single workflow
curl -s -b cookie.jar "$NP/api/workflows" | jq '.[0] | {id,name,isEnabled,version,folderPath}'
curl -s -b cookie.jar "$NP/api/workflows/by-name/deploy-prod" | jq '{id,name,isEnabled}'

# Create (definitionJson = a JSON object as a string, ≤5 MiB, depth ≤64)
curl -s -b cookie.jar -X POST "$NP/api/workflows" \
  -H 'Content-Type: application/json' \
  -d '{ "name": "Deploy App",
        "description": "Deploys the web app",
        "definitionJson": "{\"nodes\":[],\"edges\":[]}",
        "folderId": null }'

# Update (requires the lock)
curl -s -b cookie.jar -X PUT "$NP/api/workflows/21f1c0d4-..." \
  -H 'Content-Type: application/json' \
  -d '{ "name": "Deploy App v2", "description": "updated",
        "definitionJson": "{\"nodes\":[...],\"edges\":[...]}" }' -i   # 204

# Publish (atomic), duplicate, cancel-all
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../publish" \
  -H 'Content-Type: application/json' \
  -d '{ "name": "Deploy App", "description": null,
        "definitionJson": "{\"nodes\":[...],\"edges\":[...]}" }'
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../duplicate"
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../cancel-all"   # {"total":3,"signalled":2}
```

The 409 lock-conflict body: `{"message":"Workflow is already locked by alice.","lockedByUserName":"alice","lockedAt":"..."}`. A 409 version conflict: `{"code":"workflow_version_conflict","currentVersion":4}`.

## Versions & contract

| Endpoint | Purpose |
|---|---|
| `GET /{id}/versions` | The version history |
| `GET /{id}/versions/{v}` | A specific version |
| `POST /{id}/rollback/{v}` | Roll back — body `{"reason": "..."}` |
| `GET /{id}/contract` | The input/output contract |
| `GET /by-name/{name}/contract` | By-name lookup (exact case wins, otherwise case-insensitive; ambiguous → 409) |

```bash
curl -s -b cookie.jar "$NP/api/workflows/21f1c0d4-.../versions" | jq '.[0]'
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../rollback/12" \
  -H 'Content-Type: application/json' -d '{"reason":"revert bad config"}'

# Contract: inputs from manualTrigger.parameters + outputs from returnData + system
curl -s -b cookie.jar "$NP/api/workflows/by-name/deploy-prod/contract" | jq
# { "workflowName":"Deploy App", "hasManualTrigger":true,
#   "inputs":[{"name":"version","type":"string","required":true,...}],
#   "outputs":[{"name":"__executionId","source":"system"},{"name":"deployResult","source":"single"}] }
```

## Step test & coverage

| Endpoint | Purpose |
|---|---|
| `POST /{id}/steps/{stepId}/test` | A single step test |
| `GET .../test-context` | The test context (`?executionId=`) |
| `GET .../test-context/runs` | Available runs |
| `GET /{id}/coverage?windowDays=N` | Step coverage |
| `GET /{id}/step-health` | Step health |
| `GET /{id}/step-stats?windowDays=N` | Step statistics |

```bash
# Test a step with mock variables + a config override
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../steps/runHealth/test" \
  -H 'Content-Type: application/json' \
  -d '{ "mockVariables": { "checkDisk.output": "7", "checkDisk.param.freeGb": "7" },
        "configOverride": { "script": "Get-Process", "timeoutSeconds": 30 } }' | jq
# { "success":true, "output":"COMPUTERNAME=SRV01",
#   "outputParameters":{"computerName":"SRV01"}, "durationMs":412.3 }

curl -s -b cookie.jar "$NP/api/workflows/21f1c0d4-.../coverage?windowDays=7" | jq
```

## Import/export

| Endpoint | Purpose |
|---|---|
| `GET /export` | Bulk export |
| `GET /{id}/export` | Single export |
| `POST /import?folderId={guid}` | Import (name collision → the suffix `" (Imported 2)"`; `folderId` optional, absent → root, RBAC = edit on the target folder) |
| `POST /import-scorch?folderId={guid}` | SCOrch import (`.ois_export`; the same folder targeting) |

```bash
curl -s -b cookie.jar "$NP/api/workflows/21f1c0d4-.../export" -o deploy.envelope.json
import_result="$(curl -s -b cookie.jar -X POST "$NP/api/workflows/import" \
  -H 'Content-Type: application/json' --data-binary @deploy.envelope.json)"
workflow_id="$(printf '%s' "$import_result" | jq -r '.workflows[0].id')"

# Import is always disabled; only this explicit second call arms the workflow.
curl -s -o /dev/null -w '%{http_code}\n' -b cookie.jar -X POST \
  "$NP/api/workflows/$workflow_id/enable"
```

Both import endpoints always create disabled workflows and return their ids in `workflows[].id`.
`POST /api/workflows/{id}/enable` is idempotent, requires Admin/Operator plus edit permission on the
workflow folder, and succeeds with `204`. An edit lock or an unsafe workflow definition still blocks
activation visibly. Envelope type: `nodepilot-workflow-export/v1`. Secrets are redacted here (`***`)
— a sharing artifact, not a DR artifact.

## Executions

| Endpoint | Purpose |
|---|---|
| `GET /api/executions` | The list (`?workflowId=&activeOnly=&terminalOnly=`, 500 cap) |
| `GET /api/executions/{id}` | A single execution |
| `GET /api/executions/{id}/steps` | The steps of an execution |
| `POST /api/executions/{id}/cancel` / `retry` / `resume` | A single run |

```bash
curl -s -b cookie.jar "$NP/api/executions?workflowId=21f1c0d4-...&activeOnly=true" | jq '.[0]'
curl -s -b cookie.jar "$NP/api/executions/7e3f..." | jq '{status,startedAt,completedAt,triggeredBy}'
curl -s -b cookie.jar "$NP/api/executions/7e3f.../steps" | jq '.[] | {stepId,status,durationMs}'
curl -s -b cookie.jar -X POST "$NP/api/executions/7e3f.../cancel" -i   # 204
curl -s -b cookie.jar -X POST "$NP/api/executions/7e3f.../retry"  -i   # 202 + Location
```

The `resume` body (a debug pause): `{"stepId":"runHealth","mode":"continue|stepOver|stop","overrides":{"vars.targetHost":"srv02"}}` → 204.

## Machines & credentials

| Endpoint | Purpose |
|---|---|
| `GET/POST/PUT/DELETE /api/machines` | Machines (POST/PUT Admin/Operator, DELETE Admin) |
| `POST /{id}/test` | A machine connection test — body `{"credentialId": null}` |
| `GET/POST/PUT/DELETE /api/credentials` | Credentials (DELETE Admin) |

```bash
# Create a machine
curl -s -b cookie.jar -X POST "$NP/api/machines" -H 'Content-Type: application/json' \
  -d '{ "name":"SRV-PROD-01", "hostname":"srv-prod-01.contoso.com",
        "winRmPort":5985, "useSsl":false,
        "defaultCredentialId":"4c2a-...", "tags":"prod;web" }'

# Connection test (credentialId null = the machine default)
curl -s -b cookie.jar -X POST "$NP/api/machines/9f1a.../test" \
  -H 'Content-Type: application/json' -d '{"credentialId":null}'
# { "success":true, "computerName":"SRV-PROD-01", "credentialUsed":"svc-winrm" }

# Credential (password minimum 8 characters, never returned)
curl -s -b cookie.jar -X POST "$NP/api/credentials" -H 'Content-Type: application/json' \
  -d '{ "name":"svc-winrm", "username":"CONTOSO\\svc-winrm", "password":"p@ssw0rd!", "domain":null }'
# 201 → { "id":"...", "name":"svc-winrm", "username":"CONTOSO\\svc-winrm", "domain":null }
```

## Global variables

`GET /api/global-variables` (Admin/Operator), `POST/PUT/DELETE` (Admin). Name pattern `[A-Za-z0-9_-]{1,100}`. Secrets are stored but never returned (`"value":"***"`).

```bash
curl -s -b cookie.jar -X POST "$NP/api/global-variables" -H 'Content-Type: application/json' \
  -d '{ "name":"API_ENDPOINT", "value":"https://api.example.com",
        "isSecret":false, "description":"Upstream API base URL" }'

# Create a secret — the response masks the value
curl -s -b cookie.jar -X POST "$NP/api/global-variables" -H 'Content-Type: application/json' \
  -d '{ "name":"SIGNING_KEY", "value":"-----BEGIN PRIVATE KEY-----...",
        "isSecret":true, "description":null }'
# 201 → { ..., "value":"***", "isSecret":true }
```

## Authentication

| Endpoint | Purpose |
|---|---|
| `POST /api/auth/login` / `logout` / `refresh` | Password/LDAP login and the session lifecycle |
| `POST /api/auth/windows` | Windows Negotiate/Kerberos |
| `GET /api/auth/oidc` / `oidc/callback` | Release-gated OIDC authorization code + PKCE |
| `GET /api/auth/me` / `methods` | Profile / authentication-method discovery |
| `/api/scim/v2/Users` / `Groups` | Release-gated SCIM 2.0 provisioning |
| `/api/scim/v2/ServiceProviderConfig` / `ResourceTypes` / `Schemas` | SCIM 2.0 discovery |

```bash
# Login — the token lands in the httpOnly np_auth cookie (-c stores it)
curl -s -c cookie.jar -X POST "$NP/api/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{ "username":"admin", "password":"s3cret-pass" }'
# Without the header → { "userId":"...", "username":"admin", "role":"Admin" }  (the token is in the cookie only)

# The current identity and the available authentication paths
curl -s -b cookie.jar "$NP/api/auth/me"        # {"id":"...","username":"admin","role":"Admin"}
curl -s            "$NP/api/auth/methods"      # {"local":true,"ldap":false,"windows":false,"windowsEndpoint":null,"oidc":false,"oidcEndpoint":null,"oidcDisplayName":null}
```

Details on server-side sessions, AD SSO Preview, OIDC and refresh: [Authentication & roles](./authentication).

## Audit

`GET /api/audit` (Admin, cursor pagination), `GET /api/audit/export?format=csv|ndjson`.

```bash
# Filters + cursor pagination (take is at most 500)
curl -s -b cookie.jar "$NP/api/audit?action=WORKFLOW_PUBLISHED&since=2026-06-01T00:00:00Z&take=50" | jq
# { "items":[...], "nextCursor":{"timestamp":"...","id":"..."} }  → pass nextCursor as afterTs/afterId

# The next page
curl -s -b cookie.jar "$NP/api/audit?afterTs=2026-06-25T10:00:00Z&afterId=7e3f...&take=50"

# Export (CSV by default; the NDJSON stream also uses camelCase keys)
curl -s -b cookie.jar "$NP/api/audit/export?format=ndjson&since=2026-06-01T00:00:00Z" -o audit.ndjson
```

Audit codes follow `VERB_NOUN`. The complete list: [Audit log](../security/audit-log).

## Triggers & webhooks

| Endpoint | Purpose |
|---|---|
| `POST /api/trigger/{workflowNameOrId}` | The external trigger (`X-Api-Key`; the key scope has to cover the workflow GUID, and an active `manualTrigger` is required) |
| `POST\|GET\|PUT\|DELETE /api/webhooks/{workflow}/{path}` | The webhook (the verb has to match `webhookTrigger.method`) |

```bash
# External trigger — anonymous, but limited to workflow GUIDs by the integration key. Optional Idempotency-Key (24 h TTL, isolated per key principal)
curl -s -X POST "$NP/api/trigger/nightly-reconcile" \
  -H 'X-Api-Key: xyz' \
  -H 'Idempotency-Key: ci-1719100000' \
  -H 'Content-Type: application/json' \
  -d '{ "parameters":{"day":"2026-06-25"}, "timeoutSeconds":null, "debug":false }'
# 202 → ExecutionResponse + Location; a replay → 200 + Idempotent-Replayed: true

# Webhook — X-Webhook-Secret if the node has a secret configured
curl -s -X POST "$NP/api/webhooks/deploy-prod/github-push" \
  -H 'X-Webhook-Secret: whsec-...' \
  -H 'Content-Type: application/json' \
  -d '{"ref":"refs/heads/main","after":"abcd123"}'
# 202 → {"workflowId":"...","executionId":"...","message":"Triggered"}
```

Every webhook rejection path (missing/wrong secret, disabled, path/method mismatch, maintenance window) returns a uniform `404 {"message":"Webhook endpoint not found"}` — no leak of which condition failed.

## Observability & diagnostics

| Endpoint | Purpose |
|---|---|
| `GET /api/observability/config\|query\|query_range\|summary` | Prometheus/OTel queries |
| `GET /api/diagnostics/support-log\|support-log/download\|support-events\|support-events/export` | Diagnostics (Admin) |

```bash
# config is anonymous; query/query_range/summary are Admin/Operator
curl -s "$NP/api/observability/config" | jq
curl -s -b cookie.jar "$NP/api/observability/query?query=up%7Bjob%3D%22nodepilot%22%7D" | jq
curl -s -b cookie.jar "$NP/api/observability/query_range?query=rate(nodepilot_workflows_total%5B5m%5D)&start=1719100000&end=1719103600&step=1m"
curl -s -b cookie.jar "$NP/api/observability/summary" | jq '.panels[] | {key,value}'
```

`query` is limited to 8 KiB, with a metric-name prefix allow-list, and a `__name__` selector is rejected. 503 `{"message":"Prometheus query endpoint is not configured."}` if it is not configured.

## Backup, users, maintenance, folders, settings, database admin

```bash
# Backup — multipart for preview/restore, the passphrase as a form field
curl -s -b cookie.jar "$NP/api/backup/manifest" | jq   # {"sections":[{"section":"Credentials","count":12},...]}

curl -s -b cookie.jar -X POST "$NP/api/backup/export" -H 'Content-Type: application/json' \
  -d '{ "sections":["Credentials","GlobalVariables","Workflows","Settings"],
        "passphrase":"correct-horse-battery-staple" }' -o backup.npbackup

# Restore: file + passphrase + policy (skip|rename|overwrite, overridable per section)
curl -s -b cookie.jar -X POST "$NP/api/backup/restore" \
  -F "file=@backup.npbackup" -F "passphrase=correct-horse-battery-staple" -F "policy=skip,Users=Overwrite"

# Users
curl -s -b cookie.jar -X POST "$NP/api/users" -H 'Content-Type: application/json' \
  -d '{ "username":"alice", "password":"p@ssw0rd!", "role":"Operator" }'   # 201

# Settings — ETag-gated (If-Match required)
ETAG=$(curl -s -b cookie.jar -D - "$NP/api/admin/settings/Smtp" | tr -d '\r' | awk -F': ' '/^ETag:/ {print $2}')
curl -s -b cookie.jar -X PUT "$NP/api/admin/settings/Smtp" \
  -H 'Content-Type: application/json' -H "If-Match: $ETAG" -d @smtp.json
# 428 without If-Match; 412 on a mismatch; 400 from boot validation "would prevent booting"
# In a cluster: PUT /api/admin/settings/Authentication → 409 CLUSTER_CONFIG_AS_CODE_REQUIRED
```

| Area | Endpoints |
|---|---|
| Backup | `GET /api/backup/manifest`, `POST /export`, `POST /{preview\|restore}` (Admin, multipart) |
| Users | `GET/POST /api/users`, `PUT/DELETE /api/users/{id}` (Admin) |
| Maintenance windows | `GET /api/maintenance-windows`, `GET /{id}`, `GET /affecting/{workflowId}`, `POST`, `PUT/DELETE /{id}` |
| Shared folders | `GET/POST /api/shared-workflow-folders`, `PUT/DELETE /{id}`, `POST /{id}/move`, `POST /api/workflows/{workflowId}/move-folder` |
| Folder permissions | `GET/POST /api/shared-workflow-folders/{folderId}/permissions`, `PUT/DELETE /{permissionId}` |
| Settings | `GET /api/admin/settings`, `GET\|PUT /{section}`, `GET /status\|system-info\|effective-sizing`, `POST /test/smtp\|test/llm\|test/ldap` (Admin; an Authentication PUT in a cluster returns 409) |
| Database admin | `GET /api/dbadmin/tables`, `GET\|PATCH\|DELETE /tables/{name}/rows`, `GET /info`, `POST /query` (Admin) |
| Dashboard | `GET /api/stats/dashboard` |
| Activity catalog | `GET /api/activity-catalog` |
| Scheduler | `GET /api/triggers/schedule/next-fires` |
| System | `GET /api/system/host-info` (all roles) |
| AI | `POST /api/ai/generate-script\|generate-workflow` (Admin/Operator), `POST /api/ai/chat` (all roles; applying changes is Admin/Operator), `POST /api/ai/chat/applied` + `GET /api/ai/chat/activity/{workflowId}` (Admin/Operator, folder RBAC) — opt-in, SSE streaming |
| Secrets | `POST /api/secrets/reencrypt` (Admin, no body) |

The shared-folder permission grant body: `{"principalType":"User","principalKey":"<guid>","role":"FolderEditor"}` — roles `FolderViewer|FolderOperator|FolderEditor|FolderAdmin`, `principalType` `User|Group` (`Group` = an AD SID `S-1-5-21-...`).

The maintenance-window create body:

```json
{ "name":"Saturday Patch Reboot", "isEnabled":true, "mode":"Blackout",
  "scopeKind":"Global", "recurrence":"OneTime",
  "oneTimeStartUtc":"2026-06-27T22:00:00Z", "oneTimeEndUtc":"2026-06-28T06:00:00Z",
  "weeklyDaysMask":0, "timeZoneId":"UTC", "targets":null }
```

`Mode`: `Blackout|AllowOnly`. `ScopeKind`: `Global|Folders|Workflows` (with Folders/Workflows, `Targets` has to be non-empty). `Recurrence`: `OneTime|Weekly|Cron` — with `Cron`, `cronExpression` (Quartz syntax with a seconds field, e.g. `0 0 3 ? * SAT`) and `durationMinutes` (1..10080) are mandatory; the window is active for `durationMinutes` on every fire, interpreted in `timeZoneId`.

## AI (opt-in)

`Llm:Enabled=false` by default. 503 `{"code":"LLM_DISABLED",...}` when disabled, and 503 `{"code":"LLM_NO_ACTIVE_PROFILE",...}` when enabled but no LLM profile is selected.

```bash
# Generate a script (with upstream variable context)
# The optional editor context additionally requires includeCurrentScript:true; without that flag
# the server ignores a supplied currentScript, because it can contain passwords or tokens.
curl -s -b cookie.jar -X POST "$NP/api/ai/generate-script" -H 'Content-Type: application/json' \
  -d '{ "prompt":"Write a PowerShell step that checks free disk space",
        "workflowId":"21f1c0d4-...", "stepId":"runScript_1",
        "upstreamVariables":[
          {"stepId":"collectInfo","label":"Collect Info → $hostname",
           "variable":"collectInfo.param.hostname","expression":"{{collectInfo.param.hostname}}","type":"string"}] }'
# 200 → {"script":"Get-PSDrive ...","durationMs":1820,"model":"gpt-4o","totalTokens":505}

# Generate a workflow
curl -s -b cookie.jar -X POST "$NP/api/ai/generate-workflow" -H 'Content-Type: application/json' \
  -d '{ "prompt":"A workflow that checks disk space and emails on low" }'
# 200 → {"definitionJson":"{...}","suggestedName":"Check Disk Space","nodeCount":4,...}
```

## Health

| Endpoint | Purpose |
|---|---|
| `GET /healthz/live` | Liveness |
| `GET /healthz/ready` | Database readiness; an immediate 503 on `armed` or `unavailable`, with no directory dependency |
| `GET /healthz/database` | A database status report for the UI: always 200 with `{status: ok\|armed\|unavailable, sinceUtc, reason}` |
| `GET /healthz/directory` | A separate LDAPS/service-bind status |
| `GET /healthz/leader` | The HA leader probe (fail-closed) |

With the database breaker open, `/api`, new hub transports, the OIDC callback and protected metrics
are terminated before any database or authentication access. These HTTP paths use 503, `Retry-After`
and the same body:

```json
{
  "code": "DATABASE_UNAVAILABLE",
  "message": "...",
  "retryAfterSeconds": 15,
  "reason": "Unreachable",
  "retryable": true
}
```

`reason` is `Unknown`, `Unreachable`, `Wedged` or `RejectedByServer`; the last of those sets
`retryable: false`. A single command timeout uses the same contract with `code: DATABASE_TIMEOUT`, a
five-second retry interval and no `reason`. On an already established hub, methods return the same
codes as SignalR errors.

```bash
curl -s "$NP/healthz/live"  ; echo
curl -s "$NP/healthz/ready" ; echo
curl -s "$NP/healthz/database"; echo
curl -s "$NP/healthz/directory"; echo
curl -s "$NP/healthz/leader"; echo   # the HA leader probe, anonymous
```

All `healthz` endpoints are `AllowAnonymous`.
