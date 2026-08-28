# CLI (`np`)

`np` is the command-line tool for administration and operations. It accesses NodePilot exclusively through the REST API; there is no direct database access.

## Installation

`np` is **not** a .NET global tool — `PackAsTool` cannot cope with the inherited `net10.0-windows` TFM (NETSDK1146). Publish it instead and put the folder on the `PATH`:

```powershell
# Both installers already ship np: <install>\tools\np\np.exe, and with the server setup
# additionally on the machine PATH. Build it yourself only from a source checkout:
dotnet publish src/NodePilot.Cli -c Release -o C:\Tools\NodePilot-Cli
$env:PATH += ';C:\Tools\NodePilot-Cli'   # permanently through System properties → Environment variables

np auth login --server https://nodepilot.example.com
```

> **Conventions in the examples:** `<ARG>` = required, `[ARG]` = optional. `<ID-OR-NAME>` takes a workflow GUID **or** the workflow name (exact case wins, otherwise case-insensitive; ambiguous names → an error naming the GUIDs). Destructive commands (`delete`, `force-unlock`, `restore`, `reencrypt`, `db --write`) prompt interactively — in CI/pipes set `--yes` or redirect stdin. `--file -` reads from stdin everywhere.

## Command areas

| Area | Subcommands |
|---|---|
| `auth` | login / logout / whoami / **methods** (Local/LDAP/Windows; OIDC is browser-based and discoverable through `/api/auth/methods`) |
| `workflow` | list/get/run/lock/unlock/publish/enable/disable/cancel-all/duplicate/delete/export/import/versions/rollback/force-unlock/import-scorch/stats/**contract**/**coverage**/**trigger**/**step-test**/**move-folder** |
| `exec` | list/get/steps/cancel/retry/watch/resume/paused-steps |
| `machine` / `credential` / `globals` | list/create/update/delete (+ globals export/import) |
| `user` | CRUD |
| `shared-folder` | Org RBAC: list/create/rename/move/delete (`--recursive` deletes contents too)/permissions/grant/revoke |
| `maintenance` | Maintenance windows: list/get/create/update/delete |
| `audit` | `audit list` |
| `alerting` | list/get/create/update/delete/test-fire/deliveries (create/edit/delete/test-fire are admin only — see [Alerting](alerting)) |
| `system-alert` | catalog/list/get/create/update/enable/disable/delete/test-fire — system alert policies (ADR 0008); create/update read a `SaveSystemAlertPolicyRequest` JSON through `--file` |
| `health` | The health check (live/ready/leader) |
| `cron` | `cron next` |
| `db` | info/query (read by default, `--write` opt-in) |
| `dashboard` | Statistics |
| `operations` | graph (a live-ops snapshot: workflows, the call graph, running and recently finished executions; RBAC folder-scoped; `--window 30\|60`) |
| `observability` | summary/**query**/**query-range** |
| `settings` | status/system-info/effective-sizing/get/put/test smtp\|llm |
| `secrets` | **reencrypt** |
| `backup` | manifest/export/preview/restore |
| `config` | get/set |

## Global flags

Available on every command — from the base settings class `GlobalOptions`:

```
--server <URL>        Override the server URL for this call
--profile <NAME>      A named connection profile (default: 'default')
-o|--output <FORMAT>  table (the TTY default) | json | yaml
--no-color            Turn off coloured output (automatically off when stdout is redirected)
-v|--verbose          An HTTP request/response trace on stderr
```

```bash
np workflow list --profile prod -o json --no-color -v
np exec get 7e3f... --server https://np.internal:8443 -o yaml
```

**Exit codes:** `0` ok, `1` generic, `2` run failed/cancelled, `3` authentication required, `4` permission denied.

## auth

`np auth login` is interactive (it asks for the user name and password if they are not supplied through flags). Flags: `--username`, `--password` (a literal — avoid it in scripts), `--password-stdin` (one line from stdin), `--setup-token <T>` (bootstrapping the first admin).

```bash
# Interactive (asks for user + password)
np auth login --server https://np.internal:8443

# Fully scripted, with the password through stdin
echo "S3cret!" | np auth login --username admin --password-stdin --server https://np.internal:8443

# A fresh server: bootstrap the first admin (admin-setup.token)
np auth login --username admin --password-stdin \
  --setup-token "$(cat admin-setup.token)" --server https://np.internal:8443

# Discovery (anonymous, no session needed) + a profile check
np auth methods --server https://np.internal:8443
np auth whoami -o json
np auth logout
```

