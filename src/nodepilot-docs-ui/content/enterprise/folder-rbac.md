# Folder-RBAC (Stage A)

Workflows leben in **Shared Folders** (Baum, default max Tiefe 5). Pro Folder können Permissions an Benutzer oder Directory-Gruppen vergeben werden — vier Rollen, additiv: `FolderViewer < FolderOperator < FolderEditor < FolderAdmin`.

Default: **aktiv** (alle existierenden User auf Root). Eine reine Single-Node-Installation verhält sich unverändert.

## Regeln

- **Permissions vererben nach unten:** `FolderEditor` auf `/Finance` gilt für `/Finance/Reports` und tiefer ohne weitere Grants.
- **Highest-Role-Wins:** explizite Grants auf Subord-Ordnern **override** nicht — Editor auf `/Finance` + Viewer auf `/Finance/Reports` → Editor gilt überall.
- **Global Admin** bypassed alles; globale Operator/Viewer werden durch ihre `UserRole` **gecappt** — ein Viewer mit FolderAdmin bekommt keine Run/Edit/Admin-Rechte.
- **Existence Hiding:** wer einen Workflow nicht lesen darf, bekommt `404` (nicht `403`) — sonst leakt die Existenz.
- **Capabilities pro Workflow** (`canRead`, `canRun`, `canEdit`, `canAdmin`) in List/Detail-Responses — die UI zeigt Buttons nur, wo der User sie nutzen kann.
- **Sub-Workflow-Authorization zur Laufzeit:** wenn Workflow A Workflow B startet, prüft die Engine die Read-Permission des effektiven Principals auf B's Folder.
- **SignalR-Group-Routing:** Execution-Events landen nur in Hub-Groups von Usern, die den Workflow lesen dürfen.
- **Authority-scoped Gruppen:** `PrincipalType=Group` speichert `PrincipalAuthority` plus `PrincipalKey`. AD nutzt die kanonische AD-Authority und eine Windows-SID; OIDC/SCIM nutzt den exakten HTTPS-Issuer und die opake Gruppen-ID. Gruppen werden ausschließlich mit serverseitigen Membership-Snapshots ausgewertet, nie aus JWT-Claims.

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
