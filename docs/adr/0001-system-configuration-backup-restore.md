# ADR 0001 — System Configuration Backup & Restore

**Status:** Implemented (phases 1–4), security contract amended for schema v4 — 2026-08-27
**Scope:** New top-level Backup feature (API + CLI + UI). Implementation in 4 phases.

## Context

NodePilot today only has a *workflow* export (`/api/workflows/export`), which deliberately
**redacts** secrets (`WorkflowsControllerBase.RedactSecretsInDefinition`) — a *sharing*
artifact, not a restore artifact. Productive single-node operation has no disaster-recovery
path: lose the database and everything gets clicked in again (machines, credentials, globals,
users, settings, folder sharing). A raw database dump does not help, because credential secrets
are encrypted through `ISecretProtector` either with DPAPI (machine- and user-bound, **not
portable**) or with AES-GCM under `Secrets:MasterKey`.

## Decision (guiding principle)

Split by **intent**, share the code beneath:

| | Context export (stays) | System backup (new) |
|---|---|---|
| Purpose | share *one* workflow | portable system configuration |
| Location | Workflows page | its own admin entry `/backup` |
| Secrets | redacted | rewrapped behind a passphrase |
| Scope | workflows only | workflows+folders/sharing, machines, credentials, globals, users, settings |

The workflow export mapping moves into a shared helper; `/api/workflows/export` and the backup
call the same code with different **SecretHandling**. No duplicated mapping, no export button per
menu entry.

### Boundary — what the backup is NOT

It is a **system *configuration* backup**, not a full database backup. **Not** included: audit
log, execution history, step executions, support events, workflow versions, stats, idempotency
keys. Both the UI **and** the documentation have to say so plainly. For an operational
disaster-recovery plan, the native database, `ProgramData`, service configuration and key material
must be backed up as well. The combined restore procedure must be tested; a `.npbackup` file by
itself is not proof of recoverability.

## Current file format `nodepilot-system-backup/v4`

`.npbackup` is a JSON envelope whose complete content payload is encrypted and authenticated.
Section names, counts, application metadata, scripts, hostnames and all resource data are visible
only after successful passphrase verification and AES-GCM authentication. Schemas v1–v3 exposed
non-secret metadata and are deliberately rejected by the current reader.

```jsonc
{
  "schema": "nodepilot-system-backup/v4",
  "crypto": { "kdf":"PBKDF2-SHA256", "iterations":600000,
              "salt":"<b64>", "verifier":"<b64>" },
  "payload": "<base64 AES-256-GCM envelope>"
}
```

The authenticated inner payload contains `backupKind: "configuration"`, `appVersion`,
`createdAt`, `createdBy` and `sections`. Secret-bearing fields remain individually wrapped so the
existing per-resource restore path never needs a plaintext intermediate file.

## Corrections to the first draft (binding before phase 1)

### K1 — No new interface type
`ISecretProtector` already exists in `NodePilot.Core/Interfaces`. **Only** a new implementation
`PassphraseSecretProtector` is added in `NodePilot.Data/Security/`, alongside the existing
protectors. No interface in `Data/Security`.

### K2 — Workflow secrets: a SecretHandling enum rather than a bool
Workflow secrets live **inline in `DefinitionJson`** (`secret`, `apiKey`, `password`,
`authToken`, `bearer`, `connectionString` — see `SecretConfigKeys`). `redactSecrets:false` must
**not** write plaintext into the backup file. The workflow exporter gets:

```
enum SecretHandling { Redact, EncryptForBackup, PlainInternal }
```

- `Redact` — today's behaviour, for the context export / sharing.
- `EncryptForBackup` — inline secret fields become `{"$enc":…}` (passphrase rewrap), for backups.
- `PlainInternal` — internal only (round-trip tests, for example), never over the wire.

`targetMachineId`/`credentialId` are **not** secrets → no `$enc`, an ID remap instead (K3).

