# API-Endpoints

ASP.NET Core Web API, Port 5000 (Dev). Realtime via SignalR `/hubs/execution`. Alle mutierenden Endpunkte (außer `disable`) liefern `423 Locked`, wenn der Caller nicht Lock-Owner ist.

> **Wire-Format:** JSON-Property-Keys sind **camelCase** (ASP.NET Core Web-Default, in `Program.cs` nicht überschrieben). Enum-Werte werden als .NET-Name (PascalCase-String) serialisiert — z.B. `"role":"Admin"`, `"status":"Succeeded"`. Auth läuft über das httpOnly `np_auth`-Cookie — `curl` braucht `-c cookie.jar -b cookie.jar` (siehe [Authentifizierung](./authentication)). Examples unten nutzen `$NP` = `http://localhost:5000`. Request-Bodies sind case-insensitiv, aber die gezeigten Shapes spiegeln das echte Wire-Format.

## Workflows

| Endpoint | Zweck |
|---|---|
| `GET /api/workflows` | Liste (Array, 500-Row-Cap, folder-RBAC-gefiltert) |
| `POST /api/workflows` | Neu (Admin/Op) — 201 |
| `PUT /api/workflows/{id}` | Update — 204 (423 ohne Lock, 409 bei Version-Konflikt) |
| `DELETE /api/workflows/{id}` | Löschen (Admin) — 204 |
| `POST /{id}/execute` | Startet Lauf — 202 + ExecutionId |
| `POST /{id}/duplicate` | Duplizieren — 201 (Copy born disabled) |
| `POST /{id}/enable` / `disable` | Kill-Switch — 204 |
| `POST /{id}/cancel-all` | Cancelt alle Running-Executions — 200 |
| `POST /{id}/lock` / `unlock` | Edit-Lock — 200 |
| `POST /{id}/publish` | Save + Enable + Unlock (atomar) — 200 |
| `POST /{id}/force-unlock` | Admin-only, bricht fremden Lock — 200 |

```bash
# Liste + einzelner Workflow
curl -s -b cookie.jar "$NP/api/workflows" | jq '.[0] | {id,name,isEnabled,version,folderPath}'
curl -s -b cookie.jar "$NP/api/workflows/by-name/deploy-prod" | jq '{id,name,isEnabled}'

# Neu anlegen (definitionJson = JSON-Objekt als String, ≤5 MiB, Tiefe ≤64)
curl -s -b cookie.jar -X POST "$NP/api/workflows" \
  -H 'Content-Type: application/json' \
  -d '{ "name": "Deploy App",
        "description": "Deploys the web app",
        "definitionJson": "{\"nodes\":[],\"edges\":[]}",
        "folderId": null }'

# Update (erfordert Lock)
curl -s -b cookie.jar -X PUT "$NP/api/workflows/21f1c0d4-..." \
  -H 'Content-Type: application/json' \
  -d '{ "name": "Deploy App v2", "description": "updated",
        "definitionJson": "{\"nodes\":[...],\"edges\":[...]}" }' -i   # 204

# Publish (atomar), Duplicate, Cancel-All
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../publish" \
  -H 'Content-Type: application/json' \
  -d '{ "name": "Deploy App", "description": null,
        "definitionJson": "{\"nodes\":[...],\"edges\":[...]}" }'
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../duplicate"
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../cancel-all"   # {"total":3,"signalled":2}
```

409-Lock-Konflikt-Body: `{"message":"Workflow is already locked by alice.","lockedByUserName":"alice","lockedAt":"..."}`. 409-Version-Konflikt: `{"code":"workflow_version_conflict","currentVersion":4}`.

## Versionen & Contract

| Endpoint | Zweck |
|---|---|
| `GET /{id}/versions` | Versions-History |
| `GET /{id}/versions/{v}` | Spezifische Version |
| `POST /{id}/rollback/{v}` | Rollback — Body `{"reason": "..."}` |
| `GET /{id}/contract` | Input/Output-Contract |
| `GET /by-name/{name}/contract` | By-name-Lookup (exact-case gewinnt, sonst case-insensitive; mehrdeutig → 409) |

```bash
curl -s -b cookie.jar "$NP/api/workflows/21f1c0d4-.../versions" | jq '.[0]'
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../rollback/12" \
  -H 'Content-Type: application/json' -d '{"reason":"revert bad config"}'

# Contract: Inputs aus manualTrigger.parameters + Outputs aus returnData + System
curl -s -b cookie.jar "$NP/api/workflows/by-name/deploy-prod/contract" | jq
# { "workflowName":"Deploy App", "hasManualTrigger":true,
#   "inputs":[{"name":"version","type":"string","required":true,...}],
#   "outputs":[{"name":"__executionId","source":"system"},{"name":"deployResult","source":"single"}] }
```

