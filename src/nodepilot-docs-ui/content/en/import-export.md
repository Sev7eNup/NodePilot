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
the raw XML, `Content-Type: application/xml`, 300 MiB cap; folder targeting and RBAC as for
`/import`). Also available as `np workflow import-scorch --file` and as the MCP tool
`import_scorch_workflow`.

What the translation does:

- **Job concurrency** — a runbook's `MaxParallelRequests` becomes the workflow's concurrency
  limit, verbatim. That includes the value `1`, which in Orchestrator means one instance at a
  time and is its default, so an imported runbook keeps the behaviour it had instead of quietly
  becoming unlimited. Because that default is so common, most runbooks arrive limited — the
  import report tells you how many, so nothing serializes unnoticed after a migration. An absent
  or out-of-range value imports as unlimited.
- **Activities** — around forty SCOrch type names map to a NodePilot activity. Note that SCOrch's
  wire names are not always its designer labels: *Invoke Runbook* is `Trigger Policy`, and its
  child-runbook arguments come across as `startWorkflow.parameters`.
- **Program calls** — *Run Program* becomes `startProgram`, always. The export already distinguishes
  an external call from an embedded script (*Run .Net Script*, which becomes `runScript`), and that
  distinction is taken as given rather than second-guessed from what the program field happens to
  contain. What the importer does do is fill the node's two fields: where SCOrch kept the arguments
  in the program field instead of its own, the executable and its arguments are separated for you —
  including SCOrch's command-line mode, where a `|` separates the two. A command line that genuinely
  needs a shell (a pipe into another program, a redirect) runs through `cmd.exe /C`, which is how
  SCOrch runs one itself, and a bare launcher name such as `cmd` is completed to its absolute path
  because the engine does not search `PATH`, while a script in the program field — a `.ps1`, a `.vbs` —
  gets its real interpreter in `filePath`, because the engine launches through `CreateProcess`, which
  cannot start a script at all. Every such reconstruction is named in the import report.
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
- **Sub-runbook calls** — a call is matched to its child by the full path SCOrch stores. That
  matters when importing a whole estate: SCOrch allows two runbooks of the same name in different
  folders, NodePilot does not, so one is renamed on import and its callers are re-pointed at the
  name it was actually given. A call into a runbook that is neither in the file nor already in
  NodePilot is reported, since it would fail at run time.
- **Folders** — an export carries its own tree, for runbooks and for global variables alike, and
  both are rebuilt below the destination you import into. Folders that already exist are reused
  (matched ignoring case), names longer than 120 characters are shortened, and a tree deeper than
  NodePilot's five levels is merged into the deepest level that fits — the last two are reported.
  Creating them needs no permission beyond the edit rights on the destination you already need,
  since every new folder sits underneath it and inherits its access.
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

An admin-only, portable **configuration backup** containing workflows + folders/sharing, machines, credentials, globals + global-variable folders, custom activities, alerting, users, and settings. It is deliberately not advertised as a complete disaster-recovery snapshot: execution history, audit data, statistics, the native database, service configuration, and installation data are not included. Back those up separately and test the complete recovery procedure.

The only supported envelope is `nodepilot-system-backup/v4` (`.npbackup`). Export fails instead of producing a partial archive when any requested section cannot be exported. A workflow export automatically pulls in custom activities as a hard dependency.

### Secret handling

The complete configuration payload, including its metadata and section list, is encrypted and authenticated with the supplied passphrase (PBKDF2→HKDF→AES-GCM). Sensitive fields also retain their section-level protection. Metadata and preview data are unavailable until authenticated decryption succeeds.

### Restore

- Preview requires the passphrase and is shown only after the complete archive was authenticated and decrypted.
- Validates references (aborting on unresolvable ones).
- Runs in a transaction wrapped by the EF execution strategy, in dependency order, with ID remapping.
- Aborts and rolls back the complete restore if any selected part produces a warning or cannot be restored completely; settings-file changes are compensated if the database commit fails.
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
