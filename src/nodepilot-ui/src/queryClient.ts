import { QueryCache, QueryClient } from '@tanstack/react-query';
import { handleQueryError } from './lib/queryErrorToast';
import { registerAuthBoundaryQueryCacheClearer } from './security/authBoundary';

/**
 * The one application-wide server-state cache. Keeping it outside App makes the auth boundary able
 * to register a narrow `clear` callback without introducing an api-client <-> auth-store cycle.
 */
export const queryClient = new QueryClient({
  queryCache: new QueryCache({ onError: handleQueryError }),
  defaultOptions: {
    queries: {
      retry: 1,
      staleTime: 10_000,
      // SignalR invalidates affected queries precisely; refetch-on-focus otherwise creates a
      // request storm across Dashboard, Executions and Audit tabs.
      refetchOnWindowFocus: false,
    },
  },
});

registerAuthBoundaryQueryCacheClearer(() => queryClient.clear());
