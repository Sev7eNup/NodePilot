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
});