## Step-Test & Coverage

| Endpoint | Zweck |
|---|---|
| `POST /{id}/steps/{stepId}/test` | Einzelner Step-Test |
| `GET .../test-context` | Test-Kontext (`?executionId=`) |
| `GET .../test-context/runs` | Verfügbare Runs |
| `GET /{id}/coverage?windowDays=N` | Step-Coverage |
| `GET /{id}/step-health` | Step-Health |
| `GET /{id}/step-stats?windowDays=N` | Step-Statistiken |

```bash
# Step mit Mock-Variablen + Config-Override testen
curl -s -b cookie.jar -X POST "$NP/api/workflows/21f1c0d4-.../steps/runHealth/test" \
  -H 'Content-Type: application/json' \
  -d '{ "mockVariables": { "checkDisk.output": "7", "checkDisk.param.freeGb": "7" },
        "configOverride": { "script": "Get-Process", "timeoutSeconds": 30 } }' | jq
# { "success":true, "output":"COMPUTERNAME=SRV01",
#   "outputParameters":{"computerName":"SRV01"}, "durationMs":412.3 }

curl -s -b cookie.jar "$NP/api/workflows/21f1c0d4-.../coverage?windowDays=7" | jq
```

## Import/Export

| Endpoint | Zweck |
|---|---|
| `GET /export` | Bulk-Export |
| `GET /{id}/export` | Einzel-Export |
| `POST /import?folderId={guid}` | Import (Namenskollision → Suffix `" (Imported 2)"`; `folderId` optional, fehlt → Root, RBAC = Edit auf dem Ziel-Folder) |
| `POST /import-scorch?folderId={guid}` | SCOrch-Import (`.ois_export`; gleiches Folder-Targeting) |

```bash
curl -s -b cookie.jar "$NP/api/workflows/21f1c0d4-.../export" -o deploy.envelope.json
curl -s -b cookie.jar -X POST "$NP/api/workflows/import" \
  -H 'Content-Type: application/json' --data-binary @deploy.envelope.json
```

Envelope-Typ: `nodepilot-workflow-export/v1`. Secrets werden hier redigiert (`***`) — Teilen-Artefakt, kein DR.

## Executions

| Endpoint | Zweck |
|---|---|
| `GET /api/executions` | Liste (`?workflowId=&activeOnly=&terminalOnly=`, 500-Cap) |
| `GET /api/executions/{id}` | Einzelne Execution |
| `GET /api/executions/{id}/steps` | Steps einer Execution |
| `POST /api/executions/{id}/cancel` / `retry` / `resume` | Einzelner Lauf |

```bash
curl -s -b cookie.jar "$NP/api/executions?workflowId=21f1c0d4-...&activeOnly=true" | jq '.[0]'
curl -s -b cookie.jar "$NP/api/executions/7e3f..." | jq '{status,startedAt,completedAt,triggeredBy}'
curl -s -b cookie.jar "$NP/api/executions/7e3f.../steps" | jq '.[] | {stepId,status,durationMs}'
curl -s -b cookie.jar -X POST "$NP/api/executions/7e3f.../cancel" -i   # 204
curl -s -b cookie.jar -X POST "$NP/api/executions/7e3f.../retry"  -i   # 202 + Location
```

`resume`-Body (Debug-Pause): `{"stepId":"runHealth","mode":"continue|stepOver|stop","overrides":{"vars.targetHost":"srv02"}}` → 204.

## Machines & Credentials

| Endpoint | Zweck |
|---|---|
| `GET/POST/PUT/DELETE /api/machines` | Maschinen (POST/PUT Admin/Op, DELETE Admin) |
| `POST /{id}/test` | Maschinen-Verbindungstest — Body `{"credentialId": null}` |
| `GET/POST/PUT/DELETE /api/credentials` | Credentials (DELETE Admin) |

