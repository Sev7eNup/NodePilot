import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { toast, useToastStore } from '../../stores/toastStore';

describe('toastStore', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    useToastStore.setState({ toasts: [] });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('push_addsToastWithKindAndMessage', () => {
    useToastStore.getState().push('success', 'saved');
    const toasts = useToastStore.getState().toasts;
    expect(toasts).toHaveLength(1);
    expect(toasts[0]).toMatchObject({ kind: 'success', message: 'saved' });
  });

  it('push_autoDismissesAfterDefaultTtl', () => {
    useToastStore.getState().push('info', 'hello');
    expect(useToastStore.getState().toasts).toHaveLength(1);
    vi.advanceTimersByTime(4000);
    expect(useToastStore.getState().toasts).toHaveLength(0);
  });

  it('push_errorTtlIsLongerThanInfo', () => {
    useToastStore.getState().push('error', 'boom');
    vi.advanceTimersByTime(4000);
    expect(useToastStore.getState().toasts).toHaveLength(1);
    vi.advanceTimersByTime(4000);
    expect(useToastStore.getState().toasts).toHaveLength(0);
  });

  it('dismiss_removesOnlyTheGivenToast', () => {
    const id1 = useToastStore.getState().push('info', 'one');
    useToastStore.getState().push('info', 'two');
    useToastStore.getState().dismiss(id1);
    const toasts = useToastStore.getState().toasts;
    expect(toasts).toHaveLength(1);
    expect(toasts[0].message).toBe('two');
  });

  it('imperativeHelper_pushesWithoutReact', () => {
    toast.error('failed');
    expect(useToastStore.getState().toasts[0]).toMatchObject({ kind: 'error', message: 'failed' });
  });

  it('push_customTimeoutOverridesDefaultTtl', () => {
    useToastStore.getState().push('error', 'import report', 30_000);
    vi.advanceTimersByTime(8000); // default error TTL elapsed — still visible
    expect(useToastStore.getState().toasts).toHaveLength(1);
    vi.advanceTimersByTime(22_000);
    expect(useToastStore.getState().toasts).toHaveLength(0);
  });

  it('imperativeHelper_forwardsCustomTimeout', () => {
    toast.error('long-lived', 30_000);
    toast.success('short-lived', 1000);
    vi.advanceTimersByTime(1000);
    expect(useToastStore.getState().toasts.map((t) => t.message)).toEqual(['long-lived']);
    vi.advanceTimersByTime(29_000);
    expect(useToastStore.getState().toasts).toHaveLength(0);
  });

  // --- database-outage suppression ----------------------------------------------------------
  // The api client formats structured errors as "message (CODE)", so the two database 503 codes
  // are reliably present in the string every mutation onError hands to toast.error. Filtering at
  // the sink suppresses ~50 call sites at once; the global outage banner owns that message.

  it('push_databaseOutageError_isSuppressed', () => {
    const id = useToastStore.getState().push(
      'error',
      'The database is not reachable right now. (DATABASE_UNAVAILABLE)');
    expect(id).toBe(-1);
    expect(useToastStore.getState().toasts).toHaveLength(0);
  });

  it('push_databaseTimeoutError_stillToasts', () => {
    // Deliberately NOT suppressed, and this test is the reversal of an earlier one. The breaker
    // stays CLOSED for a command timeout, so no outage banner is on screen to carry the message.
    // Swallowing it here reproduced the exact defect the feature exists to remove: a busy database
    // rendering as an empty installation, with nothing for the user to act on.
    useToastStore.getState().push('error', 'Did not answer in time. (DATABASE_TIMEOUT)');
    expect(useToastStore.getState().toasts).toHaveLength(1);
  });

  it('push_ordinaryError_stillToasts', () => {
    // The guard against over-suppression: an outage must not eat unrelated failures.
    useToastStore.getState().push('error', 'Delete failed: workflow is locked');
    expect(useToastStore.getState().toasts).toHaveLength(1);
  });

  it('push_successMentioningDatabaseCodes_isNotSuppressed', () => {
    // Only the error kind is filtered - a success message quoting the code must survive.
    useToastStore.getState().push('success', 'Recovered from DATABASE_UNAVAILABLE');
    expect(useToastStore.getState().toasts).toHaveLength(1);
  });
});
