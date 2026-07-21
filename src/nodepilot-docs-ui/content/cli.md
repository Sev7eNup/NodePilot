# CLI (`np`)

Operations-CLI für Operatoren — `dotnet global tool` (`ToolCommandName=np`). Reiner HTTP-Client gegen die REST-Endpoints: keine eigene API-Surface, kein DB-Zugriff.

> **Konventionen in den Beispielen:** `<ARG>` = erforderlich, `[ARG]` = optional. `<ID-OR-NAME>` nimmt eine Workflow-GUID **oder** den Workflow-Namen (exakte Schreibweise gewinnt, sonst case-insensitive; mehrdeutige Namen → Fehler mit GUID-Hinweis). Destruktive Befehle (`delete`, `force-unlock`, `restore`, `reencrypt`, `db --write`) fragen interaktiv nach — in CI/Pipes `--yes` setzen oder stdin umleiten. `--file -` liest überall von stdin.

## Command-Bereiche

| Bereich | Subcommands |
|---|---|
| `auth` | login / logout / whoami / **methods** (Local/LDAP/Windows; OIDC ist browserbasiert und über `/api/auth/methods` discoverbar) |
| `workflow` | list/get/run/lock/unlock/publish/enable/disable/cancel-all/duplicate/delete/export/import/versions/rollback/force-unlock/import-scorch/stats/**contract**/**coverage**/**trigger**/**step-test**/**move-folder** |
| `exec` | list/get/steps/cancel/retry/watch/resume/paused-steps |
| `machine` / `credential` / `globals` | list/create/update/delete (+ globals export/import) |
| `user` | CRUD |
| `shared-folder` | org RBAC: list/create/rename/move/delete/permissions/grant/revoke |
| `maintenance` | Maintenance Windows: list/get/create/update/delete |
| `audit` | `audit list` |
| `alerting` | list/get/create/update/delete/test-fire/deliveries (Create/Edit/Delete/Test-Fire Admin-only — siehe [Alerting](../alerting)) |
| `system-alert` | catalog/list/get/create/update/enable/disable/delete/test-fire — System-Alert-Policies (ADR 0008); create/update lesen eine `SaveSystemAlertPolicyRequest`-JSON via `--file` |
| `health` | Health-Check (live/ready/leader) |
| `cron` | `cron next` |
| `db` | info/query (Read-Default, `--write` opt-in) |
| `dashboard` | Stats |
| `operations` | graph (Live-Ops-Snapshot: Workflows, Call-Graph, laufende + kürzlich beendete Executions; RBAC-folder-scoped) |
| `observability` | summary/**query**/**query-range** |
| `settings` | status/system-info/get/put/test smtp\|llm |
| `secrets` | **reencrypt** |
| `backup` | manifest/export/preview/restore |
| `config` | get/set |

## Globale Flags

Auf jedem Befehl — Basis-Settings-Klasse `GlobalOptions`:

```
--server <URL>        Server-URL für diesen Aufruf überschreiben
--profile <NAME>      Benanntes Verbindungsprofil (Default: 'default')
-o|--output <FORMAT>  table (TTY-Default) | json | yaml
--no-color            Farbausgabe aus (auto-off bei redirectiertem stdout)
-v|--verbose          HTTP-Request/Response-Trace auf stderr
```

```bash
np workflow list --profile prod -o json --no-color -v
np exec get 7e3f... --server https://np.internal:8443 -o yaml
```

**Exit-Codes:** `0` ok, `1` generic, `2` run failed/cancelled, `3` auth required, `4` permission denied.

## auth

`np auth login` ist interaktiv (fragt Username + Passwort ab, falls nicht per Flag geliefert). Flags: `--username`, `--password` (Literal — in Scripts meiden), `--password-stdin` (eine Zeile von stdin), `--setup-token <T>` (Bootstrap First-Admin).