### K3 — Restore needs a sourceId → targetId map (mandatory)
By name is not enough: workflows, machines and credentials carry no hard uniqueness guarantee,
and definitions and entities reference GUIDs. **Every remappable section** (folders, users,
credentials, machines, global variables, workflows) therefore carries an explicit `sourceId` (the
original GUID) in the format. Restore runs in two phases:

1. **Create:** assign each resource a new id (or, under `overwrite`/`skip`, the target id) and
   fill a `Dictionary<Guid,Guid>` per type (`folderMap`, `userMap`, `credentialMap`,
   `machineMap`, `globalMap`, `workflowMap`). `globalMap` only if globals are ever referenced by id.
2. **Rewrite references** through the maps:
   - `ManagedMachine.DefaultCredentialId` → `credentialMap` (see the ordering in K4).
   - `SharedWorkflowFolder.ParentFolderId` → `folderMap`; `CreatedByUserId` → `userMap` (K17).
   - `SharedFolderPermission`: `FolderId` → `folderMap`, `GrantedByUserId` → `userMap`,
     `PrincipalKey` → `userMap` **only when it is a user GUID**; AD group SIDs stay untouched.
   - Workflow definition: **only** `data.targetMachineId` → `machineMap` and `data.credentialId`
     → `credentialMap`, and **only when the field value is a parsable GUID string** (K13).

Under the `rename`/`skip` conflict policies the map points at the id actually used, so references
do not dangle.

### K13 — Keep the workflow ID rewrite narrow
The definition rewrite remaps exactly the known fields `data.targetMachineId` and
`data.credentialId`, and **only** when the value parses via `Guid.TryParse`. Everything else is
preserved byte for byte — in particular template expressions such as `{{globals.X}}` and
`{{step.output}}`, node ids (`step-123`), scripts and edge conditions. No "find every GUID".

### K4 — Folders: separate structure from grants, and fix the ordering
Folder grants (`SharedFolderPermission.PrincipalKey` = user GUID or AD SID) need the users;
`ManagedMachine.DefaultCredentialId` needs the credentials → **credentials before machines**.
`SharedWorkflowFolder.CreatedByUserId` is a user id and is remapped through `userMap` during the
structure restore (K17) — hence users before folder structure. Order (everything except settings
inside **one** database transaction):

```
1. Users                           5. Global variables
2. Folder structure (no grants;    6. Workflows  (refs via the maps — K13)
   CreatedByUserId via userMap)     7. Folder grants/permissions (late, remapped)
3. Credentials                     8. Settings  (separate, NOT in the TX — K8)
4. Machines (DefaultCredentialId
   via credentialMap)
```

An alternative for machines, if an ordering without the credentials precondition is wanted:
create them without `DefaultCredentialId` and patch it after the credentials. The order above is
the default.

### K5 — Whole-payload confidentiality and integrity
Schema v4 encrypts and authenticates the complete inner payload with AES-256-GCM. Both preview and
restore require the passphrase and reject an authentication failure before exposing section
metadata or writing anything. This supersedes the v1–v3 plaintext envelope plus whole-file HMAC.

### K6 — Upload as multipart/form-data
`preview`/`restore` take the file as `multipart/form-data` (field `file` plus field
`passphrase`), **not** as a JSON body with an embedded file. Export stays a JSON request → file
download.

## Crypto — `PassphraseSecretProtector`
The same primitives as the existing AES-GCM provider. Passphrase → PBKDF2-SHA256 (600k
iterations, random salt) → a 256-bit master secret.

**K14 — key separation:** the PBKDF2 result is **not** reused directly for several purposes.
Three subkeys are derived via HKDF-SHA256-Expand with distinct `info` labels: `encKey`
(AES-256-GCM for the `$enc` fields), `macKey` (whole-file HMAC, K5) and `verifierKey` (GCM of a
known token → passphrase check before every write). Salt, iterations and `verifier` live in the
`crypto` header.

