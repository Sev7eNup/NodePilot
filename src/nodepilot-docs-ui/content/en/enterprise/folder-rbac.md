# Folder RBAC (stage A)

Folder RBAC limits access to workflows based on their shared folder. The folders form a tree with a default maximum depth of five levels. Permissions are assigned to users or directory groups.

The four folder roles build on each other:

```text
FolderViewer < FolderOperator < FolderEditor < FolderAdmin
```

Folder RBAC is active by default. Existing users receive a permission on the root folder during an upgrade.

## Rules

- **Permissions inherit downwards:** `FolderEditor` on `/Finance` applies to `/Finance/Reports` and deeper without further grants.
- **Highest role wins:** explicit grants on subfolders do **not** override — editor on `/Finance` + viewer on `/Finance/Reports` → editor applies everywhere.
- **A global admin** bypasses everything; global operators/viewers are **capped** by their `UserRole` — a viewer with FolderAdmin gets no run/edit/admin rights.
- **Existence hiding:** unreadable workflows return `404` instead of `403`, so their existence is not revealed.
- **Capabilities per workflow** (`canRead`, `canRun`, `canEdit`, `canAdmin`) in list/detail responses — the UI only shows buttons where the user can actually use them.
- **Sub-workflow authorization at runtime:** when workflow A starts workflow B, the engine checks the effective principal's read permission on B's folder.
- **SignalR group routing:** execution events only reach the hub groups of users who may read the workflow.
- **Authority-scoped groups:** `PrincipalType=Group` stores `PrincipalAuthority` plus `PrincipalKey`. AD uses the canonical AD authority and a Windows SID; OIDC/SCIM uses the exact HTTPS issuer and the opaque group ID. Groups are evaluated exclusively with server-side membership snapshots, never from JWT claims.

## Granting permissions in the UI

On the **Workflows** page there are two routes to a folder's permissions dialog — both appear only if the caller has `FolderAdmin` on that folder (global admins always):

- **Right-click** the folder in the tree → **Permissions…**. This also works on the root folder `\`, which can be neither renamed nor deleted and therefore has no other context-menu entry.
- **Click the folder** (select it) → the **Permissions…** button at the bottom of the folder card.

## Default mapping (migration + creation)

| Global user role | Folder permission on the root |
|---|---|
| Admin | none (global bypass) |
| Operator | FolderEditor |
| Viewer | FolderViewer |

## API (RBAC-specific)

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /api/shared-workflow-folders` | Authenticated | The folder tree (filtered to readable folders + capabilities) |
| `POST /api/shared-workflow-folders` | FolderEditor on the parent | New subfolder |
| `PUT /api/shared-workflow-folders/{id}` | FolderEditor | Rename |
| `POST /api/shared-workflow-folders/{id}/move` | FolderEditor on source + target | Move |
| `DELETE /api/shared-workflow-folders/{id}` | FolderEditor (empty folders only) | Delete |
| `POST /api/workflows/{id}/move-folder` | FolderEditor on source + target | Move a workflow |
| `GET /api/shared-workflow-folders/{id}/permissions` | FolderAdmin | List grants |
| `POST /api/shared-workflow-folders/{id}/permissions` | FolderAdmin | Grant |
| `PUT /api/shared-workflow-folders/{id}/permissions/{permId}` | FolderAdmin | Update a grant |
| `DELETE /api/shared-workflow-folders/{id}/permissions/{permId}` | FolderAdmin | Revoke a grant |

`POST /api/workflows` accepts an optional `FolderId` (default: root); the server checks edit permission on the target folder and rejects with 403 otherwise.

## Configuration

None. RBAC is always active. Folders and grants are created by a global admin through the UI or the API.

## Out of scope (V1)

- **Role principals** — `PrincipalType=Role` stays reserved; user and group principals are available.
- **Per-workflow permissions** — V1 grants only at folder level. To isolate a workflow, create a subfolder.
- **Per-folder audit filters** — `GET /api/audit` is admin-only and global today. A per-folder audit view is V2.
