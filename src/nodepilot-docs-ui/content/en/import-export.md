# Import / export & backup

NodePilot uses two separate export formats:

- **Workflow export:** exchanging one or more workflow definitions.
- **System configuration backup:** restoring a complete configuration in a disaster-recovery case.

## Workflow import/export

| Endpoint | Purpose |
|---|---|
| `GET /{id}/export` | Single-workflow export |
| `GET /export` | Bulk export |
| `POST /import` | Import — creates new entries; on a name collision the suffix `" (Imported 2)"` is added. Target folder via `?folderId=` (absent → root); RBAC = edit permission on the target folder (UI: the currently selected folder, CLI: `--target-folder`, MCP: the `folderId` parameter). |
| `POST /import-scorch` | SCOrch import |

Envelope: `nodepilot-workflow-export/v1`. **Secrets are redacted here** (`***`) — this is a sharing artifact, not a DR artifact.

### SCOrch import

`POST /import-scorch` reads System Center Orchestrator's native `.ois_export` XML (body =
the raw XML, `Content-Type: application/xml`, 50 MiB cap; folder targeting and RBAC as for
`/import`). Also available as `np workflow import-scorch --file` and as the MCP tool
`import_scorch_workflow`.

What the translation does:

- **Activities** — around forty SCOrch type names map to a NodePilot activity. Note that SCOrch's
  wire names are not always its designer labels: *Invoke Runbook* is `Trigger Policy`, and its
  child-runbook arguments come across as `startWorkflow.parameters`.
- **Published Data** — `` \`d.T.~Vb/{GUID}\`d.T.~Vb/ `` and `` \`d.T.~Ed/{GUID}.field\`d.T.~Ed/ ``
  become `{{globals.Name}}` and `{{step.param.field}}`, resolving through a readable
  `outputVariable` derived from each activity's name. Field names are translated where the two
  products name the same value differently (`Query XML`'s `queryResult` is `xmlQuery`'s `result`),
  and a value NodePilot has no equivalent for is reported rather than pointed at the
  nearest-looking name.
- **Links** — on-success/on-failure links become the `stepId.success` / `stepId.failed` shortcut;
  `TRIGGERS` filters become a `conditionExpression`, joined by the link's own ALL/ANY setting. A
  filter reading a value its source does not publish is reported: such an edge would never match.
- **Branches** — *Compare Values* becomes a `decision`, and the links reading its result are
  re-pointed at the decision's case, so the branch still branches.
- **Triggers** — a runbook without one (SCOrch runbooks invoked by another need none) is given a
  manual trigger wired to its entry activities, because a NodePilot workflow with no trigger node
  has no root and fails on every run.
- **Layout** — the graph keeps its own arrangement. SCOrch's coordinates cannot be copied verbatim
  (it draws small icons, NodePilot draws cards), so the whole graph is scaled uniformly until the
  cards fit — a similarity transform, so the picture is the author's, just larger. Links are then
  made to read as curves: the designer draws an angular loop below both nodes for an edge that runs
  backwards, and after a faithful scale that catches every pair stacked in one column. Such a pair
  is docked top-to-bottom instead, which costs no movement at all; a link that a vertical dock
  cannot help has its target nudged to the right. Rows are never touched, and a genuine loop back to
  an earlier step keeps its loop on purpose. If the arrangement cannot be reproduced at all, the
  import says so and lays the graph out left-to-right instead.

What it does not do, and says so in the report: an activity with no NodePilot counterpart, or a
mapping that cannot fill a required setting, becomes a **disabled** `log` placeholder carrying the
original type name and properties. Credentials are never reconstructed (SCOrch encrypts them).
Imported workflows are always created disabled. Review the warnings before enabling anything.

## System configuration backup (ADR 0001)

A full DR snapshot of the configuration: workflows + folders/sharing, machines, credentials, globals + global-variable folders, custom activities, alerting, users, settings. **Not included:** execution history, audit, statistics. Admin only. Envelope `nodepilot-system-backup/v3` (`.npbackup`) — v2 added the `alerting` section; v3 protects complete workflow definitions with `$encDefinition`, and custom-activity scripts and input defaults with `$enc`. A workflow export automatically pulls in custom activities as a hard dependency. The reader accepts v1, v2 and v3 (including old plaintext custom-activity fields); only v3 is written. Older builds reject v3 visibly.

### Secret handling

Secrets through a **passphrase rewrap** (PBKDF2→HKDF→AES-GCM) + a whole-file HMAC. The secret logic is shared with the workflow export through `WorkflowDefinitionSecretRewriter` (`SecretHandling`).

### Restore

- **The preview runs automatically when the file is selected** — the diff table is right there. Without
  a passphrase it is the structural preview (integrity unverified); after entering the passphrase,
  click "Preview" again to additionally verify the integrity.
- Validates references (aborting on unresolvable ones).
- Runs in a transaction wrapped by the EF execution strategy, in dependency order, with ID remapping.
- Conflict policy: `skip` / `rename` / `overwrite`.
- Last-admin protection.

### Endpoints & CLI

| Endpoint | Purpose |
|---|---|
| `GET /api/backup/manifest` | The backup manifest |
| `POST /api/backup/export` | Create a backup |
| `POST /api/backup/preview` | Restore preview (multipart, admin) |
| `POST /api/backup/restore` | Restore (multipart, admin) |

UI: `/backup` (admin). CLI: `np backup manifest|export|preview|restore` — the passphrase via `--passphrase-env` / `--passphrase-file` / a prompt, **never** as a flag.

Audit: `BACKUP_EXPORTED`, `BACKUP_RESTORED`.
