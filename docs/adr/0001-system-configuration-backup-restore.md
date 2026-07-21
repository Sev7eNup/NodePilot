# ADR 0001 — System Configuration Backup & Restore

**Status:** Implemented (Phasen 1–4) — 2026-05-29
**Scope:** New top-level Backup feature (API + CLI + UI). Implementation in 4 phases.

## Kontext

NodePilot hat heute nur einen *Workflow*-Export (`/api/workflows/export`), der bewusst
Secrets **redigiert** (`WorkflowsControllerBase.RedactSecretsInDefinition`) — ein
*Teilen*-Artefakt, kein Restore-Artefakt. Für den produktiven Single-Node-Betrieb fehlt
ein Disaster-Recovery-Pfad: DB weg = alles neu klicken (Machines, Credentials, Globals,
Users, Settings, Folder-Sharing). Ein roher DB-Dump hilft nicht, weil Credential-Secrets
über `ISecretProtector` entweder per DPAPI (maschinen-/user-gebunden, **nicht portabel**)
oder per AES-GCM mit `Secrets:MasterKey` verschlüsselt sind.

## Entscheidung (Leitprinzip)

Trennung nach **Intent**, gemeinsamer Code darunter:

| | Kontext-Export (bleibt) | System-Backup (neu) |
|---|---|---|
| Zweck | *einen* Workflow teilen | DR der ganzen Konfiguration |
| Ort | Workflows-Seite | eigener Admin-Menüpunkt `/backup` |
| Secrets | redigiert | rewrapped hinter Passphrase |
| Umfang | nur Workflows | Workflows+Folders/Sharing, Machines, Credentials, Globals, Users, Settings |

Workflow-Export-Mapping wandert in einen geteilten Helper; `/api/workflows/export` und das
Backup rufen denselben Code mit unterschiedlichem **SecretHandling** auf. Kein dupliziertes
Mapping, kein Export-Button pro Menüpunkt.

### Abgrenzung — was das Backup NICHT ist

Es ist ein **System *Configuration* Backup**, kein Voll-DB-Backup. **Nicht** enthalten:
AuditLog, Execution-History, StepExecutions, SupportEvents, WorkflowVersions, Stats,
Idempotency-Keys. Das muss UI **und** Doku klar sagen. Für echte DB-Sicherung gilt weiterhin
der Postgres/SQL-Server-Backup-Pfad des DBAs.

## Datei-Format `nodepilot-system-backup/v1`

`.npbackup` (JSON). Struktur lesbar/diffbar; **nur Secret-Felder** sind passphrase-verschlüsselt.
Über die **gesamte** Datei läuft zusätzlich ein passphrase-basierter MAC (siehe Integrität).

```jsonc
{
  "schema": "nodepilot-system-backup/v1",
  "appVersion": "x.y.z",
  "createdAt": "…Z", "createdBy": "admin",
  "crypto": { "kdf":"PBKDF2-SHA256","iterations":600000,"salt":"<b64>",
              "subkeys":"HKDF-SHA256→{enc,mac,verifier}", "verifier":"<b64>" },
  "mac": "<b64 HMAC-SHA256(macKey) über kanonisches JSON ohne dieses Feld>",
  "sections": {
    // JEDES remapbare Item trägt "sourceId" (Original-Guid) — siehe K3.
    "folders":        { "structure":[ { "sourceId":"<guid>", … } ], "grants":[…] },  // getrennt! (K4)
    "users":          { "items":[ { "sourceId":"<guid>", …, "passwordHash": {"$enc":"…"} } ] },
    "credentials":    { "items":[ { "sourceId":"<guid>", …, "password": {"$enc":"…"} } ] },
    "machines":       { "items":[ { "sourceId":"<guid>", … } ] },
    "globalVariables":{ "items":[ { "sourceId":"<guid>", …, "value": {"$enc":"…"} | "plain" } ] },
    "workflows":      { "items":[ { "sourceId":"<guid>", "definition":{…} } ] },
    "settings":       { "runtimeJson": {…} }               // roher appsettings.runtime.json-Inhalt
  }
}
```