```bash
# Interaktiv (fragt user + pw ab)
np auth login --server https://np.internal:8443

# Vollständig gescriptet, Passwort via stdin
echo "S3cret!" | np auth login --username admin --password-stdin --server https://np.internal:8443

# Frischer Server: ersten Admin bootstrappen (admin-setup.token)
np auth login --username admin --password-stdin \
  --setup-token "$(cat admin-setup.token)" --server https://np.internal:8443

# Discovery (anonym, keine Session nötig) + Profil-Check
np auth methods --server https://np.internal:8443
np auth whoami -o json
np auth logout
```

`np auth login` nutzt den Passwort-Endpunkt und deckt damit lokale sowie LDAP-Anmeldung ab. Windows Negotiate und OIDC sind Browserflows; OIDC-Metadaten stehen vollständig über `GET /api/auth/methods` zur Verfügung, werden vom aktuellen CLI-DTO aber noch nicht dargestellt.

## workflow

### run / trigger

`run` startet einen Lauf als authentifizierter User; `trigger` ist session-unabhängig und nur via `X-Api-Key` gegated.

`run`-Flags: `-p|--params <k=v>` (wiederholbar; nur erstes `=` splittet), `--wait` (pollen bis terminal), `--follow` (live Step-Events via SignalR), `--debug`, `--timeout <s>`.

```bash
np workflow run deploy-prod -p environment=staging -p revision=abcd123
np workflow run deploy-prod -p env=prod --debug --follow
np workflow run 21f1c0d4-... -p env=prod --wait --timeout 600 -o json
```

`trigger`-Flags: `--api-key <K>`, `--api-key-stdin`, env `NODEPILOT_TRIGGER_API_KEY` (Preferred für Scripts), `-p|--params`, `--idempotency-key <K>`, `--timeout`, `--wait` (erfordert JWT-Session, da `/api/executions/{id}` JWT-only ist).

```bash
# API-Key aus stdin, Idempotency-Key gegen CI-Replays
np workflow trigger nightly-reconcile --api-key-stdin \
  --idempotency-key "ci-$(date +%s)" -p day=2026-06-25 --wait < api.key

# Key via env (sicherer als --api-key)
NODEPILOT_TRIGGER_API_KEY=xyz np workflow trigger hourly-report -p window=1h --timeout 300
```

### lock / unlock / publish / enable / disable / cancel-all

Alle nehmen `<ID-OR-NAME>`, keine Extra-Flags (außer `publish`).

```bash
np workflow lock deploy-prod
np workflow unlock deploy-prod
np workflow enable deploy-prod     # 423 wenn gelockt
np workflow disable deploy-prod    # ignoriert Locks (Incident-Kill-Switch)
np workflow cancel-all deploy-prod
np workflow duplicate deploy-prod
np workflow force-unlock deploy-prod   # Admin; fragt nach
```

`publish` — atomar Save + Enable + Unlock. Flags: `-f|--file <PATH>` (required, JSON-Definition), `--name`, `--description`.

```bash
np workflow publish deploy-prod -f ./deploy-prod.def.json
np workflow publish deploy-prod -f ./deploy-prod.def.json --description "bump revision" -o json
```

### versions / rollback

```bash
np workflow versions deploy-prod -o json
np workflow version deploy-prod 12 -o yaml          # eine Version voll
np workflow rollback deploy-prod 12 --reason "revert bad config"
```

### contract / coverage / stats

```bash
np workflow contract deploy-prod -o json            # Inputs (manualTrigger) + Outputs (returnData + system)
np workflow coverage deploy-prod --window-days 7    # Default 30, max 365
np workflow stats deploy-prod --window-days 30      # 1..365, per-step Dauer + Fail-Rate
```

### step-test / step-test-context

