import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api, ApiError } from '../api/client';
import type { WorkflowContractResponse } from '../types/api';

/**
 * Fetches the calling contract for a child workflow referenced by `workflowNameOrId`.
 *
 * Behavior:
 * - Empty / whitespace input: returns a null contract, no fetch.
 * - Variable expression (`{{var}}` etc.): returns a null contract (it resolves at runtime),
 *   no fetch. The caller falls back to the free-form ParameterTable.
 * - GUID-shaped input: fetches `/workflows/{id}/contract`.
 * - Other strings: fetches `/workflows/by-name/{name}/contract`. Resolution mirrors the
 *   engine (WorkflowNameResolver): exact-case wins, otherwise case-insensitive; an
 *   ambiguous name answers 409 server-side.
 * - 404 response: returns a null contract (no such workflow), surfaced via `isNotFound`.
 *
 * Debounced 250ms so typing in the workflow-name field does not fire one request per keystroke.
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
        // A 404 means no contract, which is a normal state rather than an error. Branch on the
        // status carried by ApiError instead of matching the message text.
        if (err instanceof ApiError && err.status === 404) return null;
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