## Korrekturen ggü. Erstentwurf (verbindlich vor Phase 1)

### K1 — Kein neuer Interface-Typ
`ISecretProtector` existiert bereits in `NodePilot.Core/Interfaces`. Es wird **nur** eine neue
Implementierung `PassphraseSecretProtector` in `NodePilot.Data/Security/` angelegt (neben den
bestehenden Protectors). Kein Interface in `Data/Security`.

### K2 — Workflow-Secrets: SecretHandling-Enum statt bool
Workflow-Secrets liegen **inline in `DefinitionJson`** (`secret`, `apiKey`, `password`,
`authToken`, `bearer`, `connectionString` — siehe `SecretConfigKeys`). `redactSecrets:false`
darf **nicht** Klartext in die Backup-Datei schreiben. Der Workflow-Exporter bekommt:

```
enum SecretHandling { Redact, EncryptForBackup, PlainInternal }
```

- `Redact` — heutiges Verhalten, Kontext-Export/Teilen.
- `EncryptForBackup` — inline-Secret-Felder werden zu `{"$enc":…}` (Passphrase-Rewrap), Backup.
- `PlainInternal` — nur intern (z. B. Round-Trip-Tests), nie über die Wire.

`targetMachineId`/`credentialId` sind **keine** Secrets → kein `$enc`, sondern ID-Remap (K3).

### K3 — Restore braucht sourceId → targetId-Map (Pflicht)
By-name reicht nicht: Workflows/Machines/Credentials haben keine harte Unique-Garantie, und
Definitionen/Entities referenzieren GUIDs. **Jede remapbare Sektion** (Folders, Users,
Credentials, Machines, Global Variables, Workflows) trägt darum im Format eine explizite
`sourceId` (Original-Guid). Restore ist zwei-phasig:

1. **Anlegen:** je Ressource neue (oder bei `overwrite`/`skip` die Ziel-)Id vergeben,
   `Dictionary<Guid,Guid>` pro Typ füllen (`folderMap`, `userMap`, `credentialMap`,
   `machineMap`, `globalMap`, `workflowMap`). `globalMap` nur falls je per-Id referenziert.
2. **Referenzen umschreiben** über die Maps:
   - `ManagedMachine.DefaultCredentialId` → `credentialMap` (siehe Reihenfolge K4).
   - `SharedWorkflowFolder.ParentFolderId` → `folderMap`; `CreatedByUserId` → `userMap` (K17).
   - `SharedFolderPermission`: `FolderId` → `folderMap`, `GrantedByUserId` → `userMap`,
     `PrincipalKey` → `userMap` **nur wenn es eine User-Guid ist**; AD-Group-SIDs bleiben unverändert.
   - Workflow-Definition: **nur** `data.targetMachineId` → `machineMap` und `data.credentialId`
     → `credentialMap`, und **nur wenn der Feldwert ein parsebarer GUID-String ist** (K13).

Bei Konflikt-Policy `rename`/`skip` zeigt die Map auf die tatsächlich verwendete Ziel-Id, damit
Referenzen nicht ins Leere laufen.

### K13 — Workflow-ID-Rewrite eng gefasst
Der Definition-Rewrite remappt ausschließlich die bekannten Felder `data.targetMachineId` und
`data.credentialId` und **nur**, wenn der Wert per `Guid.TryParse` parsebar ist. Alles andere
bleibt byte-für-byte erhalten — insbesondere Template-Ausdrücke wie `{{globals.X}}`,
`{{step.output}}`, Node-Ids (`step-123`), Scripts und Edge-Conditions. Kein „such alle GUIDs“.

### K4 — Folder: Struktur und Grants trennen, Reihenfolge fix
Folder-Grants (`SharedFolderPermission.PrincipalKey` = User-Guid oder AD-SID) brauchen die User;
`ManagedMachine.DefaultCredentialId` braucht die Credentials → **Credentials vor Machines**.
`SharedWorkflowFolder.CreatedByUserId` ist eine User-Id und wird beim Struktur-Restore über
`userMap` remappt (K17) — deshalb Users vor Folder-Struktur. Reihenfolge (alles außer Settings in
**einer** DB-Transaktion):

