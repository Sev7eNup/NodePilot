import { QueryCache, QueryClient } from '@tanstack/react-query';
import { handleQueryError } from './lib/queryErrorToast';
import { registerAuthBoundaryQueryCacheClearer } from './security/authBoundary';

/**
 * The single application-wide server-state cache. It lives outside App so the auth boundary can
 * register a narrow `clear` callback without creating an api-client to auth-store cycle.
 */
export const queryClient = new QueryClient({
  queryCache: new QueryCache({ onError: handleQueryError }),
  defaultOptions: {
    queries: {
      retry: 1,
      staleTime: 10_000,
      // SignalR invalidates the affected queries directly, so refetching on window focus would
      // only add redundant requests.
      refetchOnWindowFocus: false,
    },
  },
});

registerAuthBoundaryQueryCacheClearer(() => queryClient.clear());
