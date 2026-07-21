import * as React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
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

describe('useDashboardFeed', () => {
  beforeEach(() => { __currentConnection = null; });

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

    // Debounced (500ms) — wait for the invalidation to fire.
    await waitFor(() => expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['dashboard-stats'] }));
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
    // Give the debounce window a chance to prove a no-op.
    await new Promise((r) => setTimeout(r, 60));
    expect(invalidateSpy).not.toHaveBeenCalled();
  });
});