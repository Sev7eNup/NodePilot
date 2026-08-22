# Triggers

A trigger determines what starts a workflow. A workflow can contain several triggers. Each trigger starts the workflow independently of the others.

## Adding a trigger

1. Drag a trigger from the node library onto the canvas.
2. Select the trigger and configure it in the properties panel.
3. Connect the trigger to the first activity.
4. Save, publish and enable the workflow.

Automatic triggers are only monitored for published and enabled workflows.

## Trigger types

| Trigger | Use |
|---|---|
| **Manual** | Started through the web UI, the desktop app or the API |
| **Schedule** | Started at defined times |
| **Webhook** | Started by an HTTP request |
| **File watcher** | Started by a change in a directory |
| **Database** | Started when the result of a SQL query changes |
| **Windows event log** | Started by a matching Windows event |

## Manual trigger

The manual trigger suits workflows that are started deliberately.

Configurable:

- The title and description of the start dialog
- Input parameters of type text, number, yes/no or choice
- Whether each parameter is required, and its default value

When starting, NodePilot shows the defined input fields. A parameter named `customerId` is available in the workflow as `{{manual.customerId}}`.

## Schedule

The schedule trigger starts a workflow based on a cron expression. Templates are available for common schedules:

| Schedule | Cron expression |
|---|---|
| Every 5 minutes | `0 */5 * * * ?` |
| Hourly | `0 0 * * * ?` |
| Daily at 06:00 | `0 0 6 * * ?` |
| Monday to Friday at 08:00 | `0 0 8 ? * MON-FRI` |

The preview in the properties panel shows the next execution times and points out an invalid expression.

Output data:

- `firedAt`: the time of the current firing
- `nextFireAt`: the next scheduled firing

## Webhook

A webhook starts a workflow through a request to:

```text
<NodePilot address>/api/webhooks/<workflow>/<path>
```

The HTTP method and path have to match the trigger configuration. In the web UI, `POST`, `PUT` and `GET` are available.

### Securing access

Two mechanisms are available:

| Mechanism | Use |
|---|---|
| **Shared secret** | The configured secret is sent in the `X-Webhook-Secret` header. |
| **NodePilot HMAC v2** | Signed requests with a timestamp and a unique delivery ID; suitable for integrations that need replay protection. |

HMAC v2 requires a securely generated secret of at least 32 UTF-8 bytes. The sender additionally has to send `X-NodePilot-Timestamp`, `X-NodePilot-Delivery-Id` and the configured signature.

Native HMAC signatures from GitHub, GitLab or Alertmanager are not directly compatible with NodePilot HMAC v2. An adapter is required for those, which verifies the provider's signature and then produces a NodePilot request.

Further security settings are in [Hardening](./security/hardening).

### Taking values from the body

Field mappings take individual values out of a JSON body. Each mapping consists of a name and a JSONPath.

Example:

| Name | JSONPath | Use in the workflow |
|---|---|---|
| `ticketId` | `$.ticket.id` | `{{manual.ticketId}}` |

If the body contains no JSON, or the path is not found, the mapped value stays empty.

Further available webhook data:

- `webhookBody`
- `webhookMethod`
- `webhookPath`
- Query parameters as `webhookQuery_<name>`
- Permitted headers in shared-secret mode as `webhookHeader_<name>`

## File watcher

The file watcher reacts to files in a directory.

Configurable:

- An absolute directory path
- A file filter, for example `*.csv`
- The event: created, changed, deleted, renamed, or all changes
- Whether subdirectories are included

The path refers to the file system of the machine NodePilot runs on. The directory has to exist, be reachable, and lie within the server-side permitted paths.

