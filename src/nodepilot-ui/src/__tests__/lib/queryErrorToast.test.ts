import { describe, it, expect, beforeEach, vi } from 'vitest';
import { MutationCache, MutationObserver, QueryClient, type Query } from '@tanstack/react-query';
import {
  handleMutationError, handleQueryError, isSuppressedServerError, shouldToastQueryError,
} from '../../lib/queryErrorToast';
import { useToastStore } from '../../stores/toastStore';
import { ApiError } from '../../api/client';
import { AuthBoundaryChangedError } from '../../security/authBoundary';

/**
 * Builds the smallest object the policy reads. A real `Query` carries far more, and building one
 * through a QueryClient would test React Query's plumbing instead of this decision.
 */
function fakeQuery(
  options: { data?: unknown; silentError?: boolean } = {},
): Query<unknown, unknown, unknown, readonly unknown[]> {
  return {
    meta: options.silentError ? { silentError: true } : undefined,
    state: { data: options.data },
  } as unknown as Query<unknown, unknown, unknown, readonly unknown[]>;
}

describe('shouldToastQueryError', () => {
  it('reports a failure that has nothing to show', () => {
    expect(shouldToastQueryError(fakeQuery())).toBe(true);
  });

  it('stays silent when the query opted out', () => {
    expect(shouldToastQueryError(fakeQuery({ silentError: true }))).toBe(false);
  });

  it('stays silent when data is already on screen', () => {
    // A failed background refetch leaves the last good values visible. Without this, every polling
    // query would fire a toast on each interval for as long as the backend is unreachable.
    expect(shouldToastQueryError(fakeQuery({ data: [{ id: 1 }] }))).toBe(false);
  });

  it('treats an empty list as data, not as nothing to show', () => {
    // `[]` is a successful answer meaning "none". Toasting over it would put an error on screen
    // for a query that worked.
    expect(shouldToastQueryError(fakeQuery({ data: [] }))).toBe(false);
  });

  it('treats null as data', () => {
    expect(shouldToastQueryError(fakeQuery({ data: null }))).toBe(false);
  });
});

describe('handleQueryError', () => {
  beforeEach(() => {
    useToastStore.setState({ toasts: [] });
  });

  it('shows the error message when there is nothing on screen', () => {
    handleQueryError(new Error('Database is busy'), fakeQuery());
    const toasts = useToastStore.getState().toasts;
    expect(toasts).toHaveLength(1);
    expect(toasts[0].kind).toBe('error');
    expect(toasts[0].message).toBe('Database is busy');
  });

  it('falls back to a generic message when the error carries none', () => {
    handleQueryError(new Error(''), fakeQuery());
    expect(useToastStore.getState().toasts).toHaveLength(1);
    expect(useToastStore.getState().toasts[0].message).toBeTruthy();
  });

  it('falls back for a non-Error rejection', () => {
    handleQueryError('boom', fakeQuery());
    expect(useToastStore.getState().toasts).toHaveLength(1);
  });

  it('shows nothing for an opted-out query', () => {
    handleQueryError(new Error('status 502'), fakeQuery({ silentError: true }));
    expect(useToastStore.getState().toasts).toHaveLength(0);
  });

  it('shows nothing for a failed refetch that still has data', () => {
    handleQueryError(new Error('status 502'), fakeQuery({ data: [{ id: 1 }] }));
    expect(useToastStore.getState().toasts).toHaveLength(0);
  });

  it('shows nothing for an auth-boundary abort', () => {
    const abort = new Error('Authentication context changed');
    abort.name = 'AbortError';
    handleQueryError(abort, fakeQuery());
    expect(useToastStore.getState().toasts).toHaveLength(0);
  });

  it('does not accumulate a toast per poll for a persistently failing background query', () => {
    // The header polls /healthz/live on an interval. Without the opt-out, an unreachable backend
    // produces an endless stream of toasts repeating what the status pill beside them shows.
    const query = fakeQuery({ silentError: true });
    for (let poll = 0; poll < 10; poll++) handleQueryError(new Error('status 502'), query);
    expect(useToastStore.getState().toasts).toHaveLength(0);
  });
});

describe('handleQueryError — database codes', () => {
  beforeEach(() => useToastStore.setState({ toasts: [] }));

  it('handleQueryError_databaseUnavailable_doesNotToast', () => {
    // The outage banner already carries this message, and this handler fires once per failed
    // query, which during an outage is every visible query at once.
    handleQueryError(
      new ApiError('The database is not reachable right now. (DATABASE_UNAVAILABLE)', 503, 'DATABASE_UNAVAILABLE'),
      fakeQuery());

    expect(useToastStore.getState().toasts).toHaveLength(0);
  });

  it('handleQueryError_databaseTimeout_stillToasts', () => {
    // DATABASE_TIMEOUT means the breaker stayed closed, so no banner is rendered for it.
    // Suppressing the toast would leave a page that only reads `data`/`isLoading` showing an
    // empty list for a busy database.
    handleQueryError(
      new ApiError('The database did not answer in time. (DATABASE_TIMEOUT)', 503, 'DATABASE_TIMEOUT'),
      fakeQuery());

    expect(useToastStore.getState().toasts).toHaveLength(1);
    expect(useToastStore.getState().toasts[0].message).toContain('DATABASE_TIMEOUT');
  });

  it('handleQueryError_ordinaryError_stillToasts', () => {
    handleQueryError(new ApiError('Workflow is locked', 423, 'WORKFLOW_LOCKED'), fakeQuery());
    expect(useToastStore.getState().toasts).toHaveLength(1);
  });
});

