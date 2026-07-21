import { api } from './client';

/**
 * Organizational folder tree for global variables. Purely cosmetic — a global's identity in
 * `{{globals.NAME}}` templates is its bare, globally unique name, so a folder never changes how
 * it resolves. Unlike shared-workflow-folders there is no per-folder RBAC: folder management is
 * Admin-gated wholesale (mirrors the rest of the globals surface).
 */
export interface GlobalFolder {
  id: string;
  parentFolderId: string | null;
  name: string;
  path: string;
  depth: number;
  createdAt: string;
  createdByUserId: string | null;
  /** Number of variables directly in this folder (descendants excluded). */
  variableCount: number;
}

const base = '/global-variable-folders';

export const globalFoldersApi = {
  list: () => api.get<GlobalFolder[]>(base),
  create: (parentFolderId: string | null, name: string) =>
    api.post<GlobalFolder>(base, { parentFolderId, name }),
  rename: (id: string, name: string) => api.put<void>(`${base}/${id}`, { name }),
  move: (id: string, newParentFolderId: string | null) =>
    api.post<void>(`${base}/${id}/move`, { newParentFolderId }),
  delete: (id: string) => api.delete<void>(`${base}/${id}`),

  // Variable → folder reassignment.
  moveVariableToFolder: (variableId: string, folderId: string) =>
    api.post<void>(`/global-variables/${variableId}/move-folder`, { folderId }),
};

/**
 * Singleton Root folder id — mirrors the hard-coded C# sentinel
 * (`GlobalVariableFolder.RootFolderId`, …0002). Distinct from the shared-workflow-folder Root
 * (…0001). Used by the tree to render Root specially (no rename/move/delete).
 */
export const ROOT_FOLDER_ID = '00000000-0000-0000-0000-000000000002';

/** MIME used when dragging a variable row onto a folder in the tree. */
export const GLOBAL_VARIABLE_DRAG_MIME = 'application/x-nodepilot-global-variable';
