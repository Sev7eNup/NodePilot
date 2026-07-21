import { useAuthStore } from '../stores/authStore';

export type Role = 'Admin' | 'Operator' | 'Viewer';

/**
 * Client-side mirror of the server role matrix (see CLAUDE.md §Autorisierung).
 *
 * These flags only control UI affordances — hide/disable buttons so Viewers
 * don't see actions that will 403. The server enforces the actual check; a
 * forged client that bypasses these booleans still gets blocked at the API.
 * So: keep them in sync with the server matrix, but don't treat them as a
 * security boundary.
 */
export function useRole() {
  const role = (useAuthStore((s) => s.role) ?? 'Viewer') as Role;

  const isAdmin = role === 'Admin';
  const isOperator = role === 'Operator';
  const isViewer = role === 'Viewer';

  /** Admin + Operator: create/edit workflows, machines, credentials, execute, cancel. */
  const canWrite = isAdmin || isOperator;
  /** Admin only: delete workflows / machines / credentials, manage globals, users, audit. */
  const canDelete = isAdmin;
  /** Admin only: user management, audit log, write global variables. */
  const canAdmin = isAdmin;

  return { role, isAdmin, isOperator, isViewer, canWrite, canDelete, canAdmin };
}
