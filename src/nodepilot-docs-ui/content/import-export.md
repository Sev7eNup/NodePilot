# Import / Export & Backup

NodePilot hat zwei getrennte Artefakte: **Workflow-Export** (Teilen) und **System-Configuration-Backup** (DR).

## Workflow Import/Export

| Endpoint | Zweck |
|---|---|
| `GET /{id}/export` | Einzel-Workflow-Export |
| `GET /export` | Bulk-Export |
| `POST /import` | Import — erzeugt neue Einträge, Namenskollision → Suffix `" (Imported 2)"`. Ziel-Folder via `?folderId=` (fehlt → Root); RBAC = Edit-Recht auf dem Zielordner (UI: aktuell selektierter Folder, CLI: `--target-folder`, MCP: `folderId`-Param). |
| `POST /import-scorch` | SCOrch-Import |

Envelope: `nodepilot-workflow-export/v1`. **Secrets werden hier redigiert** (`***`) — Teilen-Artefakt, kein DR.

## System-Configuration Backup (ADR 0001)

Voller DR-Snapshot der Konfiguration: Workflows + Folders/Sharing, Machines, Credentials, Globals + Global-Variable-Ordner, Custom Activities, Users, Settings. **Nicht enthalten:** Execution-History, Audit, Stats. Admin-only. Envelope `nodepilot-system-backup/v1` (`.npbackup`).

### Secret-Handling

Secrets per **Passphrase-Rewrap** (PBKDF2→HKDF→AES-GCM) + Whole-file-HMAC. Geteilte Secret-Logik mit dem Workflow-Export via `WorkflowDefinitionSecretRewriter` (`SecretHandling`).

### Restore

- Validiert Refs (Abbruch bei unresolvable).
- Läuft in EF-Execution-Strategy-gekapselter Transaktion in Abhängigkeitsreihenfolge mit ID-Remap.
- Konflikt-Policy: `skip` / `rename` / `overwrite`.
- Last-Admin-Schutz.

### Endpoints & CLI

| Endpoint | Zweck |
|---|---|
| `GET /api/backup/manifest` | Backup-Manifest |
| `POST /api/backup/export` | Backup erzeugen |
| `POST /api/backup/preview` | Restore-Preview (multipart, Admin) |
| `POST /api/backup/restore` | Restore (multipart, Admin) |

UI: `/backup` (Admin). CLI: `np backup manifest|export|preview|restore` — Passphrase via `--passphrase-env` / `--passphrase-file` / Prompt, **niemals** als Flag.

Audit: `BACKUP_EXPORTED`, `BACKUP_RESTORED`.