`np auth login` uses the password endpoint and therefore covers both local and LDAP sign-in. Windows Negotiate and OIDC are browser flows; the OIDC metadata is fully available through `GET /api/auth/methods` but is not yet surfaced by the current CLI DTO.

## workflow

### run / trigger

`run` starts a run as the authenticated user; `trigger` is session-independent and limited through `X-Api-Key` to the workflow GUIDs listed in the key's scope. The workflow additionally needs an active `manualTrigger`.

`run` flags: `-p|--params <k=v>` (repeatable; only the first `=` splits), `--wait` (poll until terminal), `--follow` (live step events through SignalR), `--debug`, `--timeout <s>`.

```bash
np workflow run deploy-prod -p environment=staging -p revision=abcd123
np workflow run deploy-prod -p env=prod --debug --follow
np workflow run 21f1c0d4-... -p env=prod --wait --timeout 600 -o json
```

`trigger` flags: `--api-key <K>`, `--api-key-stdin`, the environment variable `NODEPILOT_TRIGGER_API_KEY` (preferred for scripts), `-p|--params`, `--idempotency-key <K>`, `--timeout`, `--wait` (which requires a JWT session, because `/api/executions/{id}` is JWT-only).

```bash
# The API key from stdin, an idempotency key against CI replays
np workflow trigger nightly-reconcile --api-key-stdin \
  --idempotency-key "ci-$(date +%s)" -p day=2026-06-25 --wait < api.key

# The key through the environment (safer than --api-key)
NODEPILOT_TRIGGER_API_KEY=xyz np workflow trigger hourly-report -p window=1h --timeout 300
```

### lock / unlock / publish / enable / disable / cancel-all

All take `<ID-OR-NAME>`, with no extra flags (except `publish`).

```bash
np workflow lock deploy-prod
np workflow unlock deploy-prod
np workflow enable deploy-prod     # 423 if locked
np workflow disable deploy-prod    # ignores locks (the incident kill switch)
np workflow cancel-all deploy-prod
np workflow concurrency-limit deploy-prod -m 5   # at most 5 at once; further runs queue
np workflow concurrency-limit deploy-prod -m none  # back to unlimited
np workflow duplicate deploy-prod
np workflow force-unlock deploy-prod   # Admin; prompts
```

`publish` — atomically save + enable + unlock. Flags: `-f|--file <PATH>` (required, the JSON definition), `--name`, `--description`.

```bash
np workflow publish deploy-prod -f ./deploy-prod.def.json
np workflow publish deploy-prod -f ./deploy-prod.def.json --description "bump revision" -o json
```

### versions / rollback

```bash
np workflow versions deploy-prod -o json
np workflow version deploy-prod 12 -o yaml          # one version in full
np workflow rollback deploy-prod 12 --reason "revert bad config"
```

### contract / coverage / stats

```bash
np workflow contract deploy-prod -o json            # inputs (manualTrigger) + outputs (returnData + system)
np workflow coverage deploy-prod --window-days 7    # default 30, maximum 365
np workflow stats deploy-prod --window-days 30      # 1..365, per-step duration + failure rate
```

### step-test / step-test-context

`step-test` positionals: `<WORKFLOW> <STEP-ID>`. Flags: `-m|--mock <stepName.field=value>` (repeatable), `--config-file <PATH>` (a JSON override for `data.config`; `-` = stdin).

```bash
np workflow step-test deploy-prod runHealth -m checkDisk.output=7 -m checkDisk.param.freeGb=7
np workflow step-test deploy-prod runHealth --config-file ./health-override.json
np workflow step-test-context deploy-prod runHealth --list-runs --limit 20
np workflow step-test-context deploy-prod runHealth --execution 7e3f...
```

### export / import / import-scorch

