# Folder-RBAC (Stage A)

Folder-RBAC begrenzt den Zugriff auf Workflows anhand ihrer Shared Folder. Die Ordner bilden einen Baum mit einer standardmäßigen Maximaltiefe von fünf Ebenen. Berechtigungen werden Benutzern oder Verzeichnisgruppen zugewiesen.

Die vier Folder-Rollen bauen aufeinander auf:

```text
FolderViewer < FolderOperator < FolderEditor < FolderAdmin
```

Folder-RBAC ist standardmäßig aktiv. Bestehende Benutzer erhalten beim Upgrade eine Berechtigung auf dem Root-Ordner.

## Regeln

- **Permissions vererben nach unten:** `FolderEditor` auf `/Finance` gilt für `/Finance/Reports` und tiefer ohne weitere Grants.
- **Highest-Role-Wins:** explizite Grants auf Subord-Ordnern **override** nicht — Editor auf `/Finance` + Viewer auf `/Finance/Reports` → Editor gilt überall.
- **Global Admin** bypassed alles; globale Operator/Viewer werden durch ihre `UserRole` **gecappt** — ein Viewer mit FolderAdmin bekommt keine Run/Edit/Admin-Rechte.
- **Existence Hiding:** Nicht lesbare Workflows liefern `404` statt `403`, damit ihre Existenz nicht offengelegt wird.
- **Capabilities pro Workflow** (`canRead`, `canRun`, `canEdit`, `canAdmin`) in List/Detail-Responses — die UI zeigt Buttons nur, wo der User sie nutzen kann.
- **Sub-Workflow-Authorization zur Laufzeit:** wenn Workflow A Workflow B startet, prüft die Engine die Read-Permission des effektiven Principals auf B's Folder.
- **SignalR-Group-Routing:** Execution-Events landen nur in Hub-Groups von Usern, die den Workflow lesen dürfen.
- **Authority-scoped Gruppen:** `PrincipalType=Group` speichert `PrincipalAuthority` plus `PrincipalKey`. AD nutzt die kanonische AD-Authority und eine Windows-SID; OIDC/SCIM nutzt den exakten HTTPS-Issuer und die opake Gruppen-ID. Gruppen werden ausschließlich mit serverseitigen Membership-Snapshots ausgewertet, nie aus JWT-Claims.

## Grants in der UI vergeben

Auf der Seite **Workflows** gibt es zwei Wege zum Berechtigungs-Dialog eines Ordners — beide erscheinen nur, wenn der Aufrufer `FolderAdmin` auf diesem Ordner hat (globale Admins immer):