**Rewrap** exactly as in `CredentialStore.ReencryptAllCredentialsAsync`:
- Export: decrypt with the running `ISecretProtector` → encrypt with the passphrase protector.
- Restore: decrypt with the passphrase protector → re-encrypt with the **target**
  `ISecretProtector`.

User BCrypt hashes are not re-hashed — they are placed behind the passphrase as a field and
restored 1:1.

## Backend
`IBackupPart` implementations in `NodePilot.Api/Services/Backup/`, one per resource
(`FolderBackupPart`, `UserBackupPart`, `MachineBackupPart`, `CredentialBackupPart`,
`GlobalVariableBackupPart`, `WorkflowBackupPart`, `SettingsBackupPart`), each with
`Export / Preview / Restore(ConflictPolicy, IdMaps)`.

`BackupController` (`[Authorize(Roles="Admin")]`):

| Endpoint | Body | Purpose |
|---|---|---|
| `GET /api/backup/manifest` | — | per-section counts (UI checkboxes) |
| `POST /api/backup/export` | `{ sections[], passphrase }` | streams the `.npbackup` |
| `POST /api/backup/preview` | multipart `file` + `passphrase` | authenticated per-section diff |
| `POST /api/backup/restore` | multipart `file` + `passphrase` + `policy{}` | applies it |

**Conflict policy** (by-name match, as with import; default `skip`): `skip` / `rename` (suffix) /
`overwrite`.

### K11 — Last-admin protection
A restore must **not** be able to leave the system with no active admin. Before committing,
check: at least one `IsActive` user with `Role == Admin`. Otherwise abort the user section with a
clear error (the rest of the restore may proceed; the user section is refused).

### K16 — Restoring a user invalidates sessions
When `overwrite` changes an existing target user's `Role`, `IsActive` **or** `PasswordHash`, the
`SecurityStamp` must be incremented (invalidating existing JWTs — compare the `jti`/stamp logic),
and on a password or hash change `PasswordChangedAt = UtcNow` must be set as well. Otherwise old
sessions on the target instance survive a role downgrade or a password reset.

### K17 — Remap Folder.CreatedByUserId
`SharedWorkflowFolder.CreatedByUserId` is a user id and is remapped through `userMap` during the
structure restore. If the source user id cannot be resolved in the backup or the target, set it
to `null` deliberately rather than carrying over a foreign GUID.

### K12 — Partial restore and dependency resolution
If the caller picks only a subset (say only `workflows` without `machines`/`credentials`/
`folders`), broken references would result. The rules:
- **Export** automatically pulls in the hard dependencies of the selected sections (workflows →
  the machines/credentials/folders they reference), with a visible UI/CLI note about which
  sections were added.
- **Preview/restore** additionally validate every remappable reference: the target must either be
  contained in the backup **or** already present in the target database via a `sourceId` match.
  Unresolvable references produce a hard warning in the preview and **abort** the restore — no
  silently nulled `targetMachineId`/`credentialId`.

### K18 — Wrap the restore transaction in the EF execution strategy (added later, from field testing)
Postgres **and** SQL Server both configure a *retrying* execution strategy
(`NpgsqlRetryingExecutionStrategy` / `SqlServerRetryingExecutionStrategy`), which rejects a direct
`BeginTransactionAsync` with an `InvalidOperationException`. The complete restore unit (load +
validate + transaction) therefore runs inside
`db.Database.CreateExecutionStrategy().ExecuteAsync(...)`; every attempt clears the change tracker
and rebuilds the state so a retry starts clean. SQLite (tests) supplies a non-retrying strategy →
a single pass. **Test gap:** the tests execute the wrapper but do not enforce it (SQLite would not
need it) — the invariant is only held by a comment.

### K8 — Settings replacement with compensation
The database transaction covers database sections. Runtime settings are prepared before it,
written through an atomic `RuntimeOverridesWriter.ReplaceAll` immediately before the database
commit, and restored to their original content if the database commit fails. A compensation
failure aborts with a critical manual-recovery error. The result keeps its own settings line
because a service restart may still be required.

