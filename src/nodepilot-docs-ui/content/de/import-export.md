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
das rohe XML, `Content-Type: application/xml`, 300-MiB-Grenze; Ziel-Folder und RBAC wie bei
`/import`). Ebenso verfügbar als `np workflow import-scorch --file` und als MCP-Tool
`import_scorch_workflow`.

Was die Übersetzung leistet:

- **Job-Concurrency** — das `MaxParallelRequests` eines Runbooks wird unverändert zum
  Parallelitätslimit des Workflows. Das schließt den Wert `1` ein, der in Orchestrator „ein Lauf
  zur Zeit" bedeutet und dort der Default ist — ein importiertes Runbook behält damit sein
  Verhalten, statt still unbegrenzt zu werden. Weil dieser Default so verbreitet ist, kommen die
  meisten Runbooks limitiert an; der Importreport nennt die Anzahl, damit nach einer Migration
  nichts unbemerkt serialisiert. Fehlt der Wert oder liegt er außerhalb des Bereichs, wird ohne
  Limit importiert.
- **Aktivitäten** — rund vierzig SCOrch-Typnamen werden auf eine NodePilot-Aktivität abgebildet.
  Achtung: SCOrch schreibt nicht immer die Namen, die sein Designer zeigt — *Invoke Runbook* heißt
  auf der Leitung `Trigger Policy`, und dessen Argumente für das Kind-Runbook kommen als
  `startWorkflow.parameters` mit.
- **Programmaufrufe** — *Run Program* wird **immer** zu `startProgram`. Der Export unterscheidet
  bereits eindeutig zwischen einem externen Aufruf und einem eingebetteten Skript (*Run .Net Script*,
  das zu `runScript` wird); diese Unterscheidung wird übernommen und nicht am Inhalt des
  Programmfelds nachträglich in Frage gestellt. Was der Import leistet, ist das Füllen der beiden
  Node-Felder: hat SCOrch die Argumente im Programmfeld statt im eigenen Feld stehen, werden Programm
  und Argumente getrennt — auch in SCOrchs Kommandozeilen-Modus, wo ein `|` beide trennt. Eine
  Kommandozeile, die wirklich eine Shell braucht (Pipe in ein zweites Programm, Umleitung), läuft
  über `cmd.exe /C`, so wie SCOrch sie selbst ausführt; ein Launcher ohne Pfad wie `cmd` wird auf
  seinen absoluten Pfad vervollständigt, weil die Engine `PATH` nicht durchsucht. Ein Skript im
  Programmfeld — eine `.ps1`, eine `.vbs` — bekommt seinen echten Interpreter in `filePath`: die
  Engine startet über `CreateProcess`, das ein Skript gar nicht ausführen kann. Jede solche
  Rekonstruktion steht im Import-Bericht.
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
- **Sub-Runbook-Aufrufe** — ein Aufruf wird über den vollen Pfad, den SCOrch speichert, seinem Kind
  zugeordnet. Das zählt beim Import eines Gesamtbestands: SCOrch erlaubt zwei gleichnamige Runbooks
  in verschiedenen Ordnern, NodePilot nicht — eines wird beim Import umbenannt, und seine Aufrufer
  werden auf den tatsächlich vergebenen Namen umgebogen. Ein Aufruf auf ein Runbook, das weder in
  der Datei noch bereits in NodePilot liegt, wird gemeldet; er würde zur Laufzeit scheitern.
- **Ordner** — ein Export bringt seinen eigenen Baum mit, für Runbooks ebenso wie für globale
  Variablen, und beide werden unterhalb des gewählten Zielordners nachgebaut. Bereits vorhandene
  Ordner werden wiederverwendet (Groß-/Kleinschreibung wird dabei ignoriert), Namen über 120 Zeichen
  gekürzt, und ein Baum tiefer als NodePilots fünf Ebenen wird in die tiefste passende Ebene
  gefaltet — die letzten beiden Fälle werden gemeldet. Zusätzliche Rechte braucht das nicht: alles
  Erzeugte liegt unter dem Ziel, für das ohnehin Schreibrecht nötig ist, und erbt dessen Zugriff.
- **Layout** — der Graph behält seine eigene Anordnung. SCOrch-Koordinaten lassen sich nicht
  wörtlich übernehmen (dort kleine Icons, hier Karten), deshalb wird der gesamte Graph
  gleichmäßig skaliert, bis die Karten passen — eine Ähnlichkeitsabbildung, das Bild bleibt also
  das des Autors, nur größer. Anschließend werden die Links auf geschwungene Kurven gebracht: für
  eine rückwärts laufende Kante zeichnet der Designer einen eckigen Bogen unter beiden Knoten, und
  nach einer originalgetreuen Skalierung trifft das jedes Knotenpaar, das in derselben Spalte
  übereinandersteht. So ein Paar wird stattdessen oben/unten angedockt — das kostet keinerlei
  Bewegung; wo ein vertikaler Andock nicht hilft, rückt das Ziel nach rechts. Zeilen bleiben immer
  unberührt, und ein echter Rücksprung auf einen früheren Schritt behält seinen Bogen bewusst. Lässt
  sich die Anordnung gar nicht reproduzieren, sagt der Import es und legt den Graphen stattdessen
  von links nach rechts aus.

Was sie nicht leistet — und im Report benennt: eine Aktivität ohne NodePilot-Gegenstück, oder ein
Mapping, das eine Pflichteinstellung nicht füllen kann, wird zu einem **deaktivierten**
`log`-Platzhalter mit Original-Typname und -Properties. Credentials werden nie rekonstruiert
(SCOrch verschlüsselt sie). Importierte Workflows entstehen immer disabled. Warnungen vor dem
Aktivieren durchsehen.

## System-Configuration Backup (ADR 0001)

Ein Admin-only, portables **Konfigurations-Backup** mit Workflows + Folders/Sharing, Machines, Credentials, Globals + Global-Variable-Ordnern, Custom Activities, Alerting, Users und Settings. Es ist bewusst kein vollständiger Disaster-Recovery-Snapshot: Execution-History, Audit-Daten, Statistiken, die native Datenbank, Service-Konfiguration und Installationsdaten sind nicht enthalten. Diese Bestandteile separat sichern und den vollständigen Wiederanlauf testen.

Das einzige unterstützte Envelope ist `nodepilot-system-backup/v4` (`.npbackup`). Kann eine angeforderte Sektion nicht vollständig exportiert werden, bricht der Export ab, statt ein Teil-Backup zu erzeugen. Ein Workflow-Export zieht Custom Activities automatisch als harte Abhängigkeit mit.

### Secret-Handling

Der komplette Konfigurations-Payload inklusive Metadaten und Sektionsliste wird mit der Passphrase verschlüsselt und authentifiziert (PBKDF2→HKDF→AES-GCM). Sensitive Felder behalten zusätzlich ihren Schutz innerhalb der Sektionen. Metadaten und Vorschau sind erst nach erfolgreicher authentifizierter Entschlüsselung verfügbar.

### Restore

- Die Vorschau benötigt die Passphrase und wird erst angezeigt, nachdem das komplette Archiv authentifiziert und entschlüsselt wurde.
- Validiert Refs (Abbruch bei unresolvable).
- Läuft in EF-Execution-Strategy-gekapselter Transaktion in Abhängigkeitsreihenfolge mit ID-Remap.
- Bricht den gesamten Restore ab und rollt ihn zurück, wenn eine gewählte Sektion eine Warnung erzeugt oder nicht vollständig wiederhergestellt werden kann; Settings-Dateiänderungen werden bei einem fehlgeschlagenen DB-Commit kompensiert.
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
