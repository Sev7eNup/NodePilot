import type { Query } from '@tanstack/react-query';
import { toast } from '../stores/toastStore';
import { isDatabaseOutageError } from '../api/client';
import i18n from '../i18n';

/**
 * Exactly what `QueryCache.onError` hands over. Spelled out rather than left as a bare `Query`,
 * whose error parameter defaults to `Error` while the cache passes `unknown` — close enough to
 * look right and not close enough to compile.
 */
type CachedQuery = Query<unknown, unknown, unknown, readonly unknown[]>;

/**
 * Global handler for failed queries, wired into the app's `QueryCache`.
 *
 * A failed query used to be invisible unless the page happened to read `isError`. Most do not:
 * they destructure `data` and `isLoading`, so a 500 renders as an empty list and a busy database
 * looks exactly like an empty installation. Mutations always surfaced their errors; queries never
 * did. This closes that asymmetry once, for every query, instead of page by page.
 *
 * It lives here rather than inline in `App.tsx` so the policy below can be tested as the real
 * thing rather than as a copy of it.
 */
export function handleQueryError(error: unknown, query: CachedQuery): void {
  if (!shouldToastQueryError(query)) return;
  // Database OUTAGE only: the global banner owns that message (with live state and recovery), and
  // this handler fires once per failed query — during an outage that is every visible query at once.
  // DATABASE_TIMEOUT is deliberately NOT suppressed: the breaker stays closed for it, so no banner
  // is shown, and a page that reads only `data`/`isLoading` would render a busy database as an empty
  // list with nothing to act on. That is the original defect this handler exists to prevent.
  // Precise object check here; the toast sink additionally filters by message for the mutation
  // call sites this handler never sees.
  if (isDatabaseOutageError(error)) return;
  const message = error instanceof Error && error.message ? error.message : undefined;
  toast.error(message ?? i18n.t('common:loadError'));
}

/**
 * Two reasons not to interrupt:
 *
 * - `meta.silentError` — the component renders its own error UI and would say the same thing
 *   twice. Pages with an in-place error card use this, as does the header's backend-health pill.
 * - The query already holds data. A failed background refetch leaves the last good values on
 *   screen, so there is nothing blank to explain. This one is load-bearing: the app runs ~18
 *   polling queries, and without it an unreachable backend fires a toast per query per interval,
 *   forever. The case this handler exists for is the opposite — a query with nothing to show,
 *   where the failure is the only thing worth reporting.
 */
export function shouldToastQueryError(query: CachedQuery): boolean {
  if (query.meta?.silentError) return false;
  if (query.state.data !== undefined) return false;
  return true;
}
