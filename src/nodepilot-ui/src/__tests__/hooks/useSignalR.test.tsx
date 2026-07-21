import * as React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook as rtlRenderHook, act, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

/**
 * Test wrapper: useWorkflowSignalR calls useQueryClient(), which requires a
 * QueryClientProvider. We create a fresh QueryClient per renderHook call (no retry,
 * no gcTime bleed between tests).
 */
function makeWrapper() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, staleTime: 0 } },
  });
  return ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
}

const renderHook: typeof rtlRenderHook = ((callback, options) =>
  rtlRenderHook(callback, { wrapper: makeWrapper(), ...(options ?? {}) })) as typeof rtlRenderHook;

// SignalR mock: capture event handlers per connection so tests can drive events
// without spinning up a real WebSocket. We expose the captured handlers via
// `__currentConnection` so each test can read the latest mock and emit events.
type Handler = (...args: unknown[]) => void;
type MockConnection = {
  handlers: Map<string, Handler>;
  start: ReturnType<typeof vi.fn>;
  stop: ReturnType<typeof vi.fn>;
  on: ReturnType<typeof vi.fn>;
  onreconnected: ReturnType<typeof vi.fn>;
  invoke: ReturnType<typeof vi.fn>;
  emit: (event: string, payload: unknown) => void;
};

let __currentConnection: MockConnection | null = null;

function createMockConnection(): MockConnection {
  const handlers = new Map<string, Handler>();
  const conn: MockConnection = {
    handlers,
    start: vi.fn().mockResolvedValue(undefined),
    stop: vi.fn().mockResolvedValue(undefined),
    on: vi.fn((event: string, handler: Handler) => { handlers.set(event, handler); }),
    onreconnected: vi.fn(),
    invoke: vi.fn().mockResolvedValue(undefined),
    emit: (event, payload) => {
      const h = handlers.get(event);
      if (!h) throw new Error(`No handler registered for '${event}'`);
      h(payload);
    },
  };
  return conn;
}

vi.mock('@microsoft/signalr', () => {
  class HubConnectionBuilder {
    withUrl() { return this; }
    withAutomaticReconnect() { return this; }
    configureLogging() { return this; }
    build() { __currentConnection = createMockConnection(); return __currentConnection; }
  }
  return {
    HubConnectionBuilder,
    LogLevel: { Warning: 1 },
  };
});

import { useWorkflowSignalR, COMPLETED_EXECUTION_TTL_MS, MAX_ACTIVE_DISPLAYED, MAX_AUTO_HYDRATE } from '../../hooks/useSignalR';

async function waitForLiveExecution(result: ReturnType<typeof renderHook<ReturnType<typeof useWorkflowSignalR>, []>>['result']) {
  await waitFor(() => expect(result.current.liveExecution).not.toBeNull());
  return result.current.liveExecution!;
}