```
1. Users                           5. Global Variables
2. Folder-Struktur (ohne Grants;   6. Workflows  (Refs via Maps — K13)
   CreatedByUserId via userMap)     7. Folder-Grants/Permissions (spät, remapped)
3. Credentials                     8. Settings  (separat, NICHT in der TX — K8)
4. Machines (DefaultCredentialId
   via credentialMap)
```

Alternative für Machines (falls eine Ordnung ohne Credentials-Vorbedingung gewünscht ist): erst
ohne `DefaultCredentialId` anlegen, nach Credentials patchen. Default ist die Reihenfolge oben.

### K5 — Whole-file-Integrität (MAC)
GCM-pro-Secret schützt nur verschlüsselte Felder. Klartext (Scripts, Hostnames, Rollen,
Settings-Struktur) wäre manipulierbar. Daher zusätzlich **passphrase-basierter HMAC-SHA256
über kanonisches JSON** der gesamten Datei (Feld `mac`).
- **Restore** verlangt Passphrase → verifiziert MAC **bevor** geschrieben wird. Mismatch → Abbruch.
- **Preview ohne Passphrase** bleibt möglich, liefert aber Status `integrityUnverified` und
  diffte nur Struktur/Counts/Namen — **keine** Secret-Identität (K10).

### K6 — Upload als multipart/form-data
`preview`/`restore` nehmen die Datei als `multipart/form-data` (Feld `file` + Feld `passphrase`),
**nicht** als JSON-Body mit eingebettetem File. Export bleibt JSON-Request → File-Download.

## Crypto — `PassphraseSecretProtector`
Gleiche Primitive wie der bestehende AES-GCM-Provider. Passphrase → PBKDF2-SHA256 (600k Iter.,
zufälliger Salt) → 256-bit Master-Secret.

**K14 — Key-Separation:** das PBKDF2-Ergebnis wird **nicht** direkt mehrfach verwendet. Per
HKDF-SHA256-Expand (distinkte `info`-Labels) drei Subkeys ableiten: `encKey` (AES-256-GCM der
`$enc`-Felder), `macKey` (Whole-file-HMAC, K5) und `verifierKey` (GCM des bekannten Tokens →
Passphrase-Check vor jedem Schreiben). Salt + Iterationen + `verifier` stehen im `crypto`-Header.

**Rewrap** exakt nach `CredentialStore.ReencryptAllCredentialsAsync`:
- Export: mit laufendem `ISecretProtector` entschlüsseln → mit Passphrase-Protector verschlüsseln.
- Restore: mit Passphrase-Protector entschlüsseln → mit **Ziel**-`ISecretProtector` neu verschlüsseln.
User-BCrypt-Hashes werden nicht re-hashed, nur als Feld hinter die Passphrase gelegt + 1:1 restored.

## Backend
`IBackupPart`-Implementierungen in `NodePilot.Api/Services/Backup/` je Ressource
(`FolderBackupPart`, `UserBackupPart`, `MachineBackupPart`, `CredentialBackupPart`,
`GlobalVariableBackupPart`, `WorkflowBackupPart`, `SettingsBackupPart`) mit
`Export / Preview / Restore(ConflictPolicy, IdMaps)`.

`BackupController` (`[Authorize(Roles="Admin")]`):

| Endpoint | Body | Zweck |
|---|---|---|
| `GET /api/backup/manifest` | — | Counts pro Sektion (UI-Checkboxen) |
| `POST /api/backup/export` | `{ sections[], passphrase }` | streamt `.npbackup` |
| `POST /api/backup/preview` | multipart `file` + `passphrase?` | Diff je Sektion + `integrityUnverified` |
| `POST /api/backup/restore` | multipart `file` + `passphrase` + `policy{}` | wendet an |

**Konflikt-Policy** (by-name-Match wie Import; Default `skip`): `skip` / `rename` (Suffix) / `overwrite`.

### K11 — Last-Admin-Schutz
Restore darf **nicht** dazu führen, dass danach kein aktiver Admin mehr existiert. Vor Commit
prüfen: ≥1 `IsActive` User mit `Role == Admin`. Sonst Abbruch der User-Sektion mit klarer
Fehlermeldung (Rest des Restores kann durchlaufen, User-Sektion wird verweigert).

