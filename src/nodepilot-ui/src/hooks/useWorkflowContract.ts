import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import type { WorkflowContractResponse } from '../types/api';

/**
 * Fetches the calling-contract for a child workflow referenced by `workflowNameOrId`.
 *
 * Behavior:
 * - Empty / whitespace input → returns null contract, no fetch.
 * - Variable expression (`{{var}}` etc.) → returns null contract (resolves at runtime),
 *   no fetch. The caller falls back to the free-form ParameterTable.
 * - GUID-shaped input → fetches `/workflows/{id}/contract`.
 * - Other strings → fetches `/workflows/by-name/{name}/contract`. Resolution mirrors the
 *   engine (WorkflowNameResolver): exact-case wins, otherwise case-insensitive; an
 *   ambiguous name answers 409 server-side.
 * - 404 response → returns null contract (workflow doesn't exist), surfaced via `isNotFound`.
 *
 * Debounced 250ms so typing in the workflow-name field doesn't fire one request per
 * keystroke. The debounce window is short enough to feel instant on tab-out / paste.
 */
export function useWorkflowContract(workflowNameOrId: string) {
  const [debounced, setDebounced] = useState(workflowNameOrId);

  useEffect(() => {
    const handle = setTimeout(() => setDebounced(workflowNameOrId), 250);
    return () => clearTimeout(handle);
  }, [workflowNameOrId]);

  const trimmed = debounced.trim();
  const isVariable = trimmed.startsWith('{{');
  const isGuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(trimmed);
  const enabled = !!trimmed && !isVariable;

  const { data, isFetching, error } = useQuery({
    queryKey: ['workflow-contract', isGuid ? `id:${trimmed}` : `name:${trimmed}`],
    enabled,
    staleTime: 60_000,
    retry: false,  // 404 is the common "not found yet" path; no point in retrying
    queryFn: async (): Promise<WorkflowContractResponse | null> => {
      try {
        return isGuid
          ? await api.get<WorkflowContractResponse>(`/workflows/${trimmed}/contract`)
          : await api.get<WorkflowContractResponse>(`/workflows/by-name/${encodeURIComponent(trimmed)}/contract`);
      } catch (err) {
        // 404 → no contract is a normal state, not an error. The api client throws
        // `new Error(error || response.statusText)`, so 404 with empty body becomes
        // an Error("Not Found"). We swallow that one shape; anything else propagates.
        if (err instanceof Error && /not found/i.test(err.message)) return null;
        throw err;
      }
    },
  });

  return {
    contract: data ?? null,
    isLoading: enabled && isFetching,
    isVariableExpression: isVariable,
    isNotFound: enabled && data === null && !isFetching && !error,
    error,
  };
}
