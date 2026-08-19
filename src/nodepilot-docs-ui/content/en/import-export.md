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