Symlinks, junctions and other reparse points in the watched path are always rejected. With
subdirectories enabled, this also applies to the subtree present at startup; the manual test run does
not follow such entries either. Event paths are re-checked immediately before the workflow starts.
Regular UNC shares remain supported; Windows device and extended-path namespaces (`\\?\`, `\\.\`,
`\\??\`) are rejected, because they could bypass the system-path block through an alternative
spelling. Administrative shares of the local machine (for example `\\localhost\C$`) are mapped to the
corresponding local path for the policy check; `\\localhost\C$\Windows` therefore cannot bypass the
system block. By default, a watch root must also not contain a blocked system path as a subtree (for
example `C:\` with subdirectories enabled). Named local shares with no safely derivable target path
are rejected when `AllowSystemPaths=false`; with the explicit system-path permission they remain
usable. Shares on other machines remain supported unchanged.

### Behaviour when the directory becomes unreachable

If the watched directory becomes unreachable — for example because a network share disappears through a reboot or a deleted share — NodePilot detects that and tries to re-establish the watch periodically. The intervals grow up to five minutes. As soon as the directory is reachable again, the watch continues by itself; no restart or manual intervention is needed.

Files that land in the directory during the interruption do **not** trigger the workflow retroactively. They stay there and are only picked up by a later change. If you need gapless processing, build the workflow so that it processes the whole directory at start instead of only the reported file.

So that such an interruption does not go unnoticed, a system policy on the source **Trigger not registered** can be created under [Alerting](./alerting).

Output data:

- `fileAction`: the kind of change
- `filePath`: the full file path
- `fileName`: the file name, extension included
- `fileNameWithoutExtension`: the file name with its extension removed
- `fileDirectory`: the folder the file is in

## Database

The database trigger checks a SQL query periodically. The value of the first column of the first row serves as the comparison value. The first retrieval establishes the baseline; every later change starts the workflow.

Configurable:

- The name of a stored database connection
- The polling interval
- The SQL query

The query runs before the workflow and therefore cannot use workflow variables such as `{{...}}`. Credentials belong in the server configuration, not in the workflow definition.

Output data:

- `dbSentinel`: the new comparison value
- `dbPrevious`: the previous comparison value

## Windows event log

This trigger starts the workflow on a matching entry in the Windows event log.

Configurable:

- The log, for example `Application` or `System`
- The event type
- An optional source
- An optional event ID
- An optional message pattern (a regular expression against the message text)
- The look-back period

The look-back period only applies to the manual test run of the trigger node: it determines how far the sample search looks back. The running trigger does not replay past events.

`Application` and `System` are permitted by default. Further logs, `Security` in particular, have to be permitted administratively. The trigger is only available on Windows.

Output data:

- `eventSource`
- `eventEntryType`
- `eventId`
- `eventMessage`
- `eventTimeWritten`

## Using trigger data

Trigger data is available to connected activities after the trigger node:

```text
{{manual.<name>}}
```

Alternatively, it can be accessed through the trigger node's output variable:

```text
{{<output variable>.param.<name>}}
```

Example for a file watcher with the output variable `watch`:

```text
{{manual.filePath}}
{{watch.param.filePath}}
```

There is no `{{trigger.*}}` namespace.

## Starting a workflow externally through the API

A published and enabled workflow can be started through the external trigger API independently of a webhook node. To do so it has to contain an **active manual trigger**, and its GUID has to be in the scope of the integration key used:

```bash
curl -X POST "https://nodepilot.example/api/trigger/Deploy" \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: <API key>" \
  -H "Idempotency-Key: deploy-2026-07-27-001" \
  -d '{"parameters":{"version":"2.1.0"}}'
```

Prerequisites:

- The workflow contains a `manualTrigger` that is not disabled.
- `X-Api-Key` contains an integration key of at least 32 UTF-8 bytes.
- The SHA-256 hash of that key is configured under `ExternalTrigger:Keys`, and `AllowedWorkflowIds` contains the workflow GUID. Names and wildcards are not accepted.
- `Idempotency-Key` is optional. Repeated requests with the same header **and the same authenticated integration key** start no second execution within 24 hours. Other integration keys have a separate replay domain and cannot block each other or retrieve someone else's results. NodePilot persists only a domain-separated SHA-256 digest, never the header value.

The key and hash can be generated locally. Only the hash is stored in NodePilot; the plaintext key goes exclusively to the integration:

```powershell
$key = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
$hash = [Convert]::ToBase64String(
  [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($key)))
$key
$hash
```

```json
{
  "ExternalTrigger": {
    "ApiKey": "",
    "AllowedWorkflowIds": [],
    "Keys": {
      "ci-deploy": {
        "KeyHash": "<SHA-256 as Base64>",
        "AllowedWorkflowIds": ["21f1c0d4-0000-0000-0000-000000000000"]
      }
    }
  }
}
```

Every key has its own scope. A key for workflow A cannot start workflow B. An unknown key returns `401`; a workflow that is missing, disabled, not permitted, or has not opted in through a `manualTrigger` returns `404` uniformly.

The entire `ExternalTrigger:Keys` map is evaluated atomically per provider: the highest-priority provider that declares the map owns the complete snapshot, and `Keys: {}` revokes all lower-priority integration keys. An override therefore has to contain every entry you still want, including its hash and scope. Allow-lists are atomic too: a shorter list replaces all lower-priority indices, and `[]` is deny-all. That way neither removed keys nor GUIDs can reappear through the additive `IConfiguration` merging.

The idempotency principal ID contains the case-insensitively canonicalized integration ID and the key fingerprint. A change of case alone keeps the replay domain, whereas a key rotation deliberately starts a new one. When upgrading to this storage, older raw cache entries are no longer used for replays; they expire within the regular 24-hour TTL. A retry across exactly that upgrade boundary can therefore produce one additional execution.

Migrating the old `ExternalTrigger:ApiKey`: first enter the permitted GUIDs under `ExternalTrigger:AllowedWorkflowIds`. An empty list refuses every start. Then create a new hash entry per integration and delete the legacy key. The same key must not be configured as both a legacy key and a hash entry during the migration; duplicate matches are rejected fail-closed.

Further information is in [Workflow control flow](./api/workflow-control).