```bash
np workflow export deploy-prod --out ./deploy-prod.envelope.json
np workflow export --all --out ./all-workflows.json -o json
np workflow import -f ./deploy-prod.envelope.json
cat all.json | np workflow import -f -               # a name collision adds a suffix
np workflow import -f ./deploy.json --target-folder 8a2f...        # the target folder (absent → root)
np workflow import-scorch -f ./scorch-export.ois_export   # SCOrch .ois_export XML
np workflow import-scorch -f ./scorch.ois_export --target-folder 8a2f...

# Safe CI flow: the import stays disabled, read its id from JSON, then activate deliberately
workflow_id="$(np workflow import -f ./deploy.json -o json | jq -r '.workflows[0].id')"
np workflow enable "$workflow_id"
```

`--target-folder <GUID>` requires edit permission on the target folder (RBAC). JSON and SCOrch
imports always create disabled workflows. With `-o json`, stdout contains the complete import report,
including every created id; logs and warnings remain on stderr. For a bulk import, activate each
intended id from `.workflows[]` separately with `np workflow enable <ID>`.

### move-folder

```bash
np workflow move-folder deploy-prod --target-folder 8a2f...
```

## exec

The positional `<EXECUTION-ID>` (a GUID) applies to get/steps/cancel/retry/watch/paused-steps.

```bash
np exec list -w deploy-prod --limit 20 -o table      # --workflow, --limit (server cap 500)
np exec get 7e3f...
np exec steps 7e3f...
np exec cancel 7e3f...
np exec retry 7e3f...                                 # terminal runs only → 202 + Location
np exec watch 7e3f...
np exec watch 7e3f... --no-signalr                    # force the polling fallback
np exec paused-steps 7e3f...
```

`resume` (debug-paused): `--step <ID>` (required), `--mode <continue|stepOver|stop>` (default `continue`), `--override <k=v>` (repeatable).

```bash
np exec resume 7e3f... --step runHealth --mode continue
np exec resume 7e3f... --step runHealth --mode stepOver --override freeGb=8 --override host=svr01
np exec resume 7e3f... --step runHealth --mode stop
```

## machine

The machine body comes from flags, not `--file`. `create`/`update` flags: `--name`, `--hostname`, `--port` (default 5985), `--ssl`, `--credential <GUID>`, `--tags <CSV>`. `update` is a client-side patch (fetch + merge, because the server wants a full body).

```bash
np machine create --name srv01 --hostname srv01.internal --credential 4c2a... --tags prod,web
np machine update 9f1a... --port 5986 --ssl --tags prod,web,decommissioned
np machine list -o json
np machine get 9f1a...
np machine test 9f1a...
np machine test 9f1a... --credential 4c2a...   # override the credential for this probe
np machine delete 9f1a...                       # prompts
```

## credential

Credentials come from flags. `create`: `--name`, `--username`, `--password` (minimum 8 characters; prefer `--password-stdin`), `--password-stdin`, `--domain`, `--expires <ISO-DATE>` (an optional expiry date — it feeds the `CredentialExpiring` alert signal). `update`: the same flags; an omitted password means unchanged, and `--no-expires` clears a configured expiry date.

```bash
echo 'Sup3rSecret!' | np credential create --name svc-winrm \
  --username svc-winrm@DOM --password-stdin --domain DOM --expires 2026-12-31
np credential list
np credential get 4c2a...
np credential update 4c2a... --name svc-winrm-v2 --password-stdin < newpw.txt   # rotation
np credential update 4c2a... --no-expires                                       # remove the expiry date
np credential delete 4c2a...
```

> `credential create` has **no** `--description` flag — only name/username/password/password-stdin/domain/expires.

## globals

`create`: `--name` (pattern `[A-Za-z0-9_-]{1,100}`, no dots or spaces), `--value`/`--value-stdin`, `--secret` (DPAPI, masked on read), `--description`, `--folder` (a folder ID, path or name; absent → root).

Folders are purely organizational — they change **nothing** about how `{{globals.NAME}}` resolves (names stay globally unique).

