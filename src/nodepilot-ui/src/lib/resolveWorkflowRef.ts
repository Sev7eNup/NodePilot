import type { Workflow } from '../types/api';
import { api, ApiError } from '../api/client';

const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * Resolves a `workflowNameOrId` reference (the string a startWorkflow node carries) into a
 * Workflow. A GUID goes to `GET /api/workflows/{id}`, any other string to the case-insensitive
 * `GET /api/workflows/by-name/{name}`. Templated refs (`{{variable}}`) resolve only at runtime
 * and return null without a request.
 *
 * Returns `null` on 404 so callers can tell "not found" from a real fetch error. The shared API
 * client owns authentication, stale-response protection and structured error handling.
 */
export async function resolveWorkflowRef(nameOrId: string): Promise<Workflow | null> {
  const trimmed = (nameOrId ?? '').trim();
  if (!trimmed) return null;
  if (trimmed.startsWith('{{')) return null;

  const path = GUID_PATTERN.test(trimmed)
    ? `/workflows/${trimmed}`
    : `/workflows/by-name/${encodeURIComponent(trimmed)}`;

  try {
    return await api.get<Workflow>(path);
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) return null;
    throw error;
  }
}
