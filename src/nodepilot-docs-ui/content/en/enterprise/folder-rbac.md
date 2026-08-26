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

## Selecting multiple workflows

Every row in the workflow list has a checkbox on the left, and the header checkbox selects all **visible** rows (those of the filtered folder). Holding `Shift` selects the range since the last click. As soon as something is selected, an action bar appears above the list with **Move**, **Enable**, **Disable**, **Export** and **Delete**.

The bar calls no bulk API; it runs the same single-workflow actions one after another. Each one therefore keeps its own permission check, its own edit-lock check and its own audit entry — a bulk action is never a way around a permission. That shapes how it behaves:

- A button is enabled only when the **whole** selection permits it; otherwise the tooltip names the reason. **Delete** requires the Admin role, everything else `FolderEditor`.
- **Enable** is blocked as soon as one selected workflow is checked out — `POST /enable` rejects any lock with `423`, including the caller's own. **Disable** ignores locks (kill switch).
- A failure does not stop the run: the remaining workflows are processed, a summary message names the failed ones, and exactly those stay selected — clicking again retries only them.
- **Move** works through a destination dialog (folders without write access are disabled) **or** by drag and drop: drag a selected row onto a folder in the tree and the whole selection follows. Workflows already in the destination folder are skipped.
- **Export** writes all selected workflows into **one** `nodepilot-workflow-export/v1` file — it can be read back unchanged via **Import**.

## Selecting and deleting several folders

Every row in the folder tree except **Root** carries a checkbox too — Root cannot be deleted. A plain click on the row still filters the workflow list; only the checkbox selects, and `Shift` extends over a range. Selection covers what is **visible**: collapsing a branch drops its sub-folders from the selection. That costs nothing, because deleting the parent takes the branch anyway.

Deletion includes **the contents**: sub-folders, the workflows inside them, and their execution history. The confirmation lists the affected folders with their workflow counts; the message afterwards reports the numbers the server actually deleted — the client cannot count folders it has no read permission on.

There is no bulk API here either: exactly one request per top-most folder. A selected folder that sits below another selected folder is dropped beforehand, because the parent's request already takes it. Two refusals come from the server: **423** when the subtree holds a workflow checked out by someone else, and **409** when something was moved into the subtree mid-delete. In both cases **nothing** is deleted.

The same applies to **Delete** in a single folder's right-click menu.

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
| `DELETE /api/shared-workflow-folders/{id}?recursive=true` | FolderEditor | Delete **with contents** (sub-folders + workflows); 423 if the subtree holds someone else's edit lock |
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