```bash
# Maschine anlegen
curl -s -b cookie.jar -X POST "$NP/api/machines" -H 'Content-Type: application/json' \
  -d '{ "name":"SRV-PROD-01", "hostname":"srv-prod-01.contoso.com",
        "winRmPort":5985, "useSsl":false,
        "defaultCredentialId":"4c2a-...", "tags":"prod;web" }'

# Verbindungstest (credentialId null = Machine-Default)
curl -s -b cookie.jar -X POST "$NP/api/machines/9f1a.../test" \
  -H 'Content-Type: application/json' -d '{"credentialId":null}'
# { "success":true, "computerName":"SRV-PROD-01", "credentialUsed":"svc-winrm" }

# Credential (password min 8, wird nie zurückgegeben)
curl -s -b cookie.jar -X POST "$NP/api/credentials" -H 'Content-Type: application/json' \
  -d '{ "name":"svc-winrm", "username":"CONTOSO\\svc-winrm", "password":"p@ssw0rd!", "domain":null }'
# 201 → { "id":"...", "name":"svc-winrm", "username":"CONTOSO\\svc-winrm", "domain":null }
```

## Global Variables

`GET /api/global-variables` (Admin/Op), `POST/PUT/DELETE` (Admin). Name-Pattern `[A-Za-z0-9_-]{1,100}`. Secrets werden gespeichert, aber nie zurückgegeben (`"value":"***"`).

```bash
curl -s -b cookie.jar -X POST "$NP/api/global-variables" -H 'Content-Type: application/json' \
  -d '{ "name":"API_ENDPOINT", "value":"https://api.example.com",
        "isSecret":false, "description":"Upstream API base URL" }'

# Secret anlegen — Response maskiert den Wert
curl -s -b cookie.jar -X POST "$NP/api/global-variables" -H 'Content-Type: application/json' \
  -d '{ "name":"SIGNING_KEY", "value":"-----BEGIN PRIVATE KEY-----...",
        "isSecret":true, "description":null }'
# 201 → { ..., "value":"***", "isSecret":true }
```

## Auth

| Endpoint | Zweck |
|---|---|
| `POST /api/auth/login` / `logout` / `refresh` | Passwort-/LDAP-Login und Session-Lifecycle |
| `POST /api/auth/windows` | Windows Negotiate/Kerberos |
| `GET /api/auth/oidc` / `oidc/callback` | release-gated OIDC Authorization Code + PKCE |
| `GET /api/auth/me` / `methods` | Profil / Auth-Methoden-Discovery |
| `/api/scim/v2/Users` / `Groups` | release-gated SCIM-2.0-Provisionierung |
| `/api/scim/v2/ServiceProviderConfig` / `ResourceTypes` / `Schemas` | SCIM-2.0-Discovery |

```bash
# Login — Token landet im httpOnly np_auth-Cookie (-c speichert es)
curl -s -c cookie.jar -X POST "$NP/api/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{ "username":"admin", "password":"s3cret-pass" }'
# Ohne Header → { "userId":"...", "username":"admin", "role":"Admin" }  (Token nur im Cookie)

# Wer bin ich? Welche Auth-Pfade hat der Server?
curl -s -b cookie.jar "$NP/api/auth/me"        # {"id":"...","username":"admin","role":"Admin"}
curl -s            "$NP/api/auth/methods"      # {"local":true,"ldap":false,"windows":false,"windowsEndpoint":null,"oidc":false,"oidcEndpoint":null,"oidcDisplayName":null}
```

Details zu serverseitigen Sessions, AD SSO Preview, OIDC und Refresh: [Authentifizierung & Rollen](./authentication).

## Audit

`GET /api/audit` (Admin, Cursor-Pagination), `GET /api/audit/export?format=csv|ndjson`.

```bash
# Filter + Cursor-Pagination (take max 500)
curl -s -b cookie.jar "$NP/api/audit?action=WORKFLOW_PUBLISHED&since=2026-06-01T00:00:00Z&take=50" | jq
# { "items":[...], "nextCursor":{"timestamp":"...","id":"..."} }  → nextCursor als afterTs/afterId reichen

# Nächste Seite
curl -s -b cookie.jar "$NP/api/audit?afterTs=2026-06-25T10:00:00Z&afterId=7e3f...&take=50"

# Export (CSV Default; NDJSON-Stream ist ebenfalls camelCase-keys)
curl -s -b cookie.jar "$NP/api/audit/export?format=ndjson&since=2026-06-01T00:00:00Z" -o audit.ndjson
```

Audit-Codes folgen `VERB_NOMEN`. Vollständige Liste: [Audit-Log](../security/audit-log).

## Trigger & Webhooks

| Endpoint | Zweck |
|---|---|
| `POST /api/trigger/{workflowNameOrId}` | External Trigger (`X-Api-Key` required) |
| `POST\|GET\|PUT\|DELETE /api/webhooks/{workflow}/{path}` | Webhook (Verb muss `webhookTrigger.method` matchen) |

