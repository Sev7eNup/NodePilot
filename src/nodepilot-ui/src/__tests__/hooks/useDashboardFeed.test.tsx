import * as React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook as rtlRenderHook, act, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

function makeWrapper() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: 0 } } });
  return ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
}
const renderHook: typeof rtlRenderHook = ((cb, opts) =>
  rtlRenderHook(cb, { wrapper: makeWrapper(), ...(opts ?? {}) })) as typeof rtlRenderHook;

type Handler = (...args: unknown[]) => void;
type MockConnection = {
  handlers: Map<string, Handler>;
  lifecycle: Map<string, Handler>;
  on: ReturnType<typeof vi.fn>;
  onreconnected: ReturnType<typeof vi.fn>;
  onreconnecting: ReturnType<typeof vi.fn>;
  onclose: ReturnType<typeof vi.fn>;
  invoke: ReturnType<typeof vi.fn>;
  start: ReturnType<typeof vi.fn>;
  stop: ReturnType<typeof vi.fn>;
  emit: (event: string, payload: unknown) => void;
};

let __currentConnection: MockConnection | null = null;
function createMockConnection(): MockConnection {
  const handlers = new Map<string, Handler>();
  const lifecycle = new Map<string, Handler>();
  return {
    handlers,
    lifecycle,
    on: vi.fn((event: string, handler: Handler) => { handlers.set(event, handler); }),
    onreconnected: vi.fn((h: Handler) => { lifecycle.set('onreconnected', h); }),
    onreconnecting: vi.fn((h: Handler) => { lifecycle.set('onreconnecting', h); }),
    onclose: vi.fn((h: Handler) => { lifecycle.set('onclose', h); }),
    invoke: vi.fn().mockResolvedValue(undefined),
    start: vi.fn().mockResolvedValue(undefined),
    stop: vi.fn().mockResolvedValue(undefined),
    emit: (event, payload) => { handlers.get(event)?.(payload); },
  };
}

vi.mock('@microsoft/signalr', () => {
  class HubConnectionBuilder {
    withUrl() { return this; }
    withAutomaticReconnect() { return this; }
    configureLogging() { return this; }
    build() { __currentConnection = createMockConnection(); return __currentConnection; }
  }
  return { HubConnectionBuilder, LogLevel: { Warning: 1 } };
});

import { useDashboardFeed } from '../../hooks/useDashboardFeed';
import { useLiveOpsFeed } from '../../hooks/useLiveOpsFeed';
import { clearLocalAuthBoundary } from '../../security/authBoundary';

const statusBatch = {
  events: [{ type: 'ExecutionStatusChanged', evt: { executionId: 'e1', workflowId: 'w1', status: 'Succeeded' } }],
};

describe('useDashboardFeed', () => {
  beforeEach(() => { __currentConnection = null; });
  afterEach(() => { vi.useRealTimers(); });

  it.each(['identity-change', 'unmount'] as const)('ignores queued events and invalidations after %s', async (boundary) => {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const invalidate = vi.spyOn(qc, 'invalidateQueries');
    const onStatus = vi.fn();
    const queryKey = ['ops-test'];
    const { unmount } = renderHook(() => useLiveOpsFeed({ queryKey, debounceMs: 100, onStatus }), {
      wrapper: ({ children }: { children: React.ReactNode }) => (
        <QueryClientProvider client={qc}>{children}</QueryClientProvider>
      ),
    });
    await waitFor(() => expect(__currentConnection?.invoke).toHaveBeenCalledWith('JoinOperationsFeed'));
    const oldConnection = __currentConnection;
    vi.useFakeTimers();
    act(() => oldConnection?.emit('LiveEventsBatch', statusBatch));
    expect(onStatus).toHaveBeenCalledOnce();

    act(() => {
      if (boundary === 'identity-change') clearLocalAuthBoundary();
      else unmount();
      oldConnection?.emit('LiveEventsBatch', statusBatch);
      vi.advanceTimersByTime(200);
    });

    expect(onStatus).toHaveBeenCalledOnce();
    expect(invalidate).not.toHaveBeenCalled();
  });

  it('keeps invalidation working when the feed is reconfigured with a pending timer', async () => {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const invalidate = vi.spyOn(qc, 'invalidateQueries');
    const firstKey = ['first'];
    const secondKey = ['second'];
    const { rerender } = renderHook(({ queryKey }) => useLiveOpsFeed({ queryKey, debounceMs: 100 }), {
      initialProps: { queryKey: firstKey },
      wrapper: ({ children }: { children: React.ReactNode }) => (
        <QueryClientProvider client={qc}>{children}</QueryClientProvider>
      ),
    });
    await waitFor(() => expect(__currentConnection?.invoke).toHaveBeenCalledWith('JoinOperationsFeed'));
    vi.useFakeTimers();
    act(() => __currentConnection?.emit('LiveEventsBatch', statusBatch));
    rerender({ queryKey: secondKey });
    act(() => {
      __currentConnection?.emit('LiveEventsBatch', statusBatch);
      vi.advanceTimersByTime(200);
    });
    expect(invalidate).toHaveBeenCalledExactlyOnceWith({ queryKey: secondKey });
  });

  it('joins the ops feed on connect', async () => {
    renderHook(() => useDashboardFeed());
    await waitFor(() => expect(__currentConnection?.invoke).toHaveBeenCalledWith('JoinOperationsFeed'));
  });

  it('debounce-invalidates [dashboard-stats] on an ExecutionStatusChanged batch', async () => {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const invalidateSpy = vi.spyOn(qc, 'invalidateQueries');
    renderHook(() => useDashboardFeed(), {
      wrapper: ({ children }: { children: React.ReactNode }) => (
        <QueryClientProvider client={qc}>{children}</QueryClientProvider>
      ),
    });
    await waitFor(() => expect(__currentConnection?.handlers.has('LiveEventsBatch')).toBe(true));

    await act(async () => {
      __currentConnection?.emit('LiveEventsBatch', {
        events: [{ type: 'ExecutionStatusChanged', evt: { executionId: 'e1', workflowId: 'w1', status: 'Succeeded' } }],
      });
    });

    // The invalidation is debounced by several seconds on purpose: on a busy instance executions
    // change state constantly, and refetching per event re-ran the history-reading parts of the
    // dashboard endpoint over and over. Allow for that window here.
    await waitFor(
      () => expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['dashboard-stats'] }),
      { timeout: 8_000 },
    );
  });

  it('does not invalidate for a batch without ExecutionStatusChanged', async () => {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const invalidateSpy = vi.spyOn(qc, 'invalidateQueries');
    renderHook(() => useDashboardFeed(), {
      wrapper: ({ children }: { children: React.ReactNode }) => (
        <QueryClientProvider client={qc}>{children}</QueryClientProvider>
      ),
    });
    await waitFor(() => expect(__currentConnection?.handlers.has('LiveEventsBatch')).toBe(true));

    await act(async () => {
      __currentConnection?.emit('LiveEventsBatch', {
        events: [{ type: 'StepStarted', evt: { executionId: 'e1', workflowId: 'w1' } }],
      });
    });
    // Wait past the debounce window to confirm nothing is invalidated.
    await new Promise((r) => setTimeout(r, 60));
    expect(invalidateSpy).not.toHaveBeenCalled();
  });
});