### K9 — Export only the runtime overrides, as raw file content
What is exported is **only** the **raw JSON content** of `appsettings.runtime.json` (the
database/file overrides), **not** `IConfigurationRoot` — that one is already decrypted and merged
with env, CLI and appsettings.json, which would drag host and environment secrets into the file.
So the file is read as text/JSON. Encrypted values within it (marked by
`EncryptingJsonConfigurationProvider`) are rewrapped at field level.

## CLI (`np backup …`) — headless
`BackupCommands` plus `NodePilotApiClient` methods, with DTOs duplicated in `Cli/Api/Dtos/`.
```
np backup export  --out sys.npbackup --sections all|workflows,machines,… --passphrase-env NP_BACKUP_PASS
np backup preview sys.npbackup --passphrase-env NP_BACKUP_PASS
np backup restore sys.npbackup --passphrase-env NP_BACKUP_PASS --policy workflows=skip,users=overwrite
```
The passphrase is **never** a flag (process list, shell history) — `--passphrase-env`,
`--passphrase-file` or an interactive prompt. This makes cron/scheduled DR backups possible.

## Frontend — `/backup` (admin only)
A sidebar entry in the admin group, the route behind `<AdminOnly>` and lazy-loaded, i18n namespace
`backup` (DE/EN). Banner: "Configuration backup — contains no execution history or audit log"
(the boundary).
- **Backup tab:** section checkboxes with counts (`/manifest`), passphrase plus confirmation and
  strength, button → `POST /export` → download.
- **Restore tab:** upload + required passphrase → authenticated `POST /preview` → per-section diff
  table → per-section policy dropdown →
  `POST /restore` → result summary (including the separate settings line, K8).

## Security & audit
Everything is admin-only. Audit after `SaveChanges`: `BACKUP_EXPORTED` (sections, counts, secrets
yes/no), `BACKUP_RESTORED` (policy, created/overwritten per section, settings result). The
passphrase never reaches the audit or the log. `RequestSizeLimit` plus a rate limit on restore. A
minimum passphrase length is enforced.

## Database / migration
**No schema change** — the backup only reads and writes existing tables.

## Tests (mandatory)
- Crypto units: round trip, wrong passphrase, complete-payload confidentiality, ciphertext/header
  tamper rejection and key separation.
- Backend: export→restore round trip with per-section equality; **ID remap** (a workflow with
  `targetMachineId`/`credentialId` points at the new ids after restore; `{{globals.X}}` unchanged
  — K13); `Machine.DefaultCredentialId` remap (K4); `Folder.ParentFolderId`/`CreatedByUserId` and
  grant remap; conflict policies; **partial restore with a missing dependency → abort** (K12);
  a user overwrite raises `SecurityStamp` and sets `PasswordChangedAt` (K16); last-admin protection
  (K11); the separate settings path (K8); RBAC (non-admin gets 403).
- CLI: WireMock for export/preview/restore.
- Frontend: BackupPage — section selection, passphrase validation and authenticated preview diff.

## Phases
1. Crypto (`PassphraseSecretProtector` + HKDF subkeys + MAC) + envelope (sourceId per section) +
   `IBackupPart` (all sections) + workflow export refactor (SecretHandling) + export dependency
   auto-include (K12) + `GET /manifest` + `POST /export` + CLI `export`.
2. `POST /preview` + `POST /restore` (two-phase ID remap K3/K13, conflict policy, dependency
   validation K12, user session invalidation K16, last-admin protection K11, settings separately
   K8) + CLI `preview`/`restore`.
3. Frontend `/backup` (both tabs) + navigation + i18n.
4. Hardening and docs (`deploy/README.md`, `docs/claude-reference.md`).

## Consequences
- A portable, encrypted configuration artifact; a restore onto a fresh application instance is
  possible when the surrounding database, files, configuration and key material are handled by
  the tested DR procedure.
- The context workflow export stays unchanged (redacted).
- No replacement for a full database backup — deliberately out of scope.
