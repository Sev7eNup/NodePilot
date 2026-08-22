# Import / Export & Backup

NodePilot verwendet zwei getrennte Exportformate:

- **Workflow-Export:** Austausch einzelner oder mehrerer Workflow-Definitionen.
- **System-Configuration-Backup:** Wiederherstellung einer vollständigen Konfiguration im Disaster-Recovery-Fall.

## Workflow Import/Export

| Endpoint | Zweck |
|---|---|
| `GET /{id}/export` | Einzel-Workflow-Export |
| `GET /export` | Bulk-Export |
| `POST /import` | Import — erzeugt neue Einträge, Namenskollision → Suffix `" (Imported 2)"`. Ziel-Folder via `?folderId=` (fehlt → Root); RBAC = Edit-Recht auf dem Zielordner (UI: aktuell selektierter Folder, CLI: `--target-folder`, MCP: `folderId`-Param). |
| `POST /import-scorch` | SCOrch-Import |

Envelope: `nodepilot-workflow-export/v1`. **Secrets werden hier redigiert** (`***`) — Teilen-Artefakt, kein DR.

### SCOrch-Import

`POST /import-scorch` liest das native `.ois_export`-XML von System Center Orchestrator (Body =
das rohe XML, `Content-Type: application/xml`, 50-MiB-Grenze; Ziel-Folder und RBAC wie bei
`/import`). Ebenso verfügbar als `np workflow import-scorch --file` und als MCP-Tool
`import_scorch_workflow`.

Was die Übersetzung leistet:

- **Aktivitäten** — rund vierzig SCOrch-Typnamen werden auf eine NodePilot-Aktivität abgebildet.
  Achtung: SCOrch schreibt nicht immer die Namen, die sein Designer zeigt — *Invoke Runbook* heißt
  auf der Leitung `Trigger Policy`, und dessen Argumente für das Kind-Runbook kommen als
  `startWorkflow.parameters` mit.
- **Published Data** — `` \`d.T.~Vb/{GUID}\`d.T.~Vb/ `` und `` \`d.T.~Ed/{GUID}.feld\`d.T.~Ed/ ``
  werden zu `{{globals.Name}}` bzw. `{{step.param.feld}}` und lösen über eine lesbare
  `outputVariable` auf, die aus dem Aktivitätsnamen abgeleitet wird. Feldnamen werden mit übersetzt,
  wo beide Produkte denselben Wert anders nennen (`Query XML`s `queryResult` ist `xmlQuery`s
  `result`); ein Wert ohne NodePilot-Gegenstück wird gemeldet, statt auf den nächstbesten Namen
  gebogen zu werden.
- **Links** — Erfolgs-/Fehler-Links werden zum Kürzel `stepId.success` / `stepId.failed`;
  `TRIGGERS`-Filter werden zu einer `conditionExpression`, verknüpft nach der ALLE/EINE-Einstellung
  des Links. Ein Filter, der einen Wert liest, den seine Quelle nicht publiziert, wird gemeldet —
  eine solche Kante würde nie greifen.
- **Verzweigungen** — *Compare Values* wird zu einem `decision`; die Links, die sein Ergebnis lesen,
  werden auf dessen Case umgebogen, damit der Zweig weiter verzweigt.
- **Trigger** — ein Runbook ohne eigenen Trigger (von einem anderen Runbook aufgerufene brauchen
  keinen) bekommt einen manuellen Trigger auf seine Einstiegsaktivitäten. Ohne Trigger-Node hat ein
  NodePilot-Workflow keinen Root und scheitert bei jedem Lauf.
- **Layout** — SCOrch-Koordinaten werden durch ein Layered-Layout im NodePilot-Knotenabstand
  ersetzt; die ursprüngliche vertikale Reihenfolge innerhalb einer Spalte bleibt erhalten.

Was sie nicht leistet — und im Report benennt: eine Aktivität ohne NodePilot-Gegenstück, oder ein
Mapping, das eine Pflichteinstellung nicht füllen kann, wird zu einem **deaktivierten**
`log`-Platzhalter mit Original-Typname und -Properties. Credentials werden nie rekonstruiert
(SCOrch verschlüsselt sie). Importierte Workflows entstehen immer disabled. Warnungen vor dem
Aktivieren durchsehen.

## System-Configuration Backup (ADR 0001)

Voller DR-Snapshot der Konfiguration: Workflows + Folders/Sharing, Machines, Credentials, Globals + Global-Variable-Ordner, Custom Activities, Alerting, Users, Settings. **Nicht enthalten:** Execution-History, Audit, Stats. Admin-only. Envelope `nodepilot-system-backup/v3` (`.npbackup`) — v2 ergänzte die `alerting`-Sektion; v3 schützt vollständige Workflowdefinitionen mit `$encDefinition` sowie Custom-Activity-Skripte und Eingabe-Defaults mit `$enc`. Ein Workflow-Export zieht Custom Activities automatisch als harte Abhängigkeit mit. Der Reader akzeptiert v1, v2 und v3 (inklusive alter Plaintext-Custom-Activity-Felder), geschrieben wird ausschließlich v3. Ältere Builds lehnen v3 sichtbar ab.

### Secret-Handling

Secrets per **Passphrase-Rewrap** (PBKDF2→HKDF→AES-GCM) + Whole-file-HMAC. Geteilte Secret-Logik mit dem Workflow-Export via `WorkflowDefinitionSecretRewriter` (`SecretHandling`).

### Restore

- **Vorschau läuft beim Dateiauswählen automatisch** — die Diff-Tabelle steht direkt da. Ohne
  Passphrase ist es die Struktur-Vorschau (Integrität ungeprüft); nach Eingabe der Passphrase
  erneut auf „Vorschau" klicken, um zusätzlich die Integrität zu prüfen.
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
