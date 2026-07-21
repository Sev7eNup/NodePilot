import type { Workflow } from '../types/api';

const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * Resolves a `workflowNameOrId` reference (the same string a startWorkflow node carries) into
 * a Workflow object. Returns `null` on 404 — caller distinguishes "not found" from genuine
 * fetch errors so the preview modal can show a friendly fallback instead of a generic toast.
 *
 * - Looks like a GUID → `GET /api/workflows/{id}`.
 * - Otherwise         → `GET /api/workflows/by-name/{name}` (case-insensitive lookup).
 *
 * Templated refs (`{{variable}}`) are returned as null without hitting the network — they
 * resolve only at runtime, so a design-time preview can't follow them.
 *
 * Uses fetch directly (rather than the shared `api` client) because we need to inspect the
 * response status code: the shared client throws a generic Error on any non-2xx, so 404 vs
 * 500 would be indistinguishable upstream. Auth still works through the httpOnly cookie that
 * `credentials: 'include'` ships automatically.
 */
export async function resolveWorkflowRef(nameOrId: string): Promise<Workflow | null> {
  const trimmed = (nameOrId ?? '').trim();
  if (!trimmed) return null;
  if (trimmed.startsWith('{{')) return null;

  const path = GUID_PATTERN.test(trimmed)
    ? `/api/workflows/${trimmed}`
    : `/api/workflows/by-name/${encodeURIComponent(trimmed)}`;

  const response = await fetch(path, { credentials: 'include' });
  if (response.status === 404) return null;
  if (response.status === 401) {
    if (typeof window !== 'undefined' && !globalThis.location.pathname.startsWith('/login')) {
      globalThis.location.href = '/login';
    }
    throw new Error('Unauthorized');
  }
  if (!response.ok) throw new Error(`Workflow lookup failed: ${response.status} ${response.statusText}`);
  return response.json() as Promise<Workflow>;
}