```bash
# External Trigger — anonym, nur API-Key. Optional Idempotency-Key (24h TTL)
curl -s -X POST "$NP/api/trigger/nightly-reconcile" \
  -H 'X-Api-Key: xyz' \
  -H 'Idempotency-Key: ci-1719100000' \
  -H 'Content-Type: application/json' \
  -d '{ "parameters":{"day":"2026-06-25"}, "timeoutSeconds":null, "debug":false }'
# 202 → ExecutionResponse + Location; Replay → 200 + Idempotent-Replayed: true

# Webhook — X-Webhook-Secret wenn Node einen secret gesetzt hat
curl -s -X POST "$NP/api/webhooks/deploy-prod/github-push" \
  -H 'X-Webhook-Secret: whsec-...' \
  -H 'Content-Type: application/json' \
  -d '{"ref":"refs/heads/main","after":"abcd123"}'
# 202 → {"workflowId":"...","executionId":"...","message":"Triggered"}
```

Alle Webhook-Reject-Pfade (missing/wrong secret, disabled, path/method-Mismatch, Maintenance-Window) liefern uniform `404 {"message":"Webhook endpoint not found"}` — kein Leak, welche Bedingung scheiterte.

## Observability & Diagnostics

| Endpoint | Zweck |
|---|---|
| `GET /api/observability/config\|query\|query_range\|summary` | Prometheus/OTel-Queries |
| `GET /api/diagnostics/support-log\|support-log/download\|support-events\|support-events/export` | Diagnostics (Admin) |

```bash
# config anonym; query/query_range/summary Admin/Op
curl -s "$NP/api/observability/config" | jq
curl -s -b cookie.jar "$NP/api/observability/query?query=up%7Bjob%3D%22nodepilot%22%7D" | jq
curl -s -b cookie.jar "$NP/api/observability/query_range?query=rate(nodepilot_workflows_total%5B5m%5D)&start=1719100000&end=1719103600&step=1m"
curl -s -b cookie.jar "$NP/api/observability/summary" | jq '.panels[] | {key,value}'
```

`query` ≤8 KiB, Metric-Name-Prefix-Allowlist, `__name__`-Selector rejected. 503 `{"message":"Prometheus query endpoint is not configured."}` falls nicht konfiguriert.

## Backup, Users, Maintenance, Folders, Settings, DB-Admin

```bash
# Backup — multipart für preview/restore, Passphrase als Form-Field
curl -s -b cookie.jar "$NP/api/backup/manifest" | jq   # {"sections":[{"section":"Credentials","count":12},...]}

curl -s -b cookie.jar -X POST "$NP/api/backup/export" -H 'Content-Type: application/json' \
  -d '{ "sections":["Credentials","GlobalVariables","Workflows","Settings"],
        "passphrase":"correct-horse-battery-staple" }' -o backup.npbackup

# Restore: file + passphrase + policy (skip|rename|overwrite, per-section überschreibbar)
curl -s -b cookie.jar -X POST "$NP/api/backup/restore" \
  -F "file=@backup.npbackup" -F "passphrase=correct-horse-battery-staple" -F "policy=skip,Users=Overwrite"

# Users
curl -s -b cookie.jar -X POST "$NP/api/users" -H 'Content-Type: application/json' \
  -d '{ "username":"alice", "password":"p@ssw0rd!", "role":"Operator" }'   # 201

# Settings — ETag-gated (If-Match required)
ETAG=$(curl -s -b cookie.jar -D - "$NP/api/admin/settings/Smtp" | tr -d '\r' | awk -F': ' '/^ETag:/ {print $2}')
curl -s -b cookie.jar -X PUT "$NP/api/admin/settings/Smtp" \
  -H 'Content-Type: application/json' -H "If-Match: $ETAG" -d @smtp.json
# 428 ohne If-Match; 412 bei Mismatch; 400 boot-validation "would prevent booting"
# Im Cluster: PUT /api/admin/settings/Authentication → 409 CLUSTER_CONFIG_AS_CODE_REQUIRED
```

