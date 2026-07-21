import { api } from './client';

/**
 * RBAC org-level folder API. Shared folders are visible to everyone with at least
 * FolderViewer permission and govern who can read/run/edit the workflows inside them.
 */

export type SharedFolderRole = 'FolderViewer' | 'FolderOperator' | 'FolderEditor' | 'FolderAdmin';
export type FolderPrincipalType = 'User' | 'Role' | 'Group';
export const ACTIVE_DIRECTORY_AUTHORITY = 'urn:nodepilot:identity:active-directory';

export interface SharedFolderCapabilities {
  canRead: boolean;
  canRun: boolean;
  canEdit: boolean;
  canAdmin: boolean;
}

export interface SharedFolder {
  id: string;
  parentFolderId: string | null;
  name: string;
  path: string;
  depth: number;
  createdAt: string;
  createdByUserId: string | null;
  workflowCount: number;
  capabilities: SharedFolderCapabilities;
}

export interface SharedFolderPermission {
  id: string;
  folderId: string;
  principalType: FolderPrincipalType;
  /** For User-grants this is the user's Guid as canonical lowercase string
   *  (`Guid.ToString("D")`); for Group-grants it is a provider-stable group key. Mirrors the
   *  backend's `PrincipalKey` column after the PR0a rename. */
  principalKey: string;
  /** Exact group issuer namespace. Null for user grants. */
  principalAuthority?: string | null;
  /** Human-readable display name resolved server-side (Username for User-grants,
   *  group display name for Group-grants when a directory cache is configured).
   *  Null when no resolution is available — UI falls back to the raw key. */
  principalDisplayName: string | null;
  role: SharedFolderRole;
  grantedAt: string;
  grantedByUserId: string | null;
}

const base = '/shared-workflow-folders';

export const sharedFoldersApi = {
  list: () => api.get<SharedFolder[]>(base),
  create: (parentFolderId: string | null, name: string) =>
    api.post<SharedFolder>(base, { parentFolderId, name }),
  rename: (id: string, name: string) =>
    api.put<void>(`${base}/${id}`, { name }),
  move: (id: string, newParentFolderId: string | null) =>
    api.post<void>(`${base}/${id}/move`, { newParentFolderId }),
  delete: (id: string) => api.delete<void>(`${base}/${id}`),

  // Workflow → folder reassignment.
  moveWorkflowToFolder: (workflowId: string, targetFolderId: string) =>
    api.post<void>(`/workflows/${workflowId}/move-folder`, { targetFolderId }),

  // Permission management. Group identifiers are scoped by their exact directory authority.
  listPermissions: (folderId: string) =>
    api.get<SharedFolderPermission[]>(`${base}/${folderId}/permissions`),
  grantPermission: (
    folderId: string,
    principalType: FolderPrincipalType,
    principalKey: string,
    role: SharedFolderRole,
    principalAuthority?: string,
  ) =>
    api.post<SharedFolderPermission>(`${base}/${folderId}/permissions`, {
      principalType,
      principalKey,
      principalAuthority,
      role,
    }),
  updatePermission: (folderId: string, permissionId: string, role: SharedFolderRole) =>
    api.put<void>(`${base}/${folderId}/permissions/${permissionId}`, { role }),
  revokePermission: (folderId: string, permissionId: string) =>
    api.delete<void>(`${base}/${folderId}/permissions/${permissionId}`),
};

/**
 * Singleton root folder id — matches the hard-coded sentinel from the C# model.
 * Useful for the folder tree to highlight Root distinctly (no rename/move/delete).
 */
export const ROOT_FOLDER_ID = '00000000-0000-0000-0000-000000000001';