`step-test`-Positionale: `<WORKFLOW> <STEP-ID>`. Flags: `-m|--mock <stepName.field=value>` (wiederholbar), `--config-file <PATH>` (JSON-Override für `data.config`; `-` = stdin).

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
cat all.json | np workflow import -f -               # Namenskollision → Suffix
np workflow import -f ./deploy.json --target-folder 8a2f...        # Ziel-Folder (fehlt → Root)
np workflow import-scorch -f ./scorch-export.ois_export   # SCOrch .ois_export XML
np workflow import-scorch -f ./scorch.ois_export --target-folder 8a2f...
```

`--target-folder <GUID>` erfordert Edit auf dem Ziel-Folder (RBAC).

### move-folder

```bash
np workflow move-folder deploy-prod --target-folder 8a2f...
```

## exec

Positional `<EXECUTION-ID>` (Guid) für get/steps/cancel/retry/watch/paused-steps.

```bash
np exec list -w deploy-prod --limit 20 -o table      # --workflow, --limit (Server-Cap 100)
np exec get 7e3f...
np exec steps 7e3f...
np exec cancel 7e3f...
np exec retry 7e3f...                                 # nur terminal → 202 + Location
np exec watch 7e3f...
np exec watch 7e3f... --no-signalr                    # Polling-Fallback erzwingen
np exec paused-steps 7e3f...
```

`resume` (debug-paused): `--step <ID>` (required), `--mode <continue|stepOver|stop>` (Default `continue`), `--override <k=v>` (wiederholbar).

```bash
np exec resume 7e3f... --step runHealth --mode continue
np exec resume 7e3f... --step runHealth --mode stepOver --override freeGb=8 --override host=svr01
np exec resume 7e3f... --step runHealth --mode stop
```

## machine

Maschinen-Body via Flags (nicht `--file`). `create`/`update`-Flags: `--name`, `--hostname`, `--port` (Default 5985), `--ssl`, `--credential <GUID>`, `--tags <CSV>`. `update` ist Client-seitig Patch (fetch + merge, Server will Full-Body).

```bash
np machine create --name srv01 --hostname srv01.internal --credential 4c2a... --tags prod,web
np machine update 9f1a... --port 5986 --ssl --tags prod,web,decommissioned
np machine list -o json
np machine get 9f1a...
np machine test 9f1a...
np machine test 9f1a... --credential 4c2a...   # Credential für diesen Probe überschreiben
np machine delete 9f1a...                       # fragt nach
```

## credential

Credentials via Flags. `create`: `--name`, `--username`, `--password` (min 8 Zeichen; lieber `--password-stdin`), `--password-stdin`, `--domain`, `--expires <ISO-DATE>` (optionales Ablaufdatum — speist das `CredentialExpiring`-Alert-Signal). `update`: gleiche Flags, weggelassenes Password = unverändert, `--no-expires` löscht ein gesetztes Ablaufdatum.

```bash
echo 'Sup3rSecret!' | np credential create --name svc-winrm \
  --username svc-winrm@DOM --password-stdin --domain DOM --expires 2026-12-31
np credential list
np credential get 4c2a...
np credential update 4c2a... --name svc-winrm-v2 --password-stdin < newpw.txt   # Rotation
np credential update 4c2a... --no-expires                                       # Ablaufdatum entfernen
np credential delete 4c2a...
```

> `credential create` hat **kein** `--description`-Flag — nur name/username/password/password-stdin/domain/expires.

## globals

`create`: `--name` (Pattern `[A-Za-z0-9_-]{1,100}`, keine Punkte/Leerzeichen), `--value`/`--value-stdin`, `--secret` (DPAPI, masked on read), `--description`, `--folder` (Ordner-Id, -Pfad oder -Name; fehlt → Root).

Ordner sind rein organisatorisch — sie ändern **nichts** daran, wie `{{globals.NAME}}` auflöst (Namen bleiben global eindeutig).

```bash
np globals create --name adminEmail --value ops@internal --description "Default alert recipient"
echo '-----BEGIN PRIVATE KEY-----...' | np globals create --name signingKey --value-stdin --secret --folder /Secrets
np globals list -o json
np globals update 3b8c... --value new-email@internal
np globals update 3b8c... --no-secret
np globals update 3b8c... --folder /Environment/Prod       # verschiebt die Variable
np globals delete 3b8c...
np globals export --file ./globals.json                   # secrets als ***
np globals import -f ./globals.json --upsert
np globals import -f - --dry-run < globals.json

