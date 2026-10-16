import type { Mutation, Query } from '@tanstack/react-query';
import { toast } from '../stores/toastStore';
import { isDatabaseOutageError } from '../api/client';
import i18n from '../i18n';

declare module '@tanstack/react-query' {
  interface Register {
    queryMeta: {
      /** The component renders its own error UI, so the global toast stays quiet. */
      silentError?: boolean;
    };
    mutationMeta: {
      /**
       * The caller reports the failure itself: inline error text, `mutateAsync` with its own
       * catch, or `onError` passed to `mutate()`. The cache cannot see per-call callbacks.
       */
      handlesError?: boolean;
    };
  }
}

/**
 * The query type `QueryCache.onError` hands over. Spelled out because a bare `Query` defaults
 * its error parameter to `Error` while the cache passes `unknown`.
 */
type CachedQuery = Query<unknown, unknown, unknown, readonly unknown[]>;

/** The mutation type `MutationCache.onError` hands over. */
type CachedMutation = Mutation<unknown, unknown, unknown>;

/**
 * Failures no global handler should report, for queries and mutations alike:
 *
 * - `AbortError`: a user cancel or an auth-boundary change. `AuthBoundaryChangedError` carries
 *   this name, so it is covered too.
 * - Database outages: the global banner already carries that message, and during an outage
 *   every visible request fails at once. DATABASE_TIMEOUT is not suppressed because the breaker
 *   stays closed for it, so no banner appears and the failure would otherwise be invisible. The
 *   toast sink additionally filters by message for call sites that toast on their own.
 */
export function isSuppressedServerError(error: unknown): boolean {
  if (error instanceof Error && error.name === 'AbortError') return true;
  return isDatabaseOutageError(error);
}

function errorMessage(error: unknown): string | undefined {
  return error instanceof Error && error.message ? error.message : undefined;
}

/**
 * Global handler for failed queries, wired into the app's `QueryCache`.
 *
 * Most pages read only `data` and `isLoading`, so a failed query would render as an empty list
 * and a busy database would look like an empty installation. This reports the failure once for
 * every query instead of page by page. It lives here rather than inline in `App.tsx` so the
 * policy below can be tested directly.
 */
export function handleQueryError(error: unknown, query: CachedQuery): void {
  if (isSuppressedServerError(error)) return;
  if (!shouldToastQueryError(query)) return;
  toast.error(errorMessage(error) ?? i18n.t('common:loadError'));
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

/**
 * Global handler for failed mutations, wired into the app's `MutationCache`. Without it a
 * mutation that defines no `onError` fails silently: the button just does nothing.
 */
export function handleMutationError(
  error: unknown,
  _variables: unknown,
  _onMutateResult: unknown,
  mutation: CachedMutation,
): void {
  if (isSuppressedServerError(error)) return;
  if (!shouldToastMutationError(mutation)) return;
  toast.error(errorMessage(error) ?? i18n.t('common:actionFailed'));
}

/** A mutation with its own `onError` or `meta.handlesError` reports the failure itself. */
export function shouldToastMutationError(mutation: CachedMutation): boolean {
  if (mutation.options.onError) return false;
  if (mutation.meta?.handlesError) return false;
  return true;
}