### K16 — User-Restore invalidiert Sessions
Beim `overwrite` eines bestehenden Ziel-Users muss bei Änderung von `Role`, `IsActive` **oder**
`PasswordHash`: `SecurityStamp` inkrementieren (entwertet bestehende JWTs, vgl. `jti`/Stamp-Logik),
und bei Passwort-/Hash-Änderung zusätzlich `PasswordChangedAt = UtcNow` setzen. Sonst bleiben alte
Sessions der Zielinstanz nach einem Rollen-Downgrade oder Passwort-Reset gültig.

### K17 — Folder.CreatedByUserId remappen
`SharedWorkflowFolder.CreatedByUserId` ist eine User-Id und wird beim Struktur-Restore über
`userMap` remappt. Ist die Quell-User-Id nicht im Backup/Ziel auflösbar → bewusst auf `null`
setzen (nicht die fremde Guid übernehmen).

### K12 — Partial-Restore & Dependency-Auflösung
Wählt der Caller nur eine Teilmenge (z. B. nur `workflows` ohne `machines`/`credentials`/`folders`),
entstehen sonst kaputte Referenzen. Regeln:
- **Export** zieht harte Dependencies der gewählten Sektionen automatisch mit (Workflows →
  referenzierte Machines/Credentials/Folder), mit sichtbarem UI-/CLI-Hinweis welche Sektionen
  ergänzt wurden.
- **Preview/Restore** validiert zusätzlich jede remapbare Referenz: Ziel muss entweder im Backup
  enthalten **oder** in der Ziel-DB bereits per `sourceId`-Match vorhanden sein. Nicht auflösbare
  Refs → harte Warnung im Preview und **Abbruch** im Restore (kein stilles Null-Setzen von
  `targetMachineId`/`credentialId`).

### K18 — Restore-Transaktion in EF-Execution-Strategy kapseln (nachträglich, Field-Test)
Postgres **und** SQL Server konfigurieren eine *retrying* Execution-Strategy
(`NpgsqlRetryingExecutionStrategy` / `SqlServerRetryingExecutionStrategy`), die eine
direkte `BeginTransactionAsync` mit `InvalidOperationException` ablehnt. Die komplette
Restore-Einheit (Load + Validate + Transaktion) läuft daher in
`db.Database.CreateExecutionStrategy().ExecuteAsync(...)`; jeder Versuch leert den
ChangeTracker und baut den State neu auf, damit ein Retry sauber startet. SQLite (Tests)
liefert eine non-retrying Strategy → ein Durchlauf. **Test-Lücke:** der Wrapper wird von
den Tests ausgeführt, aber nicht erzwungen (SQLite bräuchte ihn nicht) — Invariante nur
per Kommentar gesichert.

### K8 — Settings als separater, nicht-atomarer Schritt
DB-Transaktion deckt nur DB-Sektionen. Settings-Restore läuft über `RuntimeOverridesWriter`
(File `appsettings.runtime.json`) → **nach** Commit der DB-TX, mit **eigener Ergebniszeile**
(success/fail unabhängig). Kein DDL-/Datei-Hotpatch.

### K9 — Settings-Export nur runtime-Overrides, roher Datei-Inhalt
Exportiert wird **nur** der **rohe JSON-Inhalt** von `appsettings.runtime.json` (die DB-/File-
Overrides), **nicht** `IConfigurationRoot` — denn der ist bereits entschlüsselt und mit
Env/CLI/appsettings.json gemerged; das würde Host-/Env-Secrets in die Datei ziehen. Die Datei
wird also als Text/JSON gelesen. Verschlüsselte Werte darin
(`EncryptingJsonConfigurationProvider`-Markierung) werden auf Feldebene rewrapped.