describe('handleMutationError', () => {
  beforeEach(() => useToastStore.setState({ toasts: [] }));

  // Runs a mutation through a real MutationCache wired like the app's, so the handler sees the
  // same mutation object (options, meta) it gets in production.
  async function runFailingMutation(
    error: unknown,
    options: { onError?: () => void; meta?: { handlesError?: boolean }; callOnError?: () => void } = {},
  ) {
    const client = new QueryClient({ mutationCache: new MutationCache({ onError: handleMutationError }) });
    const observer = new MutationObserver(client, {
      mutationFn: () => Promise.reject(error),
      onError: options.onError,
      meta: options.meta,
    });
    // Call-time callbacks only reach a subscribed observer, as with a mounted useMutation.
    observer.subscribe(() => undefined);
    await observer.mutate(undefined, options.callOnError ? { onError: options.callOnError } : undefined)
      .catch(() => undefined);
  }

  const errorToasts = () => useToastStore.getState().toasts.filter((t) => t.kind === 'error');

  it('handleMutationError_noOwnHandler_showsTheServerMessage', async () => {
    await runFailingMutation(new ApiError('Workflow is locked by someone else (WORKFLOW_LOCKED)', 409, 'WORKFLOW_LOCKED'));
    expect(errorToasts()).toHaveLength(1);
    expect(errorToasts()[0].message).toBe('Workflow is locked by someone else (WORKFLOW_LOCKED)');
  });

  it('handleMutationError_errorWithoutMessage_fallsBackToGenericText', async () => {
    await runFailingMutation(new Error(''));
    expect(errorToasts()).toHaveLength(1);
    expect(errorToasts()[0].message).toBeTruthy();
  });

  it('handleMutationError_ownOnError_doesNotToastTwice', async () => {
    const own = vi.fn(() => { useToastStore.getState().push('error', 'own message'); });
    await runFailingMutation(new Error('boom'), { onError: own });
    expect(own).toHaveBeenCalledTimes(1);
    expect(errorToasts().map((t) => t.message)).toEqual(['own message']);
  });

  it('handleMutationError_handlesErrorMeta_staysSilent', async () => {
    await runFailingMutation(new Error('rendered inline'), { meta: { handlesError: true } });
    expect(errorToasts()).toHaveLength(0);
  });

  it('handleMutationError_callTimeOnErrorWithMeta_toastsOnce', async () => {
    // onError passed to mutate() lives on the observer, which the cache cannot see. The meta
    // flag is what keeps these call sites from showing two toasts.
    const callOnError = vi.fn(() => { useToastStore.getState().push('error', 'call-site message'); });
    await runFailingMutation(new Error('boom'), { meta: { handlesError: true }, callOnError });
    // Observer callbacks are delivered through React Query's batched notify, one tick later.
    await vi.waitFor(() => expect(callOnError).toHaveBeenCalledTimes(1));
    expect(errorToasts().map((t) => t.message)).toEqual(['call-site message']);
  });

  it('handleMutationError_abort_staysSilent', async () => {
    const abort = new Error('cancelled');
    abort.name = 'AbortError';
    await runFailingMutation(abort);
    expect(errorToasts()).toHaveLength(0);
  });

  it('handleMutationError_authBoundaryChange_staysSilent', async () => {
    await runFailingMutation(new AuthBoundaryChangedError());
    expect(errorToasts()).toHaveLength(0);
  });

  it('handleMutationError_databaseUnavailable_staysSilent', async () => {
    await runFailingMutation(
      new ApiError('The database is not reachable right now. (DATABASE_UNAVAILABLE)', 503, 'DATABASE_UNAVAILABLE'));
    expect(errorToasts()).toHaveLength(0);
  });

  it('handleMutationError_databaseTimeout_stillToasts', async () => {
    await runFailingMutation(
      new ApiError('The database did not answer in time. (DATABASE_TIMEOUT)', 503, 'DATABASE_TIMEOUT'));
    expect(errorToasts()).toHaveLength(1);
  });
});

describe('isSuppressedServerError', () => {
  it('matches what both global handlers skip', () => {
    const abort = new Error('x');
    abort.name = 'AbortError';
    expect(isSuppressedServerError(abort)).toBe(true);
    expect(isSuppressedServerError(new AuthBoundaryChangedError())).toBe(true);
    expect(isSuppressedServerError(new ApiError('down', 503, 'DATABASE_UNAVAILABLE'))).toBe(true);
    expect(isSuppressedServerError(new ApiError('slow', 503, 'DATABASE_TIMEOUT'))).toBe(false);
    expect(isSuppressedServerError(new Error('boom'))).toBe(false);
  });
});