# Ordner-Baum (Admin)
np globals folder list
np globals folder create --name Databases
np globals folder create --name Prod --parent /Environment
np globals folder rename <folder-id> --name Renamed
np globals folder move <folder-id> --parent /Environment    # reparent (Zyklus-/Tiefen-geschützt)
np globals folder delete <folder-id>                        # nur wenn leer
np globals move-folder <var-id> --folder /Databases         # Variable → Ordner
```

## user · shared-folder · maintenance

### user (Admin)

```bash
np user list -o json
echo 'TempPw1!' | np user create --username jane.doe --password-stdin --role Operator
np user update 5a1c... --role Admin --active true
np user update 5a1c... --password-stdin < newpw.txt
np user delete 5a1c...
```

### shared-folder (org RBAC-Baum)

```bash
np shared-folder list
np shared-folder create --name "Prod Workflows" --parent 8a2f...
np shared-folder rename 8a2f... --name "Prod Flows"
np shared-folder move 8a2f... --parent 9b1c...
np shared-folder move 8a2f... --to-root
np shared-folder delete 8a2f...                          # muss leer sein
np shared-folder permissions 8a2f...
np shared-folder grant 8a2f... --principal-type User   --principal-key 5a1c...            --role FolderEditor
np shared-folder grant 8a2f... --principal-type Group  --principal-key S-1-5-21-100-200-300 --role FolderOperator
np shared-folder revoke 8a2f... 7e3f...                  # <FOLDER> <PERMISSION-ID>
```

Rollen: `FolderViewer | FolderOperator | FolderEditor | FolderAdmin`. `--principal-type`: `User | Group` (`Role` ist V1-reserviert).

### maintenance (Admin)

`create`/`update`-Flags: `--name`, `--description`, `--enabled`/`--disabled`, `--mode <Blackout|AllowOnly>`, `--scope <Global|Folders|Workflows>`, `--recurrence <OneTime|Weekly|Cron>`, `--tz <TZID>`, `--one-time-start/--one-time-end <ISO>`, `--days <CSV>` (Mon..Sat), `--start/--end <HH:MM>`, `--cron <EXPR>` (Quartz, mit Sekundenfeld), `--duration-minutes <N>`, `--folder`/`--workflow` (wiederholbar).

```bash
np maintenance list
# Wöchentliches Change-Freeze-Fenster Sa/So 02:00–04:00 Lokalzeit
np maintenance create --name "Change freeze" --mode Blackout --scope Global \
  --recurrence Weekly --days Sat,Sun --start 02:00 --end 04:00 --tz "W. Europe Standard Time"
# Einmaliges Deploy-Fenster, nur ein Workflow darf laufen
np maintenance create --name "Deploy window" --mode AllowOnly --scope Workflows \
  --workflow 21f1c0d4-... --recurrence OneTime \
  --one-time-start 2026-06-25T20:00:00Z --one-time-end 2026-06-25T22:00:00Z
# Cron-Fenster: jeden Samstag 03:00 Lokalzeit, 90 Minuten offen
np maintenance create --name "Sat patching" --mode Blackout --recurrence Cron \
  --cron "0 0 3 ? * SAT" --duration-minutes 90 --tz "W. Europe Standard Time"
np maintenance update 2c4b... --enabled --end 05:00
np maintenance delete 2c4b...
```

## audit · health · cron · dashboard · observability

```bash
# Audit (Admin) — Cursor-Pagination via --after-ts/--after-id
np audit list --action WORKFLOW_PUBLISHED --since 2026-06-01T00:00:00Z --limit 50 -o json
np audit list --resource-type Workflow --resource-id 21f1c0d4-... \
  --after-ts 2026-06-25T10:00:00Z --after-id 7e3f...

# Health (anonym — /live + /ready + /leader), Exit 0 nur wenn live+ready ok.
# Der Leader-Status (leader|follower|leader_unhealthy) wird nur angezeigt —
# ein passiver HA-Follower ist gesund und kippt den Exit-Code nicht.
np health --server https://np.internal:8443

# Quartz cron — nächste Feuerzeiten
np cron next "0 0 2 ? * MON-FRI" --count 10