## CLI (`np backup …`) — headless
`BackupCommands` + `NodePilotApiClient`-Methoden, DTOs in `Cli/Api/Dtos/` dupliziert.
```
np backup export  --out sys.npbackup --sections all|workflows,machines,… --passphrase-env NP_BACKUP_PASS
np backup preview sys.npbackup --passphrase-env NP_BACKUP_PASS
np backup restore sys.npbackup --passphrase-env NP_BACKUP_PASS --policy workflows=skip,users=overwrite
```
Passphrase **nie** als Flag (Prozessliste/History) — `--passphrase-env`, `--passphrase-file` oder
interaktiver Prompt. Ermöglicht Cron-/Scheduled-DR-Backup.

## Frontend — `/backup` (Admin-only)
Sidebar-Eintrag in Admin-Gruppe, Route mit `<AdminOnly>` + lazy, i18n-Namespace `backup` (DE/EN).
Banner: „Konfigurations-Backup — enthält keine Ausführungshistorie/Audit“ (Abgrenzung).
- **Tab Backup:** Sektions-Checkboxen mit Counts (`/manifest`), Passphrase + Bestätigung + Stärke,
  Button → `POST /export` → Download.
- **Tab Restore:** Upload → `POST /preview` (Passphrase optional, sonst `integrityUnverified`-Hinweis)
  → Diff-Tabelle je Sektion → Policy-Dropdown je Sektion → `POST /restore` → Ergebnis-Summary
  (inkl. separater Settings-Zeile, K8).

## Security & Audit
Alles Admin-only. Audit nach `SaveChanges`: `BACKUP_EXPORTED` (Sektionen, Counts, Secrets ja/nein),
`BACKUP_RESTORED` (Policy, angelegt/überschrieben je Sektion, Settings-Resultat). Passphrase nie
ins Audit/Log. `RequestSizeLimit` + Rate-Limit auf Restore. Passphrase-Mindestlänge erzwungen.

## DB / Migration
**Keine Schema-Änderung** — Backup liest/schreibt nur bestehende Tabellen.

## Tests (Pflicht)
- Crypto-Unit: Round-Trip, falsche Passphrase, Tamper an Klartext → MAC-Fail, Tamper an `$enc` →
  GCM-Fail, Key-Separation (encKey ≠ macKey ≠ verifierKey).
- Backend: Export→Restore-Round-Trip mit Gleichheit je Sektion; **ID-Remap** (Workflow mit
  `targetMachineId`/`credentialId` zeigt nach Restore auf neue Ids; `{{globals.X}}` unverändert —
  K13); `Machine.DefaultCredentialId`-Remap (K4); `Folder.ParentFolderId`/`CreatedByUserId`- und
  Grant-Remap; Konflikt-Policies; **Partial-Restore mit fehlender Dependency → Abbruch** (K12);
  User-Overwrite erhöht `SecurityStamp` + setzt `PasswordChangedAt` (K16); Last-Admin-Schutz (K11);
  Settings-Separat-Pfad (K8); RBAC (Non-Admin 403).
- CLI: WireMock für export/preview/restore.
- Frontend: BackupPage — Sektionsauswahl, Passphrase-Validierung, Preview-Diff inkl.
  `integrityUnverified`-Status.

## Phasen
1. Crypto (`PassphraseSecretProtector` + HKDF-Subkeys + MAC) + Envelope (sourceId je Sektion) +
   `IBackupPart` (alle Sektionen) + Workflow-Export-Refactor (SecretHandling) + Export-Dependency-
   Auto-Include (K12) + `GET /manifest` + `POST /export` + CLI `export`.
2. `POST /preview` + `POST /restore` (zwei-phasiges ID-Remap K3/K13, Konflikt-Policy,
   Dependency-Validierung K12, User-Session-Invalidierung K16, Last-Admin-Schutz K11,
   Settings-separat K8) + CLI `preview`/`restore`.
3. Frontend `/backup` (beide Tabs) + Nav + i18n.
4. Hardening & Docs (`deploy/README.md`, `docs/claude-reference.md`).

## Konsequenzen
- Portables, sicheres DR-Artefakt; Restore auf frischer Maschine möglich (Passphrase genügt).
- Kontext-Workflow-Export bleibt unverändert (redigiert).
- Kein Voll-DB-Backup-Ersatz — bewusst abgegrenzt.
