import type { Workflow } from '../types/api';
import {
  assertAuthBoundaryGenerationCurrent,
  captureAuthBoundaryGeneration,
  handleUnauthorizedAuthBoundary,
} from '../security/authBoundary';

const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * Resolves a `workflowNameOrId` reference (the string a startWorkflow node carries) into a
 * Workflow. A GUID goes to `GET /api/workflows/{id}`, any other string to the case-insensitive
 * `GET /api/workflows/by-name/{name}`. Templated refs (`{{variable}}`) resolve only at runtime
 * and return null without a request.
 *
 * Returns `null` on 404 so callers can tell "not found" from a real fetch error. Uses `fetch`
 * instead of the shared `api` client, which hides the status code behind a generic Error; auth
 * rides on the httpOnly cookie sent by `credentials: 'include'`.
 */
export async function resolveWorkflowRef(nameOrId: string): Promise<Workflow | null> {
  const trimmed = (nameOrId ?? '').trim();
  if (!trimmed) return null;
  if (trimmed.startsWith('{{')) return null;

  const path = GUID_PATTERN.test(trimmed)
    ? `/api/workflows/${trimmed}`
    : `/api/workflows/by-name/${encodeURIComponent(trimmed)}`;

  const authBoundaryGeneration = captureAuthBoundaryGeneration();
  const response = await fetch(path, { credentials: 'include' });
  assertAuthBoundaryGenerationCurrent(authBoundaryGeneration);
  if (response.status === 404) return null;
  if (response.status === 401) {
    handleUnauthorizedAuthBoundary();
    if (typeof window !== 'undefined' && !globalThis.location.pathname.startsWith('/login')) {
      globalThis.location.href = '/login';
    }
    throw new Error('Unauthorized');
  }
  if (!response.ok) throw new Error(`Workflow lookup failed: ${response.status} ${response.statusText}`);
  const workflow = await response.json() as Workflow;
  assertAuthBoundaryGenerationCurrent(authBoundaryGeneration);
  return workflow;
}