# Dashboard & Observability
np dashboard -o json
np observability summary
np observability query --query "up{job=\"nodepilot\"}"
np observability query-range --query "rate(nodepilot_workflows_total[5m])" \
  --start 1719100000 --end 1719103600 --step 1m
```

## alerting · operations

```bash
# Alerting (Read Admin/Op; Create/Edit/Delete/Test-Fire Admin-only)
np alerting list
np alerting get 9a2f...
np alerting create --file ./rule.json
np alerting update 9a2f... --file ./rule.json
np alerting test-fire 9a2f...
np alerting deliveries 9a2f...                # Zustell-Ledger der Regel
np alerting deliveries --limit 50 -o json
np alerting delete 9a2f...

# Operations — Live-Ops-Snapshot: Workflows, Call-Graph, laufende + kürzlich
# beendete Executions (alle Rollen, RBAC-folder-scoped)
np operations graph -o json
```

## system-alert · policies (ADR 0008)

```bash
np system-alert catalog                       # verfuegbare Quellen + Felder/Parameter
np system-alert list
np system-alert get 9a2f...
np system-alert create --file ./policy.json   # SaveSystemAlertPolicyRequest als JSON
np system-alert update 9a2f... --file ./policy.json
np system-alert enable 9a2f...
np system-alert disable 9a2f...
np system-alert test-fire 9a2f...
np system-alert delete 9a2f...
```

## settings (Admin, ETag-gated)

File-Roundtrip, kein `set key=value`. `get [SECTION]` (mit `--etag-only` für Chaining), `put <SECTION>` (`--file`, `--etag`), `test smtp|llm` (`--file`).

```bash
np settings status
np settings system-info
np settings get Smtp -o json
ETAG=$(np settings get Smtp --etag-only)            # schwacher Validator, Quotes/Prefix stripped
np settings put Smtp --file ./smtp.json --etag "$ETAG"
np settings test smtp --file ./smtp-probe.json
np settings test llm --file ./llm-probe.json
```

`smtp-probe.json` (Envelope, inneres `settings` = serverseitiges `SmtpSettingsDto`; `***` hält das bestehende Secret):

```json
{ "settings": { "Host": "smtp.internal", "Port": 587, "Username": "alerts", "Password": "***" },
  "toAddress": "ops@internal" }
```

## secrets · backup · db · config

```bash
# secrets — Bulk-Re-Encryption nach Key-Rotation / Provider-Migration
np secrets reencrypt --yes                            # Exit 1 bei Partial Success

# backup — Passphrase NIE als Flag (--passphrase-env / --passphrase-file / Prompt)
np backup manifest
np backup export --out ./np-2026-06-25.npbackup --passphrase-env BACKUP_PW
np backup export --out ./partial.npbackup --sections workflows,credentials --passphrase-file ./pw.txt
np backup preview ./np-2026-06-25.npbackup --passphrase-env BACKUP_PW
np backup restore ./np-2026-06-25.npbackup --passphrase-env BACKUP_PW \
  --policy skip,users=overwrite --yes                 # skip|rename|overwrite, per-section überschreibbar

# db — Read-Default, --write opt-in (DbAdmin:AllowWriteQueries=true serverseitig)
np db info
np db query --sql "SELECT TOP 10 * FROM Workflows ORDER BY CreatedAt DESC"
np db query --file ./remediate.sql --write --yes

# config — CLI-seitig (kein Server-Roundtrip)
np config get
np config set server https://np.internal:8443
np config set default-profile prod
```

## Token-Storage

DPAPI-verschlüsselt (`CurrentUser`-Scope) unter `%APPDATA%\NodePilot\session-<profile>.dat`. Refresh transparent via `TokenRefreshHandler`. Plaintext-Config (Server-URL, Default-Profile) in `config.json`.

## Architektur-Konvention

Neuer API-Endpoint → parallele CLI-Methode in `NodePilotApiClient.cs` + Command. DTOs in `Cli/Api/Dtos/` **dupliziert** (kein ProjectReference).