- **Rechtsklick** auf den Ordner im Baum → **Berechtigungen…**. Funktioniert auch auf dem Root-Ordner `\`, der weder umbenannt noch gelöscht werden kann und deshalb sonst keinen Kontextmenü-Eintrag hat.
- **Ordner anklicken** (auswählen) → Button **Berechtigungen…** am unteren Rand der Ordner-Karte.

## Workflows mehrfach auswählen

Jede Zeile der Workflow-Liste hat links eine Checkbox, die Kopfzeile wählt alle **sichtbaren** Zeilen (also die des gefilterten Ordners). Mit gedrückter `Shift`-Taste wird der Bereich seit dem letzten Klick markiert. Sobald etwas ausgewählt ist, erscheint über der Liste eine Aktionsleiste mit **Verschieben**, **Aktivieren**, **Deaktivieren**, **Exportieren** und **Löschen**.

Die Leiste ruft keine Sammel-API auf, sondern führt dieselben Einzelaktionen nacheinander aus. Jede einzelne behält damit ihre Rechteprüfung, ihren Edit-Lock-Check und ihren Audit-Eintrag — eine Sammelaktion ist nie ein Weg an einer Berechtigung vorbei. Daraus folgt die Bedienlogik:

- Ein Button ist nur aktiv, wenn die **gesamte** Auswahl ihn zulässt; sonst nennt der Tooltip den Grund. **Löschen** verlangt die Admin-Rolle, alles andere `FolderEditor`.
- **Aktivieren** ist gesperrt, sobald ein ausgewählter Workflow ausgecheckt ist — `POST /enable` weist jeden Lock mit `423` ab, auch den eigenen. **Deaktivieren** ignoriert Locks (Kill-Switch).
- Ein Fehlschlag stoppt den Lauf nicht: die übrigen Workflows werden fertig verarbeitet, eine Sammelmeldung nennt die gescheiterten namentlich, und genau diese bleiben ausgewählt — ein erneuter Klick wiederholt nur sie.
- **Verschieben** geht über einen Zielordner-Dialog (Ordner ohne Schreibrecht sind deaktiviert) **oder** per Drag & Drop: zieht man eine ausgewählte Zeile auf einen Ordner im Baum, wandert die ganze Auswahl mit. Workflows, die bereits im Zielordner liegen, werden übersprungen.
- **Exportieren** legt alle ausgewählten Workflows in **eine** Datei im Format `nodepilot-workflow-export/v1` — sie lässt sich über **Importieren** unverändert wieder einlesen.

## Ordner mehrfach auswählen und löschen

Im Ordnerbaum trägt jede Zeile außer **Root** ebenfalls eine Checkbox — Root lässt sich nicht löschen. Ein normaler Klick auf die Zeile filtert weiterhin die Workflow-Liste; erst die Checkbox wählt aus, mit `Shift` über einen Bereich. Ausgewählt wird immer nur, was **sichtbar** ist: klappt man einen Ast ein, fallen dessen Unterordner aus der Auswahl. Das kostet nichts, denn wer den Elternordner löscht, nimmt den Ast ohnehin mit.

Gelöscht wird **samt Inhalt**: Unterordner, die darin liegenden Workflows und deren Ausführungshistorie. Der Bestätigungsdialog listet die betroffenen Ordner mit ihrer Workflow-Zahl auf; die Meldung danach nennt die Zahlen, die der Server tatsächlich gelöscht hat — der Client kann Ordner ohne Leserecht nicht mitzählen.

Auch hier läuft keine Sammel-API: pro oberstem Ordner geht genau ein Request. Ist ein ausgewählter Ordner Nachfahre eines anderen ausgewählten, wird er vorher aussortiert, weil ihn der Request des Elternordners bereits mitnimmt. Zwei Abbruchgründe kommen vom Server: **423**, wenn im Subtree ein Workflow von jemand anderem ausgecheckt ist, und **409**, wenn während des Löschens etwas in den Subtree gelegt wurde. In beiden Fällen wird **nichts** gelöscht.

Dasselbe gilt für den Eintrag **Löschen** im Rechtsklick-Menü eines einzelnen Ordners.

## Default-Mapping (Migration + Create)

| Globale UserRole | Folder-Permission auf Root |
|---|---|
| Admin | none (global bypass) |
| Operator | FolderEditor |
| Viewer | FolderViewer |

## API (RBAC-spezifisch)

| Endpoint | Auth | Zweck |
|---|---|---|
| `GET /api/shared-workflow-folders` | Authenticated | Folder-Tree (gefiltert auf lesbare Folders + Capabilities) |
| `POST /api/shared-workflow-folders` | FolderEditor auf Parent | Neuer Sub-Folder |
| `PUT /api/shared-workflow-folders/{id}` | FolderEditor | Rename |
| `POST /api/shared-workflow-folders/{id}/move` | FolderEditor auf Source + Target | Move |
| `DELETE /api/shared-workflow-folders/{id}` | FolderEditor (nur leere Folders) | Delete |
| `DELETE /api/shared-workflow-folders/{id}?recursive=true` | FolderEditor | Delete **samt Inhalt** (Unterordner + Workflows); 423 bei fremdem Edit-Lock im Subtree |
| `POST /api/workflows/{id}/move-folder` | FolderEditor auf Source + Target | Workflow umsortieren |
| `GET /api/shared-workflow-folders/{id}/permissions` | FolderAdmin | Grants auflisten |
| `POST /api/shared-workflow-folders/{id}/permissions` | FolderAdmin | Grant |
| `PUT /api/shared-workflow-folders/{id}/permissions/{permId}` | FolderAdmin | Grant updaten |
| `DELETE /api/shared-workflow-folders/{id}/permissions/{permId}` | FolderAdmin | Grant widerrufen |

`POST /api/workflows` akzeptiert optionales `FolderId` (default Root); der Server prüft Edit auf dem Target-Folder und rejectet mit 403 sonst.

## Konfiguration

Keine. RBAC ist immer aktiv. Folders + Grants werden vom globalen Admin via UI oder API angelegt.

## Out of scope (V1)

- **Role-Principals** — `PrincipalType=Role` bleibt reserviert; User- und Group-Principals sind verfügbar.
- **Per-Workflow-Permissions** — V1 granted nur auf Folder-Ebene. Zur Isolation eines Workflows: Sub-Folder anlegen.
- **Per-Folder-Audit-Filter** — `GET /api/audit` ist heute Admin-only und global. Per-Folder-Audit-View ist V2.