```bash
np globals create --name adminEmail --value ops@internal --description "Default alert recipient"
echo '-----BEGIN PRIVATE KEY-----...' | np globals create --name signingKey --value-stdin --secret --folder /Secrets
np globals list -o json
np globals update 3b8c... --value new-email@internal
np globals update 3b8c... --no-secret
np globals update 3b8c... --folder /Environment/Prod       # moves the variable
np globals delete 3b8c...
np globals export --file ./globals.json                   # secrets as ***
np globals import -f ./globals.json --upsert
np globals import -f - --dry-run < globals.json

# The folder tree (admin)
np globals folder list
np globals folder create --name Databases
np globals folder create --name Prod --parent /Environment
np globals folder rename <folder-id> --name Renamed
np globals folder move <folder-id> --parent /Environment    # reparent (cycle and depth protected)
np globals folder delete <folder-id>                        # only when empty
np globals folder delete <folder-id> --recursive --yes      # with sub-folders and variables
np globals move-folder <var-id> --folder /Databases         # a variable → a folder
```

## user · shared-folder · maintenance

### user (admin)

```bash
np user list -o json
echo 'TempPw1!' | np user create --username jane.doe --password-stdin --role Operator
np user update 5a1c... --role Admin --active true
np user update 5a1c... --password-stdin < newpw.txt
np user delete 5a1c...
```

### shared-folder (the org RBAC tree)

```bash
np shared-folder list
np shared-folder create --name "Prod Workflows" --parent 8a2f...
np shared-folder rename 8a2f... --name "Prod Flows"
np shared-folder move 8a2f... --parent 9b1c...
np shared-folder move 8a2f... --to-root
np shared-folder delete 8a2f...                          # has to be empty
np shared-folder delete 8a2f... --recursive --yes         # with sub-folders and workflows
np shared-folder permissions 8a2f...
np shared-folder grant 8a2f... --principal-type User   --principal-key 5a1c...            --role FolderEditor
np shared-folder grant 8a2f... --principal-type Group  --principal-key S-1-5-21-100-200-300 --role FolderOperator
np shared-folder revoke 8a2f... 7e3f...                  # <FOLDER> <PERMISSION-ID>
```

Roles: `FolderViewer | FolderOperator | FolderEditor | FolderAdmin`. `--principal-type`: `User | Group` (`Role` is reserved for V1).

### maintenance (admin)

`create`/`update` flags: `--name`, `--description`, `--enabled`/`--disabled`, `--mode <Blackout|AllowOnly>`, `--scope <Global|Folders|Workflows>`, `--recurrence <OneTime|Weekly|Cron>`, `--tz <TZID>`, `--one-time-start/--one-time-end <ISO>`, `--days <CSV>` (Mon..Sat), `--start/--end <HH:MM>`, `--cron <EXPR>` (Quartz, with a seconds field), `--duration-minutes <N>`, `--folder`/`--workflow` (repeatable).

```bash
np maintenance list
# A weekly change-freeze window Sat/Sun 02:00–04:00 local time
np maintenance create --name "Change freeze" --mode Blackout --scope Global \
  --recurrence Weekly --days Sat,Sun --start 02:00 --end 04:00 --tz "W. Europe Standard Time"
# A one-time deploy window in which only one workflow may run
np maintenance create --name "Deploy window" --mode AllowOnly --scope Workflows \
  --workflow 21f1c0d4-... --recurrence OneTime \
  --one-time-start 2026-06-25T20:00:00Z --one-time-end 2026-06-25T22:00:00Z
# A cron window: every Saturday at 03:00 local time, open for 90 minutes
np maintenance create --name "Sat patching" --mode Blackout --recurrence Cron \
  --cron "0 0 3 ? * SAT" --duration-minutes 90 --tz "W. Europe Standard Time"
np maintenance update 2c4b... --enabled --end 05:00
np maintenance delete 2c4b...
```

## audit · health · cron · dashboard · observability

```bash
# Audit (admin) — cursor pagination through --after-ts/--after-id
np audit list --action WORKFLOW_PUBLISHED --since 2026-06-01T00:00:00Z --limit 50 -o json
np audit list --resource-type Workflow --resource-id 21f1c0d4-... \
  --after-ts 2026-06-25T10:00:00Z --after-id 7e3f...

# Health (anonymous — /live + /ready + /leader), exit 0 only when live+ready are ok.
# The leader status (leader|follower|leader_unhealthy) is only displayed —
# a passive HA follower is healthy and does not flip the exit code.
np health --server https://np.internal:8443

# Quartz cron — the next fire times
np cron next "0 0 2 ? * MON-FRI" --count 10

# Dashboard & observability
np dashboard -o json
np observability summary
np observability query --query "up{job=\"nodepilot\"}"
np observability query-range --query "rate(nodepilot_workflows_total[5m])" \
  --start 1719100000 --end 1719103600 --step 1m
```