| Bereich | Endpoints |
|---|---|
| Backup | `GET /api/backup/manifest`, `POST /export`, `POST /{preview\|restore}` (Admin, multipart) |
| Users | `GET/POST /api/users`, `PUT/DELETE /api/users/{id}` (Admin) |
| Maintenance Windows | `GET /api/maintenance-windows`, `GET /{id}`, `GET /affecting/{workflowId}`, `POST`, `PUT/DELETE /{id}` |
| Shared Folders | `GET/POST /api/shared-workflow-folders`, `PUT/DELETE /{id}`, `POST /{id}/move`, `POST /api/workflows/{workflowId}/move-folder` |
| Folder-Permissions | `GET/POST /api/shared-workflow-folders/{folderId}/permissions`, `PUT/DELETE /{permissionId}` |
| Settings | `GET /api/admin/settings`, `GET\|PUT /{section}`, `GET /status\|system-info`, `POST /test/smtp\|test/llm\|test/ldap` (Admin; Authentication-PUT im Cluster = 409) |
| DB-Admin | `GET /api/dbadmin/tables`, `GET\|PATCH\|DELETE /tables/{name}/rows`, `GET /info`, `POST /query` (Admin) |
| Dashboard | `GET /api/stats/dashboard` |
| Activity-Catalog | `GET /api/activity-catalog` |
| Scheduler | `GET /api/triggers/schedule/next-fires` |
| System | `GET /api/system/host-info` (alle Rollen) |
| AI | `POST /api/ai/generate-script\|generate-workflow` (Admin/Op), `POST /api/ai/chat` (all roles; applying changes Admin/Op), `POST /api/ai/chat/applied` + `GET /api/ai/chat/activity/{workflowId}` (Admin/Op, folder-RBAC) — opt-in, SSE streaming |
| Secrets | `POST /api/secrets/reencrypt` (Admin, kein Body) |

Shared-Folder-Permission-Grant-Body: `{"principalType":"User","principalKey":"<guid>","role":"FolderEditor"}` — Rollen `FolderViewer|FolderOperator|FolderEditor|FolderAdmin`, `principalType` `User|Group` (`Group` = AD-SID `S-1-5-21-...`).

Maintenance-Window-Create-Body:

```json
{ "name":"Saturday Patch Reboot", "isEnabled":true, "mode":"Blackout",
  "scopeKind":"Global", "recurrence":"OneTime",
  "oneTimeStartUtc":"2026-06-27T22:00:00Z", "oneTimeEndUtc":"2026-06-28T06:00:00Z",
  "weeklyDaysMask":0, "timeZoneId":"UTC", "targets":null }
```

`Mode`: `Blackout|AllowOnly`. `ScopeKind`: `Global|Folders|Workflows` (bei Folders/Workflows muss `Targets` nicht-leer sein). `Recurrence`: `OneTime|Weekly|Cron` — bei `Cron` sind `cronExpression` (Quartz-Syntax mit Sekundenfeld, z. B. `0 0 3 ? * SAT`) und `durationMinutes` (1..10080) Pflicht; das Fenster ist bei jedem Fire für `durationMinutes` aktiv, interpretiert in `timeZoneId`.

## AI (opt-in)

`Llm:Enabled=false` default. 503 `{"code":"LLM_DISABLED",...}` wenn deaktiviert.

```bash
# Script generieren (mit Upstream-Variablen-Kontext)
curl -s -b cookie.jar -X POST "$NP/api/ai/generate-script" -H 'Content-Type: application/json' \
  -d '{ "prompt":"Write a PowerShell step that checks free disk space",
        "workflowId":"21f1c0d4-...", "stepId":"runScript_1",
        "upstreamVariables":[
          {"stepId":"collectInfo","label":"Collect Info → $hostname",
           "variable":"collectInfo.param.hostname","expression":"{{collectInfo.param.hostname}}","type":"string"}] }'
# 200 → {"script":"Get-PSDrive ...","durationMs":1820,"model":"gpt-4o","totalTokens":505}

# Workflow generieren
curl -s -b cookie.jar -X POST "$NP/api/ai/generate-workflow" -H 'Content-Type: application/json' \
  -d '{ "prompt":"A workflow that checks disk space and emails on low" }'
# 200 → {"definitionJson":"{...}","suggestedName":"Check Disk Space","nodeCount":4,...}
```

## Health

| Endpoint | Zweck |
|---|---|
| `GET /healthz/live` | Liveness |
| `GET /healthz/ready` | DB-Readiness; bewusst ohne Directory-Abhängigkeit |
| `GET /healthz/directory` | separater LDAPS-/Service-Bind-Status |
| `GET /healthz/leader` | HA-Leader-Probe (fail-closed) |

```bash
curl -s "$NP/healthz/live"  ; echo
curl -s "$NP/healthz/ready" ; echo
curl -s "$NP/healthz/directory"; echo
curl -s "$NP/healthz/leader"; echo   # HA-Leader-Probe, Anonymous
```

Alle `healthz`-Endpoints sind `AllowAnonymous`.
