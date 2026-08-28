import type { Query } from '@tanstack/react-query';
import { toast } from '../stores/toastStore';
import { isDatabaseOutageError } from '../api/client';
import i18n from '../i18n';

/**
 * The query type `QueryCache.onError` hands over. Spelled out because a bare `Query` defaults
 * its error parameter to `Error` while the cache passes `unknown`.
 */
type CachedQuery = Query<unknown, unknown, unknown, readonly unknown[]>;

/**
 * Global handler for failed queries, wired into the app's `QueryCache`.
 *
 * Most pages read only `data` and `isLoading`, so a failed query would render as an empty list
 * and a busy database would look like an empty installation. This reports the failure once for
 * every query instead of page by page. It lives here rather than inline in `App.tsx` so the
 * policy below can be tested directly.
 */
export function handleQueryError(error: unknown, query: CachedQuery): void {
  // Aborts at the auth boundary or started by the user are cancellation, not a load failure.
  if (error instanceof Error && error.name === 'AbortError') return;
  if (!shouldToastQueryError(query)) return;
  // Database outages only: the global banner already carries that message, while this handler
  // runs once per failed query, which during an outage is every visible query at once.
  // DATABASE_TIMEOUT stays unsuppressed because the breaker stays closed for it, so no banner
  // appears and the failure would otherwise be invisible. The check is by object; the toast sink
  // also filters by message for the mutation call sites this handler never sees.
  if (isDatabaseOutageError(error)) return;
  const message = error instanceof Error && error.message ? error.message : undefined;
  toast.error(message ?? i18n.t('common:loadError'));
}

/**
 * Two reasons not to show a toast:
 *
 * - `meta.silentError`: the component renders its own error UI and would say the same thing
 *   twice. Pages with an in-place error card use this, as does the header's backend-health pill.
 * - The query already holds data: a failed background refetch leaves the last good values on
 *   screen, so nothing needs explaining, and an unreachable backend would otherwise toast once
 *   per polling query per interval. Only a query with nothing to show is worth reporting.
 */
export function shouldToastQueryError(query: CachedQuery): boolean {
  if (query.meta?.silentError) return false;
  if (query.state.data !== undefined) return false;
  return true;
}