## alerting · operations

```bash
# Alerting (read: Admin/Operator; create/edit/delete/test-fire: admin only)
np alerting catalog                           # event types, filter fields, channels
np alerting list
np alerting get 9a2f...
np alerting create --file ./rule.json
np alerting update 9a2f... --file ./rule.json
np alerting test-fire 9a2f...
np alerting deliveries 9a2f...                # the rule's delivery ledger
np alerting deliveries --limit 50 -o json
np alerting delete 9a2f...

# Operations — a live-ops snapshot: workflows, the call graph, running and recently
# finished executions (all roles, RBAC folder-scoped)
np operations graph -o json
np operations graph --window 60               # the look-back for finished runs: 30 | 60 minutes
```

The most recent 4,000 finished runs in the window are listed individually. If there are more,
`density[]` additionally provides bucketed counters (`total`/`failed`/`cancelled` per workflow and
time slice) across the **whole** window — so "how much ran in the last four hours?" remains
answerable even when not every run is listed individually. The table output names the total in that
case; `meta.densityBucketSeconds` gives the slice width, and `meta.densityCapped` says that even the
counters are only a lower bound.

## system-alert · policies (ADR 0008)

```bash
np system-alert catalog                       # the available sources + fields/parameters
np system-alert list
np system-alert get 9a2f...
np system-alert create --file ./policy.json   # a SaveSystemAlertPolicyRequest as JSON
np system-alert update 9a2f... --file ./policy.json
np system-alert enable 9a2f...
np system-alert disable 9a2f...
np system-alert test-fire 9a2f...
np system-alert delete 9a2f...
```

## settings (admin, ETag-gated)

A file round trip, not `set key=value`. `get [SECTION]` (with `--etag-only` for chaining), `put <SECTION>` (`--file`, `--etag`), `test smtp|llm` (`--file`).

```bash
np settings status
np settings system-info
np settings get Smtp -o json
ETAG=$(np settings get Smtp --etag-only)            # a weak validator, quotes/prefix stripped
np settings put Smtp --file ./smtp.json --etag "$ETAG"
np settings test smtp --file ./smtp-probe.json
np settings test llm --file ./llm-probe.json
```

`smtp-probe.json` (an envelope whose inner `settings` is the server-side `SmtpSettingsDto`; `***` keeps the existing secret):

```json
{ "settings": { "Host": "smtp.internal", "Port": 587, "Username": "alerts", "Password": "***" },
  "toAddress": "ops@internal" }
```

## secrets · backup · db · config

```bash
# secrets — bulk re-encryption after a key rotation / provider migration
np secrets reencrypt --yes                            # exit 1 on partial success

# backup — NEVER pass the passphrase as a flag (--passphrase-env / --passphrase-file / a prompt)
np backup manifest
np backup export --out ./np-2026-06-25.npbackup --passphrase-env BACKUP_PW
np backup export --out ./partial.npbackup --sections workflows,credentials --passphrase-file ./pw.txt
np backup preview ./np-2026-06-25.npbackup --passphrase-env BACKUP_PW
np backup restore ./np-2026-06-25.npbackup --passphrase-env BACKUP_PW \
  --policy skip,users=overwrite --yes                 # skip|rename|overwrite, overridable per section

# db — read by default, --write is opt-in (and requires DbAdmin:AllowWriteQueries=true server-side)
np db info
np db query --sql "SELECT TOP 10 * FROM Workflows ORDER BY CreatedAt DESC"
np db query --file ./remediate.sql --write --yes

# config — client-side only (no server round trip)
np config get
np config set server https://np.internal:8443
np config set default-profile prod
```

## Token storage

DPAPI-encrypted (`CurrentUser` scope) at `%APPDATA%\NodePilot\session-<profile>.dat`. Refresh happens transparently through `TokenRefreshHandler`. The plaintext configuration (server URL, default profile) is in `config.json`.

## Architectural convention

A new API endpoint → a parallel CLI method in `NodePilotApiClient.cs` plus a command. DTOs in `Cli/Api/Dtos/` are **duplicated** (there is no ProjectReference).