describe('useWorkflowSignalR', () => {
  beforeEach(() => { __currentConnection = null; });
  afterEach(() => { vi.clearAllMocks(); });

  it('does not open a connection when workflowId is undefined', () => {
    renderHook(() => useWorkflowSignalR(undefined));
    expect(__currentConnection).toBeNull();
  });

  it('opens a connection and joins the workflow group when workflowId is provided', async () => {
    renderHook(() => useWorkflowSignalR('wf-1'));

    expect(__currentConnection).not.toBeNull();
    expect(__currentConnection!.start).toHaveBeenCalledOnce();
    await waitFor(() => {
      expect(__currentConnection!.invoke).toHaveBeenCalledWith('JoinWorkflow', 'wf-1');
    });
  });

  it('marks itself connected after the start promise resolves', async () => {
    const { result } = renderHook(() => useWorkflowSignalR('wf-1'));

    await waitFor(() => expect(result.current.connected).toBe(true));
  });

  it('StepStarted creates a Running step inside a fresh LiveExecution', async () => {
    const { result } = renderHook(() => useWorkflowSignalR('wf-1'));
    await waitFor(() => expect(result.current.connected).toBe(true));

    act(() => {
      __currentConnection!.emit('StepStarted', {
        executionId: 'exec-1', workflowId: 'wf-1',
        stepId: 'step-a', stepName: 'Check Disk', stepType: 'runScript',
        startedAt: '2026-04-26T12:00:00Z',
      });
    });

    const liveExecution = await waitForLiveExecution(result);
    expect(liveExecution.executionId).toBe('exec-1');
    expect(liveExecution.status).toBe('Running');
    expect(liveExecution.steps).toHaveLength(1);
    expect(liveExecution.steps[0]).toMatchObject({
      stepId: 'step-a', stepName: 'Check Disk', status: 'Running',
    });
  });

  it('StepCompleted updates the step status and merges outputParameters into the databus', async () => {
    const { result } = renderHook(() => useWorkflowSignalR('wf-1'));
    await waitFor(() => expect(result.current.connected).toBe(true));

    act(() => {
      __currentConnection!.emit('StepStarted', {
        executionId: 'exec-1', workflowId: 'wf-1',
        stepId: 'step-a', stepName: 'Check Disk', stepType: 'runScript',
        startedAt: '2026-04-26T12:00:00Z',
      });
    });
    act(() => {
      __currentConnection!.emit('StepCompleted', {
        executionId: 'exec-1', workflowId: 'wf-1',
        stepId: 'step-a', status: 'Succeeded',
        output: 'OK', completedAt: '2026-04-26T12:00:01Z',
        outputParameters: { hostName: 'WIN-01', exitCode: '0' },
      });
    });

    const liveExecution = await waitForLiveExecution(result);
    const step = liveExecution.steps[0];
    expect(step.status).toBe('Succeeded');
    expect(step.output).toBe('OK');
    expect(liveExecution.databus['step-a.param.hostName']).toMatchObject({
      value: 'WIN-01', kind: 'param', stepId: 'step-a', paramKey: 'hostName',
    });
    expect(liveExecution.databus['step-a.param.exitCode']).toMatchObject({
      value: '0', kind: 'param', paramKey: 'exitCode',
    });
  });

  it('ExecutionStatusChanged updates the overall status and completion fields', async () => {
    const { result } = renderHook(() => useWorkflowSignalR('wf-1'));
    await waitFor(() => expect(result.current.connected).toBe(true));

    act(() => {
      __currentConnection!.emit('StepStarted', {
        executionId: 'exec-1', workflowId: 'wf-1',
        stepId: 'step-a', stepType: 'runScript', startedAt: '2026-04-26T12:00:00Z',
      });
    });
    act(() => {
      __currentConnection!.emit('ExecutionStatusChanged', {
        executionId: 'exec-1', workflowId: 'wf-1',
        status: 'Failed', errorMessage: 'boom', completedAt: '2026-04-26T12:00:05Z',
      });
    });

    await waitFor(() => expect(result.current.liveExecution?.status).toBe('Failed'));
    expect(result.current.liveExecution!.errorMessage).toBe('boom');
    expect(result.current.liveExecution!.completedAt).toBe('2026-04-26T12:00:05Z');
  });

  it('StepPaused marks the step as Paused and classifies variables into the databus', async () => {
    const { result } = renderHook(() => useWorkflowSignalR('wf-1'));
    await waitFor(() => expect(result.current.connected).toBe(true));

    act(() => {
      __currentConnection!.emit('StepStarted', {
        executionId: 'exec-1', workflowId: 'wf-1',
        stepId: 'step-a', stepName: 'Decision', stepType: 'decision', startedAt: '2026-04-26T12:00:00Z',
      });
    });
    act(() => {
      __currentConnection!.emit('StepPaused', {
        executionId: 'exec-1', workflowId: 'wf-1',
        stepId: 'step-a', stepName: 'Decision',
        variables: {
          'step-a.output': 'hello',
          'step-b.param.host': 'WIN-01',
          'globals.SMTP_HOST': 'mail.local',
          'manual.userInput': 'admin',
        },
        pausedAt: '2026-04-26T12:00:02Z',
        reason: 'breakpoint',
      });
    });

    const liveExecution = await waitForLiveExecution(result);
    const step = liveExecution.steps[0];
    expect(step.status).toBe('Paused');
    expect(step.pausedReason).toBe('breakpoint');

    const bus = liveExecution.databus;
    expect(bus['step-a.output']).toMatchObject({ kind: 'output', stepId: 'step-a' });
    expect(bus['step-b.param.host']).toMatchObject({ kind: 'param', stepId: 'step-b', paramKey: 'host' });
    expect(bus['globals.SMTP_HOST']).toMatchObject({ kind: 'global' });
    expect(bus['manual.userInput']).toMatchObject({ kind: 'trigger' });
  });

  it('StepResumed clears the paused fields and flips status back to Running', async () => {
    const { result } = renderHook(() => useWorkflowSignalR('wf-1'));
    await waitFor(() => expect(result.current.connected).toBe(true));

    act(() => {
      __currentConnection!.emit('StepStarted', {
        executionId: 'exec-1', workflowId: 'wf-1',
        stepId: 'step-a', stepType: 'runScript', startedAt: '2026-04-26T12:00:00Z',
      });
    });
    act(() => {
      __currentConnection!.emit('StepPaused', {
        executionId: 'exec-1', workflowId: 'wf-1',
        stepId: 'step-a', variables: {}, pausedAt: '2026-04-26T12:00:02Z', reason: 'breakpoint',
      });
    });
    act(() => {
      __currentConnection!.emit('StepResumed', {
        executionId: 'exec-1', workflowId: 'wf-1', stepId: 'step-a',
      });
    });

    await waitFor(() => expect(result.current.liveExecution?.steps[0]?.status).toBe('Running'));
    const step = result.current.liveExecution!.steps[0];
    expect(step.status).toBe('Running');
    expect(step.pausedAt).toBeUndefined();
    expect(step.pausedVariables).toBeUndefined();
    expect(step.pausedReason).toBeUndefined();
  });

  it('events for a different executionId do not mutate the current LiveExecution', async () => {
    const { result } = renderHook(() => useWorkflowSignalR('wf-1'));
    await waitFor(() => expect(result.current.connected).toBe(true));

    act(() => {
      __currentConnection!.emit('StepStarted', {
        executionId: 'exec-1', workflowId: 'wf-1',
        stepId: 'step-a', stepType: 'runScript', startedAt: '2026-04-26T12:00:00Z',
      });
    });
    act(() => {
      __currentConnection!.emit('StepCompleted', {
        executionId: 'exec-OTHER', workflowId: 'wf-1',
        stepId: 'step-a', status: 'Succeeded', completedAt: '2026-04-26T12:00:01Z',
      });
    });

    // Step-a should still be Running because StepCompleted was for a different exec.
    await waitFor(() => expect(result.current.liveExecutions).toHaveLength(2));
    expect(result.current.liveExecution!.steps[0].status).toBe('Running');
    const other = result.current.liveExecutions.find((e) => e.executionId === 'exec-OTHER')!;
    expect(other.steps[0].status).toBe('Succeeded');
  });

  it('keeps parallel executions separated when StepStarted interleaves', async () => {
    const { result } = renderHook(() => useWorkflowSignalR('wf-1'));
    await waitFor(() => expect(result.current.connected).toBe(true));

    act(() => {
      __currentConnection!.emit('StepStarted', {
        executionId: 'exec-1', workflowId: 'wf-1',
        stepId: 'step-a', stepName: 'Alpha', stepType: 'runScript',
        startedAt: '2026-04-26T12:00:00Z',
      });
      __currentConnection!.emit('StepStarted', {
        executionId: 'exec-2', workflowId: 'wf-1',
        stepId: 'step-b', stepName: 'Beta', stepType: 'runScript',
        startedAt: '2026-04-26T12:00:01Z',
      });
    });

    await waitFor(() => expect(result.current.liveExecutions).toHaveLength(2));
    expect(result.current.liveExecutions.find((e) => e.executionId === 'exec-1')!.steps[0].stepName).toBe('Alpha');
    expect(result.current.liveExecutions.find((e) => e.executionId === 'exec-2')!.steps[0].stepName).toBe('Beta');
  });

  it('LiveEventsBatch applies all batched events', async () => {
    const { result } = renderHook(() => useWorkflowSignalR('wf-1'));
    await waitFor(() => expect(result.current.connected).toBe(true));

    act(() => {
      __currentConnection!.emit('LiveEventsBatch', {
        events: [
          {
            type: 'StepStarted',
            event: {
              executionId: 'exec-1', workflowId: 'wf-1',
              stepId: 'step-a', stepName: 'Alpha', stepType: 'runScript',
              startedAt: '2026-04-26T12:00:00Z',
            },
          },
          {
            type: 'StepCompleted',
            event: {
              executionId: 'exec-1', workflowId: 'wf-1',
              stepId: 'step-a', status: 'Succeeded', output: 'OK',
              completedAt: '2026-04-26T12:00:01Z',
            },
          },
        ],
      });
    });

    await waitFor(() => expect(result.current.liveExecution?.steps[0]?.status).toBe('Succeeded'));
    expect(result.current.liveExecution!.steps[0].output).toBe('OK');
  });

  it('LiveEventsBatch accepts PascalCase payloads from SignalR serializers', async () => {
    const { result } = renderHook(() => useWorkflowSignalR('wf-1'));
    await waitFor(() => expect(result.current.connected).toBe(true));

    act(() => {
      __currentConnection!.emit('LiveEventsBatch', {
        Events: [
          {
            Type: 'StepStarted',
            Event: {
              executionId: 'exec-1', workflowId: 'wf-1',
              stepId: 'step-a', stepName: 'Alpha', stepType: 'runScript',
              startedAt: '2026-04-26T12:00:00Z',
            },
          },
        ],
      });
    });

    await waitFor(() => expect(result.current.liveExecution?.steps[0]?.stepName).toBe('Alpha'));
  });

  it('clearLive resets liveExecution to null', async () => {
    const { result } = renderHook(() => useWorkflowSignalR('wf-1'));
    await waitFor(() => expect(result.current.connected).toBe(true));

    act(() => {
      __currentConnection!.emit('StepStarted', {
        executionId: 'exec-1', workflowId: 'wf-1',
        stepId: 'step-a', stepType: 'runScript', startedAt: '2026-04-26T12:00:00Z',
      });
    });
    act(() => { result.current.clearLive(); });

    expect(result.current.liveExecution).toBeNull();
  });

  it('clears live executions when the workflowId changes', async () => {
    const { result, rerender } = renderHook(
      ({ workflowId }) => useWorkflowSignalR(workflowId),
      { initialProps: { workflowId: 'wf-1' as string | undefined } },
    );
    await waitFor(() => expect(result.current.connected).toBe(true));
    const firstConnection = __currentConnection!;

    act(() => {
      firstConnection.emit('StepStarted', {
        executionId: 'exec-1', workflowId: 'wf-1',
        stepId: 'step-a', stepType: 'runScript', startedAt: '2026-04-26T12:00:00Z',
      });
    });
    await waitFor(() => expect(result.current.liveExecutions).toHaveLength(1));

    rerender({ workflowId: 'wf-2' });

    await waitFor(() => expect(result.current.liveExecutions).toHaveLength(0));
    expect(result.current.liveExecution).toBeNull();
    expect(firstConnection.stop).toHaveBeenCalledOnce();
    await waitFor(() => expect(__currentConnection!.invoke).toHaveBeenCalledWith('JoinWorkflow', 'wf-2'));
  });

  it('ignores stale events from the previous workflow after switching', async () => {
    const { result, rerender } = renderHook(
      ({ workflowId }) => useWorkflowSignalR(workflowId),
      { initialProps: { workflowId: 'wf-1' as string | undefined } },
    );
    await waitFor(() => expect(result.current.connected).toBe(true));
    const firstConnection = __currentConnection!;

    rerender({ workflowId: 'wf-2' });
    await waitFor(() => expect(__currentConnection!.invoke).toHaveBeenCalledWith('JoinWorkflow', 'wf-2'));
    const secondConnection = __currentConnection!;

    act(() => {
      firstConnection.emit('StepStarted', {
        executionId: 'old-exec', workflowId: 'wf-1',
        stepId: 'old-step', stepType: 'runScript', startedAt: '2026-04-26T12:00:00Z',
      });
      secondConnection.emit('StepStarted', {
        executionId: 'new-exec', workflowId: 'wf-2',
        stepId: 'new-step', stepType: 'runScript', startedAt: '2026-04-26T12:00:01Z',
      });
    });

    await waitFor(() => expect(result.current.liveExecutions).toHaveLength(1));
    expect(result.current.liveExecution!.executionId).toBe('new-exec');
  });

  it('evicts terminal executions from liveExecutions after the TTL', async () => {
    vi.useFakeTimers();
    try {
      const { result } = renderHook(() => useWorkflowSignalR('wf-1'));
      // Flush the resolved Promise from connection.start() so setConnected(true) runs.
      await act(async () => { await Promise.resolve(); });
      expect(result.current.connected).toBe(true);

      act(() => {
        __currentConnection!.emit('StepStarted', {
          executionId: 'exec-ttl', workflowId: 'wf-1',
          stepId: 'step-a', stepType: 'runScript', startedAt: '2026-04-26T12:00:00Z',
        });
      });
      act(() => {
        __currentConnection!.emit('ExecutionStatusChanged', {
          executionId: 'exec-ttl', workflowId: 'wf-1',
          status: 'Succeeded', completedAt: '2026-04-26T12:00:05Z',
        });
      });

      // Fire the 100 ms debounce and flush pending React state updates.
      await act(async () => { vi.advanceTimersByTime(150); });
      expect(result.current.liveExecution?.status).toBe('Succeeded');

      // Fire the eviction timer and flush React state updates.
      await act(async () => { vi.advanceTimersByTime(COMPLETED_EXECUTION_TTL_MS + 100); });
      expect(result.current.liveExecution).toBeNull();
    } finally {
      vi.useRealTimers();
    }
  });

  it('cleanup stops the connection on unmount', async () => {
    const { result, unmount } = renderHook(() => useWorkflowSignalR('wf-1'));
    await waitFor(() => expect(result.current.connected).toBe(true));

    const conn = __currentConnection!;
    unmount();

    expect(conn.stop).toHaveBeenCalledOnce();
  });

  it('schedules TTL eviction for terminal runs that arrive via hydration after navigation', async () => {
    // Regression guard: when the user navigates away from a workflow and comes back,
    // useWorkflowSignalR remounts and bulk-hydrates currently-relevant executions —
    // including ones that completed while we weren't subscribed. Previously these
    // landed in state with NO eviction timer (scheduleEviction was only called from
    // live ExecutionStatusChanged events). Result: terminal runs hung in the Live tab
    // forever, polluting the view across hours of usage.
    vi.useFakeTimers();
    try {
      const completedAt = new Date(Date.now() - 5_000).toISOString(); // finished 5s ago
      const fetchMock = vi.fn(async (url: string | URL | Request) => {
        const u = typeof url === 'string' ? url : url instanceof URL ? url.toString() : url.url;
        if (u.includes('/executions?workflowId=')) {
          return new Response(JSON.stringify([
            { id: 'exec-done', workflowId: 'wf-1', status: 'Failed',
              startedAt: new Date(Date.now() - 10_000).toISOString(),
              completedAt, errorMessage: 'boom' },
          ]), { status: 200, headers: { 'Content-Type': 'application/json' } });
        }
        if (u.endsWith('/executions/exec-done/steps')) {
          return new Response(JSON.stringify([
            { stepId: 'step-1', stepName: 'X', stepType: 'runScript',
              status: 'Failed', startedAt: new Date(Date.now() - 10_000).toISOString(),
              completedAt, errorOutput: 'boom' },
          ]), { status: 200, headers: { 'Content-Type': 'application/json' } });
        }
        return new Response('not found', { status: 404 });
      });
      vi.stubGlobal('fetch', fetchMock);

      const { result } = renderHook(() => useWorkflowSignalR('wf-1'));
      // Flush the start-promise + hydration micro/macro tasks so liveExecutions is populated.
      await act(async () => { await vi.advanceTimersByTimeAsync(50); });
      expect(result.current.connected).toBe(true);
      await act(async () => { await vi.advanceTimersByTimeAsync(150); });

      // Hydration finished — the Failed run is now in the Live state.
      expect(result.current.liveExecutions).toHaveLength(1);
      expect(result.current.liveExecutions[0]).toMatchObject({
        executionId: 'exec-done', status: 'Failed',
      });

      // 5s elapsed since completion, so eviction should fire ~25s from now.
      // Advance just under the remaining TTL — still visible.
      await act(async () => { await vi.advanceTimersByTimeAsync(20_000); });
      expect(result.current.liveExecutions).toHaveLength(1);

      // Advance past the remaining TTL — gone.
      await act(async () => { await vi.advanceTimersByTimeAsync(6_000); });
      expect(result.current.liveExecutions).toHaveLength(0);
    } finally {
      vi.useRealTimers();
      vi.unstubAllGlobals();
    }
  });

  it('evicts immediately when a hydrated terminal run is already older than the TTL', async () => {
    // Edge case: the hydration filter (TTL * 2 = 60 s window) admits runs that finished
    // up to 60 s ago, but the eviction TTL is only 30 s. For runs in the 30-60 s slice,
    // delay computes to a negative number — Math.max(0, ...) clamps it so eviction fires
    // on the next event-loop tick instead of sitting forever in setTimeout limbo.
    vi.useFakeTimers();
    try {
      const completedAt = new Date(Date.now() - 45_000).toISOString(); // finished 45s ago
      const fetchMock = vi.fn(async (url: string | URL | Request) => {
        const u = typeof url === 'string' ? url : url instanceof URL ? url.toString() : url.url;
        if (u.includes('/executions?workflowId=')) {
          return new Response(JSON.stringify([
            { id: 'exec-old', workflowId: 'wf-1', status: 'Succeeded',
              startedAt: new Date(Date.now() - 50_000).toISOString(),
              completedAt },
          ]), { status: 200, headers: { 'Content-Type': 'application/json' } });
        }
        if (u.endsWith('/executions/exec-old/steps')) {
          return new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } });
        }
        return new Response('not found', { status: 404 });
      });
      vi.stubGlobal('fetch', fetchMock);

      const { result } = renderHook(() => useWorkflowSignalR('wf-1'));
      await act(async () => { await vi.advanceTimersByTimeAsync(50); });
      await act(async () => { await vi.advanceTimersByTimeAsync(150); });

      // Run was admitted by the hydration filter (45s < 60s cutoff)…
      // …but eviction fires immediately because it's already past the 30s TTL.
      await act(async () => { await vi.advanceTimersByTimeAsync(10); });
      expect(result.current.liveExecutions).toHaveLength(0);
    } finally {
      vi.useRealTimers();
      vi.unstubAllGlobals();
    }
  });

  it('caps concurrent step-list fetches so a 100-run burst does not saturate the connection pool', async () => {
    // Regression guard: starting 100 workflows in parallel previously fired 100 simultaneous
    // GET /executions/{id}/steps calls, queueing them behind the browser's per-origin limit
    // (~6) AND saturating the API server which was already busy dispatching the runs.
    // The symptom was that F5 navigation would stall because /api/auth/me + /api/workflows
    // couldn't get a connection. The semaphore caps in-flight hydration fetches at 4.
    const MAX_OBSERVED_CAP = 4;
    let inFlight = 0;
    let peakInFlight = 0;
    const releaseHandles: Array<() => void> = [];

    const fetchMock = vi.fn(async (url: string | URL | Request) => {
      const u = typeof url === 'string' ? url : url instanceof URL ? url.toString() : url.url;
      if (u.includes('/executions?workflowId=')) {
        return new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      if (u.includes('/steps')) {
        inFlight++;
        if (inFlight > peakInFlight) peakInFlight = inFlight;
        await new Promise<void>((resolve) => { releaseHandles.push(resolve); });
        inFlight--;
        return new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      return new Response('not found', { status: 404 });
    });
    vi.stubGlobal('fetch', fetchMock);

    try {
      const { result } = renderHook(() => useWorkflowSignalR('wf-1'));
      await waitFor(() => expect(result.current.connected).toBe(true));

      act(() => {
        for (let i = 0; i < 100; i++) {
          __currentConnection!.emit('StepStarted', {
            executionId: `exec-${i}`, workflowId: 'wf-1',
            stepId: 'step-a', stepType: 'runScript',
            startedAt: '2026-04-26T12:00:00Z',
          });
        }
      });

      await waitFor(() => expect(inFlight).toBe(MAX_OBSERVED_CAP));
      expect(peakInFlight).toBeLessThanOrEqual(MAX_OBSERVED_CAP);

      while (releaseHandles.length > 0) {
        const next = releaseHandles.shift()!;
        next();
        await Promise.resolve();
      }
      expect(peakInFlight).toBeLessThanOrEqual(MAX_OBSERVED_CAP);
    } finally {
      while (releaseHandles.length > 0) releaseHandles.shift()!();
      vi.unstubAllGlobals();
    }
  });

  it('lazy-hydrates the full step list when the first SignalR event arrives for an unknown execution', async () => {
    // F5-Regression: SignalR doesn't replay history. Without lazy backfill the run's
    // step list would start at whatever event arrives first — typically the currently-
    // running step — and earlier completed steps would be invisible until the 10s
    // periodic refresh tick. This test pins down the lazy-fetch behavior.

    // Stub fetch so GET /executions?workflowId=... returns nothing (skip bulk hydration)
    // and GET /executions/{id}/steps returns the historical step list we expect to see
    // backfilled into the live state alongside the freshly-arriving SignalR step.
    const fetchMock = vi.fn(async (url: string | URL | Request) => {
      const u = typeof url === 'string' ? url : url instanceof URL ? url.toString() : url.url;
      if (u.includes('/executions?workflowId=')) {
        return new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      if (u.endsWith('/executions/exec-late/steps')) {
        return new Response(JSON.stringify([
          { stepId: 'step-1', stepName: 'First', stepType: 'runScript',
            status: 'Succeeded', startedAt: '2026-04-26T12:00:00Z',
            completedAt: '2026-04-26T12:00:01Z', output: 'one' },
          { stepId: 'step-2', stepName: 'Second', stepType: 'runScript',
            status: 'Succeeded', startedAt: '2026-04-26T12:00:01Z',
            completedAt: '2026-04-26T12:00:02Z', output: 'two' },
        ]), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      return new Response('not found', { status: 404 });
    });
    vi.stubGlobal('fetch', fetchMock);

    try {
      const { result } = renderHook(() => useWorkflowSignalR('wf-1'));
      await waitFor(() => expect(result.current.connected).toBe(true));

      // SignalR delivers the currently-active step for an exec we've never seen before.
      act(() => {
        __currentConnection!.emit('StepStarted', {
          executionId: 'exec-late', workflowId: 'wf-1',
          stepId: 'step-3', stepName: 'Third', stepType: 'runScript',
          startedAt: '2026-04-26T12:00:02Z',
        });
      });

      // The lazy fetch should land + merge: 2 historical steps + 1 in-memory current step.
      await waitFor(() => {
        const exec = result.current.liveExecutions.find((e) => e.executionId === 'exec-late');
        expect(exec?.steps.length ?? 0).toBe(3);
      });
      const exec = result.current.liveExecutions.find((e) => e.executionId === 'exec-late')!;
      const stepIds = exec.steps.map((s) => s.stepId).sort();
      expect(stepIds).toEqual(['step-1', 'step-2', 'step-3']);
      const step1 = exec.steps.find((s) => s.stepId === 'step-1')!;
      expect(step1.status).toBe('Succeeded');
      expect(step1.output).toBe('one');
      const step3 = exec.steps.find((s) => s.stepId === 'step-3')!;
      expect(step3.status).toBe('Running');

      // A second event for the same exec must NOT trigger a duplicate fetch — the dedup
      // ref is the cheap path that keeps event bursts from fanning out.
      const stepsCallsBefore = fetchMock.mock.calls.filter(
        (c) => typeof c[0] === 'string' && (c[0] as string).endsWith('/executions/exec-late/steps'),
      ).length;
      act(() => {
        __currentConnection!.emit('StepStarted', {
          executionId: 'exec-late', workflowId: 'wf-1',
          stepId: 'step-4', stepName: 'Fourth', stepType: 'runScript',
          startedAt: '2026-04-26T12:00:03Z',
        });
      });
      await waitFor(() => {
        const e = result.current.liveExecutions.find((x) => x.executionId === 'exec-late');
        expect(e?.steps.find((s) => s.stepId === 'step-4')).toBeDefined();
      });
      const stepsCallsAfter = fetchMock.mock.calls.filter(
        (c) => typeof c[0] === 'string' && (c[0] as string).endsWith('/executions/exec-late/steps'),
      ).length;
      expect(stepsCallsAfter).toBe(stepsCallsBefore);
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('shows all relevant executions as status badges on initial mount without fetching steps for terminal runs', async () => {
    // Perf contract: the listing-only initial load shows status badges immediately for all
    // runs without firing N parallel GET /steps calls. Terminal runs (Failed/Succeeded/
    // Cancelled) are never auto-hydrated — they only get step detail when the user expands
    // them via joinExecution. With 100 parallel workflows this was the primary cause of
    // UI freezes: 100 GET /steps hammered the API and browser connection pool on mount.
    let stepsFetchCount = 0;
    const fetchMock = vi.fn(async (url: string | URL | Request) => {
      const u = typeof url === 'string' ? url : url instanceof URL ? url.toString() : url.url;
      if (u.includes('/executions?workflowId=')) {
        const now = Date.now();
        return new Response(JSON.stringify([
          { id: 'exec-1', workflowId: 'wf-1', status: 'Failed',
            startedAt: new Date(now - 20_000).toISOString(), completedAt: new Date(now - 10_000).toISOString() },
          { id: 'exec-2', workflowId: 'wf-1', status: 'Succeeded',
            startedAt: new Date(now - 15_000).toISOString(), completedAt: new Date(now - 8_000).toISOString() },
          { id: 'exec-3', workflowId: 'wf-1', status: 'Failed',
            startedAt: new Date(now - 10_000).toISOString(), completedAt: new Date(now - 5_000).toISOString() },
        ]), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      if (u.includes('/steps')) {
        stepsFetchCount++;
        return new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      return new Response('not found', { status: 404 });
    });
    vi.stubGlobal('fetch', fetchMock);

    try {
      const { result } = renderHook(() => useWorkflowSignalR('wf-1'));
      await waitFor(() => expect(result.current.connected).toBe(true));
      await waitFor(() => expect(result.current.liveExecutions).toHaveLength(3));

      // All three runs appear with correct statuses — no waiting for step fetches.
      const statuses = result.current.liveExecutions.map((e) => e.status).sort();
      expect(statuses).toEqual(['Failed', 'Failed', 'Succeeded']);
      // No /steps calls for terminal runs — they are listing-only on initial mount.
      expect(stepsFetchCount).toBe(0);
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('auto-hydrates step details for only the top MAX_AUTO_HYDRATE most recent active runs on initial mount', async () => {
    // When more than MAX_AUTO_HYDRATE runs are active, only the top N (most recent by
    // startedAt) get step details fetched immediately. The rest show status badges until
    // the user expands them. This caps the initial HTTP storm regardless of how many
    // parallel workflows are running.
    const RUN_COUNT = MAX_AUTO_HYDRATE + 5; // deliberately above the auto-hydrate cap
    const stepsFetched = new Set<string>();
    const fetchMock = vi.fn(async (url: string | URL | Request) => {
      const u = typeof url === 'string' ? url : url instanceof URL ? url.toString() : url.url;
      if (u.includes('/executions?workflowId=')) {
        // exec-(RUN_COUNT-1) is newest, exec-0 is oldest.
        const runs = Array.from({ length: RUN_COUNT }, (_, i) => ({
          id: `exec-${i}`,
          workflowId: 'wf-1',
          status: 'Running',
          startedAt: new Date(Date.now() - (RUN_COUNT - i) * 60_000).toISOString(),
        }));
        return new Response(JSON.stringify(runs), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      const stepsMatch = u.match(/\/executions\/(exec-\d+)\/steps/);
      if (stepsMatch) {
        stepsFetched.add(stepsMatch[1]);
        return new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      return new Response('not found', { status: 404 });
    });
    vi.stubGlobal('fetch', fetchMock);

    try {
      const { result } = renderHook(() => useWorkflowSignalR('wf-1'));
      await waitFor(() => expect(result.current.connected).toBe(true));
      // Wait for listing + step fetches to settle.
      await waitFor(() => expect(result.current.liveExecutions).toHaveLength(RUN_COUNT));
      await waitFor(() => expect(stepsFetched.size).toBeGreaterThanOrEqual(MAX_AUTO_HYDRATE), { timeout: 2000 });

      expect(stepsFetched.size).toBe(MAX_AUTO_HYDRATE);
      // Must be the MAX_AUTO_HYDRATE most recent (highest index = newest startedAt).
      const newest = RUN_COUNT - 1;
      for (let i = 0; i < MAX_AUTO_HYDRATE; i++) {
        expect(stepsFetched).toContain(`exec-${newest - i}`);
      }
      // The oldest 5 runs are listing-only.
      expect(stepsFetched).not.toContain('exec-0');
      expect(stepsFetched).not.toContain(`exec-${RUN_COUNT - MAX_AUTO_HYDRATE - 1}`);
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('initial auto-hydration rebuilds the databus from outputParametersJson', async () => {
    const fetchMock = vi.fn(async (url: string | URL | Request) => {
      const u = typeof url === 'string' ? url : url instanceof URL ? url.toString() : url.url;
      if (u.includes('/executions?workflowId=')) {
        return new Response(JSON.stringify([
          {
            id: 'exec-output',
            workflowId: 'wf-1',
            status: 'Running',
            startedAt: '2026-04-26T10:00:00Z',
          },
        ]), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      if (u.endsWith('/executions/exec-output/steps')) {
        return new Response(JSON.stringify([
          {
            stepId: 'disk-step',
            stepName: 'Free Disk Space',
            stepType: 'custom:disk_free_check',
            status: 'Succeeded',
            output: null,
            errorOutput: null,
            traceOutput: null,
            outputParametersJson: JSON.stringify({ freeGb: '42', status: 'ok' }),
            outputVariable: 'diskCheck',
            startedAt: '2026-04-26T10:00:00Z',
            completedAt: '2026-04-26T10:00:01Z',
          },
        ]), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      return new Response('not found', { status: 404 });
    });
    vi.stubGlobal('fetch', fetchMock);

    try {
      const { result } = renderHook(() => useWorkflowSignalR('wf-1'));
      await waitFor(() => expect(result.current.connected).toBe(true));
      await waitFor(() => expect(result.current.liveExecution?.databus['disk-step.param.freeGb']?.value).toBe('42'));

      expect(result.current.liveExecution?.databus['diskCheck.param.freeGb']?.value).toBe('42');
      expect(result.current.liveExecution?.databus['disk-step.param.status']?.value).toBe('ok');
      expect(result.current.liveExecution?.databus['diskCheck.param.status']?.value).toBe('ok');
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('joinExecution fetches step details for a listing-only run on demand', async () => {
    // Step details are fetched lazily when the user expands a run (joinExecution).
    // Until then the run shows as a status badge with empty steps[].
    const RUN_COUNT = MAX_AUTO_HYDRATE + 1; // exec-0 is the oldest, listing-only
    const stepsFetched: string[] = [];
    const fetchMock = vi.fn(async (url: string | URL | Request) => {
      const u = typeof url === 'string' ? url : url instanceof URL ? url.toString() : url.url;
      if (u.includes('/executions?workflowId=')) {
        // RUN_COUNT Running runs — top MAX_AUTO_HYDRATE are auto-hydrated, exec-0 is listing-only.
        const runs = Array.from({ length: RUN_COUNT }, (_, i) => ({
          id: `exec-${i}`,
          workflowId: 'wf-1',
          status: 'Running',
          startedAt: new Date(Date.now() - (RUN_COUNT - i) * 60_000).toISOString(),
        }));
        return new Response(JSON.stringify(runs), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      const stepsMatch = u.match(/\/executions\/(exec-\d+)\/steps/);
      if (stepsMatch) {
        const id = stepsMatch[1];
        stepsFetched.push(id);
        const steps = id === 'exec-0'
          ? [{ stepId: 'step-x', stepName: 'X', stepType: 'runScript', status: 'Running',
               startedAt: new Date().toISOString() }]
          : [];
        return new Response(JSON.stringify(steps), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      return new Response('not found', { status: 404 });
    });
    vi.stubGlobal('fetch', fetchMock);

    try {
      const { result } = renderHook(() => useWorkflowSignalR('wf-1'));
      await waitFor(() => expect(result.current.connected).toBe(true));
      await waitFor(() => expect(result.current.liveExecutions).toHaveLength(RUN_COUNT));
      // Wait for the auto-hydrations to settle.
      await waitFor(() => expect(stepsFetched.filter((id) => id !== 'exec-0')).toHaveLength(MAX_AUTO_HYDRATE));

      // exec-0 is listing-only — no steps yet.
      const exec0Before = result.current.liveExecutions.find((e) => e.executionId === 'exec-0')!;
      expect(exec0Before.steps).toHaveLength(0);
      expect(stepsFetched).not.toContain('exec-0');

      // User expands exec-0 → joinExecution triggers the step fetch.
      await act(async () => {
        await result.current.joinExecution('exec-0', 'wf-1');
      });

      expect(stepsFetched).toContain('exec-0');
      // Invoke was called to join the per-execution SignalR group.
      expect(__currentConnection!.invoke).toHaveBeenCalledWith('JoinExecution', 'exec-0');
      await waitFor(() => {
        const exec0 = result.current.liveExecutions.find((e) => e.executionId === 'exec-0')!;
        expect(exec0.steps).toHaveLength(1);
      });
      const exec0After = result.current.liveExecutions.find((e) => e.executionId === 'exec-0')!;
      expect(exec0After.steps[0].stepId).toBe('step-x');
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('joinExecution refetches steps even when execution was previously auto-hydrated', async () => {
    // Regression guard: when the user opens the Live tab while a run is already in progress,
    // hydrateActive fetches steps at mount time (T0) and marks the run as hydrated. Steps
    // that start between T0 and the user's explicit expand (joinExecution) are delivered to
    // the execution-only SignalR group — which the frontend has not joined yet. Without this
    // fix, joinExecution skipped the HTTP refetch (dedup guard: already hydrated) so those
    // in-between steps were permanently missing until the terminal-state safety refetch.
    let stepsCallCount = 0;
    let stepsResponse = [
      { stepId: 'step-1', stepName: 'First', stepType: 'runScript', status: 'Succeeded',
        startedAt: '2026-04-26T12:00:00Z', completedAt: '2026-04-26T12:00:01Z' },
      { stepId: 'step-2', stepName: 'Second', stepType: 'runScript', status: 'Running',
        startedAt: '2026-04-26T12:00:01Z' },
    ];
    const fetchMock = vi.fn(async (url: string | URL | Request) => {
      const u = typeof url === 'string' ? url : url instanceof URL ? url.toString() : url.url;
      if (u.includes('/executions?workflowId=')) {
        return new Response(JSON.stringify([
          { id: 'exec-1', workflowId: 'wf-1', status: 'Running', startedAt: '2026-04-26T12:00:00Z' },
        ]), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      if (u.endsWith('/executions/exec-1/steps')) {
        stepsCallCount++;
        return new Response(JSON.stringify(stepsResponse), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      return new Response('not found', { status: 404 });
    });
    vi.stubGlobal('fetch', fetchMock);

    try {
      const { result } = renderHook(() => useWorkflowSignalR('wf-1'));
      await waitFor(() => expect(result.current.connected).toBe(true));
      // Auto-hydration at mount fetches 2 steps.
      await waitFor(() => {
        const exec = result.current.liveExecutions.find((e) => e.executionId === 'exec-1');
        expect(exec?.steps).toHaveLength(2);
      });
      expect(stepsCallCount).toBe(1);

      // Simulate new steps that started between mount and the user expanding the run —
      // update the mock so the next fetch returns 5 steps.
      stepsResponse = [
        { stepId: 'step-1', stepName: 'First', stepType: 'runScript', status: 'Succeeded',
          startedAt: '2026-04-26T12:00:00Z', completedAt: '2026-04-26T12:00:01Z' },
        { stepId: 'step-2', stepName: 'Second', stepType: 'runScript', status: 'Succeeded',
          startedAt: '2026-04-26T12:00:01Z', completedAt: '2026-04-26T12:00:02Z' },
        { stepId: 'step-3', stepName: 'Third', stepType: 'runScript', status: 'Running',
          startedAt: '2026-04-26T12:00:02Z' },
        { stepId: 'step-4', stepName: 'Fourth', stepType: 'runScript', status: 'Running',
          startedAt: '2026-04-26T12:00:02Z' },
        { stepId: 'step-5', stepName: 'Fifth', stepType: 'runScript', status: 'Running',
          startedAt: '2026-04-26T12:00:03Z' },
      ];

      // User explicitly expands the run → must refetch despite prior auto-hydration.
      await act(async () => {
        await result.current.joinExecution('exec-1', 'wf-1');
      });

      expect(stepsCallCount).toBe(2);
      await waitFor(() => {
        const exec = result.current.liveExecutions.find((e) => e.executionId === 'exec-1');
        expect(exec?.steps).toHaveLength(5);
      });
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('all active runs render in liveExecutions; liveActiveCount reflects total', async () => {
    // Display cap was removed: every Running/Pending run renders in liveExecutions, no
    // "+N more" badge. State (liveExecutionsById) and rendered list are both uncapped.
    const RUN_COUNT = 25;
    const fetchMock = vi.fn(async (url: string | URL | Request) => {
      const u = typeof url === 'string' ? url : url instanceof URL ? url.toString() : url.url;
      if (u.includes('/executions?workflowId=')) {
        const runs = Array.from({ length: RUN_COUNT }, (_, i) => ({
          id: `exec-${i}`, workflowId: 'wf-1', status: 'Running',
          startedAt: new Date(Date.now() - i * 1_000).toISOString(),
        }));
        return new Response(JSON.stringify(runs), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      if (u.includes('/steps')) return new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } });
      return new Response('not found', { status: 404 });
    });
    vi.stubGlobal('fetch', fetchMock);

    try {
      const { result } = renderHook(() => useWorkflowSignalR('wf-1'));
      await waitFor(() => expect(result.current.connected).toBe(true));
      await waitFor(() => expect(result.current.liveActiveCount).toBe(RUN_COUNT));

      // No display cap: every active run is in liveExecutions.
      expect(result.current.liveExecutions).toHaveLength(RUN_COUNT);
      expect(MAX_ACTIVE_DISPLAYED).toBe(Number.POSITIVE_INFINITY);
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('ExecutionStatusChanged updates state for any active run regardless of position', async () => {
    // Status events must land on any run in state. Even with the display cap gone, this
    // exercises the per-event update path against a sizable population so a regression
    // that drops events for late-listed runs would surface.
    const RUN_COUNT = 25;
    const fetchMock = vi.fn(async (url: string | URL | Request) => {
      const u = typeof url === 'string' ? url : url instanceof URL ? url.toString() : url.url;
      if (u.includes('/executions?workflowId=')) {
        // exec-0 is newest, exec-24 is oldest.
        const runs = Array.from({ length: RUN_COUNT }, (_, i) => ({
          id: `exec-${i}`, workflowId: 'wf-1', status: 'Running',
          startedAt: new Date(Date.now() - i * 1_000).toISOString(),
        }));
        return new Response(JSON.stringify(runs), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      if (u.includes('/steps')) return new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } });
      return new Response('not found', { status: 404 });
    });
    vi.stubGlobal('fetch', fetchMock);

    try {
      const { result } = renderHook(() => useWorkflowSignalR('wf-1'));
      await waitFor(() => expect(result.current.connected).toBe(true));
      await waitFor(() => expect(result.current.liveActiveCount).toBe(RUN_COUNT));

      // All 25 runs render — exec-24 is now visible in the list.
      expect(result.current.liveExecutions.find((e) => e.executionId === 'exec-24')).toBeDefined();

      // Backend sends a terminal status for exec-24.
      act(() => {
        __currentConnection!.emit('ExecutionStatusChanged', {
          executionId: 'exec-24', workflowId: 'wf-1',
          status: 'Succeeded', completedAt: new Date().toISOString(),
        });
      });

      // The run transitions from active to completed → liveActiveCount decrements.
      await waitFor(() => expect(result.current.liveActiveCount).toBe(RUN_COUNT - 1));
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('applyLiveEvents correctly applies 200 ExecutionStatusChanged events delivered in one LiveEventsBatch', async () => {
    // Regression guard: the old applyLiveEvents called { ...prev } inside each per-event
    // applyLiveEvent pass, producing O(N × M) property copies for N executions × M events.
    // At 200 parallel jobs the JS thread blocked for ~80ms on a single state updater call.
    // The fix does one { ...prev } upfront and applies all mutations into that copy: O(N + M).
    // This test verifies correctness (all 200 executions reach Succeeded) — the O-notation
    // improvement is enforced by the implementation and visible in browser perf recordings.
    const N = 200;
    const execIds = Array.from({ length: N }, (_, i) => `exec-batch-${i}`);

    vi.stubGlobal('fetch', vi.fn(async (url: string | URL | Request) => {
      const u = typeof url === 'string' ? url : url instanceof URL ? url.toString() : url.url;
      if (u.includes('/executions?workflowId=')) {
        const runs = execIds.map((id, i) => ({
          id, workflowId: 'wf-batch', status: 'Running',
          startedAt: new Date(Date.now() - i * 100).toISOString(),
        }));
        return new Response(JSON.stringify(runs), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      return new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } });
    }));

    try {
      const { result } = renderHook(() => useWorkflowSignalR('wf-batch'));
      // Wait until all 200 listing-only runs are in state as Running.
      await waitFor(() => expect(result.current.liveActiveCount).toBe(N));

      // Deliver all 200 ExecutionStatusChanged("Succeeded") events in one LiveEventsBatch —
      // the same path the server takes when 200 executions complete simultaneously.
      act(() => {
        __currentConnection!.emit('LiveEventsBatch', {
          events: execIds.map((id) => ({
            Type: 'ExecutionStatusChanged',
            Event: {
              executionId: id, workflowId: 'wf-batch',
              status: 'Succeeded', completedAt: '2026-05-03T00:00:00Z',
            },
          })),
        });
      });

      // All active runs should be cleared — no more Running executions.
      await waitFor(() => expect(result.current.liveActiveCount).toBe(0));
      // The display-capped list (≤50 completed) should contain only Succeeded entries.
      expect(result.current.liveExecutions.every((e) => e.status === 'Succeeded')).toBe(true);
      expect(result.current.liveExecutions.length).toBeGreaterThan(0);
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('periodic tick hydrates at most MAX_AUTO_HYDRATE newly-discovered runs', async () => {
    // Regression guard: periodic mode previously used toAutoHydrate = relevant (all
    // newly-discovered runs without a cap). During a 200-parallel stress test the first
    // 10-second tick found all 200 as "new discoveries" and triggered 200 GET /steps
    // fetches, making every run show full step detail ("3/3 steps") instead of the
    // listing-only status badge ("0/0 steps"). The fix applies the same MAX_AUTO_HYDRATE
    // cap in periodic mode as in initial mode.
    vi.useFakeTimers();
    let stepsFetchCount = 0;
    let listCallCount = 0;

    const fetchMock = vi.fn(async (url: string | URL | Request) => {
      const u = typeof url === 'string' ? url : url instanceof URL ? url.toString() : url.url;
      if (u.includes('/executions?workflowId=')) {
        listCallCount++;
        if (listCallCount === 1) {
          // Initial listing: no runs yet (stress test hasn't started).
          return new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } });
        }
        // Periodic tick: 5 running jobs discovered for the first time.
        const runs = Array.from({ length: 5 }, (_, i) => ({
          id: `exec-${i}`, workflowId: 'wf-1', status: 'Running',
          startedAt: new Date(Date.now() - i * 1_000).toISOString(),
        }));
        return new Response(JSON.stringify(runs), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      if (u.includes('/steps')) {
        stepsFetchCount++;
        return new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      return new Response('not found', { status: 404 });
    });
    vi.stubGlobal('fetch', fetchMock);

    try {
      const { result } = renderHook(() => useWorkflowSignalR('wf-1'));
      // Flush connection start.
      await act(async () => { await vi.advanceTimersByTimeAsync(50); });
      expect(result.current.connected).toBe(true);
      expect(result.current.liveExecutions).toHaveLength(0);

      // Advance past the 10-second periodic tick — 5 new runs are discovered.
      await act(async () => { await vi.advanceTimersByTimeAsync(10_500); });

      // All 5 runs appear as listing-only status badges.
      expect(result.current.liveActiveCount).toBe(5);
      // But only MAX_AUTO_HYDRATE (3) receive step-detail fetches.
      expect(stepsFetchCount).toBeLessThanOrEqual(MAX_AUTO_HYDRATE);
    } finally {
      vi.useRealTimers();
      vi.unstubAllGlobals();
    }
  });

  it('hydrateActive requests GET /executions with activeOnly=true', async () => {
    // Regression guard: hydrateActive must pass activeOnly=true so the server returns
    // only Running/Pending/Paused entries instead of up to 500 historical rows. Without
    // this flag, Live View initial load triggers a full 500-row download on every open.
    let capturedUrl = '';
    const fetchMock = vi.fn(async (url: string | URL | Request) => {
      const u = typeof url === 'string' ? url : url instanceof URL ? url.toString() : url.url;
      if (u.includes('/executions?workflowId=')) {
        capturedUrl = u;
        return new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      return new Response('not found', { status: 404 });
    });
    vi.stubGlobal('fetch', fetchMock);

    try {
      const { result } = renderHook(() => useWorkflowSignalR('wf-1'));
      await waitFor(() => expect(result.current.connected).toBe(true));
      await waitFor(() => expect(capturedUrl).not.toBe(''));

      expect(capturedUrl).toContain('activeOnly=true');
    } finally {
      vi.unstubAllGlobals();
    }
  });
});
